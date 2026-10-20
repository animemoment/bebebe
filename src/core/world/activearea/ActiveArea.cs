using System;
using Game.Core;

namespace Game.Core.WorldStreaming;

// ============================================================================
// §118 фаза 3: «один мир, одна активная зона» (PLAN.md §118.1). ActiveArea —
// материализованное окно Size×Size в АБСОЛЮТНЫХ мировых координатах: плоские
// row-major массивы (конвенция WorldGenV2.Area / WorldChunk: индекс =
// ly*Size + lx). Заполнение — ТОЛЬКО WorldGenV2.GenerateArea (единственный
// источник формул мира, §118.1); генератора-ветки «колони-окно» у зоны НЕТ.
//
// Контракт полей повторяет MapData (23 потребителя; снос MapData — не разом),
// но массивы ПЛОСКИЕ. MapDataShim (фаза 3) переносит зону в существующий
// MapData одним копированием — потребители не переписываются.
//
// КООРДИНАТЫ: ZoneOriginX/Y — мировой угол зоны [OriginX, OriginX+Size) ×
// [OriginY, OriginY+Size). Геттеры принимают МИРОВЫЕ координаты (wx, wy);
// локальный индекс = (wy - ZoneOriginY)*Size + (wx - ZoneOriginX).
// Bounds-safe, как MapData: вне зоны — безопасные дефолты (Water/false/Plains/0).
//
// СТАРТОВАЯ РАСЧИСТКА (§118.1: «дельта GroundOverride, НЕ ветка генератора»):
// круг r=StartClearRadius в центре зоны применяется ЗДЕСЬ, при Fill —
// Ground=Grass, деревьев/камней нет. Поведение дословно как у окна
// GenerateColonyWindow (UnifiedWorldGenerator.StartClearRadius=14).
//
// ДИНАМИЧЕСКИЕ СЛОИ (§118, «что не перенесено в v2» — перенесено СЮДА):
// Humidity/Fertility инициализируются как финал окна GenerateColonyWindow:
// moistNoise = MoistureQ16/65535f, heightMap = ElevationQ16/65535f, оба
// Initialize получают moistNoise (FertilityMap в окне получает moistNoise,
// НЕ SoilFertilityQ16 — канон = поведение окна, гейт фазы 3 «игра
// неотличима»). Climate — SampleBlendedCell в центре зоны (как окно),
// Seed = (uint)worldSeed (как окно). LakeCount/RiverBranchCount и прочие
// мета-статистики окна НЕ заполняются (и окном тоже) — остаются дефолтами.
// ============================================================================

/// <summary>
/// Активная зона 1024×1024: плоские массивы мира + стартовая расчистка.
/// Чистый C#, без Godot API — можно звать из фоновых потоков.
/// </summary>
public sealed class ActiveArea
{
    /// <summary>Сторона зоны в клетках (§118.1: «1024 — просто в памяти, чтоб не было фризов»).</summary>
    public const int Size = 1024;

    /// <summary>
    /// Радиус стартовой расчистки в центре зоны. Число = UnifiedWorldGenerator.
    /// StartClearRadius (канон поведения окна), но константа СОБСТВЕННАЯ:
    /// §118 фаза 6 сносит UnifiedWorldGenerator — расчистка обязана пережить снос.
    /// </summary>
    public const int StartClearRadius = 14;

    /// <summary>Мировой X левого края зоны (включительно).</summary>
    public long ZoneOriginX { get; private set; }

    /// <summary>Мировой Y верхнего края зоны (включительно).</summary>
    public long ZoneOriginY { get; private set; }

    /// <summary>Заполнена ли зона (прошёл ли Fill).</summary>
    public bool IsFilled { get; private set; }

    // Плоские row-major массивы: индекс = ly*Size + lx. Публичные — MapDataShim
    // и фаза 4 (IWorldMutationSink: мутация = массив + дельта + событие).
    public TileType[] Ground { get; } = new TileType[Size * Size];
    public bool[] TreeOnGrass { get; } = new bool[Size * Size];
    public byte[] TreeVariant { get; } = new byte[Size * Size];
    public bool[] StoneOnGrass { get; } = new bool[Size * Size];
    public byte[] Biome { get; } = new byte[Size * Size];
    public byte[] Variant { get; } = new byte[Size * Size];

    /// <summary>Почвенная влажность 0..200+ (контракт MapData; тикается симуляцией).</summary>
    public HumidityMap Humidity { get; } = new(Size, Size);

    /// <summary>Плодородие 0..200 (контракт MapData; тикается симуляцией).</summary>
    public FertilityMap Fertility { get; } = new(Size, Size);

    /// <summary>Климат центра зоны (диагностика/HUD/тесты; контракт MapData.Climate).</summary>
    public WorldClimateParams Climate { get; private set; } = WorldClimateParams.DefaultTemperate;

    /// <summary>Сид, которым зона была сгенерирована, (uint)worldSeed (контракт MapData.Seed).</summary>
    public uint Seed { get; private set; }

    /// <summary>Пустая незаполненная зона с данным мировым origin (так создаёт ActiveAreaRegistry).</summary>
    public ActiveArea(long zoneOriginX, long zoneOriginY)
    {
        ZoneOriginX = zoneOriginX;
        ZoneOriginY = zoneOriginY;
    }

    /// <summary>
    /// Заполнить зону в АБСОЛЮТНЫХ мировых координатах: GenerateArea → плоские
    /// массивы → стартовая расчистка → Humidity/Fertility → Climate/Seed.
    /// Ре-центр зоны (фаза 5: зона ходит за игроком) — тот же вызов с новым origin.
    /// </summary>
    public void Fill(ulong worldSeed, uint generatorVersion, long zoneOriginX, long zoneOriginY)
    {
        ZoneOriginX = zoneOriginX;
        ZoneOriginY = zoneOriginY;
        FillCore(worldSeed, generatorVersion);
    }

    /// <summary>Заполнить по ТЕКУЩЕМУ origin зоны (реестр: Register → TryGetZone → Fill).</summary>
    public void Fill(ulong worldSeed, uint generatorVersion)
        => FillCore(worldSeed, generatorVersion);

    private void FillCore(ulong worldSeed, uint generatorVersion)
    {
        WorldGenV2.Area area = WorldGenV2.GenerateArea(
            worldSeed, generatorVersion, ZoneOriginX, ZoneOriginY, Size, Size);
        ReadOnlySpan<GeneratedCell> cells = area.Cells.Span;
        ReadOnlySpan<bool> trees = area.TreeOnGrass.Span;
        ReadOnlySpan<bool> stones = area.StoneOnGrass.Span;
        ReadOnlySpan<byte> treeVariants = area.TreeVariant.Span;

        // Временные 2D-снимки: Initialize динамических слоёв (Humidity/Fertility)
        // принимает [,]-массивы. 2+4+4 МБ, живут только внутри Fill (ре-центр —
        // редкое событие, порог 25%, §118.1).
        var ground2D = new TileType[Size, Size];
        var moist2D = new float[Size, Size];
        var height2D = new float[Size, Size];

        for (int ly = 0; ly < Size; ly++)
        {
            int row = ly * Size;
            for (int lx = 0; lx < Size; lx++)
            {
                int i = row + lx;
                GeneratedCell cell = cells[i];
                TileType ground = TerrainToTileType(cell.Terrain);
                Ground[i] = ground;
                ground2D[lx, ly] = ground;
                TreeOnGrass[i] = trees[i];
                TreeVariant[i] = treeVariants[i];
                StoneOnGrass[i] = stones[i];
                Biome[i] = cell.Biome;
                Variant[i] = cell.Variant;
                // Как окно (строки 567-568): влага/высота клетки → float-шумы Initialize.
                moist2D[lx, ly] = cell.MoistureQ16 / 65535f;
                height2D[lx, ly] = cell.ElevationQ16 / 65535f;
            }
        }

        ApplyStartClearance(ground2D);

        // Динамические слои — в точности финал окна GenerateColonyWindow
        // (строки 592-593): Fertility.Initialize получает moistNoise, а НЕ
        // SoilFertilityQ16 — канон поведения окна (гейт фазы 3).
        Humidity.Initialize(ground2D, moist2D, height2D);
        Fertility.Initialize(ground2D, moist2D, height2D, null, Humidity.WaterFeedAt);

        // Климат центра зоны — как окно: SampleBlendedCell(origin + size/2).
        Climate = WorldClimateGenerator.SampleBlendedCell(
            worldSeed, generatorVersion, ZoneOriginX + Size / 2, ZoneOriginY + Size / 2);
        Seed = unchecked((uint)worldSeed);
        IsFilled = true;
    }

    /// <summary>
    /// Стартовая расчистка (§118.1): круг r=StartClearRadius в центре зоны —
    /// трава без деревьев/камней (вода/горы тоже выжигаются в Grass, как окно).
    /// Плоские массивы и 2D-снимок обновляются синхронно (Initialize ниже
    /// читает 2D). В фазе 4 станет дельтой GroundOverride.
    /// </summary>
    private void ApplyStartClearance(TileType[,] ground2D)
    {
        int cx = Size / 2;
        int cy = Size / 2;
        int r2 = StartClearRadius * StartClearRadius;
        int y0 = Math.Max(0, cy - StartClearRadius);
        int y1 = Math.Min(Size - 1, cy + StartClearRadius);
        int x0 = Math.Max(0, cx - StartClearRadius);
        int x1 = Math.Min(Size - 1, cx + StartClearRadius);
        for (int y = y0; y <= y1; y++)
        {
            int row = y * Size;
            for (int x = x0; x <= x1; x++)
            {
                int dx = x - cx;
                int dy = y - cy;
                if (dx * dx + dy * dy > r2)
                    continue;
                Ground[row + x] = TileType.Grass;
                TreeOnGrass[row + x] = false;
                StoneOnGrass[row + x] = false;
                ground2D[x, y] = TileType.Grass;
            }
        }
    }

    /// <summary>Биективное соответствие Terrain → TileType (как окно, строки 537-542).</summary>
    private static TileType TerrainToTileType(BaseTerrainKind terrain) => terrain switch
    {
        BaseTerrainKind.Water => TileType.Water,
        BaseTerrainKind.Mountain => TileType.Mountain,
        _ => TileType.Grass
    };

    // ------------------------------------------------------------------ //
    //  Геттеры по МИРОВЫМ координатам (bounds-safe, контракт MapData)     //
    // ------------------------------------------------------------------ //

    /// <summary>Клетка внутри зоны? (мировые координаты).</summary>
    public bool ContainsWorldCell(long wx, long wy)
        => wx >= ZoneOriginX && wy >= ZoneOriginY
        && wx < ZoneOriginX + Size && wy < ZoneOriginY + Size;

    /// <summary>
    /// Поверхность клетки. Вне зоны — Water (консервативно: «не играбельно»;
    /// семантика bounds-safe MapData; за зоной истина — WorldGenV2.SampleCell,
    /// фаза 5).
    /// </summary>
    public TileType GetGround(long wx, long wy)
        => ContainsWorldCell(wx, wy) ? Ground[WorldIndex(wx, wy)] : TileType.Water;

    /// <summary>Дерево на клетке. Вне зоны — false.</summary>
    public bool GetTree(long wx, long wy)
        => ContainsWorldCell(wx, wy) && TreeOnGrass[WorldIndex(wx, wy)];

    /// <summary>Каменная россыпь на клетке. Вне зоны — false (контракт MapData.HasStone).</summary>
    public bool HasStone(long wx, long wy)
        => ContainsWorldCell(wx, wy) && StoneOnGrass[WorldIndex(wx, wy)];

    /// <summary>Визуальный биом клетки. Вне зоны — Plains (контракт MapData.GetBiome).</summary>
    public BiomeType GetBiome(long wx, long wy)
        => ContainsWorldCell(wx, wy) ? (BiomeType)Biome[WorldIndex(wx, wy)] : BiomeType.Plains;

    /// <summary>Вариант текстуры клетки. Вне зоны — 0 (контракт MapData.GetGroundVariant).</summary>
    public int GetGroundVariant(long wx, long wy)
        => ContainsWorldCell(wx, wy) ? Variant[WorldIndex(wx, wy)] : 0;

    /// <summary>Вариант дерева 0..3. Вне зоны — 0 (контракт MapData.GetTreeVariant).</summary>
    public int GetTreeVariant(long wx, long wy)
        => ContainsWorldCell(wx, wy) ? TreeVariant[WorldIndex(wx, wy)] & 3 : 0;

    /// <summary>Локальный row-major индекс: (wy-originY)*Size + (wx-originX). Вызывающий ОБЯЗАН проверить ContainsWorldCell.</summary>
    private int WorldIndex(long wx, long wy)
        => (int)((wy - ZoneOriginY) * Size + (wx - ZoneOriginX));
}
