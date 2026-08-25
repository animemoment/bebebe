using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

public sealed class SimulationContext
{
    public TileType[,] Ground { get; }
    public bool[,] TreeOnGrass { get; }
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
        bool[,] treeOnGrass,
        bool[,] solidWalls,
        List<(int X, int Y)> walkableTiles,
        AgentSpatialGrid spatialGrid,
        AgentMovementService movement,
        Random random,
        int tileSize = 64)
    {
        Ground = ground ?? throw new ArgumentNullException(nameof(ground));
        TreeOnGrass = treeOnGrass ?? throw new ArgumentNullException(nameof(treeOnGrass));
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