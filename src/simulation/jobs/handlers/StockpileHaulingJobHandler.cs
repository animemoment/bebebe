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
        // Haul везёт любой тип (Log/Grain), а не только брёвна: иначе работы
        // по зерну создавались Sweep'ом, но вечно фейлились в OnStart.
        return (GroundItemManager.Instance.HasAvailableLogs ||
                GroundItemManager.Instance.HasAvailableItemsOfType(ItemId.Grain)) &&
               StockpileManager.Instance.HasFreeSpace;
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        // P1.1: сначала точечный резерв кучи из самой задачи (job.SourceX/Y) —
        // куча может быть дальше радиуса скана от рабочего; радиус-fallback — вторым.
        if (GroundItemManager.Instance.TryReserveSpecificCell(job.SourceX, job.SourceY, MaxCarryWeight, out var specId, out int specCount) &&
            specId != ItemId.None && specCount > 0 &&
            StockpileManager.Instance.TryReserveStockpileSlot(pool.GetPosition(agentIndex), specId, specCount, out var specSlot, out int specAccepted) &&
            specAccepted > 0)
        {
            if (specAccepted < specCount)
                GroundItemManager.Instance.ReleaseReservation(job.SourceX, job.SourceY, specCount - specAccepted);
            pool.States[agentIndex] = AgentState.MovingToSource;
            pool.SourceCellX[agentIndex] = job.SourceX;
            pool.SourceCellY[agentIndex] = job.SourceY;
            pool.TargetCellX[agentIndex] = specSlot.X;
            pool.TargetCellY[agentIndex] = specSlot.Y;
            pool.ReservedItemCount[agentIndex] = specAccepted;
            pool.TargetPositionX[agentIndex] = job.SourceX * ctx.TileSize + 32f;
            pool.TargetPositionY[agentIndex] = job.SourceY * ctx.TileSize + 32f;
            pool.StuckTimer[agentIndex] = 0f;
            return;
        }
        else if (specId != ItemId.None && specCount > 0)
        {
            // Точечный резерв взяли, но слот склада не дали — откатываем резерв.
            GroundItemManager.Instance.ReleaseReservation(job.SourceX, job.SourceY, specCount);
        }

        Vector2 pos = pool.GetPosition(agentIndex);
        // P0.3: лимит радиуса 4 чанка вместо полного 32 (см. BlueprintDeliveryJobHandler).
        // preferredId=None: везём любой тип (Log/Grain), фильтр только по наличию.
        if (GroundItemManager.Instance.TryReserveGroundItems(pos, MaxCarryWeight, false, ItemId.None, 4, out var itemCell, out var itemId, out int resCount))
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

        // P0.2: транзиентный фейл резерва — НЕ удаляем работу. Кулдаун, чтобы
        // следующий бездельник не долбился в ту же кучу каждый тик; Sweep пересоздаст при нужде.
        int currentJobId = pool.CurrentJobId[agentIndex];
        if (currentJobId != -1)
        {
            JobDispatcher.Instance.JobIndex.MarkJobUnreachable(currentJobId, 5000);
            JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(currentJobId);
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
        }
        pool.States[agentIndex] = AgentState.Idle;
        pool.JobSearchTimer[agentIndex] = 4.0f + (float)ParallelRng.NextDouble() * 4.0f;
        JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
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
                int sx = pool.SourceCellX[agentIndex];
                int sy = pool.SourceCellY[agentIndex];
                int reserved = pool.ReservedItemCount[agentIndex];
                ItemId want = GroundItemManager.Instance.PeekItemAt(sx, sy);
                int taken = GroundItemManager.Instance.TakeItems(sx, sy, reserved);
                if (taken > 0)
                {
                    // Тип берём из факта кучи, а не захардкоженный Log:
                    // иначе зерно доезжало как бревно и терялось/дублировалось.
                    pool.CarriedItemId[agentIndex] = want != ItemId.None ? want : ItemId.Log;
                    pool.CarriedItemCount[agentIndex] = taken;
                    pool.States[agentIndex] = AgentState.MovingToTarget;
                    pool.TargetPositionX[agentIndex] = pool.TargetCellX[agentIndex] * ctx.TileSize + 32f;
                    pool.TargetPositionY[agentIndex] = pool.TargetCellY[agentIndex] * ctx.TileSize + 32f;
                }
                else
                {
                    // Куча разобрана/пуста — транзиентно: работу оставляем,
                    // Sweep/Spawn пересоздадут при появлении предметов.
                    JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
                }
            }
            else if (state == AgentState.MovingToTarget)
            {
                int sx = pool.TargetCellX[agentIndex];
                int sy = pool.TargetCellY[agentIndex];

                if (StockpileManager.Instance.IsZoneTile(sx, sy))
                {
                    // Items go ONLY into the stockpile storage (DepositItems).
                    // A duplicate SpawnItems used to create copies on the ground.
                    StockpileManager.Instance.DepositItems(sx, sy, pool.CarriedItemId[agentIndex], pool.CarriedItemCount[agentIndex]);
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
                    // Одноразовая работа: удаляем. Гард в ReleaseWorkerClaim
                    // делает последующий ReleaseJobWorker безопасным
                    // (ReleaseWorkerClaim no-op'ится по !_active).
                    JobDispatcher.Instance.TryUnregisterJob(jobId);
                }
                JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
            }
        }
        else if (pool.StuckTimer[agentIndex] >= MaxStuckDuration)
        {
            // Не донёс: резервы висят на слоте склада И на куче земли —
            // отменяем оба (в MovingToSource carry ещё нет, но есть ground-резерв).
            // OnCancel НЕ вызываем напрямую: ReleaseJobWorker сам его вызовет.
            int jobId = pool.CurrentJobId[agentIndex];
            if (jobId != -1)
            {
                JobDispatcher.Instance.JobIndex.MarkJobUnreachable(jobId, 3000);
            }
            // Сбрасываем StuckTimer до Release: OnCancel/Release не должны
            // видеть протухший таймер при следующем назначении.
            pool.StuckTimer[agentIndex] = 0f;
            JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
            pool.JobSearchTimer[agentIndex] = 10.0f + (float)ParallelRng.NextDouble() * 10.0f;
        }
    }

    public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        // OnCancel НЕ удаляет работу: отмена агента (stuck/evacuate/reassign) —
        // не смерть задачи. Удаление делало «мёртвые» задачи при массовой отмене,
        // а пересоздание затем сбрасывало кулдаун (эффект «удалил/выделил»).
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