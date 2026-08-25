using Godot;

namespace Game.UI.Tools;

public interface ITool
{
    void OnHover(Vector2I tilePos, Vector2 worldPos);
    void OnClick(Vector2I tilePos, Vector2 worldPos, bool isLeftClick);
    void OnDrag(Vector2I startTile, Vector2I currentTile, Vector2 currentWorldPos);
    void OnRelease(Vector2I startTile, Vector2I endTile, Vector2 worldPos, bool isLeftClick);
    void Cancel();
}