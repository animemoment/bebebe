using System;
using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

public sealed class PlantingJobHandler : IJobHandler
{
    private const float PlantDuration = 10.0f; // 10 секунд на посадку
    private const float ReachDist = 48.0f;

    public JobTypeId TypeId => JobTypeId.Planting;
    public JobExecutionType ExecutionType => JobExecutionType.Hauling;
    public JobPriorityTier DefaultPriority => JobPriorityTier.Farming;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        return GroundItemManager.Instance.HasAvailableLogs || GroundItemManager.Instance.HasItemsAt(job.SourceX, job.SourceY);
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        Vector2 pos = pool.GetPosition(agentIndex);

        if (GroundItemManager.Instance.TryReserveGroundItems(pos, 5.0f, true, out var itemCell, out var itemId, out int resCount))
        {
            pool.States[agentIndex] = AgentState.MovingToSource;
            pool.SourceCellX[agentIndex] = itemCell.X;
            pool.SourceCellY[agentIndex] = itemCell.Y;
            pool.TargetCellX[agentIndex] = job.TargetX;
            pool.TargetCellY[agentIndex] = job.TargetY;
            pool.ReservedItemCount[agentIndex] = 1;
            pool.TargetPositionX[agentIndex] = itemCell.X * ctx.TileSize + 32f;
            pool.TargetPositionY[agentIndex] = itemCell.Y * ctx.TileSize + 32f;
            pool.WorkProgress[agentIndex] = 0f;
            pool.StuckTimer[agentIndex] = 0f;

            if (resCount > 1)
            {
                GroundItemManager.Instance.ReleaseReservation(itemCell.X, itemCell.Y, resCount - 1);
            }
        }
        else
        {
            JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
        }
    }

    public void ExecuteParallel(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        var state = pool.States[agentIndex];
        Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);

        if (state == AgentState.MovingToSource || state == AgentState.MovingToTarget)
        {
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
        Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);

        if (state == AgentState.MovingToSource)
        {
            if ((pool.GetPosition(agentIndex) - target).Length() <= ReachDist)
            {
                int taken = GroundItemManager.Instance.TakeItems(pool.SourceCellX[agentIndex], pool.SourceCellY[agentIndex], 1);
                if (taken > 0)
                {
                    pool.CarriedItemId[agentIndex] = ItemId.Grain;
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
        }
        else if (state == AgentState.MovingToTarget)
        {
            if ((pool.GetPosition(agentIndex) - target).Length() <= ReachDist)
            {
                pool.States[agentIndex] = AgentState.Working;
                pool.WorkProgress[agentIndex] = 0f;
            }
        }
        else if (state == AgentState.Working && pool.WorkProgress[agentIndex] >= PlantDuration)
        {
            int tx = pool.TargetCellX[agentIndex];
            int ty = pool.TargetCellY[agentIndex];

            int zoneId = -1;
            if (FarmZoneManager.Instance.TryGetZoneAt(tx, ty, out var zone))
            {
                zoneId = zone.Id;
            }

            CropGrowthManager.Instance.PlantCrop(tx, ty, zoneId);
            pool.CarriedItemCount[agentIndex] = 0;
            pool.CarriedItemId[agentIndex] = ItemId.None;

            int jobId = pool.CurrentJobId[agentIndex];
            if (jobId != -1)
            {
                JobDispatcher.Instance.UnregisterJob(jobId);
            }

            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
            pool.States[agentIndex] = AgentState.Idle;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
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