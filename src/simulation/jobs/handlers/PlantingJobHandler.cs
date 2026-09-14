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
        // Planting requires grain, not logs.
        return GroundItemManager.Instance.HasAvailableItemsOfType(ItemId.Grain);
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        Vector2 pos = pool.GetPosition(agentIndex);

        // P0.3: радиус 3 чанка (40 тайлов еды) вместо полного 32 — тот же лимит что в NeedsJobSystem.
        if (GroundItemManager.Instance.TryReserveGroundItems(pos, 5.0f, true, ItemId.Grain, 3, out var itemCell, out var itemId, out int resCount))
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
            // Транзиентный фейл резерва зерна: работу оставляем + кулдаун (P0.2).
            int failId = pool.CurrentJobId[agentIndex];
            if (failId != -1)
            {
                JobDispatcher.Instance.JobIndex.MarkJobUnreachable(failId, 5000);
                JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(failId);
                pool.CurrentJobId[agentIndex] = -1;
                pool.CurrentJobType[agentIndex] = JobTypeId.None;
            }
            pool.States[agentIndex] = AgentState.Idle;
            pool.JobSearchTimer[agentIndex] = 4.0f + (float)ctx.Random.NextDouble() * 4.0f;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
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

                    (int X, int Y) stand = (pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex]);
                    Game.Simulation.JobBroker.FindStandPosition(stand.X, stand.Y, ctx, out stand);
                    pool.TargetPositionX[agentIndex] = stand.X * ctx.TileSize + 32f;
                    pool.TargetPositionY[agentIndex] = stand.Y * ctx.TileSize + 32f;
                }
                else
                {
                    JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
                }
            }
            else if (pool.StuckTimer[agentIndex] >= 3.0f)
            {
                JobDispatcher.Instance.ReleaseJobWorkerForStuck(agentIndex, pool, ctx);
                pool.JobSearchTimer[agentIndex] = 4.0f + (float)ctx.Random.NextDouble() * 4.0f;
            }
        }
        else if (state == AgentState.MovingToTarget)
        {
            if ((pool.GetPosition(agentIndex) - target).Length() <= ReachDist)
            {
                pool.States[agentIndex] = AgentState.Working;
                pool.WorkProgress[agentIndex] = 0f;
            }
            else if (pool.StuckTimer[agentIndex] >= 3.0f)
            {
                // Дроп рядом, а не на клетку цели: посадка на клетку будущей стройки/
                // грядки + Construction.Can=!HasItemsAt = вечный блок стройки.
                (int X, int Y) drop = (pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex]);
                Game.Simulation.JobBroker.FindStandPosition(drop.X, drop.Y, ctx, out drop);
                GroundItemManager.Instance.SpawnItems(
                    drop.X, drop.Y,
                    pool.CarriedItemId[agentIndex], pool.CarriedItemCount[agentIndex]);
                pool.CarriedItemCount[agentIndex] = 0;
                pool.CarriedItemId[agentIndex] = ItemId.None;
                JobDispatcher.Instance.ReleaseJobWorkerForStuck(agentIndex, pool, ctx);
                pool.JobSearchTimer[agentIndex] = 4.0f + (float)ctx.Random.NextDouble() * 4.0f;
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
                // RemoveJob уже поправил _unclaimedCount — отдельный
                // ReleaseWorkerClaim после удачного Unregister удвоил бы счётчик.
                JobDispatcher.Instance.TryUnregisterJob(jobId);
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