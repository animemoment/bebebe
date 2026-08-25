using Godot;
using System.Collections.Generic;

namespace Game.Core;

public static class WallTileHelper
{
    private static readonly Vector2I[] TileMap = new Vector2I[16]
    {
        new(2, 0), // 0  : None (Одиночный)
        new(1, 3), // 1  : Up
        new(2, 3), // 2  : Right
        new(1, 1), // 3  : Up + Right
        new(0, 3), // 4  : Down
        new(0, 0), // 5  : Up + Down
        new(3, 0), // 6  : Down + Right
        new(0, 2), // 7  : Up + Down + Right
        new(3, 3), // 8  : Left
        new(2, 1), // 9  : Up + Left
        new(1, 0), // 10 : Left + Right
        new(2, 2), // 11 : Up + Left + Right
        new(0, 1), // 12 : Down + Left
        new(1, 2), // 13 : Up + Down + Left
        new(3, 2), // 14 : Down + Left + Right
        new(3, 1)  // 15 : Up + Down + Left + Right
    };

    public static Vector2I GetTile((int X, int Y) pos, HashSet<(int X, int Y)> walls)
    {
        int mask = 0;
        int x = pos.X;
        int y = pos.Y;

        if (walls.Contains((x, y - 1))) mask |= 1; // Up
        if (walls.Contains((x + 1, y))) mask |= 2; // Right
        if (walls.Contains((x, y + 1))) mask |= 4; // Down
        if (walls.Contains((x - 1, y))) mask |= 8; // Left

        return TileMap[mask];
    }
}