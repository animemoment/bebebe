using System;
using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

/// <summary>
/// Система блуждания и эвакуации для свободных агентов с экономией тактов CPU.
/// </summary>
public sealed class WanderJobSystem
{
    public bool TryAssignJob(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        // 70% шанс остаться на месте и не расходовать вычисления коллизий
        if (Random.Shared.NextDouble() > 0.30)
        {
            pool.TargetPositionX[agentIndex] = pool.PositionX[agentIndex];
            pool.TargetPositionY[agentIndex] = pool.PositionY[agentIndex];
            return true;
        }

        int curX = (int)(pool.PositionX[agentIndex] / ctx.TileSize);
        int curY = (int)(pool.PositionY[agentIndex] / ctx.TileSize);

        int dx = Random.Shared.Next(-2, 3);
        int dy = Random.Shared.Next(-2, 3);
        int tx = curX + dx;
        int ty = curY + dy;

        if (tx >= 0 && ty >= 0 && tx < ctx.MapWidth && ty < ctx.MapHeight)
        {
            if (!ctx.SolidWalls[tx, ty] && !ctx.TreeOnGrass[tx, ty])
            {
                pool.TargetPositionX[agentIndex] = tx * ctx.TileSize + 32f;
                pool.TargetPositionY[agentIndex] = ty * ctx.TileSize + 32f;
            }
            else
            {
                pool.TargetPositionX[agentIndex] = pool.PositionX[agentIndex];
                pool.TargetPositionY[agentIndex] = pool.PositionY[agentIndex];
            }
        }

        return true;
    }

    public void ExecuteParallel(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        float targetX = pool.TargetPositionX[agentIndex];
        float targetY = pool.TargetPositionY[agentIndex];
        float curX = pool.PositionX[agentIndex];
        float curY = pool.PositionY[agentIndex];

        float dx = targetX - curX;
        float dy = targetY - curY;
        float distSq = dx * dx + dy * dy;

        if (distSq > 16.0f)
        {
            float invDist = 1.0f / MathF.Sqrt(distSq);
            float stepX = dx * invDist * (60.0f * deltaTime);
            float stepY = dy * invDist * (60.0f * deltaTime);
            float nextX = curX + stepX;
            float nextY = curY + stepY;

            if (!ctx.Movement.IsTileBlocked(nextX, nextY, ctx))
            {
                pool.PositionX[agentIndex] = nextX;
                pool.PositionY[agentIndex] = nextY;
            }
            else
            {
                pool.TargetPositionX[agentIndex] = curX;
                pool.TargetPositionY[agentIndex] = curY;
            }
        }
    }

    public void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        float dx = pool.PositionX[agentIndex] - pool.TargetPositionX[agentIndex];
        float dy = pool.PositionY[agentIndex] - pool.TargetPositionY[agentIndex];
        if (dx * dx + dy * dy <= 16.0f)
        {
            if (pool.States[agentIndex] == AgentState.Evacuating)
            {
                pool.States[agentIndex] = AgentState.Idle;
                JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
            }
        }
    }
}