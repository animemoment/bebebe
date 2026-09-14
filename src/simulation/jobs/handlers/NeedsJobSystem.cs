using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Game.Core;

namespace Game.Simulation.Jobs;

/// <summary>
/// Лёгкая система поведенческих реакций на потребности (без новых JobType).
/// Только свободные агенты (Idle): при превышении порогов переводит агента в
/// Evacuating + NeedBehavior и ведёт через AgentMovementService.MoveTowards.
/// Диспетчер Evacuating игнорирует, агент не будет отобран на работу.
/// </summary>
public sealed class NeedsJobSystem
{
    private const float ReachDist = 48.0f;

    // P0.5: ThreadLocal RNG для Parallel-путей (Random.Shared contention, ctx.Random race).
    private static readonly System.Threading.ThreadLocal<Random> ThreadRandom = new(
        () => new Random(System.Environment.TickCount ^ System.Threading.Thread.CurrentThread.ManagedThreadId * 7919));

    /// <summary>
    /// Проверка триггеров для свободного агента (тайм-слайсинг 25% как поиск работы).
    /// Приоритет: голод &gt; сон/усталость &gt; окружение.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAssignNeedsBehavior(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        if (pool.NeedsBehavior[agentIndex] != NeedBehavior.None)
            return true;
        if (pool.States[agentIndex] != AgentState.Idle)
            return false;

        if (pool.Hunger[agentIndex] > AgentNeedsConfig.HungerSeekThreshold)
        {
            if (TryStartSeekingFood(agentIndex, pool, ctx))
                return true;
        }

        if (pool.Sleep[agentIndex] > AgentNeedsConfig.SleepRestThreshold ||
            pool.Fatigue[agentIndex] > AgentNeedsConfig.FatigueRestThreshold)
        {
            StartResting(agentIndex, pool);
            return true;
        }

        if (pool.EnvironmentSatisfaction[agentIndex] < AgentNeedsConfig.EnvironmentMigrateThreshold)
        {
            StartMigrating(agentIndex, pool, ctx);
            return true;
        }

        return false;
    }

    /// <summary>Ведёт агента с нуждой: движение + поедание/отдых/миграция.</summary>
    public void ExecuteParallel(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        var behavior = pool.NeedsBehavior[agentIndex];
        if (behavior == NeedBehavior.None)
            return;

        if (behavior == NeedBehavior.Resting)
        {
            CommitResting(agentIndex, deltaTime, pool);
            return;
        }

        var target = new Vector2(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);
        bool reached = ctx.Movement.MoveTowards(agentIndex, target, ReachDist, deltaTime, pool, ctx);
        if (!reached && pool.StuckTimer[agentIndex] >= 3.0f)
        {
            // Застрял по пути к еде: возвращаем резерв кучи, иначе зерно
            // навсегда вычитается из доступных (утечка _totalAvailableByType).
            if (behavior == NeedBehavior.SeekingFood && pool.ReservedItemCount[agentIndex] > 0)
            {
                GroundItemManager.Instance.ReleaseReservation(
                    pool.SourceCellX[agentIndex], pool.SourceCellY[agentIndex],
                    pool.ReservedItemCount[agentIndex]);
                pool.ReservedItemCount[agentIndex] = 0;
                pool.SourceCellX[agentIndex] = 0;
                pool.SourceCellY[agentIndex] = 0;
            }
            pool.StuckTimer[agentIndex] = 0f;
            FinishBehavior(agentIndex, pool, cooldownSec: 4f);
            return;
        }
        if (!reached)
            return;

        if (behavior == NeedBehavior.SeekingFood)
            CommitEating(agentIndex, pool);
        else if (behavior == NeedBehavior.Migrating)
            FinishBehavior(agentIndex, pool, cooldownSec: 2f);
    }

    /// <summary>Добор: достижение цели эвакуационной ходьбы (вызов из bookkeeping).</summary>
    public void Commit(int agentIndex, AgentDataPool pool)
    {
        if (pool.NeedsBehavior[agentIndex] == NeedBehavior.None)
            return;
        float dx = pool.PositionX[agentIndex] - pool.TargetPositionX[agentIndex];
        float dy = pool.PositionY[agentIndex] - pool.TargetPositionY[agentIndex];
        if (dx * dx + dy * dy > 16.0f)
            return;
        if (pool.NeedsBehavior[agentIndex] == NeedBehavior.SeekingFood)
            CommitEating(agentIndex, pool);
        else
            FinishBehavior(agentIndex, pool, cooldownSec: 2f);
    }

    private static bool TryStartSeekingFood(int a, AgentDataPool pool, SimulationContext ctx)
    {
        var pos = pool.GetPosition(a);
        // P0: радиус еды 40 тайлов = 3 чанка (было 32 = скан до 1024 чанков под lock).
        // Дальние клетки всё равно отбрасывались проверкой maxR ниже — теперь не сканируем их.
        // Резервируем сразу eatCount (2), без лишнего Reserve/Release на hot-path.
        float eatWeight = ItemRegistry.Get(ItemId.Grain).Weight * AgentNeedsConfig.FoodEatGrainCount;
        if (!GroundItemManager.Instance.TryReserveGroundItems(
                pos, eatWeight, allowFromStockpile: true, ItemId.Grain, maxChunkRadius: 3,
                out var cell, out var itemId, out int resCount))
        {
            // Еды нет (fast-fail по тоталам) или не найдена рядом: backoff 30-60с в отдельном
            // NeedsRetryTimer (не JobSearchTimer — его затирает блуждание 6с), иначе 10k голодных
            // молотят скан каждые 4 тика вечно. ParallelRng — lock-free (Random.Shared — contention).
            pool.NeedsRetryTimer[a] = 30.0f + (float)ParallelRng.NextDouble() * 30.0f;
            return false;
        }
        if (itemId != ItemId.Grain || resCount <= 0)
        {
            if (resCount > 0)
                GroundItemManager.Instance.ReleaseReservation(cell.X, cell.Y, resCount);
            pool.NeedsRetryTimer[a] = 30.0f + (float)ParallelRng.NextDouble() * 30.0f;
            return false;
        }
        int ax = (int)(pos.X / ctx.TileSize);
        int ay = (int)(pos.Y / ctx.TileSize);
        int maxR = AgentNeedsConfig.FoodSearchRadiusTiles;
        int ddx = cell.X - ax, ddy = cell.Y - ay;
        if (ddx * ddx + ddy * ddy > maxR * maxR)
        {
            GroundItemManager.Instance.ReleaseReservation(cell.X, cell.Y, resCount);
            pool.NeedsRetryTimer[a] = 30.0f + (float)ParallelRng.NextDouble() * 30.0f;
            return false;
        }
        // resCount уже == eatCount (резервировали точный вес), релиз излишка не нужен.
        int eatCount = resCount;
        JobDispatcher.Instance.IdleWorkers.RemoveIdleWorker(a, pool);
        pool.NeedsBehavior[a] = NeedBehavior.SeekingFood;
        pool.States[a] = AgentState.Evacuating;
        pool.SourceCellX[a] = cell.X;
        pool.SourceCellY[a] = cell.Y;
        pool.ReservedItemCount[a] = eatCount;
        pool.TargetPositionX[a] = cell.X * ctx.TileSize + 32f;
        pool.TargetPositionY[a] = cell.Y * ctx.TileSize + 32f;
        pool.StuckTimer[a] = 0f;
        pool.WaypointCount[a] = 0;
        pool.WaypointIndex[a] = 0;
        return true;
    }
    private static void StartResting(int a, AgentDataPool pool)
    {
        JobDispatcher.Instance.IdleWorkers.RemoveIdleWorker(a, pool);
        pool.NeedsBehavior[a] = NeedBehavior.Resting;
        pool.States[a] = AgentState.Evacuating;
        pool.TargetPositionX[a] = pool.PositionX[a];
        pool.TargetPositionY[a] = pool.PositionY[a];
        pool.StuckTimer[a] = 0f;
        pool.WaypointCount[a] = 0;
        pool.WaypointIndex[a] = 0;
    }

    private static void StartMigrating(int a, AgentDataPool pool, SimulationContext ctx)
    {
        int curX = (int)(pool.PositionX[a] / ctx.TileSize);
        int curY = (int)(pool.PositionY[a] / ctx.TileSize);
        int r = AgentNeedsConfig.MigrateRadiusTiles;
        // P0.5: ThreadLocal RNG вместо Random.Shared (внутр. lock → contention в Parallel)
        // и ctx.Random (не thread-safe → data race). Детерминизм сида в Parallel и так условен.
        var rng = ThreadRandom.Value;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int tx = curX + rng.Next(-r, r + 1);
            int ty = curY + rng.Next(-r, r + 1);
            if ((uint)tx >= (uint)ctx.MapWidth || (uint)ty >= (uint)ctx.MapHeight)
                continue;
            if (ctx.SolidWalls[tx, ty] || ctx.TreeOnGrass[tx, ty])
                continue;
            if (ctx.Ground[tx, ty] != TileType.Grass)
                continue;
            if (ctx.SpatialGrid.IsCellOvercrowded(tx, ty, 5, pool))
                continue;
            JobDispatcher.Instance.IdleWorkers.RemoveIdleWorker(a, pool);
            pool.NeedsBehavior[a] = NeedBehavior.Migrating;
            pool.States[a] = AgentState.Evacuating;
            pool.TargetCellX[a] = tx;
            pool.TargetCellY[a] = ty;
            pool.TargetPositionX[a] = tx * ctx.TileSize + 32f;
            pool.TargetPositionY[a] = ty * ctx.TileSize + 32f;
            pool.StuckTimer[a] = 0f;
            pool.WaypointCount[a] = 0;
            pool.WaypointIndex[a] = 0;
            return;
        }
        StartResting(a, pool);
    }

    private static void CommitEating(int a, AgentDataPool pool)
    {
        int taken = GroundItemManager.Instance.TakeItems(
            pool.SourceCellX[a], pool.SourceCellY[a], pool.ReservedItemCount[a]);
        if (taken > 0)
        {
            float h = pool.Hunger[a] - AgentNeedsConfig.FoodEatHungerRestore;
            pool.Hunger[a] = h < 0f ? 0f : h;
        }
        else
        {
            GroundItemManager.Instance.ReleaseReservation(
                pool.SourceCellX[a], pool.SourceCellY[a], pool.ReservedItemCount[a]);
        }
        pool.ReservedItemCount[a] = 0;
        pool.SourceCellX[a] = 0;
        pool.SourceCellY[a] = 0;
        FinishBehavior(a, pool, cooldownSec: 4f);
    }

    private static void CommitResting(int a, float dt, AgentDataPool pool)
    {
        float s = pool.Sleep[a] - AgentNeedsConfig.RestSleepRecoveryPerGameSec * dt;
        pool.Sleep[a] = s < 0f ? 0f : s;
        float f = pool.Fatigue[a] - AgentNeedsConfig.RestFatigueRecoveryPerGameSec * dt;
        pool.Fatigue[a] = f < 0f ? 0f : f;
        if (pool.Sleep[a] <= 5f && pool.Fatigue[a] <= 5f)
            FinishBehavior(a, pool, cooldownSec: 2f);
    }

    private static void FinishBehavior(int a, AgentDataPool pool, float cooldownSec)
    {
        pool.NeedsBehavior[a] = NeedBehavior.None;
        pool.States[a] = AgentState.Idle;
        pool.CurrentJobId[a] = -1;
        pool.CurrentJobType[a] = JobTypeId.None;
        pool.JobSearchTimer[a] = cooldownSec + (float)ThreadRandom.Value.NextDouble() * 2.0f;
        pool.WaypointCount[a] = 0;
        pool.WaypointIndex[a] = 0;
        JobDispatcher.Instance.IdleWorkers.AddIdleWorker(a, pool);
    }
}
