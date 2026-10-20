using System;
using System.Collections.Generic;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

// ============================================================================
// §119 P1: ПОКРЫТИЕ клетка→idЗоны, ОТДЕЛЬНО на каждый тип работ (спека
// task-15): O(1) проверка TryGetOwner вместо перебора зон. Карта = ushort[w*h]
// на JobTypeId (1024² = 2 МБ на тип), выделяется ЛЕНИВО — только когда есть
// хотя бы одна живая зона этого типа; тип без зон освобождается.
//
// Правила владения (спека §119):
//  * Enabled == false или DesiredWorkers == 0 → зона НЕ владеет клетками;
//  * пересечение зон ОДНОГО типа: спорная клетка → зона с БОЛЬШИМ
//    AssignedCount; равенство → МЕНЬШИЙ zoneId (детерминизм);
//  * зоны РАЗНЫХ типов друг друга НЕ режут (карты независимы).
//
// Rebuild: по событиям зон (ScheduleRepartition → RequestRebuild), НЕ чаще
// 1р/2с (throttle 2000 мс). Запись ТОЛЬКО в свежесобранный словарь карт с
// атомарной заменой ссылки — читатели (TryGetOwner с любого потока) всегда
// видят консистентный снапшот. Клетки НЕ материализуются: покраска диска —
// по формуле dx²+dy²≤r² по bbox-строкам (сегмент круга), без HashSet.
// ============================================================================

public sealed class ZoneCoverageMap
{
    /// <summary>Throttle пересчёта (спека: не чаще 1р/2с).</summary>
    public const int RebuildThrottleMs = 2000;

    /// <summary>0 = клетка ничья; иначе zoneId (1..65535).</summary>
    private volatile Dictionary<JobTypeId, ushort[]> _maps = new();

    private readonly object _rebuildGate = new();
    private int _dirty;
    private long _lastRebuildMs = long.MinValue;
    private int _builtVersion = -1;
    private int _w = 1024;
    private int _h = 1024;

    /// <summary>Заявить пересчёт (вызывается ZoneManager.ScheduleRepartition).</summary>
    public void RequestRebuild() => Volatile.Write(ref _dirty, 1);

    /// <summary>O(1) владелец клетки по типу работ. false — клетка ничья.</summary>
    public bool TryGetOwner(int x, int y, JobTypeId type, out int zoneId)
    {
        zoneId = 0;
        var maps = _maps;
        if (!maps.TryGetValue(type, out var map))
            return false;
        int w = _w;
        if ((uint)x >= (uint)w || (uint)y >= (uint)_h)
            return false;
        ushort v = map[y * w + x];
        if (v == 0)
            return false;
        zoneId = v;
        return true;
    }

    /// <summary>
    /// Пересчитать, если заявка есть и throttle истёк. Вызывается из роутера
    /// (Push/Sweep) и тестами. true — пересчёт выполнен. Заявка при пропуске
    /// по throttle СОХРАНЯЕТСЯ (пересчёт не теряется, только откладывается).
    /// </summary>
    public bool RebuildIfNeeded(long nowMs)
    {
        if (Volatile.Read(ref _dirty) == 0)
            return false;
        lock (_rebuildGate)
        {
            if (Volatile.Read(ref _dirty) == 0)
                return false;
            if (_lastRebuildMs != long.MinValue && nowMs - _lastRebuildMs < RebuildThrottleMs)
                return false;
            RebuildLocked(nowMs);
            return true;
        }
    }

    /// <summary>Немедленный пересчёт в обход throttle (тесты). Возвращает число типов с картами.</summary>
    public int RebuildNow(long nowMs)
    {
        lock (_rebuildGate)
        {
            RebuildLocked(nowMs);
            return _maps.Count;
        }
    }

    private void RebuildLocked(long nowMs)
    {
        _w = ZoneManager.MapWidth;
        _h = ZoneManager.MapHeight;
        long cells = (long)_w * _h;
        if (cells == 0 || cells > 64_000_000)
            return; // защита от абсурдных размеров — карты не выделяем

        var zones = ZoneManager.Instance.GetDispatchSnapshot();
        var fresh = new Dictionary<JobTypeId, ushort[]>();

        // Группировка Work-зон по единственному типу + сортировка клипа:
        // AssignedCount desc (спорная клетка — зоне с большим назначением),
        // tie → меньший zoneId (детерминизм). Первым покрашенный — владелец.
        var byType = new Dictionary<JobTypeId, List<Zone>>();
        foreach (var z in zones)
        {
            if (z.Kind != ZoneKind.Work || !z.Enabled || z.DesiredWorkers <= 0)
                continue;
            if (z.JobMask == 0 || (z.JobMask & (z.JobMask - 1UL)) != 0)
                continue; // P0 гарантирует ровно один бит; защита от кривых данных
            var type = (JobTypeId)System.Numerics.BitOperations.TrailingZeroCount(z.JobMask);
            if (type == JobTypeId.None)
                continue;
            if (!byType.TryGetValue(type, out var list))
                byType[type] = list = new List<Zone>();
            list.Add(z);
        }

        foreach (var kv in byType)
        {
            var map = new ushort[_w * _h];
            var list = kv.Value;
            list.Sort(static (a, b) =>
            {
                int c = b.AssignedCount.CompareTo(a.AssignedCount);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });
            foreach (var z in list)
                PaintZoneOnMap(map, z, _w, _h);
            fresh[kv.Key] = map;
        }

        // Атомарная замена: читатели видят либо старый, либо новый полный набор.
        _maps = fresh;
        _builtVersion = ZoneManager.Instance.ZonesVersion;
        _lastRebuildMs = nowMs;
        Volatile.Write(ref _dirty, 0);
    }

    /// <summary>Покрасить диск зоны: строки bbox, сегмент круга по формуле; клетка — только если ещё ничья.</summary>
    private static void PaintZoneOnMap(ushort[] map, Zone z, int w, int h)
    {
        if (z.Id > 65535)
            return; // id не умещается в ushort — владение не записываем (честно ничья)
        ushort id = (ushort)z.Id;
        int cx = z.CenterX, cy = z.CenterY, r = z.RadiusTiles;
        int y0 = Math.Max(0, cy - r), y1 = Math.Min(h - 1, cy + r);
        long rr = (long)r * r;
        for (int y = y0; y <= y1; y++)
        {
            long dy = y - cy;
            long rem = rr - dy * dy;
            if (rem < 0)
                continue;
            int dxmax = (int)Math.Sqrt(rem);
            int x0 = Math.Max(0, cx - dxmax), x1 = Math.Min(w - 1, cx + dxmax);
            int rowBase = y * w;
            for (int x = x0; x <= x1; x++)
            {
                int idx = rowBase + x;
                if (map[idx] == 0)
                    map[idx] = id;
            }
        }
    }

    /// <summary>Полный сброс (смена мира/сида): карты освободить, заявку снять.</summary>
    public void Reset()
    {
        lock (_rebuildGate)
        {
            _maps = new Dictionary<JobTypeId, ushort[]>();
            Volatile.Write(ref _dirty, 0);
            _lastRebuildMs = long.MinValue;
            _builtVersion = -1;
        }
    }

    /// <summary>Для тестов/диагностики: число типов с картами.</summary>
    public int TypeMapCount => _maps.Count;

    /// <summary>Для диагностики: версия зон, по которой построены текущие карты.</summary>
    public int BuiltVersion => Volatile.Read(ref _builtVersion);
}
