using System;

namespace Game.Core.WorldLayers;

/// <summary>
/// L0: рельеф в абсолютных мировых координатах. Domain-warped fBm + ridged-хребты.
/// Чистая функция (seed, x, y) → elevation [0..1]; не зависит от bbox/окна запроса —
/// фундамент бесшовности чанков (P0-фикс швов).
/// </summary>
public static class ReliefLayer
{
    /// <summary>Высота клетки мира [0..1]. SeaLevel = WorldLayerParams.SeaLevel.</summary>
    public static float Elevation(uint seed, long wx, long wy)
    {
        // 1) domain warp — извилистые берега, «текучие» континенты
        float warpX = WorldNoise.FbmAt(seed, WorldLayerParams.DomWarpX, wx, wy, 220f, 3);
        float warpY = WorldNoise.FbmAt(seed, WorldLayerParams.DomWarpY, wx, wy, 220f, 3);
        float ewx = wx + (warpX - 0.5f) * 2f * WorldLayerParams.WarpStrength;
        float ewy = wy + (warpY - 0.5f) * 2f * WorldLayerParams.WarpStrength;

        // 2) базовый fBm + Voronoi-плато (крупные глыбы суши)
        float baseE = WorldNoise.FbmAt(seed, WorldLayerParams.DomElevBase, ewx, ewy,
            WorldLayerParams.ReliefScale, WorldLayerParams.ReliefOctaves);
        float vor = 1f - Math.Clamp(WorldNoise.VoronoiF1(seed, WorldLayerParams.DomElevBase + 77,
            ewx, ewy, 340f), 0f, 1f);
        baseE = baseE * 0.86f + vor * 0.14f;

        // 3) ridged-хребты
        float ridge = WorldNoise.RidgedFbmAt(seed, WorldLayerParams.DomElevRidge, ewx, ewy,
            WorldLayerParams.RidgeScale, WorldLayerParams.RidgeOctaves);

        return Math.Clamp(baseE * (1f - WorldLayerParams.RidgeWeight) + ridge * WorldLayerParams.RidgeWeight, 0f, 1f);
    }

    public static ushort ElevationQ16(uint seed, long wx, long wy)
        => WorldLayerParams.ClampQ16((int)(Elevation(seed, wx, wy) * 65535f));

    /// <summary>Истинно, если клетка ниже уровня моря (открытая вода до гидрологии).</summary>
    public static bool IsSea(uint seed, long wx, long wy)
        => Elevation(seed, wx, wy) < WorldLayerParams.SeaLevel;
}