using System;

namespace Game.Core.WorldStreaming.Layers;

/// <summary>
/// Единый многослойный генератор мира (план §2): WorldLayerStack.
/// Мировая карта = источник правды; локальная карта — «окно» в мир через LocalMapBuilder.
/// Все значения — чистые функции от (worldSeed, generatorVersion, абсолютные long x, y).
/// </summary>
public static class WorldLayerStack
{
    /// <summary>Версия конвейера слоёв. Изменение любого слоя/параметра ⇒ +1 (старые сохранения несовместимы).</summary>
    public const uint GeneratorVersion = 4u;

    /// <summary>L0: рельеф в абсолютных координатах (кэш-дружественная чистая функция).</summary>
    public static ushort ElevationAt(ulong seed, uint ver, long x, long y)
        => ReliefLayer.SampleElevation(seed, ver, x, y);

    [ThreadStatic] private static ulong _cachedSeed;
    [ThreadStatic] private static uint _cachedVer;
    [ThreadStatic] private static Func<long, long, ushort> _cachedFn;

    /// <summary>Делегат elevation для слоёв, принимающих Func (кэш на поток).</summary>
    private static Func<long, long, ushort> ElevationFn(ulong seed, uint ver)
    {
        if (_cachedFn != null && _cachedSeed == seed && _cachedVer == ver)
            return _cachedFn;
        _cachedSeed = seed;
        _cachedVer = ver;
        _cachedFn = (x, y) => ReliefLayer.SampleElevation(seed, ver, x, y);
        return _cachedFn;
    }

    /// <summary>Полная классификация клетки L0..L7 → MacroCell.</summary>
    public static MacroCell SampleMacro(ulong seed, uint ver, long x, long y)
    {
        var elevFn = ElevationFn(seed, ver);

        ushort elev = ReliefLayer.SampleElevation(seed, ver, x, y);
        bool isOcean = ReliefLayer.IsOcean(elev);

        ushort temp = ClimateLayer.SampleTemperature(seed, ver, x, y, elev);
        ushort precip = ClimateLayer.SamplePrecipitation(seed, ver, x, y, elevFn);

        ushort flow = 0;
        bool isRiver = false, isLake = false;
        if (!isOcean)
        {
            (flow, isRiver, isLake) = HydrologyLayer.Sample(seed, ver, x, y, elevFn);
        }

        ushort stone = SurfaceLayers.SampleStone(seed, ver, x, y, elevFn);
        float slope = Slope01(elevFn, x, y);
        ushort fertility = SurfaceLayers.SampleFertility(seed, ver, x, y, flow, slope, isRiver, isLake);
        ushort forest = SurfaceLayers.SampleForest(precip, temp, fertility, elev);

        BiomeType biome = BiomeClassifier.Classify(elev, temp, precip, flow, isRiver, isLake, isOcean);
        // Вода/горы не носят растительности и камней в индексе (тайлы задаёт проекция).
        if (biome is BiomeType.DeepWater or BiomeType.Mountain)
            forest = 0;

        return new MacroCell(x, y, elev, temp, precip, flow, isRiver, isLake, isOcean,
            stone, fertility, forest, biome);
    }

    /// <summary>Быстрая версия для стримера чанков: только поля GeneratedCell.</summary>
    public static LayersGeneratedCell SampleChunkCell(ulong seed, uint ver, long x, long y)
    {
        var m = SampleMacro(seed, ver, x, y);
        BaseTerrainKind terrain = m.Biome switch
        {
            BiomeType.DeepWater => BaseTerrainKind.Water,
            BiomeType.Mountain => BaseTerrainKind.Mountain,
            _ => BaseTerrainKind.Grass
        };
        return new LayersGeneratedCell(terrain, m.ElevationQ16, m.PrecipQ16, m.ForestQ16, m.StoneQ16, m.TemperatureQ16);
    }

    /// <summary>Проекция macro → локальной клетки (дерево/камень броски по hash).</summary>
    public static LocalCellSample SampleLocal(ulong seed, uint ver, long x, long y)
    {
        var m = SampleMacro(seed, ver, x, y);
        bool isWater = m.IsOcean || m.IsRiver || m.IsLake;

        ushort treeP = BiomeClassifier.TreeProbabilityQ16(m.Biome, m.ForestQ16);
        ushort treeRoll = CoordinateHash.SampleQ16(seed, ver, GenerationDomain.Forest, x, y, lane: 11);
        bool isTree = !isWater && m.Biome != BiomeType.Mountain && treeRoll < treeP;

        ushort stoneP = m.StoneQ16;
        if (m.Biome == BiomeType.Mountain)
            stoneP = Math.Max(stoneP, (ushort)35000);
        ushort stoneRoll = CoordinateHash.SampleQ16(seed, ver, GenerationDomain.Stone, x, y, lane: 12);
        bool isStone = !isWater && stoneRoll < stoneP;

        return new LocalCellSample(m, isWater, isTree, isStone);
    }

    private static float Slope01(Func<long, long, ushort> elevFn, long x, long y)
    {
        ushort e = elevFn(x, y);
        int dmax = Math.Abs(elevFn(x + 1, y) - e);
        dmax = Math.Max(dmax, Math.Abs(elevFn(x - 1, y) - e));
        dmax = Math.Max(dmax, Math.Abs(elevFn(x, y + 1) - e));
        dmax = Math.Max(dmax, Math.Abs(elevFn(x, y - 1) - e));
        return (float)dmax / 65535f * 4f;
    }
}

/// <summary>Минимальный выход для WorldChunk (совпадает по смыслу с GeneratedCell).</summary>
public readonly record struct LayersGeneratedCell(
    BaseTerrainKind Terrain,
    ushort ElevationQ16,
    ushort MoistureQ16,
    ushort ForestQ16,
    ushort StoneQ16,
    ushort TemperatureQ16);
