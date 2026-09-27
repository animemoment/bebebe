using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Менеджер строительства деревянных стен.
/// Хранит множество установленных стен, управляет их добавлением/удалением
/// и оповещает подписчиков об изменённых тайлах через событие.
/// Не зависит от Godot API.
/// </summary>
public class WallBuildManager
{
    private readonly HashSet<(int X, int Y)> _walls = new();

    /// <summary>
    /// Событие вызывается при изменении тайлов стен.
    /// Список содержит координаты изменившихся тайлов (сама стена + соседи).
    /// БАГ D: событие дебаунсится (см. NotifyChanged) — при массовой стройке
    /// приходит ОДИН список на пачку стен, а не 100 списков по 5 клеток.
    /// </summary>
    public event Action<List<(int X, int Y)>> OnTilesUpdated;

    // Дебаунс NotifyChanged (PLAN.md §12, 200мс): пачка стен за тик копит
    // клетки в _pending, подписчик получает один List. Вызывается только
    // из главного потока (MapRenderer.OnBlueprintCompleted), lock не нужен.
    private readonly HashSet<(int X, int Y)> _pending = new();
    private long _lastFlushTicks;
    private const long DebounceMs = 200;

    /// <summary>
    /// Добавляет стену в позиции (x, y).
    /// </summary>
    public void AddWall(int x, int y)
    {
        if (_walls.Contains((x, y)))
            return; // стена уже есть

        _walls.Add((x, y));
        NotifyChanged(x, y);
    }

    /// <summary>
    /// Удаляет стену в позиции (x, y).
    /// </summary>
    public void RemoveWall(int x, int y)
    {
        if (!_walls.Remove((x, y)))
            return; // стены не было

        NotifyChanged(x, y);
    }

    /// <summary>
    /// Проверяет, есть ли стена в позиции (x, y).
    /// </summary>
    public bool IsWallAt(int x, int y) => _walls.Contains((x, y));

    /// <summary>
    /// Возвращает копию множества всех стен (для потокобезопасности).
    /// </summary>
    public HashSet<(int X, int Y)> GetAllWalls() => new(_walls);

    /// <summary>
    /// Уведомляет подписчиков об изменении тайла (x, y) и его четырёх соседей.
    /// Стену с соседями копим в _pending; событие шлём сразу, только если
    /// прошло ≥200мс с прошлого флаша, иначе — ждём FlushPending (его зовёт
    /// MapRenderer._Process каждый кадр, главный поток). Одиночная стена
    /// при простое уходит мгновенно (дебаунс уже истёк), пачка за тик — одним
    /// списком. List создаётся один на флаш, а не один на стену.
    /// </summary>
    private void NotifyChanged(int x, int y)
    {
        _pending.Add((x, y));
        _pending.Add((x, y - 1)); // верх
        _pending.Add((x, y + 1)); // низ
        _pending.Add((x - 1, y)); // лево
        _pending.Add((x + 1, y)); // право

        long now = System.Environment.TickCount64;
        if (now - _lastFlushTicks >= DebounceMs)
            FlushPending();
    }

    /// <summary>
    /// Слить накопленные клетки одним событием. Звать из главного потока
    /// (MapRenderer._Process) — там SetCellsTerrainConnect всё равно идёт
    /// покадрово через TerrainTileLayer, хвост ≤1 кадра (~16мс) незаметен.
    /// </summary>
    public void FlushPending()
    {
        if (_pending.Count == 0)
            return;
        _lastFlushTicks = System.Environment.TickCount64;
        var changed = new List<(int X, int Y)>(_pending);
        _pending.Clear();
        // E63/E64: сколько клеток за флаш + глубина очереди (растёт = рендер тонет).
        Game.Core.SimEvents.Count("render.flush_cells", changed.Count);
        Game.Core.SimEvents.Series("render.flush_queue", _pending.Count);

        OnTilesUpdated?.Invoke(changed);
    }
}