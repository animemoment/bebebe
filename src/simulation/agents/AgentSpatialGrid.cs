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