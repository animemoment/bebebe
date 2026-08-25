using System;
using System.Collections.Generic;
using Godot;
using Game.Core;

namespace Game.Simulation;

public class BuildingManager
{
    public static BuildingManager Instance { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), BuildingType> _buildings = new();

    public event Action<(int X, int Y), BuildingType> OnBuildingPlaced;
    public event Action<(int X, int Y)> OnBuildingRemoved;

    public void AddBuilding(int x, int y, BuildingType type)
    {
        lock (_lock)
        {
            _buildings[(x, y)] = type;
            Callable.From(() => OnBuildingPlaced?.Invoke((x, y), type)).CallDeferred();
        }
    }

    public void RemoveBuilding(int x, int y)
    {
        lock (_lock)
        {
            if (_buildings.Remove((x, y)))
            {
                Callable.From(() => OnBuildingRemoved?.Invoke((x, y))).CallDeferred();
            }
        }
    }

    public bool HasBuildingAt(int x, int y)
    {
        lock (_lock)
        {
            return _buildings.ContainsKey((x, y));
        }
    }

    public bool TryGetBuilding(int x, int y, out BuildingType type)
    {
        lock (_lock)
        {
            return _buildings.TryGetValue((x, y), out type);
        }
    }

    public Dictionary<(int X, int Y), BuildingType> GetAllBuildings()
    {
        lock (_lock)
        {
            return new Dictionary<(int X, int Y), BuildingType>(_buildings);
        }
    }
}