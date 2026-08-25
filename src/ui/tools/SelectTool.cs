using Godot;
using Game.Simulation;

namespace Game.UI.Tools;

public class SelectTool : ITool
{
    private readonly SelectionBox _selectionBox;
    private Vector2 _startMousePos;
    private bool _hasDragged;

    public SelectTool(SelectionBox selectionBox)
    {
        _selectionBox = selectionBox;
    }

    public void OnHover(Vector2I tilePos, Vector2 worldPos)
    {
        FarmZoneManager.Instance.SetHoveredTile(tilePos.X, tilePos.Y);
    }

    public void OnClick(Vector2I tilePos, Vector2 worldPos, bool isLeftClick)
    {
        if (isLeftClick)
        {
            _startMousePos = worldPos;
            _hasDragged = false;
            _selectionBox?.StartSelection(worldPos);
        }
    }

    public void OnDrag(Vector2I startTile, Vector2I currentTile, Vector2 currentWorldPos)
    {
        if (_selectionBox != null && _selectionBox.IsSelecting)
        {
            if ((currentWorldPos - _startMousePos).LengthSquared() > 40.0f)
            {
                _hasDragged = true;
            }
            _selectionBox.UpdateSelection(currentWorldPos);
        }
    }

    public void OnRelease(Vector2I startTile, Vector2I endTile, Vector2 worldPos, bool isLeftClick)
    {
        if (isLeftClick)
        {
            if (_selectionBox != null && _selectionBox.IsSelecting)
            {
                _selectionBox.EndSelection();
            }

            if (!_hasDragged)
            {
                FarmZoneManager.Instance.SelectZoneAt(endTile.X, endTile.Y);
            }
        }
    }

    public void Cancel()
    {
        _selectionBox?.CancelSelection();
        FarmZoneManager.Instance.SetHoveredTile(-1, -1);
        FarmZoneManager.Instance.DeselectZone();
    }
}