using System;

namespace Game.Core;

/// <summary>
/// Генератор градиентного (Perlin) шума.
/// Чистый C#, без зависимостей от Godot API.
/// Потокобезопасен — не использует статическое состояние.
/// </summary>
public static class NoiseGenerator
{
    private static readonly Vector2[] Gradients =
    {
        new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
        new(0.707f, 0.707f), new(-0.707f, 0.707f),
        new(0.707f, -0.707f), new(-0.707f, -0.707f)
    };

    /// <summary>
    /// Генерирует карту высот (значения в диапазоне [0, 1]).
    /// </summary>
    public static float[,] GenerateHeightMap(int width, int height, uint seed, float scale)
    {
        return GenerateNoiseMap(width, height, seed, scale);
    }

    /// <summary>
    /// Генерирует карту лесистости (значения в диапазоне [0, 1]).
    /// Использует другой seed для разнообразия.
    /// </summary>
    public static float[,] GenerateForestMap(int width, int height, uint seed, float scale)
    {
        return GenerateNoiseMap(width, height, seed + 1000, scale);
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