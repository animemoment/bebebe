using System;
using System.Collections.Generic;
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
    private ITool _defaultTool;
    private bool _isMouseDown = false;
    private Vector2I _startTile;

    public event Action OnToolReset;

    public override void _Ready()
    {
        Instance = this;
        _defaultTool = new SelectTool(Selection);
        _currentTool ??= _defaultTool;
        SetProcessUnhandledKeyInput(true);
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

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.Keycode == Key.F8)
            {
                StressSpawnFarm();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.F9)
            {
                StressMetrics();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.F10)
            {
                TimeManager.Instance?.SetSpeed(GameSpeed.Fast100);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    public void ResetToDefault()
    {
        SetTool(_defaultTool);
        OnToolReset?.Invoke();
    }

    // Временный стресс-хук 12к грядок (F8/F9/F10). Удалить после теста.
    // Пошагово: 1-й F8 — 1к, 2-й — +2к (=3к), 3-й — +3к (=6к), 4-й — +6к (=12к).
    private static int _stressStep;
    private static void StressSpawnFarm()
    {
        try
        {
            int[] steps = { 30, 55, 80, 110 };
            int step = _stressStep < steps.Length ? steps[_stressStep] : 110;
            _stressStep++;
            var map = MapRenderer.Instance?.MapData;
            if (map == null) { GD.PrintErr("[Stress] нет MapData"); return; }
            int cx = map.Width / 2, cy = map.Height / 2, half = step / 2;
            var plots = new List<(int X, int Y)>(step * step);
            for (int x = cx - half; x < cx + half; x++)
                for (int y = cy - half; y < cy + half; y++)
                {
                    if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) continue;
                    if (map.Ground[x, y] != TileType.Grass) continue;
                    if (map.TreeOnGrass[x, y]) continue;
                    plots.Add((x, y));
                }
            GD.Print($"[Stress] грядок к созданию: {plots.Count}");
            try { JobBroker.Instance.RegisterFarmPlotBatch(plots); }
            catch (Exception ex) { GD.PrintErr($"[Stress] RegisterBatch: {ex.Message}\n{ex.StackTrace}"); return; }
            GD.Print("[Stress] RegisterFarmPlotBatch OK");
            GD.Print($"[Stress] total={JobDispatcher.Instance.JobIndex.TotalCount} unclaimed={JobDispatcher.Instance.JobIndex.UnclaimedCount}");
        }
        catch (Exception ex) { GD.PrintErr($"[Stress] Spawn: {ex.Message}\n{ex.StackTrace}"); }
    }

    private static void StressMetrics()
    {
        try
        {
            var idx = JobDispatcher.Instance.JobIndex;
            GD.Print($"[Stress] METRICS beds={FarmJobManager.Instance.GetAllCompletedBeds().Count} unclaimed={idx.UnclaimedCount} total={idx.TotalCount}");
        }
        catch (Exception ex) { GD.PrintErr($"[Stress] Metrics: {ex.Message}"); }
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