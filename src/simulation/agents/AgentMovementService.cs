using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Game.Core;

namespace Game.Simulation;

public sealed class AgentMovementService
{
    private const float MoveSpeed = 120.0f;
    private const float WaterSpeedMultiplier = 0.45f;
    private const float AgentRadius = 8.0f;
    private const int TileShift = 6; // 1 << 6 = 64 (TileSize)

    private static readonly (int dx, int dy)[] PushAwayNeighbors = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>
    /// Движение агента к цели с поддержкой иерархического поиска пути.
    /// Если у агента активны путевые точки (HierarchicalPathfinder) — движется
    /// по ним; иначе — напрямую (со скольжением и локальным обходом), а при
    /// большой дистанции запрашивает новый иерархический путь.
    /// Возвращает true, когда агент достиг цели в пределах reachDistance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveTowards(int agentIndex, Vector2 target, float reachDistance, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        // Кулдаун повторных запросов пути (игровые секунды).
        float cd = pool.PathRequestCooldown[agentIndex];
        if (cd > 0f)
        {
            pool.PathRequestCooldown[agentIndex] = Math.Max(0f, cd - deltaTime);
        }

        // ---- Фаза 1: следование по waypoints ----
        if (pool.WaypointCount[agentIndex] > 0)
        {
            int idx = pool.WaypointIndex[agentIndex];
            if (idx >= pool.WaypointCount[agentIndex])
            {
                pool.WaypointCount[agentIndex] = 0;
                pool.WaypointIndex[agentIndex] = 0;
            }
            else
            {
                int wpBase = agentIndex * AgentPathConfig.MaxWaypoints;
                float wpX = pool.WaypointX[wpBase + idx] * ctx.TileSize + 32f;
                float wpY = pool.WaypointY[wpBase + idx] * ctx.TileSize + 32f;

                bool reachedWp = MoveToPoint(agentIndex, new Vector2(wpX, wpY), 40f, deltaTime, pool, ctx);

                if (reachedWp)
                {
                    pool.WaypointIndex[agentIndex] = (byte)(idx + 1);
                    pool.StuckTimer[agentIndex] = 0f;
                    if (pool.WaypointIndex[agentIndex] >= pool.WaypointCount[agentIndex])
                    {
                        pool.WaypointCount[agentIndex] = 0;
                        pool.WaypointIndex[agentIndex] = 0;
                    }
                }
                else if (pool.StuckTimer[agentIndex] >= 1.5f)
                {
                    // Путь устарел (стена/толпа): отбрасываем и пересчитаем —
                    // ниже сработает прямой fallback + запрос нового пути.
                    pool.WaypointCount[agentIndex] = 0;
                    pool.WaypointIndex[agentIndex] = 0;
                }
                return false;
            }
        }

        // ---- Фаза 2: прямое движение (прежнее поведение) ----
        bool reached = MoveToPoint(agentIndex, target, reachDistance, deltaTime, pool, ctx);

        // ---- Фаза 3: запрос иерархического пути для дальних маршрутов ----
        if (!reached && pool.WaypointCount[agentIndex] == 0)
        {
            float dx = target.X - pool.PositionX[agentIndex];
            float dy = target.Y - pool.PositionY[agentIndex];
            float minDist = 4.0f * ctx.TileSize;
            if (dx * dx + dy * dy >= minDist * minDist)
            {
                RequestHierarchicalPath(agentIndex, target, pool, ctx);
            }
        }

        return reached;
    }

    /// <summary>
    /// Запрашивает иерархический путь и кладёт waypoints в пул агента.
    /// Частота — не чаще раза в 0.8-1.5 игровых секунды.
    /// </summary>
    private void RequestHierarchicalPath(int agentIndex, Vector2 target, AgentDataPool pool, SimulationContext ctx)
    {
        if (pool.PathRequestCooldown[agentIndex] > 0f)
            return;

        int sx = (int)pool.PositionX[agentIndex] >> TileShift;
        int sy = (int)pool.PositionY[agentIndex] >> TileShift;
        int tx = (int)target.X >> TileShift;
        int ty = (int)target.Y >> TileShift;

        if (sx == tx && sy == ty)
            return;

        if (HierarchicalPathfinder.Instance.TryFindPath(sx, sy, tx, ty, out int[] path, out int count))
        {
            if (count > 0)
            {
                int cap = Math.Min(count, AgentPathConfig.MaxWaypoints);
                int wpBase = agentIndex * AgentPathConfig.MaxWaypoints;
                for (int k = 0; k < cap; k++)
                {
                    int packed = path[k];
                    pool.WaypointX[wpBase + k] = (short)(packed % ctx.MapWidth);
                    pool.WaypointY[wpBase + k] = (short)(packed / ctx.MapWidth);
                }
                pool.WaypointCount[agentIndex] = (byte)cap;
                pool.WaypointIndex[agentIndex] = 0;
            }

            pool.PathRequestCooldown[agentIndex] = 1.5f;
        }
        else
        {
            // Не удалось построить (изолированный регион и т.п.) — retry чаще.
            pool.PathRequestCooldown[agentIndex] = 0.8f;
        }
    }

    /// <summary>
    /// Прямолинейное движение к точке: скольжение вдоль стен, локальный
    /// обход углов, детектор застревания.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MoveToPoint(int agentIndex, Vector2 target, float reachDistance, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        float curX = pool.PositionX[agentIndex];
        float curY = pool.PositionY[agentIndex];
        float dx = target.X - curX;
        float dy = target.Y - curY;
        float distSq = dx * dx + dy * dy;

        if (distSq <= reachDistance * reachDistance)
            return true;

        float dist = MathF.Sqrt(distSq);

        int curTx = (int)curX >> TileShift;
        int curTy = (int)curY >> TileShift;
        
        float speed = MoveSpeed;
        // Усталость > 80: агент еле волочит ноги (лёгкий NeedsJobSystem).
        if (pool.Fatigue[agentIndex] > AgentNeedsConfig.FatigueSlowThreshold)
            speed *= AgentNeedsConfig.FatigueSlowMultiplier;
        if ((uint)curTx < (uint)ctx.MapWidth && (uint)curTy < (uint)ctx.MapHeight && ctx.Ground[curTx, curTy] == TileType.Water)
        {
            speed *= WaterSpeedMultiplier;
        }

        float invDist = 1.0f / dist;
        float stepX = dx * invDist * (speed * deltaTime);
        float stepY = dy * invDist * (speed * deltaTime);
        float stepLenSq = stepX * stepX + stepY * stepY;
        if (stepLenSq > distSq) { stepX = dx; stepY = dy; }

        float desiredX = curX + stepX;
        float desiredY = curY + stepY;

        // 1. Прямой шаг
        if (!IsTileBlocked(desiredX, desiredY, ctx))
        {
            pool.PositionX[agentIndex] = desiredX;
            pool.PositionY[agentIndex] = desiredY;
            pool.StuckTimer[agentIndex] = 0f;
            return false;
        }

        // 2. Скольжение вдоль препятствий (проверяем X и Y отдельно)
        bool canX = !IsTileBlocked(desiredX, curY, ctx);
        bool canY = !IsTileBlocked(curX, desiredY, ctx);

        if (canX && canY)
        {
            float dX = (target.X - desiredX) * (target.X - desiredX) + (target.Y - curY) * (target.Y - curY);
            float dY = (target.X - curX) * (target.X - curX) + (target.Y - desiredY) * (target.Y - desiredY);
            if (dX < dY)
            {
                pool.PositionX[agentIndex] = desiredX;
                pool.PositionY[agentIndex] = curY;
            }
            else
            {
                pool.PositionX[agentIndex] = curX;
                pool.PositionY[agentIndex] = desiredY;
            }
            pool.StuckTimer[agentIndex] = 0f;
            return false;
        }
        else if (canX)
        {
            pool.PositionX[agentIndex] = desiredX;
            pool.PositionY[agentIndex] = curY;
            pool.StuckTimer[agentIndex] = 0f;
            return false;
        }
        else if (canY)
        {
            pool.PositionX[agentIndex] = curX;
            pool.PositionY[agentIndex] = desiredY;
            pool.StuckTimer[agentIndex] = 0f;
            return false;
        }

        // 3. Локальный обход углов (не чаще 1 раза в 2.0 секунды при застревании)
        float previousStuck = pool.StuckTimer[agentIndex];
        pool.StuckTimer[agentIndex] += deltaTime;
        float currentStuck = pool.StuckTimer[agentIndex];

        if (currentStuck >= 0.5f && (int)(previousStuck * 0.5f) != (int)(currentStuck * 0.5f))
        {
            int targetTileX = (int)target.X >> TileShift;
            int targetTileY = (int)target.Y >> TileShift;

            Vector2 detourDir = FlowFieldManager.Instance.GetDirection(curX, curY, targetTileX, targetTileY, ctx);
            if (detourDir != Vector2.Zero)
            {
                float detourX = curX + detourDir.X * (speed * deltaTime);
                float detourY = curY + detourDir.Y * (speed * deltaTime);
                if (!IsTileBlocked(detourX, detourY, ctx))
                {
                    pool.PositionX[agentIndex] = detourX;
                    pool.PositionY[agentIndex] = detourY;
                    return false;
                }
            }
        }

        float reachWithMargin = reachDistance + 6.0f;
        if (distSq <= reachWithMargin * reachWithMargin)
            return true;

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsTileBlocked(float worldX, float worldY, SimulationContext ctx)
    {
        int tx = (int)worldX >> TileShift;
        int ty = (int)worldY >> TileShift;

        // Быстрая проверка границ карты через беззнаковый int (отсекает < 0 и >= MapSize в одну инструкцию)
        if ((uint)tx >= (uint)ctx.MapWidth || (uint)ty >= (uint)ctx.MapHeight)
            return true;

        if (ctx.SolidWalls[tx, ty])
            return true;

        // Гора = стена (блокирует), а не вода (замедляет).
        if (ctx.Ground[tx, ty] == TileType.Mountain)
            return true;

        // Проверяем соседей только если агент подошел вплотную к краю тайла (быстрая маска 63)
        int subX = (int)worldX & 63;
        int subY = (int)worldY & 63;

        if (subX < AgentRadius && tx > 0 && (ctx.SolidWalls[tx - 1, ty] || ctx.Ground[tx - 1, ty] == TileType.Mountain)) return true;
        if (subX > 64 - AgentRadius && tx < ctx.MapWidth - 1 && (ctx.SolidWalls[tx + 1, ty] || ctx.Ground[tx + 1, ty] == TileType.Mountain)) return true;
        if (subY < AgentRadius && ty > 0 && (ctx.SolidWalls[tx, ty - 1] || ctx.Ground[tx, ty - 1] == TileType.Mountain)) return true;
        if (subY > 64 - AgentRadius && ty < ctx.MapHeight - 1 && (ctx.SolidWalls[tx, ty + 1] || ctx.Ground[tx, ty + 1] == TileType.Mountain)) return true;

        return false;
    }

    public void EjectFromWall(int agentIndex, int wallX, int wallY, AgentDataPool pool, SimulationContext ctx)
    {
        if (GridHelper.TryFindNearestFreeTile(wallX, wallY, ctx.Ground, ctx.SolidWalls, ctx.TreeOnGrass, 4, out var freeTile))
        {
            float px = (freeTile.X << TileShift) + 32f;
            float py = (freeTile.Y << TileShift) + 32f;
            pool.PositionX[agentIndex] = px;
            pool.PositionY[agentIndex] = py;
            pool.TargetPositionX[agentIndex] = px;
            pool.TargetPositionY[agentIndex] = py;
        }
    }

    public void PushAgentAwayFrom(int agentIndex, int fromX, int fromY, AgentDataPool pool, SimulationContext ctx)
    {
        // P0.4: без heap-alloc в горячем пути — направления как static readonly вместо new (int,int)[4].
        foreach (var (dx, dy) in PushAwayNeighbors)
        {
            int nx = fromX + dx;
            int ny = fromY + dy;
            if ((uint)nx < (uint)ctx.MapWidth && (uint)ny < (uint)ctx.MapHeight)
            {
                if (!ctx.SolidWalls[nx, ny] &&
                    !BlueprintManager.Instance.IsBlueprintAt(nx, ny) &&
                    !FarmJobManager.Instance.IsPlotMarked(nx, ny) &&
                    !ctx.SpatialGrid.IsCellOvercrowded(nx, ny, 5, pool))
                {
                    pool.States[agentIndex] = AgentState.Evacuating;
                    pool.TargetPositionX[agentIndex] = (nx << TileShift) + 32f;
                    pool.TargetPositionY[agentIndex] = (ny << TileShift) + 32f;
                    return;
                }
            }
        }
    }
}