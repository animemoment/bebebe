using System;
using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

public sealed class StockpileHaulingJobHandler : IJobHandler
{
    private const float ReachDist = 48.0f;
    private const float MaxCarryWeight = 25.0f;
    private const float MaxStuckDuration = 5.0f;

    public JobTypeId TypeId => JobTypeId.StockpileHauling;
    public JobExecutionType ExecutionType => JobExecutionType.Hauling;
    public JobPriorityTier DefaultPriority => JobPriorityTier.StockpileHauling;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        return GroundItemManager.Instance.HasAvailableLogs && StockpileManager.Instance.HasFreeSpace;
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        Vector2 pos = pool.GetPosition(agentIndex);
        if (GroundItemManager.Instance.TryReserveGroundItems(pos, MaxCarryWeight, false, out var itemCell, out var itemId, out int resCount))
        {
            if (StockpileManager.Instance.TryReserveStockpileSlot(pos, itemId, resCount, out var slot, out int acceptedCount))
            {
                if (acceptedCount < resCount)
                {
                    GroundItemManager.Instance.ReleaseReservation(itemCell.X, itemCell.Y, resCount - acceptedCount);
                }

                pool.States[agentIndex] = AgentState.MovingToSource;
                pool.SourceCellX[agentIndex] = itemCell.X;
                pool.SourceCellY[agentIndex] = itemCell.Y;
                pool.TargetCellX[agentIndex] = slot.X;
                pool.TargetCellY[agentIndex] = slot.Y;
                pool.ReservedItemCount[agentIndex] = acceptedCount;
                pool.TargetPositionX[agentIndex] = itemCell.X * ctx.TileSize + 32f;
                pool.TargetPositionY[agentIndex] = itemCell.Y * ctx.TileSize + 32f;
                pool.StuckTimer[agentIndex] = 0f;
                return;
            }
            else
            {
                GroundItemManager.Instance.ReleaseReservation(itemCell.X, itemCell.Y, resCount);
            }
        }

        int currentJobId = pool.CurrentJobId[agentIndex];
        if (currentJobId != -1)
        {
            JobDispatcher.Instance.UnregisterJob(currentJobId);
        }
        JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
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
            pool.StuckTimer[agentIndex] = 0f;

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
                    int jobId = pool.CurrentJobId[agentIndex];
                    if (jobId != -1)
                    {
                        JobDispatcher.Instance.UnregisterJob(jobId);
                    }
                    JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
                }
            }
            else if (state == AgentState.MovingToTarget)
            {
                int sx = pool.TargetCellX[agentIndex];
                int sy = pool.TargetCellY[agentIndex];

                if (StockpileManager.Instance.IsZoneTile(sx, sy))
                {
                    StockpileManager.Instance.DepositItems(sx, sy, pool.CarriedItemId[agentIndex], pool.CarriedItemCount[agentIndex]);
                    GroundItemManager.Instance.SpawnItems(sx, sy, pool.CarriedItemId[agentIndex], pool.CarriedItemCount[agentIndex]);
                }
                else
                {
                    GroundItemManager.Instance.SpawnItems(sx, sy, pool.CarriedItemId[agentIndex], pool.CarriedItemCount[agentIndex]);
                }

                pool.CarriedItemCount[agentIndex] = 0;
                pool.CarriedItemId[agentIndex] = ItemId.None;

                int jobId = pool.CurrentJobId[agentIndex];
                if (jobId != -1)
                {
                    JobDispatcher.Instance.UnregisterJob(jobId);
                }
                JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
            }
        }
        else if (pool.StuckTimer[agentIndex] >= MaxStuckDuration)
        {
            int jobId = pool.CurrentJobId[agentIndex];
            if (jobId != -1)
            {
                JobDispatcher.Instance.UnregisterJob(jobId);
            }
            JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
            pool.JobSearchTimer[agentIndex] = 10.0f + (float)ctx.Random.NextDouble() * 10.0f;
        }
    }

    public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        int jobId = pool.CurrentJobId[agentIndex];
        if (jobId != -1)
        {
            JobDispatcher.Instance.UnregisterJob(jobId);
        }

        if (pool.States[agentIndex] == AgentState.MovingToSource)
        {
            GroundItemManager.Instance.ReleaseReservation(pool.SourceCellX[agentIndex], pool.SourceCellY[agentIndex], pool.ReservedItemCount[agentIndex]);
            StockpileManager.Instance.CancelReservation(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex], pool.ReservedItemCount[agentIndex]);
        }
        else if (pool.CarriedItemCount[agentIndex] > 0)
        {
            StockpileManager.Instance.CancelReservation(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex], pool.CarriedItemCount[agentIndex]);
            int cx = Math.Clamp((int)(pool.PositionX[agentIndex] / ctx.TileSize), 0, ctx.MapWidth - 1);
            int cy = Math.Clamp((int)(pool.PositionY[agentIndex] / ctx.TileSize), 0, ctx.MapHeight - 1);
            GroundItemManager.Instance.SpawnItems(cx, cy, pool.CarriedItemId[agentIndex], pool.CarriedItemCount[agentIndex]);
            pool.CarriedItemCount[agentIndex] = 0;
            pool.CarriedItemId[agentIndex] = ItemId.None;
        }
    }
}