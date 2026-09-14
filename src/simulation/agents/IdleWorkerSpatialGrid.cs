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
    // P-баланс: striped locks вместо одного глобального _lock. Bookkeep-фаза
    // (16 потоков) дёргает UpdateWorkerChunk на каждую смену клетки, Dispatcher —
    // Add/Remove: один lock сериализовал их всех, случайный владелец lock'а
    // стагглерил чужой батч. 32 шарда по младшим битам чанка.
    private const int StripeCount = 32;
    private readonly object[] _stripes = CreateStripes();
    private static object[] CreateStripes()
    {
        var s = new object[StripeCount];
        for (int i = 0; i < s.Length; i++) s[i] = new object();
        return s;
    }
    private static int StripeOf(int chunkIndex) => (chunkIndex & (StripeCount - 1));
    // Ресайз массивов — под отдельным lock (редко, только рост пула).
    private readonly object _resizeLock = new();
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

    private static int ComputeChunkIndex(int cellX, int cellY)
    {
        int cx = Math.Clamp(cellX >> ChunkShift, 0, ChunkDim - 1);
        int cy = Math.Clamp(cellY >> ChunkShift, 0, ChunkDim - 1);
        return cy * ChunkDim + cx;
    }

    public void AddIdleWorker(int agentIndex, AgentDataPool pool)
    {
        EnsureCapacityLocked(pool.Capacity);
        if (Volatile.Read(ref _inGrid[agentIndex]))
            return;

        int cx = Math.Clamp(pool.CurrentCellX[agentIndex] >> ChunkShift, 0, ChunkDim - 1);
        int cy = Math.Clamp(pool.CurrentCellY[agentIndex] >> ChunkShift, 0, ChunkDim - 1);
        int chunkIndex = cy * ChunkDim + cx;

        // Тот же фикс-шард, что в Remove: пара Add/Remove одного агента
        // всегда под одним lock (см. комментарий в RemoveIdleWorker).
        lock (_stripes[agentIndex & (StripeCount - 1)])
        {
            if (_inGrid[agentIndex])
                return;
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
            Interlocked.Increment(ref _totalIdleCount);
        }
    }

    public void RemoveIdleWorker(int agentIndex, AgentDataPool pool)
    {
        EnsureCapacityLocked(pool.Capacity);
        if (agentIndex < 0 || agentIndex >= _inGrid.Length)
            return;
        // Фиксированный шард по индексу агента: Add/Remove одного агента всегда
        // сериализованы между собой. Шардирование Remove по _agentChunk опасно:
        // конкурентный Move меняет чанк между чтением и lock → два потока
        // правят один linked-узел под разными шардами (use-after-unlink).
        lock (_stripes[agentIndex & (StripeCount - 1)])
        {
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
            Interlocked.Decrement(ref _totalIdleCount);
        }
    }

    private void EnsureCapacityLocked(int capacity)
    {
        if (_inGrid != null && _inGrid.Length >= capacity)
            return;
        lock (_resizeLock)
        {
            EnsureCapacity(capacity);
        }
    }

    /// <summary>
    /// Пересаживает уже зарегистрированного в сетке бездельника в новый чанк,
    /// если он переместился между клетками с момента регистрации.
    /// Вызывается из фазы bookkeeping в фоновом потоке симуляции.
    /// </summary>
    public void UpdateWorkerChunk(int agentIndex, AgentDataPool pool)
    {
        if (agentIndex < 0) return;
        if (_inGrid == null || agentIndex >= _inGrid.Length || !_inGrid[agentIndex])
            return;

        int newChunk = ComputeChunkIndex(pool.CurrentCellX[agentIndex], pool.CurrentCellY[agentIndex]);
        int curChunk = _agentChunk[agentIndex];
        if (newChunk == curChunk)
            return;

        // Тот же фикс-шард агента, что в Add/Remove: переезд сериализован
        // с Add/Remove этого же агента, чужие агенты идут параллельно.
        // MoveLocked перепроверяет _agentChunk под lock (см. ниже).
        lock (_stripes[agentIndex & (StripeCount - 1)])
        {
            MoveLocked(agentIndex, pool, curChunk, newChunk);
        }
    }

    private void MoveLocked(int agentIndex, AgentDataPool pool, int curChunk, int newChunk)
    {
        // Перепроверка под lock: агент могли снять/переместить конкурентом.
        if (!_inGrid[agentIndex] || _agentChunk[agentIndex] != curChunk)
            return;
        // Снять со старого чанка
        int next = pool.NextInIdleCell[agentIndex];
        int prev = pool.PrevInIdleCell[agentIndex];
        if (prev != -1)
        {
            pool.NextInIdleCell[prev] = next;
        }
        else if (_chunkHeads[curChunk] == agentIndex)
        {
            _chunkHeads[curChunk] = next;
        }
        if (next != -1)
        {
            pool.PrevInIdleCell[next] = prev;
        }
        pool.NextInIdleCell[agentIndex] = -1;
        pool.PrevInIdleCell[agentIndex] = -1;

        // Вставить в новый чанк
        _agentChunk[agentIndex] = newChunk;
        int oldHead = _chunkHeads[newChunk];
        pool.NextInIdleCell[agentIndex] = oldHead;
        pool.PrevInIdleCell[agentIndex] = -1;
        if (oldHead != -1)
        {
            pool.PrevInIdleCell[oldHead] = agentIndex;
        }
        _chunkHeads[newChunk] = agentIndex;
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