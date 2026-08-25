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

    public void OnHover(Vector2I tilePos, Vector2 worldPos)
    {
        if (_isDragging || _ghostLayer == null) return;
        ClearGhost();

        if (CanPlaceBlueprintAt(tilePos.X, tilePos.Y))
        {
            Vector2I atlasCoords = Vector2I.Zero;
            if (_buildingType == BuildingType.WoodWall)
            {
                var wallBlueprints = BlueprintManager.Instance.GetWallBlueprints();
                atlasCoords = WallTileHelper.GetTile((tilePos.X, tilePos.Y), wallBlueprints);
            }

            _ghostLayer.SetCell(tilePos, _sourceId, atlasCoords);
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

        HashSet<(int X, int Y)> wallBlueprints = _buildingType == BuildingType.WoodWall ? BlueprintManager.Instance.GetWallBlueprints() : null;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                if (CanPlaceBlueprintAt(x, y))
                {
                    Vector2I pos = new Vector2I(x, y);
                    Vector2I atlasCoords = Vector2I.Zero;
                    if (_buildingType == BuildingType.WoodWall && wallBlueprints != null)
                    {
                        atlasCoords = WallTileHelper.GetTile((x, y), wallBlueprints);
                    }

                    _ghostLayer.SetCell(pos, _sourceId, atlasCoords);
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

        if (_mapData != null && _mapData.Ground[x, y] == TileType.Water)
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
