using System.Collections.Generic;
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
    [Export] public Vector2 MinPositionTiles = new Vector2(-1_000_000f, -1_000_000f);
    [Export] public Vector2 MaxPositionTiles = new Vector2(1_000_000f, 1_000_000f);
    [Export] public int TileSize = MapRenderer.TileSizePx;

    public void SetWorldBoundsTiles(long minX, long minY, long maxX, long maxY)
    {
        MinPositionTiles = new Vector2(minX, minY);
        MaxPositionTiles = new Vector2(maxX, maxY);
        ClampPosition();
    }

    private bool _isMiddleDragging;

    private const string ActionUp = "camera_up";
    private const string ActionDown = "camera_down";
    private const string ActionLeft = "camera_left";
    private const string ActionRight = "camera_right";

    // === Событийная модель ввода (FIX §117 F3: «камера едет сама» после отпускания) ===
    // Было: _Process поллил Input.IsPhysicalKeyPressed и двигал камеру
    // Position += direction * speed * delta без clamp delta. При хитчах главного
    // потока (загрузка чанков, пересчёт террейна) кадр длинный, delta огромная,
    // события ввода применяются с опозданием — камера «двигается сама» и мышка рывками.
    // Стало:
    // 1) press/release ловятся событиями в _UnhandledInput; release ВСЕГДА снимает
    //    направление (лечит и залипание synthetic-ввода MCP key_hold/key_release);
    // 2) delta для расчёта скорости ограничена MaxMoveDeltaSec (~1/30 c);
    // 3) поллинг остаётся ТОЛЬКО санити-фоллбеком: если событий клавиатуры нет
    //    дольше KeyResyncIntervalSec (потеря фокуса окна, release съеден GUI),
    //    состояние зажатых клавиш синхронизируется с Input напрямую.

    private const double KeyResyncIntervalSec = 2.0;
    // Один хитч ≠ телепорт камеры на пол-экрана: движение за кадр не больше 1/30 c.
    private const float MaxMoveDeltaSec = 0.034f;
    // Middle-drag: огромный Relative после хитча не должен телепортировать камеру.
    private const float MaxDragStepWorldPx = 2000f;

    private readonly HashSet<Key> _heldKeys = new();
    private double _secondsSinceKeyEvent = KeyResyncIntervalSec;

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
                var ev = new InputEventKey { Keycode = key, PhysicalKeycode = key };
                im.ActionAddEvent(actionName, ev);
            }
        }
    }

    public override void _Process(double delta)
    {
        // Санити-фоллбек: событийный поток клавиатуры молчит дольше 2с — сверяемся с Input.
        _secondsSinceKeyEvent += delta;
        if (_secondsSinceKeyEvent >= KeyResyncIntervalSec)
            ResyncHeldKeysFromInput();

        Vector2 direction = Vector2.Zero;

        if (IsHeld(Key.W) || IsHeld(Key.Up))
            direction.Y -= 1;
        if (IsHeld(Key.S) || IsHeld(Key.Down))
            direction.Y += 1;
        if (IsHeld(Key.A) || IsHeld(Key.Left))
            direction.X -= 1;
        if (IsHeld(Key.D) || IsHeld(Key.Right))
            direction.X += 1;

        if (direction != Vector2.Zero)
            direction = direction.Normalized();

        // Clamp delta: хитч делает кадр «длинным», но камера сдвигается не больше,
        // чем на 1/30 c пути — без прыжков на пол-экрана и без разгона после отпускания.
        float speed = MoveSpeed * Mathf.Min((float)delta, MaxMoveDeltaSec);
        if (Input.IsKeyPressed(Key.Shift) || Input.IsPhysicalKeyPressed(Key.Shift))
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

            case InputEventKey keyEvent:
                HandleKeyEvent(keyEvent);
                break;

            case InputEventMouseMotion mouseMotion when _isMiddleDragging:
                HandleMiddleDragMotion(mouseMotion);
                break;
        }
    }

    private void HandleKeyEvent(InputEventKey keyEvent)
    {
        // Пока события клавиатуры приходят, фоллбек-поллинг не включается.
        _secondsSinceKeyEvent = 0.0;

        if (keyEvent.Echo)
            return; // авто-повтор не меняет состояние зажатия

        // PhysicalKeycode — приоритет (независимо от раскладки); фоллбек на Keycode
        // ловит synthetic-события, где physical_keycode не проставлен.
        Key key = keyEvent.PhysicalKeycode != Key.None
            ? keyEvent.PhysicalKeycode
            : keyEvent.Keycode;

        if (!IsCameraKey(key))
            return;

        if (keyEvent.Pressed)
            _heldKeys.Add(key);
        else
            _heldKeys.Remove(key); // release ВСЕГДА снимает направление
    }

    private static bool IsCameraKey(Key key)
    {
        return key == Key.W || key == Key.A || key == Key.S || key == Key.D
            || key == Key.Up || key == Key.Down || key == Key.Left || key == Key.Right;
    }

    private bool IsHeld(Key key) => _heldKeys.Contains(key);

    private void ResyncHeldKeysFromInput()
    {
        ResyncKey(Key.W);
        ResyncKey(Key.A);
        ResyncKey(Key.S);
        ResyncKey(Key.D);
        ResyncKey(Key.Up);
        ResyncKey(Key.Down);
        ResyncKey(Key.Left);
        ResyncKey(Key.Right);
        _secondsSinceKeyEvent = 0.0;
    }

    private void ResyncKey(Key key)
    {
        if (Input.IsPhysicalKeyPressed(key) || Input.IsKeyPressed(key))
            _heldKeys.Add(key);
        else
            _heldKeys.Remove(key);
    }

    private void HandleMiddleDragMotion(InputEventMouseMotion mouseMotion)
    {
        // Деление на актуальный Zoom — как раньше (мировая дельта), но шаг за кадр
        // ограничен: хитч с одним огромным Relative не телепортирует камеру.
        Vector2 worldDelta = mouseMotion.Relative / Zoom;

        float step = worldDelta.Length();
        if (step > MaxDragStepWorldPx)
            worldDelta *= MaxDragStepWorldPx / step;

        Position -= worldDelta;
        ClampPosition();
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
        if (!IsInsideTree())
            return;

        Vector2 minPixels = MinPositionTiles * TileSize;
        Vector2 maxPixels = MaxPositionTiles * TileSize;
        Vector2 worldSpan = maxPixels - minPixels;
        Vector2 viewportSize = GetViewportRect().Size;

        float visibleWorldX = viewportSize.X / Zoom.X;
        float visibleWorldY = viewportSize.Y / Zoom.Y;

        if (visibleWorldX >= worldSpan.X)
        {
            Position = new Vector2((minPixels.X + maxPixels.X) / 2f, Position.Y);
        }
        else
        {
            float halfViewWorld = visibleWorldX / 2f;
            Position = new Vector2(
                Mathf.Clamp(Position.X, minPixels.X + halfViewWorld, maxPixels.X - halfViewWorld),
                Position.Y
            );
        }

        if (visibleWorldY >= worldSpan.Y)
        {
            Position = new Vector2(Position.X, (minPixels.Y + maxPixels.Y) / 2f);
        }
        else
        {
            float halfViewWorld = visibleWorldY / 2f;
            Position = new Vector2(
                Position.X,
                Mathf.Clamp(Position.Y, minPixels.Y + halfViewWorld, maxPixels.Y - halfViewWorld)
            );
        }
    }
}
