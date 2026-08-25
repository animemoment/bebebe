namespace Game.Core;

/// <summary>
/// Унифицированные обобщённые состояния агента.
/// </summary>
public enum AgentState : byte
{
    Idle = 0,
    MovingToSource = 1,
    Working = 2,
    MovingToTarget = 3,
    Evacuating = 4
}