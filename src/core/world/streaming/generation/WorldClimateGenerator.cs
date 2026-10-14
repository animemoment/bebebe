using System;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Генератор физической климатической модели мира (18 параметров).
/// Детерминирован, zero-alloc, целочисленная Q16 арифметика.
/// Реализует:
/// 1. Тектонику, континенты, океаны и островные архипелаги.
/// 2. Широтную поясность: экватор (пустыни/саванны/джунгли) -> умеренный пояс -> полюса (ледники/тундра).
/// 3. Высотную поясность (падение температуры с высотой).
/// 4. Орографический эффект (дождевая тень за горными хребтами).
/// 5. Билинейное непрерывное смешивание параметров без швов на границах регионов и чанков.
/// </summary>
public static class WorldClimateGenerator
{
    public const int Q16One = 65535;
    public const int Q16Half = 32768;
    public const int SeaLevelQ16 = 22000;
    public const int MountainBaseQ16 = 44000;

    [ThreadStatic]
    private static RegionCacheEntry[] _regionCache;

    private struct RegionCacheEntry
    {
        public ulong Seed;
        public uint Version;
        public long Rx;
        public long Ry;
        public WorldClimateParams Value;
        public bool Valid;
    }

    /// <summary>
    /// Семпл макро-климата региона (rx, ry).
    /// </summary>
    public static WorldClimateParams SampleRegion(ulong worldSeed, uint generatorVersion, RegionKey region)
    {
        var macroParams = Game.Core.WorldClimate.WorldClimateProvider.ParamsFor(worldSeed);
        return SampleRegion(worldSeed, generatorVersion, region, in macroParams);
    }

    /// <summary>
    /// Семпл 18 параметров для клетки (wx, wy) со сглаживанием через соседние регионы.
    /// Использует центрированные узлы сетки и соблюдает жесткий региональный инвариант:
    /// океанический регион гарантированно остается водой (< SeaLevelQ16),
    /// а остров/суша удерживает рельеф в своем центре и образует плавный органический берег по периметру.
    /// </summary>
    public static WorldClimateParams SampleBlendedCell(ulong worldSeed, uint generatorVersion, long wx, long wy)
    {
        var macroParams = Game.Core.WorldClimate.WorldClimateProvider.ParamsFor(worldSeed);
        (RegionKey r, uint u, uint v) = RegionTraitProvider.CenteredBlendNodes(wx, wy);

        WorldClimateParams nw = SampleRegion(worldSeed, generatorVersion, r, in macroParams);
        WorldClimateParams ne = SampleRegion(worldSeed, generatorVersion, new RegionKey(r.X + 1, r.Y), in macroParams);
        WorldClimateParams sw = SampleRegion(worldSeed, generatorVersion, new RegionKey(r.X, r.Y + 1), in macroParams);
        WorldClimateParams se = SampleRegion(worldSeed, generatorVersion, new RegionKey(r.X + 1, r.Y + 1), in macroParams);

        WorldClimateParams blended = BlendBilinear(nw, ne, sw, se, u, v);

        // Региональный макро-инвариант суши и океана:
        long rx = WorldCoordinates.FloorDiv(wx, WorldRegions.RegionSize);
        long ry = WorldCoordinates.FloorDiv(wy, WorldRegions.RegionSize);
        WorldClimateParams cellRegion = SampleRegion(worldSeed, generatorVersion, new RegionKey(rx, ry), in macroParams);
        bool isOceanRegion = cellRegion.ElevationQ16 < SeaLevelQ16;

        int lx = (int)(wx - rx * WorldRegions.RegionSize);
        int ly = (int)(wy - ry * WorldRegions.RegionSize);
        int distToBorder = Math.Min(Math.Min(lx, WorldRegions.RegionSize - 1 - lx),
                                    Math.Min(ly, WorldRegions.RegionSize - 1 - ly));

        int borderJitter = ((int)CoordinateHash.ValueNoiseQ16(worldSeed + 6127ul, generatorVersion, GenerationDomain.CoastWarpMeso, wx, wy, 24) - Q16Half) * 20 / Q16Half;
        int organicDist = distToBorder + borderJitter;

        if (isOceanRegion)
        {
            // Океанический регион на мировой карте:
            // 1. Никакая интерполяция с соседней сушей не имеет права поднять океан выше уровня моря!
            if (blended.ElevationQ16 >= SeaLevelQ16)
            {
                blended = blended with { ElevationQ16 = (ushort)(SeaLevelQ16 - 1) };
            }

            // 2. В глубине океанического региона (вдали от границы суши) — гарантированный глубокий океан:
            if (organicDist >= 44)
            {
                ushort maxElev = (ushort)Math.Min((int)blended.ElevationQ16, SeaLevelQ16 - 1500);
                blended = blended with { ElevationQ16 = maxElev };
            }
        }
        else
        {
            // Регион суши / острова на мировой карте:
            // В центре острова (вдали от границы с окружающим океаном) суша гарантированно не тонет!
            // Органическая граница естественным образом искривляет форму берегов острова.
            if (organicDist >= 28)
            {
                if (blended.ElevationQ16 < SeaLevelQ16 + 200)
                {
                    blended = blended with { ElevationQ16 = (ushort)Math.Max((int)cellRegion.ElevationQ16, SeaLevelQ16 + 400) };
                }
            }
        }

        return blended;
    }

    /// <summary>
    /// Прямой расчет 18 параметров в конкретной глобальной точке мира.
    /// </summary>
    public static WorldClimateParams SampleAtCell(ulong worldSeed, uint generatorVersion, long wx, long wy)
    {
        long rx = WorldCoordinates.FloorDiv(wx, WorldRegions.RegionSize);
        long ry = WorldCoordinates.FloorDiv(wy, WorldRegions.RegionSize);
        return SampleRegion(worldSeed, generatorVersion, new RegionKey(rx, ry));
    }

    /// <summary>
    /// Прямой расчет 18 параметров в конкретной глобальной точке мира с предзагруженными macroParams.
    /// </summary>
    public static WorldClimateParams SampleAtCell(ulong worldSeed, uint generatorVersion, long wx, long wy, in Game.Core.WorldClimate.WorldClimateParams macroParams)
    {
        long rx = WorldCoordinates.FloorDiv(wx, WorldRegions.RegionSize);
        long ry = WorldCoordinates.FloorDiv(wy, WorldRegions.RegionSize);
        return SampleRegion(worldSeed, generatorVersion, new RegionKey(rx, ry), in macroParams);
    }

    /// <summary>
    /// Высокопроизводительный кэшированный расчет 18 параметров региона (ThreadStatic direct-mapped L1 cache).
    /// </summary>
    public static WorldClimateParams SampleRegion(ulong worldSeed, uint generatorVersion, RegionKey region, in Game.Core.WorldClimate.WorldClimateParams macroParams)
    {
        _regionCache ??= new RegionCacheEntry[64];
        int idx = (int)((((ulong)region.X * 73856093ul) ^ ((ulong)region.Y * 19349663ul) ^ worldSeed) & 63);
        ref var entry = ref _regionCache[idx];
        if (entry.Valid && entry.Seed == worldSeed && entry.Version == generatorVersion && entry.Rx == region.X && entry.Ry == region.Y)
        {
            return entry.Value;
        }

        WorldClimateParams result = ComputeSampleRegion(worldSeed, generatorVersion, region, in macroParams);
        entry = new RegionCacheEntry
        {
            Seed = worldSeed,
            Version = generatorVersion,
            Rx = region.X,
            Ry = region.Y,
            Value = result,
            Valid = true
        };
        return result;
    }

    private static WorldClimateParams ComputeSampleRegion(ulong worldSeed, uint generatorVersion, RegionKey region, in Game.Core.WorldClimate.WorldClimateParams macroParams)
    {
        // ---------------- 1. ШИРОТА (Latitude) ----------------
        // Мир: WorldRegions.WorldRegionsY (1024) регионов по Y. Экватор на 512.
        long safeRx = ((region.X % WorldRegions.WorldRegionsX) + WorldRegions.WorldRegionsX) % WorldRegions.WorldRegionsX;
        long safeRy = Math.Clamp(region.Y, 0, WorldRegions.WorldRegionsY - 1);
        long equatorY = WorldRegions.WorldRegionsY / 2;
        long distFromEquator = Math.Abs(safeRy - equatorY);

        // Органический двух-октавный макро-изгиб широт (фрактальные берега льдов вместо синусоиды)
        ushort latWarp1 = CoordinateHash.ValueNoiseQ16(worldSeed + 99999ul, generatorVersion,
            GenerationDomain.ContinentWarp, safeRx, safeRy, 64);
        ushort latWarp2 = CoordinateHash.ValueNoiseQ16(worldSeed + 88888ul, generatorVersion,
            GenerationDomain.ContinentWarp, safeRx, safeRy, 24);
        float latWarp = (((float)latWarp1 - Q16Half) * 0.7f + ((float)latWarp2 - Q16Half) * 0.3f) / (float)Q16Half * 0.05f;
        float normLat = Math.Clamp((float)distFromEquator / (float)equatorY + latWarp, 0f, 1f);
        ushort latitudeQ16 = (ushort)Math.Clamp((int)(normLat * Q16One), 0, Q16One);

        // ---------------- 2. ТЕКТОНИКА И ВЫСОТА (ContinentShaper) ----------------
        var cont = Game.Core.WorldClimate.ContinentShaper.Shape(macroParams, worldSeed, generatorVersion, safeRx, safeRy);

        ushort continentalnessQ16 = cont.Continental;
        ushort tectonicPressureQ16 = cont.PlateBoundary;
        ushort archipelagoTendencyQ16 = (cont.OceanDepth > 0 && cont.PlateBoundary > 6000)
            ? (ushort)Math.Min(Q16One, (int)(cont.PlateBoundary * 1.5f))
            : (ushort)0;

        ushort elevationQ16;
        if (cont.OceanDepth == 0) // Суша
        {
            elevationQ16 = (ushort)(SeaLevelQ16 + (long)cont.Elevation * (Q16One - SeaLevelQ16) / Q16One);
        }
        else // Океан
        {
            elevationQ16 = (ushort)Math.Max(0, SeaLevelQ16 - 1 - (long)cont.OceanDepth * SeaLevelQ16 / Q16One);
        }

        // ---------------- 3. ТЕМПЕРАТУРА (Temperature) ----------------
        // База от широты: экватор = 54000..60000, полюса = 4000..10000
        float baseTempFloat = Math.Clamp(1f - normLat * normLat, 0f, 1f);
        int tempSpan = macroParams.EquatorTemperature - macroParams.PolarTemperature;
        int tempVal = (int)(baseTempFloat * tempSpan + macroParams.PolarTemperature);

        // Независимый крупномасштабный шум погоды (scale=96, ±3500 Q16) для естественных изотерм
        ushort tempNoise = CoordinateHash.ValueNoiseQ16(worldSeed + 11111ul, generatorVersion,
            GenerationDomain.ClimateNoise, safeRx, safeRy, 96);
        tempVal += (int)(((long)tempNoise - Q16Half) * 7000 / Q16One);

        // Высотный градиент (Lapse rate: -6.5°C на 1000м, только выше уровня моря):
        if (elevationQ16 > SeaLevelQ16)
        {
            int altAboveSea = elevationQ16 - SeaLevelQ16;
            tempVal -= (int)((long)altAboveSea * macroParams.LapseRate / Q16One);
        }

        // Океаническая модерация:
        ushort oceanInfluenceQ16 = (ushort)Math.Clamp(Q16One - continentalnessQ16, 0, Q16One);
        tempVal = (int)(tempVal * 0.88f + (Q16Half * 0.5f + macroParams.EquatorTemperature * 0.25f) * 0.12f);
        ushort temperatureQ16 = (ushort)Math.Clamp(tempVal, 0, Q16One);

        // ---------------- 4. ВЕТЕР И ОРОГРАФИЧЕСКИЙ ЭФФЕКТ (Wind & Rain Shadow) ----------------
        // Пассаты в тропиках (восток -> запад), Западный перенос в умеренных широтах (запад -> восток)
        int windDirX = normLat < 0.22f ? -1 : 1;
        ushort windHeadingQ16 = windDirX < 0 ? (ushort)49152 : (ushort)16384;

        // Физическая дождевая тень: проверяем наличие горного барьера с наветренной стороны
        ushort rainShadowQ16 = 0;
        if (elevationQ16 < MountainBaseQ16 + 4000 && continentalnessQ16 > 20000)
        {
            // Наветренная точка на расстоянии 3 регионов навстречу ветру
            long upwindRx = safeRx - windDirX * 3;
            upwindRx = ((upwindRx % WorldRegions.WorldRegionsX) + WorldRegions.WorldRegionsX) % WorldRegions.WorldRegionsX;
            var upwindSample = Game.Core.WorldClimate.ContinentShaper.Shape(macroParams, worldSeed, generatorVersion, upwindRx, safeRy);
            if (upwindSample.OceanDepth == 0)
            {
                int upwindElev = SeaLevelQ16 + (int)((long)upwindSample.Elevation * (Q16One - SeaLevelQ16) / Q16One);
                if (upwindElev > MountainBaseQ16 && upwindElev > elevationQ16 + 2000)
                {
                    int drop = upwindElev - elevationQ16;
                    rainShadowQ16 = (ushort)Math.Clamp((int)((long)drop * macroParams.RainShadowStrength * 2 / Q16One), 0, Q16One);
                }
            }
        }

        // ---------------- 5. ВЛАЖНОСТЬ И ОСАДКИ (Планетарная 3-ячеечная модель) ----------------
        // Крупномасштабный синоптический шум влажности (scale=256, 128, 64) — плавные климатические зоны без камуфляжной пятнистости
        ushort moistNoise1 = CoordinateHash.ValueNoiseQ16(worldSeed + 55555ul, generatorVersion,
            GenerationDomain.MoistureLarge, safeRx, safeRy, 256);
        ushort moistNoise2 = CoordinateHash.ValueNoiseQ16(worldSeed + 66666ul, generatorVersion,
            GenerationDomain.MoistureLarge, safeRx, safeRy, 128);
        ushort moistNoise3 = CoordinateHash.ValueNoiseQ16(worldSeed + 77777ul, generatorVersion,
            GenerationDomain.MoistureLarge, safeRx, safeRy, 64);
        int rawMoist = (int)(((long)moistNoise1 * 60 + (long)moistNoise2 * 30 + (long)moistNoise3 * 10) / 100);

        // 1. Экваториальный пояс (ITCZ, Внутритропическая зона конвергенции: normLat < 0.16)
        // Мощный конвективный подъем теплого воздуха: тропические ливни по ВСЕЙ ширине материка (Амазонка, Конго)!
        float itczFactor = 0f;
        if (normLat < 0.16f)
        {
            itczFactor = (1f - normLat / 0.16f);
        }
        int itczBonus = (int)(itczFactor * 26000); // Гарантирует тропические дождевые леса на экваторе

        // 2. Субтропический пояс высокого давления (Ячейки Гадлея: normLat 0.20..0.36)
        // Нисходящие сухие массы воздуха: область максимальной аридности планеты (Сахара, Аравия, Калахари, Виктория)
        float hadleyDrying = 0f;
        if (normLat >= 0.18f && normLat <= 0.36f)
        {
            float peak = 1f - Math.Abs(normLat - 0.26f) / 0.08f;
            hadleyDrying = Math.Max(0f, peak) * 0.50f;
        }

        // 3. Умеренный пояс западного переноса (normLat 0.36..0.56):
        // Океанские циклоны приносят влагу на континенты
        float westerliesMoisture = 0f;
        if (normLat >= 0.36f && normLat <= 0.56f)
        {
            float wPeak = 1f - Math.Abs(normLat - 0.46f) / 0.10f;
            westerliesMoisture = Math.Max(0f, wPeak) * 0.15f;
        }

        // Континентальное иссушение внутри материков (только вне зоны ITCZ)
        float continentalDrying = (float)continentalnessQ16 / Q16One * (0.28f * (1f - itczFactor));

        // Океаническое увлажнение побережий
        int oceanBonus = (int)((long)oceanInfluenceQ16 * 14000 / Q16One);

        int moistVal = (int)(rawMoist * (1f - hadleyDrying - continentalDrying + westerliesMoisture)) + itczBonus + oceanBonus;
        moistVal -= (int)(rainShadowQ16 * 0.55f);
        ushort moistureQ16 = (ushort)Math.Clamp(moistVal, 0, Q16One);

        // Осадки
        int precipVal = (int)(moistureQ16 * (0.55f + itczFactor * 0.45f));
        ushort precipitationQ16 = (ushort)Math.Clamp(precipVal, 0, Q16One);

        // Аридность (сухость)
        int aridityVal = 0;
        if (temperatureQ16 > 30000)
        {
            int heatExcess = temperatureQ16 - 30000;
            int moistureDeficit = Math.Max(0, 42000 - moistureQ16);
            aridityVal = (int)((heatExcess * 0.45f + moistureDeficit * 0.55f) * (1f + hadleyDrying * 1.5f));
        }
        else if (normLat > 0.70f)
        {
            // Полярная холодная сухость
            aridityVal = (int)((normLat - 0.70f) / 0.30f * 20000);
        }
        ushort aridityQ16 = (ushort)Math.Clamp(aridityVal, 0, Q16One);

        // ---------------- 6. ГИДРОЛОГИЯ, ЛЕС, ПОЧВА, КАМНИ ----------------
        ushort drainageQ16 = CoordinateHash.ValueNoiseQ16(worldSeed + 12345ul, generatorVersion,
            GenerationDomain.ElevationDetail, safeRx, safeRy, 16);

        int vegVal = 0;
        if (temperatureQ16 > 15000 && aridityQ16 < 48000)
        {
            vegVal = (int)((moistureQ16 * 0.65f + temperatureQ16 * 0.35f) * (1f - aridityQ16 / (float)Q16One));
        }
        ushort vegetationDensityQ16 = (ushort)Math.Clamp(vegVal, 0, Q16One);

        int fertVal = (int)(vegetationDensityQ16 * 0.65f + moistureQ16 * 0.25f + drainageQ16 * 0.10f);
        if (temperatureQ16 < 14000 || aridityQ16 > 44000)
            fertVal = (int)(fertVal * 0.15f);
        ushort soilFertilityQ16 = (ushort)Math.Clamp(fertVal, 0, Q16One);

        // Скалистость: зависит от реальной высоты и рельефа, а не от границы Вороного!
        int rockVal = 3000;
        if (elevationQ16 > MountainBaseQ16)
        {
            int altOverBase = elevationQ16 - MountainBaseQ16;
            rockVal = 24000 + altOverBase * (Q16One - 24000) / (Q16One - MountainBaseQ16);
        }
        ushort rockExposureQ16 = (ushort)Math.Clamp(rockVal, 0, Q16One);

        // ---------------- 7. ОЛЕДЕНЕНИЕ ----------------
        // Строго полярные шапки (|lat| > 0.80) или экстремальный мороз
        ushort glaciationQ16 = 0;
        if (temperatureQ16 < 13000)
        {
            int cold = 13000 - temperatureQ16;
            glaciationQ16 = (ushort)Math.Clamp((int)(cold * 4.0f), 0, Q16One);
        }
        if (normLat > 0.80f)
        {
            float polarFactor = (normLat - 0.80f) / 0.20f;
            int cap = (int)(polarFactor * macroParams.IceCapStrength * 3.5f);
            glaciationQ16 = (ushort)Math.Clamp(glaciationQ16 + cap, 0, Q16One);
        }

        ushort riverDensityQ16 = (ushort)Math.Clamp((int)(precipitationQ16 * 0.7f + drainageQ16 * 0.3f), 0, Q16One);

        return new WorldClimateParams(
            ElevationQ16: elevationQ16,
            ContinentalnessQ16: continentalnessQ16,
            LatitudeQ16: latitudeQ16,
            TemperatureQ16: temperatureQ16,
            MoistureQ16: moistureQ16,
            PrecipitationQ16: precipitationQ16,
            DrainageQ16: drainageQ16,
            WindHeadingQ16: windHeadingQ16,
            RainShadowQ16: rainShadowQ16,
            TectonicPressureQ16: tectonicPressureQ16,
            OceanInfluenceQ16: oceanInfluenceQ16,
            VegetationDensityQ16: vegetationDensityQ16,
            SoilFertilityQ16: soilFertilityQ16,
            RockExposureQ16: rockExposureQ16,
            GlaciationQ16: glaciationQ16,
            AridityQ16: aridityQ16,
            ArchipelagoTendencyQ16: archipelagoTendencyQ16,
            RiverDensityQ16: riverDensityQ16);
    }

    /// <summary>
    /// Билинейная интерполяция 18 характеристик между четырьмя регионами.
    /// Гарантирует отсутствие любых швов на стыках регионов.
    /// </summary>
    public static WorldClimateParams BlendBilinear(
        in WorldClimateParams nw, in WorldClimateParams ne,
        in WorldClimateParams sw, in WorldClimateParams se,
        uint u, uint v)
    {
        return new WorldClimateParams(
            ElevationQ16: (ushort)Lerp2D(nw.ElevationQ16, ne.ElevationQ16, sw.ElevationQ16, se.ElevationQ16, u, v),
            ContinentalnessQ16: (ushort)Lerp2D(nw.ContinentalnessQ16, ne.ContinentalnessQ16, sw.ContinentalnessQ16, se.ContinentalnessQ16, u, v),
            LatitudeQ16: (ushort)Lerp2D(nw.LatitudeQ16, ne.LatitudeQ16, sw.LatitudeQ16, se.LatitudeQ16, u, v),
            TemperatureQ16: (ushort)Lerp2D(nw.TemperatureQ16, ne.TemperatureQ16, sw.TemperatureQ16, se.TemperatureQ16, u, v),
            MoistureQ16: (ushort)Lerp2D(nw.MoistureQ16, ne.MoistureQ16, sw.MoistureQ16, se.MoistureQ16, u, v),
            PrecipitationQ16: (ushort)Lerp2D(nw.PrecipitationQ16, ne.PrecipitationQ16, sw.PrecipitationQ16, se.PrecipitationQ16, u, v),
            DrainageQ16: (ushort)Lerp2D(nw.DrainageQ16, ne.DrainageQ16, sw.DrainageQ16, se.DrainageQ16, u, v),
            WindHeadingQ16: (ushort)Lerp2D(nw.WindHeadingQ16, ne.WindHeadingQ16, sw.WindHeadingQ16, se.WindHeadingQ16, u, v),
            RainShadowQ16: (ushort)Lerp2D(nw.RainShadowQ16, ne.RainShadowQ16, sw.RainShadowQ16, se.RainShadowQ16, u, v),
            TectonicPressureQ16: (ushort)Lerp2D(nw.TectonicPressureQ16, ne.TectonicPressureQ16, sw.TectonicPressureQ16, se.TectonicPressureQ16, u, v),
            OceanInfluenceQ16: (ushort)Lerp2D(nw.OceanInfluenceQ16, ne.OceanInfluenceQ16, sw.OceanInfluenceQ16, se.OceanInfluenceQ16, u, v),
            VegetationDensityQ16: (ushort)Lerp2D(nw.VegetationDensityQ16, ne.VegetationDensityQ16, sw.VegetationDensityQ16, se.VegetationDensityQ16, u, v),
            SoilFertilityQ16: (ushort)Lerp2D(nw.SoilFertilityQ16, ne.SoilFertilityQ16, sw.SoilFertilityQ16, se.SoilFertilityQ16, u, v),
            RockExposureQ16: (ushort)Lerp2D(nw.RockExposureQ16, ne.RockExposureQ16, sw.RockExposureQ16, se.RockExposureQ16, u, v),
            GlaciationQ16: (ushort)Lerp2D(nw.GlaciationQ16, ne.GlaciationQ16, sw.GlaciationQ16, se.GlaciationQ16, u, v),
            AridityQ16: (ushort)Lerp2D(nw.AridityQ16, ne.AridityQ16, sw.AridityQ16, se.AridityQ16, u, v),
            ArchipelagoTendencyQ16: (ushort)Lerp2D(nw.ArchipelagoTendencyQ16, ne.ArchipelagoTendencyQ16, sw.ArchipelagoTendencyQ16, se.ArchipelagoTendencyQ16, u, v),
            RiverDensityQ16: (ushort)Lerp2D(nw.RiverDensityQ16, ne.RiverDensityQ16, sw.RiverDensityQ16, se.RiverDensityQ16, u, v));
    }

    private static uint LerpQ16(uint a, uint b, uint t)
    {
        uint clampedT = Math.Min(t, (uint)Q16One);
        long value = (long)a * (Q16One - clampedT) + (long)b * clampedT + Q16Half;
        return (uint)Math.Clamp(value / Q16One, 0, Q16One);
    }

    private static uint Lerp2D(uint nw, uint ne, uint sw, uint se, uint u, uint v)
    {
        uint top = LerpQ16(nw, ne, u);
        uint bottom = LerpQ16(sw, se, u);
        return LerpQ16(top, bottom, v);
    }
}
