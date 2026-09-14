using System;

namespace Game.Core;

/// <summary>
/// Генератор градиентного (Perlin) шума.
/// Чистый C#, без зависимостей от Godot API.
/// Потокобезопасен — не использует статическое состояние.
/// </summary>
public static class NoiseGenerator
{
    // Мост fBm GPU (пункт 4): MapGenerator — чистый C# без Godot, поэтому прямая
    // зависимость Core → Simulation/GPU запрещена. Вместо неё — делегат без зависимостей,
    // который ставит GpuMapBridge.Enable() только на время генерации карты.
    // Сигнатура: (width, height, seed, baseScale, octaves, lacunarity, gain) → float[,].
    public static Func<int, int, uint, float, int, float, float, float[,]> FbmOverride = null;

    // Защита от бесконечной рекурсии: GenerateFbmMapGpu при недоступном GPU
    // сам падает назад на NoiseGenerator.GenerateFbmMap — повторный вход в мост
    // запрещён, внутренний вызов идёт сразу по CPU-пути. ThreadStatic, т.к.
    // генерация идёт в одном фоновом Task (MapRenderer), сим мост не видит.
    [ThreadStatic]
    private static bool _inFbmOverride;

    private static readonly Vector2[] Gradients =
    {
        new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
        new(0.707f, 0.707f), new(-0.707f, 0.707f),
        new(0.707f, -0.707f), new(-0.707f, -0.707f)
    };

    /// <summary>
    /// Генерирует карту высот (значения в диапазоне [0, 1]).
    /// fBm из нескольких октав: крупные материки + мелкие детали.
    /// </summary>
    public static float[,] GenerateHeightMap(int width, int height, uint seed, float scale)
    {
        return GenerateFbmMap(width, height, seed, scale, 4);
    }

    /// <summary>
    /// Генерирует карту лесистости (значения в диапазоне [0, 1]).
    /// Использует другой seed для разнообразия.
    /// </summary>
    public static float[,] GenerateForestMap(int width, int height, uint seed, float scale)
    {
        return GenerateFbmMap(width, height, seed + 1000, scale, 4);
    }

    /// <summary>
    /// Фрактальный шум (fBm): сумма октав с удвоением частоты и затуханием амплитуды.
    /// Даёт естественный рельеф вместо монотонных пятен одной октавы.
    /// </summary>
    public static float[,] GenerateFbmMap(int width, int height, uint seed, float baseScale, int octaves = 4, float lacunarity = 2.0f, float gain = 0.5f)
    {
        // Мост вызывается ПЕРВОЙ строкой, до клампов: клампы делают обе стороны сами
        // (GPU — в GenerateFbmMapGpu, CPU — ниже при fallback). Исключение моста =
        // откат на CPU + снятие override, генерацию не роняем.
        var ov = FbmOverride;
        if (ov != null && !_inFbmOverride)
        {
            try
            {
                _inFbmOverride = true;
                try { return ov(width, height, seed, baseScale, octaves, lacunarity, gain); }
                // Мост сам не кидает (у GenerateFbmMapGpu внутренний CPU-fallback),
                // но внешний чужой override может: снимаем его и идём по CPU-пути.
                catch { FbmOverride = null; }
                finally { _inFbmOverride = false; }
            }
            catch { FbmOverride = null; }
        }

        var map = new float[width, height];

        octaves = Math.Max(1, Math.Min(6, octaves));
        baseScale = Math.Max(4f, baseScale);
        lacunarity = Math.Clamp(lacunarity, 1.5f, 3.0f);
        gain = Math.Clamp(gain, 0.3f, 0.7f);

        // Нормировка по сумме амплитуд, чтобы результат остался в [0..1].
        float ampSum = 0f;
        float amp = 1f;
        for (int o = 0; o < octaves; o++)
        {
            ampSum += amp;
            amp *= gain;
        }

        amp = 1f;
        float freq = 1f;

        // Аккумулируем октавы: каждая — своя решётка градиентов (свой seed).
        for (int o = 0; o < octaves; o++)
        {
            float scale = baseScale / freq;
            AccumulateOctave(map, width, height, seed + (uint)(o * 1013), scale, amp / ampSum);
            amp *= gain;
            freq *= lacunarity;
        }

        // fBm-сумма уже в [0..1] благодаря нормировке амплитуд.
        return map;
    }

    /// <summary>
    /// Добавляет одну октаву Perlin-шума в аккумулятор map с весом weight.
    /// </summary>
    private static void AccumulateOctave(float[,] map, int width, int height, uint seed, float scale, float weight)
    {
        scale = Math.Max(2f, scale);
        var rng = new Random(unchecked((int)seed));

        int gridW = (int)MathF.Ceiling(width / scale) + 2;
        int gridH = (int)MathF.Ceiling(height / scale) + 2;
        var gradientIndices = new int[gridW, gridH];

        for (int gx = 0; gx < gridW; gx++)
        {
            for (int gy = 0; gy < gridH; gy++)
            {
                gradientIndices[gx, gy] = rng.Next(Gradients.Length);
            }
        }

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                float sx = x / scale;
                float sy = y / scale;

                int x0 = (int)MathF.Floor(sx);
                int y0 = (int)MathF.Floor(sy);
                int x1 = Math.Min(x0 + 1, gridW - 1);
                int y1 = Math.Min(y0 + 1, gridH - 1);
                x0 = Math.Clamp(x0, 0, gridW - 1);
                y0 = Math.Clamp(y0, 0, gridH - 1);

                float tx = sx - MathF.Floor(sx);
                float ty = sy - MathF.Floor(sy);

                float v00 = Dot(gradientIndices[x0, y0], tx, ty);
                float v10 = Dot(gradientIndices[x1, y0], tx - 1f, ty);
                float v01 = Dot(gradientIndices[x0, y1], tx, ty - 1f);
                float v11 = Dot(gradientIndices[x1, y1], tx - 1f, ty - 1f);

                float stx = Smoothstep(tx);
                float sty = Smoothstep(ty);

                float v0 = Lerp(v00, v10, stx);
                float v1 = Lerp(v01, v11, stx);
                float value = Lerp(v0, v1, sty);

                map[x, y] += (value * 0.707f + 0.5f) * weight;
            }
        }
    }

    private static float[,] GenerateNoiseMap(int width, int height, uint seed, float scale)
    {
        var map = new float[width, height];
        var rng = new Random((int)seed);

        // Генерируем случайные градиентные индексы для узлов сетки
        int gridW = (int)MathF.Ceiling(width / scale) + 2;
        int gridH = (int)MathF.Ceiling(height / scale) + 2;
        var gradientIndices = new int[gridW, gridH];

        for (int gx = 0; gx < gridW; gx++)
        {
            for (int gy = 0; gy < gridH; gy++)
            {
                gradientIndices[gx, gy] = rng.Next(Gradients.Length);
            }
        }

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                float sx = x / scale;
                float sy = y / scale;

                int x0 = (int)MathF.Floor(sx);
                int y0 = (int)MathF.Floor(sy);
                int x1 = x0 + 1;
                int y1 = y0 + 1;

                float tx = sx - x0;
                float ty = sy - y0;

                // Вектор от угла до точки
                float dx0 = tx;
                float dy0 = ty;
                float dx1 = tx - 1f;
                float dy1 = ty - 1f;

                // Скалярные произведения с градиентами
                float v00 = Dot(gradientIndices[x0, y0], dx0, dy0);
                float v10 = Dot(gradientIndices[x1, y0], dx1, dy0);
                float v01 = Dot(gradientIndices[x0, y1], dx0, dy1);
                float v11 = Dot(gradientIndices[x1, y1], dx1, dy1);

                // Smoothstep-интерполяция
                float stx = Smoothstep(tx);
                float sty = Smoothstep(ty);

                float v0 = Lerp(v00, v10, stx);
                float v1 = Lerp(v01, v11, stx);
                float value = Lerp(v0, v1, sty);

                // Нормализация из [-0.707..0.707] в [0..1]
                map[x, y] = value * 0.707f + 0.5f;
            }
        }

        return map;
    }

    private static float Dot(int gradientIndex, float dx, float dy)
    {
        Vector2 g = Gradients[gradientIndex];
        return g.X * dx + g.Y * dy;
    }

    private static float Smoothstep(float t)
    {
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    /// <summary>
    /// Простая 2D-структура для векторов, чтобы не зависеть от Godot API.
    /// </summary>
    private readonly struct Vector2
    {
        public readonly float X;
        public readonly float Y;

        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }
    }
}