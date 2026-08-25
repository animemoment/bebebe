using Godot;
using Game.Core;
using Game.Simulation;
using System.Collections.Generic;

namespace Game.UI.Tools;

public class FarmingTool : ITool
{
    private readonly SelectionBox _selectionBox;
    private readonly MapData _mapData;
    private readonly WallBuildManager _wallBuildManager;
    private bool _isDragging = false;
    private bool _isLeftClick = true;

    private static readonly Color FarmZoneFill = new Color(0.4f, 0.6f, 0.2f, 0.35f);
    private static readonly Color FarmZoneBorder = new Color(0.5f, 0.8f, 0.2f, 0.9f);

    private readonly List<(int X, int Y)> _cellBuffer = new(2048);

    public FarmingTool(SelectionBox selectionBox, MapData mapData, WallBuildManager wallBuildManager)
    {
        _selectionBox = selectionBox;
        _mapData = mapData;
        _wallBuildManager = wallBuildManager;
    }

    public void OnHover(Vector2I tilePos, Vector2 worldPos) { }

    public void OnClick(Vector2I tilePos, Vector2 worldPos, bool isLeftClick)
    {
        _isDragging = true;
        _isLeftClick = isLeftClick;

        if (_selectionBox != null)
        {
            _selectionBox.SetStyle(FarmZoneFill, FarmZoneBorder);
            _selectionBox.StartSelection(worldPos);
        }
    }

    public void OnDrag(Vector2I startTile, Vector2I currentTile, Vector2 currentWorldPos)
    {
        if (_isDragging && _selectionBox != null)
        {
            _selectionBox.UpdateSelection(currentWorldPos);
        }
    }

    public void OnRelease(Vector2I startTile, Vector2I endTile, Vector2 worldPos, bool isLeftClick)
    {
        if (!_isDragging) return;
        _isDragging = false;

        _selectionBox?.EndSelection();
        _selectionBox?.ResetDefaultStyle();

        int minX = Mathf.Clamp(Mathf.Min(startTile.X, endTile.X), 0, MapRenderer.MapWidth - 1);
        int maxX = Mathf.Clamp(Mathf.Max(startTile.X, endTile.X), 0, MapRenderer.MapWidth - 1);
        int minY = Mathf.Clamp(Mathf.Min(startTile.Y, endTile.Y), 0, MapRenderer.MapHeight - 1);
        int maxY = Mathf.Clamp(Mathf.Max(startTile.Y, endTile.Y), 0, MapRenderer.MapHeight - 1);

        _cellBuffer.Clear();

        if (isLeftClick)
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (CanDesignateFarmPlot(x, y))
                    {
                        _cellBuffer.Add((x, y));
                    }
                }
            }

            if (_cellBuffer.Count > 0)
            {
                FarmJobManager.Instance.MarkPlotsBatch(_cellBuffer, _mapData?.TreeOnGrass);
            }
        }
        else
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    _cellBuffer.Add((x, y));
                }
            }

            if (_cellBuffer.Count > 0)
            {
                FarmJobManager.Instance.UnmarkPlotsBatch(_cellBuffer);
            }
        }
    }

    public void Cancel()
    {
        _isDragging = false;
        _selectionBox?.CancelSelection();
        _selectionBox?.ResetDefaultStyle();
    }

    private bool CanDesignateFarmPlot(int x, int y)
    {
        if (_mapData != null && _mapData.Ground[x, y] == TileType.Water)
            return false;

        if (_wallBuildManager != null && _wallBuildManager.IsWallAt(x, y))
            return false;

        if (BuildingManager.Instance.HasBuildingAt(x, y))
            return false;

        if (BlueprintManager.Instance.IsBlueprintAt(x, y))
            return false;

        if (FarmJobManager.Instance.IsGardenBed(x, y) || FarmJobManager.Instance.IsPlotMarked(x, y))
            return false;

        return true;
    }
}