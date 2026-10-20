using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

// ============================================================================
// §119 P2: НАЗНАЧЕНИЕ РАБОЧИХ ЗОНАМ. DesiredWorkers зоны — ЗАЯВКА: при
// изменении (слайдер/фасад/BeginAssign) ровно N ближайших к центру зоны
// свободных Laborer'ов переводятся в Worker и привязываются к зоне.
//
// Контракт:
//  * AssignWorkers(zoneId, desiredN) — идемпотентное выравнивание: если
//    Worker'ов зоны меньше N — перевести из свободных Laborer'ов; больше —
//    разжать лишних (обратно в Laborer, в общий пул бездельников).
//  * BeginAssign(zoneId) — публичная точка после SetDesiredWorkers (слайдер
//    P2 UI). Снимок DesiredWorkers берётся в момент вызова.
//  * Выбор кандидатов — БЛИЖАЙШИЕ к центру зоны (спека:.worker'ы живут
//    у зоны); разжатие — дальние первыми.
//  * Согласованность: агент в Worker без зоны или с зоной-призраком —
//    разжимается (Reconcile).
// Вызывается из sim-потока (AgentSimulationThread, редкие события) —
// без Interlocked-экзотики, обычный детерминированный проход.
// ============================================================================

public sealed class WorkerAssignmentSystem
{
    public static WorkerAssignmentSystem Instance { get; } = new();

    /// <summary>Радиус найма Worker'ов вокруг центра зоны (клеток).</summary>
    public const int RecruitRadiusTiles = 64;

    /// <summary>
    /// §119 P2: выровнять число Worker'ов зоны по заявке DesiredWorkers.
    /// Возвращает (стало, набрано, разжато). Вызывается в sim-потоке.
    /// </summary>
    public (int Assigned, int Recruited, int Released) AssignWorkers(int zoneId, AgentDataPool pool, SimulationContext ctx)
    {
        if (zoneId <= 0 || pool == null || ctx == null)
            return (0, 0, 0);
        if (!ZoneManager.Instance.TryGetZoneById(zoneId, out var zone) || zone == null || zone.Kind != ZoneKind.Work)
            return (0, 0, 0);

        int desired = Math.Clamp(zone.DesiredWorkers, 0, WorkZone.MaxDesiredWorkers);

        // Текущие Worker'ы зоны (живые агенты).
        List<int> current = new(8);
        for (int i = 0; i < pool.Capacity; i++)
        {
            if (pool.Classes[i] == AgentClass.Worker && pool.AssignedZoneId[i] == zoneId)
                current.Add(i);
        }

        int recruited = 0, released = 0;

        // Перебор: лишних разжать (дальние от центра первыми).
        if (current.Count > desired)
        {
            int cxSort = zone.CenterX, cySort = zone.CenterY;
            current.Sort((a, b) =>
            {
                long da = DistSq(pool, a, cxSort, cySort);
                long db = DistSq(pool, b, cxSort, cySort);
                return db.CompareTo(da); // дальние первыми
            });
            for (int k = desired; k < current.Count; k++)
            {
                ReleaseToLaborer(current[k], pool, ctx);
                released++;
            }
            current.RemoveRange(desired, current.Count - desired);
        }
        else if (current.Count < desired)
        {
            // Найм: ближайшие к центру зоны свободные Laborer'ы.
            int need = desired - current.Count;
            int cx = zone.CenterX, cy = zone.CenterY;
            int r = RecruitRadiusTiles;
            // Спираль по радиусу от центра: клетки диском, агенты — по CurrentCell.
            var candidates = new List<(long D, int Idx)>(64);
            for (int i = 0; i < pool.Capacity && candidates.Count < need * 4; i++)
            {
                if (pool.Classes[i] != AgentClass.Laborer)
                    continue;
                if (pool.States[i] != AgentState.Idle)
                    continue; // работающий Laborer не отрывается от дела
                if (pool.NeedsBehavior[i] != NeedBehavior.None)
                    continue;
                int acx = pool.CurrentCellX[i], acy = pool.CurrentCellY[i];
                int dx = acx - cx, dy = acy - cy;
                long d = (long)dx * dx + (long)dy * dy;
                if (d > (long)r * r)
                    continue;
                candidates.Add((d, i));
            }
            candidates.Sort(static (a, b) => a.D.CompareTo(b.D));
            int take = Math.Min(need, candidates.Count);
            for (int k = 0; k < take; k++)
            {
                int idx = candidates[k].Idx;
                if (pool.Classes[idx] != AgentClass.Laborer)
                    continue; // гонка внутри прохода невозможна (sim-поток), но дёшево проверить
                PromoteToWorker(idx, zoneId, pool);
                recruited++;
                current.Add(idx);
            }
        }

        int assignedNow = current.Count;
        return (assignedNow, recruited, released);
    }
    /// <summary>Публичная точка после изменения DesiredWorkers (слайдер/UI/фасад): выровнять состав.</summary>
    public (int Assigned, int Recruited, int Released) BeginAssign(int zoneId, AgentDataPool pool, SimulationContext ctx) =>
        AssignWorkers(zoneId, pool, ctx);

    /// <summary>Разжать всех Worker'ов зоны (зона удалена/выключена). Возвращает число разжатых.</summary>
    public int ReleaseZoneWorkers(int zoneId, AgentDataPool pool, SimulationContext ctx = null)
    {
        int released = 0;
        for (int i = 0; i < pool.Capacity; i++)
        {
            if (pool.Classes[i] == AgentClass.Worker && pool.AssignedZoneId[i] == zoneId)
            {
                ReleaseToLaborer(i, pool, ctx);
                released++;
            }
        }
        return released;
    }

    /// <summary>
    /// Согласование: Worker без зоны / с удалённой зоной / зона Enabled=false /
    /// DesiredWorkers=0 → разжать. Вызывается периодически (редко).
    /// </summary>
    public int Reconcile(AgentDataPool pool)
    {
        int fixedCount = 0;
        for (int i = 0; i < pool.Capacity; i++)
        {
            if (pool.Classes[i] != AgentClass.Worker)
                continue;
            int zid = pool.AssignedZoneId[i];
            bool keep = zid > 0
                && ZoneManager.Instance.TryGetZoneById(zid, out var z)
                && z != null && z.Kind == ZoneKind.Work
                && z.Enabled && z.DesiredWorkers > 0;
            if (!keep)
            {
                ReleaseToLaborer(i, pool, null);
                fixedCount++;
            }
        }
        return fixedCount;
    }

    private static long DistSq(AgentDataPool pool, int i, int cx, int cy)
    {
        int dx = pool.CurrentCellX[i] - cx;
        int dy = pool.CurrentCellY[i] - cy;
        return (long)dx * dx + (long)dy * dy;
    }

    /// <summary>Перевод агента в Worker зоны. Idle-агент, вызов из sim-потока.</summary>
    private static void PromoteToWorker(int i, int zoneId, AgentDataPool pool)
    {
        // Из общего пула бездельников убрать: Worker'а диспатчер пирамиды не трогает.
        JobDispatcher.Instance.IdleWorkers.RemoveIdleWorker(i, pool);
        pool.Classes[i] = AgentClass.Worker;
        pool.AssignedZoneId[i] = zoneId;
        pool.JobSearchTimer[i] = 0f; // Worker ищет работу немедленно (WorkerZoneJobSystem)
    }

    /// <summary>
    /// Разжать Worker'а обратно в Laborer (общий пул бездельников). С РАБОТОЙ:
    /// Classes уже Laborer → ReleaseJobWorker снимет клейм легаси-путём;
    /// прогон задания недопустим — разжатый агент зону не обслуживает.
    /// Прогресс в клетке сохранён (CellProgressStore).
    /// </summary>
    private static void ReleaseToLaborer(int i, AgentDataPool pool, SimulationContext ctx = null)
    {
        pool.Classes[i] = AgentClass.Laborer;
        pool.AssignedZoneId[i] = -1;
        if (pool.CurrentJobId[i] != -1 || pool.States[i] != AgentState.Idle)
        {
            // Работающий/идущий на работу экс-Worker: легаси-снятие задачи
            // (OnCancel + снятие клейма + в пул бездельников). ВАЖНО: Classes
            // уже Laborer — возврат в очередь зоны НЕ сработает, работа через
            // пирамиду будет раздана заново (прогресс в клетке не потерян).
            JobDispatcher.Instance.ReleaseJobWorker(i, pool, ctx);
        }
        else
        {
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(i, pool);
        }
    }
}
