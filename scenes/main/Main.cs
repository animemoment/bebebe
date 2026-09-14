using Godot;
using Game.Core;
using Game.Simulation;
using Game.UI;
using System.Collections.Generic;

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
	private CanvasModulate _dayNightModulate;
	private DayNightCycle _dayNightCycle;
	private float _syncTimer;

	[Export] public int AgentCount = 100000;
	[Export] public HUDController HUD;

	public override void _Ready()
	{
		_timeManager = new TimeManager { Name = "TimeManager" };
		AddChild(_timeManager);

		// Смена суток: один CanvasModulate на весь мир (O(1) для GPU).
		// HUD в CanvasLayer не затемняется. Движок — игровое время из SimThread.
		_dayNightModulate = new CanvasModulate { Name = "DayNightModulate" };
		AddChild(_dayNightModulate);
		_dayNightCycle = new DayNightCycle { Name = "DayNightCycle" };
		AddChild(_dayNightCycle);
		_dayNightCycle.Initialize(_dayNightModulate);

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

		var farmZoneRenderer = new FarmZoneRenderer { Name = "FarmZoneRenderer" };
		AddChild(farmZoneRenderer);

		var cropRenderer = new CropRenderer { Name = "CropRenderer" };
		AddChild(cropRenderer);

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
			_agentThread.Start(AgentCount, _mapRenderer.MapData.Ground, _mapRenderer.MapData.TreeOnGrass, 0, _mapRenderer.MapData.Humidity);

			// Стартовый спавн 100 зерна в центре карты
			int centerX = MapRenderer.MapWidth / 2;
			int centerY = MapRenderer.MapHeight / 2;
			GroundItemManager.Instance.SpawnItems(centerX, centerY, ItemId.Grain, 100);

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
							?? FindChild("HUDController", true, false) as HUDController
							?? FindChild("hud_tscn", true, false) as HUDController;

		if (hud != null)
		{
			hud.Setup(_interactionManager, _mapRenderer, AgentCount);
		}
	}

	public override void _Process(double delta)
	{
		// Синхронизация мирового времени (симуляция — в фоновом потоке, UI — в главном).
		if (_timeManager != null && _agentThread != null)
		{
			_syncTimer += (float)delta;
			if (_syncTimer >= 0.1f)
			{
				_syncTimer = 0f;
				_timeManager.SyncGameTime(_agentThread.GameTimeSeconds);
			}
		}

		// День/ночь: тинт мира по игровому времени (троттлинг внутри, O(1) для GPU).
		// Тикает даже до старта SimThread — тогда GameTimeSeconds=0 (полночь, ночь).
		if (_dayNightCycle != null && _timeManager != null)
		{
			using (GameProfiler.Scope("Render: DayNight"))
			{
				_dayNightCycle.Tick(_timeManager.GameTimeSeconds, (float)delta);
			}

			// Солнце по дуге слева направо: тени статики + агентов за один тик, O(1).
			using (GameProfiler.Scope("Render: Shadows"))
			{
				var sun = DayNightCycle.SampleSun(WorldTime.TimeOfDaySeconds(_timeManager.GameTimeSeconds));
				_mapRenderer?.ShadowRenderer?.Tick(sun, (float)delta);
				_agentRenderer?.ApplyShadow(sun);
			}
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
