using System;
using System.Collections.Generic;
using Game.Core.WorldStreaming;

namespace Game.Core;

public static class RiverGenerator
{
    public const int MaxBranches = 2;
    public const int AnchorSize = 512; // размер anchor-ячейки для river presence

    public static (int MainLength, int BranchCount) CarveSingleRiver(
        TileType[,] ground, float[,] heightMap, int width, int height, uint seed,
        int maxBranches = MaxBranches)
    {
        var rng = new Random(unchecked((int)(seed * 2246822519u + 5000u)));
        maxBranches = Math.Clamp(maxBranches, 0, MaxBranches);

        if (!TryPickSource(heightMap, ground, width, height, rng, out int sx, out int sy))
            return (0, 0);

        var mainPath = WalkDownhill(heightMap, ground, width, height, rng, sx, sy, width + height, true);
        if (mainPath.Count < 10)
            return (0, 0);

        for (int i = 0; i < mainPath.Count; i++)
        {
            float t = mainPath.Count <= 1 ? 1f : i / (float)(mainPath.Count - 1);
            int r = t < 0.4f ? 1 : t < 0.75f ? 2 : 2;
            FillDisc(ground, width, height, mainPath[i].X, mainPath[i].Y, r);
            // Каньон-врез (п.19.1-1, эрозия-лайт): вдоль русла heightMap слегка
            // проседает — долина реки читается даже там, где воды уже нет
            // (старицы/сухие участки), влага потом стекает в эту ложбину.
            CarveCanyon(heightMap, width, height, mainPath[i].X, mainPath[i].Y, r);
        }

        int branches = 0;
        float f0 = 0.32f, f1 = 0.62f;
        if (rng.Next(2) == 0) { float tmp = f0; f0 = f1; f1 = tmp; }

        if (maxBranches >= 1 && TryCarveBranch(ground, heightMap, width, height, rng, mainPath, f0))
            branches++;
        if (maxBranches >= 2 && TryCarveBranch(ground, heightMap, width, height, rng, mainPath, f1))
            branches++;

        return (mainPath.Count, branches);
    }

    private static bool TryCarveBranch(
        TileType[,] ground, float[,] heightMap, int w, int h, Random rng,
        List<Vec> mainPath, float fraction)
    {
        // anchorIdx обязан иметь валидный диапазон [4, Count-5]:
        // при Count<10 Clamp(min>max) кидает ArgumentOutOfRangeException.
        // Короткие реки (<10) уже отфильтрованы выше.
        int anchorIdx = Math.Clamp((int)(mainPath.Count * fraction), 4, mainPath.Count - 5);
        var anchor = mainPath[anchorIdx];
        if (!TryFindBranchStart(ground, w, h, rng, anchor.X, anchor.Y, out int bx, out int by))
            return false;
        var branch = WalkDownhill(heightMap, ground, w, h, rng, bx, by, mainPath.Count * 2 / 5, false);
        if (branch.Count < 10)
            return false;
        foreach (var c in branch)
            FillDisc(ground, w, h, c.X, c.Y, 1);
        return true;
    }

    public static void GenerateRivers(TileType[,] ground, int width, int height, uint seed, int riverCount = 2, int minWidth = 1, int maxWidth = 2)
    {
        if (riverCount <= 0) return;
        float[,] hm = NoiseGenerator.GenerateFbmMap(width, height, seed, 64f, 3);
        CarveSingleRiver(ground, hm, width, height, seed, MaxBranches);
    }

    private static bool TryPickSource(float[,] heightMap, TileType[,] ground, int w, int h, Random rng, out int sx, out int sy)
    {
        sx = w / 2; sy = h / 4;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            int x = w / 8 + rng.Next(w * 6 / 8);
            int y = h / 16 + rng.Next(h * 6 / 16);
            if (ground[x, y] != TileType.Grass) continue;
            if (heightMap[x, y] < 0.52f) continue;
            sx = x; sy = y;
            return true;
        }
        for (int attempt = 0; attempt < 64; attempt++)
        {
            int x = rng.Next(w);
            int y = rng.Next(h / 2);
            if (ground[x, y] == TileType.Grass) { sx = x; sy = y; return true; }
        }
        return false;
    }

    private static bool TryFindBranchStart(TileType[,] ground, int w, int h, Random rng, int ax, int ay, out int bx, out int by)
    {
        bx = ax; by = ay;
        for (int attempt = 0; attempt < 32; attempt++)
        {
            double ang = rng.NextDouble() * Math.PI * 2.0;
            int dist = 6 + rng.Next(5);
            int x = Math.Clamp(ax + (int)Math.Round(Math.Cos(ang) * dist), 1, w - 2);
            int y = Math.Clamp(ay + (int)Math.Round(Math.Sin(ang) * dist), 1, h - 2);
            if (ground[x, y] == TileType.Grass) { bx = x; by = y; return true; }
        }
        return false;
    }

    /// <summary>Штраф входа в гору для WalkDownhill (п.19.1-2): река огибает
    /// скалы, а не течёт сквозь них. FillDisc горы тоже не затирает.</summary>
    public const float MountainPenalty = 0.15f;

    private static List<Vec> WalkDownhill(
        float[,] heightMap, TileType[,] ground, int w, int h, Random rng,
        int sx, int sy, int maxSteps, bool canEndInWater)
    {
        var path = new List<Vec>(256);
        // Stamp-посещения вместо HashSet (п.19.3-10): ноль хеширования,
        // один массив на вызов реки, O(1) проверка.
        var visitedStamp = new int[w * h];
        int stamp = 1;
        int x = sx, y = sy;
        int dx = 0, dy = 1;
        int uphill = 0;

        for (int step = 0; step < maxSteps; step++)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) break;
            int key = y * w + x;
            if (visitedStamp[key] == stamp) break;
            visitedStamp[key] = stamp;
            path.Add(new Vec(x, y));

            if (x <= 1 || y <= 1 || x >= w - 2 || y >= h - 2) break;
            if (canEndInWater && path.Count > 4 && ground[x, y] == TileType.Water) break;

            float cur = heightMap[x, y];
            int bestX = x, bestY = y;
            float bestScore = float.NegativeInfinity;

            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (ox == 0 && oy == 0) continue;
                    int nx = x + ox, ny = y + oy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    float drop = cur - heightMap[nx, ny];
                    float inertia = (ox == dx && oy == dy) ? 0.012f : (ox * dx + oy * dy < 0 ? -0.02f : 0f);
                    float meander = ((float)rng.NextDouble() - 0.5f) * 0.02f;
                    float score = drop * 4f + inertia + meander;
                    // Гора отталкивает реку: огибаем скалу стороной (п.19.1-2).
                    if (ground[nx, ny] == TileType.Mountain)
                        score -= MountainPenalty;
                    if (score > bestScore) { bestScore = score; bestX = nx; bestY = ny; }
                }
            }

            if (bestX == x && bestY == y) break;
            if (bestScore < -0.02f) { if (++uphill >= 6) break; }
            else uphill = 0;

            dx = bestX - x; dy = bestY - y;
            x = bestX; y = bestY;
        }

        return path;
    }

    private static void FillDisc(TileType[,] ground, int width, int height, int cx, int cy, int radius)
    {
        int xStart = Math.Max(0, cx - radius);
        int xEnd = Math.Min(width - 1, cx + radius);
        int yStart = Math.Max(0, cy - radius);
        int yEnd = Math.Min(height - 1, cy + radius);
        int r2 = radius * radius;
        for (int x = xStart; x <= xEnd; x++)
            for (int y = yStart; y <= yEnd; y++)
            {
                int ddx = x - cx, ddy = y - cy;
                if (ddx * ddx + ddy * ddy > r2) continue;
                // Горы река не прорезает (п.19.1-2): вода огибает скалу.
                if (ground[x, y] == TileType.Mountain) continue;
                ground[x, y] = TileType.Water;
            }
    }

    /// <summary>
    /// Каньон-врез вдоль русла (эрозия-лайт, п.19.1-1): heightMap проседает
    /// на 0.05 в центре русла с затуханием к краям. Долина читается рельефом.
    /// Горы не врезаем (скала держит форму).
    /// </summary>
    private static void CarveCanyon(float[,] heightMap, int w, int h, int cx, int cy, int radius)
    {
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(h - 1, cy + radius); y++)
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(w - 1, cx + radius); x++)
            {
                int ddx = x - cx, ddy = y - cy;
                if (ddx * ddx + ddy * ddy > radius * radius) continue;
                heightMap[x, y] -= 0.05f * (1f - MathF.Sqrt(ddx * ddx + ddy * ddy) / (radius + 1f));
            }
    }

    /// <summary>Точка с координатами для пути реки.</summary>
    public readonly struct Vec
    {
        public readonly int X;
        public readonly int Y;
        public Vec(int x, int y) { X = x; Y = y; }
    }
    
    // === Streaming rivers (§24-§33): детерминированная расстановка по anchor-сетке ===
    
    /// <summary>
    /// Определяет наличие реки в anchor-ячейке 512×512. Детерминировано от seed + координат.
    /// Река появляется когда hash % 3 == 0 — примерно каждые 3 anchor-ячейки.
    /// </summary>
    public static bool HasRiverAtAnchor(ulong worldSeed, uint generatorVersion, long anchorX, long anchorY)
    {
        ulong h = CoordinateHash.Hash((ulong)worldSeed, generatorVersion,
            GenerationDomain.RiverPresence, anchorX, anchorY, 1);
        return h % 3ul == 0;
    }

    /// <summary>
    /// Carves river(s) into ground map for a bounded region. Uses local heightMap to find
    /// sources and walks downhill. Supports cross-region rivers via anchored grid.
    /// </summary>
    public static void CarveRiversInRegion(TileType[,] ground, float[,] heightMap,
        int minX, int maxX, int minY, int maxY,
        uint seed, long riverIdHint = -1)
    {
        int width = maxX - minX;
        int height = maxY - minY;
        if (width <= 1 || height <= 1) return;

        long anchorMinX = FloorDiv(minX, AnchorSize);
        long anchorMaxX = FloorDiv(maxX - 1, AnchorSize);
        long anchorMinY = FloorDiv(minY, AnchorSize);
        long anchorMaxY = FloorDiv(maxY - 1, AnchorSize);

        for (long ay = anchorMinY; ay <= anchorMaxY; ay++)
        {
            for (long ax = anchorMinX; ax <= anchorMaxX; ax++)
            {
                if (!HasRiverAtAnchor((ulong)seed, 0, ax, ay)) continue;
                if (riverIdHint >= 0 && ax != riverIdHint) continue;
                CarveSingleRiverFromAnchor(ground, heightMap, minX, maxX, minY, maxY, width, height,
                    seed, ax, ay);
            }
        }
    }

    private static void CarveSingleRiverFromAnchor(TileType[,] ground, float[,] heightMap,
        int minX, int maxX, int minY, int maxY,
        int regionWidth, int regionHeight, uint seed,
        long anchorX, long anchorY)
    {
        long originX = anchorX * AnchorSize;
        long originY = anchorY * AnchorSize;
        int sx = unchecked((int)(originX + 32 + ((uint)(seed >> 4) % 200)));
        int sy = unchecked((int)(originY + 16 + ((uint)(seed >> 12) % 80)));

        sx = Math.Clamp(sx, Math.Max(minX + 1, minX + 4), maxX - 2);
        sy = Math.Clamp(sy, Math.Max(minY + 1, minY + 4), maxY - 2);

        if (sx < minX || sx >= maxX || sy < minY || sy >= maxY) return;
        if (ground[sx - minX, sy - minY] != TileType.Grass) return;
        if (heightMap[sx - minX, sy - minY] < 0.45f) return;

        var path = WalkDownhillFromSource(heightMap, ground, minX, maxX, minY, maxY, sx, sy, regionWidth + regionHeight);

        if (path.Count < 8) return;

        for (int i = 0; i < path.Count; i++)
        {
            var p = path[i];
            if ((uint)p.X >= (uint)regionWidth || (uint)p.Y >= (uint)regionHeight) continue;
            int r = i < path.Count * 0.3 ? 1 : i < path.Count * 0.7 ? 2 : 2;
            FillDiscRelative(ground, heightMap, minX, maxX, minY, maxY, p.X, p.Y, r);
        }

        for (int i = 0; i < path.Count; i++)
        {
            var p = path[i];
            if ((uint)p.X >= (uint)regionWidth || (uint)p.Y >= (uint)regionHeight) continue;
            CarveCanyonRelative(heightMap, minX, maxX, minY, maxY, p.X, p.Y, 2);
        }
    }

    public static bool TryFindSourceNear(float[,] heightMap, TileType[,] ground,
        int minX, int maxX, int minY, int maxY, int nearX, int nearY, float heightThreshold,
        out int sourceX, out int sourceY)
    {
        for (int r = 0; r < 32; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    int px = nearX + dx, py = nearY + dy;
                    if (px < minX || px >= maxX || py < minY || py >= maxY) continue;
                    if (ground[px - minX, py - minY] != TileType.Grass) continue;
                    if (heightMap[px - minX, py - minY] >= heightThreshold)
                    { sourceX = px; sourceY = py; return true; }
                }
            }
        }
        sourceX = 0; sourceY = 0; return false;
    }

    public static List<Vec> WalkDownhillFromSource(float[,] heightMap, TileType[,] ground,
        int minX, int maxX, int minY, int maxY, int sourceX, int sourceY, int maxSteps)
    {
        int w = maxX - minX, h = maxY - minY;
        var path = new List<Vec>(256);
        int stamp = 1;
        var visitedStamp = new int[w * h];

        int x = sourceX - minX, y = sourceY - minY;
        int dx = 0, dy = 1, uphill = 0;

        for (int step = 0; step < maxSteps; step++)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) break;
            int key = y * w + x;
            if (visitedStamp[key] == stamp) break;
            visitedStamp[key] = stamp;
            path.Add(new Vec(sourceX + (x - (sourceX - minX)), sourceY + (y - (sourceY - minY))));

            if (x <= 1 || y <= 1 || x >= w - 2 || y >= h - 2) break;
            if (ground[x, y] == TileType.Water) break;

            float cur = heightMap[x, y];
            int bestX = x, bestY = y;
            float bestScore = float.NegativeInfinity;

            for (int oy = -1; oy <= 1; oy++)
            for (int ox = -1; ox <= 1; ox++)
            {
                if (ox == 0 && oy == 0) continue;
                int nx = x + ox, ny = y + oy;
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                float drop = cur - heightMap[nx, ny];
                float inertia = (ox == dx && oy == dy) ? 0.012f : (ox * dx + oy * dy < 0 ? -0.02f : 0f);
                float score = drop * 4f + inertia;
                if (ground[nx, ny] == TileType.Mountain) score -= 0.15f;
                if (score > bestScore) { bestScore = score; bestX = nx; bestY = ny; }
            }

            if (bestX == x && bestY == y) break;
            if (bestScore < -0.02f) { if (++uphill >= 6) break; }
            else uphill = 0;

            dx = bestX - x; dy = bestY - y;
            x = bestX; y = bestY;
        }

        return path;
    }

    private static void FillDiscRelative(TileType[,] ground, float[,] heightMap,
        int minX, int maxX, int minY, int maxY, int cx, int cy, int radius)
    {
        int r2 = radius * radius;
        for (int y = Math.Max(1, cy - radius); y <= Math.Min(maxY - 2, cy + radius); y++)
        for (int x = Math.Max(1, cx - radius); x <= Math.Min(maxX - 2, cx + radius); x++)
        {
            int ddx = x - cx, ddy = y - cy;
            if (ddx * ddx + ddy * ddy > r2) continue;
            int lx = x - minX, ly = y - minY;
            if (ground[lx, ly] == TileType.Mountain) continue;
            ground[lx, ly] = TileType.Water;
        }
    }

    private static void CarveCanyonRelative(float[,] heightMap,
        int minX, int maxX, int minY, int maxY, int cx, int cy, int radius)
    {
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(maxY - 1, cy + radius); y++)
        for (int x = Math.Max(0, cx - radius); x <= Math.Min(maxX - 1, cx + radius); x++)
        {
            int ddx = x - cx, ddy = y - cy;
            if (ddx * ddx + ddy * ddy > radius * radius) continue;
            heightMap[x, y] -= 0.05f * (1f - MathF.Sqrt(ddx * ddx + ddy * ddy) / (radius + 1f));
        }
    }

    private static long FloorDiv(long x, long y) => x >= 0 ? x / y : ((x - y + 1) / y);
}

