using System;
using System.Collections.Generic;
using Godot;

namespace Game.Simulation;

/// <summary>
/// Тонкий фасад над ZoneManager для Farm-зон.
/// Сохраняет старый API 1-в-1 (FarmingTool, GardenController, CropGrowthManager,
/// PlantingJobHandler, FarmZoneRenderer, SelectTool компилируются без правок).
/// </summary>
public sealed class FarmZoneManager
{
    public static FarmZoneManager Instance { get; } = new();

    private ZoneManager Z => ZoneManager.Instance;

    public FarmZone HoveredZone => Adapt(Z.HoveredZone);
    public FarmZone SelectedZone => Adapt(Z.SelectedZone);

    public event Action OnZonesUpdated
    {
        add => Z.OnZonesUpdated += value;
        remove => Z.OnZonesUpdated -= value;
    }
    public event Action<FarmZone> OnZoneHovered
    {
        add => Z.OnZoneHovered += z => value?.Invoke(Adapt(z));
        remove { }
    }
    public event Action<FarmZone> OnZoneSelected
    {
        add => Z.OnZoneSelected += z => value?.Invoke(Adapt(z));
        remove { }
    }
    public event Action OnZoneDeselected
    {
        add => Z.OnZoneDeselected += value;
        remove => Z.OnZoneDeselected -= value;
    }
    public event Action<FarmZone> OnAutoPlantChanged
    {
        add => Z.OnAutoPlantChanged += z => value?.Invoke(Adapt(z));
        remove { }
    }

    private static FarmZone Adapt(Zone z)
    {
        if (z == null || z.Kind != ZoneKind.Farm) return null;
        var f = new FarmZone(z.Id, z.Name, z.Tiles)
        {
            AutoPlantEnabled = z.AutoPlantEnabled,
            RequiredSeedItem = z.RequiredSeedItem,
            PlantedCount = z.PlantedCount
        };
        return f;
    }

    public void CreateZone(List<(int X, int Y)> tiles)
    {
        Z.CreateFarmZones(tiles);
    }

    public void SetAutoPlant(int zoneId, bool enabled)
    {
        Z.SetAutoPlant(zoneId, enabled);
    }

    public void RemoveTiles(List<(int X, int Y)> tilesToRemove) => Z.RemoveTiles(tilesToRemove);

    public void SetHoveredTile(int x, int y) => Z.SetHoveredTile(x, y);

    public void SelectZoneAt(int x, int y)
    {
        var hit = Z.SelectZoneAt(x, y);
        // Совместимость: раньше промах по ферме гасил выбор; теперь выбор един —
        // если попали в Work-зону, это тоже валидный выбор (окно откроет WorkZoneController).
        // Если промах мимо всех — ZoneManager уже снял выбор.
    }

    public void DeselectZone()
    {
        // Гасим только если выбрана именно ферма — чужую (Work) не трогаем.
        if (Z.SelectedZone != null && Z.SelectedZone.Kind == ZoneKind.Farm)
            Z.DeselectZone();
    }

    public bool TryGetZoneAt(int x, int y, out FarmZone zone)
    {
        if (Z.TryGetZoneAt(x, y, out var z) && z.Kind == ZoneKind.Farm)
        {
            zone = Adapt(z);
            return true;
        }
        zone = null;
        return false;
    }

    public bool TryGetZoneById(int zoneId, out FarmZone zone)
    {
        if (Z.TryGetZoneById(zoneId, out var z) && z.Kind == ZoneKind.Farm)
        {
            zone = Adapt(z);
            return true;
        }
        zone = null;
        return false;
    }

    public List<FarmZone> GetAllZones()
    {
        var all = Z.GetAllZones();
        var result = new List<FarmZone>(all.Count);
        foreach (var z in all)
        {
            if (z.Kind == ZoneKind.Farm)
                result.Add(Adapt(z));
        }
        return result;
    }
}
