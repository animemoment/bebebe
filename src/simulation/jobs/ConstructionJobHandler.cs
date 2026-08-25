using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

public sealed class ConstructionJobHandler : IJobHandler
{
    private const float BuildDuration = 10.0f;
    private const float ReachDist = 48.0f;

    public JobTypeId TypeId => JobTypeId.Construction;
    public JobExecutionType ExecutionType => JobExecutionType.Stationary;
    public JobPriorityTier DefaultPriority => JobPriorityTier.Construction;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        return !GroundItemManager.Instance.HasItemsAt(job.TargetX, job.TargetY);
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        pool.States[agentIndex] = AgentState.MovingToSource;
        pool.TargetCellX[agentIndex] = job.TargetX;
        pool.TargetCellY[agentIndex] = job.TargetY;
        pool.TargetPositionX[agentIndex] = job.StandX * ctx.TileSize + 32f;
        pool.TargetPositionY[agentIndex] = job.StandY * ctx.TileSize + 32f;
        pool.WorkProgress[agentIndex] = 0f;
    }

    public void ExecuteParallel(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        var state = pool.States[agentIndex];
        if (state == AgentState.MovingToSource)
        {
            Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);
            ctx.Movement.MoveTowards(agentIndex, target, ReachDist, deltaTime, pool, ctx);
        }
        else if (state == AgentState.Working)
        {
            pool.WorkProgress[agentIndex] += deltaTime;
        }
    }

    public void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        var state = pool.States[agentIndex];
        int sx = pool.TargetCellX[agentIndex];
        int sy = pool.TargetCellY[agentIndex];

        if (state == AgentState.MovingToSource)
        {
            Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);
            if ((pool.GetPosition(agentIndex) - target).Length() <= ReachDist)
            {
                pool.States[agentIndex] = AgentState.Working;
            }
        }
        else if (state == AgentState.Working && pool.WorkProgress[agentIndex] >= BuildDuration)
        {
            if (BlueprintManager.Instance.CompleteConstruction(sx, sy, out _))
            {
                ctx.SolidWalls[sx, sy] = true;
                FlowFieldManager.Instance.ClearCache();

                int insideAgent = ctx.SpatialGrid.GetFirstAgent(sx, sy);
                while (insideAgent != -1)
                {
                    ctx.Movement.EjectFromWall(insideAgent, sx, sy, pool, ctx);
                    insideAgent = ctx.SpatialGrid.GetNextAgent(insideAgent, pool);
                }
            }

            JobDispatcher.Instance.UnregisterJob(pool.CurrentJobId[agentIndex]);
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
            pool.States[agentIndex] = AgentState.Idle;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
        }
    }

    public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx) { }
}
