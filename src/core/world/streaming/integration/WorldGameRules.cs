#nullable enable
using System;

namespace Game.Core.WorldStreaming.Integration;

/// <summary>
/// Правила «основной игры» поверх стримингового мира — фасад, которому будут
/// соответствовать легаси-менеджеры (стены, здания, добыча камня, вырубка леса,
/// ферма, склады) при миграции главного экрана. Никаких правок легаси MapData/
/// MapRenderer/SimulationContext: всё через детерминированные cell-запросы
/// (WorldCellQuery) и мульти-kind метки (IBlockWorld).
/// Все гейты работают только на загруженных чанках (задания выполняются рядом
/// с игроком); вне загруженной области возвращается false.
/// Пороговые значения — детерминированные константы для первой интеграции,
/// точные правила легаси уточнятся при подключении сцен основной игры.
/// </summary>
public sealed class WorldGameRules
{
    /// <summary>Минимум лесного шума (Q16) для вырубки.</summary>
    public const ushort TreeForestMin = 30_000;

    /// <summary>Диапазон влажности (Q16) для пахоты: не пересушено и не болото.</summary>
    public const ushort FarmMoistureMin = 28_000;
    public const ushort FarmMoistureMax = 56_000;

    private readonly IBlockWorld _world;
    private readonly ulong _seed;
    private readonly uint _generatorVersion;

    public WorldGameRules(IBlockWorld world, ulong seed, uint generatorVersion)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _seed = seed;
        _generatorVersion = generatorVersion;
    }

    /// <summary>Загружен ли чанк клетки (можно ли здесь выполнять задания/строить).</summary>
    public bool IsLoadedAt(long cellX, long cellY)
        => _world.IsLoaded(WorldCoordinates.ChunkForTile(cellX, cellY));

    /// <summary>Занята ли клетка постоянным объектом (стена/здание/склад/посев) или его планом (§29).</summary>
    public bool HasEntityBlocking(long cellX, long cellY)
    {
        byte[] kinds = WorldDeltaKind.BlockingKinds;
        for (int i = 0; i < kinds.Length; i++)
        {
            if (_world.HasBlock(kinds[i], cellX, cellY))
                return true;
        }
        return false;
    }

    // ---------- Гейты ----------

    /// <summary>Можно строить здание: загружено, трава, клетка свободна, здания ещё нет.</summary>
    public bool CanBuildAt(long cellX, long cellY)
        => IsReadyBase(cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.Building, cellX, cellY);

    /// <summary>Можно ставить стену: загружено, трава, клетка свободна, стены ещё нет.</summary>
    public bool CanPlaceWallAt(long cellX, long cellY)
        => IsReadyBase(cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.Wall, cellX, cellY);

    /// <summary>Можно добывать камень: загружено, гора или россыпь камня, не занято объектом, ещё не добыто и нет плана.</summary>
    public bool CanMineStoneAt(long cellX, long cellY)
    {
        if (!IsLoadedAt(cellX, cellY))
            return false;

        GeneratedCell cell = GetCell(cellX, cellY);
        bool hasStoneSource = cell.Terrain == BaseTerrainKind.Mountain || cell.StoneQ16 >= WorldDecor.StoneMinQ16;
        return hasStoneSource
            && !HasEntityBlocking(cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.MinedStone, cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.MinedStonePlan, cellX, cellY);
    }

    /// <summary>Можно вырубить дерево: загружено, лесной шум высокий, не вода, не занято, ещё не вырублено и нет плана.</summary>
    public bool CanCutTreeAt(long cellX, long cellY)
    {
        if (!IsLoadedAt(cellX, cellY))
            return false;

        GeneratedCell cell = GetCell(cellX, cellY);
        return cell.ForestQ16 >= TreeForestMin
            && cell.Terrain != BaseTerrainKind.Water
            && !HasEntityBlocking(cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.CutTree, cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.CutTreePlan, cellX, cellY);
    }

    /// <summary>
    /// Можно засеять: загружено, пригодная ПОЧВА (голая земля), клетка свободна от постоянных
    /// объектов и чертежей. Гейт строгий по поверхности: «трава» как терреин ещё не значит земля —
    /// песок (пустыня/пляж), лёд и клетки под видимым декором (дерево/камень) не годятся,
    /// см. <see cref="IsArableSoil"/>. Прежний зажим влажности (FarmMoistureMin/Max) намеренно
    /// НЕ применяется: он снят как источник «пустот» при разметке (PLAN §56.1.2 / §56.2.1).
    /// </summary>
    public bool CanPlantAt(long cellX, long cellY)
    {
        if (!IsLoadedAt(cellX, cellY))
            return false;

        GeneratedCell cell = GetCell(cellX, cellY);
        return IsArableSoil(in cell, cellX, cellY)
            && !HasEntityBlocking(cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.Crop, cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.CropPlan, cellX, cellY);
    }

    /// <summary>Можно сложить склад: загружено, трава, клетка свободна, склада ещё нет.</summary>
    public bool CanStockpileAt(long cellX, long cellY)
        => IsReadyBase(cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.Stockpile, cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.StockpilePlan, cellX, cellY);

    // ---------- Действия (гейт + метка) ----------

    public bool TryBuildAt(long cellX, long cellY)
        => CanBuildAt(cellX, cellY) && _world.AddBlock(WorldDeltaKind.Building, cellX, cellY);

    public bool TryPlaceWallAt(long cellX, long cellY)
        => CanPlaceWallAt(cellX, cellY) && _world.AddBlock(WorldDeltaKind.Wall, cellX, cellY);

    public bool TryMineStoneAt(long cellX, long cellY)
        => CanMineStoneAt(cellX, cellY) && _world.AddBlock(WorldDeltaKind.MinedStone, cellX, cellY);

    public bool TryCutTreeAt(long cellX, long cellY)
        => CanCutTreeAt(cellX, cellY) && _world.AddBlock(WorldDeltaKind.CutTree, cellX, cellY);

    public bool TryPlantAt(long cellX, long cellY)
        => CanPlantAt(cellX, cellY) && _world.AddBlock(WorldDeltaKind.Crop, cellX, cellY);

    public bool TryStockpileAt(long cellX, long cellY)
        => CanStockpileAt(cellX, cellY) && _world.AddBlock(WorldDeltaKind.Stockpile, cellX, cellY);

    // ---------- Служебное ----------

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private GeneratedCell GetCell(long cellX, long cellY)
    {
        // F-011 fast-path: чтение из уже загруженного в память чанка за O(1)
        if (_world is IWorldCellSource source && source.TryCellInfo(cellX, cellY, out GeneratedCell residentCell))
            return residentCell;
        return WorldCellQuery.CellAt(_seed, _generatorVersion, cellX, cellY);
    }

    /// <summary>
    /// Пригодная под грядку/посев почва — строго голая ЗЕМЛЯ, а не «трава по террейну».
    /// Единый источник истины с рендером: пропускаются ровно те клетки, которые
    /// StreamingWorldView.GetCompositeGroundIndex рисует землёй (CompositeGrass/CompositeSteppe),
    /// и отсекаются вода/песок/лёд/дерево/камень. Иначе «поставил чертёж на землю», а на экране
    /// он лежит на песке, льду, дереве или камне — тот самый «чертёж на рандом».
    /// </summary>
    private bool IsArableSoil(in GeneratedCell cell, long cellX, long cellY)
    {
        if (cell.Terrain != BaseTerrainKind.Grass)
            return false;

        // Песок (пустыня и прибрежный пляж) и лёд рисуются не землёй (см. рендер вида).
        if (cell.Biome == (byte)BiomeType.Desert
            || cell.Biome == (byte)BiomeType.Glacier
            || cell.Biome == (byte)BiomeType.DeepWater)
            return false;

        // Видимый декор: дерево/камень по тем же порогам, что рисует вид (WorldDecor).
        // Уже вырубленное/добытое (готовая метка) декор не рисует — клетка снова голая земля.
        bool visibleTree = cell.ForestQ16 >= WorldDecor.ForestMinQ16
            && !_world.HasBlock(WorldDeltaKind.CutTree, cellX, cellY);
        if (visibleTree)
            return false;

        bool visibleStone = cell.StoneQ16 >= WorldDecor.StoneMinQ16
            && !_world.HasBlock(WorldDeltaKind.MinedStone, cellX, cellY);
        return !visibleStone;
    }

    /// <summary>Общая база для «строительных» гейтов: загружено, трава, клетка не занята объектом.</summary>
    private bool IsReadyBase(long cellX, long cellY)
        => IsLoadedAt(cellX, cellY)
            && GetCell(cellX, cellY).Terrain == BaseTerrainKind.Grass
            && !HasEntityBlocking(cellX, cellY);
}