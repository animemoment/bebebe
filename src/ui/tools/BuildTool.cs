using Godot;
using Game.Core;
using Game.Simulation;
using System;
using System.Collections.Generic;

namespace Game.UI.Tools;

public class BuildTool : ITool
{
    private readonly WallBuildManager _wallBuildManager;
    private readonly TileMapLayer _ghostLayer;
    private readonly MapData _mapData;
    private readonly BuildingType _buildingType;
    private readonly int _sourceId;
    private bool _isDragging;
    private bool _isLeftClick = true;
    private readonly HashSet<Vector2I> _previewTiles = new(128);
    private readonly List<(int X, int Y)> _cellBuffer = new(2048);

    /// <summary>Мир за кромкой острова (§26 шаг 2, §28): клетки вне острова ставятся метками мира.</summary>
    public IToolWorldPlacement WorldPlacement { get; set; }

    public BuildTool(
        WallBuildManager wallBuildManager,
        TileMapLayer ghostLayer,
        MapData mapData,
        BuildingType buildingType = BuildingType.WoodWall,
        int sourceId = MapRenderer.SourceWall)
    {
        _wallBuildManager = wallBuildManager;
        _ghostLayer = ghostLayer;
        _mapData = mapData;
        _buildingType = buildingType;
        _sourceId = sourceId;
    }

    // Ghost-превью стен: движковый автотайлинг (TerrainSet 0 / terrain 0
    // из сцены wood_wall_tile). SetCellsTerrainConnect сам пересчитывает
    // соседей — WallTileHelper здесь не нужен.
    private const int GhostTerrainSetId = 0;
    private const int GhostWallTerrainId = 0;

    private void PaintGhostPreview(IEnumerable<Vector2I> cells)
    {
        if (_ghostLayer == null || _ghostLayer.TileSet == null) return;
        var arr = new Godot.Collections.Array<Vector2I>();
        foreach (var c in cells)
            arr.Add(c);
        if (arr.Count == 0) return;
        _ghostLayer.SetCellsTerrainConnect(arr, GhostTerrainSetId, GhostWallTerrainId, true);
        foreach (var c in cells)
            _previewTiles.Add(c);
    }

    public void OnHover(Vector2I tilePos, Vector2 worldPos)
    {
        if (_isDragging || _ghostLayer == null) return;
        ClearGhost();

        if (_buildingType == BuildingType.WoodWall)
        {
            if (CanPlaceBlueprintAt(tilePos.X, tilePos.Y))
                PaintGhostPreview(new[] { tilePos });
        }
        else if (CanPlaceBlueprintAt(tilePos.X, tilePos.Y))
        {
            // Мебель — одиночный ghost-Source (в ghost-TileSet под номером
            // Ghost*Source; исходный _sourceId из building-набора здесь
            // указывал бы на стену/пустоту).
            int ghostSource = MapRenderer.GhostSourceForBuilding(_buildingType);
            if (ghostSource < 0) return;
            _ghostLayer.SetCell(tilePos, ghostSource, Vector2I.Zero);
            _previewTiles.Add(tilePos);
        }
    }

    public void OnClick(Vector2I tilePos, Vector2 worldPos, bool isLeftClick)
    {
        if (!IsValidCoord(tilePos.X, tilePos.Y) && WorldPlacement == null) return;
        _isDragging = true;
        _isLeftClick = isLeftClick;
        _lastDragTile = new(-1, -1);
    }

    private Vector2I _lastDragTile = new(-1, -1);

    public void OnDrag(Vector2I startTile, Vector2I currentTile, Vector2 currentWorldPos)
    {
        if (!_isDragging || _ghostLayer == null) return;
        if (_lastDragTile == currentTile) return;
        _lastDragTile = currentTile;
        ClearGhost();

        int minX = Mathf.Min(startTile.X, currentTile.X);
        int maxX = Mathf.Max(startTile.X, currentTile.X);
        int minY = Mathf.Min(startTile.Y, currentTile.Y);
        int maxY = Mathf.Max(startTile.Y, currentTile.Y);

        long cellCount = (long)(maxX - minX + 1) * (maxY - minY + 1);
        if (cellCount > 100_000)
            return;

        if (_buildingType == BuildingType.WoodWall)
        {
            var cells = new List<Vector2I>((int)Math.Min(cellCount, 10_000));
            for (int x = minX; x <= maxX; x++)
                for (int y = minY; y <= maxY; y++)
                    if (CanPlaceBlueprintAt(x, y))
                        cells.Add(new Vector2I(x, y));
            PaintGhostPreview(cells);
            return;
        }

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                if (CanPlaceBlueprintAt(x, y))
                {
                    Vector2I pos = new Vector2I(x, y);
                    int ghostSource = MapRenderer.GhostSourceForBuilding(_buildingType);
                    if (ghostSource < 0) continue;
                    _ghostLayer.SetCell(pos, ghostSource, Vector2I.Zero);
                    _previewTiles.Add(pos);
                }
            }
        }
    }

    public void OnRelease(Vector2I startTile, Vector2I endTile, Vector2 worldPos, bool isLeftClick)
    {
        _isDragging = false;
        _lastDragTile = new(-1, -1);
        ClearGhost();

        int minX = Mathf.Min(startTile.X, endTile.X);
        int maxX = Mathf.Max(startTile.X, endTile.X);
        int minY = Mathf.Min(startTile.Y, endTile.Y);
        int maxY = Mathf.Max(startTile.Y, endTile.Y);

        long cellCount = (long)(maxX - minX + 1) * (maxY - minY + 1);
        if (cellCount > 100_000)
            return;

        _cellBuffer.Clear();
        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                _cellBuffer.Add((x, y));
            }
        }

        if (isLeftClick)
        {
            var validIslandCells = new List<(int X, int Y)>(_cellBuffer.Count);
            foreach (var (x, y) in _cellBuffer)
            {
                if (CanPlaceBlueprintAt(x, y))
                {
                    if (IsValidCoord(x, y))
                    {
                        validIslandCells.Add((x, y));
                    }
                    else if (WorldPlacement != null)
                    {
                        if (_buildingType == BuildingType.WoodWall)
                            WorldPlacement.TryPlaceWall(x, y);
                        else
                            WorldPlacement.TryPlaceBuilding(x, y);
                    }
                }
            }

            if (validIslandCells.Count > 0)
            {
                var def = StructureRegistry.Get(_buildingType);
                ConstructionManager.Instance.StartSitesBatch(validIslandCells, def, _mapData);
            }
        }
        else
        {
            var islandCells = new List<(int X, int Y)>();
            foreach (var (x, y) in _cellBuffer)
            {
                if (IsValidCoord(x, y))
                    islandCells.Add((x, y));
                else if (WorldPlacement != null)
                {
                    if (_buildingType == BuildingType.WoodWall)
                        WorldPlacement.TryRemoveWall(x, y);
                    else
                        WorldPlacement.TryRemoveBuilding(x, y);
                }
            }
            if (islandCells.Count > 0)
            {
                ConstructionManager.Instance.CancelSitesBatch(islandCells);
                BlueprintManager.Instance.RemoveBlueprintsBatch(islandCells);
            }
        }
    }

    public void Cancel()
    {
        _isDragging = false;
        _lastDragTile = new(-1, -1);
        ClearGhost();
    }

    private bool CanPlaceBlueprintAt(int x, int y)
    {
        if (IsValidCoord(x, y))
        {
            if (_mapData != null && _mapData.Ground[x, y] != TileType.Grass)
                return false;

            // На камень не строим: сначала добудь россыпь инструментом камня.
            if (_mapData != null && _mapData.HasStone(x, y))
                return false;

            if (StructureManager.Instance.HasStructureAt(x, y))
                return false;

            if (ConstructionManager.Instance.IsSiteAt(x, y))
                return false;

            if (_wallBuildManager != null && _wallBuildManager.IsWallAt(x, y))
                return false;

            if (BuildingManager.Instance.HasBuildingAt(x, y))
                return false;

            if (BlueprintManager.Instance.IsBlueprintAt(x, y))
                return false;

            // Фермерство стен не терпит: запрет на саму грядку/метку и на кольцо вокруг.
            // Касается только стен (WoodWall), мебель можно ставить где угодно.
            if (_buildingType == BuildingType.WoodWall && IsFarmTileOrAdjacent(x, y))
                return false;

            return true;
        }

        // Вне острова колонии: проверяем через WorldPlacement
        if (WorldPlacement != null)
        {
            return _buildingType == BuildingType.WoodWall
                ? WorldPlacement.CanPlaceWall(x, y)
                : WorldPlacement.CanPlaceBuilding(x, y);
        }

        return false;
    }

    /// <summary>
    /// True если клетка — грядка/метка фермы или соседняя (8 соседей) с такой.
    /// Соседство проверяем по FarmJobManager (грядки+метки) и ZoneManager (Farm-зоны).
    /// </summary>
    private static bool IsFarmTileOrAdjacent(int x, int y)
    {
        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                int nx = x + ox, ny = y + oy;
                if (!IsValidCoord(nx, ny)) continue;
                if (FarmJobManager.Instance.IsGardenBed(nx, ny)
                    || FarmJobManager.Instance.IsPlotMarked(nx, ny))
                    return true;
                if (ZoneManager.Instance.TryGetZoneAt(nx, ny, out var zone)
                    && zone.Kind == ZoneKind.Farm)
                    return true;
            }
        }
        return false;
    }

    private static bool IsValidCoord(int x, int y) =>
        x >= 0 && y >= 0 && x < MapRenderer.MapWidth && y < MapRenderer.MapHeight;

    private void ClearGhost()
    {
        if (_ghostLayer == null) return;
        foreach (var pos in _previewTiles)
            _ghostLayer.EraseCell(pos);
        _previewTiles.Clear();
    }
}
