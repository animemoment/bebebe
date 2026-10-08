using System;

namespace Game.UI.Tools;

/// <summary>
/// Общий прогон выделения инструмента по БЕСКОНЕЧНОМУ МИРУ (PLAN §28): клетки вне острова
/// отдаются в <see cref="IToolWorldPlacement"/>, островные пропускаются — их обрабатывает
/// обычная (островная) логика инструмента со своими клампами.
/// Лимиты защищают от гигантского драга: применяем не больше <see cref="MaxAppliedCells"/>,
/// просматриваем не больше <see cref="MaxScannedCells"/> клеток за одно выделение.
/// </summary>
public static class WorldPlacementPass
{
    public const int MaxAppliedCells = 4096;
    public const int MaxScannedCells = 250_000;

    /// <summary>Последнее выделение было обрезано лимитом просмотра (для HUD/диагностики).</summary>
    public static bool LastTruncated { get; private set; }

    /// <summary>
    /// Применить выделение (start → end включительно) к миру. Возвращает число успешных
    /// применений. Островные клетки (и всё, что не обслуживается миром) пропускаются.
    /// </summary>
    public static int Run(
        long startX,
        long startY,
        long endX,
        long endY,
        IToolWorldPlacement placement,
        Func<long, long, bool> apply)
    {
        if (placement == null || apply == null)
            return 0;

        long minX = Math.Min(startX, endX);
        long maxX = Math.Max(startX, endX);
        long minY = Math.Min(startY, endY);
        long maxY = Math.Max(startY, endY);

        // Гигантский драг обрезается ЦЕНТРИРОВАННО: раньше прогон съедал лимит просмотра на
        // верхние строки и низ выделения молча не обрабатывался (а SelectionBox показывал всё).
        long width = maxX - minX + 1;
        long height = maxY - minY + 1;
        LastTruncated = false;
        if (width * height > MaxScannedCells)
        {
            LastTruncated = true;
            long side = (long)Math.Sqrt(MaxScannedCells);
            long centerX = minX + width / 2;
            long centerY = minY + height / 2;
            long newMinX = Math.Max(minX, centerX - side / 2);
            long newMinY = Math.Max(minY, centerY - side / 2);
            maxX = Math.Min(maxX, newMinX + side - 1);
            maxY = Math.Min(maxY, newMinY + side - 1);
            minX = newMinX;
            minY = newMinY;
        }

        int applied = 0;
        long scanned = 0;
        for (long y = minY; y <= maxY; y++)
        {
            for (long x = minX; x <= maxX; x++)
            {
                if (++scanned > MaxScannedCells)
                {
                    LastTruncated = true;
                    return applied;
                }
                if (IsIslandCell(x, y))
                    continue;
                if (applied >= MaxAppliedCells)
                    return applied;
                if (!placement.IsWorldCell(x, y))
                    continue;
                if (apply(x, y))
                    applied++;
            }
        }
        return applied;
    }

    /// <summary>Клетка острова: [0, MapWidth)×[0, MapHeight) — там работает обычная логика.</summary>
    public static bool IsIslandCell(long cellX, long cellY)
        => cellX >= 0 && cellY >= 0 && cellX < MapRenderer.MapWidth && cellY < MapRenderer.MapHeight;
}
