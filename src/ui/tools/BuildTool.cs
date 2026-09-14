using Godot;
using Game.Core;
using Game.Simulation;
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

    // Ghost-превью стен: движковый автотайлинг ( TerrainSet 0 / terrain 0
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
            _ghostLayer.SetCell(tilePos, _sourceId, Vector2I.Zero);
            _previewTiles.Add(tilePos);
        }
    }

    public void OnClick(Vector2I tilePos, Vector2 worldPos, bool isLeftClick)
    {
        if (!IsValidCoord(tilePos.X, tilePos.Y)) return;
        _isDragging = true;
        _isLeftClick = isLeftClick;
    }

    public void OnDrag(Vector2I startTile, Vector2I currentTile, Vector2 currentWorldPos)
    {
        if (!_isDragging || _ghostLayer == null) return;
        ClearGhost();

        int minX = Mathf.Clamp(Mathf.Min(startTile.X, currentTile.X), 0, MapRenderer.MapWidth - 1);
        int maxX = Mathf.Clamp(Mathf.Max(startTile.X, currentTile.X), 0, MapRenderer.MapWidth - 1);
        int minY = Mathf.Clamp(Mathf.Min(startTile.Y, currentTile.Y), 0, MapRenderer.MapHeight - 1);
        int maxY = Mathf.Clamp(Mathf.Max(startTile.Y, currentTile.Y), 0, MapRenderer.MapHeight - 1);

        if (_buildingType == BuildingType.WoodWall)
        {
            var cells = new List<Vector2I>();
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
                    _ghostLayer.SetCell(pos, _sourceId, Vector2I.Zero);
                    _previewTiles.Add(pos);
                }
            }
        }
    }

    public void OnRelease(Vector2I startTile, Vector2I endTile, Vector2 worldPos, bool isLeftClick)
    {
        _isDragging = false;
        ClearGhost();

        int minX = Mathf.Clamp(Mathf.Min(startTile.X, endTile.X), 0, MapRenderer.MapWidth - 1);
        int maxX = Mathf.Clamp(Mathf.Max(startTile.X, endTile.X), 0, MapRenderer.MapWidth - 1);
        int minY = Mathf.Clamp(Mathf.Min(startTile.Y, endTile.Y), 0, MapRenderer.MapHeight - 1);
        int maxY = Mathf.Clamp(Mathf.Max(startTile.Y, endTile.Y), 0, MapRenderer.MapHeight - 1);

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
            var validCells = new List<(int X, int Y)>(_cellBuffer.Count);
            foreach (var (x, y) in _cellBuffer)
            {
                if (CanPlaceBlueprintAt(x, y))
                {
                    validCells.Add((x, y));
                }
            }

            if (validCells.Count > 0)
            {
                BlueprintManager.Instance.AddBlueprintsBatch(validCells, _buildingType, _mapData?.TreeOnGrass);
            }
        }
        else
        {
            BlueprintManager.Instance.RemoveBlueprintsBatch(_cellBuffer);
        }
    }

    public void Cancel()
    {
        _isDragging = false;
        ClearGhost();
    }

    private bool CanPlaceBlueprintAt(int x, int y)
    {
        if (!IsValidCoord(x, y)) return false;

        if (_mapData != null && _mapData.Ground[x, y] != TileType.Grass)
            return false;

        if (_wallBuildManager != null && _wallBuildManager.IsWallAt(x, y))
            return false;

        if (BuildingManager.Instance.HasBuildingAt(x, y))
            return false;

        if (BlueprintManager.Instance.IsBlueprintAt(x, y))
            return false;

        return true;
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
