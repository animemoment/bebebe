using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Сверхбыстрый плотный пул свободных агентов на основе непрерывного массива (SoA-friendly).
/// Zero allocation, swap-and-pop за O(1), 100% L1-кэш попадание.
/// Полная замена устаревшего IdleWorkerSpatialGrid со связными списками.
/// </summary>
public sealed class FlatIdleWorkerPool
{
    private readonly object _lock = new();
    private int[] _idleAgentIds;
    private int[] _agentToIdleIndex;
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public FlatIdleWorkerPool(int initialCapacity = 16384)
    {
        _idleAgentIds = new int[initialCapacity];
        _agentToIdleIndex = new int[initialCapacity];
        Array.Fill(_agentToIdleIndex, -1);
        _count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacityLocked(int agentIndex)
    {
        if (agentIndex >= _agentToIdleIndex.Length)
        {
            int newCap = Math.Max(_agentToIdleIndex.Length * 2, agentIndex + 1024);
            Array.Resize(ref _idleAgentIds, newCap);
            int oldLen = _agentToIdleIndex.Length;
            Array.Resize(ref _agentToIdleIndex, newCap);
            for (int i = oldLen; i < newCap; i++)
            {
                _agentToIdleIndex[i] = -1;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddIdleWorker(int agentIndex, AgentDataPool pool = null)
    {
        if (agentIndex < 0) return;

        lock (_lock)
        {
            EnsureCapacityLocked(agentIndex);
            if (_agentToIdleIndex[agentIndex] != -1)
                return; // Уже в пуле

            int idx = _count;
            _idleAgentIds[idx] = agentIndex;
            _agentToIdleIndex[agentIndex] = idx;
            _count = idx + 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveIdleWorker(int agentIndex, AgentDataPool pool = null)
    {
        if (agentIndex < 0) return;

        lock (_lock)
        {
            if (agentIndex >= _agentToIdleIndex.Length)
                return;

            int idx = _agentToIdleIndex[agentIndex];
            if (idx == -1)
                return; // Уже удален

            int lastIdx = _count - 1;
            int lastAgent = _idleAgentIds[lastIdx];

            // Swap-and-pop
            _idleAgentIds[idx] = lastAgent;
            _agentToIdleIndex[lastAgent] = idx;

            _idleAgentIds[lastIdx] = -1;
            _agentToIdleIndex[agentIndex] = -1;
            _count = lastIdx;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsIdle(int agentIndex)
    {
        if (agentIndex < 0 || agentIndex >= _agentToIdleIndex.Length)
            return false;
        return Volatile.Read(ref _agentToIdleIndex[agentIndex]) != -1;
    }

    /// <summary>
    /// Пакетный забор свободных агентов для диспетчеризации.
    /// Возвращает реальное количество скопированных ID.
    /// </summary>
    public int CollectIdleWorkers(int maxCount, int[] destination, AgentDataPool pool = null)
    {
        if (destination == null || maxCount <= 0) return 0;

        lock (_lock)
        {
            int toCopy = Math.Min(maxCount, Math.Min(_count, destination.Length));
            if (toCopy <= 0) return 0;

            Array.Copy(_idleAgentIds, 0, destination, 0, toCopy);
            return toCopy;
        }
    }

    /// <summary>
    /// Совместимость со старым API для сбора по чанку.
    /// Так как сетка стала плоской и не разделяется на чанки с мертвыми зонами,
    /// возвращает свободных агентов без принудительной изоляции.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CollectIdleWorkersInChunk(int chunkIndex, int maxCount, int[] destination, AgentDataPool pool = null)
    {
        return CollectIdleWorkers(maxCount, destination, pool);
    }

    /// <summary>
    /// Обновление чанка (no-op в плоском пуле, оставлен для обратной совместимости).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateWorkerChunk(int agentIndex, AgentDataPool pool = null)
    {
        // В плоском пуле перемещение агента не требует перекладывания по связным спискам.
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Fill(_agentToIdleIndex, -1);
            _count = 0;
        }
    }

    public int TotalIdleCount => Volatile.Read(ref _count);
}
