using System;
using System.Collections.Concurrent;
using Game.Core.WorldStreaming;

namespace Game.Core.WorldClimate;

/// <summary>
/// Провайдер климата МИРОВОЙ КАРТЫ. Единственный вход для всех потребителей:
/// маппера биомов (экран мировой карты), проекции в RegionTraits (бесшовный
/// стриминг чанков, без правок его логики) и локальной генерации (игровой остров).
///
/// Детерминизм: значение зависит ТОЛЬКО от (worldSeed, generatorVersion, координаты).
/// Никакого состояния, RNG-порядка или кэша, влияющего на результат, — соседние
/// регионы считаются независимо и в любом порядке/потоке.
///
/// Система координат: 1 единица = 1 РЕГИОН = WorldRegions.RegionSize клеток.
/// Мир 2048 x 1024 региона; экватор на regionY = 512.
/// </summary>
public static class WorldClimateProvider
{
    private static readonly ConcurrentDictionary<ulong, WorldClimateParams> ParamsCache = new();

    /// <summary>Кэш параметров по сиду (20 хешей — считать на каждую клетку нельзя).</summary>
    public static WorldClimateParams ParamsFor(ulong worldSeed)
        => ParamsCache.GetOrAdd(worldSeed, WorldClimateParams.FromSeed);

    /// <summary>Широта региона: 0 = экватор, 65535 = полюс.</summary>
    public static ushort LatitudeQ16(long regionY) => LatitudeBands.LatitudeQ16(regionY);

    /// <summary>Регион, содержащий клетку мира.</summary>
    public static long RegionIndexForCell(long cell) => FloorDiv(cell, WorldRegions.RegionSize);

    /// <summary>
    /// Климат одного РЕГИОНА (основной путь: считается один раз на регион,
    /// а затем билинейно смешивается — как уже делает экран мировой карты).
    /// </summary>
    public static WorldClimateFields SampleRegionClimate(ulong worldSeed, uint generatorVersion,
        long regionX, long regionY)
    {
        WorldClimateParams p = ParamsFor(worldSeed);
        ushort latitude = LatitudeBands.LatitudeQ16(regionY);
        ContinentSample c = ContinentShaper.Shape(p, worldSeed, generatorVersion, regionX, regionY);

        // --- Температура: широта минус высота (lapse rate) --------------------
        ushort temperature = LatitudeBands.TemperatureAtLatitude(latitude, p);
        temperature = Q16.ClampAdd(temperature,
            -(int)((long)c.Elevation * p.LapseRate / Q16.One));

        // --- Влажность -------------------------------------------------------
        ushort latitudeAridity = LatitudeBands.AridityAtLatitude(latitude, p);
        ushort moisture = Q16.ClampAdd(p.BaseMoisture,
            -(int)((long)latitudeAridity * 26000 / Q16.One));

        ushort oceanProximity = (ushort)(Q16.One - c.Continental);
        moisture = Q16.ClampAdd(moisture,
            (int)((long)oceanProximity * p.WindStrength / Q16.One));
        moisture = Q16.ClampAdd(moisture,
            -(int)((long)c.CoastDistance * p.ContinentalityDry / Q16.One));
        moisture = Q16.ClampAdd(moisture,
            -(int)((long)c.Elevation * p.RainShadowStrength / Q16.One));

        // Океанские течения греют/сушат берег (значимо только у побережья): плавный переход без шахматных артефактов.
        bool eastCoast = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion,
            GenerationDomain.ContinentWarp, regionX, regionY, 32) > Q16.Half;
        ushort currentWarmth = LatitudeBands.CurrentWarmthQ16(latitude, eastCoast);
        if (c.Continental < 42000)
            moisture = Q16.ApplyBias(moisture, currentWarmth, p.OceanCurrentStrength / 3);

        // Шум — чтобы пояса не были идеальными полосами.
        ushort climateNoise = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion,
            GenerationDomain.ClimateNoise, regionX, regionY, Math.Max(4, p.ContinentScale / 2));
        moisture = Q16.Weighted(moisture, 78, climateNoise, 22);

        // --- Производные поля ------------------------------------------------
        ushort precipitation = Q16.Mul(moisture, Q16.ClampAdd(temperature, 12000));

        ushort dryness = (ushort)(Q16.One - moisture);
        ushort aridity = Q16.Weighted(dryness, 70, latitudeAridity, 30);

        ushort warmEnough = Q16.InverseLerp(6000, 32000, temperature);
        ushort forest = Q16.Mul(moisture, warmEnough);
        forest = Q16.ClampAdd(forest, -(int)((long)aridity * 30000 / Q16.One));

        const int ColdThreshold = 20000;
        ushort ice = temperature < ColdThreshold
            ? Q16.InverseLerp(ColdThreshold, 3000, temperature)
            : (ushort)0;
        ushort polarWeight = Q16.InverseLerp(58000, Q16.One, latitude);
        ice = (ushort)Math.Clamp((long)ice + (long)polarWeight * p.IceCapStrength / Q16.One, 0L, Q16.One);

        ushort river = Q16.Mul(moisture, Q16.ClampAdd(Q16.Half, (int)c.Elevation / 3));
        river = Q16.ClampAdd(river, -(int)((long)aridity * 20000 / Q16.One));

        ushort stone = Q16.Weighted(c.Elevation, 70, aridity, 30);

        ushort fertility = Q16.Weighted(
            Q16.Mul(moisture, warmEnough), 70,
            (ushort)(Q16.One - c.Elevation), 30);
        fertility = Q16.ClampAdd(fertility, -(int)((long)ice * 40000 / Q16.One));

        ushort salinity = c.OceanDepth == 0
            ? (ushort)0
            : Q16.Weighted(c.OceanDepth, 60, Q16.Half, 40);

        int zonal = Math.Abs((int)LatitudeBands.WindZonalQ16(latitude) - Q16.Half) * 2;
        ushort wind = Q16.Weighted(p.WindStrength, 60, (ushort)Math.Min(Q16.One, zonal), 40);

        return new WorldClimateFields(
            Continental: c.Continental,
            Elevation: c.Elevation,
            OceanDepth: c.OceanDepth,
            CoastDistance: c.CoastDistance,
            Temperature: temperature,
            Moisture: moisture,
            Precipitation: precipitation,
            Aridity: aridity,
            Wind: wind,
            CurrentWarmth: currentWarmth,
            Forest: forest,
            Ice: ice,
            River: river,
            Stone: stone,
            Fertility: fertility,
            Salinity: salinity,
            Latitude: latitude);
    }

    /// <summary>
    /// Климат ТОЧКИ ПО КЛЕТКЕ мира без смешивания: регион берётся по клетке.
    /// Используется локальной генерацией (игровой остров = 1 регион).
    /// </summary>
    public static WorldClimateFields SamplePointClimate(ulong worldSeed, uint generatorVersion,
        long cellX, long cellY)
        => SampleRegionClimate(worldSeed, generatorVersion,
            RegionIndexForCell(cellX), RegionIndexForCell(cellY));

    /// <summary>Cell-centered веса смешивания регионов (непрерывны по клеткам).</summary>
    public static (uint U, uint V) BlendWeights(long cellX, long cellY)
    {
        int lx = (int)FloorMod(cellX, WorldRegions.RegionSize);
        int ly = (int)FloorMod(cellY, WorldRegions.RegionSize);
        uint u = (uint)(((long)lx * 2 + 1) * Q16.One / (2L * WorldRegions.RegionSize));
        uint v = (uint)(((long)ly * 2 + 1) * Q16.One / (2L * WorldRegions.RegionSize));
        return (u, v);
    }

    /// <summary>
    /// Климат точки с билинейным смешиванием 4 регионов — БЕСШОВНЫЙ путь
    /// (нет тайловых границ на стыках регионов 512x512).
    /// </summary>
    public static WorldClimateFields SampleBlendedClimate(ulong worldSeed, uint generatorVersion,
        long cellX, long cellY)
    {
        long rx = RegionIndexForCell(cellX);
        long ry = RegionIndexForCell(cellY);
        (uint u, uint v) = BlendWeights(cellX, cellY);

        WorldClimateFields nw = SampleRegionClimate(worldSeed, generatorVersion, rx, ry);
        WorldClimateFields ne = SampleRegionClimate(worldSeed, generatorVersion, rx + 1, ry);
        WorldClimateFields sw = SampleRegionClimate(worldSeed, generatorVersion, rx, ry + 1);
        WorldClimateFields se = SampleRegionClimate(worldSeed, generatorVersion, rx + 1, ry + 1);
        return BlendBilinear(nw, ne, sw, se, u, v);
    }

    /// <summary>Билинейное смешивание четырёх наборов полей (порядок: nw, ne, sw, se).</summary>
    public static WorldClimateFields BlendBilinear(
        in WorldClimateFields nw, in WorldClimateFields ne,
        in WorldClimateFields sw, in WorldClimateFields se, uint u, uint v)
    {
        return new WorldClimateFields(
            Continental: Blend(nw.Continental, ne.Continental, sw.Continental, se.Continental, u, v),
            Elevation: Blend(nw.Elevation, ne.Elevation, sw.Elevation, se.Elevation, u, v),
            OceanDepth: Blend(nw.OceanDepth, ne.OceanDepth, sw.OceanDepth, se.OceanDepth, u, v),
            CoastDistance: Blend(nw.CoastDistance, ne.CoastDistance, sw.CoastDistance, se.CoastDistance, u, v),
            Temperature: Blend(nw.Temperature, ne.Temperature, sw.Temperature, se.Temperature, u, v),
            Moisture: Blend(nw.Moisture, ne.Moisture, sw.Moisture, se.Moisture, u, v),
            Precipitation: Blend(nw.Precipitation, ne.Precipitation, sw.Precipitation, se.Precipitation, u, v),
            Aridity: Blend(nw.Aridity, ne.Aridity, sw.Aridity, se.Aridity, u, v),
            Wind: Blend(nw.Wind, ne.Wind, sw.Wind, se.Wind, u, v),
            CurrentWarmth: Blend(nw.CurrentWarmth, ne.CurrentWarmth, sw.CurrentWarmth, se.CurrentWarmth, u, v),
            Forest: Blend(nw.Forest, ne.Forest, sw.Forest, se.Forest, u, v),
            Ice: Blend(nw.Ice, ne.Ice, sw.Ice, se.Ice, u, v),
            River: Blend(nw.River, ne.River, sw.River, se.River, u, v),
            Stone: Blend(nw.Stone, ne.Stone, sw.Stone, se.Stone, u, v),
            Fertility: Blend(nw.Fertility, ne.Fertility, sw.Fertility, se.Fertility, u, v),
            Salinity: Blend(nw.Salinity, ne.Salinity, sw.Salinity, se.Salinity, u, v),
            Latitude: Blend(nw.Latitude, ne.Latitude, sw.Latitude, se.Latitude, u, v));
    }

    private static ushort Blend(ushort a, ushort b, ushort c, ushort d, uint u, uint v)
    {
        ushort top = Q16.Lerp(a, b, u);
        ushort bottom = Q16.Lerp(c, d, u);
        return Q16.Lerp(top, bottom, v);
    }

    private static long FloorDiv(long value, int divisor)
    {
        long q = value / divisor;
        long r = value % divisor;
        return r < 0 ? q - 1 : q;
    }

    private static long FloorMod(long value, int divisor)
    {
        long r = value % divisor;
        return r < 0 ? r + divisor : r;
    }
}