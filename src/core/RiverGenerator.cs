using System;

namespace Game.Core;

/// <summary>
/// Генератор рек на карте.
/// Чистый C#, без зависимостей от Godot API.
/// Потокобезопасен — не использует статическое состояние.
/// </summary>
public static class RiverGenerator
{
    /// <summary>
    /// Генерирует реки на карте, превращая клетки суши в TileType.Water.
    /// </summary>
    /// <param name="ground">Массив типов поверхности (модифицируется in-place).</param>
    /// <param name="width">Ширина карты в тайлах.</param>
    /// <param name="height">Высота карты в тайлах.</param>
    /// <param name="seed">Seed для воспроизводимости.</param>
    /// <param name="riverCount">Количество рек (1 или 2).</param>
    public static void GenerateRivers(TileType[,] ground, int width, int height, uint seed, int riverCount = 2)
    {
        var rng = new Random((int)seed + 5000);

        for (int i = 0; i < riverCount; i++)
        {
            GenerateSingleRiver(ground, width, height, rng, i);
        }
    }

    private static void GenerateSingleRiver(TileType[,] ground, int width, int height, Random rng, int riverIndex)
    {
        // Выбираем начальную и конечную точки на противоположных границах
        Vector2Int start, end;

        if (riverIndex == 0)
        {
            // Река 1: сверху вниз
            start = new Vector2Int(rng.Next(1, width - 2), 0);
            end = new Vector2Int(rng.Next(1, width - 2), height - 1);
        }
        else
        {
            // Река 2: слева направо
            start = new Vector2Int(0, rng.Next(1, height - 2));
            end = new Vector2Int(width - 1, rng.Next(1, height - 2));
        }

        // Строим ломаную линию
        var points = BuildPolyline(start, end, width, height, rng);

        // Рисуем реку по точкам ломаной
        int radius = 2; // ширина русла: квадрат (radius*2+1) x (radius*2+1)

        for (int p = 0; p < points.Count - 1; p++)
        {
            DrawLineSegment(ground, width, height, points[p], points[p + 1], radius);
        }
    }

    /// <summary>
    /// Строит ломаную линию от start до end с извилистостью.
    /// </summary>
    private static System.Collections.Generic.List<Vector2Int> BuildPolyline(
        Vector2Int start, Vector2Int end, int width, int height, Random rng)
    {
        var points = new System.Collections.Generic.List<Vector2Int>();
        points.Add(start);

        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float totalDist = MathF.Sqrt(dx * dx + dy * dy);

        // Длина сегмента: 10–20 тайлов
        float segmentLength = 10f + (float)rng.NextDouble() * 10f;
        int segments = Math.Max(1, (int)(totalDist / segmentLength));

        // Перпендикулярное направление
        float perpX = -dy / totalDist;
        float perpY = dx / totalDist;

        for (int i = 1; i < segments; i++)
        {
            float t = i / (float)segments;

            // Прямая позиция (линейная интерполяция)
            float px = start.X + dx * t;
            float py = start.Y + dy * t;

            // Случайное отклонение перпендикулярно направлению
            float deviation = (float)(rng.NextDouble() * 20f - 10f); // ±10 тайлов
            px += perpX * deviation;
            py += perpY * deviation;

            // Кламп к границам карты (с отступом 1 тайл)
            px = Math.Clamp(px, 1f, width - 2f);
            py = Math.Clamp(py, 1f, height - 2f);

            points.Add(new Vector2Int((int)MathF.Round(px), (int)MathF.Round(py)));
        }

        points.Add(end);
        return points;
    }

    /// <summary>
    /// Рисует отрезок линии по алгоритму Брезенхэма и заливает квадратом radius вокруг каждой точки.
    /// </summary>
    private static void DrawLineSegment(
        TileType[,] ground, int width, int height,
        Vector2Int from, Vector2Int to, int radius)
    {
        int x = from.X;
        int y = from.Y;
        int dx = Math.Abs(to.X - from.X);
        int dy = -Math.Abs(to.Y - from.Y);
        int sx = from.X < to.X ? 1 : -1;
        int sy = from.Y < to.Y ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            FillSquare(ground, width, height, x, y, radius);

            if (x == to.X && y == to.Y)
                break;

            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y += sy;
            }
        }
    }

    /// <summary>
    /// Заливает квадрат (radius*2+1) x (radius*2+1) вокруг центральной точки водой.
    /// </summary>
    private static void FillSquare(
        TileType[,] ground, int width, int height,
        int cx, int cy, int radius)
    {
        int xStart = Math.Max(0, cx - radius);
        int xEnd = Math.Min(width - 1, cx + radius);
        int yStart = Math.Max(0, cy - radius);
        int yEnd = Math.Min(height - 1, cy + radius);

        for (int x = xStart; x <= xEnd; x++)
        {
            for (int y = yStart; y <= yEnd; y++)
            {
                ground[x, y] = TileType.Water;
            }
        }
    }

    /// <summary>
    /// Простая 2D-структура для целочисленных координат, чтобы не зависеть от Godot API.
    /// </summary>
    private readonly struct Vector2Int
    {
        public readonly int X;
        public readonly int Y;

        public Vector2Int(int x, int y)
        {
            X = x;
            Y = y;
        }
    }
}