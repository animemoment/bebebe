using System;
using System.Numerics;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

public sealed class FlowFieldManager
{
    public static FlowFieldManager Instance { get; } = new();

    private const int LocalWindowRadius = 16;
    private const int WindowSize = LocalWindowRadius * 2 + 1;
    private const int TotalWindowCells = WindowSize * WindowSize;

    private static readonly (int dx, int dy)[] Neighbors =
    {
        (0, -1), (0, 1), (-1, 0), (1, 0),
        (-1, -1), (1, -1), (-1, 1), (1, 1)
    };

    private sealed class SearchBuffers
    {
        public readonly int[] DistanceField = new int[TotalWindowCells];
        public readonly (int X, int Y)[] Queue = new (int X, int Y)[TotalWindowCells];
    }

    private readonly ThreadLocal<SearchBuffers> _buffers = new(() => new SearchBuffers());

    public void ClearCache() { }

    public Vector2 GetDirection(float worldX, float worldY, int targetTileX, int targetTileY, SimulationContext ctx)
    {
        using (GameProfiler.Scope())
        {
            int startTileX = (int)(worldX / ctx.TileSize);
            int startTileY = (int)(worldY / ctx.TileSize);

            if (startTileX == targetTileX && startTileY == targetTileY)
            {
                Vector2 targetWorld = new(targetTileX * ctx.TileSize + 32f, targetTileY * ctx.TileSize + 32f);
                Vector2 toTarget = targetWorld - new Vector2(worldX, worldY);
                return toTarget.LengthSquared() > 0.001f ? Vector2.Normalize(toTarget) : Vector2.Zero;
            }

            return CalculateLocalDetourDirection(startTileX, startTileY, targetTileX, targetTileY, ctx);
        }
    }

    private Vector2 CalculateLocalDetourDirection(int startX, int startY, int targetX, int targetY, SimulationContext ctx)
    {
        using (GameProfiler.Scope())
        {
            var buffers = _buffers.Value;
            int[] distanceField = buffers.DistanceField;
            (int X, int Y)[] queue = buffers.Queue;

            int minX = startX - LocalWindowRadius;
            int minY = startY - LocalWindowRadius;

            Array.Fill(distanceField, int.MaxValue);

            int clampedTargetX = Math.Clamp(targetX, minX, startX + LocalWindowRadius);
            int clampedTargetY = Math.Clamp(targetY, minY, startY + LocalWindowRadius);

            int localTargetX = clampedTargetX - minX;
            int localTargetY = clampedTargetY - minY;
            int targetIdx = localTargetY * WindowSize + localTargetX;

            distanceField[targetIdx] = 0;

            int head = 0;
            int tail = 0;
            queue[tail++] = (clampedTargetX, clampedTargetY);

            int maxSearchSteps = 100;
            int stepsTaken = 0;

            while (head < tail && stepsTaken < maxSearchSteps)
            {
                var (cx, cy) = queue[head++];
                stepsTaken++;

                int localCy = cy - minY;
                int localCx = cx - minX;
                int curDist = distanceField[localCy * WindowSize + localCx];

                if (cx == startX && cy == startY)
                {
                    break;
                }

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + Neighbors[i].dx;
                    int ny = cy + Neighbors[i].dy;

                    if (nx >= 0 && ny >= 0 && nx < ctx.MapWidth && ny < ctx.MapHeight &&
                        nx >= minX && nx < minX + WindowSize && ny >= minY && ny < minY + WindowSize)
                    {
                        if (ctx.Ground[nx, ny] == TileType.Grass && !ctx.SolidWalls[nx, ny])
                        {
                            int localNy = ny - minY;
                            int localNx = nx - minX;
                            int nIdx = localNy * WindowSize + localNx;

                            if (distanceField[nIdx] > curDist + 1)
                            {
                                distanceField[nIdx] = curDist + 1;
                                queue[tail++] = (nx, ny);
                            }
                        }
                    }
                }
            }

            int bestDist = int.MaxValue;
            Vector2 bestDir = Vector2.Zero;

            for (int i = 0; i < Neighbors.Length; i++)
            {
                int nx = startX + Neighbors[i].dx;
                int ny = startY + Neighbors[i].dy;

                if (nx >= 0 && ny >= 0 && nx < ctx.MapWidth && ny < ctx.MapHeight &&
                    nx >= minX && nx < minX + WindowSize && ny >= minY && ny < minY + WindowSize)
                {
                    if (ctx.Ground[nx, ny] == TileType.Grass && !ctx.SolidWalls[nx, ny])
                    {
                        int nIdx = (ny - minY) * WindowSize + (nx - minX);
                        int dist = distanceField[nIdx];

                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            Vector2 stepVec = new(Neighbors[i].dx, Neighbors[i].dy);
                            bestDir = Vector2.Normalize(stepVec);
                        }
                    }
                }
            }

            return bestDir;
        }
    }
}