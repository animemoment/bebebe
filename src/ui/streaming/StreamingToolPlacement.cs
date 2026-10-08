using System;
using Game.Core.WorldStreaming.Integration;
using Game.UI.Tools;

namespace Game.UI.Streaming;

/// <summary>
/// Адаптер «инструменты → бесконечный мир» (PLAN §26 шаг 2, §28): гейты берутся у
/// <see cref="WorldGameRules"/> (вода/гора/декор/уже занято), запись — у
/// <see cref="StreamingWorldManager"/> (персистентные метки, дельтами). Клетки острова сюда
/// не попадают: их обрабатывает островная логика инструмента.
/// §29: инструмент ставит ЧЕРТЁЖ (план), а работу выполняют люди мира и записывают «готово».
/// </summary>
public sealed class StreamingToolPlacement : IToolWorldPlacement
{
    private readonly StreamingWorldManager _manager;
    private readonly WorldGameRules _rules;
    private readonly long _islandWidth;
    private readonly long _islandHeight;

    public StreamingToolPlacement(
        StreamingWorldManager manager,
        WorldGameRules rules,
        long islandWidth = MapRenderer.MapWidth,
        long islandHeight = MapRenderer.MapHeight)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _islandWidth = islandWidth;
        _islandHeight = islandHeight;
    }

    /// <summary>Сколько клеток реально поставлено/снято через адаптер (диагностика).</summary>
    public int PlacedCount { get; private set; }

    public bool IsWorldCell(long cellX, long cellY)
        => !IsIslandCell(cellX, cellY) && _rules.IsLoadedAt(cellX, cellY);

    public bool CanPlaceStockpile(long cellX, long cellY)
        => !IsIslandCell(cellX, cellY) && _rules.CanStockpileAt(cellX, cellY);

    public bool CanPlaceFarm(long cellX, long cellY)
        => !IsIslandCell(cellX, cellY) && _rules.CanPlantAt(cellX, cellY);

    public bool TryPlaceWall(long cellX, long cellY)
        => Apply(cellX, cellY, _rules.CanPlaceWallAt(cellX, cellY)
            && _manager.TryPlaceBlock(WorldDeltaKind.WallPlan, cellX, cellY));

    public bool CanPlaceWall(long cellX, long cellY)
        => !IsIslandCell(cellX, cellY) && _rules.CanPlaceWallAt(cellX, cellY);

    public bool TryRemoveWall(long cellX, long cellY)
        => Apply(cellX, cellY, _manager.TryRemoveBlock(WorldDeltaKind.WallPlan, cellX, cellY)
            || _manager.TryRemoveBlock(WorldDeltaKind.Wall, cellX, cellY));

    public bool TryPlaceStockpile(long cellX, long cellY)
        => Apply(cellX, cellY, _rules.CanStockpileAt(cellX, cellY)
            && _manager.TryPlaceBlock(WorldDeltaKind.StockpilePlan, cellX, cellY));

    public bool TryRemoveStockpile(long cellX, long cellY)
        => Apply(cellX, cellY, _manager.TryRemoveBlock(WorldDeltaKind.StockpilePlan, cellX, cellY)
            || _manager.TryRemoveBlock(WorldDeltaKind.Stockpile, cellX, cellY));

    public bool TryPlaceFarmPlot(long cellX, long cellY)
        => Apply(cellX, cellY, _rules.CanPlantAt(cellX, cellY)
            && _manager.TryPlaceBlock(WorldDeltaKind.CropPlan, cellX, cellY));

    public bool TryRemoveFarmPlot(long cellX, long cellY)
        => Apply(cellX, cellY, _manager.TryRemoveBlock(WorldDeltaKind.CropPlan, cellX, cellY)
            || _manager.TryRemoveBlock(WorldDeltaKind.Crop, cellX, cellY));

    public bool TryPlaceBuilding(long cellX, long cellY)
        => Apply(cellX, cellY, _rules.CanBuildAt(cellX, cellY)
            && _manager.TryPlaceBlock(WorldDeltaKind.BuildingPlan, cellX, cellY));

    public bool TryRemoveBuilding(long cellX, long cellY)
        => Apply(cellX, cellY, _manager.TryRemoveBlock(WorldDeltaKind.BuildingPlan, cellX, cellY)
            || _manager.TryRemoveBlock(WorldDeltaKind.Building, cellX, cellY));

    public bool TryCutTree(long cellX, long cellY)
        => Apply(cellX, cellY, _rules.TryCutTreeAt(cellX, cellY));

    public bool TryCancelCutTree(long cellX, long cellY)
        => Apply(cellX, cellY, _manager.TryRemoveBlock(WorldDeltaKind.CutTree, cellX, cellY));

    public bool TryMineStone(long cellX, long cellY)
        => Apply(cellX, cellY, _rules.TryMineStoneAt(cellX, cellY));

    public bool TryCancelMineStone(long cellX, long cellY)
        => Apply(cellX, cellY, _manager.TryRemoveBlock(WorldDeltaKind.MinedStone, cellX, cellY));

    /// <summary>Клетка острова — работа инструмента, не мира.</summary>
    public bool IsIslandCell(long cellX, long cellY)
        => cellX >= 0 && cellY >= 0 && cellX < _islandWidth && cellY < _islandHeight;

    private bool Apply(long cellX, long cellY, bool applied)
    {
        if (applied)
            PlacedCount++;
        return applied;
    }
}
