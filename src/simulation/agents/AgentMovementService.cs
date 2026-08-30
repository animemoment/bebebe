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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveTowards(int agentIndex, Vector2 target, float reachDistance, float deltaTime, AgentDataPool pool, SimulationContext ctx)
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

        // Проверяем соседей только если агент подошел вплотную к краю тайла (быстрая маска 63)
        int subX = (int)worldX & 63;
        int subY = (int)worldY & 63;

        if (subX < AgentRadius && tx > 0 && ctx.SolidWalls[tx - 1, ty]) return true;
        if (subX > 64 - AgentRadius && tx < ctx.MapWidth - 1 && ctx.SolidWalls[tx + 1, ty]) return true;
        if (subY < AgentRadius && ty > 0 && ctx.SolidWalls[tx, ty - 1]) return true;
        if (subY > 64 - AgentRadius && ty < ctx.MapHeight - 1 && ctx.SolidWalls[tx, ty + 1]) return true;

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
        (int X, int Y)[] neighbors =
        {
            (fromX + 1, fromY),
            (fromX - 1, fromY),
            (fromX, fromY + 1),
            (fromX, fromY - 1)
        };

        foreach (var (nx, ny) in neighbors)
        {
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