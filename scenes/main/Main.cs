using System;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.Simulation;
using Game.UI;
using Game.UI.Streaming;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Game.Main;

public partial class Main : Node2D
{
	[Export] public ulong WorldSeed = 0;
	private const uint WorldGeneratorVersion = 3;
	private const long WorldRegionsX = 2048; // регионов по X (мировая карта §23)
	private const long WorldRegionsY = 1024; // регионов по Y
	private const long WorldRegionCells = 512; // клеток в регионе

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
	private ItemShadowRenderer _itemShadows;
	private CropRenderer _cropRenderer;
	private GroundItemRenderer _itemRenderer;
	private StreamingWorldView _worldView;
	private StreamingWorldManager _worldManager;
	private WorldGameRules _worldRules;
	private StreamingToolPlacement _worldPlacement;
	private PackedScene _worldMapScene;
	private WorldMapOverlay _mapOverlay;
	private float _syncTimer;
	private long _lastStreamerTileX = long.MinValue;
	private long _lastStreamerTileY = long.MinValue;

	/// <summary>Точка выбора игрока на мировой карте. (-1,-1) = ожидает выбора.</summary>
	private RegionKey _spawnRegion = new(-1, -1);
	private bool _spawnPending = false;

	[Export] public int AgentCount = 10000;
	[Export] public HUDController HUD;

	/// <summary>Привязать кнопку ButtonMap к ToggleWorldMap. Вызывается из _Ready().</summary>
	private void SetupButtonMapBinding()
	{
		var hud = HUD
				?? FindChild("hud_tscn", true, false) as HUDController
				?? FindChild("HUDController", true, false) as HUDController;
		if (hud != null)
		{
			hud.BindWorldMapButton(ToggleWorldMap);
			GD.Print("[MAIN] ButtonMap bound to ToggleWorldMap via " + hud.Name);
		}
		else
		{
			GD.PrintErr("[MAIN] HUDController/hud_tscn NOT found for ButtonMap binding!");
		}
	}

	public override void _Ready()
	{
		if (WorldSeed == 0)
		{
			WorldSeed = (ulong)Random.Shared.NextInt64() ^ ((ulong)DateTime.UtcNow.Ticks << 32);
			if (WorldSeed == 0) WorldSeed = 0x51EED4C0FFEEUL;
		}

		_timeManager = new TimeManager { Name = "TimeManager" };
		AddChild(_timeManager);
		_timeManager.OnSpeedChanged += speed =>
		{
			if (_agentThread != null)
			{
				_agentThread.IsPaused = (speed == GameSpeed.Paused);
				_agentThread.SpeedMultiplier = (float)speed;
			}
		};

		// Смена сутит: один CanvasModulate на весь мир (O(1) для GPU).
		// HUD в CanvasLayer не затемняется. Движение — игровое время из SimThread.
		_dayNightModulate = new CanvasModulate { Name = "DayNightModulate" };
		AddChild(_dayNightModulate);
		_dayNightCycle = new DayNightCycle { Name = "DayNightCycle" };
		AddChild(_dayNightCycle);
		_dayNightCycle.Initialize(_dayNightModulate);

		_camera = new CameraController
		{
			Name = "Camera",
			// §24: ограничение камеры — ГРАНИЦЫ МИРА (1024×2048 регионов), а не острова 512×512.
			MapSizeTiles = new Vector2(
				(float)(WorldRegionsX * WorldRegionCells),
				(float)(WorldRegionsY * WorldRegionCells)),
			TileSize = MapRenderer.TileSizePx
		};
		AddChild(_camera);
		_camera.MakeCurrent();
		_camera.Zoom = new Vector2(0.5f, 0.5f);
		// Старт — центр острова (остров занимает клетки [0,512)² мировой сетки).
		_camera.Position = new Vector2(
			MapRenderer.MapWidth * MapRenderer.TileSizePx / 2f,
			MapRenderer.MapHeight * MapRenderer.TileSizePx / 2f);

		// §24: стриминговый фон бесконечного мира ПОД островом и на всю планету.
		_worldView = new StreamingWorldView
		{
			Name = "StreamingWorldView",
			WorldSeed = WorldSeed,
			GeneratorVersion = WorldGeneratorVersion,
			ZIndex = -1,
			Visible = true
		};
		AddChild(_worldView);

		// Создание игровых слоев и менеджеров UI ДО стримингового хранилища
		_selection = new SelectionBox { Name = "Selection" };
		AddChild(_selection);

		_interactionManager = new PlayerInteractionManager { Name = "PlayerInteractionManager" };
		AddChild(_interactionManager);

		_mapRenderer = new MapRenderer { Name = "MapRenderer", DeferAutoGeneration = true };
		AddChild(_mapRenderer);
		_mapRenderer.OnMapApplied += OnMapApplied;

		var farmZoneRenderer = new FarmZoneRenderer { Name = "FarmZoneRenderer" };
		AddChild(farmZoneRenderer);

		var cropRenderer = new CropRenderer { Name = "CropRenderer" };
		AddChild(cropRenderer);
		_cropRenderer = cropRenderer;

		var itemRenderer = new GroundItemRenderer { Name = "GroundItemRenderer" };
		AddChild(itemRenderer);
		_itemRenderer = itemRenderer;

		// Тени мелочи (Syx ShadowBatch): один MultiMesh на предметы+урожай+склад.
		_itemShadows = new ItemShadowRenderer { Name = "ItemShadowRenderer" };
		AddChild(_itemShadows);
		_itemRenderer.ItemShadows = _itemShadows;
		_cropRenderer.ItemShadows = _itemShadows;
		if (_mapRenderer.StockpileItems != null)
			_mapRenderer.StockpileItems.ItemShadows = _itemShadows;

		// Профайлер производительности (F3) — инициализируется гарантированно
		_profilerOverlay = new PerformanceOverlay { Name = "PerformanceOverlay" };
		AddChild(_profilerOverlay);

		// Привязка кнопки ButtonMap к ToggleWorldMap — гарантированно при старте сцены
		SetupButtonMapBinding();

		// Загружаем сцену мировой карты (CanvasLayer + Camera2D + TileMapLayer)
		_worldMapScene = GD.Load<PackedScene>("res://scenes/ui/WorldMap.tscn");
		_mapOverlay = _worldMapScene.Instantiate<WorldMapOverlay>();
		_mapOverlay.Name = "WorldMapOverlay";
		_mapOverlay.IsSpawnSelectionMode = true; // Первичный выбор спавна!
		_mapOverlay.Initialize(WorldSeed, WorldGeneratorVersion);
		_mapOverlay.OnRegionSelected += OnRegionSelected;
		_mapOverlay.OnSeedRegenerated += OnSeedRegenerated;
		AddChild(_mapOverlay);

		// Сразу открываем мировую карту для выбора точки спавна игроком
		Callable.From(() => _mapOverlay.OpenMap()).CallDeferred();
	}

	private void InitStreamingWorld(ulong seed)
	{
		try
		{
			string baseCacheDir = ProjectSettings.GlobalizePath("user://infinite_world_lab_cache");
			string worldSaveDir = System.IO.Path.Combine(baseCacheDir, $"world_{seed:x16}");
			var worldCoordinator = new WorldSaveCoordinator(
				worldSaveDir, seed, WorldGeneratorVersion, UnifiedWorldGenerator.FeatureSchemaVersion);
			_worldManager = new StreamingWorldManager(_worldView, worldCoordinator);
			_worldManager.Open(0, 0);
			_worldRules = new WorldGameRules(_worldView, seed, WorldGeneratorVersion);
			if (_worldPlacement != null)
				_worldPlacement = new StreamingToolPlacement(_worldManager, _worldRules);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[MAIN] Ошибка инициализации стримингового хранилища мира для сида 0x{seed:x16}: {ex.Message}");
		}
	}

	private void UpdateCameraWorldBounds()
	{
		if (_camera == null) return;
		long totalPlanetCellsX = WorldRegionsX * WorldRegionCells;
		long totalPlanetCellsY = WorldRegionsY * WorldRegionCells;
		long minTileX = -UnifiedWorldGenerator.WorldOriginX;
		long maxTileX = totalPlanetCellsX - UnifiedWorldGenerator.WorldOriginX;
		long minTileY = -UnifiedWorldGenerator.WorldOriginY;
		long maxTileY = totalPlanetCellsY - UnifiedWorldGenerator.WorldOriginY;
		_camera.SetWorldBoundsTiles(minTileX, minTileY, maxTileX, maxTileY);
	}

	private async void OnSeedRegenerated(ulong newSeed)
	{
		WorldSeed = newSeed;
		if (_worldView != null)
			_worldView.WorldSeed = newSeed;

		GD.Print($"[MAIN] Синхронизирован новый сид мира: 0x{WorldSeed:X16} (Архетип: {Game.Core.WorldClimate.WorldClimateParams.GetArchetype(WorldSeed)})");

		// Если игрок еще на этапе первичного выбора спавна, не тратим ресурсы на фоновую генерацию колонии
		if (_mapOverlay != null && _mapOverlay.IsSpawnSelectionMode)
			return;

		RegionKey spawnRegion = UnifiedWorldGenerator.FindHabitableSpawn(WorldSeed, WorldGeneratorVersion);
		UnifiedWorldGenerator.SetWorldOrigin(spawnRegion.X * WorldRegionCells, spawnRegion.Y * WorldRegionCells);
		UpdateCameraWorldBounds();

		var startClimate = WorldClimateGenerator.SampleRegion(WorldSeed, WorldGeneratorVersion, spawnRegion);
		var mapData = await Task.Run(() => UnifiedWorldGenerator.GenerateColonyWindow(
			WorldSeed,
			WorldGeneratorVersion,
			0,
			0,
			MapRenderer.MapWidth,
			MapRenderer.MapHeight,
			startClimate));

		if (_agentThread != null)
		{
			_agentThread.Stop();
			_agentThread = null;
		}

		BlueprintManager.Instance.ClearAll();
		BuildingManager.Instance.ClearAll();
		StockpileManager.Instance.ClearAll();
		FarmJobManager.Instance.ClearAll();
		TreeJobManager.Instance.ClearAll();
		StoneJobManager.Instance.ClearAll();
		GroundItemManager.Instance.ClearAll();

		_mapRenderer.ExternalPendingMap = mapData;
		_mapRenderer.StartAsync();

		if (_camera != null)
		{
			_camera.Position = new Vector2(
				MapRenderer.MapWidth * MapRenderer.TileSizePx / 2f,
				MapRenderer.MapHeight * MapRenderer.TileSizePx / 2f);
			_camera.Zoom = new Vector2(0.5f, 0.5f);
		}

		if (_mapOverlay != null)
			_mapOverlay.CurrentColonyRegion = spawnRegion;

		_lastStreamerTileX = long.MinValue;
		_lastStreamerTileY = long.MinValue;

		_worldView?.ResetWorld(newSeed);
		InitStreamingWorld(newSeed);
		GD.Print($"[MAIN] Синхронизирован новый сид мира: 0x{WorldSeed:X16} (Архетип: {Game.Core.WorldClimate.WorldClimateParams.GetArchetype(WorldSeed)})");
	}

	/// <summary>Игрок выбрал регион на мировой карте — генерируем карту и начинаем игру.</summary>
	private async void OnRegionSelected(RegionKey region)
	{
		GD.Print($"[SPAWN] Выбран регион [{region.X}, {region.Y}] → генерация карты...");

		ulong seed = WorldSeed;
		uint version = WorldGeneratorVersion;
		UnifiedWorldGenerator.SetWorldOrigin(region.X * WorldRegionCells, region.Y * WorldRegionCells);
		UpdateCameraWorldBounds();

		var climate = WorldClimateGenerator.SampleRegion(seed, version, region);
		var mapData = await Task.Run(() => UnifiedWorldGenerator.GenerateColonyWindow(
			seed,
			version,
			0,
			0,
			MapRenderer.MapWidth,
			MapRenderer.MapHeight,
			null));

		if (_agentThread != null)
		{
			_agentThread.Stop();
			_agentThread = null;
		}

		BlueprintManager.Instance.ClearAll();
		BuildingManager.Instance.ClearAll();
		StockpileManager.Instance.ClearAll();
		FarmJobManager.Instance.ClearAll();
		TreeJobManager.Instance.ClearAll();
		StoneJobManager.Instance.ClearAll();
		GroundItemManager.Instance.ClearAll();

		_mapRenderer.ExternalPendingMap = mapData;
		_mapRenderer.StartAsync();

		if (_camera != null)
		{
			_camera.Position = new Vector2(
				MapRenderer.MapWidth * MapRenderer.TileSizePx / 2f,
				MapRenderer.MapHeight * MapRenderer.TileSizePx / 2f);
			_camera.Zoom = new Vector2(0.5f, 0.5f);
		}

		if (_mapOverlay != null)
			_mapOverlay.CurrentColonyRegion = region;

		_lastStreamerTileX = long.MinValue;
		_lastStreamerTileY = long.MinValue;

		_worldView?.ResetWorld(seed);
		InitStreamingWorld(seed);
	}

	private void OnMapApplied()
	{
		_interactionManager.Initialize(_mapRenderer.WallLayer, _selection, _camera);

		if (_agentThread == null && _mapRenderer.MapData != null)
		{
			_agentThread = new AgentSimulationThread();
			_agentThread.Start(AgentCount, _mapRenderer.MapData.Ground, _mapRenderer.MapData.TreeOnGrass, 0, _mapRenderer.MapData.Humidity, _mapRenderer.MapData.StoneOnGrass, _mapRenderer.MapData.Fertility);

			// Стартовый спавн 100 зёрен в центре карты
			int centerX = MapRenderer.MapWidth / 2;
			int centerY = MapRenderer.MapHeight / 2;
			GroundItemManager.Instance.SpawnItems(centerX, centerY, ItemId.Grain, 100);

			if (_agentRenderer == null)
			{
				_agentRenderer = new AgentRenderer { Name = "AgentRenderer" };
				AddChild(_agentRenderer);
			}
			_agentRenderer.Initialize(_agentThread, AgentCount);
		}

		HUDController hud = HUD
							?? GetTree().Root.FindChild("HUDController", true, false) as HUDController
							?? FindChild("HUDController", true, false) as HUDController
							?? FindChild("hud_tscn", true, false) as HUDController;

		if (hud != null)
		{
			hud.Setup(_interactionManager, _mapRenderer, AgentCount);
			// §28: инструменты получают доступ к миру за кромкой острова.
			if (_worldManager != null && _worldRules != null)
			{
				_worldPlacement ??= new StreamingToolPlacement(_worldManager, _worldRules);
				hud.WorldPlacement = _worldPlacement;
				GD.Print("[MAIN] §28: мир за кромкой подключён к инструментам (стены/склад/ферма/камень/лес)");
			}
		}
	}

	public override void _Process(double delta)
	{
		// Синхронизация игрового времени (симуляция — в фоновом потоке, UI — в главном).
		if (_timeManager != null && _agentThread != null)
		{
			_syncTimer += (float)delta;
			if (_syncTimer >= 0.1f)
			{
				_syncTimer = 0f;
				_timeManager.SyncGameTime(_agentThread.GameTimeSeconds);
			}
		}

		// День/ночь: тинт мира по игровому времени (троттилинг внутри, O(1) для GPU).
		// Тикает даже до старта SimThread — тогда GameTimeSeconds=0 (полночь, ночь).
		if (_dayNightCycle != null && _timeManager != null)
		{
			using (GameProfiler.Scope("Render: DayNight"))
			{
				_dayNightCycle.Tick(_timeManager.GameTimeSeconds, (float)delta);
			}

  			// Солнце по углу слева направо: тени статик + агентов + мелочи за один тик, O(1).
  			using (GameProfiler.Scope("Render: Shadows"))
  			{
  				var sun = DayNightCycle.SampleSun(WorldTime.TimeOfDaySeconds(_timeManager.GameTimeSeconds));
  				_mapRenderer?.ShadowRenderer?.Tick(sun, (float)delta);
  				_agentRenderer?.ApplyShadow(sun);
  				_itemShadows?.SetSun(sun);
  			}
		}

		// §24: окно стримингового фона следует за камерой (мировые клетки, троттлинг для 60 FPS).
		if (_worldView != null && _camera != null)
		{
			long camTileX = (long)(_camera.Position.X / MapRenderer.TileSizePx);
			long camTileY = (long)(_camera.Position.Y / MapRenderer.TileSizePx);
			if (Math.Abs(camTileX - _lastStreamerTileX) >= 8 || Math.Abs(camTileY - _lastStreamerTileY) >= 8)
			{
				_lastStreamerTileX = camTileX;
				_lastStreamerTileY = camTileY;
				Rect2 viewport = GetViewport().GetVisibleRect();
				float scale = (float)MapRenderer.TileSizePx;
				float halfW = viewport.Size.X / (2f * _camera.Zoom.X);
				float halfH = viewport.Size.Y / (2f * _camera.Zoom.Y);
				var visible = new WorldRect(
					(long)Mathf.Floor(_camera.Position.X / scale - halfW / scale) - 3,
					(long)Mathf.Floor(_camera.Position.Y / scale - halfH / scale) - 3,
					(long)Mathf.Ceil(_camera.Position.X / scale + halfW / scale) + 3,
					(long)Mathf.Ceil(_camera.Position.Y / scale + halfH / scale) + 3);
				_worldView.Update(visible);
			}
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.M || key.PhysicalKeycode == Key.M)
			{
				ToggleWorldMap();
				GetViewport().SetInputAsHandled();
			}
		}
	}

	/// <summary>Открыть/закрыть мировую карту. Скрывает CanvasLayer игры, показывает TileMapLayer.</summary>
	public void ToggleWorldMap()
	{
		if (_mapOverlay == null) return;

		// Скрытие/восстановление игрового UI (включая CanvasLayer с HUD) делает сам оверлей,
		// чтобы все пути закрытия (кнопка, «Закрыть карту», выход) работали одинаково.
		if (_mapOverlay.Visible)
			_mapOverlay.CloseMap();
		else
			_mapOverlay.OpenMap();
	}

	public override void _ExitTree()
	{
		if (_mapRenderer != null)
			_mapRenderer.OnMapApplied -= OnMapApplied;

		if (_mapOverlay != null)
		{
			_mapOverlay.OnRegionSelected -= OnRegionSelected;
			_mapOverlay.OnSeedRegenerated -= OnSeedRegenerated;
		}
		_mapOverlay?.QueueFree();
		_worldView?.Shutdown();
		_agentThread?.Stop();
		base._ExitTree();
	}
}
