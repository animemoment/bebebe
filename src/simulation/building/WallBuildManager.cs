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
    /// </summary>
    public event Action<List<(int X, int Y)>> OnTilesUpdated;

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
    /// </summary>
    private void NotifyChanged(int x, int y)
    {
        var changed = new List<(int X, int Y)>(5)
        {
            (x, y),
            (x, y - 1), // верх
            (x, y + 1), // низ
            (x - 1, y), // лево
            (x + 1, y)  // право
        };

        OnTilesUpdated?.Invoke(changed);
    }
}