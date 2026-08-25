using Godot;
using Game.Core;
using Game.Simulation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Game.UI;

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

    private const uint MapSeed = 12345u;

    private const string TextureGrass     = "uid://bhbo4m0ps7yxc";
    private const string TextureWater     = "uid://cmi8pjecdx35";
    private const string TextureTree0     = "uid://c70p6ktr0vcx6";
    private const string TextureTree1     = "uid://ddkl165hm35rp";
    private const string TextureWoodWall  = "uid://chubmh2ufwgwp";
    private const string TextureWorkTable = "uid://rp2bpb5c7k2y";
    private const string TextureGardenBed = "uid://cf7ci8om64vt0";

    private const int WallAtlasColumns = 4;
    private const int WallAtlasTilePx = 64;

    private static readonly Vector2I AtlasOrigin = Vector2I.Zero;

    private TileMapLayer _groundLayer;
    private TileMapLayer _farmLayer;
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
    public WallBuildManager WallBuildManager => _wallBuildManager;
    public TileMapLayer GhostLayer => _ghostLayer;
    public TileMapLayer WallLayer => _wallLayer;

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

        _farmLayer = new TileMapLayer { Name = "FarmLayer" };
        AddChild(_farmLayer);

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

        TreeJobManager.Instance.OnTreeChopped += OnTreeChopped;

        _onZoneTileAdded = pos => _stockpileLayer.SetCell(new Vector2I(pos.X, pos.Y), SourceGrass, AtlasOrigin);
        _onZoneTileRemoved = pos => _stockpileLayer.EraseCell(new Vector2I(pos.X, pos.Y));
        StockpileManager.Instance.OnZoneTileAdded += _onZoneTileAdded;
        StockpileManager.Instance.OnZoneTileRemoved += _onZoneTileRemoved;

        _onBlueprintAdded = OnBlueprintChanged;
        _onBlueprintsBatchAdded = (list, type) => RefreshAllBlueprints();
        _onBlueprintRemoved = pos => RefreshAllBlueprints();
        _onBlueprintsBatchRemoved = list => RefreshAllBlueprints();
        _onBlueprintCompleted = OnBlueprintCompleted;

        BlueprintManager.Instance.OnBlueprintAdded += _onBlueprintAdded;
        BlueprintManager.Instance.OnBlueprintsBatchAdded += _onBlueprintsBatchAdded;
        BlueprintManager.Instance.OnBlueprintRemoved += _onBlueprintRemoved;
        BlueprintManager.Instance.OnBlueprintsBatchRemoved += _onBlueprintsBatchRemoved;
        BlueprintManager.Instance.OnBlueprintCompleted += _onBlueprintCompleted;

        _onBuildingPlaced = (pos, type) =>
        {
            int sourceId = type == BuildingType.WorkTable ? SourceWorkTable : SourceWall;
            _buildingLayer.SetCell(new Vector2I(pos.X, pos.Y), sourceId, AtlasOrigin);
        };
        _onBuildingRemoved = pos => _buildingLayer.EraseCell(new Vector2I(pos.X, pos.Y));
        BuildingManager.Instance.OnBuildingPlaced += _onBuildingPlaced;
        BuildingManager.Instance.OnBuildingRemoved += _onBuildingRemoved;

        _onPlotMarked = pos => _blueprintLayer.SetCell(new Vector2I(pos.X, pos.Y), SourceGardenBed, AtlasOrigin);
        _onPlotsBatchMarked = list => RefreshAllBlueprints();
        _onPlotUnmarked = pos => _blueprintLayer.EraseCell(new Vector2I(pos.X, pos.Y));
        _onPlotsBatchUnmarked = list => RefreshAllBlueprints();
        _onPlotCompleted = pos =>
        {
            _blueprintLayer.EraseCell(new Vector2I(pos.X, pos.Y));
            _farmLayer.SetCell(new Vector2I(pos.X, pos.Y), SourceGardenBed, AtlasOrigin);
        };

        FarmJobManager.Instance.OnPlotMarked += _onPlotMarked;
        FarmJobManager.Instance.OnPlotsBatchMarked += _onPlotsBatchMarked;
        FarmJobManager.Instance.OnPlotUnmarked += _onPlotUnmarked;
        FarmJobManager.Instance.OnPlotsBatchUnmarked += _onPlotsBatchUnmarked;
        FarmJobManager.Instance.OnPlotCompleted += _onPlotCompleted;

        Task.Run(() =>
        {
            _pendingMapData = MapGenerator.Generate(MapWidth, MapHeight, MapSeed);
            Callable.From(ApplyMap).CallDeferred();
        });
    }

    public override void _Process(double delta)
    {
        if (!_choppedTreesQueue.IsEmpty && _objectLayer != null)
        {
            while (_choppedTreesQueue.TryDequeue(out var cell))
            {
                _objectLayer.EraseCell(cell);
            }
        }
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

            for (int x = 0; x < MapWidth; x++)
            {
                for (int y = 0; y < MapHeight; y++)
                {
                    var pos = new Vector2I(x, y);
                    if (mapData.Ground[x, y] == TileType.Grass)
                    {
                        _groundLayer.SetCell(pos, SourceGrass, AtlasOrigin);
                        if (mapData.TreeOnGrass[x, y])
                        {
                            int hash = (x * 73856093) ^ (y * 19349663) ^ (int)MapSeed;
                            int sourceId = (Math.Abs(hash) % 2 == 0) ? SourceTree0 : SourceTree1;
                            _objectLayer.SetCell(pos, sourceId, AtlasOrigin);
                        }
                    }
                    else
                    {
                        _groundLayer.SetCell(pos, SourceWater, AtlasOrigin);
                    }
                }
            }

            TileSet buildingTileSet = CreateBuildingTileSet();
            _wallLayer.TileSet = buildingTileSet;
            _buildingLayer.TileSet = buildingTileSet;
            _ghostLayer.TileSet = buildingTileSet;
            _blueprintLayer.TileSet = buildingTileSet;
            _farmLayer.TileSet = buildingTileSet;

            _wallBuildManager = new WallBuildManager();
            _wallBuildManager.OnTilesUpdated += OnWallTilesUpdated;

            OnMapApplied?.Invoke();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MapRenderer: ошибка ApplyMap: {ex.Message}");
        }
    }

    private void OnBlueprintChanged((int X, int Y) pos, BuildingType type)
    {
        RefreshAllBlueprints();
    }

    private void RefreshAllBlueprints()
    {
        if (_blueprintLayer == null) return;

        var blueprints = BlueprintManager.Instance.GetAllBlueprints();
        var wallBlueprints = BlueprintManager.Instance.GetWallBlueprints();
        var farmPlots = FarmJobManager.Instance.GetAllMarkedPlots();

        _blueprintLayer.Clear();

        foreach (var (cell, bType) in blueprints)
        {
            Vector2I mapPos = new Vector2I(cell.X, cell.Y);
            if (bType == BuildingType.WoodWall)
            {
                Vector2I atlasCoords = WallTileHelper.GetTile(cell, wallBlueprints);
                _blueprintLayer.SetCell(mapPos, SourceWall, atlasCoords);
            }
            else if (bType == BuildingType.WorkTable)
            {
                _blueprintLayer.SetCell(mapPos, SourceWorkTable, AtlasOrigin);
            }
        }

        foreach (var plot in farmPlots)
        {
            _blueprintLayer.SetCell(new Vector2I(plot.X, plot.Y), SourceGardenBed, AtlasOrigin);
        }
    }

    private void OnBlueprintCompleted((int X, int Y) pos, BuildingType type)
    {
        _blueprintLayer.EraseCell(new Vector2I(pos.X, pos.Y));
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
        return tileSet;
    }

    private TileSet CreateBuildingTileSet()
    {
        var tileSet = new TileSet { TileSize = new Vector2I(TileSizePx, TileSizePx) };

        var wallTexture = LoadTexture(TextureWoodWall, "wooden_wall");
        if (wallTexture != null)
        {
            var wallAtlas = new TileSetAtlasSource { Texture = wallTexture, TextureRegionSize = new Vector2I(WallAtlasTilePx, WallAtlasTilePx) };
            for (int ty = 0; ty < WallAtlasColumns; ty++)
                for (int tx = 0; tx < WallAtlasColumns; tx++)
                    wallAtlas.CreateTile(new Vector2I(tx, ty));

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

    private void OnWallTilesUpdated(List<(int X, int Y)> changedTiles)
    {
        if (_wallLayer?.TileSet == null) return;
        HashSet<(int X, int Y)> walls = _wallBuildManager.GetAllWalls();

        foreach (var (x, y) in changedTiles)
        {
            Vector2I cellPos = new Vector2I(x, y);
            if (_wallBuildManager.IsWallAt(x, y))
            {
                Vector2I atlasCoords = WallTileHelper.GetTile((x, y), walls);
                _wallLayer.SetCell(cellPos, SourceWall, atlasCoords);
            }
            else
            {
                _wallLayer.EraseCell(cellPos);
            }
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