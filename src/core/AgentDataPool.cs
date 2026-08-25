using System;
using System.Numerics;

namespace Game.Core;

/// <summary>
/// SoA (Structure of Arrays) пул данных всех агентов с поддержкой универсальной системы задач.
/// </summary>
public sealed class AgentDataPool
{
    public readonly int Capacity;

    // Позиции и перемещение
    public readonly float[] PositionX;
    public readonly float[] PositionY;
    public readonly float[] TargetPositionX;
    public readonly float[] TargetPositionY;
    public readonly float[] LastPositionX;
    public readonly float[] LastPositionY;
    public readonly float[] Speed;

    // Состояния и задачи
    public readonly AgentState[] States;
    public readonly JobTypeId[] CurrentJobType;
    public readonly int[] CurrentJobId;
    public readonly float[] StuckTimer;
    public readonly float[] WorkProgress;
    public readonly float[] CellStayTime;
    public readonly float[] JobSearchTimer;

    // Универсальные координаты задачи
    public readonly int[] SourceCellX;
    public readonly int[] SourceCellY;
    public readonly int[] TargetCellX;
    public readonly int[] TargetCellY;
    public readonly int[] ReservedItemCount;

    // Текущая клетка
    public readonly int[] CurrentCellX;
    public readonly int[] CurrentCellY;

    // Инвентарь
    public readonly ItemId[] CarriedItemId;
    public readonly int[] CarriedItemCount;
    public readonly ToolRequirement[] EquippedTools;

    // Связный список для пространственной сетки занятых агентов
    public readonly int[] NextInSpatialCell;

    // Связный список для сетки свободных агентов (IdleWorkerSpatialGrid)
    public readonly int[] NextInIdleCell;
    public readonly int[] PrevInIdleCell;

    public AgentDataPool(int capacity)
    {
        Capacity = capacity;

        PositionX = new float[capacity];
        PositionY = new float[capacity];
        TargetPositionX = new float[capacity];
        TargetPositionY = new float[capacity];
        LastPositionX = new float[capacity];
        LastPositionY = new float[capacity];
        Speed = new float[capacity];

        States = new AgentState[capacity];
        CurrentJobType = new JobTypeId[capacity];
        CurrentJobId = new int[capacity];
        Array.Fill(CurrentJobId, -1);

        StuckTimer = new float[capacity];
        WorkProgress = new float[capacity];
        CellStayTime = new float[capacity];
        JobSearchTimer = new float[capacity];

        SourceCellX = new int[capacity];
        SourceCellY = new int[capacity];
        TargetCellX = new int[capacity];
        TargetCellY = new int[capacity];
        ReservedItemCount = new int[capacity];

        CurrentCellX = new int[capacity];
        CurrentCellY = new int[capacity];

        CarriedItemId = new ItemId[capacity];
        CarriedItemCount = new int[capacity];
        EquippedTools = new ToolRequirement[capacity];

        NextInSpatialCell = new int[capacity];
        NextInIdleCell = new int[capacity];
        PrevInIdleCell = new int[capacity];
        Array.Fill(NextInIdleCell, -1);
        Array.Fill(PrevInIdleCell, -1);
    }

    public Vector2 GetPosition(int index) => new(PositionX[index], PositionY[index]);

    public void SetPosition(int index, Vector2 pos)
    {
        PositionX[index] = pos.X;
        PositionY[index] = pos.Y;
    }

    public void CopyPositionsTo(Vector2[] destination)
    {
        int count = Math.Min(Capacity, destination.Length);
        for (int i = 0; i < count; i++)
        {
            destination[i] = new Vector2(PositionX[i], PositionY[i]);
        }
    }
}