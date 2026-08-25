using System;
using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

public sealed class BlueprintDeliveryJobHandler : IJobHandler
{
    private const float ReachDist = 48.0f;
    private const float MaxCarryWeight = 25.0f;

    public JobTypeId TypeId => JobTypeId.BlueprintDelivery;
    public JobExecutionType ExecutionType => JobExecutionType.Hauling;
    public JobPriorityTier DefaultPriority => JobPriorityTier.BlueprintSupply;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        return GroundItemManager.Instance.HasAvailableLogs;
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        Vector2 pos = pool.GetPosition(agentIndex);
        if (GroundItemManager.Instance.TryReserveGroundItems(pos, MaxCarryWeight, true, out var itemCell, out var itemId, out int resCount))
        {
            pool.States[agentIndex] = AgentState.MovingToSource;
            pool.SourceCellX[agentIndex] = itemCell.X;
            pool.SourceCellY[agentIndex] = itemCell.Y;
            pool.TargetCellX[agentIndex] = job.TargetX;
            pool.TargetCellY[agentIndex] = job.TargetY;
            pool.ReservedItemCount[agentIndex] = resCount;
            pool.TargetPositionX[agentIndex] = itemCell.X * ctx.TileSize + 32f;
            pool.TargetPositionY[agentIndex] = itemCell.Y * ctx.TileSize + 32f;
        }
        else
        {
            JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
        }
    }

    public void ExecuteParallel(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);
        ctx.Movement.MoveTowards(agentIndex, target, ReachDist, deltaTime, pool, ctx);
    }

    public void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        var state = pool.States[agentIndex];
        Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);

        if ((pool.GetPosition(agentIndex) - target).Length() <= ReachDist)
        {
            if (state == AgentState.MovingToSource)
            {
                int taken = GroundItemManager.Instance.TakeItems(pool.SourceCellX[agentIndex], pool.SourceCellY[agentIndex], pool.ReservedItemCount[agentIndex]);
                if (taken > 0)
                {
                    pool.CarriedItemId[agentIndex] = ItemId.Log;
                    pool.CarriedItemCount[agentIndex] = taken;
                    pool.States[agentIndex] = AgentState.MovingToTarget;
                    pool.TargetPositionX[agentIndex] = pool.TargetCellX[agentIndex] * ctx.TileSize + 32f;
                    pool.TargetPositionY[agentIndex] = pool.TargetCellY[agentIndex] * ctx.TileSize + 32f;
                }
                else
                {
                    JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
                }
            }
            else if (state == AgentState.MovingToTarget)
            {
                int tx = pool.TargetCellX[agentIndex];
                int ty = pool.TargetCellY[agentIndex];

                if (JobDispatcher.Instance.JobIndex.TryGetJobByPos(tx, ty, JobTypeId.BlueprintDelivery, out _))
                {
                    JobBroker.Instance.DeliverLogsToBlueprint(tx, ty, pool.CarriedItemCount[agentIndex]);
                }
                else
                {
                    GroundItemManager.Instance.SpawnItems(tx, ty, pool.CarriedItemId[agentIndex], pool.CarriedItemCount[agentIndex]);
                }

                pool.CarriedItemCount[agentIndex] = 0;
                pool.CarriedItemId[agentIndex] = ItemId.None;
                JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
            }
        }
    }

    public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        if (pool.States[agentIndex] == AgentState.MovingToSource)
        {
            GroundItemManager.Instance.ReleaseReservation(pool.SourceCellX[agentIndex], pool.SourceCellY[agentIndex], pool.ReservedItemCount[agentIndex]);
        }
        else if (pool.CarriedItemCount[agentIndex] > 0)
        {
            int cx = Math.Clamp((int)(pool.PositionX[agentIndex] / ctx.TileSize), 0, ctx.MapWidth - 1);
            int cy = Math.Clamp((int)(pool.PositionY[agentIndex] / ctx.TileSize), 0, ctx.MapHeight - 1);
            GroundItemManager.Instance.SpawnItems(cx, cy, pool.CarriedItemId[agentIndex], pool.CarriedItemCount[agentIndex]);
            pool.CarriedItemCount[agentIndex] = 0;
            pool.CarriedItemId[agentIndex] = ItemId.None;
        }
    }
}