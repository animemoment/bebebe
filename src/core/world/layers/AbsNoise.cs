using System;

namespace Game.Core.WorldStreaming.Layers;

/// <summary>
/// Чистый шум в АБСОЛЮТНЫХ мировых координатах (long x, y) — основа бесшовности.
/// В отличие от NoiseGenerator.GenerateFbmMap (сетка градиентов привязана к левому
/// верхнему углу bbox → P0 «рельеф в локальных координатах»), здесь решётка
/// градиентов глобальная: значение в точке зависит ТОЛЬКО от (seed, domain, x, y).
/// Соседние чанки, запрошенные любым bbox/порядком, дают одинаковые значения на стыке.
///
/// Детерминизм между платформами: все арифметические операции над float выполняются
/// строго слева направо без переупорядочивания выражений (см. Fma-free комментарий),
/// double-аккумуляция fBm стабильна на x86/x64/ARM64 .NET 8+.
/// </summary>
public static class AbsNoise
{
    // 8 направлений как у NoiseGenerator.Gradients — тот же визуальный характер шума.
    private static readonly float[] GradX = { 1f, -1f, 0f, 0f, 0.707f, -0.707f, 0.707f, -0.707f };
    private static readonly float[] GradY = { 0f, 0f, 1f, -1f, 0.707f, 0.707f, -0.707f, -0.707f };

    /// <summary>
    /// Хеш целочисленной точки решётки → индекс градиента (0..7).
    /// Domain-separated через CoordinateHash (стабильный mix across версий).
    /// lane != 0 — независимая «дорожка» того же домена (для октав/warp),
    /// чтобы не требовать новых членов GenerationDomain.
    /// </summary>
    private static int GradientIndex(ulong seed, uint ver, GenerationDomain domain, long gx, long gy, ulong lane)
        => (int)(CoordinateHash.Hash(seed, ver, domain, gx, gy, lane) % 8ul);

    private static float Smoothstep(float t) => t * t * (3f - 2f * t);

    /// <summary>
    /// Одна октава градиентного (Perlin-like) шума в абсолютных координатах.
    /// Возврат примерно [-1..1] (как Dot по 8 направлениям, нормировка не применяется —
    /// fBm нормируется суммой амплитуд, ridged сам приводит диапазон).
    /// scale — клеток на ячейку решётки, должен быть ≥ 2.
    /// </summary>
    public static float GradientOctave(ulong seed, uint ver, GenerationDomain domain,
        long x, long y, float scale, ulong lane = 0)
    {
        if (scale < 2f) scale = 2f;

        // world coords → lattice coords. Отрицательные координаты обрабатываются
        // корректно: MathF.Floor даёт «вниз», frac = sx - floor ∈ [0..1) всегда,
        // целочисленные индексы решётки — signed long (хеш двухкомплементарный).
        float sx = (float)x / scale;
        float sy = (float)y / scale;

        long ix0 = (long)MathF.Floor(sx);
        long iy0 = (long)MathF.Floor(sy);
        float tx = sx - (float)ix0;
        float ty = sy - (float)iy0;

        float stx = Smoothstep(tx);
        float sty = Smoothstep(ty);

        float v00 = DotL(seed, ver, domain, lane, ix0, iy0, tx, ty);
        float v10 = DotL(seed, ver, domain, lane, ix0 + 1, iy0, tx - 1f, ty);
        float v01 = DotL(seed, ver, domain, lane, ix0, iy0 + 1, tx, ty - 1f);
        float v11 = DotL(seed, ver, domain, lane, ix0 + 1, iy0 + 1, tx - 1f, ty - 1f);

        float a = v00 + (v10 - v00) * stx;
        float b = v01 + (v11 - v01) * stx;
        return a + (b - a) * sty;
    }

    /// <summary>
    /// Ridged-октава: 1-|n| в квадрате — острые хребты (тот же характер, что
    /// GenerateRidgedMap, но в абсолютных координатах). Возврат [0..1].
    /// </summary>
    public static float RidgedOctave(ulong seed, uint ver, GenerationDomain domain,
        long x, long y, float scale, ulong lane = 0)
    {
        float n = GradientOctave(seed, ver, domain, x, y, scale, lane) * 0.707f + 0.5f; // ~[0..1]
        float r = 1f - MathF.Abs(n * 2f - 1f);
        return r * r;
    }

    /// <summary>
    /// fBm в абсолютных координатах, нормированный в [0..1] (сумма амплитуд = 1).
    /// octaves 1..6, lacunarity/gain клампятся как в NoiseGenerator.
    /// </summary>
    public static float Fbm(ulong seed, uint ver, GenerationDomain domain,
        long x, long y, float baseScale, int octaves, float lacunarity = 2.0f, float gain = 0.5f)
    {
        octaves = Math.Clamp(octaves, 1, 6);
        baseScale = Math.Max(4f, baseScale);
        lacunarity = Math.Clamp(lacunarity, 1.5f, 3.0f);
        gain = Math.Clamp(gain, 0.3f, 0.7f);

        // ampSum считается так же, как в GenerateFbmMap — та же нормировка.
        float ampSum = 0f;
        float amp = 1f;
        for (int o = 0; o < octaves; o++)
        {
            ampSum += amp;
            amp *= gain;
        }

        double acc = 0.0;
        amp = 1f;
        float freq = 1f;
        for (int o = 0; o < octaves; o++)
        {
            float scale = baseScale / freq;
            // Тот же сдвиг сида октавы, что AccumulateOctave (o * 1013), но через
            // lane-параметр hash'а — без новых доменов.
            float v = GradientOctave(seed, ver, domain, x, y, scale, (ulong)o * 1013ul);
            acc += ((double)v * 0.707d + 0.5d) * ((double)amp / ampSum);
            amp *= gain;
            freq *= lacunarity;
        }
        // Нормировка: каждая октава давала [0..1]-подобное значение; делим на сумму весов.
        double norm = 0.0;
        amp = 1f;
        for (int o = 0; o < octaves; o++) { norm += (double)amp; amp *= gain; }
        return (float)Math.Clamp(acc / norm, 0.0, 1.0);
    }

    private static float DotL(ulong seed, uint ver, GenerationDomain domain, ulong lane,
        long gx, long gy, float fx, float fy)
    {
        int idx = GradientIndex(seed, ver, domain, gx, gy, lane);
        float dx = fx * GradX[idx];
        float dy = fy * GradY[idx];
        return dx + dy;
    }

    /// <summary>
    /// Ridged fBm в абсолютных координатах, [0..1], 1 = гребень хребта.
    /// </summary>
    public static float RidgedFbm(ulong seed, uint ver, GenerationDomain domain,
        long x, long y, float baseScale, int octaves)
    {
        octaves = Math.Clamp(octaves, 1, 6);
        baseScale = Math.Max(4f, baseScale);

        float ampSum = 0f;
        float amp = 1f;
        for (int o = 0; o < octaves; o++) { ampSum += amp; amp *= 0.5f; }

        double acc = 0.0;
        amp = 1f;
        float freq = 1f;
        for (int o = 0; o < octaves; o++)
        {
            float scale = baseScale / freq;
            float v = RidgedOctave(seed, ver, domain, x, y, scale, (ulong)o * 1013ul + 55555ul);
            acc += (double)v * ((double)amp / ampSum);
            amp *= 0.5f;
            freq *= 2.1f;
        }
        return (float)Math.Clamp(acc, 0.0, 1.0);
    }

    /// <summary>Q16 из float [0..1] с клампом.</summary>
    public static ushort ToQ16(float v01)
        => (ushort)Math.Clamp((long)(v01 * 65535f + 0.5f), 0, 65535);
}
