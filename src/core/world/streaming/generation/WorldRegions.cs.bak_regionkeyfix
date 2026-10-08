using System;

namespace Game.Core.WorldStreaming;

public static class WorldRegions
{
    public const int RegionSize = 512;

    public const long WorldRegionsX = 2048;
    public const long WorldRegionsY = 1024;

    public const int ChunkCells = 64;
    public const int TilesPerRegionAxis = RegionSize / ChunkCells;

    private static long FloorDiv(long x, long y)
    {
        long q = x / y;
        long r = x % y;

        if (r != 0 && ((r < 0) != (y < 0)))
            q--;

        return q;
    }

    private static long FloorMod(long x, long y)
    {
        return x - FloorDiv(x, y) * y;
    }

    public static RegionKey RegionForCell(long worldX, long worldY)
        => new(FloorDiv(worldX, RegionSize), FloorDiv(worldY, RegionSize));

    public static (int X, int Y) LocalForCell(long worldX, long worldY)
        => ((int)FloorMod(worldX, RegionSize), (int)FloorMod(worldY, RegionSize));

    public static WorldCell RegionOrigin(RegionKey region)
        => new(checked(region.X * RegionSize), checked(region.Y * RegionSize));

    public static WorldRect RegionBounds(RegionKey region)
    {
        WorldCell origin = RegionOrigin(region);

        return new WorldRect(
            origin.X,
            origin.Y,
            checked(origin.X + RegionSize),
            checked(origin.Y + RegionSize));
    }

    public static bool IsInsideWorld(RegionKey region)
        => region.X >= 0 && region.X < WorldRegionsX &&
           region.Y >= 0 && region.Y < WorldRegionsY;
}