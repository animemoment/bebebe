using System;

namespace Game.Core.WorldStreaming.Layers;

/// <summary>
/// L4 Geology (камни/осыпь), L5 Soil (плодородие), L6 Vegetation (индекс леса).
/// Все — чистые функции от абсолютных координат и выходов нижних слоёв.
/// </summary>
public static class SurfaceLayers
{
    /// <summary>L4: value-noise россыпь + бонус на крутых склонах (осыпь).</summary>
    public static ushort SampleStone(ulong seed, uint ver, long x, long y,
        Func<long, long, ushort> elevationAt)
    {
        // Базовая редкая россыпь — value-noise на решётке 24.
        ushort worley = CoordinateHash.ValueNoiseQ16(seed, ver, GenerationDomain.Stone, x, y, 24);
        float slope = Slope01(elevationAt, x, y);

        int stone = worley / 4; // базовая редкая россыпь
        if (slope > 0.35f)
            stone += (int)(WorldLayerParams.StoneSlopeBonusQ16 * Math.Min(1f, (slope - 0.35f) / 0.4f));
        else
            stone += WorldLayerParams.StoneBaseQ16;

        return WorldLayerParams.ClampQ16(stone);
    }

    /// <summary>Относительный перепад высот к соседям (0..1): max|Δ| по 4 соседям / Q16One.</summary>
    private static float Slope01(Func<long, long, ushort> elevationAt, long x, long y)
    {
        ushort e = elevationAt(x, y);
        int dmax = 0;
        int d1 = Math.Abs(elevationAt(x + 1, y) - e);
        int d2 = Math.Abs(elevationAt(x - 1, y) - e);
        int d3 = Math.Abs(elevationAt(x, y + 1) - e);
        int d4 = Math.Abs(elevationAt(x, y - 1) - e);
        dmax = Math.Max(Math.Max(d1, d2), Math.Max(d3, d4));
        return (float)dmax / 65535f * 4f; // усиление: типичные шумы дают малые Δ
    }

    /// <summary>L5: почва = базовое плодородие + аллювий (поймы у рек, низкий склон) + вулканический шум.</summary>
    public static ushort SampleFertility(ulong seed, uint ver, long x, long y,
        ushort flowQ16, float slope01, bool isRiver, bool isLake)
    {
        int fert = 20000; // базовый уровень (Q16 ~0.3)

        if (isRiver || (flowQ16 > WorldLayerParams.SwampFlowMinQ16 && slope01 < 0.25f))
            fert += (int)((long)WorldLayerParams.FertilityAlluvialQ16
                * Math.Min(WorldLayerParams.Q16One, flowQ16) / WorldLayerParams.Q16One);
        if (isLake)
            fert += WorldLayerParams.FertilityAlluvialQ16 / 2; // озёрные берега — ил

        // Редкий вулканический бонус (очень крупномасштабный шум, высокий порог).
        ushort vulc = CoordinateHash.ValueNoiseQ16(seed, ver, GenerationDomain.FeatureId, x, y, 1024);
        if (vulc > 62000)
            fert += 12000;

        return WorldLayerParams.ClampQ16(fert);
    }

    /// <summary>
    /// L6: forestIdx = sigmoid( base + precip·wP − |temp−Topt|·wT + soil·wS − elevPenalty ).
    /// Возврат [0..65535].
    /// </summary>
    public static ushort SampleForest(ushort precipQ16, ushort tempQ16, ushort fertilityQ16, ushort elevQ16)
    {
        int idx = WorldLayerParams.ForestBaseQ16;

        idx += (int)((long)precipQ16 * WorldLayerParams.ForestPrecipWeightQ16 / WorldLayerParams.Q16One);

        int tempDelta = Math.Abs(tempQ16 - WorldLayerParams.ForestTempOptimumQ16);
        idx -= (int)((long)tempDelta * WorldLayerParams.ForestTempPenaltyQ16 / WorldLayerParams.Q16One);

        idx += (int)((long)fertilityQ16 * WorldLayerParams.ForestSoilWeightQ16 / WorldLayerParams.Q16One);

        int aboveHighland = Math.Max(0, elevQ16 - WorldLayerParams.HighlandQ16);
        idx -= (int)((long)aboveHighland * WorldLayerParams.ForestElevPenaltyQ16 / WorldLayerParams.Q16One);

        // Сигмоида в [-Q16One..Q16One] → [0..Q16One].
        int sig = WorldLayerParams.SigmoidQ16(idx * 2 - WorldLayerParams.Q16One);
        return (ushort)Math.Clamp(sig, 0, ushort.MaxValue);
    }
}
