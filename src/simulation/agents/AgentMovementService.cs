using System;
using System.Numerics;
using Game.Core;

namespace Game.Simulation;

public sealed class AgentMovementService
{
    private const float MoveSpeed = 120.0f;
    private const float WaterSpeedMultiplier = 0.45f;
    private const float AgentRadius = 8.0f;

    public bool MoveTowards(int agentIndex, Vector2 target, float reachDistance, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        using (GameProfiler.Scope())
        {
            Vector2 current = pool.GetPosition(agentIndex);
            Vector2 directVec = target - current;
            float dist = directVec.Length();

            if (dist <= reachDistance)
                return true;

            int curTx = (int)(current.X / ctx.TileSize);
            int curTy = (int)(current.Y / ctx.TileSize);
            float speed = MoveSpeed;
            if (curTx >= 0 && curTy >= 0 && curTx < ctx.MapWidth && curTy < ctx.MapHeight && ctx.Ground[curTx, curTy] == TileType.Water)
            {
                speed *= WaterSpeedMultiplier;
            }

            Vector2 dir = Vector2.Normalize(directVec);
            Vector2 step = dir * (speed * deltaTime);
            if (step.Length() > dist) step = directVec;

            Vector2 desiredPos = current + step;

            // 1. Прямой шаг
            if (!IsTileBlocked(desiredPos.X, desiredPos.Y, ctx))
            {
                pool.SetPosition(agentIndex, desiredPos);
                pool.StuckTimer[agentIndex] = 0f;
                return false;
            }

            // 2. Скольжение вдоль препятствий
            Vector2 tryX = new(desiredPos.X, current.Y);
            bool canX = !IsTileBlocked(tryX.X, tryX.Y, ctx);

            Vector2 tryY = new(current.X, desiredPos.Y);
            bool canY = !IsTileBlocked(tryY.X, tryY.Y, ctx);

            if (canX && canY)
            {
                float dX = (target - tryX).LengthSquared();
                float dY = (target - tryY).LengthSquared();
                pool.SetPosition(agentIndex, dX < dY ? tryX : tryY);
                pool.StuckTimer[agentIndex] = 0f;
                return false;
            }
            else if (canX)
            {
                pool.SetPosition(agentIndex, tryX);
                pool.StuckTimer[agentIndex] = 0f;
                return false;
            }
            else if (canY)
            {
                pool.SetPosition(agentIndex, tryY);
                pool.StuckTimer[agentIndex] = 0f;
                return false;
            }

            // 3. Обход стен через локальное поле (не чаще 1 раза в 2.0 секунды)
            float previousStuck = pool.StuckTimer[agentIndex];
            pool.StuckTimer[agentIndex] += deltaTime;
            float currentStuck = pool.StuckTimer[agentIndex];

            if (currentStuck >= 0.5f && (int)(previousStuck / 2.0f) != (int)(currentStuck / 2.0f))
            {
                int targetTileX = (int)(target.X / ctx.TileSize);
                int targetTileY = (int)(target.Y / ctx.TileSize);

                Vector2 detourDir = FlowFieldManager.Instance.GetDirection(current.X, current.Y, targetTileX, targetTileY, ctx);
                if (detourDir != Vector2.Zero)
                {
                    Vector2 detourPos = current + detourDir * (speed * deltaTime);
                    if (!IsTileBlocked(detourPos.X, detourPos.Y, ctx))
                    {
                        pool.SetPosition(agentIndex, detourPos);
                        return false;
                    }
                }
            }

            if (dist <= reachDistance + 6.0f)
                return true;

            return false;
        }
    }

    public bool IsTileBlocked(float worldX, float worldY, SimulationContext ctx)
    {
        using (GameProfiler.Scope())
        {
            int mapWidth = ctx.MapWidth;
            int mapHeight = ctx.MapHeight;
            int tileSize = ctx.TileSize;

            int tx = (int)(worldX / tileSize);
            int ty = (int)(worldY / tileSize);

            if (tx < 0 || ty < 0 || tx >= mapWidth || ty >= mapHeight)
                return true;

            if (ctx.SolidWalls[tx, ty])
                return true;

            int tLeft  = (int)((worldX - AgentRadius) / tileSize);
            int tRight = (int)((worldX + AgentRadius) / tileSize);
            int tUp    = (int)((worldY - AgentRadius) / tileSize);
            int tDown  = (int)((worldY + AgentRadius) / tileSize);

            if (tLeft >= 0 && ctx.SolidWalls[tLeft, ty]) return true;
            if (tRight < mapWidth && ctx.SolidWalls[tRight, ty]) return true;
            if (tUp >= 0 && ctx.SolidWalls[tx, tUp]) return true;
            if (tDown < mapHeight && ctx.SolidWalls[tx, tDown]) return true;

            return false;
        }
    }

    public void EjectFromWall(int agentIndex, int wallX, int wallY, AgentDataPool pool, SimulationContext ctx)
    {
        using (GameProfiler.Scope())
        {
            if (GridHelper.TryFindNearestFreeTile(wallX, wallY, ctx.Ground, ctx.SolidWalls, ctx.TreeOnGrass, 4, out var freeTile))
            {
                pool.PositionX[agentIndex] = freeTile.X * ctx.TileSize + 32f;
                pool.PositionY[agentIndex] = freeTile.Y * ctx.TileSize + 32f;
                pool.TargetPositionX[agentIndex] = pool.PositionX[agentIndex];
                pool.TargetPositionY[agentIndex] = pool.PositionY[agentIndex];
            }
        }
    }

    public void PushAgentAwayFrom(int agentIndex, int fromX, int fromY, AgentDataPool pool, SimulationContext ctx)
    {
        using (GameProfiler.Scope())
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
                if (nx >= 0 && ny >= 0 && nx < ctx.MapWidth && ny < ctx.MapHeight)
                {
                    if (!ctx.SolidWalls[nx, ny] &&
                        !BlueprintManager.Instance.IsBlueprintAt(nx, ny) &&
                        !FarmJobManager.Instance.IsPlotMarked(nx, ny) &&
                        !ctx.SpatialGrid.IsCellOvercrowded(nx, ny, 5, pool))
                    {
                        pool.States[agentIndex] = AgentState.Evacuating;
                        pool.TargetPositionX[agentIndex] = nx * ctx.TileSize + 32f;
                        pool.TargetPositionY[agentIndex] = ny * ctx.TileSize + 32f;
                        return;
                    }
                }
            }
        }
    }
}