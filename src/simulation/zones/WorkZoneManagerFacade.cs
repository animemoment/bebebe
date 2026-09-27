using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Тонкий фасад над ZoneManager для Work-зон.
/// Сохраняет API круга 1-2 (WorkZoneController, SelectTool, AgentSimulationThread).
/// </summary>
public sealed class WorkZoneManager
{
    public static WorkZoneManager Instance { get; } = new();

    private ZoneManager Z => ZoneManager.Instance;

    public WorkZone SelectedZone => AdaptWork(Z.SelectedZone);

    public event Action OnZonesUpdated
    {
        add => Z.OnZonesUpdated += value;
        remove => Z.OnZonesUpdated -= value;
    }
    public event Action<WorkZone> OnZoneSelected
    {
        add => Z.OnZoneSelected += z => value?.Invoke(AdaptWork(z));
        remove { }
    }
    public event Action OnZoneDeselected
    {
        add => Z.OnZoneDeselected += value;
        remove => Z.OnZoneDeselected -= value;
    }

#pragma warning disable CS0067
    public event Action<WorkZone> OnZoneHovered;
#pragma warning restore CS0067

    public static void SetMapBounds(int w, int h) => ZoneManager.SetMapBounds(w, h);

    private static WorkZone AdaptWork(Zone z)
    {
        if (z == null || z.Kind != ZoneKind.Work) return null;
        var w = new WorkZone(z.Id, z.Name, z.CenterX, z.CenterY, z.RadiusTiles,
            z.TilesTarget, z.MaxWorkers, z.JobMask, ToIntTiles(z.Tiles), z.ChunkIndices);
        w.PriorityOverride = z.PriorityOverride;
        // AssignedCount — поле; копируем текущее значение для чтения.
        w.AssignedCount = Volatile.Read(ref z.AssignedCount);
        return w;
    }

    private static HashSet<(int, int)> ToIntTiles(HashSet<(int X, int Y)> tiles)
    {
        // ValueTuple с именами и без — один тип; прямое копирование.
        var set = new HashSet<(int, int)>(tiles.Count);
        foreach (var t in tiles)
            set.Add((t.X, t.Y));
        return set;
    }

    public bool CreateZoneAt(int cx, int cy, ulong jobMask, int tilesTarget, int maxWorkers, out WorkZone zone, out string failReason)
    {
        bool ok = Z.CreateWorkZoneAt(cx, cy, jobMask, tilesTarget, maxWorkers, out var z, out failReason);
        zone = ok ? AdaptWork(z) : null;
        return ok;
    }

    public bool RemoveZone(int id) => Z.RemoveZone(id);

    public bool TryGetZoneAt(int x, int y, out WorkZone zone)
    {
        if (Z.TryGetZoneAt(x, y, out var z) && z.Kind == ZoneKind.Work)
        {
            zone = AdaptWork(z);
            return true;
        }
        zone = null;
        return false;
    }

    public bool TryGetZoneById(int zoneId, out WorkZone zone)
    {
        if (Z.TryGetZoneById(zoneId, out var z) && z.Kind == ZoneKind.Work)
        {
            zone = AdaptWork(z);
            return true;
        }
        zone = null;
        return false;
    }

    public List<WorkZone> GetAllZones()
    {
        var all = Z.GetAllZones();
        var result = new List<WorkZone>(all.Count);
        foreach (var z in all)
        {
            if (z.Kind == ZoneKind.Work)
                result.Add(AdaptWork(z));
        }
        return result;
    }

    public bool SetTiles(int id, int target) => Z.SetWorkTiles(id, target);

    public bool SetWorkers(int id, int v) => Z.SetWorkWorkers(id, v);

    public bool SetPriorityOverride(int id, int v) => Z.SetWorkPriorityOverride(id, v);

    /// <summary>Выбрать зону; возвращает адаптированную Work-зону или null.</summary>
    public WorkZone SelectZoneAt(int x, int y)
    {
        var hit = Z.SelectZoneAt(x, y);
        return AdaptWork(hit);
    }

    public void DeselectZone()
    {
        if (Z.SelectedZone != null && Z.SelectedZone.Kind == ZoneKind.Work)
            Z.DeselectZone();
    }

    public int DispatchZones(AgentDataPool pool, SimulationContext ctx) => Z.DispatchZones(pool, ctx);
}
