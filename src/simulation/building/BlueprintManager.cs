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

    /// <summary>
    /// Сколько брёвен нужно под чертёж. Все цифры — в BuildConfig (один файл).
    /// </summary>
    public static int LogsFor(BuildingType type) => BuildConfig.LogsFor(type);

    public event Action<(int X, int Y), BuildingType> OnBlueprintAdded;
    public event Action<List<(int X, int Y)>, BuildingType> OnBlueprintsBatchAdded;
    public event Action<(int X, int Y)> OnBlueprintRemoved;
    public event Action<List<(int X, int Y)>> OnBlueprintsBatchRemoved;
    public event Action<(int X, int Y), BuildingType> OnBlueprintCompleted;

    public void AddBlueprint(int x, int y, BuildingType type, bool[,] treeOnGrass)
    {
        // F68: хронология «что построил и когда» с координатами.
        Game.Core.SimEvents.Mark("BLUEPRINT", $"{type} at={x},{y}");
        Game.Core.SimEvents.Count("build.blueprint." + type.ToString());
        lock (_lock)
        {
            if (!_blueprints.ContainsKey((x, y)))
            {
                int target = LogsFor(type);
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

    public void AddBlueprintsBatch(List<(int X, int Y)> cells, BuildingType type, bool[,] treeOnGrass, bool[,] stoneOnGrass = null)
    {
        if (cells == null || cells.Count == 0) return;

        var addedList = new List<(int X, int Y)>(cells.Count);
        var treesToChop = new List<(int X, int Y)>();
        var stonesToMine = new List<(int X, int Y)>();
        int target = LogsFor(type);

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
                    // Камень под фундаментом — автоматом в добычу (как деревья).
                    if (stoneOnGrass != null && (uint)x < (uint)stoneOnGrass.GetLength(0) && (uint)y < (uint)stoneOnGrass.GetLength(1) && stoneOnGrass[x, y])
                    {
                        stonesToMine.Add((x, y));
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
            if (stonesToMine.Count > 0)
            {
                StoneJobManager.Instance.MarkStonesBatch(stonesToMine, stoneOnGrass);
            }
            Callable.From(() => OnBlueprintsBatchAdded?.Invoke(addedList, type)).CallDeferred();
        }
    }

    public void AddDeliveredLogs(int x, int y, int count)
    {
        if (count <= 0) return;
        lock (_lock)
        {
            if (_blueprints.TryGetValue((x, y), out var site))
            {
                // #2: кламп — DeliverLogsToBlueprint уже клампит по индексу,
                // но прямые вызовы AddDeliveredLogs тоже не должны переполнять.
                int remaining = site.TargetLogs - site.DeliveredLogs;
                site.DeliveredLogs += Math.Min(count, Math.Max(0, remaining));
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
                WorkProgressTracker.Instance.Clear(x, y);
                Callable.From(() => OnBlueprintRemoved?.Invoke((x, y))).CallDeferred();
            }
        }
    }

    public void RemoveBlueprintsBatch(List<(int X, int Y)> cells)
    {
        if (cells == null || cells.Count == 0) return;

        var removedList = new List<(int X, int Y)>(cells.Count);
        var drops = new List<(int X, int Y, int Count)>(cells.Count);
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
                        drops.Add((x, y, dropped));
                    }
                }
            }
        }

        // SpawnItems ВНЕ blueprint-lock (он берёт Ground-lock): иначе инверсия
        // порядка lock'ов. Кладём рядом через FindStandPosition — НЕ на клетку
        // цели: там может встать будущая стройка, а Construction.Can=!HasItemsAt
        // заблокировал бы её навечно (как уже делает Delivery-хендлер).
        foreach (var (x, y, count) in drops)
        {
            (int X, int Y) stand = (x, y);
            // ctx недоступен из менеджера — ищем свободную клетку локально.
            bool found = false;
            for (int oy = -2; oy <= 2 && !found; oy++)
                for (int ox = -2; ox <= 2 && !found; ox++)
                {
                    if (ox == 0 && oy == 0) continue;
                    if (!GroundItemManager.Instance.HasItemsAt(x + ox, y + oy))
                    {
                        stand = (x + ox, y + oy);
                        found = true;
                    }
                }
            GroundItemManager.Instance.SpawnItems(stand.X, stand.Y, ItemId.Log, count);
        }

        if (removedList.Count > 0)
        {
            JobBroker.Instance.UnregisterBlueprintBatch(removedList);
            foreach (var (x, y) in removedList)
                WorkProgressTracker.Instance.Clear(x, y);
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

    /// <summary>Сколько бревен уже привезли на чертёж (для гейта 100% стройки).</summary>
    public int GetDelivered(int x, int y)
    {
        lock (_lock)
        {
            return _blueprints.TryGetValue((x, y), out var site) ? site.DeliveredLogs : 0;
        }
    }

    /// <summary>
    /// True если на клетке чертёж именно стены (для exists-флаша terrain-слоя
    /// чертежей: мебель в этот слой не попадает, стирать её оттуда нельзя).
    /// </summary>
    public bool IsWallBlueprintAt(int x, int y)
    {
        lock (_lock)
        {
            return _blueprints.TryGetValue((x, y), out var site)
                && site.Type == BuildingType.WoodWall;
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