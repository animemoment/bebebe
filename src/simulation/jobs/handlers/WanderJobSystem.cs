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
        if (ctx.Random.NextDouble() > 0.30)
        {
            pool.TargetPositionX[agentIndex] = pool.PositionX[agentIndex];
            pool.TargetPositionY[agentIndex] = pool.PositionY[agentIndex];
            return true;
        }

        int curX = (int)(pool.PositionX[agentIndex] / ctx.TileSize);
        int curY = (int)(pool.PositionY[agentIndex] / ctx.TileSize);

        int dx = ctx.Random.Next(-2, 3);
        int dy = ctx.Random.Next(-2, 3);
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
        Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);
        Vector2 current = pool.GetPosition(agentIndex);

        if ((target - current).LengthSquared() > 16.0f)
        {
            Vector2 dir = Vector2.Normalize(target - current);
            Vector2 step = dir * (60.0f * deltaTime);
            Vector2 next = current + step;

            if (!ctx.Movement.IsTileBlocked(next.X, next.Y, ctx))
            {
                pool.SetPosition(agentIndex, next);
            }
            else
            {
                pool.TargetPositionX[agentIndex] = current.X;
                pool.TargetPositionY[agentIndex] = current.Y;
            }
        }
    }

    public void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);
        if ((pool.GetPosition(agentIndex) - target).LengthSquared() <= 16.0f)
        {
            if (pool.States[agentIndex] == AgentState.Evacuating)
            {
                pool.States[agentIndex] = AgentState.Idle;
                JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
            }
        }
    }
}