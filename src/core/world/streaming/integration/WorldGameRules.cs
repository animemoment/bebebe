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

    /// <summary>Можно добывать камень: загружено, горы (скала), не занято объектом, ещё не добыто.</summary>
    public bool CanMineStoneAt(long cellX, long cellY)
        => IsLoadedAt(cellX, cellY)
            && WorldCellQuery.IsMountainAt(_seed, _generatorVersion, cellX, cellY)
            && !HasEntityBlocking(cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.MinedStone, cellX, cellY);

    /// <summary>Можно вырубить дерево: загружено, лесной шум высокий, не вода, не занято, ещё не вырублено.</summary>
    public bool CanCutTreeAt(long cellX, long cellY)
    {
        GeneratedCell cell = WorldCellQuery.CellAt(_seed, _generatorVersion, cellX, cellY);
        return IsLoadedAt(cellX, cellY)
            && cell.ForestQ16 >= TreeForestMin
            && cell.Terrain != BaseTerrainKind.Water
            && !HasEntityBlocking(cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.CutTree, cellX, cellY);
    }

    /// <summary>Можно засеять: загружено, трава, влажность в пахотном диапазоне, клетка свободна (и не вырублена/не добыта).</summary>
    public bool CanPlantAt(long cellX, long cellY)
    {
        // Один семпл генератора на все террейн/влажностные условия.
        GeneratedCell cell = WorldCellQuery.CellAt(_seed, _generatorVersion, cellX, cellY);
        return IsLoadedAt(cellX, cellY)
            && cell.Terrain == BaseTerrainKind.Grass
            && !HasEntityBlocking(cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.Crop, cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.CutTree, cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.MinedStone, cellX, cellY)
            && cell.MoistureQ16 >= FarmMoistureMin && cell.MoistureQ16 <= FarmMoistureMax;
    }

    /// <summary>Можно сложить склад: загружено, трава, клетка свободна, склада ещё нет.</summary>
    public bool CanStockpileAt(long cellX, long cellY)
        => IsReadyBase(cellX, cellY)
            && !_world.HasBlock(WorldDeltaKind.Stockpile, cellX, cellY);

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

    /// <summary>Общая база для «строительных» гейтов: загружено, трава, клетка не занята объектом.</summary>
    private bool IsReadyBase(long cellX, long cellY)
        => IsLoadedAt(cellX, cellY)
            && WorldCellQuery.TerrainAt(_seed, _generatorVersion, cellX, cellY) == BaseTerrainKind.Grass
            && !HasEntityBlocking(cellX, cellY);
}