using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Параллельный диспетчер задач на chunk-based batch assignment.
/// Без глобальных lock'ов — использует CAS на AssignedWorkers.
/// </summary>
public sealed class JobDispatcher
{
    public static JobDispatcher Instance { get; } = new();

    public GenericJobSpatialIndex JobIndex { get; } = new();
    public IdleWorkerSpatialGrid IdleWorkers { get; } = new();

    private const int ChunkDim = 32;
    private const int ChunkCount = ChunkDim * ChunkDim;
    private const int WorkersPerChunkBudget = 16;
    private const int ChunksPerBatch = 128;
    private const int MaxBatchScale = 4;

    // Буферы для per-chunk сбора (переиспользуемые)
    // Marker used to atomically reserve an idle worker (CurrentJobId = -2)
    // before a job claim in the parallel dispatcher.
    private const int AgentReservedMarker = -2;

    // Per-thread buffers for collecting idle workers. A shared buffer would
    // cause a data race inside Parallel.ForEach.
    private readonly ThreadLocal<int[]> _workerBuffer = new(() => new int[WorkersPerChunkBudget]);

    // Round-robin счётчик для равномерной обработки чанков
    private int _chunkScanIndex;

    /// <summary>
    /// Диспетчеризация задач. <paramref name="chunkBatchScale"/> увеличивает
    /// объём скана чанков за вызов на высоких скоростях симуляции (там частота
    /// вызовов урезана масштабированием интервала), чтобы назначение работы не
    /// задерживалось. Потолок — <see cref="MaxBatchScale"/>.
    /// </summary>
    public void DispatchPendingJobs(AgentDataPool pool, SimulationContext ctx, int chunkBatchScale = 1)
    {
        using (GameProfiler.Scope())
        {
            if (JobIndex.UnclaimedCount <= 0 || IdleWorkers.TotalIdleCount <= 0)
                return;

            int totalChunks = ChunkCount;
            int batchScale = Math.Clamp(chunkBatchScale, 1, MaxBatchScale);
            int chunksToProcess = Math.Min(ChunksPerBatch * batchScale, totalChunks);

            var partitioner = Partitioner.Create(0, chunksToProcess,
                Math.Max(1, chunksToProcess / System.Environment.ProcessorCount));

            int totalAssigned = 0;

            Parallel.ForEach(partitioner,
                new ParallelOptions { MaxDegreeOfParallelism = System.Environment.ProcessorCount },
                range =>
                {
                    for (int offset = range.Item1; offset < range.Item2; offset++)
                    {
                        int chunkIndex = Interlocked.Increment(ref _chunkScanIndex) % totalChunks;
                        int assigned = DispatchChunk(chunkIndex, pool, ctx);
                        if (assigned > 0)
                            Interlocked.Add(ref totalAssigned, assigned);
                    }
                });

            // Spill-over pass для оставшихся без работы
            if (totalAssigned > 0 && IdleWorkers.TotalIdleCount > 0 && JobIndex.UnclaimedCount > 0)
            {
                SpillOverPass(pool, ctx);
            }
        }
    }

    /// <summary>
    /// Processes one chunk: collects idle workers and assigns them jobs.
    /// </summary>
    private int DispatchChunk(int chunkIndex, AgentDataPool pool, SimulationContext ctx)
    {
        int workerCount = IdleWorkers.CollectIdleWorkersInChunk(
            chunkIndex, WorkersPerChunkBudget, _workerBuffer.Value, pool);
        if (workerCount == 0) return 0;

        if (JobIndex.GetChunkJobCount(chunkIndex) == 0)
            return 0;

        int assigned = 0;

        for (int wi = 0; wi < workerCount; wi++)
        {
            int agentIndex = _workerBuffer.Value[wi];
            if (agentIndex < 0 || agentIndex >= pool.Capacity)
                continue;
            if (pool.States[agentIndex] != AgentState.Idle)
                continue;

            // Atomically capture the idle worker: only one dispatch thread can
            // move CurrentJobId from -1 to the reserved marker.
            if (Interlocked.CompareExchange(ref pool.CurrentJobId[agentIndex], AgentReservedMarker, -1) != -1)
                continue;

            try
            {
                int workerTx = pool.CurrentCellX[agentIndex];
                int workerTy = pool.CurrentCellY[agentIndex];

                if (JobIndex.TryClaimForWorkerInChunk(
                    chunkIndex, workerTx, workerTy,
                    pool.EquippedTools[agentIndex],
                    pool, agentIndex, ctx,
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
                            assigned++;
                        }
                        catch (Exception ex)
                        {
                            GD.PrintErr($"[JobDispatcher] OnStart error {activeJob.TypeId} (agent #{agentIndex}): {ex.Message}\n{ex.StackTrace}");
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
                    // No job claimed - release the reserved worker back to the idle pool.
                    pool.CurrentJobId[agentIndex] = -1;
                }
            }
            catch
            {
                // Never leave an agent stuck in the reserved state.
                pool.CurrentJobId[agentIndex] = -1;
                throw;
            }
        }

        return assigned;
    }

    /// <summary>
    /// Spill-over pass: для оставшихся без работы агентов ищем задачи в соседних чанках.
    /// </summary>
    private void SpillOverPass(AgentDataPool pool, SimulationContext ctx)
    {
        int remaining = IdleWorkers.CollectIdleWorkers(WorkersPerChunkBudget, _workerBuffer.Value, pool);
        if (remaining == 0) return;

        for (int wi = 0; wi < remaining; wi++)
        {
            int agentIndex = _workerBuffer.Value[wi];
            if (agentIndex < 0 || agentIndex >= pool.Capacity || pool.States[agentIndex] != AgentState.Idle)
                continue;

            int workerTx = pool.CurrentCellX[agentIndex];
            int workerTy = pool.CurrentCellY[agentIndex];
            int centerChunk = GenericJobSpatialIndex.GetChunkIndexStatic(workerTx, workerTy);

            bool found = false;
            int centerCx = centerChunk % ChunkDim;
            int centerCy = centerChunk / ChunkDim;

            for (int r = 0; r <= 1 && !found; r++)
            {
                int minCx = Math.Max(0, centerCx - r);
                int maxCx = Math.Min(ChunkDim - 1, centerCx + r);
                int minCy = Math.Max(0, centerCy - r);
                int maxCy = Math.Min(ChunkDim - 1, centerCy + r);

                for (int cx = minCx; cx <= maxCx && !found; cx++)
                {
                    for (int cy = minCy; cy <= maxCy && !found; cy++)
                    {
                        if (r > 0 && cx > minCx && cx < maxCx && cy > minCy && cy < maxCy)
                            continue;

                        int chunkIdx = cy * ChunkDim + cx;
                        if (JobIndex.GetChunkJobCount(chunkIdx) == 0)
                            continue;

                        if (JobIndex.TryClaimForWorkerInChunk(
                            chunkIdx, workerTx, workerTy,
                            pool.EquippedTools[agentIndex],
                            pool, agentIndex, ctx,
                            out var spillJob))
                        {
                            IdleWorkers.RemoveIdleWorker(agentIndex, pool);
                            pool.CurrentJobId[agentIndex] = spillJob.Id;
                            pool.CurrentJobType[agentIndex] = spillJob.TypeId;

                            if (JobRegistry.TryGetHandler(spillJob.TypeId, out var handler))
                            {
                                try
                                {
                                    handler.OnStart(agentIndex, spillJob, pool, ctx);
                                    found = true;
                                }
                                catch (Exception ex)
                                {
                                    GD.PrintErr($"[JobDispatcher] Spill-over ошибка OnStart: {ex.Message}");
                                    JobIndex.ReleaseWorkerClaim(spillJob.Id);
                                    pool.CurrentJobId[agentIndex] = -1;
                                    pool.CurrentJobType[agentIndex] = JobTypeId.None;
                                    pool.States[agentIndex] = AgentState.Idle;
                                    IdleWorkers.AddIdleWorker(agentIndex, pool);
                                }
                            }
                            else
                            {
                                JobIndex.ReleaseWorkerClaim(spillJob.Id);
                                pool.CurrentJobId[agentIndex] = -1;
                                pool.CurrentJobType[agentIndex] = JobTypeId.None;
                                IdleWorkers.AddIdleWorker(agentIndex, pool);
                            }
                        }
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
                    try
                    {
                        handler.OnCancel(agentIndex, pool, ctx);
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[JobDispatcher] Ошибка OnCancel для агента #{agentIndex}: {ex.Message}");
                    }
                }

                JobIndex.ReleaseWorkerClaim(jobId);
                pool.CurrentJobId[agentIndex] = -1;
                pool.CurrentJobType[agentIndex] = JobTypeId.None;
            }

            pool.States[agentIndex] = AgentState.Idle;
            pool.JobSearchTimer[agentIndex] = 4.0f + (float)Random.Shared.NextDouble() * 4.0f;
            IdleWorkers.AddIdleWorker(agentIndex, pool);
        }
    }
}
