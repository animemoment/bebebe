using System;
using System.Collections.Generic;

namespace Game.Core;

public static class MapGenerator
{
    public const int MaxLakes = 2;
    public const int MinLakeSize = 300;
    public const int FillTrashWaterBelow = 150;
    public const int StartClearRadius = 12;
    public const float MinGrassRatio = 0.55f;
    public const float MinLandConnectivity = 0.92f;

    public static MapData Generate(int width, int height, uint seed)
    {
        var paramRng = new Random(unchecked((int)(seed * 2654435761u)));

        float heightScale = 90f + (float)paramRng.NextDouble() * 50f;
        float groveScale = 60f + (float)paramRng.NextDouble() * 40f;
        float waterThreshold = 0.38f + (float)paramRng.NextDouble() * 0.04f;
        float mountainThreshold = 0.70f + (float)paramRng.NextDouble() * 0.06f;
        float groveThreshold = 0.55f + (float)paramRng.NextDouble() * 0.07f;
        int octaves = 4 + paramRng.Next(2);
        float innerDensity = 0.78f + (float)paramRng.NextDouble() * 0.17f;
        int groveRadiusMin = 5 + paramRng.Next(3);
        int groveRadiusMax = groveRadiusMin + 2 + paramRng.Next(4);

        MapData best = null;
        float bestScore = float.NegativeInfinity;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            float wt = waterThreshold - attempt * 0.015f;
            uint s = seed + (uint)attempt * 7919u;
            var data = GenerateOnce(width, height, s,
                heightScale, groveScale, wt, mountainThreshold, groveThreshold, octaves,
                innerDensity, groveRadiusMin, groveRadiusMax,
                unchecked((uint)(s * 2246822519u + 999u)));
            float score = Score(data, width, height);
            if (best == null || score > bestScore) { best = data; bestScore = score; }
            if (MeetsPlayability(data, width, height))
                return data;
        }

        return best;
    }
    private static float Score(MapData d, int w, int h)
    {
        int grass = CountGrass(d, w, h);
        float g = grass / (float)(w * h);
        float s = g - Math.Abs(g - 0.72f) * 0.5f;
        s += d.LandConnectivity * 0.5f;
        if (d.LakeCount > MaxLakes) s -= (d.LakeCount - MaxLakes) * 0.5f;
        if (d.MainRiverLength <= 0) s -= 0.2f;
        float m = CountMountains(d, w, h) / (float)(w * h);
        if (m > 0.15f) s -= (m - 0.15f) * 2f;
        return s;
    }

    private static bool MeetsMountains(MapData d, int w, int h)
    {
        return CountMountains(d, w, h) / (float)(w * h) < 0.18f;
    }

    private static bool MeetsPlayability(MapData d, int w, int h)
    {
        int grass = CountGrass(d, w, h);
        float g = grass / (float)(w * h);
        return g >= MinGrassRatio && d.LandConnectivity >= MinLandConnectivity && d.LakeCount <= MaxLakes && MeetsMountains(d, w, h);
    }

    private static MapData GenerateOnce(
        int width, int height, uint seed,
        float heightScale, float groveScale,
        float waterThreshold, float mountainThreshold, float groveThreshold, int octaves,
        float innerDensity, int groveRadiusMin, int groveRadiusMax, uint forestSeed)
    {
        var data = new MapData(width, height);
        data.Seed = seed;

        uint seedH = seed;
        uint seedF = forestSeed;

        float[,] heightMap = NoiseGenerator.GenerateFbmMap(width, height, seedH, heightScale, octaves);
        ApplyIslandFalloff(heightMap, width, height);

        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                data.Ground[x, y] = heightMap[x, y] < waterThreshold ? TileType.Water : TileType.Grass;

        EnforceLakeLimit(data, width, height);

        var river = RiverGenerator.CarveSingleRiver(
            data.Ground, heightMap, width, height, seed + 5000u, RiverGenerator.MaxBranches);
        data.MainRiverLength = river.MainLength;
        data.RiverBranchCount = river.BranchCount;

        CarveMountains(data, heightMap, width, height, mountainThreshold);

        EnsureLandConnectivity(data, width, height);
        ClearStartArea(data, width, height);
        PlantForests(data, heightMap, width, height, seed, groveScale, groveThreshold, octaves,
            innerDensity, groveRadiusMin, groveRadiusMax, seedF);

        // Влажность после всех правок рельефа: шум влаги + вода + высота.
        float[,] moistNoise = NoiseGenerator.GenerateFbmMap(width, height, seedF + 777u, groveScale * 0.7f, 3);
        data.Humidity.Initialize(data.Ground, moistNoise);

        return data;
    }


    /// <summary>
    /// Горы по верху heightMap (после реки, до связности).
    /// Река уже прорезала воду; одиночные компоненты &lt; 6 кл. засыпаются травой.
    /// </summary>
    private static void CarveMountains(MapData data, float[,] heightMap, int w, int h, float mountainThreshold)
    {
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if (data.Ground[x, y] != TileType.Grass) continue;
                if (heightMap[x, y] > mountainThreshold)
                    data.Ground[x, y] = TileType.Mountain;
            }

        // Чистка одиночек: мелкие компоненты гор возвращаем в траву.
        var label = new int[w, h];
        int comps = 0;
        var compCells = new List<int>();
        var stack = new Stack<int>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (data.Ground[x, y] != TileType.Mountain || label[x, y] != 0) continue;
                comps++;
                compCells.Clear();
                stack.Clear();
                stack.Push(y * w + x);
                label[x, y] = comps;
                while (stack.Count > 0)
                {
                    int code = stack.Pop();
                    compCells.Add(code);
                    int cx = code % w, cy = code / w;
                    if (cx > 0 && data.Ground[cx - 1, cy] == TileType.Mountain && label[cx - 1, cy] == 0)
                    { label[cx - 1, cy] = comps; stack.Push(cy * w + cx - 1); }
                    if (cx + 1 < w && data.Ground[cx + 1, cy] == TileType.Mountain && label[cx + 1, cy] == 0)
                    { label[cx + 1, cy] = comps; stack.Push(cy * w + cx + 1); }
                    if (cy > 0 && data.Ground[cx, cy - 1] == TileType.Mountain && label[cx, cy - 1] == 0)
                    { label[cx, cy - 1] = comps; stack.Push((cy - 1) * w + cx); }
                    if (cy + 1 < h && data.Ground[cx, cy + 1] == TileType.Mountain && label[cx, cy + 1] == 0)
                    { label[cx, cy + 1] = comps; stack.Push((cy + 1) * w + cx); }
                }
                if (compCells.Count < 6)
                    foreach (int code in compCells)
                        data.Ground[code % w, code / w] = TileType.Grass;
            }
    }

    private static int CountMountains(MapData data, int width, int height)
    {
        int count = 0;
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                if (data.Ground[x, y] == TileType.Mountain)
                    count++;
        return count;
    }

    private static void ApplyIslandFalloff(float[,] hm, int w, int h)
    {
        float cx = (w - 1) * 0.5f, cy = (h - 1) * 0.5f;
        float maxD = MathF.Sqrt(cx * cx + cy * cy);
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                float dx = (x - cx) / maxD, dy = (y - cy) / maxD;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                // Море начинается только у самой кромки: до 0.62 радиуса — без изменений.
                float t = Math.Clamp((d - 0.62f) / 0.09f, 0f, 1f);
                float fall = t * t * (3f - 2f * t);
                hm[x, y] -= fall * 0.35f;
            }
    }

    private static void EnforceLakeLimit(MapData data, int w, int h)
    {
        int[,] label = new int[w, h];
        var sizes = new List<int> { 0 };
        var touchesEdge = new List<bool> { false };
        int comps = 0;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (data.Ground[x, y] != TileType.Water || label[x, y] != 0) continue;
                comps++;
                sizes.Add(0);
                touchesEdge.Add(false);
                var stack = new Stack<int>();
                stack.Push(y * w + x);
                label[x, y] = comps;
                while (stack.Count > 0)
                {
                    int code = stack.Pop();
                    int cx = code % w, cy = code / w;
                    sizes[comps]++;
                    if (cx == 0 || cy == 0 || cx == w - 1 || cy == h - 1)
                        touchesEdge[comps] = true;
                    if (cx > 0 && data.Ground[cx - 1, cy] == TileType.Water && label[cx - 1, cy] == 0)
                    { label[cx - 1, cy] = comps; stack.Push(cy * w + cx - 1); }
                    if (cx + 1 < w && data.Ground[cx + 1, cy] == TileType.Water && label[cx + 1, cy] == 0)
                    { label[cx + 1, cy] = comps; stack.Push(cy * w + cx + 1); }
                    if (cy > 0 && data.Ground[cx, cy - 1] == TileType.Water && label[cx, cy - 1] == 0)
                    { label[cx, cy - 1] = comps; stack.Push((cy - 1) * w + cx); }
                    if (cy + 1 < h && data.Ground[cx, cy + 1] == TileType.Water && label[cx, cy + 1] == 0)
                    { label[cx, cy + 1] = comps; stack.Push((cy + 1) * w + cx); }
                }
            }

        var inner = new List<int>();
        for (int i = 1; i <= comps; i++)
            if (!touchesEdge[i]) inner.Add(i);
        inner.Sort((a, b) => sizes[b].CompareTo(sizes[a]));

        var keep = new HashSet<int>();
        for (int k = 0; k < inner.Count && keep.Count < MaxLakes; k++)
        {
            int id = inner[k];
            if (sizes[id] < MinLakeSize)
            {
                if (keep.Count == 0 && sizes[id] >= FillTrashWaterBelow) keep.Add(id);
                continue;
            }
            keep.Add(id);
        }

        var keptSizes = new List<int>();
        foreach (int id in keep) keptSizes.Add(sizes[id]);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (data.Ground[x, y] != TileType.Water) continue;
                int id = label[x, y];
                if (id == 0 || touchesEdge[id] || keep.Contains(id)) continue;
                data.Ground[x, y] = TileType.Grass;
            }

        keptSizes.Sort((a, b) => b.CompareTo(a));
        data.LakeCount = keptSizes.Count;
        data.LakeSizes = keptSizes.ToArray();
    }
    private static void EnsureLandConnectivity(MapData data, int w, int h)
    {
        int total = 0;
        int sx = -1, sy = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (data.Ground[x, y] == TileType.Grass)
                {
                    total++;
                    if (sx < 0 && x >= w / 4 && x < w * 3 / 4 && y >= h / 4 && y < h * 3 / 4)
                    { sx = x; sy = y; }
                }
        if (total == 0) { data.LandConnectivity = 0f; return; }
        if (sx < 0)
        {
            for (int y = 0; y < h && sx < 0; y++)
                for (int x = 0; x < w; x++)
                    if (data.Ground[x, y] == TileType.Grass) { sx = x; sy = y; break; }
        }

        var seen = new bool[w, h];
        var queue = new Queue<int>();
        queue.Enqueue(sy * w + sx);
        seen[sx, sy] = true;
        int reached = FloodFillCount(data, w, h, seen, queue);

        data.LandConnectivity = reached / (float)total;
        if (data.LandConnectivity >= MinLandConnectivity || reached == total) return;

        int bridges = 0;
        for (int y = 1; y < h - 1 && bridges < 4; y++)
            for (int x = 1; x < w - 1 && bridges < 4; x++)
            {
                if (data.Ground[x, y] != TileType.Water && data.Ground[x, y] != TileType.Mountain) continue;
                bool leftSeen = data.Ground[x - 1, y] == TileType.Grass && seen[x - 1, y];
                bool rightGrass = data.Ground[x + 1, y] == TileType.Grass && !seen[x + 1, y];
                bool upSeen = data.Ground[x, y - 1] == TileType.Grass && seen[x, y - 1];
                bool downGrass = data.Ground[x, y + 1] == TileType.Grass && !seen[x, y + 1];
                if ((leftSeen && rightGrass) || (upSeen && downGrass))
                {
                    data.Ground[x, y] = TileType.Grass;
                    bridges++;
                }
            }

        if (bridges > 0)
        {
            Array.Clear(seen, 0, seen.Length);
            var q2 = new Queue<int>();
            q2.Enqueue(sy * w + sx);
            seen[sx, sy] = true;
            int r2 = FloodFillCount(data, w, h, seen, q2);
            data.LandConnectivity = r2 / (float)Math.Max(1, CountGrass(data, w, h));
        }
    }

    private static int FloodFillCount(MapData data, int w, int h, bool[,] seen, Queue<int> queue)
    {
        int reached = 0;
        while (queue.Count > 0)
        {
            int code = queue.Dequeue();
            int cx = code % w, cy = code / w;
            reached++;
            if (cx > 0 && !seen[cx - 1, cy] && data.Ground[cx - 1, cy] == TileType.Grass)
            { seen[cx - 1, cy] = true; queue.Enqueue(cy * w + cx - 1); }
            if (cx + 1 < w && !seen[cx + 1, cy] && data.Ground[cx + 1, cy] == TileType.Grass)
            { seen[cx + 1, cy] = true; queue.Enqueue(cy * w + cx + 1); }
            if (cy > 0 && !seen[cx, cy - 1] && data.Ground[cx, cy - 1] == TileType.Grass)
            { seen[cx, cy - 1] = true; queue.Enqueue((cy - 1) * w + cx); }
            if (cy + 1 < h && !seen[cx, cy + 1] && data.Ground[cx, cy + 1] == TileType.Grass)
            { seen[cx, cy + 1] = true; queue.Enqueue((cy + 1) * w + cx); }
        }
        return reached;
    }

    private static void ClearStartArea(MapData data, int w, int h)
    {
        int cx = w / 2, cy = h / 2;
        int r2 = StartClearRadius * StartClearRadius;
        for (int x = Math.Max(0, cx - StartClearRadius); x <= Math.Min(w - 1, cx + StartClearRadius); x++)
            for (int y = Math.Max(0, cy - StartClearRadius); y <= Math.Min(h - 1, cy + StartClearRadius); y++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > r2) continue;
                // Старт-поляна: горы и вода засыпаются травой.
                data.Ground[x, y] = TileType.Grass;
                data.TreeOnGrass[x, y] = false;
            }
    }
    private static void PlantForests(
        MapData data, float[,] heightMap, int w, int h, uint seed,
        float groveScale, float groveThreshold, int octaves,
        float innerDensity, int groveRadiusMin, int groveRadiusMax, uint forestSeed)
    {
        float[,] groveMap = NoiseGenerator.GenerateFbmMap(w, h, forestSeed, groveScale, octaves);
        float[,] moisture = NoiseGenerator.GenerateFbmMap(w, h, forestSeed + 777u, groveScale * 0.7f, 3);
        var treeRng = new Random(unchecked((int)(seed * 40503u + 7u)));
        var clusterRng = new Random(unchecked((int)(seed * 2246822519u + 999u)));

        var groves = new List<(int X, int Y, int R)>();
        int minDist = Math.Max(10, groveRadiusMax * 2 + 4);
        int attempts = (w * h) / (minDist * minDist) * 8;
        for (int a = 0; a < attempts && groves.Count < 220; a++)
        {
            int x = clusterRng.Next(w), y = clusterRng.Next(h);
            if (data.Ground[x, y] != TileType.Grass) continue;
            if (groveMap[x, y] <= groveThreshold) continue;
            bool tooClose = false;
            foreach (var g in groves)
            {
                int dx = g.X - x, dy = g.Y - y;
                if (dx * dx + dy * dy < minDist * minDist) { tooClose = true; break; }
            }
            if (tooClose) continue;
            int waterNear = 0;
            for (int oy = -4; oy <= 4 && waterNear < 13; oy++)
                for (int ox = -4; ox <= 4; ox++)
                {
                    int nx = x + ox, ny = y + oy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    if (data.Ground[nx, ny] == TileType.Water) waterNear++;
                }
            double accept = 0.35 + moisture[x, y] * 0.5 + Math.Min(1, waterNear / 12f) * 0.35;
            if (clusterRng.NextDouble() > accept) continue;
            int r = groveRadiusMin + clusterRng.Next(groveRadiusMax - groveRadiusMin + 1);
            groves.Add((x, y, r));
        }

        int[,] groveId = new int[w, h];
        for (int i = 0; i < groves.Count; i++)
        {
            var (gx, gy, r) = groves[i];
            int rr = r + 2;
            for (int x = Math.Max(0, gx - rr); x <= Math.Min(w - 1, gx + rr); x++)
                for (int y = Math.Max(0, gy - rr); y <= Math.Min(h - 1, gy + rr); y++)
                {
                    if (groveId[x, y] != 0) continue;
                    int dx = x - gx, dy = y - gy;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    float edgeNeed = groveThreshold - 0.12f + 0.12f * (dist / rr);
                    if (dist <= r || groveMap[x, y] > edgeNeed)
                        groveId[x, y] = i + 1;
                }
        }

        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                data.TreeVariant[x, y] = (byte)TreeVariantFor(x, y, seed);
                if (data.Ground[x, y] != TileType.Grass) { data.TreeOnGrass[x, y] = false; continue; }
                if (groveId[x, y] == 0) { data.TreeOnGrass[x, y] = false; continue; }
                bool clearing = groveMap[x, y] < groveThreshold - 0.02f && treeRng.NextDouble() < 0.75;
                data.TreeOnGrass[x, y] = !clearing && treeRng.NextDouble() < innerDensity;
            }
    }

    public static int TreeVariantFor(int x, int y, uint seed)
    {
        unchecked
        {
            uint hb = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (seed * 83492791u);
            hb ^= hb >> 13;
            hb *= 1274126177u;
            hb ^= hb >> 16;
            return (int)(hb % 4u);
        }
    }

    private static int CountGrass(MapData data, int width, int height)
    {
        int count = 0;
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                if (data.Ground[x, y] == TileType.Grass)
                    count++;
        return count;
    }
}


