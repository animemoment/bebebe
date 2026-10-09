using System;

namespace Game.Core.WorldLayers;

/// <summary>
/// L4–L6: геология, почва, растительность. Все значения — чистые функции
/// абсолютных координат (seed, wx, wy), без окна запроса.
/// </summary>
public static class VegetationLayer
{
    /// <summary>L4: вероятность камня [0..1] (Voronoi-скопление + осыпи у склонов/воды).</summary>
    public static double StoneProbability(uint seed, long wx, long wy)
    {
        float vor = WorldNoise.VoronoiF1(seed, WorldLayerParams.DomStone, wx, wy,
            WorldLayerParams.StoneVoronoiScale);
        double cluster = 1.0 - Math.Clamp(vor, 0f, 1f); // близость к центру ячейки → скопление

        // Крутизна склона (перепад высот с соседями).
        float e = ReliefLayer.Elevation(seed, wx, wy);
        float eX = ReliefLayer.Elevation(seed, wx + 1, wy);
        float eY = ReliefLayer.Elevation(seed, wx, wy + 1);
        float slope = Math.Max(Math.Abs(e - eX), Math.Abs(e - eY)) * 10f;

        double p = WorldLayerParams.StoneBaseDensity + cluster * 0.02;
        if (slope > 0.5f) p *= WorldLayerParams.StoneSlopeMult;
        else if (HydrologyLayer.IsWater(seed, wx, wy)) p *= WorldLayerParams.StoneWaterMult;
        else if (cluster < 0.2 && slope < 0.2f) p *= WorldLayerParams.StoneDryMult;

        return Math.Clamp(p, 0.0, 1.0);
    }

    /// <summary>L5: плодородие Q16 (аллювий пойм + фоновая вулканика/гумус).</summary>
    public static ushort FertilityQ16(uint seed, long wx, long wy)
    {
        int f = (int)(WorldNoise.FbmAt(seed, WorldLayerParams.DomSoil, wx, wy, 160f, 3) * 90f);

        // Пойма: рядом с рекой/озером + низкий склон → аллювий.
        byte hydro = HydrologyLayer.FlagsAt(seed, wx, wy);
        if ((hydro & HydrologyLayer.FWater) != 0)
            f += WorldLayerParams.FertilityAlluvialBonus;
        else
        {
            for (int dy = -2; dy <= 2 && !((hydro & HydrologyLayer.FWater) != 0); dy++)
                for (int dx = -2; dx <= 2; dx++)
                    if ((HydrologyLayer.FlagsAt(seed, wx + dx, wy + dy) & HydrologyLayer.FWater) != 0)
                    { f += WorldLayerParams.FertilityAlluvialBonus / 2; break; }
        }

        // Органика под лесом (индекс считается напрямую, без рекурсии через Fertility).
        ushort precip = ClimateLayer.PrecipitationQ16(seed, wx, wy);
        ushort temp = ClimateLayer.TemperatureQ16(seed, wx, wy);
        float elevL = ReliefLayer.Elevation(seed, wx, wy);
        int forestIdxRaw = ComputeForestIndex(precip, temp, f * 327, elevL);
        if (forestIdxRaw >= WorldLayerParams.ForestIndexLo)
            f += WorldLayerParams.FertilityForestBonus;

        return (ushort)WorldLayerParams.ClampI(f, 0, FertilityMap.MaxFertility);
    }

    /// <summary>L6: индекс леса Q16 = f(осадки, температура, почва, высота).</summary>
    public static ushort ForestIndexQ16(uint seed, long wx, long wy)
    {
        if ((HydrologyLayer.FlagsAt(seed, wx, wy) & HydrologyLayer.FWater) != 0) return 0;

        ushort precip = ClimateLayer.PrecipitationQ16(seed, wx, wy);
        ushort temp = ClimateLayer.TemperatureQ16(seed, wx, wy);
        int soil = FertilityQ16(seed, wx, wy) * 327; // ~[0..65535] из [0..200]
        float elev = ReliefLayer.Elevation(seed, wx, wy);

        int idx = ComputeForestIndex(precip, temp, soil, elev);

        // Мелкий шум плотности (пятнистость крон).
        float n = WorldNoise.FbmAt(seed, WorldLayerParams.DomVegNoise, wx, wy, 24f, 2);
        idx = WorldLayerParams.ClampI((int)(idx * (0.85f + n * 0.3f)), 0, 65535);

        return (ushort)idx;
    }

    /// <summary>Ядро индекса леса без рекурсии: sigmoid-подобная смесь факторов.</summary>
    private static int ComputeForestIndex(int precipQ16, int tempQ16, int soilQ16, float elev01)
    {
        int elevPenalty = elev01 > WorldLayerParams.MountainElevHi
            ? (int)((elev01 - WorldLayerParams.MountainElevHi) * 120000f) : 0;

        int tOpt = WorldLayerParams.ForestTempOptimumQ16;
        int tDist = Math.Abs(tempQ16 - tOpt);

        int idx = precipQ16 * WorldLayerParams.ForestPrecipWeight - tDist + soilQ16 / 2 - elevPenalty;
        return WorldLayerParams.ClampI(idx, 0, 65535);
    }

    /// <summary>Нормированный индекс леса [0..1].</summary>
    public static double Forest01(uint seed, long wx, long wy)
    {
        int idx = ForestIndexQ16(seed, wx, wy);
        int lo = WorldLayerParams.ForestIndexLo, hi = WorldLayerParams.ForestIndexHi;
        if (idx <= lo) return 0.0;
        return Math.Min(1.0, (idx - lo) / (double)(hi - lo));
    }

    /// <summary>Детерминированный «бросок» дерева на клетку.</summary>
    public static bool TreeRoll(uint seed, long wx, long wy, double forest01, int treeKPercent)
    {
        if (forest01 <= 0 || treeKPercent <= 0) return false;
        double roll = WorldNoise.CellRandom(seed, WorldLayerParams.DomTreeRoll, wx, wy);
        return roll < forest01 * treeKPercent / 100.0;
    }

    /// <summary>Детерминированный «бросок» камня на клетку.</summary>
    public static bool StoneRoll(uint seed, long wx, long wy)
    {
        double p = StoneProbability(seed, wx, wy);
        return WorldNoise.CellRandom(seed, WorldLayerParams.DomStoneRoll, wx, wy) < p;
    }
}
