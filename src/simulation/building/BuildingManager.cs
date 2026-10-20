using System;
using System.Collections.Generic;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Адаптер обратной совместимости. Вся логика теперь в StructureManager.
/// </summary>
public class BuildingManager
{
    public static BuildingManager Instance { get; } = new();

    public event Action<(int X, int Y), BuildingType> OnBuildingPlaced;
    public event Action<(int X, int Y)> OnBuildingRemoved;

    private BuildingManager()
    {
        StructureManager.Instance.OnStructureAdded += s =>
        {
            if (!s.Definition.IsWall)
            {
                OnBuildingPlaced?.Invoke((s.X, s.Y), s.Type);
            }
        };

        StructureManager.Instance.OnStructureRemoved += (pos, def) =>
        {
            if (!def.IsWall)
            {
                OnBuildingRemoved?.Invoke(pos);
            }
        };
    }

    public void AddBuilding(int x, int y, BuildingType type)
    {
        var def = StructureRegistry.Get(type);
        StructureManager.Instance.AddStructure(x, y, def);
    }

    public void RemoveBuilding(int x, int y)
    {
        StructureManager.Instance.RemoveStructure(x, y);
    }

    public bool HasBuildingAt(int x, int y)
    {
        return StructureManager.Instance.HasStructureAt(x, y);
    }

    public bool TryGetBuilding(int x, int y, out BuildingType type)
    {
        if (StructureManager.Instance.TryGetStructure(x, y, out var s))
        {
            type = s.Type;
            return true;
        }
        type = BuildingType.WoodWall;
        return false;
    }

    public Dictionary<(int X, int Y), BuildingType> GetAllBuildings()
    {
        var result = new Dictionary<(int X, int Y), BuildingType>();
        foreach (var kvp in StructureManager.Instance.GetAllStructures())
        {
            if (!kvp.Value.Definition.IsWall)
            {
                result[kvp.Key] = kvp.Value.Type;
            }
        }
        return result;
    }

    public void ClearAll()
    {
        StructureManager.Instance.ClearAll();
    }
}