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

                bool reachedWp = MoveToPoint(agentIndex, new Vector2(wpX, wpY), 16f, deltaTime, pool, ctx);

                if (reachedWp)
                {
                    // Пропускаем цепочку близких точек сразу: иначе агент
                    // топчется в середине пути (пошаговая остановка на каждой
                    // точке сглаженного пути давала осцилляцию туда-обратно).
                    int next = idx + 1;
                    int total = pool.WaypointCount[agentIndex];
                    int wpBase2 = agentIndex * AgentPathConfig.MaxWaypoints;
                    float px = pool.PositionX[agentIndex];
                    float py = pool.PositionY[agentIndex];
                    while (next < total)
                    {
                        float nx = pool.WaypointX[wpBase2 + next] * ctx.TileSize + 32f;
                        float ny = pool.WaypointY[wpBase2 + next] * ctx.TileSize + 32f;
                        float ddx = nx - px;
                        float ddy = ny - py;
                        if (ddx * ddx + ddy * ddy > 24f * 24f)
                            break;
                        next++;
                    }
                    pool.WaypointIndex[agentIndex] = (byte)next;
                    pool.StuckTimer[agentIndex] = 0f;
                    if (pool.WaypointIndex[agentIndex] >= pool.WaypointCount[agentIndex])
                    {
                        pool.WaypointCount[agentIndex] = 0;
                        pool.WaypointIndex[agentIndex] = 0;
                        // Цепочка завершена этим тиком — проваливаемся в фазу 2
                        // сразу (иначе агент стоит один лишний тик: return false ниже).
                        goto Phase2;
                    }
                }
                else if (pool.StuckTimer[agentIndex] >= 1.5f)
                {
                    // Путь устарел (стена/толпа): отбрасываем и пересчитаем —
                    // ниже сработает прямой fallback + запрос нового пути.
                    pool.WaypointCount[agentIndex] = 0;
                    pool.WaypointIndex[agentIndex] = 0;
                    // Waypoints сброшены — сразу прямое движение этим тиком.
                    goto Phase2;
                }
                return false;
            }
        }

    Phase2:
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
                // P1 (Phase2-пик): stagger запросов — не все агенты в один тик.
                // Кулдаун и так 1.5с, но тысячи агентов выходят из него синхронно
                // (назначены одним диспатчем) и хором бьют в A*. Разносим по
                // остатку индекса: ~1/4 агентов за тик вместо всех сразу.
                // Джиттер кулдауна при спавне (0..1.5с) не даёт вечной фазовой
                // синхронизации одних и тех же агентов.
                uint gate = Game.Simulation.AgentSimTickGate.Current;
                if ((((uint)agentIndex ^ ((uint)agentIndex >> 4)) & 3u) != (gate & 3u))
                    return reached;
                RequestHierarchicalPath(agentIndex, target, pool, ctx);
            }
        }

        return reached;
    }

    /// <summary>
    /// Запрашивает иерархический путь и кладёт waypoints в пул агента.
    /// Частота — не чаще раза в 0.8-1.5 игровых секунды.
    /// B21/B22: счётчики запросов/успехов + отдельный замер A* (не движения).
    /// </summary>
    private void RequestHierarchicalPath(int agentIndex, Vector2 target, AgentDataPool pool, SimulationContext ctx)
    {
        // P0-1: без счётчика — кулдаун-хит случается на каждого агента каждый тик.
        if (pool.PathRequestCooldown[agentIndex] > 0f)
        {
            return;
        }

        int sx = (int)pool.PositionX[agentIndex] >> TileShift;
        int sy = (int)pool.PositionY[agentIndex] >> TileShift;
        int tx = (int)target.X >> TileShift;
        int ty = (int)target.Y >> TileShift;

        if (sx == tx && sy == ty)
            return;

        // P0-1: без Stopwatch/счётчиков на запрос — A* идёт из тысяч агентов,
        // каждый Count = lock(_cLock). Тяжёлые пути видны по кванту Phase2.
        bool ok = HierarchicalPathfinder.Instance.TryFindPath(sx, sy, tx, ty, out int[] path, out int count);
        if (ok)
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

        // Крупный dt (0.5 на 100x) давал шаг 60px за суб-степ: агент
        // перепрыгивал через цель и возвращался — пинг-понг у финиша.
        // Режем на суб-шаги ≤16px, каждый со своим arrive-капом.
        float totalStep = speed * deltaTime;
        int subSteps = (int)(totalStep / 16f) + 1;
        if (subSteps > 4) subSteps = 4;
        if (subSteps < 1) subSteps = 1;
        float subDt = deltaTime / subSteps;
        bool anyMoved = false;

        for (int s = 0; s < subSteps; s++)
        {
            float sx = pool.PositionX[agentIndex];
            float sy = pool.PositionY[agentIndex];
            float sdx = target.X - sx;
            float sdy = target.Y - sy;
            float sDistSq = sdx * sdx + sdy * sdy;
            if (sDistSq <= reachDistance * reachDistance)
                return true;
            float sDist = MathF.Sqrt(sDistSq);
            float invDist = 1.0f / sDist;
            float stepLen = speed * subDt;
            if (stepLen >= sDist)
            {
                // Дошли бы до цели за этот суб-шаг: ставим точно в цель,
                // дёрганья из-за перепрыгивания мимо нет.
                if (!IsTileBlocked(target.X, target.Y, ctx))
                {
                    pool.PositionX[agentIndex] = target.X;
                    pool.PositionY[agentIndex] = target.Y;
                    pool.StuckTimer[agentIndex] = 0f;
                    return true;
                }
                // Цель внутри стены (редкий stand-кейс): считаем дошедшим,
                // коммит-фаза перепроверит дистанцию сама.
                return sDistSq <= (reachDistance + 6f) * (reachDistance + 6f);
            }
            float stepX = sdx * invDist * stepLen;
            float stepY = sdy * invDist * stepLen;

            float desiredX = sx + stepX;
            float desiredY = sy + stepY;

            // 1. Прямой шаг
            if (!IsTileBlocked(desiredX, desiredY, ctx))
            {
                pool.PositionX[agentIndex] = desiredX;
                pool.PositionY[agentIndex] = desiredY;
                anyMoved = true;
                continue;
            }

            // 2. Скольжение вдоль препятствий — детерминированно по
            // доминирующей оси (не минимум остатка: он давал чередование
            // X/Y каждый кадр = дрожание у стены и осцилляцию в углу).
            bool canX = !IsTileBlocked(desiredX, sy, ctx);
            bool canY = !IsTileBlocked(sx, desiredY, ctx);

            if (canX && canY)
            {
                if (MathF.Abs(sdx) >= MathF.Abs(sdy))
                {
                    pool.PositionX[agentIndex] = desiredX;
                    pool.PositionY[agentIndex] = sy;
                }
                else
                {
                    pool.PositionX[agentIndex] = sx;
                    pool.PositionY[agentIndex] = desiredY;
                }
                anyMoved = true;
                continue;
            }
            else if (canX)
            {
                pool.PositionX[agentIndex] = desiredX;
                pool.PositionY[agentIndex] = sy;
                anyMoved = true;
                continue;
            }
            else if (canY)
            {
                pool.PositionX[agentIndex] = sx;
                pool.PositionY[agentIndex] = desiredY;
                anyMoved = true;
                continue;
            }
            else
            {
                // Суб-шаг упёрся: дальше только обход угла (ниже).
                break;
            }
        }

        if (anyMoved)
        {
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

        // Каменная россыпь проходима: агенты ходят прямо по ней.
        // Добытчик всё равно работает с соседней stand-клетки (Mining).

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