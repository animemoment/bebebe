using System;

using Game.Core.WorldStreaming;

namespace Game.Core.WorldLayers;

/// <summary>
/// L3: гидрология. Требует глобального знания стока → считается на регионе 512×512
/// с margin (WorldLayerParams.HydroMargin) и кэшируется по RegionKey. Детерминизм:
/// регион + margin фиксированы ⇒ сток через границу региона входит в расчёт одинаково
/// при любом порядке/окне запроса — швов рек/озёр нет (P1-фикс).
/// Алгоритм: priority-flood (заполнение впадин) → D8-сток → flow accumulation →
/// accum &gt; RiverThreshold = река; залитые впадины достаточно большого размера = озёра.
/// </summary>
public static class HydrologyLayer
{
    /// <summary>Флаг клетки: биты — water(1), lake(2), river(4).</summary>
    public const byte FWater = 1, FLake = 2, FRiver = 4;

    /// <summary>Данные гидрологии региона: флаги и накопление стока на сетке W×W (с margin).</summary>
    public sealed record RegionData(uint Seed, byte[] Flags, int[] Flow);

    /// <summary>
    /// Кэш гидрологии: LRU по (seed, rx, ry). Бюджет — 1 регион (640×640 ≈ 5 МБ):
    /// игрок находится в одном регионе, стриминг окна идёт рядом с ним. При промахе
    /// регион пересчитывается детерминированно (чистая функция) — корректность не
    /// зависит от кэша, только скорость. Потокобезопасно (lock).
    /// </summary>
    private const long RegionBytes = (long)(S + 2 * M) * (S + 2 * M) * (sizeof(byte) + sizeof(int));
    public const long MaxCachedBytes = RegionBytes; // ~5 МБ, ровно один регион
    public static readonly int MaxCachedRegions = 1;

    private static readonly object CacheLock = new();
    private static readonly System.Collections.Generic.Dictionary<(uint Seed, long Rx, long Ry), RegionData> Cache = new();
    private static readonly System.Collections.Generic.LinkedList<(uint Seed, long Rx, long Ry)> Lru = new();

    private const int S = WorldRegions.RegionSize;   // 512
    private const int M = WorldLayerParams.HydroMargin; // 64
    private const int W = S + 2 * M;                 // 640

    /// <summary>Флаги клетки (биты water|lake|river). Детерминировано от (seed, wx, wy).</summary>
    public static byte FlagsAt(uint seed, long wx, long wy)
    {
        long rx = ClimateLayer.FloorDiv(wx, S);
        long ry = ClimateLayer.FloorDiv(wy, S);
        var rd = GetRegion(seed, rx, ry);
        int lx = (int)(wx - rx * S);
        int ly = (int)(wy - ry * S);
        return rd.Flags[(ly + M) * W + (lx + M)];
    }

    public static bool IsRiver(uint seed, long wx, long wy) => (FlagsAt(seed, wx, wy) & FRiver) != 0;
    public static bool IsLake(uint seed, long wx, long wy) => (FlagsAt(seed, wx, wy) & FLake) != 0;
    public static bool IsWater(uint seed, long wx, long wy) => (FlagsAt(seed, wx, wy) & FWater) != 0;

    /// <summary>Flow accumulation клетки (для пойм/болот).</summary>
    public static int FlowAt(uint seed, long wx, long wy)
    {
        long rx = ClimateLayer.FloorDiv(wx, S);
        long ry = ClimateLayer.FloorDiv(wy, S);
        var rd = GetRegion(seed, rx, ry);
        int lx = (int)(wx - rx * S);
        int ly = (int)(wy - ry * S);
        return rd.Flow[(ly + M) * W + (lx + M)];
    }

    /// <summary>Кэш-гет региона (вычисление ленивое, потокобезопасное, LRU).</summary>
    public static RegionData GetRegion(uint seed, long rx, long ry)
    {
        var key = (seed, rx, ry);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                // touch LRU
                Lru.Remove(key);
                Lru.AddFirst(key);
                return cached;
            }
        }

        // Вычисление вне блокировки (дублирование при гонке безопасно — функция чистая).
        var data = ComputeRegion(seed, rx, ry);

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var existing)) return existing;
            Cache[key] = data;
            Lru.AddFirst(key);
            while (Lru.Count > MaxCachedRegions)
            {
                var oldest = Lru.Last;
                if (oldest == null) break;
                Lru.RemoveLast();
                Cache.Remove(oldest.Value);
            }
        }
        return data;
    }

    /// <summary>Полный расчёт гидрологии региона (публично для тестов стыков).</summary>
    public static RegionData ComputeRegion(uint seed, long rx, long ry)
    {
        long ox = rx * S - M;
        long oy = ry * S - M;

        // 1) Сетка высот (Q: int = elevation*1000), вода ниже SeaLevel помечена заранее.
        var h = new int[W * W];
        var preWater = new bool[W * W];
        for (int y = 0; y < W; y++)
        {
            long wy = oy + y;
            for (int x = 0; x < W; x++)
            {
                float e = ReliefLayer.Elevation(seed, ox + x, wy);
                int idx = y * W + x;
                h[idx] = (int)(e * 1000f);
                preWater[idx] = e < WorldLayerParams.SeaLevel;
            }
        }

        // 2) Priority flood: поднимаем все замкнутые впадины до уровня седла.
        //    Океанские клетки — «сточные» границы (не поднимаются).
        var filled = FillDepressions(h, preWater);

        // 3) D8 направление стока + накопление (Barnes по убыванию высоты).
        var flow = Accumulate(filled, preWater);

        // 4) Флаги: река по accum; озёра — залитые впадины (filled > исходной высоты)
        //    размером ≥ LakeMinCells, плюс всё ниже SeaLevel = вода.
        var flags = new byte[W * W];
        for (int i = 0; i < W * W; i++)
        {
            if (preWater[i]) { flags[i] = FWater; continue; }
            int rise = filled[i] - h[i];
            if (rise >= 2 && flow[i] >= WorldLayerParams.SpringMinFlow)
                flags[i] = FWater | FLake;
            else if (flow[i] > WorldLayerParams.RiverThreshold)
                flags[i] = FWater | FRiver;
        }

        // 5) Связь русел через границу: реки, вытекающие из окна, продолжатся в соседнем
        //    регионе так же детерминированно (тот же рельеф в margin-зоне).
        return new RegionData(S, flags, flow);
    }

    private static int[] FillDepressions(int[] h, bool[] preWater)
    {
        int n = h.Length;
        var filled = (int[])h.Clone();
        // Простой многопроходный BFS-подъём от стоков (океан/граница): корректный
        // priority-flood на целочисленных высотах.
        var inQueue = new bool[n];
        var queue = new System.Collections.Generic.Queue<int>(n);

        // Стоки: предводные клетки и весь канвас-бордер.
        for (int y = 0; y < W; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                bool border = x == 0 || y == 0 || x == W - 1 || y == W - 1;
                if (preWater[i] || border) { queue.Enqueue(i); inQueue[i] = true; }
            }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % W, y = i / W;
            for (int d = 0; d < 8; d++)
            {
                int nx = x + DX[d], ny = y + DY[d];
                if ((uint)nx >= W || (uint)ny >= W) continue;
                int j = ny * W + nx;
                if (inQueue[j]) continue;
                // Поднимаем соседа до уровня стока (минимум — его собственная высота).
                if (filled[j] < filled[i]) filled[j] = filled[i];
                queue.Enqueue(j); inQueue[j] = true;
            }
        }
        return filled;
    }

    private static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };

    private static int[] Accumulate(int[] filled, bool[] preWater)
    {
        int n = filled.Length;
        var flow = new int[n];
        for (int i = 0; i < n; i++) flow[i] = 1;

        // Сортировка клеток по убыванию заполненной высоты.
        // ВАЖНО: clamp только сверху — нижний кламп к 0 схлопывал все подводные/низкие
        // клетки в одну bucket и ломал порядок стока (впадины становились «реками»).
        const int MaxH = 2_000_000; // heights stored as elevation*1000, e <= ~1.7 => <= 1_700_000
        var counts = new int[MaxH + 1];
        for (int i = 0; i < n; i++)
        {
            int v = filled[i];
            if (v > MaxH) v = MaxH; else if (v < 0) v = 0;
            counts[v]++;
        }
        var pos = new int[MaxH + 1];
        int acc = 0;
        for (int v = MaxH; v >= 0; v--) { pos[v] = acc; acc += counts[v]; } // убывание высот
        var cursor = (int[])pos.Clone();
        var order = new int[n];
        for (int i = 0; i < n; i++)
        {
            int v = filled[i];
            if (v > MaxH) v = MaxH; else if (v < 0) v = 0;
            order[cursor[v]++] = i;
        }

        foreach (int i in order)
        {
            int x = i % W, y = i / W;
            int best = -1, bestH = filled[i];
            for (int d = 0; d < 8; d++)
            {
                int nx = x + DX[d], ny = y + DY[d];
                if ((uint)nx >= W || (uint)ny >= W) continue;
                int j = ny * W + nx;
                if (filled[j] < bestH) { bestH = filled[j]; best = j; }
            }
            if (best >= 0) flow[best] += flow[i];
        }
        return flow;
    }
}
