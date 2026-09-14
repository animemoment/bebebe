using System;
using System.Threading;

namespace Game.Simulation.Scheduling;

/// <summary>
/// Измерение перегруза ядер + адаптация размера батча (PLAN.md §4.6, Шаг 2).
/// Вся EMA/imbalance-логика живёт здесь: планировщик только спрашивает
/// <see cref="SuggestBatchSize"/> и сообщает замеры через
/// <see cref="RecordWorkerBatch"/>/<see cref="EndPhase"/>.
/// Без зависимости от Godot. Без contention: запись — только в ThreadLocal,
/// агрегация — однопоточно в <see cref="EndPhase"/> (sim-поток уже дождался join).
/// </summary>
public sealed class LoadMonitor
{
    /// <summary>EMA-коэффициент адаптации размера батча.</summary>
    private const double AdaptRate = 0.25;

    /// <summary>Шаг роста лёгкого батча.</summary>
    private const int BatchGrowStep = 32;

    private sealed class WorkerAccum
    {
        public double EmaMs = 0.25;
        public int CurrentSize = DynamicWorkScheduler.InitialBatchSize;
        public double TotalMs;
        public double MaxMs;
        public long Batches;
        public long Items;
        public int Stragglers;
    }

    private readonly ThreadLocal<WorkerAccum> _local =
        new(() => new WorkerAccum(), trackAllValues: true);

    private double _wallMs;
    private double _cpuMs;
    private double _maxBatchMs;
    private double _emaBatchMs;
    private int _stragglerCount;
    private int _imbalancePct;

    // FIX круг-2 №11: глобальный флаг contention (выставляется вором при успешном
    // steal, сбрасывается в EndPhase). Пока флаг стоит, SuggestBatchSize возвращает
    // уменьшенный размер — жертва с очередью получит сигнал уменьшить батч
    // на следующей фазе. Запись — lock-free через Volatile (флаг int 0/1).
    private int _stealContention;

    /// <summary>Порог тяжёлого батча (мс). Поле, а не const — для тюнинга из overlay/тестов.</summary>
    public double HeavyBatchMs = 1.5;

    private double EffectiveHeavyMs => HeavyBatchMs > 0.0 && double.IsFinite(HeavyBatchMs) ? HeavyBatchMs : 1.5;

    /// <summary>
    /// Записать время одного батча с worker-потока. Lock-free: пишет только в ThreadLocal.
    /// Сразу адаптирует размер следующего батча этого потока:
    /// ms&gt;Heavy или Ema&gt;Heavy → размер /2 до Min; ms&lt;Heavy*0.25 → +32 до Max.
    /// </summary>
    /// <param name="workerId">Информационный id потока (источник истины — ThreadLocal).</param>
    /// <param name="batchMs">Время батча в мс. Битые замеры (NaN/Inf/&lt;0) игнорируются.</param>
    /// <param name="batchSize">Размер батча, должен быть &gt; 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">Если batchSize &lt;= 0.</exception>
    public void RecordWorkerBatch(int workerId, double batchMs, int batchSize)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Размер батча должен быть > 0.");
        if (double.IsNaN(batchMs) || double.IsInfinity(batchMs) || batchMs < 0.0)
            return; // Битый замер — игнорируем, EMA не портим.

        double heavy = EffectiveHeavyMs;
        var a = _local.Value;
        a.EmaMs += (batchMs - a.EmaMs) * AdaptRate;
        if (batchMs > heavy || a.EmaMs > heavy)
            a.CurrentSize = Math.Max(DynamicWorkScheduler.MinBatchSize, a.CurrentSize / 2);
        else if (batchMs < heavy * 0.25)
            a.CurrentSize = Math.Min(DynamicWorkScheduler.MaxBatchSize, a.CurrentSize + BatchGrowStep);

        a.TotalMs += batchMs;
        if (batchMs > a.MaxMs)
            a.MaxMs = batchMs;
        a.Batches++;
        a.Items += batchSize;
        if (batchMs > heavy)
            a.Stragglers++;
    }

    /// <summary>
    /// Завершить фазу: агрегировать thread-local аккумуляторы.
    /// Вызывать однопоточно из sim-потока после join. Битые wall/cpu (NaN/Inf/&lt;=0)
    /// игнорируются (хранятся нули, imbalance = 0).
    /// Per-phase аккумуляторы сбрасываются; EMA и CurrentSize живут между фазами.
    /// </summary>
    public void EndPhase(double wallMs, double cpuMs, int degreeOfParallelism)
    {
        double maxMs = 0.0;
        double totalMs = 0.0;
        long batches = 0;
        int stragglers = 0;

        foreach (var a in _local.Values)
        {
            if (a.MaxMs > maxMs)
                maxMs = a.MaxMs;
            totalMs += a.TotalMs;
            batches += a.Batches;
            stragglers += a.Stragglers;
            // Сброс per-phase аккумуляторов; EMA-состояние живёт отдельно.
            a.TotalMs = 0.0;
            a.MaxMs = 0.0;
            a.Batches = 0;
            a.Items = 0;
            a.Stragglers = 0;
        }

        _wallMs = double.IsFinite(wallMs) && wallMs > 0.0 ? wallMs : 0.0;
        _cpuMs = double.IsFinite(cpuMs) && cpuMs > 0.0 ? cpuMs : 0.0;
        _maxBatchMs = maxMs;
        _stragglerCount = Math.Max(0, stragglers);
        // FIX круг-2 №11: флаг contention живёт одну фазу — сброс в EndPhase.
        Volatile.Write(ref _stealContention, 0);
        if (batches > 0)
        {
            double avg = totalMs / batches;
            _emaBatchMs = _emaBatchMs <= 0.0 ? avg : _emaBatchMs + (avg - _emaBatchMs) * AdaptRate;
        }

        _imbalancePct = 0;
        if (_wallMs > 0.001 && _cpuMs > 0.001)
        {
            int dop = Math.Max(1, degreeOfParallelism);
            double ideal = _cpuMs / dop;
            int imb = (int)Math.Round(Math.Max(0.0, (_wallMs - ideal) / _wallMs) * 100.0);
            _imbalancePct = Math.Clamp(imb, 0, 100);
        }
    }

    /// <summary>Снимок статистики (struct-копия, без аллокаций).</summary>
    public SchedulerStats GetStats()
    {
        return new SchedulerStats(_wallMs, _cpuMs, _imbalancePct, _maxBatchMs, _emaBatchMs, _stragglerCount);
    }

    /// <summary>
    /// Предложить стартовый/текущий размер батча под kind с учётом EWMA потока.
    /// Возвращает адаптированный CurrentSize (см. <see cref="RecordWorkerBatch"/>),
    /// clamp [MinBatchSize..MaxBatchSize]. Heavy-работы на свежей фазе
    /// (у потока ещё нет замеров) стартуют с MinBatchSize.
    /// FIX круг-2 №10: costHint влияет на размер — эффективный размер =
    /// base / max(1, costHint): дорогой чанк режется мельче.
    /// FIX круг-2 №11: при выставленном флаге steal-contention размер
    /// уменьшается вдвое (сигнал жертве с очередью ужаться на следующей фазе).
    /// </summary>
    public int SuggestBatchSize(WorkKind kind, float costHint = 1.0f)
    {
        var a = _local.Value;
        int size;
        if (kind == WorkKind.Heavy && a.Batches == 0)
        {
            a.CurrentSize = DynamicWorkScheduler.MinBatchSize;
            size = DynamicWorkScheduler.MinBatchSize;
        }
        else
        {
            size = Math.Clamp(a.CurrentSize, DynamicWorkScheduler.MinBatchSize, DynamicWorkScheduler.MaxBatchSize);
        }
        float hint = (!float.IsFinite(costHint) || costHint <= 0f) ? 1.0f : costHint;
        if (hint > 1.0f)
            size = Math.Max(DynamicWorkScheduler.MinBatchSize, (int)(size / hint));
        // FIX круг-2 №11: флаг contention — ужаться вдвое (не ниже Min).
        if (Volatile.Read(ref _stealContention) != 0)
            size = Math.Max(DynamicWorkScheduler.MinBatchSize, size / 2);
        return size;
    }

    // FIX круг-2 №11: вор зовёт при успешном steal (глобальный флаг contention).
    /// <summary>
    /// Отметить успешное воровство (зовёт вор). Выставляет глобальный флаг
    /// contention: на следующей фазе <see cref="SuggestBatchSize"/> вернёт
    /// уменьшенный размер. Флаг сбрасывается в <see cref="EndPhase"/>.
    /// Lock-free: один Volatile.Write.
    /// </summary>
    public void RecordSteal()
    {
        Volatile.Write(ref _stealContention, 1);
    }

    // FIX круг-2 №3: принудительный shrink после кванта, даже если body не yield'ила.
    /// <summary>
    /// Принудительно ужать следующий батч потока до Min (квант превышен,
    /// body yield игнорировала). Lock-free: пишет только в ThreadLocal.
    /// </summary>
    public void ShrinkToMin()
    {
        _local.Value.CurrentSize = DynamicWorkScheduler.MinBatchSize;
    }

    /// <summary>
    /// Сброс накопленной статистики и EMA (смена скорости симуляции).
    /// Только в quiescent-состоянии (после join всех workers, из sim-потока),
    /// никогда конкурентно из worker-потоков.
    /// </summary>
    public void Reset()
    {
        _wallMs = 0.0;
        _cpuMs = 0.0;
        _maxBatchMs = 0.0;
        _emaBatchMs = 0.0;
        _stragglerCount = 0;
        _imbalancePct = 0;
        foreach (var a in _local.Values)
        {
            a.EmaMs = 0.25;
            a.CurrentSize = DynamicWorkScheduler.InitialBatchSize;
            a.TotalMs = 0.0;
            a.MaxMs = 0.0;
            a.Batches = 0;
            a.Items = 0;
            a.Stragglers = 0;
        }
    }
}
