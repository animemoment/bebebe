using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

public sealed class JobDispatcher
{
    public static JobDispatcher Instance { get; } = new();

    public GenericJobSpatialIndex JobIndex { get; } = new();
    public IdleWorkerSpatialGrid IdleWorkers { get; } = new();

    private const int MaxAssignmentsPerTick = 30;
    private readonly int[] _idleBuffer = new int[MaxAssignmentsPerTick];

    public void DispatchPendingJobs(AgentDataPool pool, SimulationContext ctx)
    {
        using (GameProfiler.Scope())
        {
            if (JobIndex.UnclaimedCount <= 0 || IdleWorkers.TotalIdleCount <= 0)
                return;

            // Выбираем пачку свободных рабочих (до 30 за тик)
            int idleCount = IdleWorkers.CollectIdleWorkers(MaxAssignmentsPerTick, _idleBuffer, pool);
            if (idleCount == 0) return;

            for (int i = 0; i < idleCount; i++)
            {
                int agentIndex = _idleBuffer[i];
                if (pool.States[agentIndex] != AgentState.Idle)
                    continue;

                int workerTx = pool.CurrentCellX[agentIndex];
                int workerTy = pool.CurrentCellY[agentIndex];

                // Рабочий атомарно ищет и забирает ближайшую задачу вокруг себя
                if (JobIndex.TryClaimNearestJobForWorker(
                    workerTx, workerTy,
                    pool.EquippedTools[agentIndex],
                    pool, agentIndex, ctx,
                    out var activeJob))
                {
                    IdleWorkers.RemoveIdleWorker(agentIndex, pool);
                    pool.CurrentJobId[agentIndex] = activeJob.Id;
                    pool.CurrentJobType[agentIndex] = activeJob.TypeId;

                    if (JobRegistry.TryGetHandler(activeJob.TypeId, out var handler))
                    {
                        handler.OnStart(agentIndex, activeJob, pool, ctx);
                    }
                }
            }
        }
    }

    public int RegisterJob(JobData job) => JobIndex.RegisterJob(job);
    public void RegisterBatch(List<JobData> jobs) => JobIndex.RegisterBatch(jobs);
    public void UnregisterJob(int jobId) => JobIndex.RemoveJob(jobId, out _);
    public void UnregisterJobByPos(int x, int y, JobTypeId type) => JobIndex.RemoveJobByPos(x, y, type, out _);
    public void UnregisterBatchByPositions(List<(int X, int Y)> positions, JobTypeId type) => JobIndex.RemoveBatchByPositions(positions, type);

    public void ReleaseJobWorker(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        using (GameProfiler.Scope())
        {
            int jobId = pool.CurrentJobId[agentIndex];
            if (jobId != -1)
            {
                if (JobRegistry.TryGetHandler(pool.CurrentJobType[agentIndex], out var handler))
                {
                    handler.OnCancel(agentIndex, pool, ctx);
                }

                JobIndex.ReleaseWorkerClaim(jobId);
                pool.CurrentJobId[agentIndex] = -1;
                pool.CurrentJobType[agentIndex] = JobTypeId.None;
            }

            pool.States[agentIndex] = AgentState.Idle;
            pool.JobSearchTimer[agentIndex] = 4.0f + (float)ctx.Random.NextDouble() * 4.0f;
            IdleWorkers.AddIdleWorker(agentIndex, pool);
        }
    }
}