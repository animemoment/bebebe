using Godot;

namespace Game.UI;

public partial class CameraController : Camera2D
{
    [Export] public float MoveSpeed = 3500f;
    [Export] public float ShiftMultiplier = 2.5f;
    [Export] public float ZoomFactor = 0.12f;
    // Ограничение зума: предотвращает отрисовку сотен тысяч перекрывающихся тайлов в 1 пиксель
    [Export] public Vector2 MinZoom = new Vector2(0.12f, 0.12f);
    [Export] public Vector2 MaxZoom = new Vector2(4f, 4f);
    [Export] public Vector2 MapSizeTiles = new Vector2(MapRenderer.MapWidth, MapRenderer.MapHeight);
    [Export] public int TileSize = MapRenderer.TileSizePx;

    private bool _isMiddleDragging;

    private const string ActionUp = "camera_up";
    private const string ActionDown = "camera_down";
    private const string ActionLeft = "camera_left";
    private const string ActionRight = "camera_right";

    public override void _Ready()
    {
        EnsureAction(ActionUp, Key.W, Key.Up);
        EnsureAction(ActionDown, Key.S, Key.Down);
        EnsureAction(ActionLeft, Key.A, Key.Left);
        EnsureAction(ActionRight, Key.D, Key.Right);

        Vector2 mapSizePixels = MapSizeTiles * TileSize;
        Position = mapSizePixels / 2f;

        Vector2 viewportSize = GetViewportRect().Size;
        float coverage = Mathf.Sqrt(0.35f);
        float zoomX = viewportSize.X / (mapSizePixels.X * coverage);
        float zoomY = viewportSize.Y / (mapSizePixels.Y * coverage);
        float zoom = Mathf.Max(0.12f, Mathf.Min(zoomX, zoomY));
        Zoom = new Vector2(zoom, zoom);
    }

    private static void EnsureAction(string actionName, params Key[] defaultKeys)
    {
        var im = InputMap.Singleton;
        if (!im.HasAction(actionName))
        {
            im.AddAction(actionName);
            foreach (var key in defaultKeys)
            {
                var ev = new InputEventKey { Keycode = key };
                im.ActionAddEvent(actionName, ev);
            }
        }
    }

    public override void _Process(double delta)
    {
        Vector2 direction = Vector2.Zero;

        if (Input.IsActionPressed(ActionUp))
            direction.Y -= 1;
        if (Input.IsActionPressed(ActionDown))
            direction.Y += 1;
        if (Input.IsActionPressed(ActionLeft))
            direction.X -= 1;
        if (Input.IsActionPressed(ActionRight))
            direction.X += 1;

        if (direction != Vector2.Zero)
            direction = direction.Normalized();

        float speed = MoveSpeed * (float)delta;
        if (Input.IsKeyPressed(Key.Shift))
            speed *= ShiftMultiplier;

        Position += direction * speed;
        ClampPosition();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton mouseButton:
                HandleMouseButton(mouseButton);
                break;

            case InputEventMouseMotion mouseMotion when _isMiddleDragging:
                Vector2 screenDelta = mouseMotion.Relative;
                Position -= screenDelta / Zoom;
                ClampPosition();
                break;
        }
    }

    private void HandleMouseButton(InputEventMouseButton mouseButton)
    {
        if (mouseButton.ButtonIndex == MouseButton.Middle)
        {
            _isMiddleDragging = mouseButton.Pressed;
            return;
        }

        if (mouseButton.ButtonIndex == MouseButton.WheelUp && mouseButton.Pressed)
        {
            ZoomAtPoint(Zoom * (1f + ZoomFactor));
        }
        else if (mouseButton.ButtonIndex == MouseButton.WheelDown && mouseButton.Pressed)
        {
            ZoomAtPoint(Zoom / (1f + ZoomFactor));
        }
    }

    private void ZoomAtPoint(Vector2 newZoom)
    {
        Vector2 worldMouse = GetGlobalMousePosition();

        newZoom = newZoom.Clamp(MinZoom, MaxZoom);
        if (newZoom == Zoom)
            return;

        Zoom = newZoom;
        Position += worldMouse - GetGlobalMousePosition();
        ClampPosition();
    }

    private void ClampPosition()
    {
        Vector2 mapSizePixels = MapSizeTiles * TileSize;
        Vector2 viewportSize = GetViewportRect().Size;

        float visibleWorldX = viewportSize.X / Zoom.X;
        float visibleWorldY = viewportSize.Y / Zoom.Y;

        if (visibleWorldX >= mapSizePixels.X)
        {
            Position = new Vector2(mapSizePixels.X / 2f, Position.Y);
        }
        else
        {
            float halfViewWorld = visibleWorldX / 2f;
            Position = new Vector2(
                Mathf.Clamp(Position.X, halfViewWorld, mapSizePixels.X - halfViewWorld),
                Position.Y
            );
        }

        if (visibleWorldY >= mapSizePixels.Y)
        {
            Position = new Vector2(Position.X, mapSizePixels.Y / 2f);
        }
        else
        {
            float halfViewWorld = visibleWorldY / 2f;
            Position = new Vector2(
                Position.X,
                Mathf.Clamp(Position.Y, halfViewWorld, mapSizePixels.Y - halfViewWorld)
            );
        }
    }
}