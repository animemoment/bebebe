using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Высокопроизводительный диспетчер задач (Hierarchical Work Matcher).
/// Способен распределять тысячи задач за доли миллисекунды без жестких капов (20/тик)
/// и слепых зон на основе JobSpatialPyramid и FlatIdleWorkerPool.
/// </summary>
public sealed class JobDispatcher
{
    public static JobDispatcher Instance { get; } = new();

    public JobSpatialPyramid JobIndex { get; } = new();
    public FlatIdleWorkerPool IdleWorkers { get; } = new();

    public const int MaxAssignmentsPerTick = 4096;

    /// <summary>Резерв-маркер CAS клейма агента (диспатчер + Worker-выдача).</summary>
    public const int AgentReservedMarker = -2;

    // Пакетный буфер свободных агентов на один проход диспетчеризации
    private const int MaxBatchWorkers = 4096;
    private readonly int[] _workerBatchBuffer = new int[MaxBatchWorkers];

    // task-2/perf: курсор ротации стартового индекса батча свободных агентов.
    // См. комментарий в DispatchPendingJobs: без ротации «необслуживаемые» агенты в
    // голове FlatIdleWorkerPool выжигали квант 0.8 мс каждый диспатч, а хвост батча
    // не осматривался никогда. Доступ только из sim-потока (диспетчер) — volatile не нужен.
    private int _dispatchScanCursor;

    // Квант максимального времени на один тик диспетчеризации (0.8 мс)
    private const double MaxDispatchBudgetMs = 0.8;

    public void TriggerImmediateDispatch()
    {
    }

    /// <summary>
    /// Главная точка входа: пакетное распределение задач свободным агентам.
    /// Больше никаких 20 назначений за тик — распределяет весь доступный пул
    /// в пределах кванта времени (< 0.8 мс).
    /// </summary>
    public void DispatchPendingJobs(AgentDataPool pool, SimulationContext ctx, int chunkBatchScale = 1)
    {
        int unclaimed = JobIndex.UnclaimedCount;
        int idleCount = IdleWorkers.TotalIdleCount;
        if (unclaimed <= 0 || idleCount <= 0)
            return;

        using (GameProfiler.ScopeCustom("Simulation.JobDispatch"))
        {
            long startTicks = Stopwatch.GetTimestamp();
            double ticksToMs = 1000.0 / Stopwatch.Frequency;

            bool hasStockpileSpace = StockpileManager.Instance.HasFreeSpace;
            bool hasAvailableLogs = GroundItemManager.Instance.HasAvailableLogs;

            int toCollect = Math.Min(idleCount, MaxBatchWorkers);
            int collected = IdleWorkers.CollectIdleWorkers(toCollect, _workerBatchBuffer, pool);
            if (collected <= 0)
                return;

            JobIndex.BeginClaimEpoch();

            // task-2/perf: ротация стартового индекса пакета idle-агентов.
            // Батч обрабатывается до исчерпания кванта (MaxDispatchBudgetMs), а
            // CollectIdleWorkers всегда копирует пул с нулевого индекса и swap-and-pop
            // не двигает агентов, которым работа не подошла. Поэтому при фиксированном
            // старте одни и те же «необслуживаемые» агенты в голове массива выжигали
            // весь квант каждый диспатч, а агенты в хвосте батча не осматривались
            // НИКОГДА — визуально «работу распределяют очень медленно». Тот же приём
            // уже применён в ZoneManager (_zoneScanIndex) и FillPrioritizedUnclaimed.
            // Первый диспатч стартует с 0 — поведение не меняется.
            int scanStart = _dispatchScanCursor % collected;
            if (scanStart < 0)
                scanStart = 0;

            int assigned = 0;
            int examined = 0;
            for (int n = 0; n < collected; n++)
            {
                // Проверка бюджета времени каждые 64 назначения
                if ((n & 63) == 0 && n > 0)
                {
                    double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * ticksToMs;
                    if (elapsedMs >= MaxDispatchBudgetMs)
                        break;
                }

                examined = n + 1;
                int i = scanStart + n;
                if (i >= collected)
                    i -= collected;

                int agentIndex = _workerBatchBuffer[i];
                if (agentIndex < 0 || agentIndex >= pool.Capacity || pool.States[agentIndex] != AgentState.Idle)
                    continue;

                // Атомарно резервируем агента
                if (Interlocked.CompareExchange(ref pool.CurrentJobId[agentIndex], AgentReservedMarker, -1) != -1)
                    continue;

                try
                {
                    int ax = pool.CurrentCellX[agentIndex];
                    int ay = pool.CurrentCellY[agentIndex];

                    if (JobIndex.TryClaimJobForAgent(
                        ax, ay,
                        pool.EquippedTools[agentIndex],
                        pool, agentIndex, ctx,
                        hasStockpileSpace, hasAvailableLogs,
                        out var activeJob))
                    {
                        IdleWorkers.RemoveIdleWorker(agentIndex, pool);
                        pool.CurrentJobId[agentIndex] = activeJob.Id;
                        pool.CurrentJobType[agentIndex] = activeJob.TypeId;

                        if (JobRegistry.TryGetHandler(activeJob.TypeId, out var handler))
                        {
                            try
                            {
                                handler.OnStart(agentIndex, activeJob, pool, ctx);
                                pool.LastJobCategory[agentIndex] = (int)JobPriorityManager.Instance.GetCategory(activeJob.TypeId);

                                if (pool.States[agentIndex] == AgentState.Idle)
                                {
                                    // Откат при отказе OnStart
                                    int rollbackId = pool.CurrentJobId[agentIndex];
                                    if (rollbackId != -1)
                                    {
                                        JobIndex.ReleaseWorkerClaim(rollbackId);
                                        pool.CurrentJobId[agentIndex] = -1;
                                        pool.CurrentJobType[agentIndex] = JobTypeId.None;
                                    }
                                    IdleWorkers.AddIdleWorker(agentIndex, pool);
                                }
                                else
                                {
                                    assigned++;
                                }
                            }
                            catch (Exception ex)
                            {
                                GD.PrintErr($"[JobDispatcher] OnStart ошибка: {ex.Message}");
                                JobIndex.ReleaseWorkerClaim(activeJob.Id);
                                pool.CurrentJobId[agentIndex] = -1;
                                pool.CurrentJobType[agentIndex] = JobTypeId.None;
                                pool.States[agentIndex] = AgentState.Idle;
                                IdleWorkers.AddIdleWorker(agentIndex, pool);
                            }
                        }
                        else
                        {
                            JobIndex.ReleaseWorkerClaim(activeJob.Id);
                            pool.CurrentJobId[agentIndex] = -1;
                            pool.CurrentJobType[agentIndex] = JobTypeId.None;
                            IdleWorkers.AddIdleWorker(agentIndex, pool);
                        }
                    }
                    else
                    {
                        // Работа не найдена — возвращаем агента
                        pool.CurrentJobId[agentIndex] = -1;
                    }
                }
                catch
                {
                    pool.CurrentJobId[agentIndex] = -1;
                    throw;
                }
            }

            // Курсор остаётся в [0, collected): следующий диспатч продолжит с того места,
            // на котором оборвался квант (при полном проходе — с прежней позиции).
            _dispatchScanCursor = (_dispatchScanCursor + examined) % collected;
        }
    }

    public int RegisterJob(JobData job) => JobIndex.RegisterJob(job);
    public void RegisterBatch(List<JobData> jobs) => JobIndex.RegisterBatch(jobs);
    public void UnregisterJob(int jobId) => JobIndex.RemoveJob(jobId, out _);
    public bool TryUnregisterJob(int jobId) => JobIndex.RemoveJob(jobId, out _);
    public void UnregisterJobByPos(int x, int y, JobTypeId type) => JobIndex.RemoveJobByPos(x, y, type, out _);
    public void UnregisterBatchByPositions(List<(int X, int Y)> positions, JobTypeId type) => JobIndex.RemoveBatchByPositions(positions, type);

    public void ReleaseJobWorker(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        int jobId = pool.CurrentJobId[agentIndex];
        if (jobId == AgentReservedMarker)
        {
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
            return;
        }

        if (jobId != -1)
        {
            if (JobRegistry.TryGetHandler(pool.CurrentJobType[agentIndex], out var handler))
            {
                try
                {
                    handler.OnCancel(agentIndex, pool, ctx);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[JobDispatcher] OnCancel error #{agentIndex}: {ex.Message}");
                }
            }

            // §119 P2: Worker'а прервали (нужды/watchdog/отмена) — работа
            // возвращается в ОЧЕРЕДЬ ЕГО ЗОНЫ (прогресс в клетке уже сохранён
            // хендлером через CellProgressStore/WorkProgressTracker). Клейм
            // пирамиды тоже снимается: работа снова unclaimed — следующий
            // Worker зоны может её заклеймить, а после 60с она доступна и
            // Laborer'ам. Laborer'у — легаси: только ReleaseWorkerClaim.
            if (pool.Classes[agentIndex] == AgentClass.Worker)
            {
                JobIndex.ReleaseWorkerClaim(jobId);
                ZoneJobRouter.Instance.Unassign(jobId);
            }
            else
                JobIndex.ReleaseWorkerClaim(jobId);
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
        }

        pool.WaypointCount[agentIndex] = 0;
        pool.WaypointIndex[agentIndex] = 0;
        pool.States[agentIndex] = AgentState.Idle;
        pool.JobSearchTimer[agentIndex] = 4.0f + (float)ParallelRng.NextDouble() * 4.0f;
        // §119 P2: Worker НЕ возвращается в общий пул бездельников — его
        // выдаёт только его зона (WorkerZoneJobSystem). Laborer — как раньше.
        if (pool.Classes[agentIndex] != AgentClass.Worker)
            IdleWorkers.AddIdleWorker(agentIndex, pool);
    }

    public void ReleaseJobWorkerForStuck(int agentIndex, AgentDataPool pool, SimulationContext ctx, int cooldownMs = 5000)
    {
        int jobId = pool.CurrentJobId[agentIndex];
        if (jobId != -1 && jobId != AgentReservedMarker)
        {
            // §119 P2: Worker'у «упёрся в стену» кулдаун недостижимости НЕ ставим —
            // работа возвращается в очередь его зоны (ReleaseJobWorker ниже),
            // где её подберёт следующий Worker этой зоны. Laborer — как раньше.
            if (pool.Classes[agentIndex] != AgentClass.Worker)
                JobIndex.MarkJobUnreachable(jobId, cooldownMs);
        }
        ReleaseJobWorker(agentIndex, pool, ctx);
    }

    /// <summary>
    /// §119 P2: клейм РАБОТЫ ЗОНЫ для Worker'а (роутер уже выдал ZoneJob).
    /// Фиксирует клейм в реестре по id и инициализирует состояние агента
    /// хендлером (OnStart). Вызывается ТОЛЬКО из WorkerZoneJobSystem; валидацию
    /// работы вызывающий уже провёл — здесь чистая фиксация.
    /// </summary>
    public bool TryAssignWorkerToZoneJob(int agentIndex, int jobId, AgentDataPool pool, SimulationContext ctx)
    {
        // Атомарный резерв агента (тот же маркер, что в DispatchPendingJobs).
        if (Interlocked.CompareExchange(ref pool.CurrentJobId[agentIndex], AgentReservedMarker, -1) != -1)
            return false;
        try
        {
            if (!JobIndex.TryClaimJobById(jobId, out var activeJob))
            {
                pool.CurrentJobId[agentIndex] = -1;
                return false;
            }

            IdleWorkers.RemoveIdleWorker(agentIndex, pool);
            pool.CurrentJobId[agentIndex] = activeJob.Id;
            pool.CurrentJobType[agentIndex] = activeJob.TypeId;

            if (JobRegistry.TryGetHandler(activeJob.TypeId, out var handler))
            {
                try
                {
                    handler.OnStart(agentIndex, activeJob, pool, ctx);
                    pool.LastJobCategory[agentIndex] = (int)JobPriorityManager.Instance.GetCategory(activeJob.TypeId);
                    if (pool.States[agentIndex] == AgentState.Idle)
                    {
                        // OnStart отказал: откат по легаси-сценарию диспатчера.
                        JobIndex.ReleaseWorkerClaim(activeJob.Id);
                        ZoneJobRouter.Instance.Unassign(activeJob.Id);
                        pool.CurrentJobId[agentIndex] = -1;
                        pool.CurrentJobType[agentIndex] = JobTypeId.None;
                        IdleWorkers.AddIdleWorker(agentIndex, pool);
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[JobDispatcher] Worker OnStart ошибка: {ex.Message}");
                    JobIndex.ReleaseWorkerClaim(activeJob.Id);
                    ZoneJobRouter.Instance.Unassign(activeJob.Id);
                    pool.CurrentJobId[agentIndex] = -1;
                    pool.CurrentJobType[agentIndex] = JobTypeId.None;
                    pool.States[agentIndex] = AgentState.Idle;
                    IdleWorkers.AddIdleWorker(agentIndex, pool);
                    return false;
                }
            }

            JobIndex.ReleaseWorkerClaim(activeJob.Id);
            ZoneJobRouter.Instance.Unassign(activeJob.Id);
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
            IdleWorkers.AddIdleWorker(agentIndex, pool);
            return false;
        }
        catch
        {
            pool.CurrentJobId[agentIndex] = -1;
            throw;
        }
    }
}
