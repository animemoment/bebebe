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
    /// P3: параллельный rebuild. Разбиваем агентов по полосам (stripeCount полос),
    /// каждая полоса пишет ТОЛЬКО в свои ячейки (cellIndex % stripeCount == stripe) —
    /// гонок нет: два агента в одну ячейку всегда в одной полосе и идут
    /// последовательно внутри неё. Локальный список активных ячеек на полосу,
    /// затем слияние в общий _activeCells (Clear идёт только по нему).
    /// Out-of-bounds агенты получают Next=-1 (как в Insert).
    /// </summary>
    public void RebuildParallel(int agentCount, int[] cellX, int[] cellY, int[] nextInCell, int stripeCount = 0)
    {
        // Разреженная очистка прошлого rebuild (O(K) активных, не O(W*H)).
        Clear();

        if (agentCount <= 0)
            return;

        int stripes = stripeCount <= 0
            ? Math.Max(1, Math.Min(Environment.ProcessorCount, 8))
            : Math.Max(1, stripeCount);

        // Маленький пул — последовательный путь дешевле, чем веер потоков.
        if (agentCount < 4096 || stripes < 2)
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

        // Локальные счётчики активных ячеек на полосу (пишутся только своим потоком).
        int[] stripeActiveCounts = new int[stripes];
        // Первый проход: считаем, сколько ячеек достанется каждой полосе, чтобы
        // выделить точные буферы без перераспределения (O(N) дешёвый скан int).
        int[] stripeCellHint = new int[stripes];
        for (int i = 0; i < agentCount; i++)
        {
            int cx = cellX[i], cy = cellY[i];
            if ((uint)cx >= (uint)_width || (uint)cy >= (uint)_height)
                continue;
            stripeCellHint[(cy * _width + cx) % stripes]++;
        }

        int[][] stripeActive = new int[stripes][];
        for (int s = 0; s < stripes; s++)
            stripeActive[s] = new int[Math.Max(1, stripeCellHint[s])];

        System.Threading.Tasks.Parallel.For(0, stripes,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = stripes },
            s =>
            {
                int[] localActive = stripeActive[s];
                int localCount = 0;
                for (int i = s; i < agentCount; i += stripes)
                {
                    int cx = cellX[i], cy = cellY[i];
                    if ((uint)cx >= (uint)_width || (uint)cy >= (uint)_height)
                    {
                        nextInCell[i] = -1;
                        continue;
                    }
                    int cellIndex = cy * _width + cx;
                    // В эту ячейку пишут только агенты этой полосы, НО разные
                    // полосы делят _cellHeads: ячейка j принадлежит ровно одной
                    // полосе (j % stripes == s), чужие ячейки не трогаем.
                    if (cellIndex % stripes != s)
                    {
                        // Агент не в своей полосе по ячейке — откладываем:
                        // его обработает своя полоса во втором проходе.
                        // Помечаем временным значением, не -1 (чтобы отличить).
                        nextInCell[i] = int.MinValue;
                        continue;
                    }
                    if (_cellHeads[cellIndex] == -1)
                        localActive[localCount++] = cellIndex;
                    nextInCell[i] = _cellHeads[cellIndex];
                    _cellHeads[cellIndex] = i;
                }
                stripeActiveCounts[s] = localCount;
            });

        // Второй проход: агенты, чья ячейка в другой полосе (stride по агентам
        // не совпал с полосой ячейки). Их мало, идём последовательно.
        for (int i = 0; i < agentCount; i++)
        {
            if (nextInCell[i] != int.MinValue)
                continue;
            int cx = cellX[i], cy = cellY[i];
            int cellIndex = cy * _width + cx; // bounds уже проверены в 1-м проходе
            int s = cellIndex % stripes;
            if (_cellHeads[cellIndex] == -1)
                stripeActive[s][stripeActiveCounts[s]++] = cellIndex;
            nextInCell[i] = _cellHeads[cellIndex];
            _cellHeads[cellIndex] = i;
        }

        // Слияние локальных списков активных ячеек в общий.
        for (int s = 0; s < stripes; s++)
        {
            int[] localActive = stripeActive[s];
            int localCount = stripeActiveCounts[s];
            for (int k = 0; k < localCount; k++)
                _activeCells[_activeCellCount++] = localActive[k];
        }
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
            return true;

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