using System;
using System.Collections.Generic;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Адаптер обратной совместимости для чертежей. Вся логика теперь в ConstructionManager.
/// </summary>
public class BlueprintManager
{
    public static BlueprintManager Instance { get; } = new();

    public static int LogsFor(BuildingType type) => BuildConfig.LogsFor(type);

    public event Action<(int X, int Y), BuildingType> OnBlueprintAdded;
    public event Action<List<(int X, int Y)>, BuildingType> OnBlueprintsBatchAdded;
    public event Action<(int X, int Y)> OnBlueprintRemoved;
    public event Action<List<(int X, int Y)>> OnBlueprintsBatchRemoved;
    public event Action<(int X, int Y), BuildingType> OnBlueprintCompleted;

    private BlueprintManager()
    {
        ConstructionManager.Instance.OnSiteAdded += site =>
        {
            OnBlueprintAdded?.Invoke((site.X, site.Y), site.Definition.Type);
        };

        ConstructionManager.Instance.OnSitesBatchAdded += sites =>
        {
            if (sites.Count == 0) return;
            var list = new List<(int X, int Y)>(sites.Count);
            foreach (var s in sites) list.Add((s.X, s.Y));
            OnBlueprintsBatchAdded?.Invoke(list, sites[0].Definition.Type);
        };

        ConstructionManager.Instance.OnSiteRemoved += pos =>
        {
            OnBlueprintRemoved?.Invoke(pos);
        };

        ConstructionManager.Instance.OnSitesBatchRemoved += list =>
        {
            OnBlueprintsBatchRemoved?.Invoke(list);
        };

        ConstructionManager.Instance.OnSiteCompleted += site =>
        {
            OnBlueprintCompleted?.Invoke((site.X, site.Y), site.Definition.Type);
        };
    }

    public void AddBlueprint(int x, int y, BuildingType type, bool[,] treeOnGrass)
    {
        var def = StructureRegistry.Get(type);
        ConstructionManager.Instance.StartSite(x, y, def, null);
    }

    public void AddBlueprintsBatch(List<(int X, int Y)> cells, BuildingType type, bool[,] treeOnGrass, bool[,] stoneOnGrass = null)
    {
        var def = StructureRegistry.Get(type);
        ConstructionManager.Instance.StartSitesBatch(cells, def, null);
    }

    public void AddDeliveredLogs(int x, int y, int count)
    {
        ConstructionManager.Instance.DeliverMaterial(x, y, ItemId.Log, count, out _, out _);
    }

    public void RemoveBlueprint(int x, int y, out int droppedLogs)
    {
        droppedLogs = 0;
        if (ConstructionManager.Instance.TryGetSite(x, y, out var site))
        {
            droppedLogs = site.GetDelivered(ItemId.Log);
        }
        ConstructionManager.Instance.CancelSite(x, y);
    }

    public void RemoveBlueprintsBatch(List<(int X, int Y)> cells)
    {
        ConstructionManager.Instance.CancelSitesBatch(cells);
    }

    public bool IsBlueprintAt(int x, int y)
    {
        return ConstructionManager.Instance.IsSiteAt(x, y);
    }

    public int GetDelivered(int x, int y)
    {
        return ConstructionManager.Instance.TryGetSite(x, y, out var site)
            ? site.GetDelivered(ItemId.Log)
            : 0;
    }

    public bool IsWallBlueprintAt(int x, int y)
    {
        return ConstructionManager.Instance.IsWallSiteAt(x, y);
    }

    public Dictionary<(int X, int Y), BuildingType> GetAllBlueprints()
    {
        var dict = new Dictionary<(int X, int Y), BuildingType>();
        foreach (var (pos, site) in ConstructionManager.Instance.GetAllSites())
        {
            dict[pos] = site.Definition.Type;
        }
        return dict;
    }

    public HashSet<(int X, int Y)> GetWallBlueprints()
    {
        return ConstructionManager.Instance.GetWallSites();
    }

    public bool CompleteConstruction(int x, int y, out BuildingType completedType)
    {
        if (ConstructionManager.Instance.CompleteConstruction(x, y, null, out var def))
        {
            completedType = def.Type;
            return true;
        }
        completedType = BuildingType.WoodWall;
        return false;
    }

    public void ClearAll()
    {
        ConstructionManager.Instance.ClearAll();
    }
}