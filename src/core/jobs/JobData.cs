namespace Game.Core;

/// <summary>
/// Унифицированная компактная структура задачи в памяти (0 GC).
/// </summary>
public struct JobData
{
    public int Id;
    public JobTypeId TypeId;
    public JobExecutionType ExecutionType;
    public JobPriorityTier PriorityTier;
    public ToolRequirement RequiredTool;

    public int SourceX;
    public int SourceY;
    public int TargetX;
    public int TargetY;
    public int StandX;
    public int StandY;

    public ItemId TargetItemId;
    public int TargetItemCount;
    public int CurrentDeliveredCount;

    public int MaxWorkers;
    public int AssignedWorkers;

    public float WorkDuration;
    public bool IsActive;

    public bool IsAvailable => IsActive && AssignedWorkers < MaxWorkers;
}