using Godot;

namespace Game.UI.Tools;

public class SelectTool : ITool
{
    private readonly SelectionBox _selectionBox;

    public SelectTool(SelectionBox selectionBox)
    {
        _selectionBox = selectionBox;
    }

    public void OnHover(Vector2I tilePos, Vector2 worldPos) { }

    public void OnClick(Vector2I tilePos, Vector2 worldPos, bool isLeftClick)
    {
        if (isLeftClick && _selectionBox != null)
            _selectionBox.StartSelection(worldPos);
    }

    public void OnDrag(Vector2I startTile, Vector2I currentTile, Vector2 currentWorldPos)
    {
        if (_selectionBox != null && _selectionBox.IsSelecting)
            _selectionBox.UpdateSelection(currentWorldPos);
    }

    public void OnRelease(Vector2I startTile, Vector2I endTile, Vector2 worldPos, bool isLeftClick)
    {
        if (isLeftClick && _selectionBox != null && _selectionBox.IsSelecting)
            _selectionBox.EndSelection();
    }

    public void Cancel()
    {
        _selectionBox?.CancelSelection();
    }
}