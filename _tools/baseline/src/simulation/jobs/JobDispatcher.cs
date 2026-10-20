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

    private const int AgentReservedMarker = -2;

    // Пакетный буфер свободных агентов на один проход диспетчеризации
    private const int MaxBatchWorkers = 4096;
    private readonly int[] _workerBatchBuffer = new int[MaxBatchWorkers];

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

            int assigned = 0;
            for (int i = 0; i < collected; i++)
            {
                // Проверка бюджета времени каждые 64 назначения
                if ((i & 63) == 0 && i > 0)
                {
                    double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * ticksToMs;
                    if (elapsedMs >= MaxDispatchBudgetMs)
                        break;
                }

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

            JobIndex.ReleaseWorkerClaim(jobId);
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
        }

        pool.WaypointCount[agentIndex] = 0;
        pool.WaypointIndex[agentIndex] = 0;
        pool.States[agentIndex] = AgentState.Idle;
        pool.JobSearchTimer[agentIndex] = 4.0f + (float)ParallelRng.NextDouble() * 4.0f;
        IdleWorkers.AddIdleWorker(agentIndex, pool);
    }

    public void ReleaseJobWorkerForStuck(int agentIndex, AgentDataPool pool, SimulationContext ctx, int cooldownMs = 5000)
    {
        int jobId = pool.CurrentJobId[agentIndex];
        if (jobId != -1 && jobId != AgentReservedMarker)
        {
            JobIndex.MarkJobUnreachable(jobId, cooldownMs);
        }
        ReleaseJobWorker(agentIndex, pool, ctx);
    }
}
