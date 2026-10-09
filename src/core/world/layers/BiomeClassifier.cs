using System;

namespace Game.Core.WorldStreaming.Layers;

/// <summary>
/// L7 Biome Classifier: единая классификация (диаграмма Уиттакера T×P + переопределения).
/// Дедупликация трёх путей BiomeMapper (Pick / PickWithoutTemp / NewPickFallback) —
/// здесь единственный источник истины. Пороги — WorldLayerParams (группа 6).
/// </summary>
public static class BiomeClassifier
{
    /// <summary>
    /// Классификация клетки по выходам слоёв. Порядок переопределений фиксирован (план §2.1):
    /// вода (ocean/river/lake) → горы → болото → Whittaker(T,P).
    /// </summary>
    public static BiomeType Classify(ushort elevQ16, ushort tempQ16, ushort precipQ16,
        ushort flowQ16, bool isRiver, bool isLake, bool isOcean)
    {
        // 1. Вода: океан/река/озеро.
        if (isOcean || isRiver || isLake)
            return BiomeType.DeepWater;

        // 2. Высокогорье → Mountain (elev > mountainElevHi).
        if (elevQ16 >= WorldLayerParams.MountainElevHiQ16)
            return BiomeType.Mountain;

        // 3. Болото: очень мокро + низина + заметный сток.
        if (precipQ16 >= WorldLayerParams.VeryWetPrecipQ16
            && elevQ16 < WorldLayerParams.SeaLevelQ16 + 8000
            && flowQ16 >= WorldLayerParams.SwampFlowMinQ16)
            return BiomeType.Swamp;

        // 4. Диаграмма Уиттакера: (T, P) → биом.
        bool cold = tempQ16 < WorldLayerParams.TundraTempMaxQ16;
        bool taigaCold = tempQ16 < WorldLayerParams.TaigaTempMaxQ16;
        bool hot = tempQ16 >= WorldLayerParams.HotTempMinQ16;

        bool veryDry = precipQ16 < WorldLayerParams.VeryDryPrecipQ16;
        bool dry = precipQ16 < WorldLayerParams.DryPrecipQ16;
        bool wet = precipQ16 >= WorldLayerParams.WetPrecipQ16;
        bool veryWet = precipQ16 >= WorldLayerParams.VeryWetPrecipQ16;

        if (cold)
            return veryDry ? BiomeType.Desert : BiomeType.Tundra; // холодная суша = тундра

        if (taigaCold)
            return wet || veryWet ? BiomeType.Taiga : BiomeType.Grassland;

        if (hot)
        {
            if (veryDry) return BiomeType.Desert;
            if (dry) return BiomeType.Savanna;
            if (veryWet) return BiomeType.Forest; // тропический лес
            return wet ? BiomeType.Forest : BiomeType.Savanna;
        }

        // Умеренная зона (TaigaTempMax ≤ T < HotTempMin, включая warm-подзону).
        if (veryDry) return BiomeType.Desert;
        if (dry) return BiomeType.Steppe;
        if (wet || veryWet) return BiomeType.Forest;
        return BiomeType.Plains;
    }

    /// <summary>
    /// Ключ тайла мировой карты (variant внутри биома) — детерминированный hash.
    /// Совместим по смыслу с BiomeMapper.HashVariant (тот же домен FeatureId).
    /// </summary>
    public static int Variant(ulong seed, uint ver, long x, long y, BiomeType biome)
    {
        int count = biome switch
        {
            BiomeType.Desert => 3,
            BiomeType.Steppe => 3,
            BiomeType.Savanna => 3,
            BiomeType.Grassland => 3,
            BiomeType.Tundra => 3,
            BiomeType.DeepWater => 3,
            BiomeType.Forest => 4,
            BiomeType.Taiga => 4,
            _ => 1
        };
        ulong h = CoordinateHash.Hash(seed, ver, GenerationDomain.FeatureId, x, y);
        return (int)((h >> 32) % (uint)count);
    }

    /// <summary>Вероятность дерева на суше: forest · k_biome (план §2.2 п.26).</summary>
    public static ushort TreeProbabilityQ16(BiomeType biome, ushort forestQ16)
    {
        // Коэффициенты плотности деревьев по биомам (Q16 множители forest-индекса).
        int kBiome = biome switch
        {
            BiomeType.Forest or BiomeType.Taiga => WorldLayerParams.Q16One,
            BiomeType.Swamp => 32768,
            BiomeType.Plains => 16384,
            BiomeType.Grassland => 9000,
            BiomeType.Steppe or BiomeType.Savanna => 5000,
            BiomeType.Tundra => 2000,
            BiomeType.Desert => Math.Min(forestQ16, WorldLayerParams.DesertTreeMaxQ16),
            _ => 0
        };

        if (biome == BiomeType.Desert)
            return (ushort)Math.Min(kBiome, WorldLayerParams.DesertTreeMaxQ16);

        long p = (long)forestQ16 * kBiome / WorldLayerParams.Q16One;
        return (ushort)Math.Clamp(p, 0, ushort.MaxValue);
    }
}
