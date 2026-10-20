using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

public sealed class SimulationContext
{
    public TileType[,] Ground { get; }
    public HumidityMap Humidity { get; }
    /// <summary>Плодородие почвы 0..200. Может быть null (старые вызовы) — рост тогда без множителя.</summary>
    public FertilityMap Fertility { get; }
    public bool[,] TreeOnGrass { get; }
    /// <summary>Каменные россыпи (добыча, Mining). Может быть null в старых тестах.</summary>
    public bool[,] StoneOnGrass { get; }
    public bool[,] SolidWalls { get; }
    public List<(int X, int Y)> WalkableTiles { get; }
    public AgentSpatialGrid SpatialGrid { get; }
    public AgentMovementService Movement { get; }
    public Random Random { get; }
    public int TileSize { get; }
    public int MapWidth { get; }
    public int MapHeight { get; }

    public SimulationContext(
        TileType[,] ground,
        HumidityMap humidity,
        bool[,] treeOnGrass,
        bool[,] solidWalls,
        List<(int X, int Y)> walkableTiles,
        AgentSpatialGrid spatialGrid,
        AgentMovementService movement,
        Random random,
        int tileSize = 64,
        bool[,] stoneOnGrass = null,
        FertilityMap fertility = null)
    {
        Ground = ground ?? throw new ArgumentNullException(nameof(ground));
        Humidity = humidity; // может быть null (старые вызовы) — рост тогда без множителя
        Fertility = fertility; // может быть null — рост тогда без множителя плодородия
        TreeOnGrass = treeOnGrass ?? throw new ArgumentNullException(nameof(treeOnGrass));
        StoneOnGrass = stoneOnGrass; // null = карты без камня (старые сейвы/тесты)
        SolidWalls = solidWalls ?? throw new ArgumentNullException(nameof(solidWalls));
        WalkableTiles = walkableTiles ?? throw new ArgumentNullException(nameof(walkableTiles));
        SpatialGrid = spatialGrid ?? throw new ArgumentNullException(nameof(spatialGrid));
        Movement = movement ?? throw new ArgumentNullException(nameof(movement));
        Random = random ?? throw new ArgumentNullException(nameof(random));
        TileSize = tileSize;
        MapWidth = ground.GetLength(0);
        MapHeight = ground.GetLength(1);
    }
}