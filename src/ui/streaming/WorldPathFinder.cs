using System;
using System.Collections.Generic;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;

namespace Game.UI.Streaming;

/// <summary>
/// Поиск пути для людей мира (§30): A* по ПРОЦЕДУРНОЙ поверхности (<see cref="WorldCellQuery"/>),
/// поэтому путь считается и в незагруженных чанках — данные чанка не нужны.
/// Проходимо: трава; вода и горы обходятся. Поиск ограничен бюджетом и радиусом,
/// словари переиспользуются между поисками (без GC-мусора на каждый путь).
/// Только main thread.
/// </summary>
public sealed class WorldPathFinder
{
    /// <summary>Максимум раскрытых клеток за один поиск.</summary>
    public const int MaxExpanded = 24_000;

    /// <summary>Максимальное отклонение от прямой (клетки): больше — считаем недостижимым.</summary>
    public const int MaxDetourCells = 384;

    private const float Diag = 1.41421356f;

    private readonly ulong _seed;
    private readonly uint _version;

    private readonly Dictionary<(long X, long Y), float> _cost = new(4096);
    private readonly Dictionary<(long X, long Y), (long X, long Y)> _from = new(4096);
    private readonly List<long> _heapX = new(1024);
    private readonly List<long> _heapY = new(1024);
    private readonly List<float> _heapG = new(1024);
    private readonly List<float> _heapF = new(1024);

    public WorldPathFinder(ulong seed, uint generatorVersion)
    {
        _seed = seed;
        _version = generatorVersion;
    }

    /// <summary>Проходима ли клетка (трава; вода и горы — нет).</summary>
    public bool Walkable(long cellX, long cellY)
        => WorldCellQuery.TerrainAt(_seed, _version, cellX, cellY) == BaseTerrainKind.Grass;

    /// <summary>
    /// Найти путь из клетки в клетку. Возвращает false, если цели нет смысла достигать
    /// (цель непроходима, путь длиннее лимита или бюджет поиска исчерпан).
    /// </summary>
    public bool TryFindPath(long startX, long startY, long targetX, long targetY, List<(long X, long Y)> path)
    {
        path.Clear();
        if (!Walkable(targetX, targetY))
            return false;
        if (!SnapToWalkable(ref startX, ref startY))
            return false;
        if (startX == targetX && startY == targetY)
        {
            path.Add((targetX, targetY));
            return true;
        }
        if (Math.Abs(targetX - startX) > MaxDetourCells || Math.Abs(targetY - startY) > MaxDetourCells)
            return false;

        _cost.Clear();
        _from.Clear();
        _heapX.Clear();
        _heapY.Clear();
        _heapG.Clear();
        _heapF.Clear();

        var start = (X: startX, Y: startY);
        _cost[start] = 0f;
        Push(startX, startY, 0f, Heuristic(startX, startY, targetX, targetY));

        int expanded = 0;
        while (_heapX.Count > 0)
        {
            Pop(out long cx, out long cy, out float g);
            // Устаревшая запись кучи (стоимость уже улучшена) — не раскрываем повторно:
            // иначе дубли жгут бюджет MaxExpanded и путь «не находится».
            if (g > _cost[(cx, cy)])
                continue;
            float baseCost = g;

            if (cx == targetX && cy == targetY)
            {
                Reconstruct(targetX, targetY, path);
                return path.Count > 0;
            }

            if (++expanded > MaxExpanded)
                break;

            for (int ox = -1; ox <= 1; ox++)
            {
                for (int oy = -1; oy <= 1; oy++)
                {
                    if (ox == 0 && oy == 0)
                        continue;
                    long nx = cx + ox;
                    long ny = cy + oy;
                    if (Math.Abs(nx - startX) > MaxDetourCells || Math.Abs(ny - startY) > MaxDetourCells)
                        continue;
                    if (!Walkable(nx, ny))
                        continue;
                    // Диагональ не срезает угол между двумя непроходимыми клетками.
                    if (ox != 0 && oy != 0 && (!Walkable(cx + ox, cy) || !Walkable(cx, cy + oy)))
                        continue;

                    float step = ox != 0 && oy != 0 ? Diag : 1f;
                    float cost = baseCost + step;
                    var key = (X: nx, Y: ny);
                    if (_cost.TryGetValue(key, out float known) && known <= cost)
                        continue;
                    _cost[key] = cost;
                    _from[key] = (cx, cy);
                    Push(nx, ny, cost, cost + Heuristic(nx, ny, targetX, targetY));
                }
            }
        }
        return false;
    }

    /// <summary>Ближайшая проходимая стартовая клетка (агент может стоять на воде/горе).</summary>
    private bool SnapToWalkable(ref long startX, ref long startY)
    {
        if (Walkable(startX, startY))
            return true;
        for (int radius = 1; radius <= 12; radius++)
        {
            for (long ox = -radius; ox <= radius; ox++)
            {
                for (long oy = -radius; oy <= radius; oy++)
                {
                    if (Math.Abs(ox) != radius && Math.Abs(oy) != radius)
                        continue;
                    if (!Walkable(startX + ox, startY + oy))
                        continue;
                    startX += ox;
                    startY += oy;
                    return true;
                }
            }
        }
        return false;
    }

    private static float Heuristic(long x, long y, long tx, long ty)
    {
        long dx = Math.Abs(tx - x);
        long dy = Math.Abs(ty - y);
        long min = Math.Min(dx, dy);
        return min * Diag + (Math.Max(dx, dy) - min);
    }

    private void Reconstruct(long targetX, long targetY, List<(long X, long Y)> path)
    {
        var current = (X: targetX, Y: targetY);
        while (true)
        {
            path.Add(current);
            if (!_from.TryGetValue(current, out (long X, long Y) previous))
                break;
            current = previous;
        }
        path.Reverse();
    }

    private void Push(long x, long y, float g, float f)
    {
        _heapX.Add(x);
        _heapY.Add(y);
        _heapG.Add(g);
        _heapF.Add(f);
        int i = _heapX.Count - 1;
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (_heapF[parent] <= _heapF[i])
                break;
            Swap(parent, i);
            i = parent;
        }
    }

    private void Pop(out long x, out long y, out float g)
    {
        x = _heapX[0];
        y = _heapY[0];
        g = _heapG[0];
        int last = _heapX.Count - 1;
        _heapX[0] = _heapX[last];
        _heapY[0] = _heapY[last];
        _heapG[0] = _heapG[last];
        _heapF[0] = _heapF[last];
        _heapX.RemoveAt(last);
        _heapY.RemoveAt(last);
        _heapG.RemoveAt(last);
        _heapF.RemoveAt(last);

        int count = _heapX.Count;
        int i = 0;
        while (true)
        {
            int left = i * 2 + 1;
            int right = left + 1;
            int smallest = i;
            if (left < count && _heapF[left] < _heapF[smallest])
                smallest = left;
            if (right < count && _heapF[right] < _heapF[smallest])
                smallest = right;
            if (smallest == i)
                break;
            Swap(smallest, i);
            i = smallest;
        }
    }

    private void Swap(int a, int b)
    {
        (_heapX[a], _heapX[b]) = (_heapX[b], _heapX[a]);
        (_heapY[a], _heapY[b]) = (_heapY[b], _heapY[a]);
        (_heapG[a], _heapG[b]) = (_heapG[b], _heapG[a]);
        (_heapF[a], _heapF[b]) = (_heapF[b], _heapF[a]);
    }
}
