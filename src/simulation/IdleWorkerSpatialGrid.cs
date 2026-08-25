using System;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Безопасный и сверхбыстрый O(1) пространственный индекс свободных агентов (Idle).
/// </summary>
public sealed class IdleWorkerSpatialGrid
{
    private const int ChunkShift = 4; // 16x16 тайлов на чанк
    private const int ChunkDim = 32;   // 32x32 чанка
    private const int MaxPerChunkInspect = 32;

    private readonly int[] _chunkHeads = new int[ChunkDim * ChunkDim];
    private bool[] _inGrid;
    private int[] _agentChunk;
    private readonly object _lock = new();
    private int _totalIdleCount = 0;
    private int _lastChunkScanIndex = 0;

    public int TotalIdleCount { get { lock (_lock) return _totalIdleCount; } }

    public IdleWorkerSpatialGrid()
    {
        Array.Fill(_chunkHeads, -1);
    }

    private void EnsureCapacity(int capacity)
    {
        if (_inGrid == null || _inGrid.Length < capacity)
        {
            Array.Resize(ref _inGrid, capacity);
            int oldLen = _agentChunk != null ? _agentChunk.Length : 0;
            Array.Resize(ref _agentChunk, capacity);
            for (int i = oldLen; i < capacity; i++)
            {
                _agentChunk[i] = -1;
            }
        }
    }

    public void AddIdleWorker(int agentIndex, AgentDataPool pool)
    {
        lock (_lock)
        {
            EnsureCapacity(pool.Capacity);
            if (_inGrid[agentIndex])
                return;

            int cx = Math.Clamp(pool.CurrentCellX[agentIndex] >> ChunkShift, 0, ChunkDim - 1);
            int cy = Math.Clamp(pool.CurrentCellY[agentIndex] >> ChunkShift, 0, ChunkDim - 1);
            int chunkIndex = cy * ChunkDim + cx;

            _inGrid[agentIndex] = true;
            _agentChunk[agentIndex] = chunkIndex;

            int oldHead = _chunkHeads[chunkIndex];
            pool.NextInIdleCell[agentIndex] = oldHead;
            pool.PrevInIdleCell[agentIndex] = -1;

            if (oldHead != -1)
            {
                pool.PrevInIdleCell[oldHead] = agentIndex;
            }

            _chunkHeads[chunkIndex] = agentIndex;
            _totalIdleCount++;
        }
    }

    public void RemoveIdleWorker(int agentIndex, AgentDataPool pool)
    {
        lock (_lock)
        {
            EnsureCapacity(pool.Capacity);
            if (!_inGrid[agentIndex])
                return;

            int chunkIndex = _agentChunk[agentIndex];
            _inGrid[agentIndex] = false;
            _agentChunk[agentIndex] = -1;

            int next = pool.NextInIdleCell[agentIndex];
            int prev = pool.PrevInIdleCell[agentIndex];

            if (prev != -1)
            {
                pool.NextInIdleCell[prev] = next;
            }
            else
            {
                if (chunkIndex >= 0 && chunkIndex < _chunkHeads.Length && _chunkHeads[chunkIndex] == agentIndex)
                {
                    _chunkHeads[chunkIndex] = next;
                }
            }

            if (next != -1)
            {
                pool.PrevInIdleCell[next] = prev;
            }

            pool.NextInIdleCell[agentIndex] = -1;
            pool.PrevInIdleCell[agentIndex] = -1;
            _totalIdleCount = Math.Max(0, _totalIdleCount - 1);
        }
    }

    public int CollectIdleWorkers(int maxCount, int[] destination, AgentDataPool pool)
    {
        if (destination == null || destination.Length < maxCount)
            return 0;

        lock (_lock)
        {
            if (_totalIdleCount <= 0)
                return 0;

            int collected = 0;
            int totalChunks = _chunkHeads.Length;

            // Round-robin проход по всем чанкам карты без застревания на 0-м чанке
            for (int offset = 0; offset < totalChunks && collected < maxCount; offset++)
            {
                int chunkIdx = (_lastChunkScanIndex + offset) % totalChunks;
                int curr = _chunkHeads[chunkIdx];

                while (curr != -1 && collected < maxCount)
                {
                    destination[collected++] = curr;
                    curr = pool.NextInIdleCell[curr];
                }
            }

            _lastChunkScanIndex = (_lastChunkScanIndex + 17) % totalChunks;
            return collected;
        }
    }

    public bool TryClaimNearestIdleWorker(
        int targetTileX, int targetTileY,
        ToolRequirement requiredTool,
        AgentDataPool pool,
        out int claimedAgentIndex)
    {
        claimedAgentIndex = -1;
        if (_totalIdleCount <= 0) return false;

        lock (_lock)
        {
            if (_totalIdleCount <= 0) return false;

            int centerCx = Math.Clamp(targetTileX >> ChunkShift, 0, ChunkDim - 1);
            int centerCy = Math.Clamp(targetTileY >> ChunkShift, 0, ChunkDim - 1);

            float targetPxX = targetTileX * 64f + 32f;
            float targetPxY = targetTileY * 64f + 32f;

            float bestDistSq = float.MaxValue;
            int bestAgent = -1;

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

                        int curr = _chunkHeads[cy * ChunkDim + cx];
                        int inspectCount = 0;

                        while (curr != -1 && inspectCount++ < MaxPerChunkInspect)
                        {
                            if (pool.States[curr] == AgentState.Idle &&
                                (requiredTool == ToolRequirement.None || (pool.EquippedTools[curr] & requiredTool) != 0))
                            {
                                float dx = pool.PositionX[curr] - targetPxX;
                                float dy = pool.PositionY[curr] - targetPxY;
                                float distSq = dx * dx + dy * dy;

                                if (distSq < bestDistSq)
                                {
                                    bestDistSq = distSq;
                                    bestAgent = curr;
                                }
                            }
                            curr = pool.NextInIdleCell[curr];
                        }
                    }
                }

                if (bestAgent != -1)
                    break;
            }

            if (bestAgent != -1)
            {
                RemoveIdleWorker(bestAgent, pool);
                claimedAgentIndex = bestAgent;
                return true;
            }

            return false;
        }
    }
}