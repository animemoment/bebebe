using Godot;
using Game.Core;
using Game.Simulation;
using Game.Simulation.Gpu;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Game.UI;

/// <summary>Режим просмотра карты (оверлеи поверх основных тайлов).</summary>
public enum MapMode
{
    Normal,
    Humidity
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

    private const int WallAtlasColumns = 7;
    private const int WallAtlasRows = 7;
    private const int WallAtlasTilePx = 64;

    private static readonly Vector2I AtlasOrigin = Vector2I.Zero;

    private TileMapLayer _groundLayer;
    private TileMapLayer _mountainLayer;
    private TileMapLayer _farmLayer;
    private HumidityOverlayRenderer _humidityOverlay;
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
    // Стандарт слоёв: движковый автотайлинг через TerrainTileLayer
    // (SetCellsTerrainConnect нельзя звать из фоновых потоков — копим грязные
    // клетки и пересчитываем одним вызовом на слой в _Process).
    private TerrainTileLayer _blueprintWalls;
    private TerrainTileLayer _builtWalls;
    private TerrainTileLayer _mountains;
    private const int WallTerrainSetId = 0;
    private const int WallTerrainId = 0;
    private const int MountainTerrainSetId = 0;
    private const int MountainTerrainId = 0;

    private Action<(int X, int Y)> _onZoneTileAdded;
    private Action<(int X, int Y)> _onZoneTileRemoved;

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
    public MapMode CurrentMapMode { get; private set; } = MapMode.Normal;
    public event Action<MapMode> OnMapModeChanged;
    public WallBuildManager WallBuildManager => _wallBuildManager;
    public TileMapLayer GhostLayer => _ghostLayer;
    public TileMapLayer WallLayer => _wallLayer;
    public ShadowCasterRenderer ShadowRenderer { get; private set; }

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

        ShadowRenderer = new ShadowCasterRenderer { Name = "ShadowCasterRenderer" };
        AddChild(ShadowRenderer);

        // Иконки содержимого склада (StockpileManager.SnapshotQueue).
        // Canonical-точка для предметных рендереров — scenes/main/Main.cs (_Ready, рядом
        // с GroundItemRenderer), но scenes/ по правилу src-only не трогаем — поэтому
        // StockpileItemRenderer добавляется здесь, ребёнком MapRenderer (ZIndex 6 внутри
        // всё равно выше StockpileLayer с Z по умолчанию).
        var stockpileItemRenderer = new StockpileItemRenderer { Name = "StockpileItemRenderer" };
        AddChild(stockpileItemRenderer);

        TreeJobManager.Instance.OnTreeChopped += OnTreeChopped;

        _onZoneTileAdded = pos => _pendingCells.Enqueue(new PendingCell(_stockpileLayer, new Vector2I(pos.X, pos.Y), SourceGrass, AtlasOrigin, false));
        _onZoneTileRemoved = pos => _pendingCells.Enqueue(new PendingCell(_stockpileLayer, new Vector2I(pos.X, pos.Y), 0, AtlasOrigin, true));
        StockpileManager.Instance.OnZoneTileAdded += _onZoneTileAdded;
        StockpileManager.Instance.OnZoneTileRemoved += _onZoneTileRemoved;

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
            int sourceId = type == BuildingType.WorkTable ? SourceWorkTable : SourceWall;
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

            // Мост fBm GPU (пункт 4): override — только на время генерации карты,
            // сим его не видит. GPU-клампы внутри GenerateFbmMapGpu, CPU — в fallback.
            GpuMapBridge.Enable();
            try
            {
                _pendingMapData = MapGenerator.Generate(MapWidth, MapHeight, seed);
            }
            finally
            {
                GpuMapBridge.Disable();
            }

            int grass = 0, water = 0, mountains = 0, trees = 0;
            for (int x = 0; x < MapWidth; x++)
                for (int y = 0; y < MapHeight; y++)
                {
                    if (_pendingMapData.Ground[x, y] == TileType.Grass) grass++;
                    else if (_pendingMapData.Ground[x, y] == TileType.Mountain) mountains++;
                    else water++;
                    if (_pendingMapData.TreeOnGrass[x, y]) trees++;
                }
            GD.Print($"MapRenderer: карта сгенерирована, seed={seed} (SeedOverride={SeedOverride}), суша={grass}, вода={water}, горы={mountains}, деревья={trees}, озёр={_pendingMapData.LakeCount}[{string.Join(",", _pendingMapData.LakeSizes)}], река={_pendingMapData.MainRiverLength}+{_pendingMapData.RiverBranchCount}пр, связность={_pendingMapData.LandConnectivity:P1}");

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
        if (_blueprintRefreshQueued)
        {
            _blueprintRefreshQueued = false;
            RefreshAllBlueprints();
        }
        // Оверлей влажности обновляется сам по таймеру (троттлинг внутри Refresh):
        // тик влажности раз в 30с игрового, текстуру чаще дёргать незачем.
        if (CurrentMapMode == MapMode.Humidity && _humidityOverlay != null)
            _humidityOverlay.RefreshThrottled(Humidity, delta);
        // Стандарт: terrain-слои флашатся через TerrainTileLayer.
        _blueprintWalls?.Flush(c => BlueprintManager.Instance.IsBlueprintAt(c.X, c.Y));
        _builtWalls?.Flush(c => _wallBuildManager != null && _wallBuildManager.IsWallAt(c.X, c.Y));
        _mountains?.Flush(c => _pendingMapData != null && (uint)c.X < (uint)_pendingMapData.Width && (uint)c.Y < (uint)_pendingMapData.Height && _pendingMapData.Ground[c.X, c.Y] == TileType.Mountain);
    }

    private void QueueBlueprintRefresh() => _blueprintRefreshQueued = true;

    /// <summary>
    /// Переключить режим карты. Humidity — показать синий слой влажности,
    /// Normal — спрятать. Слой обновляется раз в кадр троттлингом внутри.
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
        OnMapModeChanged?.Invoke(mode);
    }

    /// <summary>Дёрнуть обновление оверлея (после тика влажности раз в 30с).</summary>
    public void RefreshHumidityOverlay()
    {
        if (CurrentMapMode == MapMode.Humidity && _humidityOverlay != null)
            _humidityOverlay.Refresh(Humidity);
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
            _ghostLayer.TileSet = wallTerrainTileSet;
            _wallLayer.TileSet = wallTerrainTileSet;
            _blueprintLayer.TileSet = wallTerrainTileSet;
            _buildingLayer.TileSet = buildingTileSet;
            _farmLayer.TileSet = buildingTileSet;

            // Стандарт: стены и чертежи стен — terrain-слои (автотайлинг считает движок).
            _blueprintWalls = new TerrainTileLayer(_blueprintLayer, WallTerrainSetId, WallTerrainId);
            _builtWalls = new TerrainTileLayer(_wallLayer, WallTerrainSetId, WallTerrainId);

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
        QueueBlueprintRefresh();
    }

    private void RefreshAllBlueprints()
    {
        if (_blueprintLayer == null) return;

        var blueprints = BlueprintManager.Instance.GetAllBlueprints();
        var farmPlots = FarmJobManager.Instance.GetAllMarkedPlots();

        _blueprintLayer.Clear();

        // Чертежи стен — через terrain-слой (стандарт): движок подберёт атлас сам.
        var wallCells = new List<Vector2I>();
        foreach (var (cell, bType) in blueprints)
        {
            Vector2I mapPos = new Vector2I(cell.X, cell.Y);
            if (bType == BuildingType.WoodWall)
            {
                wallCells.Add(mapPos);
            }
            else if (bType == BuildingType.WorkTable)
            {
                _blueprintLayer.SetCell(mapPos, SourceWorkTable, AtlasOrigin);
            }
        }
        _blueprintWalls?.RebuildAll(wallCells);

        foreach (var plot in farmPlots)
        {
            _blueprintLayer.SetCell(new Vector2I(plot.X, plot.Y), SourceGardenBed, AtlasOrigin);
        }
    }

    private void OnBlueprintCompleted((int X, int Y) pos, BuildingType type)
    {
        // Завершённый чертёж стены: грязная клетка, соседей пересчитает флаш слоя.
        _blueprintWalls?.MarkDirty(new Vector2I(pos.X, pos.Y));
        if (type == BuildingType.WoodWall)
        {
            _wallBuildManager.AddWall(pos.X, pos.Y);
        }
        else if (type == BuildingType.WorkTable)
        {
            BuildingManager.Instance.AddBuilding(pos.X, pos.Y, BuildingType.WorkTable);
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
        StockpileManager.Instance.OnZoneTileAdded -= _onZoneTileAdded;
        StockpileManager.Instance.OnZoneTileRemoved -= _onZoneTileRemoved;

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