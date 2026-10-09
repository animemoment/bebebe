using System;
using System.Collections.Generic;

namespace Game.Core.WorldStreaming.Layers;

/// <summary>
/// L3 Hydrology: priority-flood (заполнение впадин, Barnes-Pagliano-стиль через
/// binary heap) → D8 направление стока → flow accumulation → реки/озёра.
///
/// Выполняется на регион-окне (WorldRegions.RegionSize) С МАРГИНОМ
/// (HydroMarginCells ≥ riverReach). Маргин фиксирован константой, границы регионов
/// фиксированы сеткой → результат для клетки зависит только от (seed, ver, abs x, y),
/// НЕ от порядка загрузки чанков ⇒ стыки соседних регионов совпадают байт-в-байт.
/// Кэш по RegionKey — потокобезопасен (ConcurrentDictionary), повторные запросы бесплатны.
/// </summary>
public sealed class RegionHydrology
{
    /// <summary>Сторона сетки гидрологии в клетках (регион + 2*маргин).</summary>
    public readonly int GridSide;
    /// <summary>Левый верхний угол окна в мировых координатах.</summary>
    public readonly long OriginX;
    public readonly long OriginY;

    private readonly ushort[] _rawElev;     // Q16 исходные высоты L0 (для проверки моря)
    private readonly ushort[] _filledElev; // Q16 заполненные высоты (priority-flood выход)
    private readonly int[] _downstream;    // индекс ячейки стока или -1 (океан/край)
    private readonly ushort[] _flowQ16;    // нормированный log2 accumulation
    private readonly bool[] _isLake;       // озёрные ячейки (заполненные впадины суши)

    private RegionHydrology(int side, long originX, long originY,
        ushort[] raw, ushort[] filled, int[] downstream, ushort[] flow, bool[] lake)
    {
        GridSide = side;
        OriginX = originX;
        OriginY = originY;
        _rawElev = raw;
        _filledElev = filled;
        _downstream = downstream;
        _flowQ16 = flow;
        _isLake = lake;
    }

    public ushort FlowAt(int i) => _flowQ16[i];
    public bool IsLakeAt(int i) => _isLake[i];
    /// <summary>Исходная (до priority-flood) высота ячейки — для проверки «море».</summary>
    public ushort RawElevAt(int i) => _rawElev[i];

    public int IndexOf(long worldX, long worldY)
    {
        int lx = (int)(worldX - OriginX);
        int ly = (int)(worldY - OriginY);
        if ((uint)lx >= (uint)GridSide || (uint)ly >= (uint)GridSide)
            return -1;
        return ly * GridSide + lx;
    }

    /// <summary>D8 смещения (индексный порядок зафиксирован — детерминизм).</summary>
    private static readonly int[] Dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
    private static readonly int[] Dy = { 0, 1, 1, 1, 0, -1, -1, -1 };

    /// <summary>Заполненные высоты (после priority-flood) — используются D8-стоком.</summary>
    public ushort FilledAt(int i) => _filledElev[i];

    /// <summary>
    /// Приоритетная очередь: key = Q16 высота; при равенстве ключей порядок вставки
    /// (FIFO) — обход priority-flood становится детерминированным и эквивалентным
    /// классическому BFS «flood fill» для плоских областей.
    /// </summary>
    private sealed class MinHeap
    {
        private readonly int[] _idx;
        private readonly ushort[] _key;
        private readonly long[] _seq;
        private readonly List<(int, ushort)> _seeds = new();
        private int _count;
        private long _nextSeq;

        public int Count => _count;
        public IReadOnlyList<(int Idx, ushort Key)> BoundarySeeds => _seeds;

        public MinHeap(int capacity)
        {
            _idx = new int[capacity];
            _key = new ushort[capacity];
            _seq = new long[capacity];
        }

        private bool Less(int a, int b)
            => _key[a] != _key[b] ? _key[a] < _key[b] : _seq[a] < _seq[b];

        public void Push(int idx, ushort key)
        {
            int i = _count++;
            _idx[i] = idx;
            _key[i] = key;
            _seq[i] = _nextSeq++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (!Less(i, parent)) break;
                Swap(parent, i);
                i = parent;
            }
        }

        public void PushBoundary(int idx, ushort key)
        {
            _seeds.Add((idx, key));
            Push(idx, key);
        }

        public (int Idx, ushort Key) Pop()
        {
            var top = (_idx[0], _key[0]);
            _count--;
            _idx[0] = _idx[_count];
            _key[0] = _key[_count];
            _seq[0] = _seq[_count];
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, smallest = i;
                if (l < _count && Less(l, smallest)) smallest = l;
                if (r < _count && Less(r, smallest)) smallest = r;
                if (smallest == i) break;
                Swap(i, smallest);
                i = smallest;
            }
            return top;
        }

        private void Swap(int a, int b)
        {
            (_idx[a], _idx[b]) = (_idx[b], _idx[a]);
            (_key[a], _key[b]) = (_key[b], _key[a]);
            (_seq[a], _seq[b]) = (_seq[b], _seq[a]);
        }
    }

    /// <summary>
    /// Построить гидрологию региона. elevationAt — чистая функция L0 (абсолютные координаты).
    /// </summary>
    public static RegionHydrology Build(ulong seed, uint ver, RegionKey region,
        Func<long, long, ushort> elevationAt)
    {
        int margin = WorldLayerParams.HydroMarginCells;
        int size = WorldRegions.RegionSize;
        int side = size + 2 * margin;
        long originX = checked(region.X * size - margin);
        long originY = checked(region.Y * size - margin);

        int n = side * side;
        var elev = new ushort[n];
        for (int y = 0; y < side; y++)
        {
            long wy = originY + y;
            int row = y * side;
            for (int x = 0; x < side; x++)
                elev[row + x] = elevationAt(originX + x, wy);
        }

        // ---- Priority flood: минимум по граничному кольцу внутрь (Barnes et al.) ----
        var filled = new ushort[n];
        var visited = new bool[n];
        var heap = new MinHeap(n);

        for (int x = 0; x < side; x++)
        {
            EnqueueBoundary(heap, visited, elev, x);
            EnqueueBoundary(heap, visited, elev, (side - 1) * side + x);
        }
        for (int y = 1; y < side - 1; y++)
        {
            EnqueueBoundary(heap, visited, elev, y * side);
            EnqueueBoundary(heap, visited, elev, y * side + side - 1);
        }
        foreach (var (idx, e) in heap.BoundarySeeds)
            filled[idx] = e;

        while (heap.Count > 0)
        {
            (int idx, ushort e) = heap.Pop();
            int cx = idx % side;
            int cy = idx / side;

            for (int d = 0; d < 8; d++)
            {
                int nx = cx + Dx[d];
                int ny = cy + Dy[d];
                if ((uint)nx >= (uint)side || (uint)ny >= (uint)side)
                    continue;
                int ni = ny * side + nx;
                if (visited[ni])
                    continue;
                visited[ni] = true;
                // Заполняем впадину до уровня стока + минимальный уклон (1 единица Q16).
                ushort f = (ushort)Math.Max(elev[ni], Math.Min(ushort.MaxValue, (int)e + 1));
                filled[ni] = f;
                heap.Push(ni, f);
            }
        }

        // ---- D8: сток к самому низкому соседу (по filled) ----
        var downstream = new int[n];
        Array.Fill(downstream, -1);
        for (int y = 0; y < side; y++)
        {
            int row = y * side;
            for (int x = 0; x < side; x++)
            {
                int i = row + x;
                ushort fe = filled[i];
                int best = -1;
                ushort bestE = fe;
                for (int d = 0; d < 8; d++)
                {
                    int nx = x + Dx[d];
                    int ny = y + Dy[d];
                    if ((uint)nx >= (uint)side || (uint)ny >= (uint)side)
                        continue; // край = океан/граница мира, сток уходит за окно
                    int ni = ny * side + nx;
                    if (filled[ni] < bestE)
                    {
                        bestE = filled[ni];
                        best = ni;
                    }
                }
                downstream[i] = best;
            }
        }

        // ---- Accumulation: обход в порядке возрастания filled (heap-сортировка индексов) ----
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        // Детерминированная сортировка: stable sort по (filled, index).
        Array.Sort(order, (a, b) =>
        {
            int c = filled[a].CompareTo(filled[b]);
            return c != 0 ? c : a.CompareTo(b);
        });

        var accum = new uint[n];
        for (int i = 0; i < n; i++) accum[i] = 1; // каждая ячейка = 1 клетка стока
        // Суммируем от высоких к низким: вклад ячейки переходит ниже по течению.
        for (int k = n - 1; k >= 0; k--)
        {
            int i = order[k];
            int ds = downstream[i];
            if (ds >= 0 && ds != i)
                accum[ds] += accum[i];
        }

        // Нормировка log2(accum) → Q16: log2(2^15)=15 → 65535.
        var flowQ16 = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            double lg = Math.Log2(Math.Max(1u, accum[i]));
            flowQ16[i] = (ushort)Math.Clamp(lg / WorldLayerParams.FlowLog2Divisor * 65535.0, 0, 65535);
        }

        // ---- Озёра: детерминированный seed-рост «озёрных семян» на суше ----
        // Каждое семя — локальная минимальная чаша (нет соседа ниже). Вокруг семени
        // flood-fill по клеткам с filled ≤ filled[семя] (уровень воды = чаша), в пределах окна.
        // Результат зависит только от (seed, ver, окно) ⇒ одинаков при любом порядке запросов.
        var isLake = new bool[n];
        var lakeQueue = new Queue<int>();
        for (int y = 1; y < side - 1; y++)
        {
            int row = y * side;
            for (int x = 1; x < side - 1; x++)
            {
                int i = row + x;
                if (ReliefLayer.IsOcean(elev[i]))
                    continue; // море — не озеро

                bool localMin = true;
                for (int d = 0; d < 8; d++)
                {
                    int ni = (y + Dy[d]) * side + (x + Dx[d]);
                    if (filled[ni] < filled[i]) { localMin = false; break; }
                }
                if (!localMin)
                    continue;

                long wx = originX + x;
                long wy = originY + y;
                ulong h = CoordinateHash.Hash(seed, ver, GenerationDomain.LakePresence, wx, wy);
                if (h % (ulong)WorldLayerParams.LakeFillDivisor != 0)
                    continue;

                // Рост озера от семени: уровень воды = filled семени.
                ushort waterLevel = filled[i];
                if (!isLake[i])
                {
                    isLake[i] = true;
                    lakeQueue.Enqueue(i);
                }
                while (lakeQueue.Count > 0)
                {
                    int ci = lakeQueue.Dequeue();
                    int cx2 = ci % side;
                    int cy2 = ci / side;
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = cx2 + Dx[d];
                        int ny = cy2 + Dy[d];
                        if ((uint)nx >= (uint)side || (uint)ny >= (uint)side)
                            continue;
                        int ni = ny * side + nx;
                        if (isLake[ni] || ReliefLayer.IsOcean(elev[ni]))
                            continue;
                        if (filled[ni] <= waterLevel)
                        {
                            isLake[ni] = true;
                            lakeQueue.Enqueue(ni);
                        }
                    }
                }
            }
        }

        return new RegionHydrology(side, originX, originY, elev, filled, downstream, flowQ16, isLake);
    }

    private static void EnqueueBoundary(MinHeap heap, bool[] visited, ushort[] elev, int idx)
    {
        if (visited[idx]) return;
        visited[idx] = true;
        heap.PushBoundary(idx, elev[idx]);
    }
}

/// <summary>
/// L3 фасад: кэш гидрологии по RegionKey + доступ к cell-значениям.
/// Река = flow > порога ИЛИ ячейка внутри озера (тогда это озеро).
/// </summary>
public static class HydrologyLayer
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, uint, long, long), RegionHydrology> Cache = new();

    public static RegionHydrology GetRegion(ulong seed, uint ver, RegionKey region,
        Func<long, long, ushort> elevationAt)
    {
        var key = (seed, ver, region.X, region.Y);
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        var built = RegionHydrology.Build(seed, ver, region, elevationAt);
        Cache[key] = built;
        return built;
    }

    public static void ClearCache() => Cache.Clear();

    public static int MaxCachedRegions => 1024;

    /// <summary>Значения L3 в абсолютной клетке: flow Q16, река, озеро.</summary>
    public static (ushort FlowQ16, bool IsRiver, bool IsLake) Sample(ulong seed, uint ver,
        long x, long y, Func<long, long, ushort> elevationAt)
    {
        RegionKey region = WorldRegions.RegionForCell(x, y);
        var hydro = GetRegion(seed, ver, region, elevationAt);
        int i = hydro.IndexOf(x, y);
        if (i < 0)
            return (0, false, false); // не должно случаться: окно перекрывает весь регион

        bool isLake = hydro.IsLakeAt(i);
        ushort flow = hydro.FlowAt(i);
        bool isRiver = !isLake && flow > WorldLayerParams.RiverThresholdQ16;
        return (flow, isRiver, isLake);
    }

    /// <summary>
    /// Быстрая проверка «море ли клетка» по уже готовой сетке гидрологии
    /// (elev[i] &lt; локальный уровень моря с архипелажной маской). Используется
    /// фасадом стека для согласованности океан/река/озеро на границах.
    /// </summary>
    public static bool IsOceanCell(ulong seed, uint ver, RegionHydrology hydro, int i)
        => hydro.RawElevAt(i) < ReliefLayer.LocalSeaLevel(seed, ver,
            hydro.OriginX + i % hydro.GridSide, hydro.OriginY + i / hydro.GridSide);
}
