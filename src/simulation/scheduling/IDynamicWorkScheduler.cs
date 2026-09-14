using System;

namespace Game.Simulation.Scheduling;

/// <summary>
/// Снимок статистики планировщика для LoadMonitor / overlay. readonly struct — копия по значению.
/// </summary>
public readonly struct SchedulerStats
{
    /// <summary>Wall-time фазы (мс).</summary>
    public readonly double WallMs;

    /// <summary>Суммарное CPU-время всех потоков (мс).</summary>
    public readonly double CpuMs;

    /// <summary>Дисбаланс в %: (wall - cpu/DOP)/wall*100, 0 = идеал.</summary>
    public readonly int ImbalancePct;

    /// <summary>Самый медленный батч фазы = хвост (мс).</summary>
    public readonly double MaxBatchMs;

    /// <summary>Сглаженная медиана времени батча (мс).</summary>
    public readonly double EmaBatchMs;

    /// <summary>Число батчей дольше HeavyMs за фазу.</summary>
    public readonly int StragglerCount;

    public SchedulerStats(double wallMs, double cpuMs, int imbalancePct, double maxBatchMs, double emaBatchMs, int stragglerCount)
    {
        WallMs = wallMs;
        CpuMs = cpuMs;
        ImbalancePct = imbalancePct;
        MaxBatchMs = maxBatchMs;
        EmaBatchMs = emaBatchMs;
        StragglerCount = stragglerCount;
    }
}

/// <summary>
/// Фасад планировщика — то, что зовёт sim-поток (PLAN.md §4.2).
/// Чистый C#, без зависимости от Godot.
/// </summary>
public interface IDynamicWorkScheduler
{
    /// <summary>
    /// Поэлементный проход [0,count). body обязана быть thread-safe для непересекающихся i.
    /// </summary>
    /// <param name="count">Число элементов (&gt;= 0; при 0 — no-op).</param>
    /// <param name="body">Тело на один индекс. null запрещён.</param>
    /// <param name="kind">Классификация работы (в v1 игнорируется).</param>
    /// <param name="profilerScope">Имя для GameProfiler.RecordPhaseBalance (null = не публиковать).</param>
    // SCHEDULING RULE: из body(i) запрещены ЛЮБЫЕ Godot Node API
    // (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
    // Разрешены: чистые вычисления, SoA-массивы, Interlocked/Volatile,
    // ConcurrentQueue.Enqueue (снапшоты), ThreadLocal, ParallelRng.
    // Всё, что должно попасть в главный поток, — через существующие очереди
    // (PositionQueue/SnapshotQueue) или CallDeferred из sim-потока (не из workers).
    void ForEach(int count, Action<int> body, WorkKind kind = WorkKind.Fast, string profilerScope = null);

    /// <summary>
    /// Диапазонный проход: body(start,endExclusive). Возвращает число обработанных элементов.
    /// </summary>
    /// <param name="count">Размер диапазона [0,count).</param>
    /// <param name="body">Тело на диапазон. null запрещён.</param>
    /// <param name="kind">Классификация работы (в v1 игнорируется).</param>
    /// <param name="profilerScope">Имя для GameProfiler.RecordPhaseBalance (null = не публиковать).</param>
    // SCHEDULING RULE: из body(start,end) запрещены ЛЮБЫЕ Godot Node API
    // (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
    // Разрешены: чистые вычисления, SoA-массивы, Interlocked/Volatile,
    // ConcurrentQueue.Enqueue (снапшоты), ThreadLocal, ParallelRng.
    // Всё, что должно попасть в главный поток, — через существующие очереди
    // (PositionQueue/SnapshotQueue) или CallDeferred из sim-потока (не из workers).
    int ForEachRange(int count, Action<int, int> body, WorkKind kind = WorkKind.Fast, string profilerScope = null);

    /// <summary>
    /// Прерываемая версия (Шаг 4): body обрабатывает диапазон [start,end) и возвращает
    /// число реально обработанных элементов. Внутри body каждые <c>YieldEveryN=8</c>
    /// элементов проверяет <c>quantum.ShouldYield</c>; при истечении возвращает
    /// обработанный префикс — планировщик Requeue-ит остаток как новый WorkItem
    /// с CostHint×1.5. Исключение в body НЕ роняет фазу: считается через Interlocked,
    /// остальные элементы добиваются, покрытие сохраняется.
    /// FIX круг-2 №3 — ЧЕСТНО О КВАНТЕ: квант КООПЕРАТИВНЫЙ. Body ОБЯЗАНА либо
    /// вызывать ShouldYield/ShouldYieldCached каждые YieldEveryN элементов и
    /// возвращать прогресс (done &lt; count) при истечении, либо смириться с тем,
    /// что планировщик лишь ужмёт СЛЕДУЮЩИЙ батч до Min (ShrinkToMin) — уже взятый
    /// батч прервать принудительно НЕЛЬЗЯ без её участия. Body, вернувшая done&lt;=0
    /// без прогресса, считается ошибочной: батч засчитывается обработанным целиком
    /// (+1 к счётчику ошибок), requeue НЕ делается (защита от бесконечного цикла).
    /// </summary>
    /// <param name="count">Размер диапазона [0,count). &lt;0 → ArgumentOutOfRangeException; 0 → no-op/0.</param>
    /// <param name="body">Тело на диапазон: (start, endExclusive, quantum) → processed count [0..end-start]. null запрещён.</param>
    /// <param name="kind">Классификация работы.</param>
    /// <param name="profilerScope">Имя для GameProfiler.RecordPhaseBalance (null = не публиковать).</param>
    // SCHEDULING RULE: из body(s,e,quantum) запрещены ЛЮБЫЕ Godot Node API
    // (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
    // Разрешены: чистые вычисления, SoA-массивы, Interlocked/Volatile,
    // ConcurrentQueue.Enqueue (снапшоты), ThreadLocal, ParallelRng.
    // Всё, что должно попасть в главный поток, — через существующие очереди
    // (PositionQueue/SnapshotQueue) или CallDeferred из sim-потока (не из workers).
    int ForEachSplittable(int count, Func<int, int, WorkQuantum, int> body, WorkKind kind = WorkKind.Fast, string profilerScope = null);

    /// <summary>Снимок статистики последней фазы (struct-копия).</summary>
    SchedulerStats GetStats();

    /// <summary>Сброс EMA-состояния (смена скорости симуляции).</summary>
    void Reset();
}
