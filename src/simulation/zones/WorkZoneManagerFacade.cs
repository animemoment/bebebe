using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Тонкий фасад над ZoneManager для Work-зон.
/// Сохраняет API круга 1-2 (WorkZoneController, SelectTool, AgentSimulationThread).
/// §119 P0: модель зон — центр + радиус (64..512) без материализации клеток;
/// MaxWorkers → DesiredWorkers (заявка без потолка); добавлены SetRadius /
/// SetDesiredWorkers / RemoveZoneAt (снос зоны по центру — путь P5 для
/// сноса центрального блока). Легаси-методы SetTiles/SetWorkers оставлены
/// как алиасы до P2 (UI их вызывает; значение трактуется как радиус).
/// </summary>
public sealed class WorkZoneManager
{
    public static WorkZoneManager Instance { get; } = new();

    static WorkZoneManager()
    {
        // §119 P5: снос центрального блока = снос зоны-якоря. Подключаем хук
        // сразу (P0 слит): StructureManager.RemoveStructure зовёт RemoveZoneAt
        // по центру блока; работы зоны падают в непокрытые роутера.
        StructureManager.CentralBlockRemovedHook = (cx, cy) => Instance.RemoveZoneAt(cx, cy);
    }

    private ZoneManager Z => ZoneManager.Instance;

    public WorkZone SelectedZone => AdaptWork(Z.SelectedZone);

    private readonly Dictionary<Action<WorkZone>, Action<Zone>> _selectedHandlers = new();
    private readonly object _evtLock = new();

    public event Action OnZonesUpdated
    {
        add => Z.OnZonesUpdated += value;
        remove => Z.OnZonesUpdated -= value;
    }
    public event Action<WorkZone> OnZoneSelected
    {
        add
        {
            if (value == null) return;
            lock (_evtLock)
            {
                Action<Zone> wrapper = z => value(AdaptWork(z));
                _selectedHandlers[value] = wrapper;
                Z.OnZoneSelected += wrapper;
            }
        }
        remove
        {
            if (value == null) return;
            lock (_evtLock)
            {
                if (_selectedHandlers.Remove(value, out var wrapper))
                    Z.OnZoneSelected -= wrapper;
            }
        }
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
            z.DesiredWorkers, z.JobMask, z.ChunkIndices)
        {
            TilesTarget = z.TilesTarget
        };
        w.PriorityOverride = z.PriorityOverride;
        w.Enabled = z.Enabled;
        w.AnchorBlockId = z.AnchorBlockId;
        // AssignedCount — поле; копируем текущее значение для чтения.
        w.AssignedCount = Volatile.Read(ref z.AssignedCount);
        return w;
    }

    /// <summary>
    /// §119 P0: создать зону. Параметр — РАДИУС 64..512 (кламп; до P2 UI шлёт
    /// легаси-значение слайдера, оно клампится), desiredWorkers — заявка 0..999.
    /// jobMask обязан содержать ровно один бит типа работ.
    /// </summary>
    public bool CreateZoneAt(int cx, int cy, ulong jobMask, int radiusTiles, int desiredWorkers, out WorkZone zone, out string failReason)
    {
        bool ok = Z.CreateWorkZoneAt(cx, cy, jobMask, radiusTiles, desiredWorkers, out var z, out failReason);
        zone = ok ? AdaptWork(z) : null;
        return ok;
    }

    public bool RemoveZone(int id) => Z.RemoveZone(id);

    /// <summary>§119 P0/P5: снести Work-зону с ЦЕНТРОМ в этой клетке (снос центрального блока).</summary>
    public bool RemoveZoneAt(int cx, int cy) => Z.RemoveZoneAtCenter(cx, cy);

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

    /// <summary>§119 P0: задать радиус зоны (64..512, кламп).</summary>
    public bool SetRadius(int id, int radius) => Z.SetWorkRadius(id, radius);

    /// <summary>§119: задать заявку на число рабочих (0..999, без потолка).</summary>
    public bool SetDesiredWorkers(int id, int v) => Z.SetWorkDesiredWorkers(id, v);

    /// <summary>
    /// §119: вкл/выкл зоны (тумблер P6). Выключенная зона НЕ владеет клетками
    /// (покрытие пересчитывается по событию — ScheduleRepartition внутри).
    /// </summary>
    public bool SetEnabled(int id, bool enabled) => Z.SetWorkEnabled(id, enabled);

    /// <summary>
    /// §119 P2: заявка DesiredWorkers → перевод ровно N ближайших свободных
    /// Laborer'ов в Worker'а зоны (WorkerAssignmentSystem). Вызывается слайдером
    /// UI / тестами. Возвращает (стало Worker'ов, набрано, разжато).
    /// </summary>
    public (int Assigned, int Recruited, int Released) BeginAssign(int zoneId, AgentDataPool pool, SimulationContext ctx) =>
        WorkerAssignmentSystem.Instance.BeginAssign(zoneId, pool, ctx);

    /// <summary>ЛЕГАСИ-алиас SetRadius до P2 (UI): аргумент трактуется как радиус.</summary>
    public bool SetTiles(int id, int target) => Z.SetWorkRadius(id, target);

    /// <summary>ЛЕГАСИ-алиас SetDesiredWorkers до P2 (UI).</summary>
    public bool SetWorkers(int id, int v) => Z.SetWorkDesiredWorkers(id, v);

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
