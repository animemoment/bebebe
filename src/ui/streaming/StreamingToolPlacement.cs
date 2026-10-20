using System;
using Godot;
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

    /// <summary>
    /// §117.2: размеры острова Main передаёт ЯВНО (Main.ColonySizeTiles, 1024×1024) —
    /// это развязывает границы от флипа MapRenderer.MapWidth/Height. Дефолты
    /// (MapWidth/Height) остаются только как fallback для тестов/других вызывающих.
    /// </summary>
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
        => _rules.IsLoadedAt(cellX, cellY);

    public bool CanPlaceStockpile(long cellX, long cellY)
        => _rules.CanStockpileAt(cellX, cellY);

    public bool CanPlaceFarm(long cellX, long cellY)
        => _rules.CanPlantAt(cellX, cellY);

    public bool TryPlaceWall(long cellX, long cellY)
        => Apply(cellX, cellY, _rules.CanPlaceWallAt(cellX, cellY)
            && _manager.TryPlaceBlock(WorldDeltaKind.WallPlan, cellX, cellY));

    public bool CanPlaceWall(long cellX, long cellY)
        => _rules.CanPlaceWallAt(cellX, cellY);

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

    public bool CanPlaceBuilding(long cellX, long cellY)
        => _rules.CanBuildAt(cellX, cellY);

    public bool TryRemoveBuilding(long cellX, long cellY)
        => Apply(cellX, cellY, _manager.TryRemoveBlock(WorldDeltaKind.BuildingPlan, cellX, cellY)
            || _manager.TryRemoveBlock(WorldDeltaKind.Building, cellX, cellY));

    public bool TryCutTree(long cellX, long cellY)
    {
        bool gate = _rules.CanCutTreeAt(cellX, cellY);
        bool placed = gate && _manager.TryPlaceBlock(WorldDeltaKind.CutTreePlan, cellX, cellY);
        GD.Print($"[CUT-TREE] ({cellX},{cellY}) gate={gate} placed={placed} rules={_rules != null} mgr={_manager != null}");
        return Apply(cellX, cellY, placed);
    }

    public bool TryCancelCutTree(long cellX, long cellY)
        => Apply(cellX, cellY, _manager.TryRemoveBlock(WorldDeltaKind.CutTreePlan, cellX, cellY)
            || _manager.TryRemoveBlock(WorldDeltaKind.CutTree, cellX, cellY));

    public bool TryMineStone(long cellX, long cellY)
        => Apply(cellX, cellY, _rules.CanMineStoneAt(cellX, cellY)
            && _manager.TryPlaceBlock(WorldDeltaKind.MinedStonePlan, cellX, cellY));

    public bool TryCancelMineStone(long cellX, long cellY)
        => Apply(cellX, cellY, _manager.TryRemoveBlock(WorldDeltaKind.MinedStonePlan, cellX, cellY)
            || _manager.TryRemoveBlock(WorldDeltaKind.MinedStone, cellX, cellY));

    /// <summary>Клетка острова колонии: [0, _islandWidth)×[0, _islandHeight).</summary>
    public bool IsIslandCell(long cellX, long cellY)
        => cellX >= 0 && cellY >= 0 && cellX < _islandWidth && cellY < _islandHeight;

    private bool Apply(long cellX, long cellY, bool applied)
    {
        if (applied)
            PlacedCount++;
        return applied;
    }
}
