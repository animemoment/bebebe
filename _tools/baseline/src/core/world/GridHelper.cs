using System;

namespace Game.Core;

public static class GridHelper
{
    private static readonly (int X, int Y)[] CardinalNeighbors =
    {
        (0, -1),
        (0, 1),
        (-1, 0),
        (1, 0)
    };

    public static bool TryFindAdjacentWalkable(
        int tx, int ty,
        TileType[,] ground,
        bool[,] solidWalls,
        bool[,] treeOnGrass,
        out (int X, int Y) standPos)
    {
        return TryFindAdjacentWalkable(tx, ty, ground, solidWalls, treeOnGrass, null, out standPos);
    }

    /// <summary>
    /// Перегрузка с камнем: клетка стояния не должна быть занята ни деревом, ни россыпью.
    /// Саму клетку-цель (с камнем/деревом) рабочий не занимает — стоит рядом.
    /// stoneOnGrass может быть null (старые вызовы без камня).
    /// </summary>
    public static bool TryFindAdjacentWalkable(
        int tx, int ty,
        TileType[,] ground,
        bool[,] solidWalls,
        bool[,] treeOnGrass,
        bool[,] stoneOnGrass,
        out (int X, int Y) standPos)
    {
        int mapWidth = ground.GetLength(0);
        int mapHeight = ground.GetLength(1);

        foreach (var (dx, dy) in CardinalNeighbors)
        {
            int nx = tx + dx;
            int ny = ty + dy;

            if (nx >= 0 && ny >= 0 && nx < mapWidth && ny < mapHeight)
            {
                if (ground[nx, ny] == TileType.Grass && !solidWalls[nx, ny] && (treeOnGrass == null || !treeOnGrass[nx, ny]) && (stoneOnGrass == null || !stoneOnGrass[nx, ny]))
                {
                    standPos = (nx, ny);
                    return true;
                }
            }
        }

        standPos = (tx, ty);
        return false;
    }

    public static bool TryFindNearestFreeTile(
        int startX, int startY,
        TileType[,] ground,
        bool[,] solidWalls,
        bool[,] treeOnGrass,
        int maxRadius,
        out (int X, int Y) freeTile)
    {
        freeTile = (-1, -1);
        int mapWidth = ground.GetLength(0);
        int mapHeight = ground.GetLength(1);

        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                        continue;

                    int x = startX + dx;
                    int y = startY + dy;

                    if (x >= 0 && y >= 0 && x < mapWidth && y < mapHeight)
                    {
                        bool freeTree = treeOnGrass == null || !treeOnGrass[x, y];
                        if (ground[x, y] == TileType.Grass && !solidWalls[x, y] && freeTree)
                        {
                            freeTile = (x, y);
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }
}