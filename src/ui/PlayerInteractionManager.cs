using System;
using Godot;
using Game.UI.Tools;

namespace Game.UI;

public partial class PlayerInteractionManager : Node2D
{
    public static PlayerInteractionManager Instance { get; private set; }

    [Export] public CameraController Camera { get; set; }
    [Export] public SelectionBox Selection { get; set; }
    [Export] public TileMapLayer WallLayer { get; set; }

    private ITool _currentTool;
    private ITool _defaultTool;
    private bool _isMouseDown = false;
    private Vector2I _startTile;

    public event Action OnToolReset;

    public override void _Ready()
    {
        Instance = this;
        _defaultTool = new SelectTool(Selection);
        _currentTool ??= _defaultTool;
    }

    public void Initialize(TileMapLayer wallLayer, SelectionBox selection, CameraController camera)
    {
        Instance = this;
        WallLayer = wallLayer;
        Selection = selection;
        Camera = camera;

        _defaultTool = new SelectTool(Selection);
        SetTool(_defaultTool);
    }

    public void SetTool(ITool tool)
    {
        _currentTool?.Cancel();
        _currentTool = tool ?? _defaultTool;
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

        Vector2 worldPos = GetGlobalMousePosition();
        Vector2I tilePos = WallLayer.LocalToMap(WallLayer.GetLocalMousePosition());

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