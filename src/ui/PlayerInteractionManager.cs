using System;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.UI.Tools;

namespace Game.UI;

public partial class PlayerInteractionManager : Node2D
{
    public static PlayerInteractionManager Instance { get; private set; }

    [Export] public CameraController Camera { get; set; }
    [Export] public SelectionBox Selection { get; set; }
    [Export] public TileMapLayer WallLayer { get; set; }

    private ITool _currentTool;
    private SelectTool _defaultTool;
    private ICellProbeOverlay _cellProbe;
    public ITool DefaultTool => _defaultTool;

    /// <summary>
    /// Прокинуть источник подсказок клетки в дефолтный инструмент. Принимает узкий интерфейс:
    /// PlayerInteractionManager не должен ссылаться на HUDController, иначе цикл
    /// SelectTool → HUDController → PIM → SelectTool снова сомкнётся.
    /// Вызов до создания инструмента безопасен — проба применится в _Ready/Initialize.
    /// </summary>
    public void SetCellProbe(ICellProbeOverlay probe)
    {
        _cellProbe = probe;
        _defaultTool?.BindCellProbe(probe);
    }
    private bool _isMouseDown = false;
    private Vector2I _startTile;

    public event Action OnToolReset;

    public override void _Ready()
    {
        Instance = this;
        _defaultTool = new SelectTool(Selection);
        _defaultTool.BindCellProbe(_cellProbe);
        _currentTool ??= _defaultTool;
    }

    public void Initialize(TileMapLayer wallLayer, SelectionBox selection, CameraController camera)
    {
        Instance = this;
        WallLayer = wallLayer;
        Selection = selection;
        Camera = camera;

        _defaultTool = new SelectTool(Selection);
        _defaultTool.BindCellProbe(_cellProbe);
        SetTool(_defaultTool);
    }

    public void SetTool(ITool tool)
    {
        // F69: момент «нажали инструмент» — видно на графике.
        var next = tool ?? _defaultTool;
        var toolName = next?.GetType().Name ?? "None";
        Game.Core.SimEvents.Mark("TOOL", toolName);
        Game.Core.SimEvents.Count("tool." + toolName);
        _currentTool?.Cancel();
        _currentTool = next;
    }

    public void ResetToDefault()
    {
        SetTool(_defaultTool);
        OnToolReset?.Invoke();
    }

    public bool IsDefaultTool => _currentTool == _defaultTool;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_currentTool == null || WallLayer == null)
            return;

        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            if (!IsDefaultTool)
            {
                ResetToDefault();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        // §118: позиция из события, а не из GetGlobalMousePosition(): текущее
        // положение мыши могло уже уехать (отстающие/синтетические клики летели мимо).
        Vector2 worldPos;
        if (@event is InputEventMouseMotion motionEv)
            worldPos = (Vector2)(GetViewport().GetCanvasTransform().AffineInverse() * motionEv.Position);
        else if (@event is InputEventMouseButton btnEv)
            worldPos = (Vector2)(GetViewport().GetCanvasTransform().AffineInverse() * btnEv.Position);
        else
            worldPos = GetGlobalMousePosition();

        Vector2I tilePos = WallLayer.LocalToMap(WallLayer.ToLocal(worldPos));

        if (@event is InputEventMouseMotion)
        {
            if (_isMouseDown)
                _currentTool.OnDrag(_startTile, tilePos, worldPos);
            else
                _currentTool.OnHover(tilePos, worldPos);
        }
        else if (@event is InputEventMouseButton mouseBtn)
        {
            if (mouseBtn.ButtonIndex == MouseButton.Middle)
                return;

            GD.Print($"[PIM] btn={mouseBtn.ButtonIndex} pressed={mouseBtn.Pressed} tile=({tilePos.X},{tilePos.Y}) world=({worldPos.X:F0},{worldPos.Y:F0}) tool={_currentTool?.GetType().Name ?? "null"}");

            bool isLeft = mouseBtn.ButtonIndex == MouseButton.Left;
            bool isRight = mouseBtn.ButtonIndex == MouseButton.Right;

            if (mouseBtn.Pressed && (isLeft || isRight))
            {
                _isMouseDown = true;
                _startTile = tilePos;
                _currentTool.OnClick(tilePos, worldPos, isLeft);
                GetViewport().SetInputAsHandled();
            }
            else if (!mouseBtn.Pressed && _isMouseDown && (isLeft || isRight))
            {
                _isMouseDown = false;
                _currentTool.OnRelease(_startTile, tilePos, worldPos, isLeft);
                GetViewport().SetInputAsHandled();
            }
        }
    }
}
