using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Адаптер обратной совместимости для стен. Вся логика теперь в StructureManager.
/// </summary>
public class WallBuildManager : IDisposable
{
    public event Action<List<(int X, int Y)>> OnTilesUpdated;
    private readonly Action<List<(int X, int Y)>> _wallHandler;

    public WallBuildManager()
    {
        _wallHandler = list => OnTilesUpdated?.Invoke(list);
        StructureManager.Instance.OnWallTilesUpdated += _wallHandler;
    }

    public void Dispose()
    {
        StructureManager.Instance.OnWallTilesUpdated -= _wallHandler;
    }

    public void AddWall(int x, int y)
    {
        StructureManager.Instance.AddStructure(x, y, StructureRegistry.WoodWall);
    }

    public void RemoveWall(int x, int y)
    {
        StructureManager.Instance.RemoveStructure(x, y);
    }

    public bool IsWallAt(int x, int y) => StructureManager.Instance.IsWallAt(x, y);

    public HashSet<(int X, int Y)> GetAllWalls() => StructureManager.Instance.GetAllWalls();

    public void FlushPending()
    {
        StructureManager.Instance.FlushPendingWallUpdates();
    }
}