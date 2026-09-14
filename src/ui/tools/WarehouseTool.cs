using Godot;
using Game.Simulation;

namespace Game.UI.Tools;

public class WarehouseTool : ITool
{
    private readonly SelectionBox _selectionBox;
    private bool _isDragging = false;
    private bool _isLeftClick = true;

    private static readonly Color PurpleZoneFill = new Color(0.6f, 0.1f, 0.85f, 0.4f);
    private static readonly Color PurpleZoneBorder = new Color(0.85f, 0.3f, 1.0f, 0.9f);

    public WarehouseTool(SelectionBox selectionBox)
    {
        _selectionBox = selectionBox;
    }

    public void OnHover(Vector2I tilePos, Vector2 worldPos) { }

    public void OnClick(Vector2I tilePos, Vector2 worldPos, bool isLeftClick)
    {
        _isDragging = true;
        _isLeftClick = isLeftClick;

        if (_selectionBox != null)
        {
            _selectionBox.SetStyle(PurpleZoneFill, PurpleZoneBorder);
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

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                if (isLeftClick)
                    StockpileManager.Instance.AddZoneTile(x, y);
                else
                    StockpileManager.Instance.RemoveZoneTile(x, y);
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