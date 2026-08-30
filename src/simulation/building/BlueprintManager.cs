using System;
using System.Collections.Generic;
using Godot;
using Game.Core;

namespace Game.Simulation;

public class BlueprintManager
{
    public static BlueprintManager Instance { get; } = new();

    public class BlueprintSite
    {
        public int X;
        public int Y;
        public BuildingType Type;
        public int DeliveredLogs;
        public int TargetLogs = 15;

        public bool IsReadyToBuild => DeliveredLogs >= TargetLogs;
    }

    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), BlueprintSite> _blueprints = new(1024);

    public event Action<(int X, int Y), BuildingType> OnBlueprintAdded;
    public event Action<List<(int X, int Y)>, BuildingType> OnBlueprintsBatchAdded;
    public event Action<(int X, int Y)> OnBlueprintRemoved;
    public event Action<List<(int X, int Y)>> OnBlueprintsBatchRemoved;
    public event Action<(int X, int Y), BuildingType> OnBlueprintCompleted;

    public void AddBlueprint(int x, int y, BuildingType type, bool[,] treeOnGrass)
    {
        lock (_lock)
        {
            if (!_blueprints.ContainsKey((x, y)))
            {
                int target = type == BuildingType.WorkTable ? 25 : 15;
                _blueprints[(x, y)] = new BlueprintSite
                {
                    X = x,
                    Y = y,
                    Type = type,
                    TargetLogs = target
                };

                if (treeOnGrass != null && treeOnGrass[x, y])
                {
                    TreeJobManager.Instance.MarkTree(x, y);
                }

                JobBroker.Instance.RegisterBlueprint(x, y, type, target);
                Callable.From(() => OnBlueprintAdded?.Invoke((x, y), type)).CallDeferred();
            }
        }
    }

    public void AddBlueprintsBatch(List<(int X, int Y)> cells, BuildingType type, bool[,] treeOnGrass)
    {
        if (cells == null || cells.Count == 0) return;

        var addedList = new List<(int X, int Y)>(cells.Count);
        var treesToChop = new List<(int X, int Y)>();
        int target = type == BuildingType.WorkTable ? 25 : 15;

        lock (_lock)
        {
            foreach (var (x, y) in cells)
            {
                if (!_blueprints.ContainsKey((x, y)))
                {
                    _blueprints[(x, y)] = new BlueprintSite
                    {
                        X = x,
                        Y = y,
                        Type = type,
                        TargetLogs = target
                    };
                    addedList.Add((x, y));

                    if (treeOnGrass != null && treeOnGrass[x, y])
                    {
                        treesToChop.Add((x, y));
                    }
                }
            }
        }

        if (addedList.Count > 0)
        {
            JobBroker.Instance.RegisterBlueprintBatch(addedList, type, target);
            if (treesToChop.Count > 0)
            {
                TreeJobManager.Instance.MarkTreesBatch(treesToChop);
            }
            Callable.From(() => OnBlueprintsBatchAdded?.Invoke(addedList, type)).CallDeferred();
        }
    }

    public void AddDeliveredLogs(int x, int y, int count)
    {
        lock (_lock)
        {
            if (_blueprints.TryGetValue((x, y), out var site))
            {
                site.DeliveredLogs += count;
            }
        }
    }

    public void RemoveBlueprint(int x, int y, out int droppedLogs)
    {
        lock (_lock)
        {
            droppedLogs = 0;
            if (_blueprints.TryGetValue((x, y), out var site))
            {
                droppedLogs = site.DeliveredLogs;
                _blueprints.Remove((x, y));
                JobBroker.Instance.UnregisterBlueprint(x, y);
                Callable.From(() => OnBlueprintRemoved?.Invoke((x, y))).CallDeferred();
            }
        }
    }

    public void RemoveBlueprintsBatch(List<(int X, int Y)> cells)
    {
        if (cells == null || cells.Count == 0) return;

        var removedList = new List<(int X, int Y)>(cells.Count);
        lock (_lock)
        {
            foreach (var (x, y) in cells)
            {
                if (_blueprints.TryGetValue((x, y), out var site))
                {
                    int dropped = site.DeliveredLogs;
                    _blueprints.Remove((x, y));
                    removedList.Add((x, y));

                    if (dropped > 0)
                    {
                        GroundItemManager.Instance.SpawnItems(x, y, ItemId.Log, dropped);
                    }
                }
            }
        }

        if (removedList.Count > 0)
        {
            JobBroker.Instance.UnregisterBlueprintBatch(removedList);
            Callable.From(() => OnBlueprintsBatchRemoved?.Invoke(removedList)).CallDeferred();
        }
    }

    public bool IsBlueprintAt(int x, int y)
    {
        lock (_lock)
        {
            return _blueprints.ContainsKey((x, y));
        }
    }

    public Dictionary<(int X, int Y), BuildingType> GetAllBlueprints()
    {
        lock (_lock)
        {
            var result = new Dictionary<(int X, int Y), BuildingType>(_blueprints.Count);
            foreach (var (pos, site) in _blueprints)
            {
                result[pos] = site.Type;
            }
            return result;
        }
    }

    public HashSet<(int X, int Y)> GetWallBlueprints()
    {
        lock (_lock)
        {
            var walls = new HashSet<(int X, int Y)>();
            foreach (var (pos, site) in _blueprints)
            {
                if (site.Type == BuildingType.WoodWall)
                    walls.Add(pos);
            }
            return walls;
        }
    }

    public bool CompleteConstruction(int x, int y, out BuildingType completedType)
    {
        lock (_lock)
        {
            if (_blueprints.TryGetValue((x, y), out var site))
            {
                completedType = site.Type;
                _blueprints.Remove((x, y));
                JobBroker.Instance.UnregisterBlueprint(x, y);

                var typeCopy = completedType;
                Callable.From(() => OnBlueprintCompleted?.Invoke((x, y), typeCopy)).CallDeferred();
                return true;
            }
            completedType = BuildingType.WoodWall;
            return false;
        }
    }
}