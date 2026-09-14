using Godot;
using Game.Core;
using Game.Simulation;
using System.Collections.Generic;

namespace Game.UI.Tools;

public class TreeFellingTool : ITool
{
    private readonly SelectionBox _selectionBox;
    private readonly MapData _mapData;
    private bool _isDragging = false;
    private bool _isLeftClick = true;

    private static readonly Color BlackZoneFill = new Color(0f, 0f, 0f, 0.4f);
    private static readonly Color BlackZoneBorder = new Color(0.1f, 0.1f, 0.1f, 0.85f);

    private readonly List<(int X, int Y)> _cellBuffer = new(2048);

    public TreeFellingTool(SelectionBox selectionBox, MapData mapData)
    {
        _selectionBox = selectionBox;
        _mapData = mapData;
    }

    public void OnHover(Vector2I tilePos, Vector2 worldPos) { }

    public void OnClick(Vector2I tilePos, Vector2 worldPos, bool isLeftClick)
    {
        _isDragging = true;
        _isLeftClick = isLeftClick;

        if (_selectionBox != null)
        {
            _selectionBox.SetStyle(BlackZoneFill, BlackZoneBorder);
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
                    if (_mapData != null && _mapData.TreeOnGrass[x, y])
                    {
                        _cellBuffer.Add((x, y));
                    }
                }
            }

            if (_cellBuffer.Count > 0)
            {
                TreeJobManager.Instance.MarkTreesBatch(_cellBuffer);
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
                TreeJobManager.Instance.UnmarkTreesBatch(_cellBuffer);
            }
        }
    }

    public void Cancel()
    {
        _isDragging = false;
        _selectionBox?.CancelSelection();
        _selectionBox?.ResetDefaultStyle();
    }
}
