using System;
using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

public sealed class BlueprintDeliveryJobHandler : IJobHandler
{
    private const float ReachDist = 20.0f;
    private const float MaxCarryWeight = 25.0f;

    public JobTypeId TypeId => JobTypeId.BlueprintDelivery;
    public JobExecutionType ExecutionType => JobExecutionType.Hauling;
    public JobPriorityTier DefaultPriority => JobPriorityTier.BlueprintSupply;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        // Если агент уже несёт брёвна, ему не нужны брёвна на земле
        bool hasLogs = pool != null && (uint)agentIndex < (uint)pool.Capacity && pool.CarriedItemId[agentIndex] == ItemId.Log && pool.CarriedItemCount[agentIndex] > 0;
        if (!hasLogs && !GroundItemManager.Instance.HasAvailableLogs && StockpileManager.Instance.GetLiveItemCount(ItemId.Log) <= 0)
            return false;

        // Поднос — только на клетках в стадии Supplying
        if (ConstructionManager.Instance.TryGetSite(job.TargetX, job.TargetY, out var site))
        {
            if (site.Stage != ConstructionStage.Supplying)
                return false;
        }
        else if (!ConstructionManager.Instance.IsStageAllowed(job.TargetX, job.TargetY, JobTypeId.BlueprintDelivery))
        {
            return false;
        }

        if (ctx != null
            && (uint)job.TargetX < (uint)ctx.MapWidth && (uint)job.TargetY < (uint)ctx.MapHeight)
        {
            if (ctx.TreeOnGrass != null && ctx.TreeOnGrass[job.TargetX, job.TargetY])
                return false;
            if (ctx.StoneOnGrass != null && ctx.StoneOnGrass[job.TargetX, job.TargetY])
                return false;
            if (ctx.Ground != null && ctx.Ground[job.TargetX, job.TargetY] != TileType.Grass)
                return false;

            // Чужие предметы (не брёвна) мешают подносу. Сами брёвна — не мешают.
            var itemOnGround = GroundItemManager.Instance.PeekItemAt(job.TargetX, job.TargetY);
            if (itemOnGround != ItemId.None && itemOnGround != ItemId.Log)
                return false;
        }
        return true;
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        pool.WaypointCount[agentIndex] = 0;
        pool.WaypointIndex[agentIndex] = 0;

        int remaining = job.TargetItemCount - job.CurrentDeliveredCount;
        if (remaining <= 0)
        {
            int failId = pool.CurrentJobId[agentIndex];
            if (failId != -1)
            {
                JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(failId);
                pool.CurrentJobId[agentIndex] = -1;
                pool.CurrentJobType[agentIndex] = JobTypeId.None;
            }
            pool.States[agentIndex] = AgentState.Idle;
            pool.JobSearchTimer[agentIndex] = 2.0f + (float)ParallelRng.NextDouble() * 2.0f;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
            return;
        }

        // Агент уже держит брёвна (например, срубил дерево) — сразу несёт на стройку!
        if (pool.CarriedItemId[agentIndex] == ItemId.Log && pool.CarriedItemCount[agentIndex] > 0)
        {
            pool.States[agentIndex] = AgentState.MovingToTarget;
            pool.TargetCellX[agentIndex] = job.TargetX;
            pool.TargetCellY[agentIndex] = job.TargetY;
            (int X, int Y) stand = (job.TargetX, job.TargetY);
            JobBroker.FindStandPosition(job.TargetX, job.TargetY, ctx, out stand);
            pool.TargetPositionX[agentIndex] = stand.X * ctx.TileSize + 32f;
            pool.TargetPositionY[agentIndex] = stand.Y * ctx.TileSize + 32f;
            pool.StuckTimer[agentIndex] = 0f;
            return;
        }

        Vector2 pos = pool.GetPosition(agentIndex);
        float needWeight = Math.Min(MaxCarryWeight, remaining * ItemRegistry.Get(ItemId.Log).Weight);
        int needCount = Math.Max(1, (int)(needWeight / ItemRegistry.Get(ItemId.Log).Weight));

        bool reservedFromGround = GroundItemManager.Instance.TryReserveGroundItems(pos, needWeight, true, ItemId.Log, out var itemCell, out var itemId, out int resCount);
        bool reservedFromStock = false;
        (int X, int Y) stockCell = (-1, -1);
        int stockResCount = 0;

        if (!reservedFromGround)
        {
            reservedFromStock = StockpileManager.Instance.TryReserveStockpileItem(pos, ItemId.Log, needCount, 0, out stockCell, out stockResCount);
        }

        if (reservedFromGround)
        {
            pool.States[agentIndex] = AgentState.MovingToSource;
            pool.SourceCellX[agentIndex] = itemCell.X;
            pool.SourceCellY[agentIndex] = itemCell.Y;
            pool.TargetCellX[agentIndex] = job.TargetX;
            pool.TargetCellY[agentIndex] = job.TargetY;
            pool.ReservedItemCount[agentIndex] = resCount;
            pool.TargetPositionX[agentIndex] = itemCell.X * ctx.TileSize + 32f;
            pool.TargetPositionY[agentIndex] = itemCell.Y * ctx.TileSize + 32f;
            pool.StuckTimer[agentIndex] = 0f;
        }
        else if (reservedFromStock)
        {
            pool.States[agentIndex] = AgentState.MovingToSource;
            pool.SourceCellX[agentIndex] = stockCell.X;
            pool.SourceCellY[agentIndex] = stockCell.Y;
            pool.TargetCellX[agentIndex] = job.TargetX;
            pool.TargetCellY[agentIndex] = job.TargetY;
            pool.ReservedItemCount[agentIndex] = stockResCount;
            pool.TargetPositionX[agentIndex] = stockCell.X * ctx.TileSize + 32f;
            pool.TargetPositionY[agentIndex] = stockCell.Y * ctx.TileSize + 32f;
            pool.StuckTimer[agentIndex] = 0f;
        }
        else
        {
            int failId = pool.CurrentJobId[agentIndex];
            if (failId != -1)
            {
                JobDispatcher.Instance.JobIndex.MarkJobUnreachable(failId, 2000);
                JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(failId);
                pool.CurrentJobId[agentIndex] = -1;
                pool.CurrentJobType[agentIndex] = JobTypeId.None;
            }
            pool.States[agentIndex] = AgentState.Idle;
            pool.JobSearchTimer[agentIndex] = 2.0f + (float)ParallelRng.NextDouble() * 2.0f;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
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
        int tx = pool.TargetCellX[agentIndex];
        int ty = pool.TargetCellY[agentIndex];
        Vector2 standTarget = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);
        Vector2 blueprintCenter = new(tx * ctx.TileSize + 32f, ty * ctx.TileSize + 32f);
        Vector2 agentPos = pool.GetPosition(agentIndex);

        bool reached = (agentPos - standTarget).Length() <= ReachDist ||
                       (state == AgentState.MovingToTarget && (agentPos - blueprintCenter).Length() <= 64.0f);

        if (reached)
        {
            pool.StuckTimer[agentIndex] = 0f;
            if (state == AgentState.MovingToSource)
            {
                int sx = pool.SourceCellX[agentIndex];
                int sy = pool.SourceCellY[agentIndex];
                int count = pool.ReservedItemCount[agentIndex];
                int taken = 0;

                if (StockpileManager.Instance.IsZoneTile(sx, sy))
                {
                    taken = StockpileManager.Instance.WithdrawReservedItem(sx, sy, count);
                }
                if (taken == 0)
                {
                    taken = GroundItemManager.Instance.TakeItems(sx, sy, count);
                }

                if (taken > 0)
                {
                    pool.CarriedItemId[agentIndex] = ItemId.Log;
                    pool.CarriedItemCount[agentIndex] = taken;
                    pool.States[agentIndex] = AgentState.MovingToTarget;
                    (int X, int Y) stand = (tx, ty);
                    JobBroker.FindStandPosition(tx, ty, ctx, out stand);
                    pool.TargetPositionX[agentIndex] = stand.X * ctx.TileSize + 32f;
                    pool.TargetPositionY[agentIndex] = stand.Y * ctx.TileSize + 32f;
                }
                else
                {
                    JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
                }
            }
            else if (state == AgentState.MovingToTarget)
            {
                int carried = pool.CarriedItemCount[agentIndex];
                ItemId carriedId = pool.CarriedItemId[agentIndex];
                if (carried > 0 && carriedId != ItemId.None)
                {
                    JobBroker.Instance.DeliverLogsToBlueprint(tx, ty, carried);
                }

                pool.CarriedItemCount[agentIndex] = 0;
                pool.CarriedItemId[agentIndex] = ItemId.None;
                JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
            }
        }
        else if (pool.StuckTimer[agentIndex] >= 3.0f)
        {
            JobDispatcher.Instance.ReleaseJobWorkerForStuck(agentIndex, pool, ctx);
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