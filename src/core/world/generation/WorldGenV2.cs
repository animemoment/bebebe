using System;
using Game.Core.WorldStreaming.Integration;

namespace Game.Core.WorldStreaming;

// ============================================================================
// §118 фаза 1: единственный источник формул мира; старый UnifiedWorldGenerator
// удаляется в фазе 6. Файл НОВЫЙ: вызовы старого генератора НЕ переключаются
// (интеграция — фаза 3, PLAN.md §118.2), StreamingWorldView/MapRenderer/Main
// не зависят от этого файла.
//
// КОНТРАКТ (§118.1):
//  * SampleCell(worldSeed, generatorVersion, wx, wy) — формулы ОДНОЙ клетки в
//    АБСОЛЮТНЫХ мировых координатах (wx/wy уже включают WorldOrigin — внутри
//    генератора нет скрытого глобального состояния). Дословный перенос
//    UnifiedWorldGenerator.SampleCell при overrideClimate == null (прямой семпл
//    климата в искривлённых координатах — то, что зовут TestWorldGenSeamAudit),
//    дополненный плодородием и вариантами дерева/камня:
//    «рельеф/климат/влага/плодородие/лес/камень/варианты».
//  * GenerateArea(worldSeed, generatorVersion, originX, originY, width, height)
//    — ЕДИНСТВЕННЫЙ путь генерации области: плоские row-major массивы (конвенция
//    WorldChunk: индекс = y*Width + x), Q0.16-фикспоинт, ноль аллокаций на клетку
//    (5 плоских массивов на область + сетка климата). Чанк 64×64 =
//    GenerateArea(..., 64, 64): отдельной ветки «колони-окно» НЕТ. Стартовая
//    расчистка НЕ выполняется (§118.1: станет дельтой GroundOverride в фазе 3).
//
// ПЕРЕНОС ФОРМУЛ (канон = ПОВЕДЕНИЕ ОКНА GenerateColonyWindow; для областей,
// выровненных по 16, оно ПОБИТОВО совпадает с 5×5-сеткой GenerateChunk — это
// уже обеспечено шов-тестами tests/test_streamingview_decor_seam.cs и
// tests/TestWorldGenSeamAudit.cs, v2 обязан им соответствовать):
//  * Климат области: сетка макро-климата с шагом 16 клеток, якоря =
//    origin + 16·g, значения WorldClimateGenerator.SampleBlendedCell, билинейная
//    интерполяция LerpClimate1D/LerpQ16 — дословно как GenerateColonyWindow
//    (subDiv = max(1, side/16), bx = min(subDiv-1, lx/16), u = (lx%16)*65535/16).
//    Для width/height, кратных 16, якоря совпадают с чанковыми и результат
//    идентичен GenerateChunk.
//  * Клетка: дословный перенос тела UnifiedWorldGenerator.SampleCell (строки
//    246–463): те же константы, тот же порядок операций, те же хэш-домены
//    (CoordinateHash.ValueNoiseQ16) и те же сид-смещения (2001/2002/3001/3002/
//    12345/54321/7171/8282/9393/777/888/999/33333/44444).
//  * Декор (размещение деревьев/камней): дословный перенос формулы
//    GenerateColonyWindow (строки 544–562) == StreamingWorldView.IslandDecorPass.
//    Пороги — WorldDecor.ForestMinQ16/StoneMinQ16 (единый источник истины §26.1).
//    Плодородие порога рощ — SoilFertilityQ16 ИНТЕРПОЛИРОВАННОЙ сетки климата
//    (как в GenerateColonyWindow), а не пойменно-усиленное внутри клетки.
//
// ЗАДОКУМЕНТИРОВАННОЕ ОТЛИЧИЕ от буквальной формулы GenerateColonyWindow (одно,
// унаследовано из стриминга, см. StreamingWorldView.cs:1474–1476): остаток %100
// приведён к [0,100) (евклидов, Mod100). В домене окна (wx,wy >= 0) результат
// ПОБИТОВО совпадает с C#-остатком % окна; в отрицательных квадрантах знаковый %
// окна выжигал бы лес на пол-планеты («лысая» половина мира, §117/F4) — но окно
// там никогда не работает, а игрок в отрицательных координатах видит СТРИМИНГ
// (IslandDecorPass/Mod100). v2 берёт поведение стриминга: единая формула,
// совпадающая с окном во всём домене окна и со стримингом везде.
//
// ЧТО НЕ ПЕРЕНЕСЕНО (намеренно, фазы 3+): стартовая расчистка (дельта
// GroundOverride); динамические слои Humidity/Fertility (Initialize в окне —
// фаза 3 MapData-shim; v2 отдаёт исходные шумы: ElevationQ16/MoistureQ16);
// Parallel.For (последовательный цикл: та же детерминированность, потокобезопасность
// через ThreadStatic-кэши климата; параллелизм вернёт WorldStore при необходимости);
// WorldChunkGeneratorTiming (телеметрия старого чанкового пути).
// ============================================================================

/// <summary>
/// §118 фаза 1: единственный источник формул мира. Все формулы перенесены из
/// UnifiedWorldGenerator дословно; см. контракт в шапке файла.
/// </summary>
public static class WorldGenV2
{
    // ------------------------------------------------------------------ //
    //  Публичные типы результата                                          //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Результат семпла одной клетки: данные, совместимые со старым GeneratedCell
    /// (террейн/высота/влага/температура/лес/камень/биом/вариант), плюс плодородие
    /// (климат клетки ПОСЛЕ пойменного усиления — так заканчивает старый SampleCell)
    /// и варианты дерева/камня (0..3, детерминированные хэши координат+сида).
    /// </summary>
    public readonly record struct CellSample(
        BaseTerrainKind Terrain,
        ushort ElevationQ16,
        ushort MoistureQ16,
        ushort TemperatureQ16,
        ushort SoilFertilityQ16,
        ushort ForestQ16,
        ushort StoneQ16,
        byte Biome,
        byte Variant,
        byte TreeVariant,
        byte StoneVariant)
    {
        /// <summary>Представление, бит-в-бит совместимое со старым GeneratedCell.</summary>
        public GeneratedCell ToGeneratedCell()
            => new(Terrain, ElevationQ16, MoistureQ16, ForestQ16, StoneQ16, TemperatureQ16, Biome, Variant);

        public static implicit operator GeneratedCell(CellSample s) => s.ToGeneratedCell();
    }

    /// <summary>
    /// Результат генерации области: плоские массивы row-major (индекс = y*Width + x),
    /// конвенция WorldChunk. Cells — бит-в-бит данные GeneratedCell старых путей;
    /// TreeOnGrass/StoneOnGrass/TreeVariant — размещение декора по формуле
    /// GenerateColonyWindow == IslandDecorPass (стартовой расчистки НЕТ).
    /// </summary>
    public sealed class Area
    {
        private readonly GeneratedCell[] _cells;
        private readonly bool[] _treeOnGrass;
        private readonly bool[] _stoneOnGrass;
        private readonly byte[] _treeVariant;

        public long OriginX { get; }
        public long OriginY { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>Клетки области, row-major. Длина = Width*Height.</summary>
        public ReadOnlyMemory<GeneratedCell> Cells => _cells;

        /// <summary>Дерево на клетке (только на траве), row-major.</summary>
        public ReadOnlyMemory<bool> TreeOnGrass => _treeOnGrass;

        /// <summary>Каменная россыпь на клетке (только на траве без дерева), row-major.</summary>
        public ReadOnlyMemory<bool> StoneOnGrass => _stoneOnGrass;

        /// <summary>Вариант спрайта дерева 0..3 (хэш мировых координат+сида), row-major.</summary>
        public ReadOnlyMemory<byte> TreeVariant => _treeVariant;

        internal Area(
            long originX, long originY, int width, int height,
            GeneratedCell[] cells, bool[] treeOnGrass, bool[] stoneOnGrass, byte[] treeVariant)
        {
            OriginX = originX;
            OriginY = originY;
            Width = width;
            Height = height;
            _cells = cells;
            _treeOnGrass = treeOnGrass;
            _stoneOnGrass = stoneOnGrass;
            _treeVariant = treeVariant;
        }

        /// <summary>Клетка по локальным координатам. Bounds-safe (как WorldChunk).</summary>
        public GeneratedCell GetCell(int localX, int localY)
        {
            if ((uint)localX >= (uint)Width || (uint)localY >= (uint)Height)
                throw new ArgumentOutOfRangeException(nameof(localX), "Локальная координата вне области.");
            return _cells[localY * Width + localX];
        }

        /// <summary>Дерево по локальным координатам. Bounds-safe.</summary>
        public bool GetTree(int localX, int localY)
        {
            if ((uint)localX >= (uint)Width || (uint)localY >= (uint)Height)
                throw new ArgumentOutOfRangeException(nameof(localX), "Локальная координата вне области.");
            return _treeOnGrass[localY * Width + localX];
        }

        /// <summary>Камень по локальным координатам. Bounds-safe.</summary>
        public bool GetStone(int localX, int localY)
        {
            if ((uint)localX >= (uint)Width || (uint)localY >= (uint)Height)
                throw new ArgumentOutOfRangeException(nameof(localX), "Локальная координата вне области.");
            return _stoneOnGrass[localY * Width + localX];
        }

        /// <summary>Вариант дерева 0..3 по локальным координатам. Bounds-safe.</summary>
        public int GetTreeVariant(int localX, int localY)
        {
            if ((uint)localX >= (uint)Width || (uint)localY >= (uint)Height)
                throw new ArgumentOutOfRangeException(nameof(localX), "Локальная координата вне области.");
            return _treeVariant[localY * Width + localX] & 3;
        }
    }

    // ------------------------------------------------------------------ //
    //  Публичный API (ровно два метода)                                   //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Детерминированный расчёт характеристик ОДНОЙ клетки в абсолютных мировых
    /// координатах (wx, wy). Повторяет старый UnifiedWorldGenerator.SampleCell с
    /// overrideClimate == null: двухслойный Domain Warping, климат прямо в
    /// искривлённых координатах (SampleBlendedCell), риджевая тектоника,
    /// прибрежная геоморфология, гидрология (река/озеро/пойма), биом по
    /// БиомМапперу, лес/камень, варианты дерева/камня.
    /// </summary>
    public static CellSample SampleCell(ulong worldSeed, uint generatorVersion, long wx, long wy)
    {
        (long warpedWx, long warpedWy) = ComputeWarp(worldSeed, generatorVersion, wx, wy);
        WorldClimateParams climate =
            WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, warpedWx, warpedWy);
        return SampleCellCore(worldSeed, generatorVersion, wx, wy, warpedWx, warpedWy, in climate);
    }

    /// <summary>
    /// ЕДИНСТВЕННЫЙ путь генерации: область [originX, originX+width) ×
    /// [originY, originY+height) в абсолютных мировых координатах. Климат —
    /// интерполяция сетки 16×16, якоря = origin + 16·g (поведение окна
    /// GenerateColonyWindow; для сторон, кратных 16, побитово = GenerateChunk).
    /// origin НЕ обязан быть выровнен по 16: сетка заякоривается на origin, ровно
    /// как окно заякоривается на свой baseOrigin.
    /// </summary>
    public static Area GenerateArea(
        ulong worldSeed,
        uint generatorVersion,
        long originX,
        long originY,
        int width,
        int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Ширина области должна быть больше 0.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Высота области должна быть больше 0.");

        const int SubSize = 16;   // шаг опорной сетки климата (как в окне/чанке)
        const int Q16One = 65535;
        int subDivX = Math.Max(1, width / SubSize);
        int subDivY = Math.Max(1, height / SubSize);
        int gridW = subDivX + 1;
        int gridH = subDivY + 1;

        // Опорная сетка климата: SampleBlendedCell в узлах origin + 16·g.
        // (Окно заполняет её Parallel.For — значения те же, порядок не важен.)
        var grid = new WorldClimateParams[gridW * gridH];
        for (int gy = 0; gy < gridH; gy++)
        {
            long gwy = originY + gy * SubSize;
            int row = gy * gridW;
            for (int gx = 0; gx < gridW; gx++)
            {
                long gwx = originX + gx * SubSize;
                grid[row + gx] = WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, gwx, gwy);
            }
        }

        var cells = new GeneratedCell[width * height];
        var treeOnGrass = new bool[width * height];
        var stoneOnGrass = new bool[width * height];
        var treeVariant = new byte[width * height];

        for (int ly = 0; ly < height; ly++)
        {
            long wy = originY + ly;
            int by = Math.Min(subDivY - 1, ly / SubSize);
            uint v = (uint)(((long)(ly % SubSize)) * Q16One / SubSize);
            int rowOffset = ly * width;
            int rowA = by * gridW;
            int rowB = (by + 1) * gridW;

            for (int lx = 0; lx < width; lx++)
            {
                long wx = originX + lx;
                int bx = Math.Min(subDivX - 1, lx / SubSize);
                uint u = (uint)(((long)(lx % SubSize)) * Q16One / SubSize);

                WorldClimateParams nw = grid[rowA + bx];
                WorldClimateParams ne = grid[rowA + bx + 1];
                WorldClimateParams swClimate = grid[rowB + bx];
                WorldClimateParams se = grid[rowB + bx + 1];

                WorldClimateParams top = LerpClimate1D(in nw, in ne, u);
                WorldClimateParams bot = LerpClimate1D(in swClimate, in se, u);
                WorldClimateParams cellClimate = LerpClimate1D(in top, in bot, v);

                (long warpedWx, long warpedWy) = ComputeWarp(worldSeed, generatorVersion, wx, wy);
                CellSample s = SampleCellCore(
                    worldSeed, generatorVersion, wx, wy, warpedWx, warpedWy, in cellClimate);
                GeneratedCell cell = s.ToGeneratedCell();
                cells[rowOffset + lx] = cell;
                treeVariant[rowOffset + lx] = s.TreeVariant;

                // Декор: формула GenerateColonyWindow (строки 544–562) ==
                // StreamingWorldView.IslandDecorPass (дословно, включая Mod100).
                // (isGrass окна «Terrain==Grass && Ground==Grass» — Ground выводится
                // из Terrain биективным switch, проверка избыточна и опущена.)
                (bool hasTree, bool hasStone) = DecorPass(wx, wy, in cell, cellClimate.SoilFertilityQ16);
                treeOnGrass[rowOffset + lx] = hasTree;
                stoneOnGrass[rowOffset + lx] = hasStone;
            }
        }

        return new Area(originX, originY, width, height, cells, treeOnGrass, stoneOnGrass, treeVariant);
    }

    // ------------------------------------------------------------------ //
    //  Внутренние формулы (перенесены ДОСЛОВНО, всё private)              //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Двухслойный Domain Warping берега (Meso: 64 кл., Micro: 16 кл.) — дословно
    /// из UnifiedWorldGenerator.SampleCell (шаг 1).
    /// </summary>
    private static (long WarpedWx, long WarpedWy) ComputeWarp(
        ulong worldSeed, uint generatorVersion, long wx, long wy)
    {
        ushort wMesoX = CoordinateHash.ValueNoiseQ16(worldSeed + 2001ul, generatorVersion, GenerationDomain.CoastWarpMeso, wx, wy, 64);
        ushort wMesoY = CoordinateHash.ValueNoiseQ16(worldSeed + 2002ul, generatorVersion, GenerationDomain.CoastWarpMeso, wx + 31, wy, 64);
        long wx1 = wx + ((int)wMesoX - 32768) * 36 / 32768;
        long wy1 = wy + ((int)wMesoY - 32768) * 36 / 32768;

        ushort wMicroX = CoordinateHash.ValueNoiseQ16(worldSeed + 3001ul, generatorVersion, GenerationDomain.CoastWarpMicro, wx1, wy1, 16);
        ushort wMicroY = CoordinateHash.ValueNoiseQ16(worldSeed + 3002ul, generatorVersion, GenerationDomain.CoastWarpMicro, wx1 + 13, wy1, 16);
        long warpedWx = wx1 + ((int)wMicroX - 32768) * 6 / 32768;
        long warpedWy = wy1 + ((int)wMicroY - 32768) * 6 / 32768;
        return (warpedWx, warpedWy);
    }

    /// <summary>
    /// Тело клетки — дословный перенос UnifiedWorldGenerator.SampleCell (строки
    /// 246–463) с заменой только источника климата: передаётся готовый климат
    /// (интерполяция сетки — как окно/чанк; прямой семпл — как одиночный
    /// SampleCell). Локальная копия climate мутируется пойменным усилением —
    /// ровно как в старом коде.
    /// </summary>
    private static CellSample SampleCellCore(
        ulong worldSeed,
        uint generatorVersion,
        long wx,
        long wy,
        long warpedWx,
        long warpedWy,
        in WorldClimateParams climateOverride)
    {
        WorldClimateParams climate = climateOverride;

        // 2. Локальная топография: холмы и волнистость
        ushort hillNoise = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion, GenerationDomain.ElevationMedium, warpedWx, warpedWy, 64);
        ushort fineNoise = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion, GenerationDomain.ElevationFine, wx, wy, 24);
        int hillOffset = ((int)hillNoise - 32768) * 5500 / 32768 + ((int)fineNoise - 32768) * 2500 / 32768;

        int mountainLift = 0;
        if (climate.ElevationQ16 >= WorldClimateGenerator.SeaLevelQ16)
        {
            // Двуоктавный риджевый мультифрактал (Ridged Multifractal Tectonics)
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
            // Плавное затухание горного хребта к уровню моря
            float shoreFade = Math.Clamp(((int)climate.ElevationQ16 - WorldClimateGenerator.SeaLevelQ16) / 2000f, 0f, 1f);
            mountainLift = (int)((baseRidgeLift + tectonicRidgeLift) * shoreFade);
        }

        // Прибрежная геоморфология (бухты, мысы, защищенные гавани):
        int coastalMod = 0;
        int distToSeaLevel = (int)climate.ElevationQ16 - WorldClimateGenerator.SeaLevelQ16;
        if (Math.Abs(distToSeaLevel) < 4000)
        {
            float coastFade = 1.0f - Math.Abs(distToSeaLevel) / 4000f;

            // 1. Мысы (Headlands)
            ushort capeRaw = CoordinateHash.ValueNoiseQ16(worldSeed + 7171ul, generatorVersion, GenerationDomain.CoastFeature, warpedWx, warpedWy, 48);
            int capeDist = Math.Abs((int)capeRaw - 32768);
            int capeRidge = Math.Max(0, 32768 - capeDist) * 2;
            int capeLift = (capeRidge - 32768) * 2200 / 32768;

            // 2. Бухты и гавани (Coves / Basins)
            ushort coveRaw = CoordinateHash.ValueNoiseQ16(worldSeed + 8282ul, generatorVersion, GenerationDomain.CoastFeature, warpedWx + 27, warpedWy + 19, 72);
            int coveBasin = ((int)coveRaw - 32768) * 2400 / 32768;

            // 3. Заливчики и фьорды (Inlets)
            ushort inletRaw = CoordinateHash.ValueNoiseQ16(worldSeed + 9393ul, generatorVersion, GenerationDomain.CoastFeature, warpedWx - 15, warpedWy + 33, 24);
            int inlet = ((int)inletRaw - 32768) * 900 / 32768;

            coastalMod = (int)((capeLift + coveBasin + inlet) * coastFade);
        }

        // Гипсометрический якорь суши (Coastline Anchoring)
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

            // Направленная открытая река с вихревым меандрированием
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

            // Физическая гидрологическая пойма (Riparian Buffer)
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

            // Прибрежный песчаный пляж (Sand Beach)
            if (elevationQ16 < WorldClimateGenerator.SeaLevelQ16 + 1000 && pick.Biome != BiomeType.Glacier)
            {
                pick = new TilePick(BiomeType.Desert, pick.Variant);
            }
        }

        // 6. Растительность (деревья): альпийская граница леса + пойменные галерейные леса
        ushort forestQ16 = 0;
        if (terrain == BaseTerrainKind.Grass && pick.Biome != BiomeType.Desert && pick.Biome != BiomeType.Glacier && elevationQ16 < 43000)
        {
            ushort fNoise = CoordinateHash.ValueNoiseQ16(worldSeed + 33333ul, generatorVersion, GenerationDomain.Forest, warpedWx, warpedWy, 32);
            int fCluster = ((int)fNoise - 18000);
            int baseVeg = (int)climate.VegetationDensityQ16;
            if (inRiparianZone) baseVeg = Math.Min(65535, baseVeg + 14000);
            int f = baseVeg * 50 / 100 + fCluster * 60 / 100;
            forestQ16 = (ushort)Math.Clamp(f, 0, 65535);
        }

        // 7. Каменная россыпь: гравитационные осыпи у подножия гор и аллювиальный галечник рек
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

        return new CellSample(
            Terrain: terrain,
            ElevationQ16: elevationQ16,
            MoistureQ16: climate.MoistureQ16,
            TemperatureQ16: climate.TemperatureQ16,
            SoilFertilityQ16: climate.SoilFertilityQ16,
            ForestQ16: forestQ16,
            StoneQ16: stoneQ16,
            Biome: (byte)pick.Biome,
            Variant: (byte)pick.Variant,
            TreeVariant: (byte)TreeVariantFor((int)wx, (int)wy, (uint)worldSeed),
            StoneVariant: (byte)StoneVariantFor((int)wx, (int)wy, (uint)worldSeed));
    }

    /// <summary>
    /// Размещение декора (деревья/камни) — дословный перенос формулы
    /// GenerateColonyWindow (строки 544–562) == StreamingWorldView.IslandDecorPass
    /// (включая евклидов Mod100 — см. шапку файла). wx/wy — МИРОВЫЕ координаты.
    /// soilFertilityQ16 — плодородие ИНТЕРПОЛИРОВАННОЙ сетки климата (как окно).
    /// </summary>
    private static (bool HasTree, bool HasStone) DecorPass(
        long wx, long wy, in GeneratedCell cell, ushort soilFertilityQ16)
    {
        bool isGrass = cell.Terrain == BaseTerrainKind.Grass;

        // Естественная плотность рощ: группами с полянками; на плодородных почвах гуще.
        bool inForestGrove = isGrass && cell.ForestQ16 >= WorldDecor.ForestMinQ16;
        bool hasTree = false;
        if (inForestGrove)
        {
            int groveThreshold = 55 + soilFertilityQ16 * 25 / 65535;
            hasTree = ((wx ^ (wy * 3)) & 3) != 0
                && Mod100(wx * 17 ^ wy * 31) < groveThreshold;
        }

        // Камни: только на свободной сухой траве, не на дереве.
        bool hasStone = false;
        if (isGrass && !hasTree && cell.StoneQ16 >= WorldDecor.StoneMinQ16)
        {
            int stoneThreshold = 45 + (int)cell.StoneQ16 * 20 / 65535;
            hasStone = Mod100(wx * 7 ^ wy * 13) < stoneThreshold;
        }
        return (hasTree, hasStone);
    }

    /// <summary>Евклидов остаток по модулю 100 (для значений ≥ 0 эквивалентен C# %).</summary>
    private static int Mod100(long value)
    {
        int r = (int)(value % 100);
        return r < 0 ? r + 100 : r;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ushort LerpQ16(ushort a, ushort b, uint t)
    {
        uint clampedT = Math.Min(t, (uint)65535);
        long value = (long)a * (65535 - clampedT) + (long)b * clampedT + 32768;
        return (ushort)Math.Clamp(value / 65535, 0, 65535);
    }

    /// <summary>Линейная интерполяция 18 климатических полей — дословно из UnifiedWorldGenerator.</summary>
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

    /// <summary>Детерминированный вариант спрайта камня 0..3 — дословно из UnifiedWorldGenerator.</summary>
    private static int StoneVariantFor(int x, int y, uint seed)
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

    /// <summary>Детерминированный вариант дерева 0..3 — дословно из UnifiedWorldGenerator.</summary>
    private static int TreeVariantFor(int x, int y, uint seed)
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
