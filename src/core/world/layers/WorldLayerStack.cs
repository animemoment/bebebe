using System;
using Game.Core.WorldStreaming;

namespace Game.Core.WorldLayers;

/// <summary>Макро-клетка мировой карты (1 клетка = 1 тайл мира).</summary>
public readonly record struct MacroCell(
    BiomeType Biome,
    ushort ElevationQ16,
    ushort TemperatureQ16,
    ushort PrecipQ16,
    ushort ForestQ16,
    bool IsRiver,
    bool IsLake);

/// <summary>Локальная клетка игровой области — проекция мира в тайлы MapData.</summary>
public readonly record struct LocalCellSample(
    TileType Ground,
    bool Tree,
    bool Stone,
    int Humidity0200,
    int Fertility0200,
    BiomeType Biome);

/// <summary>
/// Единый многослойный генератор мира (v4): мировая карта = источник истины,
/// локальная карта = «окно» в мир с тем же сидом и абсолютными координатами.
/// Все методы — чистые функции (seed, wx, wy); результат не зависит от bbox,
/// порядка запроса и потоков. Слои: L0 Relief → L1/L2 Climate → L3 Hydrology →
/// L4–L6 Vegetation/Geology/Soil → L7 BiomeClassifier.
/// </summary>
public static class WorldLayerStack
{
    public const uint Version = WorldLayerParams.GeneratorVersion;

    /// <summary>L0..L7: макро-клетка мировой карты.</summary>
    public static MacroCell SampleMacro(uint seed, long wx, long wy)
    {
        byte hydro = HydrologyLayer.FlagsAt(seed, wx, wy);
        return new MacroCell(
            BiomeClassifier.Classify(seed, wx, wy),
            ReliefLayer.ElevationQ16(seed, wx, wy),
            ClimateLayer.TemperatureQ16(seed, wx, wy),
            ClimateLayer.PrecipitationQ16(seed, wx, wy),
            VegetationLayer.ForestIndexQ16(seed, wx, wy),
            (hydro & HydrologyLayer.FRiver) != 0,
            (hydro & HydrologyLayer.FLake) != 0);
    }

    /// <summary>Проекция мира в локальную клетку игровой области.</summary>
    public static LocalCellSample SampleLocal(uint seed, long wx, long wy)
    {
        var biome = BiomeClassifier.Classify(seed, wx, wy);
        byte hydro = HydrologyLayer.FlagsAt(seed, wx, wy);

        // Ground: вода (море/река/озеро) → Water; горы → Mountain; остальное → Grass.
        TileType ground = biome switch
        {
            BiomeType.DeepWater => TileType.Water,
            BiomeType.Mountain => TileType.Mountain,
            _ => TileType.Grass,
        };

        bool tree = false, stone = false;
        if (ground == TileType.Grass)
        {
            double forest = VegetationLayer.Forest01(seed, wx, wy);
            int k = BiomeClassifier.TreeK(biome);
            tree = VegetationLayer.TreeRoll(seed, wx, wy, forest, k);
            stone = VegetationLayer.StoneRoll(seed, wx, wy);
        }

        // Влажность локальной клетки: база из осадков + надбавка за воду рядом.
        int humidity = ClimateLayer.PrecipitationQ16(seed, wx, wy) / 328; // [0..200]
        if ((hydro & HydrologyLayer.FWater) != 0) humidity = HumidityMap.MaxMoisture;
        else
        {
            for (int dy = -3; dy <= 3 && humidity < HumidityMap.MaxMoisture; dy++)
                for (int dx = -3; dx <= 3; dx++)
                    if ((HydrologyLayer.FlagsAt(seed, wx + dx, wy + dy) & HydrologyLayer.FWater) != 0)
                    { humidity = Math.Min(HumidityMap.MaxMoisture, humidity + 60); break; }
        }

        int fertility = VegetationLayer.FertilityQ16(seed, wx, wy);

        return new LocalCellSample(ground, tree, stone,
            WorldLayerParams.ClampI(humidity, HumidityMap.MinMoisture, HumidityMap.MaxMoisture),
            fertility, biome);
    }

    /// <summary>Совместимость со старым API: GeneratedCell для стриминга чанков.</summary>
    public static GeneratedCell ToGeneratedCell(uint seed, long wx, long wy)
    {
        var m = SampleMacro(seed, wx, wy);
        BaseTerrainKind terrain = m.Biome switch
        {
            BiomeType.DeepWater => BaseTerrainKind.Water,
            BiomeType.Mountain => BaseTerrainKind.Mountain,
            _ => BaseTerrainKind.Grass,
        };
        return new GeneratedCell(
            terrain,
            m.ElevationQ16,
            m.PrecipQ16,                 // moisture = осадки (L2)
            m.ForestQ16,                 // лесной индекс (L6)
            StoneQ16(seed, wx, wy, terrain),
            m.TemperatureQ16);
    }

    private static ushort StoneQ16(uint seed, long wx, long wy, BaseTerrainKind terrain)
    {
        if (terrain != BaseTerrainKind.Grass) return 0;
        double p = VegetationLayer.StoneProbability(seed, wx, wy);
        return WorldLayerParams.ClampQ16((int)(p * 65535.0 * 6.0)); // усиление: порог WorldDecor 35k достижим
    }
}
