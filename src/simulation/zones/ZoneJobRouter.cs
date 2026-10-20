using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

// ============================================================================
// §119 P1: ПУШ-РОУТЕР работ зон (спека task-15). Пуш = УВЕДОМЛЕНИЕ: работа
// живёт в реестре домена (JobSpatialPyramid — источник истины и путь
// разнорабочих, НЕ трогаем); роутер ведёт только ОЧЕРЕДИ ЗОН для P2-Рабочих.
//
// Контракт:
//  * Push(type,x,y,jobId) — O(1), lock-free, БЕЗ глобального лока (спека:
//    «ConcurrentQueue на зону»): владелец клетки — O(1) ZoneCoverageMap;
//    покрыта → lock-free enqueue в очередь своей зоны; нет → непокрытые.
//    Ноль словарных операций на горячем пути → 10k пушей < 5 мс даже в
//    Debug-сборке (замер приёмки). Дубликат пуша не дедуплицируем —
//    DequeueNearest при раскладке идемпотентен (dedup по jobId).
//  * DequeueNearest(zoneId,wx,wy) — БЛИЖАЙШАЯ к рабочему работа (не FIFO).
//    Дренаж очереди + выбор ближайшей под замком ЗОНЫ (не глобальным) с
//    возвратом остатка в исходном порядке прихода: работа не выдаётся
//    дважды; ничья дистанции — меньший jobId (детерминизм).
//  * Unassign(jobId) — возврат работы в очередь её зоны (прерывания нуждами,
//    watchdog) — §119 «работа возвращается в очередь зоны». lock-free.
//  * Sweep(now) — репуш НЕпокрытых старше 30с (мгновенного прохода при
//    создании зоны НЕТ); чистит работы, исчезнувшие из реестра (реестр —
//    источник истины).
//  * Домены зовут ТОЛЬКО Push — никаких прямых записей в очереди зон.
// ============================================================================

public sealed class ZoneJobRouter
{
    public static ZoneJobRouter Instance { get; } = new();

    /// <summary>Карта покрытия клетка→зона (отдельно на тип работ). Управляется ZoneManager.ScheduleRepartition.</summary>
    public ZoneCoverageMap Coverage { get; } = new();

    /// <summary>Работа в маршруте (передаётся Рабочему при выдаче).</summary>
    public readonly struct ZoneJob
    {
        public readonly int JobId;
        public readonly JobTypeId Type;
        public readonly int X;
        public readonly int Y;

        public ZoneJob(int jobId, JobTypeId type, int x, int y)
        {
            JobId = jobId;
            Type = type;
            X = x;
            Y = y;
        }
    }

    /// <summary>Запись в очереди зоны: маршрут + порядковый номер прихода (для детерминированного возврата остатка).</summary>
    private readonly struct QueuedJob
    {
        public readonly int JobId;
        public readonly JobTypeId Type;
        public readonly int X;
        public readonly int Y;
        public readonly long Seq;

        public QueuedJob(int jobId, JobTypeId type, int x, int y, long seq)
        {
            JobId = jobId;
            Type = type;
            X = x;
            Y = y;
            Seq = seq;
        }
    }

    /// <summary>Непокрытая работа: маршрут + таймштамп постановки (для 30с репуша).</summary>
    private readonly struct UncoveredEntry
    {
        public readonly int JobId;
        public readonly JobTypeId Type;
        public readonly int X;
        public readonly int Y;
        public readonly long EnqueuedMs;

        public UncoveredEntry(int jobId, JobTypeId type, int x, int y, long enqueuedMs)
        {
            JobId = jobId;
            Type = type;
            X = x;
            Y = y;
            EnqueuedMs = enqueuedMs;
        }
    }

    private const int RepushAfterMs = 30_000;

    /// <summary>
    /// §119 P2: квалифицированная работа в очереди зоны доступна разнорабочему
    /// (Laborer) после 60с простоя. До этого Laborer'у она НЕ отдаётся
    /// (JobDispatcher сверяет IsHeldForWorkerFresh) — её ждёт Worker зоны.
    /// </summary>
    public const long WorkerHoldMs = 60_000;

    /// <summary>Тестовый овервет часов (IsHeldForWorkerFresh/репуш-тесты). &lt;0 = реальное время.</summary>
    public static long TestClockOverrideMs = -1;

    /// <summary>Роутерные «сейчас» (ms): тест-овервет или Environment.TickCount64.</summary>
    public static long NowMs => TestClockOverrideMs >= 0 ? TestClockOverrideMs : Environment.TickCount64;

    /// <summary>Очередь зоны: lock-free ConcurrentQueue (пуш без лока); замок = Gate — только для дренажа (dequeue/sweep/remove).</summary>
    private sealed class ZoneGate
    {
        public readonly ConcurrentQueue<QueuedJob> Q = new();
    }

    private readonly ConcurrentDictionary<int, ZoneGate> _zoneQueues = new();
    private readonly ConcurrentQueue<UncoveredEntry> _uncovered = new();

    /// <summary>§119 P2: jobId → момент постановки в очередь зоны (для 60с-холда Laborer'а).
    /// Плотный массив по id пирамиды (id плотные) +1 (0 = нет записи) — дешевле
    /// ConcurrentDictionary на горячем пуш-пути (P1-регресс 6.8мс/10k → цель <5мс).</summary>
    private long[] _queuedAt = new long[1024];

    private void SetQueuedAt(int jobId, long now)
    {
        long[] arr = _queuedAt;
        if (jobId >= arr.Length)
        {
            lock (_zoneQueues) // редкий рост; _zoneQueues уже ConcurrentDictionary — просто общий замок
            {
                if (jobId >= _queuedAt.Length)
                    Array.Resize(ref _queuedAt, Math.Max(_queuedAt.Length * 2, jobId + 1024));
                arr = _queuedAt;
            }
        }
        Volatile.Write(ref arr[jobId], now + 1); // +1: 0 = «нет записи»
    }

    private bool TryGetQueuedAt(int jobId, out long queuedAt)
    {
        queuedAt = 0;
        long[] arr = _queuedAt;
        if ((uint)jobId >= (uint)arr.Length)
            return false;
        long v = Volatile.Read(ref arr[jobId]);
        if (v == 0)
            return false;
        queuedAt = v - 1;
        return true;
    }

    private void ClearQueuedAt(int jobId)
    {
        long[] arr = _queuedAt;
        if ((uint)jobId < (uint)arr.Length)
            Volatile.Write(ref arr[jobId], 0);
    }

    /// <summary>Выданные (взятые Рабочим) работы: jobId → (зона, маршрут) — для Unassign.</summary>
    private readonly ConcurrentDictionary<int, (int ZoneId, QueuedJob Job)> _assigned = new();

    private long _seq;
    private ZoneGate GateOrNew(int zoneId) =>
        _zoneQueues.GetOrAdd(zoneId, static _ => new ZoneGate());

    // ---------------- Push ----------------

    /// <summary>
    /// §119: пуш новой работы доменом. O(1), lock-free, без глобального лока,
    /// без heap-аллокаций на добавление. jobId ≥ 0 валиден (пирамида нумерует
    /// с 0); type None или отрицательный id игнорируются. nowMs &lt; 0 —
    /// реальное время (Environment.TickCount64); тесты передают синтетическую
    /// базу для детерминизма.
    /// </summary>
    public void Push(JobTypeId type, int x, int y, int jobId, long nowMs = -1)
    {
        if (type == JobTypeId.None || jobId < 0)
            return;
        long now = nowMs >= 0 ? nowMs : Environment.TickCount64;
        // Throttled пересчёт покрытия, если зоны менялись (дешёвый чек на пуш).
        Coverage.RebuildIfNeeded(now);

        if (Coverage.TryGetOwner(x, y, type, out int zoneId) && zoneId > 0)
        {
            GateOrNew(zoneId).Q.Enqueue(new QueuedJob(jobId, type, x, y, Interlocked.Increment(ref _seq)));
            SetQueuedAt(jobId, now); // §119 P2: отсчёт 60с-холда Worker'а
        }
        else
            _uncovered.Enqueue(new UncoveredEntry(jobId, type, x, y, now));
    }

    // ---------------- Выдача (P2-Рабочий) ----------------

    /// <summary>
    /// Ближайшая к рабочему работа из очереди зоны (спека: НЕ FIFO). Дренаж
    /// очереди под замком ЗОНЫ, выбор ближайшей (ничья дистанции — меньший
    /// jobId), остаток возвращается в порядке прихода. Работа не выдаётся
    /// дважды. Реестр здесь НЕ проверяется (чистка мёртвых — дело Sweep;
    /// проверка на горячем пути выдачи недешёва). false — очередь зоны пуста.
    /// </summary>
    public bool DequeueNearest(int zoneId, int workerX, int workerY, out ZoneJob job)
    {
        job = default;
        if (zoneId <= 0 || !_zoneQueues.TryGetValue(zoneId, out var gate))
            return false;

        lock (gate)
        {
            if (gate.Q.IsEmpty)
                return false;

            // Дренаж: dedup по jobId (дубли-пуши), выброс мёртвых, поиск лучшей.
            var seen = new Dictionary<int, (QueuedJob Job, int Order)>();
            int order = 0;
            int bestId = -1;
            int bestDist = int.MaxValue;
            QueuedJob best = default;
            while (gate.Q.TryDequeue(out var j))
            {
                if (!seen.TryAdd(j.JobId, (j, order++)))
                    continue; // дубль — храним первое пришествие
                int dx = j.X - workerX;
                int dy = j.Y - workerY;
                int d = dx * dx + dy * dy;
                if (d < bestDist || (d == bestDist && j.JobId < bestId))
                {
                    bestDist = d;
                    bestId = j.JobId;
                    best = j;
                }
            }
            if (bestId < 0)
                return false;

            // Возврат остатка в исходном порядке прихода (детерминизм очереди).
            foreach (var kv in seen)
            {
                if (kv.Key == bestId)
                    continue;
                gate.Q.Enqueue(kv.Value.Job);
            }

            _assigned[bestId] = (zoneId, best);
            job = new ZoneJob(bestId, best.Type, best.X, best.Y);
            return true;
        }
    }

    /// <summary>Сколько записей сейчас в очереди зоны (диагностика/UI P2; возможны дубли-пуши).</summary>
    public int QueueDepth(int zoneId) =>
        _zoneQueues.TryGetValue(zoneId, out var gate) ? gate.Q.Count : 0;

    /// <summary>
    /// §119 P2: работа стоит в очереди зоны МЕНЬШЕ 60с — она «держится» для
    /// Worker'а своей зоны и разнорабочему (Laborer через пирамиду) НЕ отдаётся.
    /// false также для работ не из очередей зон (обычный пирамидный путь).
    /// Вызывается JobDispatcher'ом ПЕРЕД отдачей Laborer'у.
    /// </summary>
    public bool IsHeldForWorkerFresh(int jobId)
    {
        return TryGetQueuedAt(jobId, out long queuedAt)
            && NowMs - queuedAt < WorkerHoldMs;
    }

    /// <summary>Таймштамп постановки работы в очередь зоны (0 = нет записи; для тестов/диагностики).</summary>
    public long GetQueuedAtMs(int jobId) =>
        TryGetQueuedAt(jobId, out long t) ? t : 0;

    // ---------------- Возврат (нужды/watchdog, §119) ----------------

    /// <summary>
    /// Вернуть работу в очередь её зоны (прерывание нуждами, watchdog «упёрся
    /// в стену»). lock-free. false — работа не на руках у Рабочего (уже в
    /// очереди или снята) — возвращать нечего. Позиция в очереди — хвост
    /// (последний пришедший), 60с-холд перезапускается (прогресс работы в
    /// клетке сохранён — CellProgressStore, агентов прогресс не теряется).
    /// </summary>
    public bool Unassign(int jobId, long nowMs = -1)
    {
        long now = nowMs >= 0 ? nowMs : Environment.TickCount64;
        if (!_assigned.TryRemove(jobId, out var rec))
            return false;
        GateOrNew(rec.ZoneId).Q.Enqueue(new QueuedJob(jobId, rec.Job.Type, rec.Job.X, rec.Job.Y,
            Interlocked.Increment(ref _seq)));
        SetQueuedAt(jobId, now); // §119 P2: холд 60с стартует заново после возврата
        return true;
    }

    // ---------------- Sweep (репуш 30с + чистка) ----------------

    /// <summary>
    /// Репуш: (1) throttled пересчёт покрытия; (2) непокрытые работы старше
    /// 30с пробуют снова (младше — возвращаются с исходным таймштампом);
    /// (3) работы, исчезнувшие из реестра (выполнены/сняты), вычищаются из
    /// всех структур. Возвращает число переложенных из «непокрытых» в очереди
    /// зон.
    /// </summary>
    public int Sweep(long nowMs)
    {
        int moved = 0;
        Coverage.RebuildIfNeeded(nowMs);

        // 1. Раскладка непокрытых: дренаж всей очереди; пуш-потоки продолжают
        //    класть новые записи (их разберёт следующий Sweep).
        if (!_uncovered.IsEmpty)
        {
            var drained = new List<UncoveredEntry>(_uncovered.Count + 16);
            while (_uncovered.TryDequeue(out var e))
                drained.Add(e);

            foreach (var e in drained)
            {
                if (!JobDispatcher.Instance.JobIndex.TryGetJob(e.JobId, out _))
                    continue; // мёртвая — выкинута
                if (nowMs - e.EnqueuedMs < RepushAfterMs)
                {
                    _uncovered.Enqueue(e); // ещё рано: хранит ИСХОДНЫЙ таймштамп
                    continue;
                }
                if (Coverage.TryGetOwner(e.X, e.Y, e.Type, out int zoneId) && zoneId > 0)
                {
                    GateOrNew(zoneId).Q.Enqueue(new QueuedJob(e.JobId, e.Type, e.X, e.Y,
                        Interlocked.Increment(ref _seq)));
                    moved++;
                }
                else
                {
                    _uncovered.Enqueue(e); // покрытия всё ещё нет — ждём следующие 30с
                }
            }
        }

        // 2. Чистка мёртвых работ (реестр — истина) в очередях зон: дренаж + фильтр.
        //    §119 P2: мёртвый id снимается и с 60с-холда (TryRemove — только
        //    выжившие перезаписывают timestamp при следующем пушe/Unassign).
        foreach (var kv in _zoneQueues)
        {
            var gate = kv.Value;
            if (gate.Q.IsEmpty)
                continue;
            lock (gate)
            {
                if (gate.Q.IsEmpty)
                    continue;
                var kept = new List<QueuedJob>(gate.Q.Count);
                while (gate.Q.TryDequeue(out var j))
                    if (JobDispatcher.Instance.JobIndex.TryGetJob(j.JobId, out _))
                        kept.Add(j);
                    else
                        ClearQueuedAt(j.JobId);
                foreach (var j in kept)
                    gate.Q.Enqueue(j);
            }
        }

        // 3. Чистка выданных и непокрытых.
        foreach (var kv in _assigned)
            if (!JobDispatcher.Instance.JobIndex.TryGetJob(kv.Key, out _))
                _assigned.TryRemove(kv.Key, out _);
        if (!_uncovered.IsEmpty)
        {
            var kept = new List<UncoveredEntry>(_uncovered.Count);
            while (_uncovered.TryDequeue(out var e))
                if (JobDispatcher.Instance.JobIndex.TryGetJob(e.JobId, out _))
                    kept.Add(e);
                else
                    ClearQueuedAt(e.JobId);
            foreach (var e in kept)
                _uncovered.Enqueue(e);
        }
        return moved;
    }

    // ---------------- Жизненный цикл зон ----------------

    /// <summary>Зона удалена (снос якоря P5, RemoveZone): её очередь падает в непокрытые.</summary>
    public void OnZoneRemoved(int zoneId, long nowMs = -1)
    {
        long now = nowMs >= 0 ? nowMs : Environment.TickCount64;
        if (!_zoneQueues.TryRemove(zoneId, out var gate))
            return;
        lock (gate)
        {
            while (gate.Q.TryDequeue(out var j))
            {
                _uncovered.Enqueue(new UncoveredEntry(j.JobId, j.Type, j.X, j.Y, now));
                ClearQueuedAt(j.JobId); // холд снят вместе с очередью
            }
        }
    }

    /// <summary>Полный сброс (смена мира): все очереди/записи/покрытие.</summary>
    public void Reset()
    {
        _zoneQueues.Clear();
        _uncovered.Clear();
        _assigned.Clear();
        Array.Clear(_queuedAt, 0, _queuedAt.Length);
        Coverage.Reset();
    }

    // ---------------- Диагностика (тесты/отчёт) ----------------

    public int UncoveredCount => _uncovered.Count;
    public int AssignedRecordCount => _assigned.Count;
    public int ZoneQueueCount => _zoneQueues.Count;

    /// <summary>Немедленный пересчёт покрытия в обход throttle (тесты).</summary>
    public void ForceRebuild(long nowMs) => Coverage.RebuildNow(nowMs);
}
