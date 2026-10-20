using System;
using Game.Core.WorldStreaming;

namespace Game.Core.WorldStreaming.Integration;

/// <summary>
/// Deterministic read-only sampling of the generated base world. These queries do not
/// require a resident chunk; they sample the generator at absolute signed coordinates.
/// </summary>
public static class WorldCellQuery
{
    /// <summary>
    /// Полная клетка одним семплом генератора. Нужна гейтам, которым требуются
    /// и террейн, и влажность/лес: два отдельных запроса — это два полных семпла.
    /// </summary>
    public static GeneratedCell CellAt(ulong seed, uint ver, long x, long y)
        => Sample(seed, ver, x, y);

    public static BaseTerrainKind TerrainAt(ulong seed, uint ver, long x, long y)
        => Sample(seed, ver, x, y).Terrain;

    /// <summary>Raw ushort Q0.16 elevation value, retained as a short legacy-friendly name.</summary>
    public static ushort ElevationAt(ulong seed, uint ver, long x, long y)
        => ElevationQ16At(seed, ver, x, y);

    public static ushort ElevationQ16At(ulong seed, uint ver, long x, long y)
        => Sample(seed, ver, x, y).ElevationQ16;

    public static ushort MoistureQ16At(ulong seed, uint ver, long x, long y)
        => Sample(seed, ver, x, y).MoistureQ16;

    public static ushort ForestQ16At(ulong seed, uint ver, long x, long y)
        => Sample(seed, ver, x, y).ForestQ16;

    public static ushort StoneQ16At(ulong seed, uint ver, long x, long y)
        => Sample(seed, ver, x, y).StoneQ16;

    public static bool IsWaterAt(ulong seed, uint ver, long x, long y)
        => TerrainAt(seed, ver, x, y) == BaseTerrainKind.Water;

    public static bool IsMountainAt(ulong seed, uint ver, long x, long y)
        => TerrainAt(seed, ver, x, y) == BaseTerrainKind.Mountain;

    private static GeneratedCell Sample(ulong seed, uint ver, long x, long y)
        => UnifiedWorldGenerator.SampleCell(seed, ver, UnifiedWorldGenerator.WorldOriginX + x, UnifiedWorldGenerator.WorldOriginY + y);
}
