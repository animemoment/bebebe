using System;
using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

public sealed class PlantingJobHandler : IJobHandler
{
    private const float PlantDuration = 10.0f; // 10 секунд на посадку
    private const float ReachDist = 20.0f;

    public JobTypeId TypeId => JobTypeId.Planting;
    public JobExecutionType ExecutionType => JobExecutionType.Hauling;
    public JobPriorityTier DefaultPriority => JobPriorityTier.Farming;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        if (!FarmJobManager.Instance.IsGardenBed(job.TargetX, job.TargetY))
            return false;

        // Planting requires grain (from ground or stockpile).
        return GroundItemManager.Instance.HasAvailableItemsOfType(ItemId.Grain) || StockpileManager.Instance.GetLiveItemCount(ItemId.Grain) > 0;
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        pool.WaypointCount[agentIndex] = 0;
        pool.WaypointIndex[agentIndex] = 0;

        Vector2 pos = pool.GetPosition(agentIndex);

        bool reservedFromGround = GroundItemManager.Instance.TryReserveGroundItems(pos, 5.0f, true, ItemId.Grain, 3, out var itemCell, out var itemId, out int resCount);
        bool reservedFromStock = false;
        (int X, int Y) stockCell = (-1, -1);
        int stockResCount = 0;

        if (!reservedFromGround)
        {
            reservedFromStock = StockpileManager.Instance.TryReserveStockpileItem(pos, ItemId.Grain, 1, 0, out stockCell, out stockResCount);
        }

        if (reservedFromGround)
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
        else if (reservedFromStock)
        {
            pool.States[agentIndex] = AgentState.MovingToSource;
            pool.SourceCellX[agentIndex] = stockCell.X;
            pool.SourceCellY[agentIndex] = stockCell.Y;
            pool.TargetCellX[agentIndex] = job.TargetX;
            pool.TargetCellY[agentIndex] = job.TargetY;
            pool.ReservedItemCount[agentIndex] = 1;
            pool.TargetPositionX[agentIndex] = stockCell.X * ctx.TileSize + 32f;
            pool.TargetPositionY[agentIndex] = stockCell.Y * ctx.TileSize + 32f;
            pool.WorkProgress[agentIndex] = 0f;
            pool.StuckTimer[agentIndex] = 0f;
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
            pool.JobSearchTimer[agentIndex] = 4.0f + (float)ParallelRng.NextDouble() * 4.0f;
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
            // Визуал посадки: 6 стадий ProcessOfWork.
            WorkProgressTracker.Instance.ReportFraction(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex],
                pool.WorkProgress[agentIndex] / PlantDuration);
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
                int sx = pool.SourceCellX[agentIndex];
                int sy = pool.SourceCellY[agentIndex];
                int taken = 0;
                if (StockpileManager.Instance.IsZoneTile(sx, sy))
                {
                    taken = StockpileManager.Instance.WithdrawReservedItem(sx, sy, 1);
                }
                if (taken == 0)
                {
                    taken = GroundItemManager.Instance.TakeItems(sx, sy, 1);
                }

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
                pool.JobSearchTimer[agentIndex] = 4.0f + (float)ParallelRng.NextDouble() * 4.0f;
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
                pool.JobSearchTimer[agentIndex] = 4.0f + (float)ParallelRng.NextDouble() * 4.0f;
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
            WorkProgressTracker.Instance.Clear(tx, ty);
            pool.CarriedItemCount[agentIndex] = 0;
            pool.CarriedItemId[agentIndex] = ItemId.None;

            // Эталон Farming: stale unregister → освободить claim вручную.
            int jobId = pool.CurrentJobId[agentIndex];
            if (jobId != -1)
            {
                if (!JobDispatcher.Instance.TryUnregisterJob(jobId))
                    JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(jobId);
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
            int sx = pool.SourceCellX[agentIndex];
            int sy = pool.SourceCellY[agentIndex];
            int count = pool.ReservedItemCount[agentIndex];
            if (StockpileManager.Instance.IsZoneTile(sx, sy))
            {
                StockpileManager.Instance.CancelWithdrawalReservation(sx, sy, count);
            }
            GroundItemManager.Instance.ReleaseReservation(sx, sy, count);
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