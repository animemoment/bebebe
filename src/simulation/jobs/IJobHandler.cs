using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Единый контракт для обработки любой игровой механики или профессии.
/// </summary>
public interface IJobHandler
{
    JobTypeId TypeId { get; }
    JobExecutionType ExecutionType { get; }
    JobPriorityTier DefaultPriority { get; }
    ToolRequirement RequiredTool { get; }

    bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx);
    void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx);
    void ExecuteParallel(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx);
    void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx);
    void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx);
}