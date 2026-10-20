using System;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.Core.WorldStreaming.Mutation;
using Game.Simulation;
using Game.UI;
using Game.UI.Streaming;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Game.Main;

public partial class Main : Node2D
{
	[Export] public ulong WorldSeed = 0;
	private const uint WorldGeneratorVersion = 3;
	private const long WorldRegionsX = 2048; // регионов по X (мировая карта §23)
	private const long WorldRegionsY = 1024; // регионов по Y
	private const long WorldRegionCells = 512; // клеток в регионе
	// §117.2: играбельная зона колонии вокруг точки спавна — окно GenerateColonyWindow
	// (раньше неявно 512×512 через MapRenderer.MapWidth/Height). ЕДИНАЯ точка отката:
	// 1024 → 512 возвращает колонию к прежнему размеру. Согласована с флипом
	// MapRenderer.MapWidth/Height 512→1024 (делает dev-terrain-window).
	private const int ColonySizeTiles = 1024;

	// §117/B5: мин-интервал (wall-clock, мс) между _worldView.Update — план чанков
	// не пересобирается чаще ~8 раз/с даже при быстром панорамировании (Shift).
	private const ulong StreamingUpdateMinIntervalMs = 120;

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
	// §118 фаза 3: реестр активных зон (поселение = зона 1024² в мировых координатах).
	private ActiveAreaRegistry _activeAreaRegistry;
	// §118 фаза 4: единый канал островных мутаций — запись (зеркало событий 6 доменов)
	// + островной стор дельт с персистентностью (у острова её раньше не было вовсе).
	private IslandOverrideStore _islandDeltas;
	private IslandMutationMirror _islandMirror;
	private string _islandZoneId;
	// §119 P7б (task-17): origin активной зоны (нужен state-replay/snapshot в мировых клетках).
	private long _islandOriginX;
	private long _islandOriginY;
	private float _syncTimer;
	private long _lastStreamerTileX = long.MinValue;
	private long _lastStreamerTileY = long.MinValue;
	// §117/B5: wall-clock (Time.GetTicksMsec) последнего _worldView.Update.
	private ulong _lastStreamerUpdateMsec;

	/// <summary>Точка выбора игрока на мировой карте. (-1,-1) = ожидает выбора.</summary>
	private RegionKey _spawnRegion = new(-1, -1);
	private bool _spawnPending = false;
	private int _activeGenerationId = 0;

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

/// <summary>§118 фаза 4: поднять единый канал островных мутаций (стор + зеркало). Идемпотентно.</summary>
	private void InitIslandMutationChannel()
	{
		if (_islandDeltas != null)
			return;
		_islandDeltas = new IslandOverrideStore();
		_islandMirror = new IslandMutationMirror(_islandDeltas);
		_islandMirror.Attach();
		GD.Print("[World] канал островных мутаций готов: IWorldMutationSink + зеркало 6 доменов");
	}

	/// <summary>Файл островных дельт зоны (остров впервые получает персистентность, аудит §5.4).</summary>
	private static string IslandDeltaPath(string zoneId)
		=> ProjectSettings.GlobalizePath($"user://colonies/{zoneId}.island.delta");

	/// <summary>
	/// §118 фаза 4: привязать стор к зоне/сиду и подгрузить дельты с диска.
	/// Ключ сида смешивается с версией генератора: смена формул автоматически
	/// инвалидирует старые дельты (они были рассчитаны на другую базу).
	/// </summary>
	private IslandOverrideStore PrepareIslandStore(string zoneId, ulong seed, uint generatorVersion)
	{
		InitIslandMutationChannel();
		_islandZoneId = zoneId;

		uint seedKey = unchecked((uint)(seed ^ (seed >> 32)) ^ (generatorVersion * 0x9E3779B9u));
		_islandDeltas.RebindSeed(seedKey);
		// Зеркало обязано знать origin зоны: менеджеры дают ЛОКАЛЬНЫЕ координаты
		// острова (0..1023), а стор хранит МИРОВЫЕ клетки.
		_islandMirror?.SetZoneOrigin(UnifiedWorldGenerator.WorldOriginX, UnifiedWorldGenerator.WorldOriginY);
		// §119 P7б: origin запоминаем для state-replay/snapshot (мировые клетки).
		_islandOriginX = UnifiedWorldGenerator.WorldOriginX;
		_islandOriginY = UnifiedWorldGenerator.WorldOriginY;
		string path = IslandDeltaPath(zoneId);
		try
		{
			if (!_islandDeltas.Load(path))
				return _islandDeltas;
			if (_islandDeltas.Seed != seedKey)
			{
				// Файл от другого мира/версии формул: база другая, дельты не применимы.
				_islandDeltas.RebindSeed(seedKey);
				GD.Print($"[World] {zoneId}: дельты другого мира проигнорированы");
			}
			else if (_islandDeltas.Count > 0)
			{
				GD.Print($"[World] {zoneId}: загружено {_islandDeltas.Count} островных дельт");
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[World] {zoneId}: островные дельты не загружены — {ex.Message}");
		}
		// §119 P7б (task-17 п.3): зоны колонии (.zones.json) — НЕ зависят от версии
		// генератора (чистый seed). Файл другого сида → файл и зоны менеджера чистятся.
		try
		{
			if (ZonePersistence.TryLoadZones(zoneId, seed, out string zonesError) || zonesError == null)
			{
				int restored = 0;
				foreach (Zone z in ZoneManager.Instance.GetAllZones())
					if (z.Kind == ZoneKind.Work) restored++;
				GD.Print($"[World] {zoneId}: зоны восстановлены ({restored} Work-зон)");
			}
			else if (zonesError != null)
			{
				GD.PrintErr($"[World] {zoneId}: .zones.json не загружен — {zonesError}");
			}
			else
			{
				GD.Print($"[World] {zoneId}: файл зон отсутствует (первая сессия колонии)");
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[World] {zoneId}: .zones.json не загружен — {ex.Message}");
		}
		return _islandDeltas;
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
		// Старт — центр играбельной зоны колонии (клетки [0,ColonySizeTiles)², §117.2).
		_camera.Position = new Vector2(
			ColonySizeTiles * MapRenderer.TileSizePx / 2f,
			ColonySizeTiles * MapRenderer.TileSizePx / 2f);

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
			UnifiedWorldGenerator.ActiveWorldSeed = seed;
			string baseCacheDir = ProjectSettings.GlobalizePath("user://infinite_world_lab_cache");
			string worldSaveDir = System.IO.Path.Combine(baseCacheDir, $"world_{seed:x16}");
			var worldCoordinator = new WorldSaveCoordinator(
				worldSaveDir, seed, WorldGeneratorVersion, UnifiedWorldGenerator.FeatureSchemaVersion);
			_worldManager = new StreamingWorldManager(_worldView, worldCoordinator);
			_worldManager.Open(0, 0);
			_worldRules = new WorldGameRules(_worldView, seed, WorldGeneratorVersion);
			_worldPlacement = new StreamingToolPlacement(_worldManager, _worldRules, ColonySizeTiles, ColonySizeTiles);

			if (HUD != null)
				HUD.WorldPlacement = _worldPlacement;
			else if (HUDController.Instance != null)
				HUDController.Instance.WorldPlacement = _worldPlacement;

			// §113.5/B2: SelectTool больше не удаляет по ПКМ — WorldPlacement
			// дефолтному инструменту не нужен.
			// §118 фаза 5: мир-агенты снесены — работники острова это колонисты.
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
		try
		{
			WorldSeed = newSeed;
			UnifiedWorldGenerator.ActiveWorldSeed = newSeed;
			if (_worldView != null)
				_worldView.WorldSeed = newSeed;

			GD.Print($"[MAIN] Синхронизирован новый сид мира: 0x{WorldSeed:X16} (Архетип: {Game.Core.WorldClimate.WorldMacroClimateParams.GetArchetype(WorldSeed)})");

			// Если игрок еще на этапе первичного выбора спавна, не тратим ресурсы на фоновую генерацию колонии
			if (_mapOverlay != null && _mapOverlay.IsSpawnSelectionMode)
				return;

			RegionKey spawnRegion = UnifiedWorldGenerator.FindHabitableSpawn(WorldSeed, WorldGeneratorVersion);
			UnifiedWorldGenerator.SetWorldOrigin(spawnRegion.X * WorldRegionCells, spawnRegion.Y * WorldRegionCells);
			UpdateCameraWorldBounds();

			int genId = Interlocked.Increment(ref _activeGenerationId);
			// §118 фаза 3: генерация через ActiveArea (единая зона 1024² в памяти,
			// единственный источник формул — WorldGenV2) + мост в MapData: 23
			// потребителя не меняются, начинка идентична окну (TestActiveArea parity).
			// §118 фаза 4: после Fill применяются островные дельты (база + дельта),
			// иначе срубленное/добытое не переживало бы перезапуск.
			string zoneId = $"colony_{spawnRegion.X}_{spawnRegion.Y}";
			IslandOverrideStore islandStore = PrepareIslandStore(zoneId, WorldSeed, WorldGeneratorVersion);
			var mapData = await Task.Run(() =>
			{
				var registry = _activeAreaRegistry ??= new ActiveAreaRegistry();
				registry.RegisterZone(zoneId,
					UnifiedWorldGenerator.WorldOriginX, UnifiedWorldGenerator.WorldOriginY);
				registry.TryGetZone(zoneId, out ActiveArea area);
				area.Fill(WorldSeed, WorldGeneratorVersion);
				IslandDeltaApplyResult deltaResult = IslandDeltaApplicator.Apply(area, islandStore);
				GD.Print($"[World] {zoneId}: островные дельты — {deltaResult}");
				registry.SetActiveZone(zoneId);
				return MapDataShim.ToMapData(area);
			});

			if (genId != Volatile.Read(ref _activeGenerationId))
				return;

			if (_agentThread != null)
			{
				if (!_agentThread.Stop(3000))
					GD.PrintErr("[MAIN] Предыдущий AgentSimulationThread не остановился в пределах таймаута!");
				_agentThread = null;
			}

			ResetColonyState();

			// §119 P7б (task-17 п.4): СОСТОЯНИЕ колонии после очистки менеджеров —
			// идемпотентный домен-реплей + разворачивание payload'ов (стадия культур,
			// содержимое склада, доставка стройки, прогресс монтажа/рубки, кучи на земле).
			// Порядок валиден только здесь: ResetColonyState затирал реплей из Task.Run.
			IslandStateReplay.Replay(islandStore, _islandOriginX, _islandOriginY);

			_mapRenderer.ExternalPendingMap = mapData;
			_mapRenderer.StartAsync();

			if (_camera != null)
			{
				// Центр играбельной зоны ColonySizeTiles² (§117.2); зум размером не обусловлен.
				_camera.Position = new Vector2(
					ColonySizeTiles * MapRenderer.TileSizePx / 2f,
					ColonySizeTiles * MapRenderer.TileSizePx / 2f);
				_camera.Zoom = new Vector2(0.5f, 0.5f);
			}

			if (_mapOverlay != null)
				_mapOverlay.CurrentColonyRegion = spawnRegion;

			_lastStreamerTileX = long.MinValue;
			_lastStreamerTileY = long.MinValue;

			_worldView?.ResetWorld(newSeed);
			InitStreamingWorld(newSeed);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[MAIN] Ошибка при регенерации сида мира 0x{newSeed:X16}: {ex.Message}\n{ex.StackTrace}");
		}
	}

	/// <summary>Игрок выбрал регион на мировой карте — генерируем карту и начинаем игру.</summary>
	private async void OnRegionSelected(RegionKey region)
	{
		try
		{
			GD.Print($"[SPAWN] Выбран регион [{region.X}, {region.Y}] → генерация карты...");

			ulong seed = WorldSeed;
			uint version = WorldGeneratorVersion;
			UnifiedWorldGenerator.ActiveWorldSeed = seed;
			UnifiedWorldGenerator.SetWorldOrigin(region.X * WorldRegionCells, region.Y * WorldRegionCells);
			UpdateCameraWorldBounds();

			int genId = Interlocked.Increment(ref _activeGenerationId);
			// §118 фаза 3: генерация через ActiveArea (единая зона 1024² в памяти,
			// единственный источник формул — WorldGenV2) + мост в MapData: 23
			// потребителя не меняются, начинка идентична окну (TestActiveArea parity).
			// §118 фаза 4: после Fill применяются островные дельты (база + дельта).
			string zoneId = $"colony_{region.X}_{region.Y}";
			IslandOverrideStore islandStore = PrepareIslandStore(zoneId, seed, version);
			var mapData = await Task.Run(() =>
			{
				var registry = _activeAreaRegistry ??= new ActiveAreaRegistry();
				registry.RegisterZone(zoneId,
					UnifiedWorldGenerator.WorldOriginX, UnifiedWorldGenerator.WorldOriginY);
				registry.TryGetZone(zoneId, out ActiveArea area);
				area.Fill(seed, version);
				IslandDeltaApplyResult deltaResult = IslandDeltaApplicator.Apply(area, islandStore);
				GD.Print($"[World] {zoneId}: островные дельты — {deltaResult}");
				registry.SetActiveZone(zoneId);
				return MapDataShim.ToMapData(area);
			});

			if (genId != Volatile.Read(ref _activeGenerationId))
				return;

			if (_agentThread != null)
			{
				if (!_agentThread.Stop(3000))
					GD.PrintErr("[MAIN] Предыдущий AgentSimulationThread не остановился в пределах таймаута!");
				_agentThread = null;
			}

			ResetColonyState();

			// §119 P7б (task-17 п.4): СОСТОЯНИЕ колонии после очистки менеджеров
			// (идемпотентный домен-реплей + payload'ы) — до старта сима.
			IslandStateReplay.Replay(islandStore, _islandOriginX, _islandOriginY);

			_mapRenderer.ExternalPendingMap = mapData;
			_mapRenderer.StartAsync();

			if (_camera != null)
			{
				// Центр играбельной зоны ColonySizeTiles² (§117.2); зум размером не обусловлен.
				_camera.Position = new Vector2(
					ColonySizeTiles * MapRenderer.TileSizePx / 2f,
					ColonySizeTiles * MapRenderer.TileSizePx / 2f);
				_camera.Zoom = new Vector2(0.5f, 0.5f);
			}

			if (_mapOverlay != null)
				_mapOverlay.CurrentColonyRegion = region;

			_lastStreamerTileX = long.MinValue;
			_lastStreamerTileY = long.MinValue;

			_worldView?.ResetWorld(seed);
			InitStreamingWorld(seed);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[SPAWN] Ошибка при генерации выбранного региона [{region.X}, {region.Y}]: {ex.Message}\n{ex.StackTrace}");
		}
	}

	private void OnMapApplied()
	{
		// Мост A (PLAN.md §112.2): инъекция MapData в симуляцию —
		// GroundItemManager.TrySpillItemsAdjacent больше не зависит от Game.UI.
		GroundItemManager.Instance.SetMapData(_mapRenderer.MapData);

		_interactionManager.Initialize(_mapRenderer.WallLayer, _selection, _camera);

		if (_agentThread == null && _mapRenderer.MapData != null)
		{
			_agentThread = new AgentSimulationThread();
			_agentThread.Start(AgentCount, _mapRenderer.MapData.Ground, _mapRenderer.MapData.TreeOnGrass, 0, _mapRenderer.MapData.Humidity, _mapRenderer.MapData.StoneOnGrass, _mapRenderer.MapData.Fertility);

			if (_timeManager != null)
			{
				_agentThread.IsPaused = _timeManager.IsPaused;
				_agentThread.SpeedMultiplier = _timeManager.SpeedMultiplier;
			}

			// Стартовый спавн 100 зёрен: центр РЕАЛЬНО применённой карты (§117.2),
			// со спиральным поиском суши — центр колонии 1024² может попасть в озеро/реку.
			SpawnStarterGrain(_mapRenderer.MapData);

			if (_agentRenderer == null)
			{
				_agentRenderer = new AgentRenderer { Name = "AgentRenderer" };
				AddChild(_agentRenderer);
			}
			_agentRenderer.Initialize(_agentThread, AgentCount);
		}

		HUDController hud = HUD
							?? FindChild("hud_tscn", true, false) as HUDController
							?? FindChild("HUDController", true, false) as HUDController
							?? HUDController.Instance;

		if (hud != null)
		{
			hud.Setup(_interactionManager, _mapRenderer, AgentCount);
			// §28: инструменты получают доступ к миру за кромкой острова.
			if (_worldManager != null && _worldRules != null)
			{
				_worldPlacement ??= new StreamingToolPlacement(_worldManager, _worldRules, ColonySizeTiles, ColonySizeTiles);
				hud.WorldPlacement = _worldPlacement;
				// §113.5/B2: SelectTool больше не удаляет по ПКМ — инъекция не нужна.
				GD.Print("[MAIN] §28: мир за кромкой подключён к инструментам (стены/склад/ферма/камень/лес)");
			}
		}
	}

	/// <summary>
	/// §117.2 (задача 5): стартовые 100 зёрен — от центра карты, но строго на
	/// проходимой суше (Grass без дерева/камня — туда дойдёт агент). В колонии
	/// 1024×1024 центр легко попадает в озеро или реку. Кольцевой поиск от центра,
	/// радиус 256 тайлов; размеры берём из реально применённого MapData.
	/// </summary>
	private static void SpawnStarterGrain(MapData map)
	{
		if (map?.Ground == null) return;
		int cx = map.Width / 2;
		int cy = map.Height / 2;
		const int MaxRadius = 256;
		for (int r = 0; r <= MaxRadius; r++)
		{
			for (int oy = -r; oy <= r; oy++)
			{
				for (int ox = -r; ox <= r; ox++)
				{
					// Только периметр кольца r — без повторного обхода внутренности.
					if (Math.Max(Math.Abs(ox), Math.Abs(oy)) != r) continue;
					int x = cx + ox;
					int y = cy + oy;
					if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) continue;
					if (map.Ground[x, y] == TileType.Grass
						&& !map.TreeOnGrass[x, y]
						&& !map.StoneOnGrass[x, y])
					{
						GroundItemManager.Instance.SpawnItems(x, y, ItemId.Grain, 100);
						return;
					}
				}
			}
		}
		// Вырожденный мир (суши возле центра нет): старое поведение — центр как есть.
		GD.PrintErr("[MAIN] SpawnStarterGrain: проходимая суша возле центра не найдена — спавн зерна в центр без проверки.");
		GroundItemManager.Instance.SpawnItems(cx, cy, ItemId.Grain, 100);
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
		// §117/B5: двойной гейт — сдвиг 8+ тайлов И min-интервал 120 мс wall-clock
		// (Time.GetTicksMsec — реальные часы, не масштабируемое игровое время):
		// при быстром панорамировании (Shift ~137 тайл/с) план чанков не дёргается
		// чаще ~8 раз/с (раньше — каждые 0.06 с, F5).
		if (_worldView != null && _camera != null)
		{
			long camTileX = (long)(_camera.Position.X / MapRenderer.TileSizePx);
			long camTileY = (long)(_camera.Position.Y / MapRenderer.TileSizePx);
			ulong nowMsec = Time.GetTicksMsec();
			if ((Math.Abs(camTileX - _lastStreamerTileX) >= 8 || Math.Abs(camTileY - _lastStreamerTileY) >= 8)
				&& nowMsec - _lastStreamerUpdateMsec >= StreamingUpdateMinIntervalMs)
			{
				_lastStreamerUpdateMsec = nowMsec;
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

	private static void ResetColonyState()
	{
		BlueprintManager.Instance.ClearAll();
		BuildingManager.Instance.ClearAll();
		ConstructionManager.Instance.ClearAll();
		StructureManager.Instance.ClearAll();
		StockpileManager.Instance.ClearAll();
		FarmJobManager.Instance.ClearAll();
		TreeJobManager.Instance.ClearAll();
		StoneJobManager.Instance.ClearAll();
		GroundItemManager.Instance.ClearAll();
		CropGrowthManager.Instance.ClearAll();
		ZoneManager.Instance.ClearAll();
		JobDispatcher.Instance.JobIndex.Clear();
		JobDispatcher.Instance.IdleWorkers.Clear();
		WorkProgressTracker.Instance.ClearAll();
		DemandRegistry.Instance.ClearAll();
		HierarchicalPathfinder.Instance.Reset();
		FlowFieldManager.Instance.ClearCache();
		MapRenderer.ClearTileSetCaches();
		Game.Core.WorldClimate.WorldClimateProvider.ClearCache();
		JobPriorityManager.Instance.Reset();
		GameProfiler.Reset();
		SimEvents.Reset();
		ProfilerLogService.Instance.Reset();
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
		// §118 фаза 2 (аудит WorldDeltaApi §6.4/§6.5): ДО остановки стримера —
		// чекпоинт мирового времени в манифест и принудительный flush дельт
		// resident-чанков (включая desired), иначе правки текущего окна теряются.
		float worldTimeOnExit = _timeManager?.GameTimeSeconds ?? 0f;
		try
		{
			_worldManager?.Checkpoint(worldTimeOnExit);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[World] exit checkpoint failed: {ex.Message}");
		}
		try
		{
			_worldView?.FlushResidentDeltas(worldTimeOnExit);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[World] exit delta flush failed: {ex.Message}");
		}
		// §118 фаза 4: островные дельты (деревья/камни/постройки/фермы/склады)
		// сохраняются на выходе — у острова до этого не было персистентности вовсе.
		if (_islandDeltas != null && !string.IsNullOrEmpty(_islandZoneId))
		{
			// §119 P7б (task-17 п.4): ПЕРЕД записью — payload'ы актуального состояния
			// (стадии культур, содержимое склада, доставка стройки, прогресс монтажа
			// и рубки, кучи предметов на земле).
			try
			{
				IslandStateSnapshot.RefreshPayloads(_islandDeltas, _islandOriginX, _islandOriginY);
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[World] exit state snapshot failed: {ex.Message}");
			}
			try
			{
				if (_islandDeltas.TrySave(IslandDeltaPath(_islandZoneId), out string saveError))
				{
					GD.Print($"[World] островные дельты сохранены: {_islandDeltas.Count} ({_islandZoneId})");
				}
				else
				{
					GD.PrintErr($"[World] островные дельты НЕ сохранены: {saveError}");
				}
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[World] exit island delta save failed: {ex.Message}");
			}
			// §119 P7б (task-17 п.3): .zones.json — Work-зоны колонии переживают выход.
			try
			{
				if (ZonePersistence.SaveZones(_islandZoneId, WorldSeed, out string zonesError))
					GD.Print($"[World] зоны сохранены: {_islandZoneId}.zones.json");
				else
					GD.PrintErr($"[World] зоны НЕ сохранены: {zonesError}");
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[World] exit zones save failed: {ex.Message}");
			}
		}
		_islandMirror?.Detach();
		_worldView?.Shutdown();
		_agentThread?.Stop();
		base._ExitTree();
	}
}
