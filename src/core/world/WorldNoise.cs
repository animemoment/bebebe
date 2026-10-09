using System;

namespace Game.Core;

/// <summary>
/// Абсолютно-координатный градиентный (Perlin) шум для единого генератора мира.
/// В отличие от NoiseGenerator (bbox-индексы), здесь узлы решётки адресуются
/// ХЕШЕМ МИРОВЫХ координат — значение в точке (wx,wy) не зависит от того,
/// какое окно/bbox его запросило. Это фундамент бесшовности чанков.
/// Чистый C#, без Godot API, потокобезопасен (не использует статическое состояние).
/// </summary>
public static class WorldNoise
{
    private const uint GradCount = 8u;

    // 8 направлений (x,y) ×127: |dot| ≤ ~0.707·127 → нормировка *0.5/127+0.5 ∈ [0,1].
    private static readonly int[] GradX = { 127, -127, 0, 0, 90, -90, 90, -90 };
    private static readonly int[] GradY = { 0, 0, 127, -127, 90, 90, -90, -90 };

    /// <summary>Стабильный 32-бит хеш сида + домена + целочисленного узла решётки.</summary>
    internal static uint HashNode(uint seed, uint domain, int ix, int iy)
    {
        unchecked
        {
            uint h = seed * 2654435761u ^ 0x9E3779B9u;
            h ^= (uint)ix * 2246822519u;
            h ^= (uint)iy * 3266489917u;
            h ^= domain * 668265263u;
            h ^= h >> 15; h *= 2246822519u;
            h ^= h >> 13; h *= 3266489917u;
            h ^= h >> 16;
            return h;
        }
    }

    /// <summary>Значение одной октавы в [-1..1] (сглаживание smoothstep).</summary>
    public static float GradientOctave(uint seed, uint domain, float x, float y, float scale)
    {
        if (scale < 2f) scale = 2f;
        float sx = x / scale;
        float sy = y / scale;
        int x0 = (int)MathF.Floor(sx);
        int y0 = (int)MathF.Floor(sy);
        float tx = sx - x0;
        float ty = sy - y0;
        float stx = tx * tx * (3f - 2f * tx);
        float sty = ty * ty * (3f - 2f * ty);

        int g00 = (int)(HashNode(seed, domain, x0, y0) % GradCount);
        int g10 = (int)(HashNode(seed, domain, x0 + 1, y0) % GradCount);
        int g01 = (int)(HashNode(seed, domain, x0, y0 + 1) % GradCount);
        int g11 = (int)(HashNode(seed, domain, x0 + 1, y0 + 1) % GradCount);

        float v00 = (GradX[g00] * tx + GradY[g00] * ty) * 0.5f / 127f;
        float v10 = (GradX[g10] * (tx - 1f) + GradY[g10] * ty) * 0.5f / 127f;
        float v01 = (GradX[g01] * tx + GradY[g01] * (ty - 1f)) * 0.5f / 127f;
        float v11 = (GradX[g11] * (tx - 1f) + GradY[g11] * (ty - 1f)) * 0.5f / 127f;

        float a = v00 + (v10 - v00) * stx;
        float b = v01 + (v11 - v01) * stx;
        return (a + (b - a) * sty) * 2f;
    }

    /// <summary>fBm в абсолютных мировых координатах, результат ≈ [0..1].</summary>
    public static float FbmAt(uint seed, uint domain, float wx, float wy,
        float baseScale, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        octaves = Math.Clamp(octaves, 1, 6);
        float ampSum = 0f, amp = 1f;
        for (int o = 0; o < octaves; o++) { ampSum += amp; amp *= gain; }
        float sum = 0f;
        amp = 1f;
        float freq = 1f;
        for (int o = 0; o < octaves; o++)
        {
            float n = GradientOctave(seed, domain + (uint)o * 1013u, wx, wy, baseScale / freq);
            sum += (n * 0.5f + 0.5f) * amp;
            amp *= gain;
            freq *= lacunarity;
        }
        return sum / ampSum;
    }

    /// <summary>Ridged fBm (острые хребты), результат ≈ [0..1], 1 = гребень.</summary>
    public static float RidgedFbmAt(uint seed, uint domain, float wx, float wy,
        float baseScale, int octaves)
    {
        octaves = Math.Clamp(octaves, 1, 6);
        float ampSum = 0f, amp = 1f;
        for (int o = 0; o < octaves; o++) { ampSum += amp; amp *= 0.5f; }
        float sum = 0f;
        amp = 1f;
        float freq = 1f;
        for (int o = 0; o < octaves; o++)
        {
            float n = GradientOctave(seed, domain + (uint)o * 1013u + 55555u, wx, wy, baseScale / freq);
            float ridged = 1f - Math.Abs(n); // 0..1, 1 = гребень
            sum += ridged * ridged * amp;
            amp *= 0.5f;
            freq *= 2.1f;
        }
        return sum / ampSum;
    }

    /// <summary>Voronoi/Worley F1 по клеткам решётки (масштаб = сторона ячейки).</summary>
    public static float VoronoiF1(uint seed, uint domain, float wx, float wy, float cellSize)
    {
        if (cellSize < 2f) cellSize = 2f;
        int cx = (int)MathF.Floor(wx / cellSize);
        int cy = (int)MathF.Floor(wy / cellSize);
        float best = float.MaxValue;
        for (int oy = -1; oy <= 1; oy++)
            for (int ox = -1; ox <= 1; ox++)
            {
                uint h = HashNode(seed, domain, cx + ox, cy + oy);
                float px = (cx + ox) * cellSize + (h & 0xFFFF) / 65535f * cellSize;
                float py = (cy + oy) * cellSize + ((h >> 16) & 0xFFFF) / 65535f * cellSize;
                float dx = px - wx, dy = py - wy;
                float d = dx * dx + dy * dy;
                if (d < best) best = d;
            }
        return MathF.Sqrt(best) / cellSize; // 0..~1.4, обычно клампят
    }

    /// <summary>Детерминированный uniform [0..1) по мировым координатам клетки.</summary>
    public static double CellRandom(uint seed, uint domain, long wx, long wy)
    {
        unchecked
        {
            uint h = (uint)wx * 73856093u ^ (uint)wy * 19349663u ^ seed * 40503u ^ domain * 83492791u;
            h ^= h >> 13; h *= 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (double)(1UL << 24);
        }
    }
}
