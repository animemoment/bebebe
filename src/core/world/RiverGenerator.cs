using System;
using System.Collections.Generic;

namespace Game.Core;

public static class RiverGenerator
{
    public const int MaxBranches = 2;

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

    private static List<Vec> WalkDownhill(
        float[,] heightMap, TileType[,] ground, int w, int h, Random rng,
        int sx, int sy, int maxSteps, bool canEndInWater)
    {
        var path = new List<Vec>(256);
        var visited = new HashSet<int>();
        int x = sx, y = sy;
        int dx = 0, dy = 1;
        int uphill = 0;

        for (int step = 0; step < maxSteps; step++)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) break;
            int key = y * w + x;
            if (!visited.Add(key)) break;
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
                if (ddx * ddx + ddy * ddy <= r2)
                    ground[x, y] = TileType.Water;
            }
    }

    private readonly struct Vec
    {
        public readonly int X;
        public readonly int Y;
        public Vec(int x, int y) { X = x; Y = y; }
    }
}
