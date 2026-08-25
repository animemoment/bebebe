using Godot;
using Game.Core;
using Game.Simulation;
using Game.UI;

namespace Game.Main;

public partial class Main : Node2D
{
    private AgentSimulationThread _agentThread;
    private AgentRenderer _agentRenderer;
    private MapRenderer _mapRenderer;
    private PlayerInteractionManager _interactionManager;
    private CameraController _camera;
    private SelectionBox _selection;
    private TimeManager _timeManager;
    private PerformanceOverlay _profilerOverlay;

    [Export] public int AgentCount = 550;
    [Export] public HUDController HUD;

    public override void _Ready()
    {
        _timeManager = new TimeManager { Name = "TimeManager" };
        AddChild(_timeManager);

        _camera = new CameraController
        {
            Name = "Camera",
            MapSizeTiles = new Vector2(MapRenderer.MapWidth, MapRenderer.MapHeight),
            TileSize = MapRenderer.TileSizePx
        };
        AddChild(_camera);
        _camera.MakeCurrent();
        _camera.Zoom = new Vector2(0.5f, 0.5f);

        _selection = new SelectionBox { Name = "Selection" };
        AddChild(_selection);

        _interactionManager = new PlayerInteractionManager { Name = "PlayerInteractionManager" };
        AddChild(_interactionManager);

        _mapRenderer = new MapRenderer { Name = "MapRenderer" };
        AddChild(_mapRenderer);
        _mapRenderer.OnMapApplied += OnMapApplied;

        var itemRenderer = new GroundItemRenderer { Name = "GroundItemRenderer" };
        AddChild(itemRenderer);

        _profilerOverlay = new PerformanceOverlay { Name = "PerformanceOverlay" };
        AddChild(_profilerOverlay);
    }

    private void OnMapApplied()
    {
        _interactionManager.Initialize(_mapRenderer.WallLayer, _selection, _camera);

        if (_agentThread == null && _mapRenderer.MapData != null)
        {
            _agentThread = new AgentSimulationThread();
            _agentThread.Start(AgentCount, _mapRenderer.MapData.Ground, _mapRenderer.MapData.TreeOnGrass);

            _timeManager.OnSpeedChanged += speed =>
            {
                if (_agentThread != null)
                {
                    _agentThread.IsPaused = (speed == GameSpeed.Paused);
                    _agentThread.SpeedMultiplier = (float)speed;
                }
            };

            _agentRenderer = new AgentRenderer { Name = "AgentRenderer" };
            AddChild(_agentRenderer);
            _agentRenderer.Initialize(_agentThread, AgentCount);
        }

        HUDController hud = HUD
                            ?? GetTree().Root.FindChild("HUDController", true, false) as HUDController
                            ?? FindChild("HUDController", true, false) as HUDController;

        if (hud != null)
        {
            hud.Setup(_interactionManager, _mapRenderer, AgentCount);
        }
    }

    public override void _ExitTree()
    {
        if (_mapRenderer != null)
        {
            _mapRenderer.OnMapApplied -= OnMapApplied;
        }

        _agentThread?.Stop();
        base._ExitTree();
    }
}