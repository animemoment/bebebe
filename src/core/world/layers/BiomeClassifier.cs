using System;

using Game.Core.WorldStreaming;

namespace Game.Core.WorldLayers;

/// <summary>
/// L7: единый классификатор биомов (диаграмма Уиттакера T×P + переопределения по
/// высоте/воде). Заменяет дубли BiomeMapper.Pick / PickWithoutTemp / NewPickFallback.
/// Детерминирован от (seed, wx, wy) — вариант тайла из хеша координат.
/// </summary>
public static class BiomeClassifier
{
    /// <summary>Биом клетки суши/воды по слоям. Не зависит от окна запроса.</summary>
    public static BiomeType Classify(uint seed, long wx, long wy)
    {
        byte hydro = HydrologyLayer.FlagsAt(seed, wx, wy);
        float elev = ReliefLayer.Elevation(seed, wx, wy);
        ushort temp = ClimateLayer.TemperatureQ16(seed, wx, wy);
        ushort precip = ClimateLayer.PrecipitationQ16(seed, wx, wy);

        // Переопределения по воде/рельефу — раньше климатической диаграммы.
        if ((hydro & HydrologyLayer.FWater) != 0) return BiomeType.DeepWater;
        if (elev > WorldLayerParams.MountainElevHi) return BiomeType.Mountain;

        // Диаграмма Уиттакера (T × P), пороги Q16 из WorldLayerParams.
        if (temp < WorldLayerParams.TundraTempHi)
            return BiomeType.Steppe; // тундра маппится на доступный степной тайл (льда нет в атласе)

        bool dry = precip < WorldLayerParams.DryPrecipHi;
        bool arid = precip < WorldLayerParams.AridPrecipHi;
        bool wet = precip >= WorldLayerParams.WetPrecipLo;

        if (temp < WorldLayerParams.TaigaTempHi)
            return dry ? BiomeType.Steppe : BiomeType.Forest;      // тайга ↔ степь

        if (temp < WorldLayerParams.TemperateTempHi)
        {
            if (arid) return BiomeType.Steppe;                     // сухие умеренные — степь
            if (wet)
                return precip >= WorldLayerParams.SwampPrecipMin && elev < WorldLayerParams.SeaLevel + 0.08f
                    ? BiomeType.Swamp : BiomeType.Forest;
            return BiomeType.Plains;
        }

        // Жаркая зона.
        if (arid) return BiomeType.Desert;
        if (dry) return BiomeType.Steppe;                          // саванна → степной тайл
        if (precip >= WorldLayerParams.SwampPrecipMin && elev < WorldLayerParams.SeaLevel + 0.06f)
            return BiomeType.Swamp;
        return wet ? BiomeType.Forest : BiomeType.Plains;
    }

    /// <summary>Плотность деревьев биома (процент ×100 от индекса леса).</summary>
    public static int TreeK(BiomeType biome) => biome switch
    {
        BiomeType.Desert => WorldLayerParams.TreeKDesert,
        BiomeType.Steppe => WorldLayerParams.TreeKSavanna,
        BiomeType.Plains => WorldLayerParams.TreeKMeadowDefault,
        BiomeType.Forest => WorldLayerParams.TreeKTropical,
        BiomeType.Swamp => WorldLayerParams.TreeKSwamp,
        _ => 0,
    };

    /// <summary>Вариант тайла [0..count) — детерминирован от координат.</summary>
    public static int Variant(uint seed, long wx, long wy, int count)
    {
        if (count <= 1) return 0;
        uint h = WorldNoise.HashNode(seed, 0x51EE7u, unchecked((int)wx), unchecked((int)wy));
        return (int)(h % (uint)count);
    }

    /// <summary>TilePick для совместимости с BiomeMapper-путём (мировая карта).</summary>
    public static TilePick Pick(uint seed, long wx, long wy)
    {
        var b = Classify(seed, wx, wy);
        int variant = b switch
        {
            BiomeType.Desert or BiomeType.Steppe or BiomeType.DeepWater => Variant(seed, wx, wy, 3),
            BiomeType.Forest => Variant(seed, wx, wy, 4),
            _ => 0,
        };
        return new TilePick(b, variant);
    }
}
