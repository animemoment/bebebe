using System;
using System.Collections.Generic;

namespace Game.Core.WorldStreaming;

// ============================================================================
// §118.1 фаза 3: реестр зон поселений — «одна зона на поселение» (фундамент
// мульти-поселений). Просто словарь в памяти, БЕЗ Godot API (можно из фоновых
// потоков). Зона поселения = мировой прямоугольник ActiveArea.Size²;
// ПЕРЕСЕЧЕНИЕ прямоугольников двух поселений запрещено (Register → false:
// клетки одной зоны не могут принадлежать двум поселениям сразу — иначе
// расходятся дельты/мутации). Касание краями — НЕ пересечение.
// ============================================================================

/// <summary>
/// Реестр активных зон поселений. Зона материализуется лениво при первом
/// обращении (TryGetZone/SetActiveZone) — ПУСТОЙ ActiveArea с origin;
/// данные появляются после zone.Fill(seed, version) (зовёт потребитель,
/// например интеграция Lead после выбора спавна).
/// </summary>
public sealed class ActiveAreaRegistry
{
    private sealed class ZoneRecord
    {
        public string Id = "";
        public long OriginX;
        public long OriginY;
        public ActiveArea Area; // ленивая материализация
    }

    private readonly Dictionary<string, ZoneRecord> _zones = new();

    /// <summary>Активная зона (за которой сейчас играют). null — не выбрана.</summary>
    public ActiveArea ActiveZone { get; private set; }

    /// <summary>Сколько зон зарегистрировано.</summary>
    public int Count => _zones.Count;

    /// <summary>
    /// Зарегистрировать зону поселения [originX, originX+Size) ×
    /// [originY, originY+Size). Отрицательные origin допустимы (мир бесконечен,
    /// §118.1). false — пустой id, ДУБЛЬ id или прямоугольник ПЕРЕСЕКАЕТСЯ
    /// с уже зарегистрированной зоной (строгое перекрытие; касание краями — ок).
    /// </summary>
    public bool RegisterZone(string id, long originX, long originY)
    {
        if (string.IsNullOrEmpty(id))
            return false;
        if (_zones.ContainsKey(id))
            return false;

        foreach (ZoneRecord z in _zones.Values)
        {
            if (originX < z.OriginX + ActiveArea.Size && z.OriginX < originX + ActiveArea.Size
                && originY < z.OriginY + ActiveArea.Size && z.OriginY < originY + ActiveArea.Size)
                return false;
        }

        _zones[id] = new ZoneRecord { Id = id, OriginX = originX, OriginY = originY };
        return true;
    }

    /// <summary>
    /// Снять зону с регистрации. true — зона была. Если снималась АКТИВНАЯ
    /// зона — ActiveZone сбрасывается в null.
    /// </summary>
    public bool UnregisterZone(string id)
    {
        if (string.IsNullOrEmpty(id) || !_zones.Remove(id, out ZoneRecord removed))
            return false;
        if (ReferenceEquals(ActiveZone, removed.Area))
            ActiveZone = null;
        return true;
    }

    /// <summary>
    /// Зона по id. true — зона зарегистрирована; материализуется при первом
    /// обращении и кэшируется (одна и та же инстанция на id). ПУСТАЯ:
    /// IsFilled=false до zone.Fill(seed, version).
    /// </summary>
    public bool TryGetZone(string id, out ActiveArea zone)
    {
        zone = null;
        if (string.IsNullOrEmpty(id) || !_zones.TryGetValue(id, out ZoneRecord rec))
            return false;
        rec.Area ??= new ActiveArea(rec.OriginX, rec.OriginY);
        zone = rec.Area;
        return true;
    }

    /// <summary>
    /// Назначить АКТИВНУЮ зону (где сейчас играет игрок). false — id не
    /// зарегистрирован (ActiveZone не меняется). Зона материализуется как в
    /// TryGetZone.
    /// </summary>
    public bool SetActiveZone(string id)
    {
        if (!TryGetZone(id, out ActiveArea zone))
            return false;
        ActiveZone = zone;
        return true;
    }
}
