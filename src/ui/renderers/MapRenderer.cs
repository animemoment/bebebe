using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.Simulation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Game.UI;

/// <summary>Режим просмотра карты (оверлеи поверх основных тайлов).</summary>
public enum MapMode
{
	Normal,
	Humidity,
	Fertility
}

public partial class MapRenderer : Node2D
{
	public static MapRenderer Instance { get; private set; }

	/// <summary>
	/// Размер играбельного острова колонии в тайлах (§117.2: 512→1024).
	/// Main генерирует окно колонии ColonySizeTiles=1024 и передаёт размер явно;
	/// эти константы обязаны совпадать с ним — иначе ApplyMap отрисует только
	/// квадрант карты, а skip-проверки стриминга (PaintCell/IsOccludedByColony)
	/// будут считать чужую кромку. Точка отката: вернуть 512.
	/// </summary>
	public const int MapWidth = 1024;
	public const int MapHeight = 1024;
	public const int TileSizePx = 64;

	/// <summary>
	/// Размер follow-окна террейна в тайлах (ColorRect _groundSurface + R8-текстура
	/// terrain_map). ДЕКУПЛИРОВАН от MapWidth/MapHeight (§117.2): когда остров колонии
	/// вырастет 512→1024, follow-окно остаётся 512×512 и продолжает следить за камерой —
	/// его размер обусловлен зоной видимости камеры, а не размером острова.
	/// </summary>
	public const int TerrainWindowTiles = 512;

	/// <summary>Квант сдвига follow-окна в тайлах: 1 чанк стриминга = 64 клетки.</summary>
	private const int TerrainWindowShift = 64;

	/// <summary>Шаг сетки макро-климата для внешних клеток (как GenerateChunk / GenerateColonyWindow).</summary>
	private const int TerrainClimateStep = 16;

	public const int SourceGrass     = 0;
	public const int SourceWater     = 1;
	public const int SourceTree0     = 2;
	public const int SourceWall      = 3;
	public const int SourceTree1     = 4;
	public const int SourceWorkTable = 5;
	public const int SourceGardenBed = 6;
	public const int SourceTree2     = 7;
	public const int SourceTree3     = 8;
	public const int SourceMountain  = 9;
	// Камень-россыпь: 4 отдельных Source (по одному квадранту 64x64 из
	// stone.png 128x128) — как 4 варианта деревьев (SourceTree0..3).
	public const int SourceStone0    = 10;
	public const int SourceStone1    = 11;
	public const int SourceStone2    = 12;
	public const int SourceStone3    = 13;
	// Мебель: одиночные спрайты 64x64 (как WorkTable).
	public const int SourceBed        = 14;
	public const int SourceBench      = 15;
	public const int SourceNightstand = 16;
	public const int SourceSand       = 17;
	// §119 P5: центральный блок (central.png) — одиночный спрайт 64x64.
	public const int SourceCentralBlock = 18;
	// Композитный объединенный атлас декораций (деревья + камни) для батчинга в 1 DrawCall
	public const int SourceObjectComposite = 20;
	// Ghost-призрак: TileSet _ghostLayer — ДУБЛЬ сценового wall-набора с
	// добавленными одиночными Source (трава/грядка/мебель). Id взяты с запасом
	// от сценовых (там максимум ~16), чтобы не пересекаться с terrain-источниками.
	// Зону рисуем ТОЛЬКО этими константами (НЕ SourceGrass/SourceGardenBed —
	// их номера в ghost-наборе заняты стенами, иначе призрак = стены).
	public const int GhostGrassSource      = 100;
	public const int GhostGardenBedSource  = 101;
	public const int GhostWorkTableSource  = 105;
	public const int GhostStockpileSource  = 106;
	public const int GhostBedSource        = 114;
	public const int GhostBenchSource      = 115;
	public const int GhostNightstandSource = 116;
	// §119 P5: ghost центрального блока.
	public const int GhostCentralBlockSource = 117;

	/// <summary>
	/// Сид карты. 0 = случайный каждый запуск, иначе фиксированный (для дебага/повторов).
	/// Задаётся в инспекторе Godot у MapRenderer.
	/// </summary>
	[Export] public int SeedOverride = 0;

	/// <summary>Отложить автоматическую генерацию карты (например, в режиме выбора спавна из меню).</summary>
	[Export] public bool DeferAutoGeneration { get; set; } = false;

	public static uint CurrentMapSeed { get; private set; }

	/// <summary>Внешняя карта (выбор спавна игрока на мировой карте). Применяется вместо автогенерации.</summary>
	public MapData ExternalPendingMap 
	{ 
		set 
		{ 
			_hasExternalMap = true;
			Interlocked.Exchange(ref _pendingMapData, value); 
		} 
	}
	
	/// <summary>Применить внешнюю карту синхронно (вызывается из Main.cs после установки ExternalPendingMap).</summary>
	public void StartAsync()
	{
		if (_pendingMapData != null)
			ApplyMap();
	}
	
	private volatile bool _hasExternalMap = false;

	private const string TextureGrass     = "uid://bw85uoku784o5";
	private const string TextureWater     = "uid://cmi8pjecdx35";
	private const string TextureTree0     = "uid://c70p6ktr0vcx6";
	private const string TextureTree1     = "uid://ddkl165hm35rp";
	private const string TextureTree2     = "uid://do5qth6ieq2q4";
	private const string TextureTree3     = "uid://n3xkro0lffbt";
	private const string TextureWoodWall  = "uid://chubmh2ufwgwp";
	private const string TextureWorkTable = "uid://rp2bpb5c7k2y";
	private const string TextureGardenBed = "uid://47god8qachvh"; // <-- Исправлен правильный UID грядки
	private const string TextureMountain = "uid://de7mit41ukuoq";
	private const string TextureProcessOfWork = "uid://qqhtvxqngv4a";
	private const string TextureStone = "uid://du6ur8mvtu3le";
	private const string TextureBed = "uid://0uf8ok6dxhyi";
	private const string TextureBench = "uid://bi667xckuklur";
	private const string TextureNightstand = "uid://ir5jk74j01fg";
	// §119 P5: спрайт центрального блока (uid из спеки, res://ui/assets/textures/objects/central.png).
	private const string TextureCentralBlock = "uid://dsiddo1lt1scb";
	private const string TextureSand = "uid://cmxrvsia7sdqi";

	private const int WallAtlasColumns = 7;
	private const int WallAtlasRows = 7;
	private const int WallAtlasTilePx = 64;

	private static readonly Vector2I AtlasOrigin = Vector2I.Zero;

	private const string TerrainBlendShaderPath = "res://src/shaders/terrain_blend.gdshader";
	private ShaderMaterial _terrainMaterial;
	private ImageTexture _terrainTypeTexture;
	private ColorRect _groundSurface;
	private float _waterAccumulatedTime;
	private long _currentTerrainOriginX = long.MinValue;
	private long _currentTerrainOriginY = long.MinValue;
	private bool _terrainTextureNeedsUpdate = true;
	private byte[] _terrainBuffer;

	private TileMapLayer _groundLayer;
	private TileMapLayer _mountainLayer;
	private TileMapLayer _farmLayer;
	private TileMapLayer _farmBlueprintLayer;
	private TerrainTileLayer _farmTerrain;
	private TerrainTileLayer _farmBlueprintTerrain;
	private HumidityOverlayRenderer _humidityOverlay;
	private FertilityOverlayRenderer _fertilityOverlay;
	private TileMapLayer _stockpileLayer;
	private TileMapLayer _objectLayer;
	private TileMapLayer _wallLayer;
	private TileMapLayer _buildingLayer;
	private TileMapLayer _ghostLayer;
	private TileMapLayer _blueprintLayer;

	private DesignationRenderer _designationRenderer;

	private MapData _pendingMapData;
	private WallBuildManager _wallBuildManager;

	private readonly ConcurrentQueue<Vector2I> _choppedTreesQueue = new();

	// П.2: батчинг SetCell/EraseCell. События менеджеров только складывают клетки в очередь,
	// реальный TileMap трогаем раз в кадр ограниченным бюджетом — иначе 1000 завершений
	// за тик = 1000 дорогих SetCell + микрофриз главного потока.
	private readonly struct PendingCell
	{
		public readonly TileMapLayer Layer;
		public readonly Vector2I Pos;
		public readonly int Source;
		public readonly Vector2I Atlas;
		public readonly bool Erase;
		public PendingCell(TileMapLayer layer, Vector2I pos, int source, Vector2I atlas, bool erase)
		{
			Layer = layer; Pos = pos; Source = source; Atlas = atlas; Erase = erase;
		}
	}
	private readonly ConcurrentQueue<PendingCell> _pendingCells = new();
	private const int MaxCellsPerFrame = 2048;
	private bool _blueprintRefreshQueued;
	private long _lastBlueprintRefreshMs;
	// Стандарт слоёв: движковый автотайлинг через TerrainTileLayer
	// (SetCellsTerrainConnect нельзя звать из фоновых потоков — копим грязные
	// клетки и пересчитываем одним вызовом на слой в _Process).
	private TileMapLayer _wallBlueprintLayer;
	private TerrainTileLayer _builtWalls;
	private TerrainTileLayer _wallBlueprints;
	private TerrainTileLayer _mountains;
	private const int WallTerrainSetId = 0;
	private const int WallTerrainId = 0;
	private const int MountainTerrainSetId = 0;
	private const int MountainTerrainId = 0;

	// Прогресс ломки/стройки: отдельный слой поверх всего (атлас ProcessOfWork
	// 3×2 = 6 стадий). Источник данных — WorkProgressTracker (хендлеры пишут
	// стадии, здесь DrainDirty в _Process). Сцену не трогаем — TileSet строим
	// кодом из uid://qqhtvxqngv4a.
	public const int SourceWorkProgress = 200;
	private TileMapLayer _workProgressLayer;
	private readonly List<(int X, int Y, int Stage)> _progressDrain = new(256);
	private readonly List<(int X, int Y)> _progressStale = new(128);
	private float _progressSweepTimer;

	private Action<(int X, int Y)> _onZoneTileAdded;
	private Action<(int X, int Y)> _onZoneTileRemoved;
	private Action<(int X, int Y)[]> _onZoneTilesBatchAdded;
	private Action<(int X, int Y)[]> _onZoneTilesBatchRemoved;

	private Action<(int X, int Y), BuildingType> _onBuildingPlaced;
	private Action<(int X, int Y)> _onBuildingRemoved;

	private Action<(int X, int Y)> _onPlotMarked;
	private Action<List<(int X, int Y)>> _onPlotsBatchMarked;
	private Action<(int X, int Y)> _onPlotUnmarked;
	private Action<List<(int X, int Y)>> _onPlotsBatchUnmarked;
	private Action<(int X, int Y)> _onPlotCompleted;

	private MapData _currentMapData;
	public MapData MapData => _currentMapData ?? _pendingMapData;
	public HumidityMap Humidity => (_currentMapData ?? _pendingMapData)?.Humidity;
	public FertilityMap Fertility => (_currentMapData ?? _pendingMapData)?.Fertility;
	public MapMode CurrentMapMode { get; private set; } = MapMode.Normal;
	public event Action<MapMode> OnMapModeChanged;
	public WallBuildManager WallBuildManager => _wallBuildManager;
	public TileMapLayer GhostLayer => _ghostLayer;
	public TileMapLayer WallLayer => _wallLayer;
	public ShadowCasterRenderer ShadowRenderer { get; private set; }
	public StockpileItemRenderer StockpileItems { get; private set; }

	public event Action OnMapApplied;

	public override void _Ready()
	{
		Instance = this;

		_groundSurface = new ColorRect
		{
			Name = "GroundSurface",
			Color = new Color(0f, 0f, 0f),
			// Размер = follow-окно террейна (НЕ остров): TerrainWindowTiles, чтобы
			// рост MapWidth 512→1024 (§117.2) не растягивал камера-следящий ColorRect.
			Size = new Vector2(TerrainWindowTiles * TileSizePx, TerrainWindowTiles * TileSizePx),
			Position = Vector2.Zero,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		AddChild(_groundSurface);

		_groundLayer = new TileMapLayer { Name = "GroundLayer", RenderingQuadrantSize = 128 };
		Shader terrainShader = null;
		try
		{
			terrainShader = GD.Load<Shader>(TerrainBlendShaderPath);
		}
		catch (Exception)
		{
			// Fallback на стандартный рендер плиток без шейдера
		}
		if (terrainShader != null)
		{
			_terrainMaterial = new ShaderMaterial { Shader = terrainShader };
			var grassTex = LoadTexture(TextureGrass, "grass");
			var sandTex = LoadTexture(TextureSand, "sand");
			var waterTex = LoadTexture(TextureWater, "water");
			if (grassTex != null) _terrainMaterial.SetShaderParameter("tex_grass", grassTex);
			if (sandTex != null) _terrainMaterial.SetShaderParameter("tex_sand", sandTex);
			if (waterTex != null) _terrainMaterial.SetShaderParameter("tex_water", waterTex);
			_terrainMaterial.SetShaderParameter("map_size", new Vector2(TerrainWindowTiles, TerrainWindowTiles));
			_groundSurface.Material = _terrainMaterial;
			_groundSurface.Visible = true;
			_groundLayer.Visible = false;
		}
		AddChild(_groundLayer);

		_mountainLayer = new TileMapLayer { Name = "MountainLayer", RenderingQuadrantSize = 128 };
		AddChild(_mountainLayer);

		_farmLayer = new TileMapLayer { Name = "FarmLayer", RenderingQuadrantSize = 128 };
		AddChild(_farmLayer);

		_farmBlueprintLayer = new TileMapLayer
		{
			Name = "FarmBlueprintLayer",
			Modulate = new Color(1f, 1f, 1f, 0.5f),
			RenderingQuadrantSize = 128
		};
		AddChild(_farmBlueprintLayer);

		// Оверлей влажности: один TextureRect 512×512 поверх земли,
		// текстуру готовит HumidityOverlayRenderer. Скрыт по умолчанию.
		_humidityOverlay = new HumidityOverlayRenderer { Name = "HumidityOverlay" };
		AddChild(_humidityOverlay);
		_humidityOverlay.Visible = false;

		// Оверлей плодородия: тот же приём (один TextureRect поверх земли).
		// Режимы взаимоисключающие — виден только один слой за раз.
		_fertilityOverlay = new FertilityOverlayRenderer { Name = "FertilityOverlay" };
		AddChild(_fertilityOverlay);
		_fertilityOverlay.Visible = false;

		_stockpileLayer = new TileMapLayer
		{
			Name = "StockpileLayer",
			Modulate = new Color(0.65f, 0.15f, 0.9f, 0.45f),
			RenderingQuadrantSize = 128
		};
		AddChild(_stockpileLayer);

		_objectLayer = new TileMapLayer { Name = "ObjectLayer", RenderingQuadrantSize = 128 };
		AddChild(_objectLayer);

		_buildingLayer = new TileMapLayer { Name = "BuildingLayer", RenderingQuadrantSize = 128 };
		AddChild(_buildingLayer);

		_blueprintLayer = new TileMapLayer
		{
			Name = "BlueprintLayer",
			Modulate = new Color(1f, 1f, 1f, 0.5f),
			RenderingQuadrantSize = 128
		};
		AddChild(_blueprintLayer);

		// Чертежи стен: отдельный полупрозрачный terrain-слой на том же
		// сценовом наборе — движок сам считает состыковку чертёж↔чертёж
		// через SetCellsTerrainConnect. Мебель/грядки остаются в _blueprintLayer.
		_wallBlueprintLayer = new TileMapLayer
		{
			Name = "WallBlueprintLayer",
			Modulate = new Color(1f, 1f, 1f, 0.5f),
			RenderingQuadrantSize = 128
		};
		AddChild(_wallBlueprintLayer);

		_wallLayer = new TileMapLayer { Name = "WoodWallLayer", RenderingQuadrantSize = 128 };
		AddChild(_wallLayer);

		_ghostLayer = new TileMapLayer
		{
			Name = "WallGhostLayer",
			Modulate = new Color(1f, 1f, 1f, 0.4f),
			RenderingQuadrantSize = 128
		};
		AddChild(_ghostLayer);

		_designationRenderer = new DesignationRenderer { Name = "DesignationRenderer" };
		AddChild(_designationRenderer);

		// Слой прогресса работ (ProcessOfWork 3×2): поверх стен/чертежей,
		// полупрозрачный. TileSet — кодовый (сцену не трогаем).
		_workProgressLayer = new TileMapLayer
		{
			Name = "WorkProgressLayer",
			Modulate = new Color(1f, 1f, 1f, 0.85f),
			RenderingQuadrantSize = 128
		};
		AddChild(_workProgressLayer);

		ShadowRenderer = new ShadowCasterRenderer { Name = "ShadowCasterRenderer" };
		AddChild(ShadowRenderer);

		// Иконки содержимого склада (StockpileManager.SnapshotQueue).
		// Canonical-точка для предметных рендереров — scenes/main/Main.cs (_Ready, рядом
		// с GroundItemRenderer), но scenes/ по правилу src-only не трогаем — поэтому
		// StockpileItemRenderer добавляется здесь, ребёнком MapRenderer (ZIndex 6 внутри
		// всё равно выше StockpileLayer с Z по умолчанию).
		var stockpileItemRenderer = new StockpileItemRenderer { Name = "StockpileItemRenderer" };
		AddChild(stockpileItemRenderer);
		StockpileItems = stockpileItemRenderer;
		TreeJobManager.Instance.OnTreeChopped += OnTreeChopped;
		// Подписка строго один раз: ApplyMap() вызывается при каждой загрузке карты,
		// и подписка внутри него накапливала N обработчиков (каждый добытый камень
		// обрабатывался N раз) и удерживала освобождённый MapRenderer в invocation list.
		StoneJobManager.Instance.OnStoneMined += OnStoneMined;

		_onZoneTileAdded = pos => _pendingCells.Enqueue(new PendingCell(_stockpileLayer, new Vector2I(pos.X, pos.Y), SourceGrass, AtlasOrigin, false));
		_onZoneTileRemoved = pos => _pendingCells.Enqueue(new PendingCell(_stockpileLayer, new Vector2I(pos.X, pos.Y), 0, AtlasOrigin, true));
		_onZoneTilesBatchAdded = arr => { foreach (var pos in arr) _pendingCells.Enqueue(new PendingCell(_stockpileLayer, new Vector2I(pos.X, pos.Y), SourceGrass, AtlasOrigin, false)); };
		_onZoneTilesBatchRemoved = arr => { foreach (var pos in arr) _pendingCells.Enqueue(new PendingCell(_stockpileLayer, new Vector2I(pos.X, pos.Y), 0, AtlasOrigin, true)); };
		StockpileManager.Instance.OnZoneTileAdded += _onZoneTileAdded;
		StockpileManager.Instance.OnZoneTileRemoved += _onZoneTileRemoved;
		StockpileManager.Instance.OnZoneTilesBatchAdded += _onZoneTilesBatchAdded;
		StockpileManager.Instance.OnZoneTilesBatchRemoved += _onZoneTilesBatchRemoved;

		ConstructionManager.Instance.OnSitesBatchAdded += sites =>
		{
			foreach (var s in sites)
			{
				if (s.Definition.IsWall)
					_wallBlueprints?.MarkDirty(new Vector2I(s.X, s.Y));
				else
					_pendingCells.Enqueue(new PendingCell(_blueprintLayer, new Vector2I(s.X, s.Y), SourceForBuilding(s.Definition.Type), AtlasOrigin, false));
			}
		};
		ConstructionManager.Instance.OnSiteAdded += s =>
		{
			if (s.Definition.IsWall)
				_wallBlueprints?.MarkDirty(new Vector2I(s.X, s.Y));
			else
				_pendingCells.Enqueue(new PendingCell(_blueprintLayer, new Vector2I(s.X, s.Y), SourceForBuilding(s.Definition.Type), AtlasOrigin, false));
		};
		ConstructionManager.Instance.OnSiteRemoved += pos =>
		{
			MarkWallBlueprintRemoved(pos.X, pos.Y);
		};
		ConstructionManager.Instance.OnSitesBatchRemoved += list =>
		{
			foreach (var pos in list)
			{
				MarkWallBlueprintRemoved(pos.X, pos.Y);
			}
		};
		ConstructionManager.Instance.OnSiteCompleted += s =>
		{
			_wallBlueprints?.MarkDirty(new Vector2I(s.X, s.Y));
		};

		_onBuildingPlaced = (pos, type) =>
		{
			int sourceId = SourceForBuilding(type);
			_pendingCells.Enqueue(new PendingCell(_buildingLayer, new Vector2I(pos.X, pos.Y), sourceId, AtlasOrigin, false));
			ShadowRenderer?.AddCaster(pos.X, pos.Y);
		};
		_onBuildingRemoved = pos =>
		{
			_pendingCells.Enqueue(new PendingCell(_buildingLayer, new Vector2I(pos.X, pos.Y), 0, AtlasOrigin, true));
			ShadowRenderer?.RemoveCaster(pos.X, pos.Y);
		};
		BuildingManager.Instance.OnBuildingPlaced += _onBuildingPlaced;
		BuildingManager.Instance.OnBuildingRemoved += _onBuildingRemoved;

		_onPlotMarked = pos => _farmBlueprintTerrain?.MarkDirty(new Vector2I(pos.X, pos.Y));
		_onPlotsBatchMarked = list =>
		{
			foreach (var pos in list)
				_farmBlueprintTerrain?.MarkDirty(new Vector2I(pos.X, pos.Y));
		};
		_onPlotUnmarked = pos =>
		{
			_farmBlueprintTerrain?.MarkDirty(new Vector2I(pos.X, pos.Y));
			_farmTerrain?.MarkDirty(new Vector2I(pos.X, pos.Y));
		};
		_onPlotsBatchUnmarked = list =>
		{
			foreach (var pos in list)
			{
				_farmBlueprintTerrain?.MarkDirty(new Vector2I(pos.X, pos.Y));
				_farmTerrain?.MarkDirty(new Vector2I(pos.X, pos.Y));
			}
		};
		_onPlotCompleted = pos =>
		{
			_farmBlueprintTerrain?.MarkDirty(new Vector2I(pos.X, pos.Y));
			_farmTerrain?.MarkDirty(new Vector2I(pos.X, pos.Y));
		};

		FarmJobManager.Instance.OnPlotMarked += _onPlotMarked;
		FarmJobManager.Instance.OnPlotsBatchMarked += _onPlotsBatchMarked;
		FarmJobManager.Instance.OnPlotUnmarked += _onPlotUnmarked;
		FarmJobManager.Instance.OnPlotsBatchUnmarked += _onPlotsBatchUnmarked;
		FarmJobManager.Instance.OnPlotCompleted += _onPlotCompleted;

		// Если карта уже подана извне или отложена автогенерация — пропускаем
		if (_pendingMapData != null || _hasExternalMap || DeferAutoGeneration) return;

		Task.Run(() =>
		{
			uint seed = SeedOverride != 0
				? unchecked((uint)SeedOverride)
				: unchecked((uint)Random.Shared.Next(int.MinValue, int.MaxValue));
			CurrentMapSeed = seed;

			// Единая генерация через UnifiedWorldGenerator
			_pendingMapData = UnifiedWorldGenerator.GenerateColonyWindow(seed, UnifiedWorldGenerator.DefaultGeneratorVersion, 0, 0, MapWidth, MapHeight);

			int grass = 0, water = 0, mountains = 0, trees = 0, stones = 0;
			for (int x = 0; x < MapWidth; x++)
				for (int y = 0; y < MapHeight; y++)
				{
					if (_pendingMapData.Ground[x, y] == TileType.Grass) grass++;
					else if (_pendingMapData.Ground[x, y] == TileType.Mountain) mountains++;
					else water++;
					if (_pendingMapData.TreeOnGrass[x, y]) trees++;
					if (_pendingMapData.StoneOnGrass[x, y]) stones++;
				}
			GD.Print($"MapRenderer: карта сгенерирована, seed={seed} (SeedOverride={SeedOverride}), суша={grass}, вода={water}, горы={mountains}, деревья={trees}, камни={stones}, озёр={_pendingMapData.LakeCount}[{string.Join(",", _pendingMapData.LakeSizes)}], река={_pendingMapData.MainRiverLength}+{_pendingMapData.RiverBranchCount}пр, связность={_pendingMapData.LandConnectivity:P1}");

			Callable.From(ApplyMap).CallDeferred();
		});
	}

	public override void _Process(double delta)
	{
		if (!_choppedTreesQueue.IsEmpty && _objectLayer != null)
		{
			int budget = MaxCellsPerFrame;
			while (budget-- > 0 && _choppedTreesQueue.TryDequeue(out var cell))
			{
				_objectLayer.EraseCell(cell);
			}
		}
		// Flush батча клеток: адаптивный бюджет — при массовой разметке (500x500 фермы)
		// обрабатываем до 16384 клеток за кадр, чтобы экран мгновенно заполнялся без фризов
		int cellsBudget = _pendingCells.Count > 4096 ? 16384 : MaxCellsPerFrame;
		while (cellsBudget-- > 0 && _pendingCells.TryDequeue(out var pc))
		{
			if (pc.Layer == null) continue;
			if (pc.Erase) pc.Layer.EraseCell(pc.Pos);
			else pc.Layer.SetCell(pc.Pos, pc.Source, pc.Atlas);
		}
		// Дебаунс 200мс: при массовой стройке (1000 чертежей батчем) полный
		// Clear+SetCell всех чертежей каждый кадр давал десятки мс фриза.
		// Флаг лишь коалесцирует события, гейт — по wall-clock.
		if (_blueprintRefreshQueued
			&& (System.Environment.TickCount64 - _lastBlueprintRefreshMs) >= 200)
		{
			_blueprintRefreshQueued = false;
			_lastBlueprintRefreshMs = System.Environment.TickCount64;
			RefreshAllBlueprints();
		}
		// Оверлей влажности обновляется сам по таймеру (троттлинг внутри Refresh):
		// тик влажности раз в 30с игрового, текстуру чаще дёргать незачем.
		if (CurrentMapMode == MapMode.Humidity && _humidityOverlay != null)
			_humidityOverlay.RefreshThrottled(Humidity, delta);
		else if (CurrentMapMode == MapMode.Fertility && _fertilityOverlay != null)
			_fertilityOverlay.RefreshThrottled(Fertility, delta);
		// Стандарт: terrain-слои флашатся через TerrainTileLayer.
		// WallBuildManager копит NotifyChanged дебаунсом 200мс — сливаем хвост
		// здесь же (главный поток, _Process), иначе пачка стен висела бы в
		// _pending до следующей одиночной стены.
		_wallBuildManager?.FlushPending();
		// Terrain-флаш обоих слоёв стен: построенные — по IsWallAt, чертежи —
		// только WoodWall-чертежи (мебель в этот слой не попадает).
		_builtWalls?.Flush(c => StructureManager.Instance.IsWallAt(c.X, c.Y));
		_wallBlueprints?.Flush(c => ConstructionManager.Instance.IsWallSiteAt(c.X, c.Y));
		_farmTerrain?.Flush(c => FarmJobManager.Instance.IsGardenBed(c.X, c.Y));
		_farmBlueprintTerrain?.Flush(c => FarmJobManager.Instance.IsPlotMarked(c.X, c.Y));
		// Macro-LOD: при сильном отдалении (Zoom < 0.22x) скрываем микро-слои,
		// которые неразличимы глазом, но создают десятки тысяч CanvasItems.
		var cam = GetViewport()?.GetCamera2D();
		float zoom = cam?.Zoom.X ?? 1f;
		if (_workProgressLayer != null)
		{
			bool showProgress = zoom >= 0.22f;
			if (_workProgressLayer.Visible != showProgress)
				_workProgressLayer.Visible = showProgress;
		}
		if (ShadowRenderer != null && zoom < 0.22f && ShadowRenderer.Visible)
		{
			ShadowRenderer.Visible = false;
		}

		FlushWorkProgress(delta);

		if (_terrainMaterial != null)
		{
			float speed = TimeManager.Instance != null ? TimeManager.Instance.SpeedMultiplier : 1f;
			float rate = speed <= 0f ? 0f : Mathf.Pow(speed, 0.45f);
			_waterAccumulatedTime += (float)delta * rate;
			_terrainMaterial.SetShaderParameter("water_time", _waterAccumulatedTime);

			if (_groundSurface != null && cam != null)
			{
				long camTileX = (long)MathF.Floor(cam.Position.X / TileSizePx);
				long camTileY = (long)MathF.Floor(cam.Position.Y / TileSizePx);
				// Сдвиг окна с квантованием 64 клетки (1 чанк стриминга = 4096 px).
				// Центрируем окно СВОИМ размером TerrainWindowTiles (§117.2): рост острова
				// MapWidth 512→1024 поведению follow-окна ничего не меняет.
				long targetOriginX = WorldCoordinates.FloorDiv(camTileX - TerrainWindowTiles / 2, TerrainWindowShift) * TerrainWindowShift;
				long targetOriginY = WorldCoordinates.FloorDiv(camTileY - TerrainWindowTiles / 2, TerrainWindowShift) * TerrainWindowShift;

				// Применяем готовый фоновый буфер, если фоновый таск завершился.
				// Устаревший буфер (камера уже уехала / была инвалидация) НЕ применяем:
				// раньше он применялся — окно прыгало назад и пересчитывалось целиком.
				if (_pendingReadyBuffer != null)
				{
					byte[] readyBuf = _pendingReadyBuffer;
					_pendingReadyBuffer = null;
					if (_pendingReadyGen == _terrainWindowGen
						&& _pendingReadyOriginX == targetOriginX
						&& _pendingReadyOriginY == targetOriginY)
					{
						ApplyTerrainBuffer(_pendingReadyOriginX, _pendingReadyOriginY, readyBuf);
						_terrainWindowValidGen = _pendingReadyGen;
					}
				}

				if (targetOriginX != _currentTerrainOriginX || targetOriginY != _currentTerrainOriginY || _terrainTextureNeedsUpdate)
				{
					RequestTerrainWindowUpdate(targetOriginX, targetOriginY);
				}
			}
		}
	}

	public void InvalidateTerrainTexture()
	{
		_terrainTextureNeedsUpdate = true;
		// Новое поколение контента окна: клетки текущего окна больше нельзя
		// переиспользовать инкрементально (сид/WorldOrigin/карта изменились) —
		// следующий запрос пересчитает окно целиком (forceFull).
		_terrainWindowGen++;
	}

	// ================= F2 (§117.1): follow-окно террейна =================
	// Землю всего мира (остров + бесконечный стриминг) рисует _groundSurface
	// (ColorRect + terrain_blend.gdshader): окно TerrainWindowTiles² следует за
	// камерой с квантованием 64 клетки. Байты окна (0=Grass, 1=Sand, 2=Water)
	// заполняются тремя путями:
	//  1) окно ЦЕЛИКОМ внутри острова → синхронно из mapData (~0.3 мс);
	//  2) внешние клетки → сетка макро-климата шагом 16 (как GenerateChunk:
	//     5×5 узлов на чанк 64², 33×33 на окно 512²) + билинейный Lerp по u,v
	//     внутри 16×16 подоблока + UnifiedWorldGenerator.SampleCell(overrideClimate):
	//     полный WorldClimateGenerator.SampleBlendedCell (тектоника/Voronoi) зовётся
	//     ТОЛЬКО в узлах сетки — 1089 раз вместо 262144 (сотни раз дешевле, F2);
	//     решётка сетки совпадает с чанковой (origin окна кратен 64, WorldOrigin
	//     кратен 512) → 0 швов со стриминговыми чанками;
	//  3) сдвиг окна на 64 клетки → инкрементально: переиспользуем уже посчитанные
	//     клетки (сдвиг буфера front→back), пересчитываем только новую полосу 64×512.

	private volatile bool _terrainUpdateInProgress = false;
	private byte[] _backTerrainBuffer;
	private volatile byte[] _pendingReadyBuffer;
	private long _pendingReadyOriginX;
	private long _pendingReadyOriginY;
	private long _requestedOriginX;
	private long _requestedOriginY;
	private int _pendingReadyGen;
	/// <summary>Поколение контента окна: +1 на каждую инвалидацию (сид/WorldOrigin/карта) — клетки разных поколений инкрементально не переиспользуются.</summary>
	private int _terrainWindowGen;
	/// <summary>Поколение, которому соответствует _terrainBuffer (front-зеркало применённого окна).</summary>
	private int _terrainWindowValidGen = -1;
	/// <summary>Переиспользуемый Image окна: аллокация один раз, дальше SetData (анти-GC, lead F-3).</summary>
	private Image _terrainImage;

	/// <summary>
	/// Кап параллелизма заливки окна (lead): Parallel.For по умолчанию берёт все ядра
	/// и конкурирует с воркерами WorldChunkStreamer и симуляцией → ThreadPool
	/// голодание → хитчи главного потока.
	/// </summary>
	private static readonly int TerrainFillMaxDop = Math.Max(2, System.Environment.ProcessorCount / 4);
	private static readonly ParallelOptions TerrainFillParallelOptions = new ParallelOptions
	{
		MaxDegreeOfParallelism = TerrainFillMaxDop
	};

	private void RequestTerrainWindowUpdate(long originX, long originY)
	{
		_requestedOriginX = originX;
		_requestedOriginY = originY;
		bool forceFull = _terrainTextureNeedsUpdate;
		_terrainTextureNeedsUpdate = false;
		int gen = _terrainWindowGen;

		MapData mapData = _pendingMapData ?? _currentMapData;

		// Быстрый путь: окно ЦЕЛИКОМ внутри острова колонии — заполняем мгновенно
		// синхронно из mapData (~0.3 мс) без запуска фонового потока. Обобщение
		// прежнего условия origin==(0,0): окно может быть МЕНЬШЕ острова (MapWidth→1024,
		// §117.2) и лежать с любым origin внутри [0..mapData.Width-окно] — границы
		// острова берём из фактического mapData, не из констант.
		if (mapData != null
			&& originX >= 0 && originY >= 0
			&& originX + TerrainWindowTiles <= mapData.Width
			&& originY + TerrainWindowTiles <= mapData.Height)
		{
			_terrainBuffer ??= new byte[TerrainWindowTiles * TerrainWindowTiles];
			FillIslandWindowFromMapData(_terrainBuffer, mapData, originX, originY);
			ApplyTerrainBuffer(originX, originY, _terrainBuffer);
			_terrainWindowValidGen = gen;
			return;
		}

		if (_terrainUpdateInProgress)
		{
			// Фоновый таск ещё считает прошлое окно — запрос не теряем: возвращаем
			// флаг, _Process следующим кадром перезапросит уже актуальный origin.
			if (forceFull)
				_terrainTextureNeedsUpdate = true;
			return;
		}
		_terrainUpdateInProgress = true;

		_backTerrainBuffer ??= new byte[TerrainWindowTiles * TerrainWindowTiles];

		// Инкрементальность (F2): при сдвиге окна переиспользуем уже посчитанные
		// клетки — сдвиг буфера front→back ВЫПОЛНЯЕТСЯ НА ГЛАВНОМ ПОТОКЕ (фоновый
		// таск фронт не трогает — гонок нет), пересчёт только новой полосы.
		int compX0 = 0, compX1 = 0, compY0 = 0, compY1 = 0;
		bool canReuse = !forceFull
			&& _terrainBuffer != null
			&& _terrainWindowValidGen == gen
			&& _currentTerrainOriginX != long.MinValue
			&& Math.Abs(originX - _currentTerrainOriginX) < TerrainWindowTiles
			&& Math.Abs(originY - _currentTerrainOriginY) < TerrainWindowTiles;
		if (canReuse)
		{
			int dx = (int)(originX - _currentTerrainOriginX);
			int dy = (int)(originY - _currentTerrainOriginY);
			// Вычисляемые полосы = клетки вне пересечения старого и нового окон.
			if (dx > 0) { compX0 = TerrainWindowTiles - dx; compX1 = TerrainWindowTiles; }
			else if (dx < 0) { compX0 = 0; compX1 = -dx; }
			if (dy > 0) { compY0 = TerrainWindowTiles - dy; compY1 = TerrainWindowTiles; }
			else if (dy < 0) { compY0 = 0; compY1 = -dy; }

			// Сдвиг буфера: пересечение окон front → back (BlockCopy по строкам,
			// ~0.1 мс на 512² — дешевле пересчёта в ~1000 раз).
			int xRe0 = Math.Max(0, -dx);
			int xRe1 = Math.Min(TerrainWindowTiles, TerrainWindowTiles - dx);
			int yRe0 = Math.Max(0, -dy);
			int yRe1 = Math.Min(TerrainWindowTiles, TerrainWindowTiles - dy);
			for (int y = yRe0; y < yRe1; y++)
			{
				Buffer.BlockCopy(_terrainBuffer, (y + dy) * TerrainWindowTiles + (xRe0 + dx),
					_backTerrainBuffer, y * TerrainWindowTiles + xRe0, xRe1 - xRe0);
			}
		}
		else
		{
			// Полное заполнение: первое окно / инвалидация (сид/WorldOrigin/карта) /
			// прыжок камеры дальше размера окна.
			compX0 = 0;
			compX1 = TerrainWindowTiles;
		}

		long reqX = originX;
		long reqY = originY;
		ulong seed = UnifiedWorldGenerator.ActiveWorldSeed;
		uint ver = UnifiedWorldGenerator.DefaultGeneratorVersion;
		long worldOriginX = UnifiedWorldGenerator.WorldOriginX;
		long worldOriginY = UnifiedWorldGenerator.WorldOriginY;
		int cx0 = compX0, cx1 = compX1, cy0 = compY0, cy1 = compY1;
		MapData md = mapData;

		Task.Run(() =>
		{
			try
			{
				FillTerrainWindowExternal(_backTerrainBuffer, TerrainWindowTiles,
					seed, ver, reqX, reqY, worldOriginX, worldOriginY,
					cx0, cx1, cy0, cy1, md);

				_pendingReadyGen = gen;
				_pendingReadyOriginX = reqX;
				_pendingReadyOriginY = reqY;
				_pendingReadyBuffer = _backTerrainBuffer;
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[MapRenderer] Ошибка фоновой генерации террейна: {ex.Message}");
			}
			finally
			{
				_terrainUpdateInProgress = false;
			}
		});
	}

	/// <summary>Быстрый путь: окно целиком внутри острова — байты напрямую из mapData.</summary>
	private static void FillIslandWindowFromMapData(byte[] buffer, MapData mapData, long originX, long originY)
	{
		for (int y = 0; y < TerrainWindowTiles; y++)
		{
			int rowOffset = y * TerrainWindowTiles;
			int my = (int)(originY + y);
			for (int x = 0; x < TerrainWindowTiles; x++)
			{
				buffer[rowOffset + x] = TerrainByteFromMapData(mapData, (int)(originX + x), my);
			}
		}
	}

	/// <summary>Байт типа поверхности из острова: 0=Grass, 1=Sand (биом Desert), 2=Water; Mountain → трава (как прежде).</summary>
	private static byte TerrainByteFromMapData(MapData mapData, int x, int y)
	{
		if (mapData.Ground[x, y] == TileType.Water)
			return 2;
		return mapData.GetBiome(x, y) == BiomeType.Desert ? (byte)1 : (byte)0;
	}

	/// <summary>
	/// Ядро F2-фикса: заполнение вычисляемой области follow-окна террейна.
	/// Внешние клетки (вне острова) — сетка макро-климата шагом TerrainClimateStep
	/// с билинейной интерполяцией Lerp по u,v внутри 16×16 подоблока +
	/// UnifiedWorldGenerator.SampleCell(overrideClimate) — в точности схема
	/// GenerateChunk (строки 173-199) / GenerateColonyWindow: полный
	/// WorldClimateGenerator.SampleBlendedCell зовётся только в узлах сетки
	/// (33×33 на полное окно против 262144 полных семплов у старого пути),
	/// а совпадение решётки с чанковой гарантирует 0 швов со стримингом.
	/// Клетки острова — быстрый путь из mapData. buffer уже содержит
	/// переиспользуемые (сдвинутые) клетки: заполняются только полосы
	/// x∈[compX0..compX1) и y∈[compY0..compY1); обе пустые = no-op
	/// (окно уже посчитано и просто сдвинуто). internal — для headless-бенчмарка
	/// tests/terrain_window_bench.cs (та же сборка).
	/// </summary>
	internal static void FillTerrainWindowExternal(
		byte[] buffer, int windowSize,
		ulong worldSeed, uint generatorVersion,
		long originLocalX, long originLocalY,
		long worldOriginX, long worldOriginY,
		int compX0, int compX1, int compY0, int compY1,
		MapData mapData)
	{
		bool hasX = compX1 > compX0;
		bool hasY = compY1 > compY0;
		if (!hasX && !hasY)
			return; // сдвиг (0,0): всё окно уже посчитано (сдвиг буфера)

		// Диапазон подоблоков 16×16, покрывающих вычисляемые клетки → сетка климата.
		// Строки полосы compY считают ВСЮ ширину окна → сетка по X нужна полная (и наоборот).
		int xCoverMin = hasY ? 0 : compX0;
		int xCoverMax = hasY ? windowSize : compX1;
		int yCoverMin = hasX ? 0 : compY0;
		int yCoverMax = hasX ? windowSize : compY1;
		int gxLo = xCoverMin / TerrainClimateStep;
		int gxHi = (xCoverMax - 1) / TerrainClimateStep + 1; // последний УЗЕЛ сетки (включительно)
		int gyLo = yCoverMin / TerrainClimateStep;
		int gyHi = (yCoverMax - 1) / TerrainClimateStep + 1;
		int gridW = gxHi - gxLo + 1;
		int gridH = gyHi - gyLo + 1;

		long worldBaseX = worldOriginX + originLocalX;
		long worldBaseY = worldOriginY + originLocalY;

		// Сетка климата: единственные полные вызовы SampleBlendedCell (тектоника/
		// Voronoi) во всём заполнении: 33×33 на полное окно, 5×33 на полосу 64.
		var grid = new WorldClimateParams[gridW * gridH];
		Parallel.For(0, gridH, TerrainFillParallelOptions, gy =>
		{
			long gwy = worldBaseY + (long)(gyLo + gy) * TerrainClimateStep;
			int row = gy * gridW;
			for (int gx = 0; gx < gridW; gx++)
			{
				long gwx = worldBaseX + (long)(gxLo + gx) * TerrainClimateStep;
				grid[row + gx] = WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, gwx, gwy);
			}
		});

		int mapW = mapData?.Width ?? 0;
		int mapH = mapData?.Height ?? 0;

		Parallel.For(0, windowSize, TerrainFillParallelOptions, y =>
		{
			// Строка полосы compY считает всю ширину; остальные строки — только столбцы compX.
			bool fullRow = (uint)(y - compY0) < (uint)(compY1 - compY0);
			int xStart = fullRow ? 0 : compX0;
			int xEnd = fullRow ? windowSize : compX1;
			if (xEnd <= xStart)
				return;

			int rowOffset = y * windowSize;
			long wy = worldBaseY + y;
			long cy = originLocalY + y;
			uint v = (uint)((y % TerrainClimateStep) * 65535 / TerrainClimateStep);
			int gTopRow = (y / TerrainClimateStep - gyLo) * gridW;
			int gBotRow = gTopRow + gridW;

			for (int x = xStart; x < xEnd; x++)
			{
				long cx = originLocalX + x;
				byte type;
				if (mapData != null && (uint)cx < (uint)mapW && (uint)cy < (uint)mapH)
				{
					// Клетка острова: прежний быстрый путь из mapData.
					type = TerrainByteFromMapData(mapData, (int)cx, (int)cy);
				}
				else
				{
					// Внешняя клетка: Lerp климата внутри 16×16 подоблока
					// (образец — GenerateChunk строки 173-199).
					int bx = x / TerrainClimateStep - gxLo;
					uint u = (uint)((x % TerrainClimateStep) * 65535 / TerrainClimateStep);
					WorldClimateParams nw = grid[gTopRow + bx];
					WorldClimateParams ne = grid[gTopRow + bx + 1];
					WorldClimateParams swC = grid[gBotRow + bx];
					WorldClimateParams seC = grid[gBotRow + bx + 1];
					WorldClimateParams top = LerpClimate(in nw, in ne, u);
					WorldClimateParams bot = LerpClimate(in swC, in seC, u);
					WorldClimateParams cellClimate = LerpClimate(in top, in bot, v);

					GeneratedCell cell = UnifiedWorldGenerator.SampleCell(worldSeed, generatorVersion, worldBaseX + x, wy, cellClimate);
					type = cell.Terrain == BaseTerrainKind.Water ? (byte)2
						: (cell.Biome == (byte)BiomeType.Desert ? (byte)1 : (byte)0);
				}
				buffer[rowOffset + x] = type;
			}
		});
	}

	/// <summary>Побитовая копия UnifiedWorldGenerator.LerpQ16 (там private): та же арифметика Q16 — идентичность климата чанкам стриминга.</summary>
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	private static ushort LerpClimateQ16(ushort a, ushort b, uint t)
	{
		uint clampedT = Math.Min(t, 65535u);
		long value = (long)a * (65535 - clampedT) + (long)b * clampedT + 32768;
		return (ushort)Math.Clamp(value / 65535, 0, 65535);
	}

	/// <summary>Побитовая копия UnifiedWorldGenerator.LerpClimate1D (там private): билинейный Lerp 18 параметров по t.</summary>
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	private static WorldClimateParams LerpClimate(in WorldClimateParams a, in WorldClimateParams b, uint t)
	{
		return new WorldClimateParams(
			LerpClimateQ16(a.ElevationQ16, b.ElevationQ16, t),
			LerpClimateQ16(a.ContinentalnessQ16, b.ContinentalnessQ16, t),
			LerpClimateQ16(a.LatitudeQ16, b.LatitudeQ16, t),
			LerpClimateQ16(a.TemperatureQ16, b.TemperatureQ16, t),
			LerpClimateQ16(a.MoistureQ16, b.MoistureQ16, t),
			LerpClimateQ16(a.PrecipitationQ16, b.PrecipitationQ16, t),
			LerpClimateQ16(a.DrainageQ16, b.DrainageQ16, t),
			LerpClimateQ16(a.WindHeadingQ16, b.WindHeadingQ16, t),
			LerpClimateQ16(a.RainShadowQ16, b.RainShadowQ16, t),
			LerpClimateQ16(a.TectonicPressureQ16, b.TectonicPressureQ16, t),
			LerpClimateQ16(a.OceanInfluenceQ16, b.OceanInfluenceQ16, t),
			LerpClimateQ16(a.VegetationDensityQ16, b.VegetationDensityQ16, t),
			LerpClimateQ16(a.SoilFertilityQ16, b.SoilFertilityQ16, t),
			LerpClimateQ16(a.RockExposureQ16, b.RockExposureQ16, t),
			LerpClimateQ16(a.GlaciationQ16, b.GlaciationQ16, t),
			LerpClimateQ16(a.AridityQ16, b.AridityQ16, t),
			LerpClimateQ16(a.ArchipelagoTendencyQ16, b.ArchipelagoTendencyQ16, t),
			LerpClimateQ16(a.RiverDensityQ16, b.RiverDensityQ16, t));
	}

	private void ApplyTerrainBuffer(long originX, long originY, byte[] buffer)
	{
		_currentTerrainOriginX = originX;
		_currentTerrainOriginY = originY;

		// Front-зеркало: _terrainBuffer всегда содержит применённое окно — источник
		// клеток для инкрементального сдвига при следующем запросе. Заполняется и
		// быстрым путём (он пишет прямо в _terrainBuffer), и фоновым (копия из back).
		if (!ReferenceEquals(buffer, _terrainBuffer))
		{
			_terrainBuffer ??= new byte[TerrainWindowTiles * TerrainWindowTiles];
			Buffer.BlockCopy(buffer, 0, _terrainBuffer, 0, TerrainWindowTiles * TerrainWindowTiles);
		}

		// Image переиспользуется (анти-GC, lead F-3): аллокация только на первом
		// применении окна, дальше SetData в тот же объект + Update текстуры (R8).
		if (_terrainImage == null)
		{
			_terrainImage = Image.CreateFromData(TerrainWindowTiles, TerrainWindowTiles, false, Image.Format.R8, _terrainBuffer);
			_terrainTypeTexture = ImageTexture.CreateFromImage(_terrainImage);
			_terrainMaterial?.SetShaderParameter("terrain_map", _terrainTypeTexture);
		}
		else
		{
			_terrainImage.SetData(TerrainWindowTiles, TerrainWindowTiles, false, Image.Format.R8, _terrainBuffer);
			_terrainTypeTexture.Update(_terrainImage);
		}

		if (_groundSurface != null)
		{
			// Позиция согласована с Size окна (TerrainWindowTiles — фиксируется в _Ready
			// той же константой): ColorRect покрывает [origin, origin+окно) тайлов.
			_groundSurface.Position = new Vector2(originX * TileSizePx, originY * TileSizePx);
		}
		_terrainMaterial?.SetShaderParameter("map_origin", new Vector2(originX, originY));
		_terrainMaterial?.SetShaderParameter("map_size", new Vector2(TerrainWindowTiles, TerrainWindowTiles));
	}

	/// <summary>
	/// Слой прогресса работ: DrainDirty трекера → SetCell/EraseCell бюджетом
	/// 256/кадр + SweepStale раз в 2с (протухшие без Clear стираем).
	/// Стадия -1 = стереть спрайт (работа завершена/отменена).
	/// </summary>
	private void FlushWorkProgress(double delta)
	{
		if (_workProgressLayer?.TileSet == null)
			return;
		WorkProgressTracker.Instance.DrainDirty(_progressDrain, 256);
		if (!_workProgressLayer.Visible)
		{
			// При отдалении камеры (LOD) слой скрыт — стираем только завершённые, не рисуем новые
			foreach (var (x, y, stage) in _progressDrain)
			{
				if (stage < 0)
					_workProgressLayer.EraseCell(new Vector2I(x, y));
			}
			return;
		}
		foreach (var (x, y, stage) in _progressDrain)
		{
			var pos = new Vector2I(x, y);
			if (stage < 0)
			{
				_workProgressLayer.EraseCell(pos);
				continue;
			}
			var (ax, ay) = WorkProgressTracker.AtlasForStage(stage);
			_workProgressLayer.SetCell(pos, SourceWorkProgress, new Vector2I(ax, ay));
		}
		_progressSweepTimer += (float)delta;
		if (_progressSweepTimer >= 2.0f)
		{
			_progressSweepTimer = 0f;
			WorkProgressTracker.Instance.SweepStale(_progressStale, System.TimeSpan.FromSeconds(10));
			foreach (var (x, y) in _progressStale)
				_workProgressLayer.EraseCell(new Vector2I(x, y));
		}
	}

	/// <summary>
	/// Кодовый TileSet прогресса из uid://qqhtvxqngv4a (атлас 3×2, тайл 64).
	/// Одиночный Source без terrain — стадии выбираем атлас-координатой.
	/// Null — текстура не загрузилась (слой молча пуст, игра идёт).
	/// </summary>
	private TileSet CreateWorkProgressTileSet()
	{
		var tileSet = new TileSet { TileSize = new Vector2I(TileSizePx, TileSizePx) };
		var tex = LoadTexture(TextureProcessOfWork, "process_of_work");
		if (tex == null)
		{
			GD.PrintErr("MapRenderer: текстура прогресса (uid://qqhtvxqngv4a) не найдена!");
			return tileSet;
		}
		var src = new TileSetAtlasSource { Texture = tex, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
		for (int ty = 0; ty < 2; ty++)
			for (int tx = 0; tx < 3; tx++)
				src.CreateTile(new Vector2I(tx, ty));
		tileSet.AddSource(src, SourceWorkProgress);
		return tileSet;
	}

	private void QueueBlueprintRefresh() => _blueprintRefreshQueued = true;

	/// <summary>
	/// Переключить режим карты. Humidity — синий слой влажности, Fertility —
	/// зелёный слой плодородия, Normal — спрятать всё. Режимы взаимоисключающие.
	/// Слой обновляется раз в кадр троттлингом внутри.
	/// </summary>
	public void SetMapMode(MapMode mode)
	{
		CurrentMapMode = mode;
		if (_humidityOverlay != null)
		{
			// Сначала Visible=true, потом Refresh: невидимому Refresh — no-op.
			_humidityOverlay.Visible = mode == MapMode.Humidity;
			if (mode == MapMode.Humidity)
				_humidityOverlay.Refresh(Humidity);
		}
		if (_fertilityOverlay != null)
		{
			_fertilityOverlay.Visible = mode == MapMode.Fertility;
			if (mode == MapMode.Fertility)
				_fertilityOverlay.Refresh(Fertility);
		}
		OnMapModeChanged?.Invoke(mode);
	}

	/// <summary>Дёрнуть обновление оверлея (после тика влажности раз в 30с).</summary>
	public void RefreshHumidityOverlay()
	{
		if (CurrentMapMode == MapMode.Humidity && _humidityOverlay != null)
			_humidityOverlay.Refresh(Humidity);
	}

	/// <summary>Дёрнуть обновление оверлея (после тика плодородия раз в 60с).</summary>
	public void RefreshFertilityOverlay()
	{
		if (CurrentMapMode == MapMode.Fertility && _fertilityOverlay != null)
			_fertilityOverlay.Refresh(Fertility);
	}

	public void ClearAllLayers()
	{
		_groundLayer?.Clear();
		_objectLayer?.Clear();
		_mountainLayer?.Clear();
		_wallLayer?.Clear();
		_buildingLayer?.Clear();
		_farmLayer?.Clear();
		_farmBlueprintLayer?.Clear();
		_farmTerrain?.Clear();
		_farmBlueprintTerrain?.Clear();
		_stockpileLayer?.Clear();
		_blueprintLayer?.Clear();
		_wallBlueprintLayer?.Clear();
		_ghostLayer?.Clear();
		_workProgressLayer?.Clear();
	}

	private void ApplyMap()
	{
		try
		{
			MapData mapData = _pendingMapData;
			if (mapData == null) return;
			_currentMapData = mapData;
			_pendingMapData = null;

			ClearAllLayers();

			TileSet groundTileSet = CreateGroundTileSet();
			_groundLayer.TileSet = groundTileSet;
			_stockpileLayer.TileSet = groundTileSet;

			if (_terrainMaterial != null)
			{
				var grassTex = LoadTexture(TextureGrass, "grass");
				var sandTex = LoadTexture(TextureSand, "sand");
				var waterTex = LoadTexture(TextureWater, "water");
				if (grassTex != null) _terrainMaterial.SetShaderParameter("tex_grass", grassTex);
				if (sandTex != null) _terrainMaterial.SetShaderParameter("tex_sand", sandTex);
				if (waterTex != null) _terrainMaterial.SetShaderParameter("tex_water", waterTex);

				// Новая карта = новое поколение контента окна (могли смениться сид и
				// WorldOrigin): без этого инкрементальный сдвиг переиспользовал бы
				// байты прошлой карты (InvalidateTerrainTexture → forceFull).
				InvalidateTerrainTexture();
				RequestTerrainWindowUpdate(_currentTerrainOriginX == long.MinValue ? 0 : _currentTerrainOriginX,
									_currentTerrainOriginY == long.MinValue ? 0 : _currentTerrainOriginY);
				if (_groundSurface != null)
				{
					_groundSurface.Material = _terrainMaterial;
					_groundSurface.Visible = true;
				}
				_groundLayer.Visible = false;
			}

			TileSet objectTileSet = CreateObjectTileSet();
			_objectLayer.TileSet = objectTileSet;

			

			// Горы — terrain-слой (стандарт): TileSet с разметкой из сцены
			// (mountains_tile в Main.tscn), атлас подбирает движок.
			TileSet mountainTerrainTileSet = GetMountainTerrainTileSet() ?? CreateMountainTileSet();
			_mountainLayer.TileSet = mountainTerrainTileSet;
			_mountains = new TerrainTileLayer(_mountainLayer, MountainTerrainSetId, MountainTerrainId);
			var mountainCells = new List<Vector2I>();

			bool populateGround = _groundLayer != null && _groundLayer.Visible;
			Vector2I[] grassVariants = populateGround ? GetAtlasTileCoords(groundTileSet, SourceGrass) : null;
			Vector2I[] waterVariants = populateGround ? GetAtlasTileCoords(groundTileSet, SourceWater) : null;
			Vector2I[] sandVariants = populateGround ? GetAtlasTileCoords(groundTileSet, SourceSand) : null;
			bool hasSand = populateGround && groundTileSet.HasSource(SourceSand);

			for (int x = 0; x < MapWidth; x++)
			{
				for (int y = 0; y < MapHeight; y++)
				{
					var pos = new Vector2I(x, y);
					int variant = populateGround ? mapData.GetGroundVariant(x, y) : 0;

					if (mapData.Ground[x, y] == TileType.Mountain)
					{
						if (populateGround)
						{
							Vector2I coords = grassVariants[variant % grassVariants.Length];
							_groundLayer.SetCell(pos, SourceGrass, coords);
						}
						mountainCells.Add(pos);
					}
					else if (mapData.Ground[x, y] == TileType.Grass)
					{
						if (populateGround)
						{
							var biome = mapData.GetBiome(x, y);
							int sourceId;
							Vector2I coords;
							if (biome == BiomeType.Desert && hasSand)
							{
								sourceId = SourceSand;
								coords = sandVariants[variant % sandVariants.Length];
							}
							else
							{
								sourceId = SourceGrass;
								coords = grassVariants[variant % grassVariants.Length];
							}
							_groundLayer.SetCell(pos, sourceId, coords);
						}

						if (mapData.TreeOnGrass[x, y])
						{
							int treeVariant = mapData.GetTreeVariant(x, y) & 3;
							_objectLayer.SetCell(pos, SourceObjectComposite, new Vector2I(treeVariant, 0));
						}
						else if (mapData.StoneOnGrass[x, y])
						{
							int stoneVariant = UnifiedWorldGenerator.StoneVariantFor(x, y, mapData.Seed) & 3;
							_objectLayer.SetCell(pos, SourceObjectComposite, new Vector2I(4 + stoneVariant, 0));
						}
					}
					else
					{
						if (populateGround)
						{
							Vector2I coords = waterVariants[variant % waterVariants.Length];
							_groundLayer.SetCell(pos, SourceWater, coords);
						}
					}
				}
			}

			_mountains.RebuildAll(mountainCells);

			// Слои стен/чертежей/госта используют TileSet с terrain-разметкой
			// из сцены (wood_wall_tile в Main.tscn) — автотайлинг везде считает
			// сам Godot через SetCellsTerrainConnect. Кодовый CreateBuildingTileSet
			// больше не используется для стен (там нет terrain-битов).
			TileSet wallTerrainTileSet = GetWallTerrainTileSet() ?? CreateBuildingTileSet();
			TileSet buildingTileSet = CreateBuildingTileSet();
			// Слой прогресса: TileSet кодом из ProcessOfWork (атлас 3×2).
			_workProgressLayer.TileSet = CreateWorkProgressTileSet();
			// Ghost — ДУБЛЬ сценового terrain-набора + одиночные Source травы/
			// грядки/мебели (Ghost*Source). Исходный сценовый набор содержит
			// ТОЛЬКО стены: рисовать зону его SourceGrass/SourceGardenBed (=0)
			// НЕЛЬЗЯ — там стены, призрак заливался стенами (баг склада).
			// Дубликат никому не мешает: wall/blueprint-слои сидят на оригинале.
			TileSet ghostTileSet = CloneTileSetWithGhostSources(wallTerrainTileSet);
			_ghostLayer.TileSet = ghostTileSet ?? wallTerrainTileSet;
			_wallLayer.TileSet = wallTerrainTileSet;
			// Чертежи стен — сценовый terrain-набор (состыковка считает движок),
			// мебель/грядки — кодовый building-набор (одиночные спрайты).
			_wallBlueprintLayer.TileSet = wallTerrainTileSet;
			_blueprintLayer.TileSet = buildingTileSet;
			_buildingLayer.TileSet = buildingTileSet;

			TileSet farmTerrainTileSet = GetGardenBedTerrainTileSet() ?? buildingTileSet;
			_farmLayer.TileSet = farmTerrainTileSet;
			_farmBlueprintLayer.TileSet = farmTerrainTileSet;
			_farmTerrain = new TerrainTileLayer(_farmLayer, 0, 0);
			_farmBlueprintTerrain = new TerrainTileLayer(_farmBlueprintLayer, 0, 0);

		// Стандарт: построенные стены и чертежи стен — terrain-слои
		// (автотайлинг считает движок). Мебель — одиночные тайлы в building-наборе.
		_builtWalls = new TerrainTileLayer(_wallLayer, WallTerrainSetId, WallTerrainId);
		_wallBlueprints = new TerrainTileLayer(_wallBlueprintLayer, WallTerrainSetId, WallTerrainId);

			if (_wallBuildManager != null)
			{
				_wallBuildManager.OnTilesUpdated -= OnWallTilesUpdated;
				_wallBuildManager.Dispose();
				_wallBuildManager = null;
			}

			_wallBuildManager = new WallBuildManager();
			_wallBuildManager.OnTilesUpdated += OnWallTilesUpdated;

			// Начальные тени статики: деревья из MapData + пустые стены/здания (их докинет Rebuild при стройке).
			ShadowRenderer?.RebuildStatic(
				mapData,
				_wallBuildManager.GetAllWalls(),
				BuildingManager.Instance.GetAllBuildings());

			OnMapApplied?.Invoke();
		}
		catch (Exception ex)
		{
			GD.PrintErr($"MapRenderer: ошибка ApplyMap: {ex.Message}");
		}
	}

	/// <summary>
	/// Снос чертежа стены: помечаем грязной саму клетку И 4 ортогональных соседа —
	/// симметрично OnBlueprintCompleted. Без соседей у оставшихся чертежей сохранялась
	/// старая состыковка (движок перерисовывал только саму клетку), и снятая стена
	/// оставляла визуальный «призрак» стыка.
	/// </summary>
	private void MarkWallBlueprintRemoved(int x, int y)
	{
		_wallBlueprints?.MarkDirty(new Vector2I(x, y));
		_wallBlueprints?.MarkDirty(new Vector2I(x + 1, y));
		_wallBlueprints?.MarkDirty(new Vector2I(x - 1, y));
		_wallBlueprints?.MarkDirty(new Vector2I(x, y + 1));
		_wallBlueprints?.MarkDirty(new Vector2I(x, y - 1));
		_pendingCells.Enqueue(new PendingCell(_blueprintLayer, new Vector2I(x, y), 0, AtlasOrigin, true));
	}

	private void OnBlueprintChanged((int X, int Y) pos, BuildingType type)
	{
		if (type == BuildingType.WoodWall)
			_wallBlueprints?.MarkDirty(new Vector2I(pos.X, pos.Y));
		else
			_pendingCells.Enqueue(new PendingCell(_blueprintLayer, new Vector2I(pos.X, pos.Y), SourceForBuilding(type), AtlasOrigin, false));
	}

	private void RefreshAllBlueprints()
	{
		if (_blueprintLayer == null) return;

		var blueprints = BlueprintManager.Instance.GetAllBlueprints();
		var farmPlots = FarmJobManager.Instance.GetAllMarkedPlots();

		_blueprintLayer.Clear();

		// Мебель — одиночные SetCell в building-слое. Стены идут только
		// через terrain-слой чертежей (_wallBlueprints.RebuildAll ниже):
		// движок сам считает состыковку чертёж↔чертёж.
		var wallCells = new List<Vector2I>();
		foreach (var (cell, bType) in blueprints)
		{
			Vector2I mapPos = new Vector2I(cell.X, cell.Y);
			if (bType == BuildingType.WoodWall)
				wallCells.Add(mapPos);
			else
				_blueprintLayer.SetCell(mapPos, SourceForBuilding(bType), AtlasOrigin);
		}
		_wallBlueprints?.RebuildAll(wallCells);

		foreach (var plot in farmPlots)
		{
			_blueprintLayer.SetCell(new Vector2I(plot.X, plot.Y), SourceGardenBed, AtlasOrigin);
		}
	}

	private void OnBlueprintCompleted((int X, int Y) pos, BuildingType type)
	{
		if (type == BuildingType.WoodWall)
		{
			// Чертёж убран из terrain-слоя (соседние чертежи пересчитаются
			// сами через exists-флаш), построенная стена — в _builtWalls.
			_wallBlueprints?.MarkDirty(new Vector2I(pos.X, pos.Y));
			_wallBlueprints?.MarkDirty(new Vector2I(pos.X + 1, pos.Y));
			_wallBlueprints?.MarkDirty(new Vector2I(pos.X - 1, pos.Y));
			_wallBlueprints?.MarkDirty(new Vector2I(pos.X, pos.Y + 1));
			_wallBlueprints?.MarkDirty(new Vector2I(pos.X, pos.Y - 1));
			_wallBuildManager.AddWall(pos.X, pos.Y);
			_builtWalls?.MarkDirty(new Vector2I(pos.X + 1, pos.Y - 1));
			_builtWalls?.MarkDirty(new Vector2I(pos.X - 1, pos.Y - 1));
			_builtWalls?.MarkDirty(new Vector2I(pos.X + 1, pos.Y + 1));
			_builtWalls?.MarkDirty(new Vector2I(pos.X - 1, pos.Y + 1));
		}
		else
		{
			// Мебель и верстак: стираем одиночное превью + один спрайт в building-слой.
			_pendingCells.Enqueue(new PendingCell(_blueprintLayer, new Vector2I(pos.X, pos.Y), 0, AtlasOrigin, true));
			BuildingManager.Instance.AddBuilding(pos.X, pos.Y, type);
		}
	}

	private void OnTreeChopped((int X, int Y) pos)
	{
		// §118 фаза 4 (аудит MapDataWriter §1, строки 1345/1359): записи в _pendingMapData
		// здесь БЫЛИ МЁРТВЫМИ — ApplyMap обнуляет _pendingMapData, поэтому `?.` их гасил.
		// Авторитетную мутацию делает сим-хендлер (TreeChoppingJobHandler пишет прямо в
		// алиас-массив MapData.TreeOnGrass), а на мир её зеркалит единый канал
		// (IWorldMutationSink/IslandMutationMirror). Рендеру остаётся только погасить
		// спрайт и тень.
		ShadowRenderer?.RemoveCaster(pos.X, pos.Y);
		_choppedTreesQueue.Enqueue(new Vector2I(pos.X, pos.Y));
	}

	private void OnStoneMined((int X, int Y) pos)
	{
		// См. комментарий в OnTreeChopped: запись в _pendingMapData была мёртвой.
		// Россыпь гасит спрайт; симуляционный флаг снимает MiningJobHandler, зеркалит фасад.
		ShadowRenderer?.RemoveCaster(pos.X, pos.Y);
		_choppedTreesQueue.Enqueue(new Vector2I(pos.X, pos.Y));
	}

	private TileSet CreateGroundTileSet()
	{
		var tileSet = new TileSet { TileSize = new Vector2I(TileSizePx, TileSizePx) };
		AddGroundAtlasSource(tileSet, TextureGrass, "grass", SourceGrass);
		AddGroundAtlasSource(tileSet, TextureWater, "water", SourceWater);
		AddGroundAtlasSource(tileSet, TextureSand, "sand", SourceSand);
		return tileSet;
	}

	private void AddGroundAtlasSource(TileSet tileSet, string uid, string name, int sourceId)
	{
		var tex = LoadTexture(uid, name);
		if (tex == null) return;

		var src = new TileSetAtlasSource { Texture = tex, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
		int cols = Math.Max(1, tex.GetWidth() / TileSizePx);
		int rows = Math.Max(1, tex.GetHeight() / TileSizePx);
		for (int r = 0; r < rows; r++)
		{
			for (int c = 0; c < cols; c++)
			{
				src.CreateTile(new Vector2I(c, r));
			}
		}
		tileSet.AddSource(src, sourceId);
	}

	private static Vector2I[] GetAtlasTileCoords(TileSet tileSet, int sourceId)
	{
		if (tileSet == null || !tileSet.HasSource(sourceId))
			return new[] { Vector2I.Zero };
		var src = tileSet.GetSource(sourceId) as TileSetAtlasSource;
		if (src == null || src.GetTilesCount() == 0)
			return new[] { Vector2I.Zero };
		int count = src.GetTilesCount();
		var coords = new Vector2I[count];
		for (int i = 0; i < count; i++)
			coords[i] = src.GetTileId(i);
		return coords;
	}

	private TileSet CreateObjectTileSet()
	{
		var tileSet = new TileSet { TileSize = new Vector2I(TileSizePx, TileSizePx) };

		// 1. Композитный объединенный атлас 512x64 (PLAN.md §36):
		// [0..3]: 4 варианта деревьев (tree, tree_1, tree_2, tree_3)
		// [4..7]: 4 варианта камней (4 квадранта 2x2 из stone.png)
		// Все 70 000 объектов слоя делят ОДНУ текстуру, исключая разрывы батчинга.
		var atlasImg = Image.CreateEmpty(TileSizePx * 8, TileSizePx, false, Image.Format.Rgba8);

		Texture2D[] treeTextures = {
			LoadTexture(TextureTree0, "tree"),
			LoadTexture(TextureTree1, "tree_1"),
			LoadTexture(TextureTree2, "tree_2"),
			LoadTexture(TextureTree3, "tree_3")
		};

		for (int v = 0; v < 4; v++)
		{
			var img = treeTextures[v]?.GetImage();
			if (img != null)
			{
				atlasImg.BlitRect(img, new Rect2I(0, 0, TileSizePx, TileSizePx), new Vector2I(v * TileSizePx, 0));
			}
		}

		var stoneTex = LoadTexture(TextureStone, "stone");
		var stoneImg = stoneTex?.GetImage();
		if (stoneImg != null)
		{
			for (int v = 0; v < 4; v++)
			{
				var (qx, qy) = UnifiedWorldGenerator.StoneAtlasFor(v);
				atlasImg.BlitRect(stoneImg, new Rect2I(qx * TileSizePx, qy * TileSizePx, TileSizePx, TileSizePx), new Vector2I((4 + v) * TileSizePx, 0));
			}
		}

		var compositeSource = new TileSetAtlasSource
		{
			Texture = ImageTexture.CreateFromImage(atlasImg),
			TextureRegionSize = new Vector2I(TileSizePx, TileSizePx)
		};
		for (int i = 0; i < 8; i++)
		{
			compositeSource.CreateTile(new Vector2I(i, 0));
		}
		tileSet.AddSource(compositeSource, SourceObjectComposite);

		// Обратная совместимость: регистрация отдельных SourceTree/SourceStone для внешних вызовов
		int[] treeSources = { SourceTree0, SourceTree1, SourceTree2, SourceTree3 };
		for (int v = 0; v < 4; v++)
		{
			if (treeTextures[v] != null)
			{
				var s = new TileSetAtlasSource { Texture = treeTextures[v], TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
				s.CreateTile(Vector2I.Zero);
				tileSet.AddSource(s, treeSources[v]);
			}
		}

		if (stoneTex != null)
		{
			int[] stoneSources = { SourceStone0, SourceStone1, SourceStone2, SourceStone3 };
			for (int v = 0; v < 4; v++)
			{
				var (qx, qy) = UnifiedWorldGenerator.StoneAtlasFor(v);
				var quadrant = new AtlasTexture
				{
					Atlas = stoneTex,
					Region = new Rect2(qx * TileSizePx, qy * TileSizePx, TileSizePx, TileSizePx)
				};
				var s = new TileSetAtlasSource
				{
					Texture = quadrant,
					TextureRegionSize = new Vector2I(TileSizePx, TileSizePx)
				};
				s.CreateTile(Vector2I.Zero);
				tileSet.AddSource(s, stoneSources[v]);
			}
		}

		return tileSet;
	}

	/// <summary>
	/// SourceId спрайта дерева по варианту 0..3. Неизвестный вариант -> SourceTree0.
	/// </summary>
	public static int GetTreeSourceId(int variant)
	{
		return variant switch
		{
			1 => SourceTree1,
			2 => SourceTree2,
			3 => SourceTree3,
			_ => SourceTree0,
		};
	}

	/// <summary>
	/// SourceId спрайта камня по варианту 0..3 (квадранты stone.png 2x2).
	/// </summary>
	public static int GetStoneSourceId(int variant)
	{
		return (variant & 3) switch
		{
			1 => SourceStone1,
			2 => SourceStone2,
			3 => SourceStone3,
			_ => SourceStone0,
		};
	}

	/// <summary>
	/// SourceId слоя зданий по типу постройки. Стены идут terrain-слоем
	/// (этот Source здесь не используется — для единообразия маппинга).
	/// </summary>
	public static int SourceForBuilding(BuildingType type)
	{
		return type switch
		{
			BuildingType.WorkTable => SourceWorkTable,
			BuildingType.Bed => SourceBed,
			BuildingType.Bench => SourceBench,
			BuildingType.Nightstand => SourceNightstand,
			BuildingType.CentralBlock => SourceCentralBlock,
			_ => SourceWall,
		};
	}

	private TileSet CreateMountainTileSet()
	{
		var tileSet = new TileSet { TileSize = new Vector2I(TileSizePx, TileSizePx) };
		var mountainTex = LoadTexture(TextureMountain, "mountains");
		if (mountainTex == null)
		{
			GD.PrintErr("MapRenderer: текстура гор (uid://de7mit41ukuoq) не найдена!");
			return tileSet;
		}
		var src = new TileSetAtlasSource { Texture = mountainTex, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
		// Атлас 5×5: используются только 4×4 (последний столбец x=4 и строка y=4 — пустые).
		for (int ty = 0; ty < 4; ty++)
			for (int tx = 0; tx < 4; tx++)
				src.CreateTile(new Vector2I(tx, ty));
		tileSet.AddSource(src, SourceMountain);
		return tileSet;
	}

	private TileSet CreateBuildingTileSet()
	{
		var tileSet = new TileSet { TileSize = new Vector2I(TileSizePx, TileSizePx) };

		var wallTexture = LoadTexture(TextureWoodWall, "wooden_wall");
		if (wallTexture != null)
		{
			var wallAtlas = new TileSetAtlasSource { Texture = wallTexture, TextureRegionSize = new Vector2I(WallAtlasTilePx, WallAtlasTilePx) };
			// Атлас стен 7×7 (448×448). Клетки без terrain-разметки в TileSet не создаём —
			// SetCell на несуществующий тайл молча не отрисуется, а WallTileHelper их не возвращает.
			for (int ty = 0; ty < WallAtlasRows; ty++)
				for (int tx = 0; tx < WallAtlasColumns; tx++)
				{
					if (!WallTileHelper.IsAtlasTileUsed(tx, ty)) continue;
					wallAtlas.CreateTile(new Vector2I(tx, ty));
				}

			tileSet.AddSource(wallAtlas, SourceWall);
		}

		var tableTexture = LoadTexture(TextureWorkTable, "work_table");
		if (tableTexture != null)
		{
			var tableSource = new TileSetAtlasSource { Texture = tableTexture, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
			tableSource.CreateTile(Vector2I.Zero);
			tileSet.AddSource(tableSource, SourceWorkTable);
		}

		// Мебель 64x64 одиночными спрайтами (кровать/скамья/тумбочка).
		AddSingleTile(tileSet, TextureBed, "bed", SourceBed);
		AddSingleTile(tileSet, TextureBench, "bench", SourceBench);
		AddSingleTile(tileSet, TextureNightstand, "nightstand", SourceNightstand);
		// §119 P5: центральный блок — тем же одиночным спрайтом.
		AddSingleTile(tileSet, TextureCentralBlock, "central_block", SourceCentralBlock);

		var gardenBedTexture = LoadTexture(TextureGardenBed, "garden_beds");
		if (gardenBedTexture != null)
		{
			var bedSource = new TileSetAtlasSource { Texture = gardenBedTexture, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
			bedSource.CreateTile(Vector2I.Zero);
			tileSet.AddSource(bedSource, SourceGardenBed);
		}

		return tileSet;
	}

	/// <summary>
	/// Один спрайт 64x64 отдельным Source (мебель). Молча пропускает,
	/// если текстура не загрузилась — слой просто не отрисует этот тип.
	/// </summary>
	private void AddSingleTile(TileSet tileSet, string textureUid, string name, int sourceId)
	{
		var tex = LoadTexture(textureUid, name);
		if (tex == null)
		{
			GD.PrintErr($"MapRenderer: текстура '{name}' ({textureUid}) не найдена!");
			return;
		}
		var src = new TileSetAtlasSource { Texture = tex, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
		src.CreateTile(Vector2I.Zero);
		tileSet.AddSource(src, sourceId);
	}

	/// <summary>
	/// Дубликат сценового wall-TileSet + одиночные ghost-Source (трава/грядка/
	/// мебель) под константами Ghost*Source. Клон нужен, т.к. исходный набор
	/// из сцены AddSource не принимает (чужой ресурс), а номера 0/6 в нём
	/// уже заняты стенами. Null — клонировать нечего (нет сценового набора).
	/// </summary>
	private TileSet CloneTileSetWithGhostSources(TileSet wallTerrainTileSet)
	{
		if (wallTerrainTileSet == null)
			return null;
		var ghost = wallTerrainTileSet.Duplicate() as TileSet;
		if (ghost == null)
			return null;
		ghost.TileSize = new Vector2I(TileSizePx, TileSizePx);
		AddGhostSingleTile(ghost, TextureGrass, "ghost_grass", GhostGrassSource);
		AddGhostSingleTile(ghost, TextureGardenBed, "ghost_garden", GhostGardenBedSource);
		AddGhostStockpileTile(ghost, GhostStockpileSource);
		AddGhostSingleTile(ghost, TextureWorkTable, "ghost_table", GhostWorkTableSource);
		AddGhostSingleTile(ghost, TextureBed, "ghost_bed", GhostBedSource);
		AddGhostSingleTile(ghost, TextureBench, "ghost_bench", GhostBenchSource);
		AddGhostSingleTile(ghost, TextureNightstand, "ghost_nightstand", GhostNightstandSource);
		AddGhostSingleTile(ghost, TextureCentralBlock, "ghost_central_block", GhostCentralBlockSource);
		return ghost;
	}

	private void AddGhostStockpileTile(TileSet tileSet, int sourceId)
	{
		if (tileSet.HasSource(sourceId))
			return;
		var img = Image.CreateEmpty(TileSizePx, TileSizePx, false, Image.Format.Rgba8);
		var fillColor = new Color(0.65f, 0.15f, 0.9f, 0.55f);
		var borderColor = new Color(0.85f, 0.35f, 1f, 0.95f);
		for (int y = 0; y < TileSizePx; y++)
		{
			for (int x = 0; x < TileSizePx; x++)
			{
				bool isBorder = x <= 1 || x >= TileSizePx - 2 || y <= 1 || y >= TileSizePx - 2;
				img.SetPixel(x, y, isBorder ? borderColor : fillColor);
			}
		}
		var tex = ImageTexture.CreateFromImage(img);
		var src = new TileSetAtlasSource { Texture = tex, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
		src.CreateTile(Vector2I.Zero);
		tileSet.AddSource(src, sourceId);
	}

	private void AddGhostSingleTile(TileSet tileSet, string textureUid, string name, int sourceId)
	{
		if (tileSet.HasSource(sourceId))
			return;
		var tex = LoadTexture(textureUid, name);
		if (tex == null)
			return;
		var src = new TileSetAtlasSource { Texture = tex, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
		src.CreateTile(Vector2I.Zero);
		tileSet.AddSource(src, sourceId);
	}

	/// <summary>
	/// SourceId ghost-призрака для зоны ("warehouse" — фиолетовая складская зона, остальное — грядка).
	/// </summary>
	public static int GhostSourceForZone(string zoneKind) =>
		zoneKind == "warehouse" ? GhostStockpileSource : GhostGardenBedSource;

	/// <summary>
	/// SourceId ghost-призрака для постройки (стены идут terrain'ом и здесь
	/// не нужны — для них PaintWallGhost; мебель — одиночные Ghost*Source).
	/// -1 — рисовать нечем (стены/неизвестное).
	/// </summary>
	public static int GhostSourceForBuilding(BuildingType type)
	{
		return type switch
		{
			BuildingType.WorkTable => GhostWorkTableSource,
			BuildingType.Bed => GhostBedSource,
			BuildingType.Bench => GhostBenchSource,
			BuildingType.Nightstand => GhostNightstandSource,
			BuildingType.CentralBlock => GhostCentralBlockSource,
			_ => -1,
		};
	}

	/// <summary>
	/// TileSet с terrain-разметкой стен из сцены (нода wood_wall_tile в Main.tscn).
	/// Нужен ghost-слою: BuildTool рисует превью движковым SetCellsTerrainConnect.
	/// Возвращает null, если нода не найдена (например, MapRenderer создан кодом в тесте).
	/// </summary>
	private static TileSet _cachedWallTerrainTileSet;
	private static TileSet _cachedMountainTerrainTileSet;
	private static TileSet _cachedGardenBedTerrainTileSet;

	public static void ClearTileSetCaches()
	{
		_cachedWallTerrainTileSet = null;
		_cachedMountainTerrainTileSet = null;
		_cachedGardenBedTerrainTileSet = null;
	}

	public static TileSet GetWallTerrainTileSet()
	{
		if (_cachedWallTerrainTileSet != null && GodotObject.IsInstanceValid(_cachedWallTerrainTileSet))
			return _cachedWallTerrainTileSet;
		var tree = Engine.GetMainLoop() as SceneTree;
		var layer = (tree?.CurrentScene ?? tree?.Root)?.FindChild("wood_wall_tile", true, false) as TileMapLayer;
		_cachedWallTerrainTileSet = layer?.TileSet;
		return _cachedWallTerrainTileSet;
	}

	/// <summary>
	/// TileSet с terrain-разметкой гор из сцены (нода mountains_tile в Main.tscn,
	/// terrain_set "mountain0"). Стандарт: генерация гор в MapData, отрисовку
	/// атласа считает движок. Null — нода не найдена, fallback на кодовый TileSet.
	/// </summary>
	public static TileSet GetMountainTerrainTileSet()
	{
		if (_cachedMountainTerrainTileSet != null && GodotObject.IsInstanceValid(_cachedMountainTerrainTileSet))
			return _cachedMountainTerrainTileSet;
		var tree = Engine.GetMainLoop() as SceneTree;
		var layer = (tree?.CurrentScene ?? tree?.Root)?.FindChild("mountains_tile", true, false) as TileMapLayer;
		_cachedMountainTerrainTileSet = layer?.TileSet;
		return _cachedMountainTerrainTileSet;
	}

	/// <summary>
	/// TileSet с terrain-разметкой грядок из сцены (нода GardenBeds в Main.tscn).
	/// Автотайлинг грядок считает сам Godot через SetCellsTerrainConnect.
	/// </summary>
	public static TileSet GetGardenBedTerrainTileSet()
	{
		if (_cachedGardenBedTerrainTileSet != null && GodotObject.IsInstanceValid(_cachedGardenBedTerrainTileSet))
			return _cachedGardenBedTerrainTileSet;
		var tree = Engine.GetMainLoop() as SceneTree;
		var layer = (tree?.CurrentScene ?? tree?.Root)?.FindChild("GardenBeds", true, false) as TileMapLayer;
		_cachedGardenBedTerrainTileSet = layer?.TileSet;
		return _cachedGardenBedTerrainTileSet;
	}

	private void OnWallTilesUpdated(List<(int X, int Y)> changedTiles)
	{
		if (_wallLayer?.TileSet == null) return;

		// Построенные стены — через terrain-слой (стандарт). Тени сразу (дешёвый dirty-флаг).
		// exists=IsWallAt: соседние пустые клетки движок использует только как контекст,
		// SetCellsTerrainConnect сам решит что стереть/оставить — EraseCell здесь НЕ зовём.
		foreach (var (x, y) in changedTiles)
		{
			_builtWalls?.MarkDirty(new Vector2I(x, y));
			if (_wallBuildManager.IsWallAt(x, y))
				ShadowRenderer?.AddCaster(x, y);
			else
				ShadowRenderer?.RemoveCaster(x, y);
		}
	}

	private Texture2D LoadTexture(string pathOrUid, string name)
	{
		try
		{
			return ResourceLoader.Load<Texture2D>(pathOrUid);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"MapRenderer: ошибка загрузки '{name}': {ex.Message}");
			return null;
		}
	}

	public override void _ExitTree()
	{
		TreeJobManager.Instance.OnTreeChopped -= OnTreeChopped;
		StoneJobManager.Instance.OnStoneMined -= OnStoneMined;
		StockpileManager.Instance.OnZoneTileAdded -= _onZoneTileAdded;
		StockpileManager.Instance.OnZoneTileRemoved -= _onZoneTileRemoved;
		StockpileManager.Instance.OnZoneTilesBatchAdded -= _onZoneTilesBatchAdded;
		StockpileManager.Instance.OnZoneTilesBatchRemoved -= _onZoneTilesBatchRemoved;

		BuildingManager.Instance.OnBuildingPlaced -= _onBuildingPlaced;
		BuildingManager.Instance.OnBuildingRemoved -= _onBuildingRemoved;

		FarmJobManager.Instance.OnPlotMarked -= _onPlotMarked;
		FarmJobManager.Instance.OnPlotsBatchMarked -= _onPlotsBatchMarked;
		FarmJobManager.Instance.OnPlotUnmarked -= _onPlotUnmarked;
		FarmJobManager.Instance.OnPlotsBatchUnmarked -= _onPlotsBatchUnmarked;
		FarmJobManager.Instance.OnPlotCompleted -= _onPlotCompleted;

		if (_wallBuildManager != null)
		{
			_wallBuildManager.OnTilesUpdated -= OnWallTilesUpdated;
			_wallBuildManager.Dispose();
			_wallBuildManager = null;
		}

		base._ExitTree();
	}
}
