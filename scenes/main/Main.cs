using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.Simulation;
using Game.UI;
using Game.UI.Streaming;
using System.Collections.Generic;

namespace Game.Main;

public partial class Main : Node2D
{
	private const ulong WorldMapSeed = 0x0123456789ABCDEFUL;
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

	/// <summary>Точка выбора игрока на мировой карте. (-1,-1) = ожидает выбора.</summary>
	private RegionKey _spawnRegion = new(-1, -1);
	private bool _spawnPending = false;

	[Export] public int AgentCount = 100;
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
		_timeManager = new TimeManager { Name = "TimeManager" };
		AddChild(_timeManager);

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

		// §24: стриминговый фон бесконечного мира ПОД островом (океан-архипелаг за кромкой).
		// Мир рисуется настоящими тайлами игры по 64 px (1 клетка = 1 тайл) — масштаб 1,
		// как и у острова (MapRenderer.TileSizePx = 64).
		_worldView = new StreamingWorldView
		{
			Name = "StreamingWorldView",
			WorldSeed = WorldMapSeed,
			GeneratorVersion = WorldGeneratorVersion,
			// Мир — ФОН: сценовые слои острова (mountains_tile, wood_wall_tile, грядки)
			// идут раньше него в дереве, поэтому без z=-1 стриминговые тайлы рисовались
			// ПОВЕРХ них (отсюда «странные горы» вместо автотайлов острова).
			ZIndex = -1
		};
		AddChild(_worldView);

		// §26 шаг 2: менеджер и правила мира — через них инструменты ставят ЗА кромкой
		// острова (метки мира, персистентные дельтами). Клетки [0,512)² идут прежним путём.
		string worldSaveDir = ProjectSettings.GlobalizePath("user://infinite_world_lab_cache");
		var worldCoordinator = new WorldSaveCoordinator(
			worldSaveDir, WorldMapSeed, WorldGeneratorVersion, WorldFeatureGenerator.FeatureSchemaVersion);
		_worldManager = new StreamingWorldManager(_worldView, worldCoordinator);
		_worldManager.Open(0, 0);
		_worldRules = new WorldGameRules(_worldView, WorldMapSeed, WorldGeneratorVersion);

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

		_profilerOverlay = new PerformanceOverlay { Name = "PerformanceOverlay" };
		AddChild(_profilerOverlay);

		// Привязка кнопки ButtonMap к ToggleWorldMap ДО любых других операций,
		// чтобы кнопка работала всегда (до и после старта игры).
		SetupButtonMapBinding();

		// Загружаем сцену мировой карты (CanvasLayer + Camera2D + TileMapLayer)
		_worldMapScene = GD.Load<PackedScene>("res://scenes/ui/WorldMap.tscn");
		_mapOverlay = _worldMapScene.Instantiate<WorldMapOverlay>();
		_mapOverlay.Name = "WorldMapOverlay";
		_mapOverlay.Initialize(WorldMapSeed, WorldGeneratorVersion);
		AddChild(_mapOverlay);
	}

	/// <summary>Игрок выбрал регион на мировой карте — генерируем карту и начинаем игру.</summary>
	private void OnRegionSelected(RegionKey region)
	{
		// Если игра уже начата — просто телепортируем камеру
		if (_agentThread != null)
		{
			long tileX = region.X * WorldRegionCells + WorldRegionCells / 2;
			long tileY = region.Y * WorldRegionCells + WorldRegionCells / 2;
			_camera.Position = new Vector2(tileX * MapRenderer.TileSizePx, tileY * MapRenderer.TileSizePx);
			return;
		}

		GD.Print($"[SPAWN] Выбран регион [{region.X}, {region.Y}] → генерация карты...");

		// Генерируем карту вокруг выбранной точки через MapGenerator.RegionAPI
		int spawnTileX = (int)(region.X * WorldRegionCells + WorldRegionCells / 2);
		int spawnTileY = (int)(region.Y * WorldRegionCells + WorldRegionCells / 2);

		var mapData = MapGenerator.GenerateRegion(
			spawnTileX - MapRenderer.MapWidth / 2,
			spawnTileX + MapRenderer.MapWidth / 2,
			spawnTileY - MapRenderer.MapHeight / 2,
			spawnTileY + MapRenderer.MapHeight / 2,
			unchecked((uint)WorldMapSeed),
			isPlayableMap: true,
			spawnCenterX: spawnTileX,
			spawnCenterY: spawnTileY);

		// Задаём внешнюю карту ДО вызова StartAsync, чтобы StartAsync увидел _hasExternalMap
		_mapRenderer.ExternalPendingMap = mapData;
		
		// Запускаем MapRenderer с уже заданной внешней картой
		_mapRenderer.StartAsync();
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
		bool mapOpen = _mapOverlay != null && _mapOverlay.Visible;

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
		// Пока открыта мировая карта — игровой мир полностью скрыт, тени/тинт не видны:
		// пропускаем весь рендер-тик (экономия CPU на кадрах карты).
		if (!mapOpen && _dayNightCycle != null && _timeManager != null)
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

		// §24: окно стримингового фона следует за камерой острова (мировые клетки).
		// Пока открыта мировая карта — весь игровой мир скрыт (Visible=false),
		// его Update/стриминг не нужен: это главный источник просадок FPS на карте.
		if (_worldView != null && _mapOverlay != null && !_mapOverlay.Visible)
		{
			Rect2 viewport = GetViewport().GetVisibleRect();
			float scale = (float)MapRenderer.TileSizePx;
			float halfW = viewport.Size.X / (2f * _camera.Zoom.X);
			float halfH = viewport.Size.Y / (2f * _camera.Zoom.Y);
			var visible = new WorldRect(
				(long)Mathf.Floor(_camera.Position.X / scale - halfW / scale) - 3,
				(long)Mathf.Floor(_camera.Position.Y / scale - halfH / scale) - 3,
				(long)Mathf.Ceil(_camera.Position.X / scale + halfW / scale) + 3,
				(long)Mathf.Ceil(_camera.Position.Y / scale + halfH / scale) + 3);
			_worldView.Update(visible, _camera.Zoom.X);
		}
	}

	/// <summary>Открыть/закрыть мировую карту. Скрывает CanvasLayer игры, показывает TileMapLayer.</summary>
	public void ToggleWorldMap()
	{
		if (_mapOverlay == null) return;

		if (_mapOverlay.Visible)
		{
			// Порядок важен: сначала закрываем карту (она гасит свою Camera2D),
			// ПОТОМ включаем игру — SetGameRenderActive вызывает MakeCurrent на
			// игровой камере, и она гарантированно становится текущей. Обратный
			// порядок давал рассинхрон: после закрытия карты не работали
			// движение/зум, а HUD «слетал» (кадры шли через чужую камеру).
			_mapOverlay.CloseMap();
			SetGameRenderActive(true);
			SetAgentsVisible(true);
			RestoreHudVisibility();
		}
		else
		{
			// Полная заморозка игрового рендера: скрытие одного лишь CanvasLayer
			// НЕ убирает Node2D-слои (MapRenderer с ~14 TileMapLayer острова и
			// стримингового мира, AgentRenderer, тени, урожай, предметы) — они
			// продолжают отрисовываться под картой. Это главный источник 20 FPS:
			// GPU тянет два мира одновременно. Visible=false на корневых узлах
			// исключает все дочерние слои из отрисовки за один флаг.
			SetGameRenderActive(false);
			SetAgentsVisible(false);
			// HUD (CanvasLayer игры) скрываем ПОСЛЕ включения Camera2D мировой карты:
			// в Godot 4 скрытие CanvasLayer может сделать MakeCurrent «следующей по
			// приоритету» камере и перехватить текущую камеру у только что открытой
			// карты. Порядок ниже исключает это — карта остаётся активной камерой.
			_mapOverlay.OpenMap();
			CanvasLayer canvas = GetNodeOrNull<CanvasLayer>("CanvasLayer");
			if (canvas != null) canvas.Visible = false;
		}
	}

	/// <summary>
	/// Вкл/выкл ВСЁ рисование игрового мира (остров + стриминг + агенты + тени).
	/// CameraController глушим через SetProcess(false): его _Process читает Input
	/// напрямую и двигал бы «текущую камеру» — т.е. камеру мировой карты.
	/// </summary>
	private void SetGameRenderActive(bool active)
	{
		if (_camera != null && IsInstanceValid(_camera))
		{
			_camera.SetProcess(active);
			_camera.Enabled = active;
			// При включении игры возвращаем статус текущей камеры игровой
			// камере: пока была открыта карта, текущей была Camera2D карты.
			if (active)
				_camera.MakeCurrent();
		}

		void Hide(Node n)
		{
			if (n != null && IsInstanceValid(n)) n.Visible = active;
		}

		Hide(_mapRenderer);   // все тайловые слои острова + оверлеи + тени (дочерние)
		Hide(_worldView);    // стриминговый мир: 6 TileMapLayer по 64×64 тайлов
		// Рендереры, созданные в _Ready локальными переменными, ищем по имени:
		Hide(FindChild("FarmZoneRenderer", true, false));
		Hide(_agentRenderer);
		Hide(_itemShadows);
		Hide(_cropRenderer);
		Hide(_itemRenderer);
		Hide(_selection);
		Hide(_dayNightModulate);

		// Стриминг-тик окна чанков (Update вызывается из _Process Main только при
		// активной игре; внутренний _Process вью тоже глушим для верности).
		if (_worldView != null && IsInstanceValid(_worldView))
			_worldView.SetProcess(active);
	}

	/// <summary>
	/// Страховка от «слёта интерфейса»: если при открытии карты что-то скрыло
	/// CanvasLayer/HUD, после закрытия восстанавливаем видимость явно.
	/// Повторные Visible=true идемпотентны.
	/// </summary>
	private void RestoreHudVisibility()
	{
		CanvasLayer canvas = GetNodeOrNull<CanvasLayer>("CanvasLayer");
		if (canvas != null)
			canvas.Visible = true;

		Node hud = FindChild("hud_tscn", true, false) ?? FindChild("HUDController", true, false);
		if (hud is Control hc)
			hc.Visible = true;
	}

	/// <summary>Вкл/выкл рендер агентов (MultiMesh-тела + тени + все Node2D-рендереры на дереве).</summary>
	private void SetAgentsVisible(bool visible)
	{
		if (_agentRenderer != null && IsInstanceValid(_agentRenderer))
		{
			_agentRenderer.Visible = visible;
			// Обходим всё дерево рендерера: MultiMeshInstance2D (тела/ноги/тени)
			// могут лежать в вложенных узлах — один флаг на детях 1-го уровня
			// их не покрывал, и при открытой карте агенты продолжали рисоваться.
			void ToggleTree(Node n)
			{
				foreach (Node child in n.GetChildren())
				{
					if (child is CanvasItem c)
						c.Visible = visible;
					ToggleTree(child);
				}
			}
			ToggleTree(_agentRenderer);
		}

		// WorldAgentRenderer/прочие точечные рендереры агентов ищем по имени в дереве.
		// ВАЖНО: Node.FindNode("Name", recursive=false) ищет НЕ сам узел, а его
		// ДЕТЕЙ по имени (это аналог GetNode для вложенных путей). Старый вызов
		// FindNode("WorldAgentRenderer", true, false) возвращал null — рендерер
		// никогда не скрывался, и агенты стримингового мира рисовались под картой.
		Node worldAgents = FindChild("WorldAgentRenderer", true, false);
		if (worldAgents is CanvasItem ci)
			ci.Visible = visible;
	}

	public override void _ExitTree()
	{
		if (_mapRenderer != null)
			_mapRenderer.OnMapApplied -= OnMapApplied;

		// Очищаем открытую карту при выходе
		_mapOverlay?.QueueFree();
		_worldView?.Shutdown();
		_agentThread?.Stop();
		base._ExitTree();
	}
}
