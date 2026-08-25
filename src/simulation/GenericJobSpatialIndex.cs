using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Пространственный индекс задач с поддержкой прямого поиска ближайших задач для рабочих.
/// </summary>
public sealed class GenericJobSpatialIndex
{
    private const int ChunkShift = 4;
    private const int ChunkDim = 32;
    private const int PriorityCount = 7;

    private readonly object _lock = new();
    private readonly Dictionary<int, JobData> _jobs = new(4096);
    private readonly Dictionary<(int X, int Y, JobTypeId Type), int> _jobPosMap = new(4096);
    private readonly HashSet<int>[] _jobsByPriority = new HashSet<int>[PriorityCount];
    private readonly HashSet<int>[,] _unclaimedByChunk = new HashSet<int>[ChunkDim, ChunkDim];

    private int _nextJobId = 1;
    private int _unclaimedCount = 0;

    public int UnclaimedCount { get { lock (_lock) return _unclaimedCount; } }
    public int TotalCount { get { lock (_lock) return _jobs.Count; } }

    public GenericJobSpatialIndex()
    {
        for (int p = 0; p < PriorityCount; p++)
        {
            _jobsByPriority[p] = new HashSet<int>();
        }

        for (int cx = 0; cx < ChunkDim; cx++)
        {
            for (int cy = 0; cy < ChunkDim; cy++)
            {
                _unclaimedByChunk[cx, cy] = new HashSet<int>();
            }
        }
    }

    private static (int CX, int CY) GetChunkCoord(int tileX, int tileY)
    {
        int cx = Math.Clamp(tileX >> ChunkShift, 0, ChunkDim - 1);
        int cy = Math.Clamp(tileY >> ChunkShift, 0, ChunkDim - 1);
        return (cx, cy);
    }

    public int RegisterJob(JobData job)
    {
        lock (_lock)
        {
            var key = (job.TargetX, job.TargetY, job.TypeId);
            if (_jobPosMap.TryGetValue(key, out int existingId))
                return existingId;

            int id = _nextJobId++;
            job.Id = id;
            job.IsActive = true;

            _jobs[id] = job;
            _jobPosMap[key] = id;

            if (job.IsAvailable)
            {
                int p = (int)job.PriorityTier;
                _jobsByPriority[p].Add(id);
                var (cx, cy) = GetChunkCoord(job.TargetX, job.TargetY);
                _unclaimedByChunk[cx, cy].Add(id);
                _unclaimedCount++;
            }
            return id;
        }
    }

    public void RegisterBatch(List<JobData> jobs)
    {
        if (jobs == null || jobs.Count == 0) return;

        lock (_lock)
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var key = (job.TargetX, job.TargetY, job.TypeId);
                if (_jobPosMap.ContainsKey(key))
                    continue;

                int id = _nextJobId++;
                job.Id = id;
                job.IsActive = true;

                _jobs[id] = job;
                _jobPosMap[key] = id;

                if (job.IsAvailable)
                {
                    int p = (int)job.PriorityTier;
                    _jobsByPriority[p].Add(id);
                    var (cx, cy) = GetChunkCoord(job.TargetX, job.TargetY);
                    _unclaimedByChunk[cx, cy].Add(id);
                    _unclaimedCount++;
                }
            }
        }
    }

    public bool TryGetJob(int id, out JobData job)
    {
        lock (_lock)
        {
            return _jobs.TryGetValue(id, out job);
        }
    }

    public bool TryGetJobByPos(int x, int y, JobTypeId type, out JobData job)
    {
        lock (_lock)
        {
            if (_jobPosMap.TryGetValue((x, y, type), out int id))
            {
                return _jobs.TryGetValue(id, out job);
            }
            job = default;
            return false;
        }
    }

    public bool TryAddJobProgress(int x, int y, JobTypeId type, int countToAdd, out bool isCompleted, out JobData jobSnapshot)
    {
        lock (_lock)
        {
            if (_jobPosMap.TryGetValue((x, y, type), out int id) && _jobs.TryGetValue(id, out var job))
            {
                job.CurrentDeliveredCount += countToAdd;
                _jobs[id] = job;
                isCompleted = job.CurrentDeliveredCount >= job.TargetItemCount;
                jobSnapshot = job;
                return true;
            }

            isCompleted = false;
            jobSnapshot = default;
            return false;
        }
    }

    public bool TryClaimNearestJobForWorker(
        int workerTileX, int workerTileY,
        ToolRequirement workerTools,
        AgentDataPool pool, int agentIndex,
        SimulationContext ctx,
        out JobData claimedJob)
    {
        lock (_lock)
        {
            claimedJob = default;
            if (_unclaimedCount <= 0) return false;

            int centerCx = Math.Clamp(workerTileX >> ChunkShift, 0, ChunkDim - 1);
            int centerCy = Math.Clamp(workerTileY >> ChunkShift, 0, ChunkDim - 1);

            bool hasStockpileSpace = StockpileManager.Instance.HasFreeSpace;
            bool hasAvailableLogs = GroundItemManager.Instance.HasAvailableLogs;

            int bestJobId = -1;
            int bestPriority = -1;
            float bestDistSq = float.MaxValue;

            for (int r = 0; r < ChunkDim; r++)
            {
                int minCx = Math.Max(0, centerCx - r);
                int maxCx = Math.Min(ChunkDim - 1, centerCx + r);
                int minCy = Math.Max(0, centerCy - r);
                int maxCy = Math.Min(ChunkDim - 1, centerCy + r);

                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    for (int cy = minCy; cy <= maxCy; cy++)
                    {
                        if (r > 0 && cx > minCx && cx < maxCx && cy > minCy && cy < maxCy)
                            continue;

                        var chunk = _unclaimedByChunk[cx, cy];
                        if (chunk.Count == 0) continue;

                        foreach (int jobId in chunk)
                        {
                            if (!_jobs.TryGetValue(jobId, out var job) || !job.IsAvailable)
                                continue;

                            if (job.RequiredTool != ToolRequirement.None && (workerTools & job.RequiredTool) == 0)
                                continue;

                            if (job.TypeId == JobTypeId.StockpileHauling && !hasStockpileSpace)
                                continue;

                            if (job.TypeId == JobTypeId.BlueprintDelivery && !hasAvailableLogs)
                                continue;

                            int priority = JobPriorityManager.Instance.GetPriorityForJobType(job.TypeId);
                            if (priority <= 0)
                                continue;

                            if (!JobRegistry.TryGetHandler(job.TypeId, out var handler) || !handler.CanAgentExecute(agentIndex, job, pool, ctx))
                                continue;

                            float dx = job.StandX - workerTileX;
                            float dy = job.StandY - workerTileY;
                            float distSq = dx * dx + dy * dy;

                            if (priority > bestPriority || (priority == bestPriority && distSq < bestDistSq))
                            {
                                bestPriority = priority;
                                bestDistSq = distSq;
                                bestJobId = jobId;
                            }
                        }
                    }
                }

                // Если в текущем кольце найдена подходящая задача — мгновенный выход
                if (bestJobId != -1)
                    break;
            }

            if (bestJobId != -1 && _jobs.TryGetValue(bestJobId, out var selectedJob))
            {
                selectedJob.AssignedWorkers++;
                _jobs[bestJobId] = selectedJob;

                if (!selectedJob.IsAvailable)
                {
                    int p = (int)selectedJob.PriorityTier;
                    _jobsByPriority[p].Remove(bestJobId);
                    var (cx, cy) = GetChunkCoord(selectedJob.TargetX, selectedJob.TargetY);
                    _unclaimedByChunk[cx, cy].Remove(bestJobId);
                    _unclaimedCount = Math.Max(0, _unclaimedCount - 1);
                }

                claimedJob = selectedJob;
                return true;
            }

            return false;
        }
    }

    public void ReleaseWorkerClaim(int jobId)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                bool wasFull = !job.IsAvailable;
                job.AssignedWorkers = Math.Max(0, job.AssignedWorkers - 1);
                _jobs[jobId] = job;

                if (wasFull && job.IsAvailable)
                {
                    int p = (int)job.PriorityTier;
                    _jobsByPriority[p].Add(jobId);
                    var (cx, cy) = GetChunkCoord(job.TargetX, job.TargetY);
                    _unclaimedByChunk[cx, cy].Add(jobId);
                    _unclaimedCount++;
                }
            }
        }
    }

    public bool RemoveJob(int id, out JobData removedJob)
    {
        lock (_lock)
        {
            if (_jobs.Remove(id, out removedJob))
            {
                _jobPosMap.Remove((removedJob.TargetX, removedJob.TargetY, removedJob.TypeId));
                int p = (int)removedJob.PriorityTier;
                if (_jobsByPriority[p].Remove(id))
                {
                    var (cx, cy) = GetChunkCoord(removedJob.TargetX, removedJob.TargetY);
                    _unclaimedByChunk[cx, cy].Remove(id);
                    _unclaimedCount = Math.Max(0, _unclaimedCount - 1);
                }
                return true;
            }
            removedJob = default;
            return false;
        }
    }

    public bool RemoveJobByPos(int x, int y, JobTypeId type, out JobData removedJob)
    {
        lock (_lock)
        {
            if (_jobPosMap.TryGetValue((x, y, type), out int id))
            {
                return RemoveJob(id, out removedJob);
            }
            removedJob = default;
            return false;
        }
    }

    public void RemoveBatchByPositions(List<(int X, int Y)> positions, JobTypeId type)
    {
        if (positions == null || positions.Count == 0) return;
        lock (_lock)
        {
            for (int i = 0; i < positions.Count; i++)
            {
                var (x, y) = positions[i];
                if (_jobPosMap.TryGetValue((x, y, type), out int id))
                {
                    if (_jobs.Remove(id, out var removedJob))
                    {
                        _jobPosMap.Remove((x, y, type));
                        int p = (int)removedJob.PriorityTier;
                        if (_jobsByPriority[p].Remove(id))
                        {
                            var (cx, cy) = GetChunkCoord(x, y);
                            _unclaimedByChunk[cx, cy].Remove(id);
                            _unclaimedCount = Math.Max(0, _unclaimedCount - 1);
                        }
                    }
                }
            }
        }
    }

    public void FillPrioritizedUnclaimed(List<int> destination)
    {
        lock (_lock)
        {
            destination.Clear();
            for (int p = 0; p < PriorityCount; p++)
            {
                foreach (var id in _jobsByPriority[p])
                {
                    destination.Add(id);
                }
            }
        }
    }
}