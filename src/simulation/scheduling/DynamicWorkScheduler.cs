using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Game.Core;

namespace Game.Simulation.Scheduling;

/// <summary>
/// Реализация планировщика по умолчанию (PLAN.md §4.7, Шаги 2–4).
/// Центральный атомарный курсор первичен; per-worker
/// <see cref="WorkStealingQueue{WorkItem}"/> + steal-half — только когда курсор пуст
/// (до 2 попыток у случайной жертвы). EMA/imbalance полностью в
/// <see cref="LoadMonitor"/> (планировщик лишь спрашивает SuggestBatchSize).
/// Splittable-остатки Requeue-ятся с CostHint×1.5 (приоритет — голова локальной
/// очереди, при переполнении — глобальный ConcurrentQueue).
/// Без зависимости от Godot: ошибки body считаются через Interlocked,
/// печать — из sim-потока (см. AgentSimulationThread).
/// </summary>
public sealed class DynamicWorkScheduler : IDynamicWorkScheduler
{
    /// <summary>Общий экземпляр для sim-потока и диспетчера.</summary>
    public static DynamicWorkScheduler Shared { get; } = new DynamicWorkScheduler();

    /// <summary>Стартовый размер батча.</summary>
    public const int InitialBatchSize = 128;

    /// <summary>Минимальный размер батча (пол тяжёлых батчей).</summary>
    public const int MinBatchSize = 32;

    /// <summary>Максимальный размер батча (потолок лёгких батчей).</summary>
    public const int MaxBatchSize = 512;

    /// <summary>
    /// Порог «тяжёлого» батча в мс. Поле, а не const — для тюнинга из overlay/тестов.
    /// </summary>
    public static double HeavyBatchMs = 1.5;

    /// <summary>
    /// Квант прерывания splittable-батча в мс (Шаг 4, дефолт 2.0).
    /// Некорректные значения санитизируются при использовании.
    /// </summary>
    public static double QuantumMs = 2.0;

    private readonly int? _forcedDop;
    private readonly LoadMonitor _monitor = new();

    /// <summary>Per-worker deque для steal-half. Центральный курсор первичен.</summary>
    private readonly ThreadLocal<WorkStealingQueue<WorkItem>> _localDeque =
        new(() => new WorkStealingQueue<WorkItem>(1024), trackAllValues: true);

    /// <summary>
    /// Thread-local буфер для украденных элементов.
    /// ВАЖНО (XML-doc запрет): буфер обязан быть thread-local — общий List
    /// между ворами дал бы data race (Add/RemoveRange без синхронизации).
    /// </summary>
    private readonly ThreadLocal<List<WorkItem>> _stealBuffer =
        new(() => new List<WorkItem>(64), trackAllValues: true);

    private int _lastPhaseErrors;

    /// <summary>Число ошибок body за последнюю фазу (Interlocked-счётчик, печать — из sim-потока).</summary>
    public int LastPhaseErrorCount => Volatile.Read(ref _lastPhaseErrors);

    /// <summary>
    /// Создаёт планировщик.
    /// </summary>
    /// <param name="degreeOfParallelism">
    /// DOP-инжекция для тестов (фикс @destroyer №7): null → Environment.ProcessorCount.
    /// При dop==1 — строго последовательный loop без Parallel.For (для golden-тестов).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Если degreeOfParallelism &lt;= 0.</exception>
    public DynamicWorkScheduler(int? degreeOfParallelism = null)
    {
        if (degreeOfParallelism.HasValue && degreeOfParallelism.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(degreeOfParallelism), "DOP должен быть > 0.");
        _forcedDop = degreeOfParallelism;
    }

    private int GetDop() => _forcedDop ?? Environment.ProcessorCount;

    private static double EffectiveQuantumMs()
    {
        double q = QuantumMs;
        if (!double.IsFinite(q) || q <= 0.0)
            return 2.0;
        return Math.Min(q, WorkQuantum.MaxQuantumMs);
    }

    private void Publish(string profilerScope, double wallMs, double cpuMs)
    {
        if (profilerScope == null)
            return;
        var st = _monitor.GetStats();
        // FIX круг-2 №6: аккумуляция sub-steps в тиковый тотал (sum wall/cpu,
        // max MaxBatchMs, sum StragglerCount) — overlay видит сумму тика, а не
        // последний sub-step. Сброс — ResetTickBalance в начале тика (sim-поток).
        GameProfiler.AccumulateSchedulerStats(profilerScope, wallMs, cpuMs, st.MaxBatchMs, st.StragglerCount);
    }

    /// <summary>
    /// Сброс тикового аккумулятора баланса (зовёт sim-поток один раз в начале
    /// тика, перед циклом sub-steps — см. FIX круг-2 №6 в GameProfiler).
    /// </summary>
    public static void ResetTickBalance(string profilerScope)
    {
        if (profilerScope == null)
            return;
        GameProfiler.ResetTickBalance(profilerScope);
    }

    /// <inheritdoc/>
    public void ForEach(int count, Action<int> body, WorkKind kind = WorkKind.Fast, string profilerScope = null)
    {
        if (body == null)
            throw new ArgumentNullException(nameof(body));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count должен быть >= 0.");
        if (count == 0)
        {
            // FIX круг-2 №8: пустая фаза обязана публиковать нули, иначе
            // GetStats/SnapshotBalance врут старьём от прошлой фазы.
            Volatile.Write(ref _lastPhaseErrors, 0);
            _monitor.EndPhase(0, 0, GetDop());
            Publish(profilerScope, 0, 0);
            return;
        }

        int dop = GetDop();
        long wall0 = Stopwatch.GetTimestamp();

        if (count < MinBatchSize * 2 || dop <= 1)
        {
            // Sequential fast-path: per-i try/catch, остальные i добиваются.
            // SCHEDULING RULE: из body(i) запрещены ЛЮБЫЕ Godot Node API
            // (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
            // Разрешены: чистые вычисления, SoA-массивы, Interlocked/Volatile,
            // ConcurrentQueue.Enqueue (снапшоты), ThreadLocal, ParallelRng.
            int errors = 0;
            for (int i = 0; i < count; i++)
            {
                try { body(i); }
                catch { errors++; }
            }
            double wallMs = (Stopwatch.GetTimestamp() - wall0) * 1000.0 / Stopwatch.Frequency;
            Volatile.Write(ref _lastPhaseErrors, errors);
            // FIX круг-2 №5: fast-path обязан кормить EMA — один RecordWorkerBatch
            // на всю фазу, иначе MaxBatchMs/Straggler/EMA нулевые/протухшие.
            _monitor.RecordWorkerBatch(Thread.CurrentThread.ManagedThreadId, wallMs, count);
            _monitor.EndPhase(wallMs, wallMs, 1);
            Publish(profilerScope, wallMs, wallMs);
            return;
        }

        long batchSeq = 0;
        long totalCpuTicks = 0;
        int phaseErrors = 0;
        var deques = new WorkStealingQueue<WorkItem>[dop];
        // FIX круг-2 №2: overflow для украденного при полном deque (было: молча
        // терялся хвост stealBuf). Покрытие [0,count) не должно теряться.
        var overflow = new ConcurrentQueue<WorkItem>();

        Parallel.For(0, dop, new ParallelOptions { MaxDegreeOfParallelism = dop }, slot =>
        {
            var local = _localDeque.Value;
            local.Clear(); // Слот стартует пустым (фаза — single-use).
            deques[slot] = local;
            var stealBuf = _stealBuffer.Value;
            long localCpuTicks = 0;
            // FIX круг-2 №7: локальный счётчик ошибок — один Interlocked.Add
            // в конце потока вместо CAS на каждое исключение (10k CAS → 1 ADD).
            int localErrors = 0;

            while (true)
            {
                // 1) Локальная очередь (requeue-остатки имеют приоритет).
                if (local.TryPopLocal(out WorkItem li))
                {
                    long t0 = Stopwatch.GetTimestamp();
                    int end = li.Start + li.Count;
                    for (int i = li.Start; i < end; i++)
                    {
                        try { body(i); }
                        // FIX круг-2 №7: молча локально (circuit-breaker: остаток
                        // батча всё равно добиваем, покрытие держим, без CAS).
                        catch { localErrors++; }
                    }
                    long t1 = Stopwatch.GetTimestamp();
                    localCpuTicks += t1 - t0;
                    _monitor.RecordWorkerBatch(Thread.CurrentThread.ManagedThreadId,
                        (t1 - t0) * 1000.0 / Stopwatch.Frequency, li.Count);
                    continue;
                }
                if (overflow.TryDequeue(out WorkItem gi))
                {
                    long t0 = Stopwatch.GetTimestamp();
                    int end = gi.Start + gi.Count;
                    for (int i = gi.Start; i < end; i++)
                    {
                        try { body(i); }
                        catch { localErrors++; }
                    }
                    long t1 = Stopwatch.GetTimestamp();
                    localCpuTicks += t1 - t0;
                    _monitor.RecordWorkerBatch(Thread.CurrentThread.ManagedThreadId,
                        (t1 - t0) * 1000.0 / Stopwatch.Frequency, gi.Count);
                    continue;
                }
                // 2) Центральный курсор (первичен).
                // FIX круг-2 №10: hint влияет на размер батча исполнителя.
                int want = _monitor.SuggestBatchSize(kind, kind == WorkKind.Heavy ? 2.0f : 1.0f);
                long start = Interlocked.Add(ref batchSeq, want) - want;
                if (start < count)
                {
                    int size = (int)Math.Min((long)want, (long)count - start);
                    long t0 = Stopwatch.GetTimestamp();
                    int end = (int)(start + size);
                    // SCHEDULING RULE: из body(i) запрещены ЛЮБЫЕ Godot Node API
                    // (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
                    // Разрешены: чистые вычисления, SoA-массивы, Interlocked/Volatile,
                    // ConcurrentQueue.Enqueue (снапшоты), ThreadLocal, ParallelRng.
                    for (int i = (int)start; i < end; i++)
                    {
                        try { body(i); }
                        catch { localErrors++; }
                    }
                    long t1 = Stopwatch.GetTimestamp();
                    localCpuTicks += t1 - t0;
                    _monitor.RecordWorkerBatch(Thread.CurrentThread.ManagedThreadId,
                        (t1 - t0) * 1000.0 / Stopwatch.Frequency, size);
                    continue;
                }
                // 3) Steal-half при простое: до 2 попыток у случайной жертвы.
                bool stole = false;
                for (int attempt = 0; attempt < 2 && !stole; attempt++)
                {
                    int victimIdx = ParallelRng.Next(0, dop);
                    if (victimIdx == slot)
                        continue;
                    var victim = Volatile.Read(ref deques[victimIdx]);
                    if (victim == null || victim == local || victim.Count == 0)
                        continue;
                    stealBuf.Clear();
                    // FIX круг-2 №10: кража «половины по стоимости»
                    // (Count×CostHint через costOf), а не по штукам.
                    int stolen = victim.TryStealHalfByCostInto(stealBuf, static w => w.Count * w.CostHint);
                    if (stolen > 0 && stealBuf.Count > 0)
                    {
                        stole = true;
                        // FIX круг-2 №11: вор сигналит contention — жертва ужмётся.
                        _monitor.RecordSteal();
                        // Украденное выполняется локально: кладём обратно в свою deque.
                        // FIX круг-2 №10: украденный WorkItem с CostHint>2 subdivided
                        // на части по MinBatch — hint влияет на исполнение.
                        foreach (var s in stealBuf)
                        {
                            if (s.CostHint > 2.0f && s.Count > MinBatchSize)
                            {
                                int off = 0;
                                while (off < s.Count)
                                {
                                    int piece = Math.Min(MinBatchSize, s.Count - off);
                                    var part = new WorkItem(s.Start + off, piece, 1.0f, s.Kind);
                                    if (!local.TryPushLocal(part))
                                        overflow.Enqueue(part);
                                    off += piece;
                                }
                            }
                            else if (!local.TryPushLocal(s))
                            {
                                // FIX круг-2 №2: deque полна — в overflow, НЕ терять.
                                overflow.Enqueue(s);
                            }
                        }
                        stealBuf.Clear();
                    }
                }
                if (!stole)
                {
                    // Финальная перепроверка гонки: пока воровали, другой поток
                    // мог положить остаток в overflow.
                    if (!overflow.IsEmpty || Volatile.Read(ref batchSeq) < count)
                        continue;
                    break;
                }
            }

            Interlocked.Add(ref totalCpuTicks, localCpuTicks);
            // FIX круг-2 №7: один ADD на поток вместо CAS на исключение.
            if (localErrors > 0)
                Interlocked.Add(ref phaseErrors, localErrors);
        });

        double wallMs2 = (Stopwatch.GetTimestamp() - wall0) * 1000.0 / Stopwatch.Frequency;
        double cpuMs = totalCpuTicks * 1000.0 / Stopwatch.Frequency;
        Volatile.Write(ref _lastPhaseErrors, Volatile.Read(ref phaseErrors));
        _monitor.EndPhase(wallMs2, cpuMs, dop);
        Publish(profilerScope, wallMs2, cpuMs);
        // NOTE: курсор batchSeq МОЖЕТ убегать дальше count + MaxBatchSize*dop —
        // это нормально: поток делает Interlocked.Add до проверки start < count,
        // а при успешном steal цикл повторяется и добавляет ещё. Покрытие при этом
        // не страдает (size всегда clamp'ится к count - start). Поэтому здесь НЕТ
        // Debug.Assert на overrun — он ложно срабатывал в Phase3b (см. лог
        // "batch cursor overrun") и спамил ERROR через GodotTraceListener.
    }

    /// <inheritdoc/>
    public int ForEachRange(int count, Action<int, int> body, WorkKind kind = WorkKind.Fast, string profilerScope = null)
    {
        if (body == null)
            throw new ArgumentNullException(nameof(body));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count должен быть >= 0.");
        if (count == 0)
        {
            // FIX круг-2 №8: пустая фаза публикует нули (не врёт старьём).
            Volatile.Write(ref _lastPhaseErrors, 0);
            _monitor.EndPhase(0, 0, GetDop());
            Publish(profilerScope, 0, 0);
            return 0;
        }

        int dop = GetDop();
        long wall0 = Stopwatch.GetTimestamp();

        if (count < MinBatchSize * 2 || dop <= 1)
        {
            // SCHEDULING RULE: из body(start,end) запрещены ЛЮБЫЕ Godot Node API
            // (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
            // Разрешены: чистые вычисления, SoA-массивы, Interlocked/Volatile,
            // ConcurrentQueue.Enqueue (снапшоты), ThreadLocal, ParallelRng.
            int errors = 0;
            try { body(0, count); }
            catch { errors++; }
            // Примечание: range-body атомарна по смыслу (один вызов на диапазон);
            // при исключении остаток диапазона добить поэлементно нельзя без
            // per-i семантики — ошибка считается, фаза не падает.
            double wallMs = (Stopwatch.GetTimestamp() - wall0) * 1000.0 / Stopwatch.Frequency;
            Volatile.Write(ref _lastPhaseErrors, errors);
            // FIX круг-2 №5: fast-path кормит EMA одним вызовом.
            _monitor.RecordWorkerBatch(Thread.CurrentThread.ManagedThreadId, wallMs, count);
            _monitor.EndPhase(wallMs, wallMs, 1);
            Publish(profilerScope, wallMs, wallMs);
            return count;
        }

        long batchSeq = 0;
        int processed = 0;
        long totalCpuTicks = 0;
        int phaseErrors = 0;
        var deques = new WorkStealingQueue<WorkItem>[dop];
        // FIX круг-2 №2: overflow для украденного при полном deque.
        var overflow = new ConcurrentQueue<WorkItem>();

        Parallel.For(0, dop, new ParallelOptions { MaxDegreeOfParallelism = dop }, slot =>
        {
            var local = _localDeque.Value;
            local.Clear();
            deques[slot] = local;
            var stealBuf = _stealBuffer.Value;
            int localProcessed = 0;
            long localCpuTicks = 0;
            // FIX круг-2 №7: локальный счётчик — один Add в конце потока.
            int localErrors = 0;

            while (true)
            {
                if (local.TryPopLocal(out WorkItem li))
                {
                    long t0 = Stopwatch.GetTimestamp();
                    try
                    {
                        // SCHEDULING RULE: из body(start,end) запрещены ЛЮБЫЕ Godot Node API
                        // (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
                        body(li.Start, li.Start + li.Count);
                    }
                    catch { localErrors++; }
                    long t1 = Stopwatch.GetTimestamp();
                    localCpuTicks += t1 - t0;
                    _monitor.RecordWorkerBatch(Thread.CurrentThread.ManagedThreadId,
                        (t1 - t0) * 1000.0 / Stopwatch.Frequency, li.Count);
                    localProcessed += li.Count;
                    continue;
                }
                if (overflow.TryDequeue(out WorkItem gi))
                {
                    long t0 = Stopwatch.GetTimestamp();
                    try { body(gi.Start, gi.Start + gi.Count); }
                    catch { localErrors++; }
                    long t1 = Stopwatch.GetTimestamp();
                    localCpuTicks += t1 - t0;
                    _monitor.RecordWorkerBatch(Thread.CurrentThread.ManagedThreadId,
                        (t1 - t0) * 1000.0 / Stopwatch.Frequency, gi.Count);
                    localProcessed += gi.Count;
                    continue;
                }
                int want = _monitor.SuggestBatchSize(kind, kind == WorkKind.Heavy ? 2.0f : 1.0f);
                long start = Interlocked.Add(ref batchSeq, want) - want;
                if (start < count)
                {
                    int end = (int)Math.Min(start + want, count);
                    long t0 = Stopwatch.GetTimestamp();
                    // SCHEDULING RULE: из body(start,end) запрещены ЛЮБЫЕ Godot Node API
                    // (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
                    try { body((int)start, end); }
                    catch { localErrors++; }
                    long t1 = Stopwatch.GetTimestamp();
                    localCpuTicks += t1 - t0;
                    _monitor.RecordWorkerBatch(Thread.CurrentThread.ManagedThreadId,
                        (t1 - t0) * 1000.0 / Stopwatch.Frequency, end - (int)start);
                    localProcessed += end - (int)start;
                    continue;
                }
                bool stole = false;
                for (int attempt = 0; attempt < 2 && !stole; attempt++)
                {
                    int victimIdx = ParallelRng.Next(0, dop);
                    if (victimIdx == slot)
                        continue;
                    var victim = Volatile.Read(ref deques[victimIdx]);
                    if (victim == null || victim == local || victim.Count == 0)
                        continue;
                    stealBuf.Clear();
                    // FIX круг-2 №10: кража половины по стоимости (Count×CostHint).
                    int stolen = victim.TryStealHalfByCostInto(stealBuf, static w => w.Count * w.CostHint);
                    if (stolen > 0 && stealBuf.Count > 0)
                    {
                        stole = true;
                        // FIX круг-2 №11: вор сигналит contention.
                        _monitor.RecordSteal();
                        // FIX круг-2 №2: при полном deque — в overflow, НЕ терять хвост.
                        // FIX круг-2 №10: CostHint>2 subdivided на части по MinBatch.
                        foreach (var s in stealBuf)
                        {
                            if (s.CostHint > 2.0f && s.Count > MinBatchSize)
                            {
                                int off = 0;
                                while (off < s.Count)
                                {
                                    int piece = Math.Min(MinBatchSize, s.Count - off);
                                    var part = new WorkItem(s.Start + off, piece, 1.0f, s.Kind);
                                    if (!local.TryPushLocal(part))
                                        overflow.Enqueue(part);
                                    off += piece;
                                }
                            }
                            else if (!local.TryPushLocal(s))
                            {
                                overflow.Enqueue(s);
                            }
                        }
                        stealBuf.Clear();
                    }
                }
                if (!stole)
                {
                    // Финальная перепроверка гонки (overflow мог пополниться).
                    if (!overflow.IsEmpty || Volatile.Read(ref batchSeq) < count)
                        continue;
                    break;
                }
            }

            Interlocked.Add(ref processed, localProcessed);
            Interlocked.Add(ref totalCpuTicks, localCpuTicks);
            // FIX круг-2 №7: один ADD на поток.
            if (localErrors > 0)
                Interlocked.Add(ref phaseErrors, localErrors);
        });

        double wallMs2 = (Stopwatch.GetTimestamp() - wall0) * 1000.0 / Stopwatch.Frequency;
        double cpuMs = totalCpuTicks * 1000.0 / Stopwatch.Frequency;
        Volatile.Write(ref _lastPhaseErrors, Volatile.Read(ref phaseErrors));
        _monitor.EndPhase(wallMs2, cpuMs, dop);
        Publish(profilerScope, wallMs2, cpuMs);

        return Volatile.Read(ref processed);
    }

    /// <inheritdoc/>
    public int ForEachSplittable(int count, Func<int, int, WorkQuantum, int> body, WorkKind kind = WorkKind.Fast, string profilerScope = null)
    {
        if (body == null)
            throw new ArgumentNullException(nameof(body));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count должен быть >= 0.");
        if (count == 0)
        {
            // FIX круг-2 №8: пустая фаза публикует нули (не врёт старьём).
            Volatile.Write(ref _lastPhaseErrors, 0);
            _monitor.EndPhase(0, 0, GetDop());
            Publish(profilerScope, 0, 0);
            return 0;
        }

        int dop = GetDop();
        long wall0 = Stopwatch.GetTimestamp();

        if (count < MinBatchSize * 2 || dop <= 1)
        {
            // Sequential fast-path с квантом: остаток добивается тут же, в цикле.
            // SCHEDULING RULE: из body(s,e,quantum) запрещены ЛЮБЫЕ Godot Node API
            // (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
            // Разрешены: чистые вычисления, SoA-массивы, Interlocked/Volatile,
            // ConcurrentQueue.Enqueue (снапшоты), ThreadLocal, ParallelRng.
            int errors = 0;
            int done0 = 0;
            try { done0 = body(0, count, new WorkQuantum(EffectiveQuantumMs())); }
            catch { errors++; done0 = count; }
            done0 = Math.Clamp(done0, 0, count);
            int restStart = done0;
            while (restStart < count)
            {
                int done;
                try { done = body(restStart, count, new WorkQuantum(EffectiveQuantumMs())); }
                catch { errors++; done = count - restStart; }
                done = Math.Clamp(done, 0, count - restStart);
                if (done <= 0)
                {
                    // Body вернула 0 без прогресса и без yield — защита от
                    // бесконечного цикла: добиваем остаток принудительно.
                    // (Корректные body всегда либо прогрессируют, либо yield'ят.)
                    errors++;
                    break;
                }
                restStart += done;
            }
            int total = Math.Max(done0, restStart);
            total = Math.Clamp(total, 0, count);
            double wallMs = (Stopwatch.GetTimestamp() - wall0) * 1000.0 / Stopwatch.Frequency;
            Volatile.Write(ref _lastPhaseErrors, errors);
            // FIX круг-2 №5: sequential splittable fast-path тоже кормит EMA.
            _monitor.RecordWorkerBatch(Thread.CurrentThread.ManagedThreadId, wallMs, count);
            _monitor.EndPhase(wallMs, wallMs, 1);
            Publish(profilerScope, wallMs, wallMs);
            return total;
        }

        long batchSeq = 0;
        int processed = 0;
        long totalCpuTicks = 0;
        int phaseErrors = 0;
        var deques = new WorkStealingQueue<WorkItem>[dop];
        var overflow = new ConcurrentQueue<WorkItem>();
        double quantumMs = EffectiveQuantumMs();
        double quantumTicksD = quantumMs * Stopwatch.Frequency / 1000.0;

        Parallel.For(0, dop, new ParallelOptions { MaxDegreeOfParallelism = dop }, slot =>
        {
            var local = _localDeque.Value;
            local.Clear();
            deques[slot] = local;
            var stealBuf = _stealBuffer.Value;
            int localProcessed = 0;
            long localCpuTicks = 0;
            // FIX круг-2 №7: локальный счётчик — один Add в конце потока.
            int localErrors = 0;

            while (true)
            {
                WorkItem item = default;
                bool hasItem = false;
                if (local.TryPopLocal(out WorkItem li))
                {
                    item = li;
                    hasItem = true;
                }
                else if (overflow.TryDequeue(out WorkItem gi))
                {
                    item = gi;
                    hasItem = true;
                }
                else
                {
                    // FIX круг-2 №10: hint влияет на размер следующего батча:
                    // Heavy-курсор просит батч с costHint=2.0 (режется мельче).
                    float wantHint = kind == WorkKind.Heavy ? 2.0f : 1.0f;
                    int want = _monitor.SuggestBatchSize(kind, wantHint);
                    long start = Interlocked.Add(ref batchSeq, want) - want;
                    if (start < count)
                    {
                        int size = (int)Math.Min((long)want, (long)count - start);
                        float hint = kind == WorkKind.Heavy ? 2.0f : 1.0f;
                        item = new WorkItem((int)start, size, hint, kind);
                        hasItem = true;
                    }
                }

                if (hasItem)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    var quantum = new WorkQuantum(quantumMs);
                    int done;
                    try
                    {
                        // SCHEDULING RULE: из body(s,e,quantum) запрещены ЛЮБЫЕ Godot Node API
                        // (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
                        // Разрешены: чистые вычисления, SoA-массивы, Interlocked/Volatile,
                        // ConcurrentQueue.Enqueue (снапшоты), ThreadLocal, ParallelRng.
                        // КВАНТ КООПЕРАТИВНЫЙ (FIX круг-2 №3): body ОБЯЗАНА вызывать
                        // ShouldYield/ShouldYieldCached и возвращать прогресс — иначе
                        // планировщик лишь ужмёт СЛЕДУЮЩИЙ батч (см. ниже).
                        done = body(item.Start, item.Start + item.Count, quantum);
                    }
                    catch
                    {
                        // Исключение НЕ роняет фазу и НЕ отравляет очередь:
                        // батч считается обработанным целиком (без requeue).
                        localErrors++;
                        done = item.Count;
                    }
                    done = Math.Clamp(done, 0, item.Count);
                    long t1 = Stopwatch.GetTimestamp();
                    double batchMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                    localCpuTicks += t1 - t0;
                    _monitor.RecordWorkerBatch(Thread.CurrentThread.ManagedThreadId,
                        batchMs, item.Count);
                    // FIX круг-2 №3: квант enforced даже если body игнорит yield:
                    // превышение кванта = straggler + принудительный shrink до Min.
                    // Безопасно: RecordWorkerBatch уже засчитал straggler по HeavyMs;
                    // здесь дожимаем адаптацию следующего батча этого потока.
                    if ((t1 - t0) > quantumTicksD)
                        _monitor.ShrinkToMin();
                    // FIX круг-2 №1 (ZeroProgressHang): body вернула done<=0 без
                    // прогресса — НЕ класть обратно в очередь (был бы бесконечный
                    // Requeue). Засчитать батч обработанным целиком + errors++.
                    if (done <= 0)
                    {
                        localErrors++;
                        localProcessed += item.Count;
                        continue;
                    }
                    localProcessed += done;
                    if (done < item.Count)
                    {
                        float newHint = item.CostHint * 1.5f;
                        if (!float.IsFinite(newHint) || newHint <= 0f)
                            newHint = 1.5f;
                        var rest = new WorkItem(item.Start + done, item.Count - done, newHint, kind);
                        // Приоритет — в голову локальной очереди; при переполнении — в глобальный курсор остатков.
                        if (!local.TryPushLocal(rest))
                            overflow.Enqueue(rest);
                    }
                    continue;
                }

                // Курсор и очереди пустые — steal-half, до 2 попыток.
                bool stole = false;
                for (int attempt = 0; attempt < 2 && !stole; attempt++)
                {
                    int victimIdx = ParallelRng.Next(0, dop);
                    if (victimIdx == slot)
                        continue;
                    var victim = Volatile.Read(ref deques[victimIdx]);
                    if (victim == null || victim == local || victim.Count == 0)
                        continue;
                    stealBuf.Clear();
                    // FIX круг-2 №10: кража половины по стоимости (Count×CostHint).
                    int stolen = victim.TryStealHalfByCostInto(stealBuf, static w => w.Count * w.CostHint);
                    if (stolen > 0 && stealBuf.Count > 0)
                    {
                        stole = true;
                        // FIX круг-2 №11: вор сигналит contention.
                        _monitor.RecordSteal();
                        // FIX круг-2 №10: CostHint>2 subdivided на части по MinBatch.
                        foreach (var s in stealBuf)
                        {
                            if (s.CostHint > 2.0f && s.Count > MinBatchSize)
                            {
                                int off = 0;
                                while (off < s.Count)
                                {
                                    int piece = Math.Min(MinBatchSize, s.Count - off);
                                    var part = new WorkItem(s.Start + off, piece, 1.0f, s.Kind);
                                    if (!local.TryPushLocal(part))
                                        overflow.Enqueue(part);
                                    off += piece;
                                }
                            }
                            else if (!local.TryPushLocal(s))
                            {
                                overflow.Enqueue(s);
                            }
                        }
                        stealBuf.Clear();
                    }
                }
                if (!stole)
                {
                    // Финальная перепроверка гонки: пока мы воровали, другой поток
                    // мог Requeue-ить остаток в overflow.
                    if (overflow.TryDequeue(out WorkItem late))
                    {
                        if (!local.TryPushLocal(late))
                            overflow.Enqueue(late);
                        continue;
                    }
                    if (Volatile.Read(ref batchSeq) < count)
                        continue;
                    break;
                }
            }

            Interlocked.Add(ref processed, localProcessed);
            Interlocked.Add(ref totalCpuTicks, localCpuTicks);
            // FIX круг-2 №7: один ADD на поток вместо CAS на исключение.
            if (localErrors > 0)
                Interlocked.Add(ref phaseErrors, localErrors);
        });

        double wallMs2 = (Stopwatch.GetTimestamp() - wall0) * 1000.0 / Stopwatch.Frequency;
        double cpuMs = totalCpuTicks * 1000.0 / Stopwatch.Frequency;
        Volatile.Write(ref _lastPhaseErrors, Volatile.Read(ref phaseErrors));
        _monitor.EndPhase(wallMs2, cpuMs, dop);
        Publish(profilerScope, wallMs2, cpuMs);

        return Volatile.Read(ref processed);
    }

    /// <inheritdoc/>
    public SchedulerStats GetStats()
    {
        return _monitor.GetStats();
    }

    /// <summary>
    /// Сброс EMA и переиспользуемых очередей.
    /// Только в quiescent-состоянии (после join всех workers, из sim-потока) —
    /// никогда конкурентно из worker-потоков.
    /// </summary>
    public void Reset()
    {
        foreach (var q in _localDeque.Values)
            q.Clear();
        foreach (var b in _stealBuffer.Values)
            b.Clear();
        Volatile.Write(ref _lastPhaseErrors, 0);
        _monitor.Reset();
    }
}
