using Godot;
using Game.Core;
using Game.Simulation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    public const int MapWidth = 512;
    public const int MapHeight = 512;
    public const int TileSizePx = 64;

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
    // Ghost-призрак: TileSet _ghostLayer — ДУБЛЬ сценового wall-набора с
    // добавленными одиночными Source (трава/грядка/мебель). Id взяты с запасом
    // от сценовых (там максимум ~16), чтобы не пересекаться с terrain-источниками.
    // Зону рисуем ТОЛЬКО этими константами (НЕ SourceGrass/SourceGardenBed —
    // их номера в ghost-наборе заняты стенами, иначе призрак = стены).
    public const int GhostGrassSource      = 100;
    public const int GhostGardenBedSource  = 101;
    public const int GhostWorkTableSource  = 105;
    public const int GhostBedSource        = 114;
    public const int GhostBenchSource      = 115;
    public const int GhostNightstandSource = 116;

    /// <summary>
    /// Сид карты. 0 = случайный каждый запуск, иначе фиксированный (для дебага/повторов).
    /// Задаётся в инспекторе Godot у MapRenderer.
    /// </summary>
    [Export] public int SeedOverride = 0;

    public static uint CurrentMapSeed { get; private set; }

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

    private const int WallAtlasColumns = 7;
    private const int WallAtlasRows = 7;
    private const int WallAtlasTilePx = 64;

    private static readonly Vector2I AtlasOrigin = Vector2I.Zero;

    private TileMapLayer _groundLayer;
    private TileMapLayer _mountainLayer;
    private TileMapLayer _farmLayer;
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

    private Action<(int X, int Y), BuildingType> _onBlueprintAdded;
    private Action<List<(int X, int Y)>, BuildingType> _onBlueprintsBatchAdded;
    private Action<(int X, int Y)> _onBlueprintRemoved;
    private Action<List<(int X, int Y)>> _onBlueprintsBatchRemoved;
    private Action<(int X, int Y), BuildingType> _onBlueprintCompleted;

    private Action<(int X, int Y), BuildingType> _onBuildingPlaced;
    private Action<(int X, int Y)> _onBuildingRemoved;

    private Action<(int X, int Y)> _onPlotMarked;
    private Action<List<(int X, int Y)>> _onPlotsBatchMarked;
    private Action<(int X, int Y)> _onPlotUnmarked;
    private Action<List<(int X, int Y)>> _onPlotsBatchUnmarked;
    private Action<(int X, int Y)> _onPlotCompleted;

    public MapData MapData => _pendingMapData;
    public HumidityMap Humidity => _pendingMapData?.Humidity;
    public FertilityMap Fertility => _pendingMapData?.Fertility;
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

        var bg = new ColorRect
        {
            Name = "Background",
            Color = new Color(0f, 0f, 0f),
            Size = new Vector2(MapWidth * TileSizePx, MapHeight * TileSizePx),
            Position = Vector2.Zero,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        AddChild(bg);

        _groundLayer = new TileMapLayer { Name = "GroundLayer" };
        AddChild(_groundLayer);

        _mountainLayer = new TileMapLayer { Name = "MountainLayer" };
        AddChild(_mountainLayer);

        _farmLayer = new TileMapLayer { Name = "FarmLayer" };
        AddChild(_farmLayer);

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
            Modulate = new Color(0.65f, 0.15f, 0.9f, 0.45f)
        };
        AddChild(_stockpileLayer);

        _objectLayer = new TileMapLayer { Name = "ObjectLayer" };
        AddChild(_objectLayer);

        _buildingLayer = new TileMapLayer { Name = "BuildingLayer" };
        AddChild(_buildingLayer);

        _blueprintLayer = new TileMapLayer
        {
            Name = "BlueprintLayer",
            Modulate = new Color(1f, 1f, 1f, 0.5f)
        };
        AddChild(_blueprintLayer);

        // Чертежи стен: отдельный полупрозрачный terrain-слой на том же
        // сценовом наборе — движок сам считает состыковку чертёж↔чертёж
        // через SetCellsTerrainConnect. Мебель/грядки остаются в _blueprintLayer.
        _wallBlueprintLayer = new TileMapLayer
        {
            Name = "WallBlueprintLayer",
            Modulate = new Color(1f, 1f, 1f, 0.5f)
        };
        AddChild(_wallBlueprintLayer);

        _wallLayer = new TileMapLayer { Name = "WoodWallLayer" };
        AddChild(_wallLayer);

        _ghostLayer = new TileMapLayer
        {
            Name = "WallGhostLayer",
            Modulate = new Color(1f, 1f, 1f, 0.4f)
        };
        AddChild(_ghostLayer);

        _designationRenderer = new DesignationRenderer { Name = "DesignationRenderer" };
        AddChild(_designationRenderer);

        // Слой прогресса работ (ProcessOfWork 3×2): поверх стен/чертежей,
        // полупрозрачный. TileSet — кодовый (сцену не трогаем).
        _workProgressLayer = new TileMapLayer
        {
            Name = "WorkProgressLayer",
            Modulate = new Color(1f, 1f, 1f, 0.85f)
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

        _onZoneTileAdded = pos => _pendingCells.Enqueue(new PendingCell(_stockpileLayer, new Vector2I(pos.X, pos.Y), SourceGrass, AtlasOrigin, false));
        _onZoneTileRemoved = pos => _pendingCells.Enqueue(new PendingCell(_stockpileLayer, new Vector2I(pos.X, pos.Y), 0, AtlasOrigin, true));
        _onZoneTilesBatchAdded = arr => { foreach (var pos in arr) _pendingCells.Enqueue(new PendingCell(_stockpileLayer, new Vector2I(pos.X, pos.Y), SourceGrass, AtlasOrigin, false)); };
        _onZoneTilesBatchRemoved = arr => { foreach (var pos in arr) _pendingCells.Enqueue(new PendingCell(_stockpileLayer, new Vector2I(pos.X, pos.Y), 0, AtlasOrigin, true)); };
        StockpileManager.Instance.OnZoneTileAdded += _onZoneTileAdded;
        StockpileManager.Instance.OnZoneTileRemoved += _onZoneTileRemoved;
        StockpileManager.Instance.OnZoneTilesBatchAdded += _onZoneTilesBatchAdded;
        StockpileManager.Instance.OnZoneTilesBatchRemoved += _onZoneTilesBatchRemoved;

        _onBlueprintAdded = OnBlueprintChanged;
        _onBlueprintsBatchAdded = (list, type) => QueueBlueprintRefresh();
        _onBlueprintRemoved = pos => QueueBlueprintRefresh();
        _onBlueprintsBatchRemoved = list => QueueBlueprintRefresh();
        _onBlueprintCompleted = OnBlueprintCompleted;

        BlueprintManager.Instance.OnBlueprintAdded += _onBlueprintAdded;
        BlueprintManager.Instance.OnBlueprintsBatchAdded += _onBlueprintsBatchAdded;
        BlueprintManager.Instance.OnBlueprintRemoved += _onBlueprintRemoved;
        BlueprintManager.Instance.OnBlueprintsBatchRemoved += _onBlueprintsBatchRemoved;
        BlueprintManager.Instance.OnBlueprintCompleted += _onBlueprintCompleted;

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

        _onPlotMarked = pos => _pendingCells.Enqueue(new PendingCell(_blueprintLayer, new Vector2I(pos.X, pos.Y), SourceGardenBed, AtlasOrigin, false));
        _onPlotsBatchMarked = list => QueueBlueprintRefresh();
        _onPlotUnmarked = pos => _pendingCells.Enqueue(new PendingCell(_blueprintLayer, new Vector2I(pos.X, pos.Y), 0, AtlasOrigin, true));
        _onPlotsBatchUnmarked = list => QueueBlueprintRefresh();
        _onPlotCompleted = pos =>
        {
            _pendingCells.Enqueue(new PendingCell(_blueprintLayer, new Vector2I(pos.X, pos.Y), 0, AtlasOrigin, true));
            _pendingCells.Enqueue(new PendingCell(_farmLayer, new Vector2I(pos.X, pos.Y), SourceGardenBed, AtlasOrigin, false));
        };

        FarmJobManager.Instance.OnPlotMarked += _onPlotMarked;
        FarmJobManager.Instance.OnPlotsBatchMarked += _onPlotsBatchMarked;
        FarmJobManager.Instance.OnPlotUnmarked += _onPlotUnmarked;
        FarmJobManager.Instance.OnPlotsBatchUnmarked += _onPlotsBatchUnmarked;
        FarmJobManager.Instance.OnPlotCompleted += _onPlotCompleted;

        Task.Run(() =>
        {
            uint seed = SeedOverride != 0
                ? unchecked((uint)SeedOverride)
                : unchecked((uint)Random.Shared.Next(int.MinValue, int.MaxValue));
            CurrentMapSeed = seed;

            // GPU-трек удалён: генерация карты — чистый CPU (NoiseGenerator fBm).
            _pendingMapData = MapGenerator.Generate(MapWidth, MapHeight, seed);

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
        // Flush батча клеток: ограниченный бюджет за кадр, дубли схлопнутся сами
        // т.к. события идут потоком, а TileMap трогаем здесь.
        int cellsBudget = MaxCellsPerFrame;
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
        _builtWalls?.Flush(c => _wallBuildManager != null && _wallBuildManager.IsWallAt(c.X, c.Y));
        _wallBlueprints?.Flush(c => BlueprintManager.Instance.IsWallBlueprintAt(c.X, c.Y));
        _mountains?.Flush(c => _pendingMapData != null && (uint)c.X < (uint)_pendingMapData.Width && (uint)c.Y < (uint)_pendingMapData.Height && _pendingMapData.Ground[c.X, c.Y] == TileType.Mountain);
        FlushWorkProgress(delta);
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

    private void ApplyMap()
    {
        try
        {
            MapData mapData = _pendingMapData;
            if (mapData == null) return;

            TileSet groundTileSet = CreateGroundTileSet();
            _groundLayer.TileSet = groundTileSet;
            _stockpileLayer.TileSet = groundTileSet;

            TileSet objectTileSet = CreateObjectTileSet();
            _objectLayer.TileSet = objectTileSet;

            // Россыпи камня стираем из object-слоя по событию добычи
            // (близнец OnTreeChopped) — клетки копим в тот же батч-очередь.
            StoneJobManager.Instance.OnStoneMined += OnStoneMined;

            // Горы — terrain-слой (стандарт): TileSet с разметкой из сцены
            // (mountains_tile в Main.tscn), атлас подбирает движок.
            TileSet mountainTerrainTileSet = GetMountainTerrainTileSet() ?? CreateMountainTileSet();
            _mountainLayer.TileSet = mountainTerrainTileSet;
            _mountains = new TerrainTileLayer(_mountainLayer, MountainTerrainSetId, MountainTerrainId);

            var mountainCells = new List<Vector2I>();
            for (int x = 0; x < MapWidth; x++)
            {
                for (int y = 0; y < MapHeight; y++)
                {
                    var pos = new Vector2I(x, y);
                    if (mapData.Ground[x, y] == TileType.Mountain)
                    {
                        _groundLayer.SetCell(pos, SourceGrass, AtlasOrigin);
                        mountainCells.Add(pos);
                    }
                    else if (mapData.Ground[x, y] == TileType.Grass)
                    {
                        _groundLayer.SetCell(pos, SourceGrass, AtlasOrigin);
                        if (mapData.TreeOnGrass[x, y])
                        {
                            int sourceId = GetTreeSourceId(mapData.GetTreeVariant(x, y));
                            _objectLayer.SetCell(pos, sourceId, AtlasOrigin);
                        }
                        else if (mapData.StoneOnGrass[x, y])
                        {
                            // Россыпь камня: вариант 0..3 из stone.png 128x128,
                            // детерминирован сидом карты (тот же при перезапуске).
                            int variant = MapGenerator.StoneVariantFor(x, y, mapData.Seed);
                            _objectLayer.SetCell(pos, GetStoneSourceId(variant), AtlasOrigin);
                        }
                    }
                    else
                    {
                        _groundLayer.SetCell(pos, SourceWater, AtlasOrigin);
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
            _farmLayer.TileSet = buildingTileSet;

        // Стандарт: построенные стены и чертежи стен — terrain-слои
        // (автотайлинг считает движок). Мебель — одиночные тайлы в building-наборе.
        _builtWalls = new TerrainTileLayer(_wallLayer, WallTerrainSetId, WallTerrainId);
        _wallBlueprints = new TerrainTileLayer(_wallBlueprintLayer, WallTerrainSetId, WallTerrainId);

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

    private void OnBlueprintChanged((int X, int Y) pos, BuildingType type)
    {
        if (type == BuildingType.WoodWall)
            _wallBlueprints?.MarkDirty(new Vector2I(pos.X, pos.Y));
        else
            QueueBlueprintRefresh();
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
        if (_pendingMapData?.TreeOnGrass != null)
        {
            _pendingMapData.TreeOnGrass[pos.X, pos.Y] = false;
        }

        ShadowRenderer?.RemoveCaster(pos.X, pos.Y);
        _choppedTreesQueue.Enqueue(new Vector2I(pos.X, pos.Y));
    }

    private void OnStoneMined((int X, int Y) pos)
    {
        // Россыпь добыта: гасим и симуляционный флаг (чтобы вскопка/валидатор
        // не считали клетку занятой), и стираем спрайт из object-слоя.
        if (_pendingMapData?.StoneOnGrass != null)
        {
            if ((uint)pos.X < (uint)_pendingMapData.Width && (uint)pos.Y < (uint)_pendingMapData.Height)
                _pendingMapData.StoneOnGrass[pos.X, pos.Y] = false;
        }

        ShadowRenderer?.RemoveCaster(pos.X, pos.Y);
        _choppedTreesQueue.Enqueue(new Vector2I(pos.X, pos.Y));
    }

    private TileSet CreateGroundTileSet()
    {
        var tileSet = new TileSet { TileSize = new Vector2I(TileSizePx, TileSizePx) };
        var grassTex = LoadTexture(TextureGrass, "grass");
        if (grassTex != null)
        {
            var src = new TileSetAtlasSource { Texture = grassTex, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
            src.CreateTile(Vector2I.Zero);
            tileSet.AddSource(src, SourceGrass);
        }
        var waterTex = LoadTexture(TextureWater, "water");
        if (waterTex != null)
        {
            var src = new TileSetAtlasSource { Texture = waterTex, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
            src.CreateTile(Vector2I.Zero);
            tileSet.AddSource(src, SourceWater);
        }
        return tileSet;
    }

    private TileSet CreateObjectTileSet()
    {
        var tileSet = new TileSet { TileSize = new Vector2I(TileSizePx, TileSizePx) };
        var t0 = LoadTexture(TextureTree0, "tree");
        if (t0 != null)
        {
            var s0 = new TileSetAtlasSource { Texture = t0, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
            s0.CreateTile(Vector2I.Zero);
            tileSet.AddSource(s0, SourceTree0);
        }
        var t1 = LoadTexture(TextureTree1, "tree_1");
        if (t1 != null)
        {
            var s1 = new TileSetAtlasSource { Texture = t1, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
            s1.CreateTile(Vector2I.Zero);
            tileSet.AddSource(s1, SourceTree1);
        }
        var t2 = LoadTexture(TextureTree2, "tree_2");
        if (t2 != null)
        {
            var s2 = new TileSetAtlasSource { Texture = t2, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
            s2.CreateTile(Vector2I.Zero);
            tileSet.AddSource(s2, SourceTree2);
        }
        else
        {
            GD.PrintErr("MapRenderer: текстура tree_2 (uid://do5qth6ieq2q4) не найдена!");
        }
        var t3 = LoadTexture(TextureTree3, "tree_3");
        if (t3 != null)
        {
            var s3 = new TileSetAtlasSource { Texture = t3, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };
            s3.CreateTile(Vector2I.Zero);
            tileSet.AddSource(s3, SourceTree3);
        }
        else
        {
            GD.PrintErr("MapRenderer: текстура tree_3 (uid://n3xkro0lffbt) не найдена!");
        }
        // Камень stone.png 128x128: 4 квадранта 2x2 по 64px — каждый своим Source
        // (как 4 варианта деревьев). Режем через AtlasTexture с регионом
        // квадранта: движок сам нарежет Source по 64 из этой 64-картинки.
        var stoneTex = LoadTexture(TextureStone, "stone");
        if (stoneTex != null)
        {
            int[] stoneSources = { SourceStone0, SourceStone1, SourceStone2, SourceStone3 };
            for (int v = 0; v < 4; v++)
            {
                var (qx, qy) = MapGenerator.StoneAtlasFor(v);
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
        else
        {
            GD.PrintErr("MapRenderer: текстура камня (uid://du6ur8mvtu3le) не найдена!");
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
        AddGhostSingleTile(ghost, TextureWorkTable, "ghost_table", GhostWorkTableSource);
        AddGhostSingleTile(ghost, TextureBed, "ghost_bed", GhostBedSource);
        AddGhostSingleTile(ghost, TextureBench, "ghost_bench", GhostBenchSource);
        AddGhostSingleTile(ghost, TextureNightstand, "ghost_nightstand", GhostNightstandSource);
        return ghost;
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
    /// SourceId ghost-призрака для зоны ("warehouse" — трава, остальное — грядка).
    /// </summary>
    public static int GhostSourceForZone(string zoneKind) =>
        zoneKind == "warehouse" ? GhostGrassSource : GhostGardenBedSource;

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
            _ => -1,
        };
    }

    /// <summary>
    /// TileSet с terrain-разметкой стен из сцены (нода wood_wall_tile в Main.tscn).
    /// Нужен ghost-слою: BuildTool рисует превью движковым SetCellsTerrainConnect.
    /// Возвращает null, если нода не найдена (например, MapRenderer создан кодом в тесте).
    /// </summary>
    public static TileSet GetWallTerrainTileSet()
    {
        var layer = (Engine.GetMainLoop() as SceneTree)?.Root?.FindChild("wood_wall_tile", true, false) as TileMapLayer;
        return layer?.TileSet;
    }

    /// <summary>
    /// TileSet с terrain-разметкой гор из сцены (нода mountains_tile в Main.tscn,
    /// terrain_set "mountain0"). Стандарт: генерация гор в MapData, отрисовку
    /// атласа считает движок. Null — нода не найдена, fallback на кодовый TileSet.
    /// </summary>
    public static TileSet GetMountainTerrainTileSet()
    {
        var layer = (Engine.GetMainLoop() as SceneTree)?.Root?.FindChild("mountains_tile", true, false) as TileMapLayer;
        return layer?.TileSet;
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

        BlueprintManager.Instance.OnBlueprintAdded -= _onBlueprintAdded;
        BlueprintManager.Instance.OnBlueprintsBatchAdded -= _onBlueprintsBatchAdded;
        BlueprintManager.Instance.OnBlueprintRemoved -= _onBlueprintRemoved;
        BlueprintManager.Instance.OnBlueprintsBatchRemoved -= _onBlueprintsBatchRemoved;
        BlueprintManager.Instance.OnBlueprintCompleted -= _onBlueprintCompleted;

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
        }

        base._ExitTree();
    }
}