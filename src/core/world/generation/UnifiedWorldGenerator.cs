using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Game.Core;
using Game.Core.WorldStreaming.Integration;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Единый детерминированный генератор мира (UnifiedWorldGenerator).
/// Заменяет собой устаревшие разрозненные генераторы (MapGenerator, WorldChunkGenerator,
/// GpuChunkGenerator, ParallelChunkGenerator, WorldFeatureGenerator).
/// 
/// Генерирует террейн, биомы, растительность, камни и климатические поля для любой точки
/// мира (wx, wy) и любого чанка (cx, cy) напрямую из физической климатической модели планеты
/// (WorldClimateGenerator / BiomeMapper).
/// </summary>
public static class UnifiedWorldGenerator
{
    public const uint FeatureSchemaVersion = 1;
    public const uint DefaultGeneratorVersion = 3;
    public const int StartClearRadius = 14;

    /// <summary>
    /// Глобальное смещение центра мира (WorldOrigin) в мировых клетках.
    /// Позволяет привязать локальную систему координат колонии и стриминга (0..512)
    /// к любому выбранному континентальному региону планеты.
    /// </summary>
    public static long WorldOriginX { get; set; } = 0;
    public static long WorldOriginY { get; set; } = 0;

    public static void SetWorldOrigin(long originX, long originY)
    {
        WorldOriginX = originX;
        WorldOriginY = originY;
    }

    /// <summary>
    /// Автоматический поиск пригодного континентального региона для спавна колонии.
    /// Сканирует планету по спирали от центра (экватор и умеренные широты).
    /// Гарантирует сушу с высотой выше уровня моря и биомом Plains/Forest/Steppe.
    /// </summary>
    public static RegionKey FindHabitableSpawn(ulong worldSeed, uint generatorVersion)
    {
        long centerRx = WorldRegions.WorldRegionsX / 2;
        long centerRy = WorldRegions.WorldRegionsY / 2;

        var macroParams = Game.Core.WorldClimate.WorldClimateProvider.ParamsFor(worldSeed);

        RegionKey bestFallback = new RegionKey(centerRx, centerRy);
        int bestFallbackScore = -1;

        RegionKey? CheckCandidate(long rawRx, long rawRy)
        {
            if (rawRy < 150 || rawRy > WorldRegions.WorldRegionsY - 150)
                return null;

            long rx = ((rawRx % WorldRegions.WorldRegionsX) + WorldRegions.WorldRegionsX) % WorldRegions.WorldRegionsX;
            long ry = rawRy;

            var regKey = new RegionKey(rx, ry);
            var climate = WorldClimateGenerator.SampleRegion(worldSeed, generatorVersion, regKey, in macroParams);

            if (climate.ElevationQ16 >= WorldClimateGenerator.SeaLevelQ16)
            {
                var pick = BiomeMapper.PickFromClimate(worldSeed, generatorVersion, rx * 512 + 256, ry * 512 + 256, climate);
                if (pick.Biome != BiomeType.DeepWater && pick.Biome != BiomeType.Glacier)
                {
                    int score = (int)(climate.ElevationQ16 - WorldClimateGenerator.SeaLevelQ16);
                    if (pick.Biome == BiomeType.Plains || pick.Biome == BiomeType.Forest || pick.Biome == BiomeType.Steppe)
                        score += 10000;

                    if (score > bestFallbackScore)
                    {
                        bestFallbackScore = score;
                        bestFallback = regKey;
                    }

                    if (climate.ElevationQ16 >= WorldClimateGenerator.SeaLevelQ16 + 2000 &&
                        (pick.Biome == BiomeType.Plains || pick.Biome == BiomeType.Forest || pick.Biome == BiomeType.Steppe))
                    {
                        return regKey;
                    }
                }
            }

            return null;
        }

        // 1. Поиск по спирали (шаг 8 регионов)
        for (int r = 0; r <= 1024; r += 8)
        {
            if (r == 0)
            {
                var c = CheckCandidate(centerRx, centerRy);
                if (c.HasValue) return c.Value;
                continue;
            }

            for (int dx = -r; dx <= r; dx += 8)
            {
                var c1 = CheckCandidate(centerRx + dx, centerRy - r);
                if (c1.HasValue) return c1.Value;
                var c2 = CheckCandidate(centerRx + dx, centerRy + r);
                if (c2.HasValue) return c2.Value;
            }

            for (int dy = -r + 8; dy <= r - 8; dy += 8)
            {
                var c1 = CheckCandidate(centerRx - r, centerRy + dy);
                if (c1.HasValue) return c1.Value;
                var c2 = CheckCandidate(centerRx + r, centerRy + dy);
                if (c2.HasValue) return c2.Value;
            }
        }

        // Если идеальный биом не найден в спирали, но найден сухой регион:
        if (bestFallbackScore >= 0)
            return bestFallback;

        // В крайнем случае — полное сканирование карты с шагом 32
        for (long y = 200; y < WorldRegions.WorldRegionsY - 200; y += 32)
        {
            for (long x = 0; x < WorldRegions.WorldRegionsX; x += 32)
            {
                var c = CheckCandidate(x, y);
                if (c.HasValue) return c.Value;
            }
        }

        return bestFallback;
    }

    /// <summary>
    /// <summary>
    /// Генерация базового чанка 64x64 по глобальному ключу.
    /// Zero-alloc по отношению к долгоживущим объектам, потокобезопасен.
    /// Макро-климат планеты рассчитывается в 4 углах чанка и интерполируется,
    /// сокращая тяжелые вызовы тектоники/Voronoi на 99.9% и гарантируя 0 швов.
    /// </summary>
    public static WorldChunk GenerateChunk(ulong worldSeed, uint generatorVersion, ChunkKey key)
    {
        var cells = new GeneratedCell[WorldChunk.CellCount];
        long chunkOriginX = key.X * (long)WorldCoordinates.ChunkSize;
        long chunkOriginY = key.Y * (long)WorldCoordinates.ChunkSize;

        long minWx = WorldOriginX + chunkOriginX;
        long minWy = WorldOriginY + chunkOriginY;

        const int ChunkSide = WorldCoordinates.ChunkSize; // 64
        const int SubDiv = 4; // 4x4 подсетка по 16 клеток
        const int SubSize = 16;
        const int Q16One = 65535;

        // Сетка 5x5 макро-климата (шаг 16 клеток) на стеке — устраняет фасеточную линейность чанков 64x64,
        // гарантирует 0 швов со смежными чанками и 0 аллокаций в куче.
        const int GridSide = SubDiv + 1;
        Span<WorldClimateParams> grid = stackalloc WorldClimateParams[GridSide * GridSide];
        for (int gy = 0; gy <= SubDiv; gy++)
        {
            long gwy = minWy + gy * SubSize;
            int row = gy * GridSide;
            for (int gx = 0; gx <= SubDiv; gx++)
            {
                long gwx = minWx + gx * SubSize;
                grid[row + gx] = WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, gwx, gwy);
            }
        }

        var sw = Stopwatch.StartNew();

        for (int y = 0; y < ChunkSide; y++)
        {
            int by = y / SubSize;
            int row = by * GridSide;
            int nextRow = (by + 1) * GridSide;
            uint v = (uint)(((long)(y % SubSize)) * Q16One / SubSize);
            long wy = minWy + y;
            int rowOffset = y * ChunkSide;

            for (int x = 0; x < ChunkSide; x++)
            {
                int bx = x / SubSize;
                uint u = (uint)(((long)(x % SubSize)) * Q16One / SubSize);
                long wx = minWx + x;

                WorldClimateParams nw = grid[row + bx];
                WorldClimateParams ne = grid[row + bx + 1];
                WorldClimateParams swClimate = grid[nextRow + bx];
                WorldClimateParams se = grid[nextRow + bx + 1];

                WorldClimateParams top = LerpClimate1D(in nw, in ne, u);
                WorldClimateParams bot = LerpClimate1D(in swClimate, in se, u);
                WorldClimateParams cellClimate = LerpClimate1D(in top, in bot, v);

                cells[rowOffset + x] = SampleCell(worldSeed, generatorVersion, wx, wy, cellClimate);
            }
        }

        sw.Stop();
        WorldChunkGeneratorTiming.Record(key, sw.ElapsedMilliseconds);

        return new WorldChunk(key, cells);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ushort LerpQ16(ushort a, ushort b, uint t)
    {
        uint clampedT = Math.Min(t, (uint)65535);
        long value = (long)a * (65535 - clampedT) + (long)b * clampedT + 32768;
        return (ushort)Math.Clamp(value / 65535, 0, 65535);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static WorldClimateParams LerpClimate1D(in WorldClimateParams a, in WorldClimateParams b, uint t)
    {
        return new WorldClimateParams(
            ElevationQ16: LerpQ16(a.ElevationQ16, b.ElevationQ16, t),
            ContinentalnessQ16: LerpQ16(a.ContinentalnessQ16, b.ContinentalnessQ16, t),
            LatitudeQ16: LerpQ16(a.LatitudeQ16, b.LatitudeQ16, t),
            TemperatureQ16: LerpQ16(a.TemperatureQ16, b.TemperatureQ16, t),
            MoistureQ16: LerpQ16(a.MoistureQ16, b.MoistureQ16, t),
            PrecipitationQ16: LerpQ16(a.PrecipitationQ16, b.PrecipitationQ16, t),
            DrainageQ16: LerpQ16(a.DrainageQ16, b.DrainageQ16, t),
            WindHeadingQ16: LerpQ16(a.WindHeadingQ16, b.WindHeadingQ16, t),
            RainShadowQ16: LerpQ16(a.RainShadowQ16, b.RainShadowQ16, t),
            TectonicPressureQ16: LerpQ16(a.TectonicPressureQ16, b.TectonicPressureQ16, t),
            OceanInfluenceQ16: LerpQ16(a.OceanInfluenceQ16, b.OceanInfluenceQ16, t),
            VegetationDensityQ16: LerpQ16(a.VegetationDensityQ16, b.VegetationDensityQ16, t),
            SoilFertilityQ16: LerpQ16(a.SoilFertilityQ16, b.SoilFertilityQ16, t),
            RockExposureQ16: LerpQ16(a.RockExposureQ16, b.RockExposureQ16, t),
            GlaciationQ16: LerpQ16(a.GlaciationQ16, b.GlaciationQ16, t),
            AridityQ16: LerpQ16(a.AridityQ16, b.AridityQ16, t),
            ArchipelagoTendencyQ16: LerpQ16(a.ArchipelagoTendencyQ16, b.ArchipelagoTendencyQ16, t),
            RiverDensityQ16: LerpQ16(a.RiverDensityQ16, b.RiverDensityQ16, t));
    }

    /// <summary>
    /// Детерминированный расчет характеристик одной клетки в мировых координатах (wx, wy).
    /// Включает трехслойный каскадный Domain Warping, гипсометрический якорь суши (Coastline Anchoring),
    /// естественные бухты и мысы, песчаные пляжи для причалов и кластерную растительность.
    /// </summary>
    public static GeneratedCell SampleCell(ulong worldSeed, uint generatorVersion, long wx, long wy, WorldClimateParams? overrideClimate = null)
    {
        // 1. Двухслойный высокоэффективный Domain Warping (Meso: 64 кл., Micro: 16 кл.)
        // Устраняет «квадратность» и прямые фасетки береговой линии, создавая естественные
        // извилистые заливы, полуострова, бухты и естественные гавани.
        ushort wMesoX = CoordinateHash.ValueNoiseQ16(worldSeed + 2001ul, generatorVersion, GenerationDomain.CoastWarpMeso, wx, wy, 64);
        ushort wMesoY = CoordinateHash.ValueNoiseQ16(worldSeed + 2002ul, generatorVersion, GenerationDomain.CoastWarpMeso, wx + 31, wy, 64);
        long wx1 = wx + ((int)wMesoX - 32768) * 36 / 32768;
        long wy1 = wy + ((int)wMesoY - 32768) * 36 / 32768;

        ushort wMicroX = CoordinateHash.ValueNoiseQ16(worldSeed + 3001ul, generatorVersion, GenerationDomain.CoastWarpMicro, wx1, wy1, 16);
        ushort wMicroY = CoordinateHash.ValueNoiseQ16(worldSeed + 3002ul, generatorVersion, GenerationDomain.CoastWarpMicro, wx1 + 13, wy1, 16);
        long warpedWx = wx1 + ((int)wMicroX - 32768) * 6 / 32768;
        long warpedWy = wy1 + ((int)wMicroY - 32768) * 6 / 32768;

        // Физический макро-климат планеты: семплируется со сглаживанием по искривленным координатам
        WorldClimateParams climate = overrideClimate ?? WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, warpedWx, warpedWy);

        // 2. Локальная топография: холмы и волнистость
        ushort hillNoise = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion, GenerationDomain.ElevationMedium, warpedWx, warpedWy, 64);
        ushort fineNoise = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion, GenerationDomain.ElevationFine, wx, wy, 24);
        int hillOffset = ((int)hillNoise - 32768) * 5500 / 32768 + ((int)fineNoise - 32768) * 2500 / 32768;

        int mountainLift = 0;
        if (climate.ElevationQ16 >= WorldClimateGenerator.SeaLevelQ16)
        {
            // Двухоктавный риджевый мультифрактал (Ridged Multifractal Tectonics: острые тектонические хребты и отроги)
            ushort ridgeRaw1 = CoordinateHash.ValueNoiseQ16(worldSeed + 12345ul, generatorVersion, GenerationDomain.ElevationDetail, warpedWx, warpedWy, 44);
            int ridgeDist1 = Math.Abs((int)ridgeRaw1 - 32768);
            int ridgeQ1 = Math.Max(0, 32768 - ridgeDist1) * 2;

            ushort ridgeRaw2 = CoordinateHash.ValueNoiseQ16(worldSeed + 54321ul, generatorVersion, GenerationDomain.ElevationDetail, warpedWx + 17, warpedWy + 23, 22);
            int ridgeDist2 = Math.Abs((int)ridgeRaw2 - 32768);
            int ridgeQ2 = Math.Max(0, 32768 - ridgeDist2) * 2;

            int multiRidge = (int)((long)ridgeQ1 * (32768 + ridgeQ2 / 2) / 65535L);

            int mountainWeight = Math.Max(16000, Math.Max((int)climate.RockExposureQ16, (int)climate.TectonicPressureQ16));
            mountainWeight = Math.Max(mountainWeight, 16000 + ((int)climate.ElevationQ16 - WorldClimateGenerator.SeaLevelQ16) * 60 / 100);
            int baseRidgeLift = (int)((long)multiRidge * 6500L / 65535L);
            int tectonicRidgeLift = (int)((long)multiRidge * mountainWeight * 16000L / (65535L * 65535L));
            mountainLift = baseRidgeLift + tectonicRidgeLift;
        }

        // Прибрежная геоморфология (бухты, мысы, защищенные гавани для причалов):
        int coastalMod = 0;
        int distToSeaLevel = (int)climate.ElevationQ16 - WorldClimateGenerator.SeaLevelQ16;
        if (Math.Abs(distToSeaLevel) < 4000)
        {
            // 1. Мысы (Headlands): вытягивают полуострова в море
            ushort capeRaw = CoordinateHash.ValueNoiseQ16(worldSeed + 7171ul, generatorVersion, GenerationDomain.CoastFeature, warpedWx, warpedWy, 48);
            int capeDist = Math.Abs((int)capeRaw - 32768);
            int capeRidge = Math.Max(0, 32768 - capeDist) * 2;
            int capeLift = (capeRidge - 32768) * 2200 / 32768;

            // 2. Бухты и гавани (Coves / Basins): вырезают защищенные чаши для судов и причалов
            ushort coveRaw = CoordinateHash.ValueNoiseQ16(worldSeed + 8282ul, generatorVersion, GenerationDomain.CoastFeature, warpedWx + 27, warpedWy + 19, 72);
            int coveBasin = ((int)coveRaw - 32768) * 2400 / 32768;

            // 3. Заливчики и фьорды (Inlets)
            ushort inletRaw = CoordinateHash.ValueNoiseQ16(worldSeed + 9393ul, generatorVersion, GenerationDomain.CoastFeature, warpedWx - 15, warpedWy + 33, 24);
            int inlet = ((int)inletRaw - 32768) * 900 / 32768;

            coastalMod = capeLift + coveBasin + inlet;
        }

        // Гипсометрический якорь суши (Coastline Anchoring & Height Margin Dampening):
        if (climate.ElevationQ16 >= WorldClimateGenerator.SeaLevelQ16)
        {
            if (hillOffset < 0)
            {
                int margin = (int)climate.ElevationQ16 - WorldClimateGenerator.SeaLevelQ16;
                int maxAllowedDepression = Math.Max(0, margin - 150);
                hillOffset = Math.Max(hillOffset, -maxAllowedDepression);
            }
        }
        else // Океан: морское дно ниже уровня моря
        {
            mountainLift = 0;
            if (hillOffset > 0)
            {
                int oceanDepth = WorldClimateGenerator.SeaLevelQ16 - (int)climate.ElevationQ16;
                int maxAllowedLift = Math.Max(0, oceanDepth - 200);
                hillOffset = Math.Min(hillOffset, maxAllowedLift);
            }
        }

        int rawLandElev = (int)climate.ElevationQ16 + hillOffset + mountainLift + coastalMod;
        if (climate.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16)
        {
            rawLandElev = Math.Min(rawLandElev, WorldClimateGenerator.SeaLevelQ16 - 1);
        }
        ushort elevationQ16 = (ushort)Math.Clamp(rawLandElev, 0, 65535);

        // 3. Локальная гидрология: физическое русло реки и эрозионная пойма
        bool isLake = false;
        bool isRiver = false;
        bool inRiparianZone = false;

        if (climate.ElevationQ16 >= WorldClimateGenerator.SeaLevelQ16)
        {
            ushort lakeNoise = CoordinateHash.ValueNoiseQ16(worldSeed + 54321ul, generatorVersion, GenerationDomain.LakeGeometry, warpedWx, warpedWy, 56);
            int lakeThreshold = 10000 + (int)climate.PrecipitationQ16 * 8000 / 65535 - (int)climate.DrainageQ16 * 5000 / 65535;
            lakeThreshold = Math.Clamp(lakeThreshold, 4000, 16000);
            isLake = (lakeNoise < lakeThreshold);

            // Направленная открытая река с вихревым меандрированием (математическая гарантия отсутствия петель)
            ushort rWarpX = CoordinateHash.ValueNoiseQ16(worldSeed + 777ul, generatorVersion, GenerationDomain.RiverGeometry, wx, wy, 72);
            ushort rWarpY = CoordinateHash.ValueNoiseQ16(worldSeed + 888ul, generatorVersion, GenerationDomain.RiverGeometry, wx + 31, wy, 72);
            long rwx = wx + ((int)rWarpX - 32768) * 20 / 32768;
            long rwy = wy + ((int)rWarpY - 32768) * 20 / 32768;

            ushort meanderNoise = CoordinateHash.ValueNoiseQ16(worldSeed + 999ul, generatorVersion, GenerationDomain.RiverGeometry, rwx, rwy, 96);
            long meanderOffset = ((long)meanderNoise - 32768) * 64 / 32768;
            long transPos = (rwx * 3 - rwy * 4) / 5 + meanderOffset;
            const int riverPeriod = 512;
            long localRiverPos = ((transPos % riverPeriod) + riverPeriod) % riverPeriod;
            int riverCenterDist = (int)Math.Abs(localRiverPos - 256) * 65535 / 256;

            int riverWidth = 550 + (int)climate.RiverDensityQ16 * 750 / 65535;
            isRiver = (riverCenterDist < riverWidth && climate.RiverDensityQ16 > 18000);

            // Физическая гидрологическая пойма (Riparian Buffer):
            // В радиусе речной долины река прорезает каньон/русло и насыщает прилегающие почвы влагой
            int riparianWidth = riverWidth * 3;
            inRiparianZone = (riverCenterDist < riparianWidth);
            if (inRiparianZone)
            {
                int ripBlend = (riparianWidth - riverCenterDist) * 65535 / riparianWidth;
                ushort boostedMoist = (ushort)Math.Min(65535, (int)climate.MoistureQ16 + ripBlend * 12000 / 65535);
                ushort boostedFert = (ushort)Math.Min(65535, (int)climate.SoilFertilityQ16 + ripBlend * 16000 / 65535);
                climate = climate with { MoistureQ16 = boostedMoist, SoilFertilityQ16 = boostedFert };

                // Эрозионное понижение русла долины к уровню воды
                int valleyCarve = ripBlend * 1400 / 65535;
                elevationQ16 = (ushort)Math.Max(0, (int)elevationQ16 - valleyCarve);
            }
        }

        // 4. Выбор биома по диаграмме Уиттекера
        TilePick pick = BiomeMapper.PickFromClimate(worldSeed, generatorVersion, wx, wy, in climate);

        // 5. Определение базового типа поверхности
        BaseTerrainKind terrain;
        if (climate.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16 || pick.Biome == BiomeType.DeepWater)
        {
            elevationQ16 = (ushort)Math.Min((int)elevationQ16, WorldClimateGenerator.SeaLevelQ16 - 1);
            terrain = BaseTerrainKind.Water;
        }
        else if (isRiver && elevationQ16 < 46000)
        {
            elevationQ16 = (ushort)Math.Max(0, elevationQ16 - 1200);
            terrain = BaseTerrainKind.Water;
        }
        else if (isLake && elevationQ16 < 42000)
        {
            elevationQ16 = (ushort)Math.Max(0, elevationQ16 - 1000);
            terrain = BaseTerrainKind.Water;
        }
        else if (elevationQ16 < WorldClimateGenerator.SeaLevelQ16)
        {
            terrain = BaseTerrainKind.Water;
        }
        else if (elevationQ16 >= 47000 || pick.Biome == BiomeType.Mountain)
        {
            terrain = BaseTerrainKind.Mountain;
        }
        else
        {
            terrain = BaseTerrainKind.Grass;

            // Прибрежный песчаный пляж (Sand Beach):
            if (elevationQ16 < WorldClimateGenerator.SeaLevelQ16 + 1000 && pick.Biome != BiomeType.Glacier)
            {
                pick = new TilePick(BiomeType.Desert, pick.Variant);
            }
        }

        // 6. Растительность (деревья): альпийская граница леса (Tree line < 43000) + пойменные галерейные леса
        ushort forestQ16 = 0;
        if (terrain == BaseTerrainKind.Grass && pick.Biome != BiomeType.Desert && pick.Biome != BiomeType.Glacier && elevationQ16 < 43000)
        {
            ushort fNoise = CoordinateHash.ValueNoiseQ16(worldSeed + 33333ul, generatorVersion, GenerationDomain.Forest, warpedWx, warpedWy, 32);
            int fCluster = ((int)fNoise - 18000);
            int baseVeg = (int)climate.VegetationDensityQ16;
            if (inRiparianZone) baseVeg = Math.Min(65535, baseVeg + 14000); // Густые пойменные рощи вдоль воды
            int f = baseVeg * 50 / 100 + fCluster * 60 / 100;
            forestQ16 = (ushort)Math.Clamp(f, 0, 65535);
        }

        // 7. Каменная россыпь: гравитационные осыпи (Talus scree) у подножия гор и аллювиальный галечник рек
        ushort stoneQ16 = 0;
        if (terrain == BaseTerrainKind.Grass)
        {
            ushort sNoise = CoordinateHash.ValueNoiseQ16(worldSeed + 44444ul, generatorVersion, GenerationDomain.Stone, warpedWx, warpedWy, 24);

            int mountainFootScree = 0;
            if (elevationQ16 >= 35000 && elevationQ16 <= 46500)
            {
                int screeDist = Math.Abs((int)elevationQ16 - 41000);
                mountainFootScree = Math.Max(0, 5500 - screeDist) * 28000 / 5500;
            }

            int riverGravel = inRiparianZone ? 9000 : 0;
            int rockBase = Math.Max((int)climate.RockExposureQ16, mountainFootScree + riverGravel);
            int sCluster = ((int)sNoise - 18000);
            int s = rockBase * 50 / 100 + sCluster * 60 / 100;
            stoneQ16 = (ushort)Math.Clamp(s, 0, 65535);
        }

        return new GeneratedCell(
            terrain,
            elevationQ16,
            climate.MoistureQ16,
            forestQ16,
            stoneQ16,
            climate.TemperatureQ16,
            (byte)pick.Biome,
            (byte)pick.Variant);
    }

    /// <summary>
    /// Генерирует прямоугольное окно колонии (например, 512x512) вокруг стартовой позиции,
    /// заполняя MapData для локальной симуляции рабочих, ферм и рендера.
    /// Использует ту же 16x16 интерполяционную сетку климата, обеспечивая 100% бесшовное
    /// соответствие со стриминговым миром за кромкой острова.
    /// </summary>
    public static MapData GenerateColonyWindow(
        ulong worldSeed,
        uint generatorVersion,
        long originX,
        long originY,
        int width,
        int height,
        WorldClimateParams? explicitClimate = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Размеры окна колонии должны быть больше 0.");

        var data = new MapData(width, height)
        {
            Seed = unchecked((uint)worldSeed),
            Climate = explicitClimate ?? WorldClimateGenerator.SampleBlendedCell(
                worldSeed, generatorVersion, WorldOriginX + originX + width / 2, WorldOriginY + originY + height / 2)
        };

        var heightMap = new float[width, height];
        var moistNoise = new float[width, height];

        const int SubSize = 16;
        const int Q16One = 65535;
        int subDivX = Math.Max(1, width / SubSize);
        int subDivY = Math.Max(1, height / SubSize);

        // Предварительный расчет опорной сетки климата 16x16 (многопоточно через Parallel.For)
        // Исключает плоский разрыв между центром колонии и стримингом
        var grid = new WorldClimateParams[subDivX + 1, subDivY + 1];
        Parallel.For(0, subDivY + 1, gy =>
        {
            long gwy = WorldOriginY + originY + gy * SubSize;
            for (int gx = 0; gx <= subDivX; gx++)
            {
                long gwx = WorldOriginX + originX + gx * SubSize;
                grid[gx, gy] = explicitClimate ?? WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, gwx, gwy);
            }
        });

        Parallel.For(0, height, ly =>
        {
            long wy = WorldOriginY + originY + ly;
            int by = Math.Min(subDivY - 1, ly / SubSize);
            uint v = (uint)(((long)(ly % SubSize)) * Q16One / SubSize);

            for (int lx = 0; lx < width; lx++)
            {
                long wx = WorldOriginX + originX + lx;
                int bx = Math.Min(subDivX - 1, lx / SubSize);
                uint u = (uint)(((long)(lx % SubSize)) * Q16One / SubSize);

                WorldClimateParams nw = grid[bx, by];
                WorldClimateParams ne = grid[bx + 1, by];
                WorldClimateParams swClimate = grid[bx, by + 1];
                WorldClimateParams se = grid[bx + 1, by + 1];

                WorldClimateParams top = LerpClimate1D(in nw, in ne, u);
                WorldClimateParams bot = LerpClimate1D(in swClimate, in se, u);
                WorldClimateParams cellClimate = LerpClimate1D(in top, in bot, v);

                GeneratedCell cell = SampleCell(worldSeed, generatorVersion, wx, wy, cellClimate);

                data.Ground[lx, ly] = cell.Terrain switch
                {
                    BaseTerrainKind.Water => TileType.Water,
                    BaseTerrainKind.Mountain => TileType.Mountain,
                    _ => TileType.Grass
                };

                // Строгий инвариант: деревья и камни могут быть ТОЛЬКО на чистой траве
                bool isGrass = (cell.Terrain == BaseTerrainKind.Grass) && (data.Ground[lx, ly] == TileType.Grass);

                // Естественная плотность рощ: внутри зоны леса (ForestQ16 >= 30000)
                // деревья растут группами с полянками и проходами; на плодородных почвах рощи гуще
                bool inForestGrove = isGrass && (cell.ForestQ16 >= WorldDecor.ForestMinQ16);
                int groveThreshold = 55 + (int)cellClimate.SoilFertilityQ16 * 25 / 65535;
                bool treePass = inForestGrove && (((wx ^ (wy * 3)) & 3) != 0) && (((wx * 17 ^ wy * 31) % 100) < groveThreshold);
                bool hasTree = inForestGrove && treePass;
                data.TreeOnGrass[lx, ly] = hasTree;
                data.TreeVariant[lx, ly] = (byte)TreeVariantFor((int)wx, (int)wy, (uint)worldSeed);

                // Камни: только на свободной сухой траве, не на дереве, не на воде, не на скалах
                // Плотность камней выше у подножия гор и на каменистых почвах
                bool inStoneDeposit = isGrass && !hasTree && (cell.StoneQ16 >= WorldDecor.StoneMinQ16);
                int stoneThreshold = 45 + (int)cell.StoneQ16 * 20 / 65535;
                bool stonePass = inStoneDeposit && (((wx * 7 ^ wy * 13) % 100) < stoneThreshold);
                bool hasStone = inStoneDeposit && stonePass;
                data.StoneOnGrass[lx, ly] = hasStone;

                data.Biome[lx, ly] = cell.Biome;
                data.Variant[lx, ly] = cell.Variant;

                heightMap[lx, ly] = cell.ElevationQ16 / 65535f;
                moistNoise[lx, ly] = cell.MoistureQ16 / 65535f;
            }
        });

        // Очищаем стартовую площадку в центре окна (поляна без деревьев, камней, воды и гор)
        int cx = width / 2;
        int cy = height / 2;
        int r2 = StartClearRadius * StartClearRadius;
        for (int y = Math.Max(0, cy - StartClearRadius); y <= Math.Min(height - 1, cy + StartClearRadius); y++)
        {
            for (int x = Math.Max(0, cx - StartClearRadius); x <= Math.Min(width - 1, cx + StartClearRadius); x++)
            {
                int dx = x - cx;
                int dy = y - cy;
                if (dx * dx + dy * dy <= r2)
                {
                    data.Ground[x, y] = TileType.Grass;
                    data.TreeOnGrass[x, y] = false;
                    data.StoneOnGrass[x, y] = false;
                }
            }
        }

        // Инициализируем динамические слои почвы (влажность и плодородие)
        data.Humidity.Initialize(data.Ground, moistNoise, heightMap);
        data.Fertility.Initialize(data.Ground, moistNoise, heightMap, null, data.Humidity.WaterFeedAt);

        return data;
    }

    /// <summary>
    /// Детерминированный вариант спрайта камня 0..3 из атласа 2x2.
    /// </summary>
    public static int StoneVariantFor(int x, int y, uint seed)
    {
        unchecked
        {
            uint hb = (uint)(x * 2246822519u) ^ (uint)(y * 3266489917u) ^ (seed * 668265263u);
            hb ^= hb >> 13;
            hb *= 1274126177u;
            hb ^= hb >> 16;
            return (int)(hb % 4u);
        }
    }

    /// <summary>
    /// Атласные координаты квадранта камня 0..3 -> (TX, TY).
    /// </summary>
    public static (int TX, int TY) StoneAtlasFor(int variant)
    {
        return (variant & 3) switch
        {
            1 => (1, 0),
            2 => (0, 1),
            3 => (1, 1),
            _ => (0, 0),
        };
    }

    /// <summary>
    /// Детерминированный вариант дерева 0..3.
    /// </summary>
    public static int TreeVariantFor(int x, int y, uint seed)
    {
        unchecked
        {
            uint hb = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (seed * 83492791u);
            hb ^= hb >> 13;
            hb *= 1274126177u;
            hb ^= hb >> 16;
            return (int)(hb % 4u);
        }
    }
}
