using System;
using System.Threading;
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

    public int TotalIdleCount => Volatile.Read(ref _totalIdleCount);

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

    /// <summary>
    /// Lock-free сбор свободных рабочих из указанного чанка.
    /// Читает односвязный список без lock'а — консистентность для симуляции достаточна.
    /// </summary>
    public int CollectIdleWorkersInChunk(int chunkIndex, int maxCount, int[] destination, AgentDataPool pool)
    {
        if (chunkIndex < 0 || chunkIndex >= _chunkHeads.Length || maxCount <= 0)
            return 0;

        int collected = 0;
        int curr = Volatile.Read(ref _chunkHeads[chunkIndex]);

        while (curr != -1 && collected < maxCount)
        {
            if (curr < pool.Capacity && pool.States[curr] == AgentState.Idle)
            {
                destination[collected++] = curr;
            }
            curr = Volatile.Read(ref pool.NextInIdleCell[curr]);
        }

        return collected;
    }

    /// <summary>
    /// Lock-free сбор свободных рабочих round-robin по чанкам (без глобального lock'а).
    /// </summary>
    public int CollectIdleWorkers(int maxCount, int[] destination, AgentDataPool pool)
    {
        if (destination == null || destination.Length < maxCount)
            return 0;

        if (Volatile.Read(ref _totalIdleCount) <= 0)
            return 0;

        int collected = 0;
        int totalChunks = _chunkHeads.Length;

        for (int offset = 0; offset < totalChunks && collected < maxCount; offset++)
        {
            int chunkIdx = (_lastChunkScanIndex + offset) % totalChunks;
            int curr = Volatile.Read(ref _chunkHeads[chunkIdx]);

            while (curr != -1 && collected < maxCount)
            {
                if (curr < pool.Capacity && pool.States[curr] == AgentState.Idle)
                {
                    destination[collected++] = curr;
                }
                curr = Volatile.Read(ref pool.NextInIdleCell[curr]);
            }
        }

        _lastChunkScanIndex = (_lastChunkScanIndex + 17) % totalChunks;
        return collected;
    }
}