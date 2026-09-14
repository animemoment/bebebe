using System;
using System.Collections.Generic;
using Godot;

namespace Game.Simulation;

public sealed class FarmZoneManager
{
    public static FarmZoneManager Instance { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<int, FarmZone> _zones = new(256);
    private readonly Dictionary<(int X, int Y), int> _tileToZoneId = new(4096);
    private int _nextZoneId = 1;

    public FarmZone HoveredZone { get; private set; }
    public FarmZone SelectedZone { get; private set; }

    public event Action OnZonesUpdated;
    public event Action<FarmZone> OnZoneHovered;
    public event Action<FarmZone> OnZoneSelected;
    public event Action OnZoneDeselected;
    public event Action<FarmZone> OnAutoPlantChanged;

    private static readonly (int X, int Y)[] CardinalDirections =
    {
        (0, -1), (0, 1), (-1, 0), (1, 0)
    };

    public void CreateZone(List<(int X, int Y)> tiles)
    {
        if (tiles == null || tiles.Count == 0) return;

        var components = SplitIntoConnectedComponents(new HashSet<(int X, int Y)>(tiles));

        lock (_lock)
        {
            foreach (var comp in components)
            {
                int id = _nextZoneId++;
                string name = $"Грядка #{id}";
                var zone = new FarmZone(id, name, comp);
                _zones[id] = zone;

                foreach (var pos in comp)
                {
                    _tileToZoneId[pos] = id;
                }
            }
        }

        Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
    }

    public void SetAutoPlant(int zoneId, bool enabled)
    {
        FarmZone zone = null;
        lock (_lock)
        {
            if (_zones.TryGetValue(zoneId, out zone))
            {
                zone.AutoPlantEnabled = enabled;
            }
        }

        if (zone != null)
        {
            if (enabled)
            {
                // Запускаем задачи посадки на все готовые вскопанные клетки зоны без посадок
                foreach (var (x, y) in zone.Tiles)
                {
                    if (FarmJobManager.Instance.IsGardenBed(x, y) && !CropGrowthManager.Instance.HasCrop(x, y))
                    {
                        JobBroker.Instance.RegisterPlanting(x, y, zone.Id);
                    }
                }
            }
            else
            {
                foreach (var (x, y) in zone.Tiles)
                {
                    JobBroker.Instance.UnregisterPlanting(x, y);
                }
            }

            OnAutoPlantChanged?.Invoke(zone);
        }
    }

    public void RemoveTiles(List<(int X, int Y)> tilesToRemove)
    {
        if (tilesToRemove == null || tilesToRemove.Count == 0) return;

        var affectedZones = new HashSet<int>();

        lock (_lock)
        {
            foreach (var pos in tilesToRemove)
            {
                CropGrowthManager.Instance.RemoveCrop(pos.X, pos.Y);
                JobBroker.Instance.UnregisterPlanting(pos.X, pos.Y);
                JobBroker.Instance.UnregisterHarvest(pos.X, pos.Y);

                if (_tileToZoneId.TryGetValue(pos, out int zoneId))
                {
                    _tileToZoneId.Remove(pos);
                    if (_zones.TryGetValue(zoneId, out var zone))
                    {
                        zone.Tiles.Remove(pos);
                        affectedZones.Add(zoneId);
                    }
                }
            }

            foreach (int zoneId in affectedZones)
            {
                if (!_zones.TryGetValue(zoneId, out var zone)) continue;

                if (zone.Tiles.Count == 0)
                {
                    _zones.Remove(zoneId);
                    if (SelectedZone?.Id == zoneId) DeselectZoneInternal();
                    if (HoveredZone?.Id == zoneId) HoveredZone = null;
                    continue;
                }

                var subComponents = SplitIntoConnectedComponents(zone.Tiles);
                if (subComponents.Count > 1)
                {
                    bool wasAuto = zone.AutoPlantEnabled;
                    _zones.Remove(zoneId);
                    if (SelectedZone?.Id == zoneId) DeselectZoneInternal();

                    foreach (var comp in subComponents)
                    {
                        int newId = _nextZoneId++;
                        var newZone = new FarmZone(newId, $"Грядка #{newId}", comp)
                        {
                            AutoPlantEnabled = wasAuto
                        };
                        _zones[newId] = newZone;
                        foreach (var pos in comp)
                        {
                            _tileToZoneId[pos] = newId;
                        }

                        if (wasAuto)
                        {
                            foreach (var (x, y) in comp)
                            {
                                if (FarmJobManager.Instance.IsGardenBed(x, y) && !CropGrowthManager.Instance.HasCrop(x, y))
                                    JobBroker.Instance.RegisterPlanting(x, y, newId);
                            }
                        }
                    }
                }
            }
        }

        Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
    }

    public void SetHoveredTile(int x, int y)
    {
        FarmZone newHovered = null;
        lock (_lock)
        {
            if (_tileToZoneId.TryGetValue((x, y), out int zoneId))
            {
                _zones.TryGetValue(zoneId, out newHovered);
            }
        }

        if (HoveredZone != newHovered)
        {
            HoveredZone = newHovered;
            OnZoneHovered?.Invoke(HoveredZone);
            OnZonesUpdated?.Invoke();
        }
    }

    public void SelectZoneAt(int x, int y)
    {
        FarmZone targetZone = null;
        lock (_lock)
        {
            if (_tileToZoneId.TryGetValue((x, y), out int zoneId))
            {
                _zones.TryGetValue(zoneId, out targetZone);
            }
        }

        if (targetZone != null)
        {
            SelectedZone = targetZone;
            OnZoneSelected?.Invoke(SelectedZone);
            OnZonesUpdated?.Invoke();
        }
        else
        {
            DeselectZone();
        }
    }

    public void DeselectZone()
    {
        if (SelectedZone != null)
        {
            DeselectZoneInternal();
            OnZoneDeselected?.Invoke();
            OnZonesUpdated?.Invoke();
        }
    }

    private void DeselectZoneInternal() => SelectedZone = null;

    public bool TryGetZoneAt(int x, int y, out FarmZone zone)
    {
        lock (_lock)
        {
            if (_tileToZoneId.TryGetValue((x, y), out int zoneId))
            {
                return _zones.TryGetValue(zoneId, out zone);
            }
            zone = null;
            return false;
        }
    }

    public bool TryGetZoneById(int zoneId, out FarmZone zone)
    {
        lock (_lock)
        {
            return _zones.TryGetValue(zoneId, out zone);
        }
    }

    public List<FarmZone> GetAllZones()
    {
        lock (_lock)
        {
            return new List<FarmZone>(_zones.Values);
        }
    }

    private static List<HashSet<(int X, int Y)>> SplitIntoConnectedComponents(HashSet<(int X, int Y)> tiles)
    {
        var result = new List<HashSet<(int X, int Y)>>();
        var unvisited = new HashSet<(int X, int Y)>(tiles);
        var queue = new Queue<(int X, int Y)>();

        while (unvisited.Count > 0)
        {
            using var enumerator = unvisited.GetEnumerator();
            enumerator.MoveNext();
            var start = enumerator.Current;

            var comp = new HashSet<(int X, int Y)>();
            queue.Enqueue(start);
            unvisited.Remove(start);

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                comp.Add(curr);

                foreach (var dir in CardinalDirections)
                {
                    var neighbor = (curr.X + dir.X, curr.Y + dir.Y);
                    if (unvisited.Remove(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            result.Add(comp);
        }

        return result;
    }
}