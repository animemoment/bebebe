using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Пространственная 2D-сетка с O(1) доступом, контролем плотности и разреженной очисткой O(K).
/// </summary>
public sealed class AgentSpatialGrid
{
    private readonly int[] _cellHeads;
    private readonly int[] _activeCells;
    private int _activeCellCount = 0;
    private readonly int _width;
    private readonly int _height;

    public AgentSpatialGrid(int width, int height)
    {
        _width = width;
        _height = height;
        _cellHeads = new int[width * height];
        _activeCells = new int[width * height];
        Array.Fill(_cellHeads, -1);
    }

    /// <summary>
    /// Очищает только активные ячейки за O(K), где K — число занятых клеток (~1000 вместо 262144).
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < _activeCellCount; i++)
        {
            _cellHeads[_activeCells[i]] = -1;
        }
        _activeCellCount = 0;
    }

    public void Insert(int agentIndex, int cellX, int cellY, AgentDataPool pool)
    {
        if (cellX < 0 || cellY < 0 || cellX >= _width || cellY >= _height)
        {
            pool.NextInSpatialCell[agentIndex] = -1;
            return;
        }

        int cellIndex = cellY * _width + cellX;

        // Если ячейка была пустой — запоминаем её для быстрой очистки
        if (_cellHeads[cellIndex] == -1)
        {
            _activeCells[_activeCellCount++] = cellIndex;
        }

        pool.NextInSpatialCell[agentIndex] = _cellHeads[cellIndex];
        _cellHeads[cellIndex] = agentIndex;
    }

    /// <summary>
    /// P3: истинно параллельный lock-free rebuild через Treiber stack (PLAN.md §36).
    /// Агенты делятся на непрерывные диапазоны [start, end) по воркерам.
    /// Вставка в ячейку выполняется через атомарный Interlocked.Exchange:
    /// гонки исключены, ячейка регистрируется в _activeCells ровно один раз (когда oldHead == -1).
    /// Ноль полосовых промахов (в отличие от старого cellIndex % stripes), 100% многопоточность.
    /// </summary>
    public void RebuildParallel(int agentCount, int[] cellX, int[] cellY, int[] nextInCell, int stripeCount = 0)
    {
        // Разреженная очистка прошлого rebuild (O(K) активных, не O(W*H)).
        Clear();

        if (agentCount <= 0)
            return;

        int dop = stripeCount <= 0
            ? Math.Max(1, Math.Min(Environment.ProcessorCount, 8))
            : Math.Max(1, stripeCount);

        // Однопоточный путь для пулов до 65k агентов в разы быстрее многопоточного,
        // так как массив 10k элементов полностью помещается в L1/L2 кэш CPU
        // и исключает 10 000 атомарных инструкций Interlocked и false-sharing.
        if (agentCount <= 65536 || dop < 2)
        {
            for (int i = 0; i < agentCount; i++)
            {
                int cx = cellX[i], cy = cellY[i];
                if ((uint)cx >= (uint)_width || (uint)cy >= (uint)_height)
                {
                    nextInCell[i] = -1;
                    continue;
                }
                int cellIndex = cy * _width + cx;
                if (_cellHeads[cellIndex] == -1)
                    _activeCells[_activeCellCount++] = cellIndex;
                nextInCell[i] = _cellHeads[cellIndex];
                _cellHeads[cellIndex] = i;
            }
            return;
        }

        int chunkSize = (agentCount + dop - 1) / dop;
        var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = dop };

        System.Threading.Tasks.Parallel.For(0, dop,
            options,
            threadIndex =>
            {
                int start = threadIndex * chunkSize;
                int end = Math.Min(agentCount, start + chunkSize);

                for (int i = start; i < end; i++)
                {
                    int cx = cellX[i], cy = cellY[i];
                    if ((uint)cx >= (uint)_width || (uint)cy >= (uint)_height)
                    {
                        nextInCell[i] = -1;
                        continue;
                    }
                    int cellIndex = cy * _width + cx;

                    // Lock-free Treiber stack: атомарно заменяем голову ячейки на i
                    // и связываем i со старой головой.
                    int oldHead = System.Threading.Interlocked.Exchange(ref _cellHeads[cellIndex], i);
                    nextInCell[i] = oldHead;

                    // Если старая голова была -1, эта ячейка впервые стала активной в текущем кадре.
                    // Ровно один поток получит oldHead == -1 для каждой активной ячейки.
                    if (oldHead == -1)
                    {
                        int activeIdx = System.Threading.Interlocked.Increment(ref _activeCellCount) - 1;
                        _activeCells[activeIdx] = cellIndex;
                    }
                }
            });
    }

    public int GetFirstAgent(int cellX, int cellY)
    {
        if (cellX < 0 || cellY < 0 || cellX >= _width || cellY >= _height)
            return -1;

        return _cellHeads[cellY * _width + cellX];
    }

    public int GetNextAgent(int currentAgentIndex, AgentDataPool pool)
    {
        if (currentAgentIndex < 0 || currentAgentIndex >= pool.Capacity)
            return -1;

        return pool.NextInSpatialCell[currentAgentIndex];
    }

    public bool IsCellOvercrowded(int cellX, int cellY, int maxCount, AgentDataPool pool)
    {
        if (cellX < 0 || cellY < 0 || cellX >= _width || cellY >= _height)
            return false;

        int count = 0;
        int curr = _cellHeads[cellY * _width + cellX];
        while (curr != -1)
        {
            count++;
            if (count >= maxCount) return true;
            curr = pool.NextInSpatialCell[curr];
        }
        return false;
    }

    public bool HasAgentStandingLongerThan(int cellX, int cellY, int excludeAgentIndex, float minSeconds, AgentDataPool pool, out int blockingAgentIndex)
    {
        blockingAgentIndex = -1;
        int curr = GetFirstAgent(cellX, cellY);

        while (curr != -1)
        {
            if (curr != excludeAgentIndex && pool.CellStayTime[curr] >= minSeconds)
            {
                blockingAgentIndex = curr;
                return true;
            }
            curr = GetNextAgent(curr, pool);
        }

        return false;
    }
}