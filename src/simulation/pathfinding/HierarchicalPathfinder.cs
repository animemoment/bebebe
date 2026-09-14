using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Иерархический поиск пути (упрощённый HPA*): карта делится на регионы
/// (по умолчанию 16x16 тайлов), путь строится сначала ГРУБО через граф
/// регионов (A* по регионам), а затем ДЕТАЛИЗИРУЕТСЯ полноценным A* по
/// клеткам в узком «окне» из 1-3 соседних регионов — от входа условной
/// зоны к её выходу для входа в следующую зону.
///
/// Особенности:
///  - Граничные «порталы» — непрерывные проходимые отрезки общей границы
///    двух регионов; якорь портала — центральная клетка отрезка.
///  - A* на детальном уровне работает по локальному окну (клетки лишь
///    нескольких регионов), что даёт копеечную стоимость даже на 512x512.
///  - Кэш: грубые пути (регион->регион) и детальные сегменты
///    (клетка->клетка) кэшируются; инвалидируется при постройке стен.
///  - Потокобезопасен: построение оверлея под lock, scratch-буферы A*
///    ThreadLocal, кэш-словари под отдельным lock'ом.
///  - Не зависит от Godot API (чистый C#).
/// </summary>
public sealed class HierarchicalPathfinder
{
    public static HierarchicalPathfinder Instance { get; } = new();

    /// <summary>Размер региона в тайлах (степень двойки: 4 -> 16x16 тайлов).</summary>
    private const int RegionShift = 4;
    private const int RegionSize = 1 << RegionShift;

    // Стоимость прохождения клетки: 0 — блокирована (стена), 1 — трава,
    // 3 — вода (в движении вода лишь замедляет, поэтому проходима, но дорогая).
    private const byte CostBlocked = 0;
    private const byte CostGrass = 1;
    private const byte CostWater = 3;

    // Лимиты кэша (простая политика: при переполнении — полная очистка).
    private const int MaxRegionPathCache = 32768;
    private const int MaxSegmentCache = 65536;

    // Детальное окно: до 3x3 регионов = (3*16)^2 = 2304 клетки.
    private const int LocalCapacity = (3 * RegionSize) * (3 * RegionSize);

    private int _mapWidth;
    private int _mapHeight;
    private int _regionDimX;
    private int _regionDimY;

    private byte[] _cost;              // [mapWidth*mapHeight]
    private int[] _edgeStart;          // [regions*4] стартовый индекс в _portals
    private int[] _edgeCount;          // [regions*4] число порталов у (регион, направление)
    private int[] _portals;            // плоский массив якорей порталов (packed cell)
    private int _portalCount;
    private int _buildVersion;

    private SimulationContext _ctx;

    private readonly object _buildLock = new();
    // P-баланс: кэши — ConcurrentDictionary вместо Dictionary+_cacheLock.
    // _cacheLock сериализовал все 16 потоков Parallel-фаз на КАЖДОМ запросе пути:
    // один «тяжёлый» ComputeLocalSegment держал lock, остальные 15 ждали —
    // классический straggler при низкой средней загрузке CPU.
    private readonly ConcurrentDictionary<int, int[]> _regionPathCache = new(4, 1024);
    private readonly ConcurrentDictionary<ulong, int[]> _segmentCache = new(4, 4096);

    private sealed class SearchBuffers
    {
        // Детальный A* (локальное окно).
        public readonly int[] G = new int[LocalCapacity + 1];
        public readonly int[] Parent = new int[LocalCapacity + 1];
        public readonly int[] Open = new int[LocalCapacity + 1];
        public readonly bool[] Closed = new bool[LocalCapacity + 1];
        public readonly int[] Value = new int[LocalCapacity + 1];

        // Грубый A* (граф регионов).
        public readonly int[] RG = new int[4096];
        public readonly int[] RParent = new int[4096];
        public readonly int[] ROpen = new int[4096];
        public readonly bool[] RClosed = new bool[4096];
        public readonly int[] RValue = new int[4096];
    }

    private readonly ThreadLocal<SearchBuffers> _buffers = new(() => new SearchBuffers());

    private enum Dir : byte
    {
        North = 0,
        East = 1,
        South = 2,
        West = 3,
        None = 4
    }

    // ------------------------------------------------------------------
    // Публичный API
    // ------------------------------------------------------------------

    /// <summary>
    /// Устанавливает контекст симуляции (источник данных карты). Вызывается
    /// один раз при старте симуляции. Сброс оверлея — через Invalidate().
    /// </summary>
    public void Initialize(SimulationContext ctx)
    {
        lock (_buildLock)
        {
            _ctx = ctx;
            _cost = null;
            _buildVersion++;
        }
    }

    /// <summary>
    /// Сбрасывает кэши и помечает оверлей на перестройку (при постройке стен).
    /// </summary>
    public void Invalidate()
    {
        lock (_buildLock)
        {
            _buildVersion++;
            _cost = null;
            _regionPathCache.Clear();
            _segmentCache.Clear();
        }
    }

    /// <summary>
    /// Строит иерархический путь от тайла (sx, sy) к (tx, ty).
    /// При успехе возвращает true; если прямая видимость есть — true с
    /// count == 0 (двигаться напрямую). Точки (без стартовой клетки)
    /// записываются в <paramref name="outPath"/> как int-коды y*mapWidth+x,
    /// количество — в <paramref name="count"/>.
    /// </summary>
    public bool TryFindPath(int sx, int sy, int tx, int ty, out int[] outPath, out int count)
    {
        outPath = null;
        count = 0;

        if (sx == tx && sy == ty)
            return true;

        // Деградация при «пустом» оверлее (симуляция не инициализирована).
        if (_cost == null)
        {
            EnsureOverlay();
            if (_cost == null)
                return false;
        }

        // Прямая видимость — дешёвый путь без построения.
        if (HasLineOfSight(sx, sy, tx, ty))
            return true;

        int sr = RegionOfCell(sx, sy);
        int tr = RegionOfCell(tx, ty);

        int[] regionPath = GetRegionPathCached(sr, tr);
        if (regionPath == null || regionPath.Length < 2)
            return false;

        int regionCount = regionPath.Length;

        // Выбор порталов на каждом переходе (якорь в клетке региона обхода).
        int[] anchorX = new int[regionCount - 1];
        int[] anchorY = new int[regionCount - 1];

        for (int t = 0; t < regionCount - 1; t++)
        {
            if (!PickBestPortal(regionPath[t], regionPath[t + 1], sx, sy, tx, ty,
                out anchorX[t], out anchorY[t]))
            {
                return false; // порталы между соседями исчезли — пересчёт на след. кадре
            }
        }

        var path = new List<int>(64);

        for (int i = 0; i < regionCount; i++)
        {
            int fromX = i == 0 ? sx : anchorX[i - 1];
            int fromY = i == 0 ? sy : anchorY[i - 1];
            int toX = i == regionCount - 1 ? tx : anchorX[i];
            int toY = i == regionCount - 1 ? ty : anchorY[i];

            if (fromX == toX && fromY == toY)
                continue;

            int[] seg = GetSegmentCached(fromX, fromY, toX, toY);
            if (seg == null || seg.Length < 2)
                return false;

            // Копируем сегмент, исключая его первый элемент (либо это старт,
            // либо якорь, уже добавленный предыдущим сегментом).
            for (int k = 1; k < seg.Length; k++)
            {
                path.Add(seg[k]);
            }
        }

        if (path.Count == 0)
            return true;

        int[] result = SmoothPath(sx, sy, tx, ty, path);
        if (result == null || result.Length == 0)
            return false;

        outPath = result;
        count = result.Length;
        return true;
    }

    // ------------------------------------------------------------------
    // Построение оверлея (стоимости + порталы регионов)
    // ------------------------------------------------------------------

    private void EnsureOverlay()
    {
        lock (_buildLock)
        {
            if (_cost != null)
                return;

            if (_ctx == null)
            {
                // Симуляция ещё не стартовала — оверлей недоступен.
                return;
            }

            int w = _ctx.MapWidth;
            int h = _ctx.MapHeight;
            byte[] cost = new byte[w * h];

            for (int y = 0; y < h; y++)
            {
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte c;
                    if (_ctx.SolidWalls[x, y] || _ctx.Ground[x, y] == TileType.Mountain)
                        c = CostBlocked;
                    else if (_ctx.Ground[x, y] == TileType.Water)
                        c = CostWater;
                    else
                        c = CostGrass;
                    cost[rowBase + x] = c;
                }
            }

            _mapWidth = w;
            _mapHeight = h;
            _regionDimX = (w + RegionSize - 1) >> RegionShift;
            _regionDimY = (h + RegionSize - 1) >> RegionShift;
            _cost = cost;

            // ---- Портал: проходимый отрезок общей границы двух регионов.
            // Каждую общую границу регистрируем от «юго-восточной» стороны,
            // чтобы обратный переход пользовался тем же набором якорей.
            int regionCount = _regionDimX * _regionDimY;
            _edgeStart = new int[regionCount * 4];
            _edgeCount = new int[regionCount * 4];
            Array.Fill(_edgeStart, -1);
            Array.Clear(_edgeCount, 0, _edgeCount.Length);
            _portals = new int[regionCount * 4 * 16];
            _portalCount = 0;

            for (int ry = 0; ry < _regionDimY; ry++)
            {
                for (int rx = 0; rx < _regionDimX; rx++)
                {
                    int rid = ry * _regionDimX + rx;

                    // Восточная граница (сосед справа).
                    if (rx + 1 < _regionDimX)
                    {
                        _edgeStart[rid * 4 + (byte)Dir.East] = _portalCount;
                        _edgeCount[rid * 4 + (byte)Dir.East] =
                            BuildEdgePortals(rx, ry, Dir.East, cost, w, h);
                    }

                    // Южная граница.
                    if (ry + 1 < _regionDimY)
                    {
                        _edgeStart[rid * 4 + (byte)Dir.South] = _portalCount;
                        _edgeCount[rid * 4 + (byte)Dir.South] =
                            BuildEdgePortals(rx, ry, Dir.South, cost, w, h);
                    }

                    // Северная/западная — те же якоря, что у верхнего/левого
                    // соседа (их ребра South/East).
                    if (ry > 0)
                    {
                        int northNeighborBase = (ry - 1) * _regionDimX * 4 + rx * 4 + (byte)Dir.South;
                        _edgeStart[rid * 4 + (byte)Dir.North] = _edgeStart[northNeighborBase];
                        _edgeCount[rid * 4 + (byte)Dir.North] = _edgeCount[northNeighborBase];
                    }
                    if (rx > 0)
                    {
                        _edgeStart[rid * 4 + (byte)Dir.West] = _edgeStart[rid * 4 - 4 + (byte)Dir.East];
                        _edgeCount[rid * 4 + (byte)Dir.West] = _edgeCount[rid * 4 - 4 + (byte)Dir.East];
                    }
                }
            }
        }
    }

    /// <summary>
    /// Находит непрерывные проходимые отрезки общей границы региона (rx, ry)
    /// с соседом в направлении <paramref name="dir"/> (East или South) и
    /// записывает якоря (центральную клетку отрезка, packed) в _portals.
    /// Возвращает число порталов.
    /// </summary>
    private int BuildEdgePortals(int rx, int ry, Dir dir, byte[] cost, int w, int h)
    {
        int x0 = rx << RegionShift;
        int y0 = ry << RegionShift;
        int x1 = Math.Min(x0 + RegionSize - 1, w - 1);
        int y1 = Math.Min(y0 + RegionSize - 1, h - 1);

        int count = 0;

        if (dir == Dir.East)
        {
            int nx = Math.Min(x1 + 1, w - 1);
            int runStartY = -1;
            for (int y = y0; y <= y1; y++)
            {
                bool a = cost[y * w + x1] != CostBlocked;
                bool b = cost[y * w + nx] != CostBlocked;
                if (a && b)
                {
                    if (runStartY < 0) runStartY = y;
                }
                else if (runStartY >= 0)
                {
                    _portals[_portalCount++] = ((runStartY + y - 1) >> 1) * w + x1;
                    count++;
                    runStartY = -1;
                }
            }
            if (runStartY >= 0)
            {
                _portals[_portalCount++] = ((runStartY + y1) >> 1) * w + x1;
                count++;
            }
        }
        else // Dir.South
        {
            int ny = Math.Min(y1 + 1, h - 1);
            int runStartX = -1;
            for (int x = x0; x <= x1; x++)
            {
                bool a = cost[y1 * w + x] != CostBlocked;
                bool b = cost[ny * w + x] != CostBlocked;
                if (a && b)
                {
                    if (runStartX < 0) runStartX = x;
                }
                else if (runStartX >= 0)
                {
                    _portals[_portalCount++] = y1 * w + ((runStartX + x - 1) >> 1);
                    count++;
                    runStartX = -1;
                }
            }
            if (runStartX >= 0)
            {
                _portals[_portalCount++] = y1 * w + ((runStartX + x1) >> 1);
                count++;
            }
        }

        return count;
    }

    // ------------------------------------------------------------------
    // Хелперы координат и порталов
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RegionOfCell(int tx, int ty)
    {
        return (ty >> RegionShift) * _regionDimX + (tx >> RegionShift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RegionCoordX(int regionId) => regionId % _regionDimX;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RegionCoordY(int regionId) => regionId / _regionDimX;

    /// <summary>Направление от региона ra к соседнему региону rb (или None).</summary>
    private Dir DirectionBetween(int ra, int rb)
    {
        int ax = RegionCoordX(ra), ay = RegionCoordY(ra);
        int bx = RegionCoordX(rb), by = RegionCoordY(rb);
        if (by == ay)
        {
            if (bx == ax + 1) return Dir.East;
            if (bx == ax - 1) return Dir.West;
        }
        if (bx == ax)
        {
            if (by == ay + 1) return Dir.South;
            if (by == ay - 1) return Dir.North;
        }
        return Dir.None;
    }

    /// <summary>
    /// Выбирает лучший портал на общей границе регионов ra и rb:
    /// якорь, ближайший к прямой (sx,sy)-(tx,ty). Якорь возвращается в
    /// координатах клетки региона ra.
    /// </summary>
    private bool PickBestPortal(int ra, int rb, int sx, int sy, int tx, int ty,
        out int anchorX, out int anchorY)
    {
        anchorX = -1;
        anchorY = -1;

        Dir dir = DirectionBetween(ra, rb);
        if (dir == Dir.None)
            return false;

        int idx = ra * 4 + (byte)dir;
        int start = _edgeStart[idx];
        int cnt = _edgeCount[idx];
        if (start < 0 || cnt <= 0)
            return false;

        int best = -1;
        int bestScore = int.MaxValue;
        int bestPacked = 0;

        for (int i = 0; i < cnt; i++)
        {
            int portalPacked = _portals[start + i];
            int px = portalPacked % _mapWidth;
            int py = portalPacked / _mapWidth;

            // Расстояние от якоря до прямой S-T (упрощённо: до отрезка через
            // проекцию). Достаточно точности для выбора портала.
            int score = DistSqToSegment(px, py, sx, sy, tx, ty);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
                bestPacked = portalPacked;
            }
        }

        if (best < 0)
            return false;

        anchorX = bestPacked % _mapWidth;
        anchorY = bestPacked / _mapWidth;
        return true;
    }

    private static int DistSqToSegment(int px, int py, int ax, int ay, int bx, int by)
    {
        int vx = bx - ax, vy = by - ay;
        int wx = px - ax, wy = py - ay;
        int lenSq = vx * vx + vy * vy;
        if (lenSq == 0)
        {
            int offX = px - ax, offY = py - ay;
            return offX * offX + offY * offY;
        }
        float t = (float)(wx * vx + wy * vy) / (float)lenSq;
        t = Math.Max(0f, Math.Min(1f, t));
        float cx = ax + t * vx;
        float cy = ay + t * vy;
        float projDx = px - cx, projDy = py - cy;
        return (int)(projDx * projDx + projDy * projDy);
    }

    // ------------------------------------------------------------------
    // Грубый уровень: A* по графу регионов
    // ------------------------------------------------------------------

    private int[] GetRegionPathCached(int sr, int tr)
    {
        int key = sr * 4096 + tr;
        if (_regionPathCache.TryGetValue(key, out int[] cached))
            return cached;

        int[] path = ComputeRegionPath(sr, tr);
        if (path == null)
            return null;

        if (_regionPathCache.Count >= MaxRegionPathCache)
            _regionPathCache.Clear();
        _regionPathCache[key] = path;
        return path;
    }

    /// <summary>
    /// A* по графу регионов: узлы — регионы, ребро существует, если между
    /// регионами есть хотя бы один портал. Гедонистика — клеточное расстояние
    /// между центрами регионов.
    /// </summary>
    private int[] ComputeRegionPath(int sr, int tr)
    {
        var b = _buffers.Value;
        int[] g = b.RG;
        int[] parent = b.RParent;
        int[] open = b.ROpen;
        bool[] closed = b.RClosed;

        int cap = _regionDimX * _regionDimY;
        if (cap > g.Length)
            return null;

        Array.Fill(g, int.MaxValue);
        Array.Fill(parent, -1);
        Array.Fill(closed, false);

        int openCount = 0;
        open[0] = sr;
        openCount = 1;
        g[sr] = 0;

        int trx = RegionCoordX(tr);
        int tryRegionY = RegionCoordY(tr);

        const int MaxExpand = 4096;
        int expanded = 0;

        while (openCount > 0 && expanded < MaxExpand)
        {
            int cur = RegionPop(open, ref openCount);
            if (cur == tr)
                break;
            if (closed[cur])
                continue;
            closed[cur] = true;
            expanded++;

            int cx = RegionCoordX(cur);
            int cy = RegionCoordY(cur);

            // 4 соседа по региональной сетке.
            TryRelaxRegion(cur, cy * _regionDimX + (cx + 1), cx + 1 < _regionDimX,
                trx, tryRegionY, b, open, ref openCount);
            TryRelaxRegion(cur, cy * _regionDimX + (cx - 1), cx > 0,
                trx, tryRegionY, b, open, ref openCount);
            TryRelaxRegion(cur, (cy + 1) * _regionDimX + cx, cy + 1 < _regionDimY,
                trx, tryRegionY, b, open, ref openCount);
            TryRelaxRegion(cur, (cy - 1) * _regionDimX + cx, cy > 0,
                trx, tryRegionY, b, open, ref openCount);
        }

        if (parent[tr] < 0 && sr != tr)
            return null;

        // Восстановление.
        var rev = new List<int>(16);
        int curNode = tr;
        while (curNode >= 0)
        {
            rev.Add(curNode);
            curNode = parent[curNode];
        }
        int n = rev.Count;
        var result = new int[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = rev[n - 1 - i];
        }
        return result;
    }

    private void TryRelaxRegion(int from, int to, bool valid,
        int trx, int tryRegionY, SearchBuffers b, int[] open, ref int openCount)
    {
        if (!valid)
            return;

        // Ребро существует только если между регионами есть портал.
        Dir dir = DirectionBetween(from, to);
        if (dir == Dir.None)
            return;

        int idx = from * 4 + (byte)dir;
        if (_edgeCount[idx] <= 0)
            return;

        if (b.RClosed[to])
            return;

        int tentative = b.RG[from] + 1;
        if (tentative >= b.RG[to])
            return;

        b.RG[to] = tentative;
        b.RParent[to] = from;

        // Индекс по f = g + h в куче.
        long f = (long)tentative + RegionHeuristic(to, trx, tryRegionY);
        RegionPush(open, ref openCount, to, (int)Math.Min(f, int.MaxValue));
    }

    /// <summary>Клеточная (чебышёва) оценка между регионами, в узлах.</summary>
    private int RegionHeuristic(int regionId, int trx, int tryRegionY)
    {
        int dx = Math.Abs(RegionCoordX(regionId) - trx);
        int dy = Math.Abs(RegionCoordY(regionId) - tryRegionY);
        return Math.Max(dx, dy);
    }

    // ------------------------------------------------------------------
    // Детальный уровень: A* по клеткам локального окна
    // ------------------------------------------------------------------

    private int[] GetSegmentCached(int fx, int fy, int tx, int ty)
    {
        int packedFrom = fy * _mapWidth + fx;
        int packedTo = ty * _mapWidth + tx;
        ulong key = ((ulong)packedFrom << 32) | (uint)packedTo;

        if (_segmentCache.TryGetValue(key, out int[] cached))
            return cached;

        int[] seg = ComputeLocalSegment(fx, fy, tx, ty);
        if (seg == null)
            return null;

        if (_segmentCache.Count >= MaxSegmentCache)
            _segmentCache.Clear();
        _segmentCache[key] = seg;
        return seg;
    }

    // ------------------------------------------------------------------
    // Бинарные кучи (lazy-deletion A*)
    // ------------------------------------------------------------------

    private static void PushMin(int[] open, SearchBuffers b, int[] value, ref int openCount, int node, int key)
    {
        int i = openCount;
        open[openCount++] = node;
        value[node] = key;
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (value[open[p]] <= key)
                break;
            open[i] = open[p];
            i = p;
        }
        open[i] = node;
    }

    private static int PopMin(int[] open, SearchBuffers b, int[] value, ref int openCount)
    {
        int root = open[0];
        openCount--;
        if (openCount > 0)
        {
            int moved = open[openCount];
            int key = value[moved];
            int i = 0;
            while (true)
            {
                int left = i * 2 + 1;
                if (left >= openCount)
                    break;
                int right = left + 1;
                int child = left;
                if (right < openCount && value[open[right]] < value[open[left]])
                    child = right;
                if (value[open[child]] >= key)
                    break;
                open[i] = open[child];
                i = child;
            }
            open[i] = moved;
        }
        return root;
    }

    private void RegionPush(int[] open, ref int openCount, int node, int key)
    {
        var b = _buffers.Value;
        PushMin(open, b, b.RValue, ref openCount, node, key);
    }

    private int RegionPop(int[] open, ref int openCount)
    {
        var b = _buffers.Value;
        return PopMin(open, b, b.RValue, ref openCount);
    }

    private void LocalPush(int[] open, ref int openCount, int node, int key)
    {
        var b = _buffers.Value;
        PushMin(open, b, b.Value, ref openCount, node, key);
    }

    private int LocalPop(int[] open, ref int openCount)
    {
        var b = _buffers.Value;
        return PopMin(open, b, b.Value, ref openCount);
    }

    /// <summary>
    /// Полноценный A* по клеткам в окне, охватывающем регионы обеих точек
    /// плюс одно кольцо запаса (до 3x3 регионов = 2304 клетки). Это «от входа
    /// зоны к её выходу»: точка входа и точка выхода лежат на границах,
    /// окно включает оба региона, поэтому путь может свободно пересекать
    /// границу в любом легальном месте.
    /// </summary>
    private int[] ComputeLocalSegment(int fx, int fy, int tx, int ty)
    {
        if (tx < 0 || ty < 0 || tx >= _mapWidth || ty >= _mapHeight)
            return null;
        if (IsCellBlocked(tx, ty))
            return null;

        int rx0 = fx >> RegionShift, ry0 = fy >> RegionShift;
        int rx1 = tx >> RegionShift, ry1 = ty >> RegionShift;

        int minRX = Math.Max(0, Math.Min(rx0, rx1) - 1);
        int maxRX = Math.Min(_regionDimX - 1, Math.Max(rx0, rx1) + 1);
        int minRY = Math.Max(0, Math.Min(ry0, ry1) - 1);
        int maxRY = Math.Min(_regionDimY - 1, Math.Max(ry0, ry1) + 1);

        int minX = minRX << RegionShift;
        int minY = minRY << RegionShift;
        int maxX = Math.Min(((maxRX + 1) << RegionShift) - 1, _mapWidth - 1);
        int maxY = Math.Min(((maxRY + 1) << RegionShift) - 1, _mapHeight - 1);

        int ww = maxX - minX + 1;
        int hh = maxY - minY + 1;
        int cap = ww * hh;
        if (cap > LocalCapacity)
            return null;

        var b = _buffers.Value;
        int[] g = b.G;
        int[] parent = b.Parent;
        int[] open = b.Open;
        bool[] closed = b.Closed;

        Array.Fill(g, int.MaxValue);
        Array.Fill(parent, -1);
        Array.Fill(closed, false);

        int sidx = (fy - minY) * ww + (fx - minX);
        int tidx = (ty - minY) * ww + (tx - minX);
        if (sidx < 0 || tidx < 0 || sidx >= cap || tidx >= cap)
            return null;

        g[sidx] = 0;
        int openCount = 0;
        LocalPush(open, ref openCount, sidx, OctileDist(fx - tx, fy - ty));

        const int MaxExpand = 4096;
        int expanded = 0;

        while (openCount > 0 && expanded < MaxExpand)
        {
            int u = LocalPop(open, ref openCount);
            if (closed[u])
                continue;
            closed[u] = true;
            expanded++;

            if (u == tidx)
                break;

            int ux = u % ww + minX;
            int uy = u / ww + minY;
            int uCost = g[u];

            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = uy + dy;
                if (ny < minY || ny > maxY)
                    continue;
                int nyw = ny * _mapWidth;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    int nx = ux + dx;
                    if (nx < minX || nx > maxX)
                        continue;

                    int cellCost = _cost[nyw + nx];
                    if (cellCost == CostBlocked)
                        continue;

                    int step = (dx != 0 && dy != 0) ? 14 : 10;
                    if (cellCost == CostWater)
                        step *= 3;

                    int v = (ny - minY) * ww + (nx - minX);
                    if (closed[v])
                        continue;

                    int tentative = uCost + step;
                    if (tentative < g[v])
                    {
                        g[v] = tentative;
                        parent[v] = u;
                        int f = tentative + OctileDist(nx - tx, ny - ty);
                        LocalPush(open, ref openCount, v, f);
                    }
                }
            }
        }

        if (g[tidx] == int.MaxValue)
            return null;

        // Восстановление пути (включая старт и цель).
        var rev = new List<int>(16);
        int cur = tidx;
        while (cur != -1)
        {
            rev.Add(cur);
            if (cur == sidx)
                break;
            cur = parent[cur];
        }
        int n = rev.Count;
        var result = new int[n];
        for (int i = 0; i < n; i++)
        {
            int loc = rev[n - 1 - i];
            int lx = loc % ww + minX;
            int ly = loc / ww + minY;
            result[i] = ly * _mapWidth + lx;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OctileDist(int dx, int dy)
    {
        dx = Math.Abs(dx);
        dy = Math.Abs(dy);
        return 10 * Math.Max(dx, dy) - 4 * Math.Min(dx, dy);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCellBlocked(int tx, int ty)
    {
        if ((uint)tx >= (uint)_mapWidth || (uint)ty >= (uint)_mapHeight)
            return true;
        return _cost[ty * _mapWidth + tx] == CostBlocked;
    }

    // ------------------------------------------------------------------
    // Прямая видимость и сглаживание
    // ------------------------------------------------------------------

    /// <summary>
    /// Клеточная прямая видимость по Брезенхэму: сегмент свободен, если ни
    /// одна клетка по пути не блокирована стеной (вода допустима — движение
    /// напрямую сквозь воду разрешено, лишь медленнее).
    /// </summary>
    private bool HasLineOfSight(int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int steps = Math.Max(dx, dy);
        if (steps == 0)
            return true;

        for (int i = 1; i <= steps; i++)
        {
            int x = (int)Math.Round(x0 + ((float)(x1 - x0) * i) / steps);
            int y = (int)Math.Round(y0 + ((float)(y1 - y0) * i) / steps);
            if (IsCellBlocked(x, y))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Прореживает детальный путь: оставляет только точки поворота, где
    /// прямая видимость между последовательными опорными точками сохраняется.
    /// </summary>
    private int[] SmoothPath(int sx, int sy, int tx, int ty, List<int> path)
    {
        var result = new List<int>(Math.Min(path.Count, 32));
        int curX = sx;
        int curY = sy;
        int lastX = -1;
        int lastY = -1;

        for (int i = 0; i < path.Count; i++)
        {
            int packed = path[i];
            int px = packed % _mapWidth;
            int py = packed / _mapWidth;

            if (HasLineOfSight(curX, curY, px, py))
            {
                lastX = px;
                lastY = py;
                continue;
            }

            if (lastX >= 0)
            {
                result.Add(lastY * _mapWidth + lastX);
                curX = lastX;
                curY = lastY;
            }

            // Текущая точка обязана быть видимой из нового опорного положения
            // (она — первая невидимая из предыдущего).
            lastX = px;
            lastY = py;
        }

        // Финал: марш мимо последней опорной к цели.
        if (HasLineOfSight(curX, curY, tx, ty))
        {
            result.Add(ty * _mapWidth + tx);
        }
        else if (lastX >= 0 && !(lastX == tx && lastY == ty))
        {
            result.Add(lastY * _mapWidth + lastX);
        }
        else if (result.Count == 0)
        {
            result.Add(path[path.Count - 1]);
        }

        if (result.Count == 0)
            return null;
        return result.ToArray();
    }
}