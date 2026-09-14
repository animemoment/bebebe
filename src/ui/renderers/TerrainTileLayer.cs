using Godot;
using System;
using System.Collections.Generic;

namespace Game.UI;

/// <summary>
/// Стандарт отрисовки тайловых слоёв: движковый автотайлинг через terrain.
/// Вместо ручного подбора атлас-координат в C# (SetCell с вычисленным atlas —
/// так ломались стены: код рассинхронизировался с разметкой) слой копит грязные
/// клетки, а во Flush одним вызовом SetCellsTerrainConnect отдаёт движку ТОЛЬКО
/// клетки, где тайл реально есть. Соседей для контекста движок подтягивает сам;
/// заливать зоной 3×3 нельзя — одна клетка расползалась в квадрат 3×3.
/// TileSet обязан содержать terrain-разметку (из сцены, как wood_wall_tile).
/// Главный поток: MarkDirty — откуда угодно, Flush — только из _Process.
/// </summary>
public sealed class TerrainTileLayer
{
    private readonly TileMapLayer _layer;
    private readonly int _terrainSetId;
    private readonly int _terrainId;
    private readonly HashSet<Vector2I> _dirty = new();

    public TerrainTileLayer(TileMapLayer layer, int terrainSetId = 0, int terrainId = 0)
    {
        _layer = layer;
        _terrainSetId = terrainSetId;
        _terrainId = terrainId;
    }

    public int DirtyCount => _dirty.Count;

    public void MarkDirty(Vector2I cell) => _dirty.Add(cell);

    public void MarkDirty(IEnumerable<Vector2I> cells)
    {
        foreach (var c in cells)
            _dirty.Add(c);
    }

    /// <summary>
    /// Пересчитать грязные клетки движком. exists(cell) — есть ли тайл сейчас
    /// (нет — EraseCell, иначе — в набор для SetCellsTerrainConnect).
    /// </summary>
    public void Flush(Func<Vector2I, bool> exists)
    {
        if (_dirty.Count == 0) return;
        if (_layer?.TileSet == null) { _dirty.Clear(); return; }

        foreach (var c in _dirty)
        {
            if (!exists(c))
                _layer.EraseCell(c);
        }
        var cells = new Godot.Collections.Array<Vector2I>();
        foreach (var c in _dirty)
        {
            if (exists(c))
                cells.Add(c);
        }
        _dirty.Clear();
        if (cells.Count > 0)
            _layer.SetCellsTerrainConnect(cells, _terrainSetId, _terrainId, true);
    }

    /// <summary>Полная перестройка слоя: стереть всё и залить terrain'ом набор клеток.</summary>
    public void RebuildAll(IEnumerable<Vector2I> cells)
    {
        if (_layer?.TileSet == null) return;
        _layer.Clear();
        _dirty.Clear();
        var arr = new Godot.Collections.Array<Vector2I>();
        foreach (var c in cells)
        {
            arr.Add(c);
            _dirty.Add(c);
        }
        if (arr.Count > 0)
            _layer.SetCellsTerrainConnect(arr, _terrainSetId, _terrainId, true);
        _dirty.Clear();
    }

    public void Clear()
    {
        _dirty.Clear();
        _layer?.Clear();
    }
}
