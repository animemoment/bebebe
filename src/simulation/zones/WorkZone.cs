using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// DTO Work-зоны для UI (тонкий фасад WorkZoneManager отдаёт копии этого класса).
/// Экземпляры ОДНОРАЗОВЫЕ: создаются в AdaptWork() под локом ZoneManager из
/// согласованного снапшота, дальше читаются main-потоком. Живые зоны переведены
/// на immutable-swap (Zone.Shape), поэтому мутабельных коллекций здесь больше нет.
/// </summary>
public sealed class WorkZone
{
    public int Id { get; }
    // Единственное изменяемое извне поле — чистый UI-контент (переименование),
    // к симуляции не проткает.
    public string Name { get; set; }
    public int CenterX { get; }
    public int CenterY { get; }
    public int RadiusTiles { get; }
    public int TilesTarget { get; }
    public int MaxWorkers { get; }
    public ulong JobMask { get; }
    public int? PriorityOverride { get; internal set; }
    public int AssignedCount;
    public HashSet<(int, int)> Tiles { get; }
    public IReadOnlyList<int> ChunkIndices { get; }

    public WorkZone(int id, string name, int centerX, int centerY, int radiusTiles, int tilesTarget, int maxWorkers, ulong jobMask, HashSet<(int, int)> tiles, IReadOnlyList<int> chunkIndices)
    {
        // Конструктор проверяет аргументы СЕЙЧАС, а не роняет Tiles.Add позже и «не там».
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(chunkIndices);
        Id = id;
        Name = name;
        CenterX = centerX;
        CenterY = centerY;
        RadiusTiles = radiusTiles;
        TilesTarget = tilesTarget;
        MaxWorkers = maxWorkers;
        JobMask = jobMask;
        PriorityOverride = null;
        AssignedCount = 0;
        Tiles = tiles;
        ChunkIndices = chunkIndices;
    }

    public static int CalcRadius(int tilesTarget)
    {
        // Валидация: отрицательный target → Sqrt(отриц) → NaN → (int)NaN = 0 —
        // диск из «нулевого» радиуса даёт одну клетку и маскирует ошибку вызывающего кода.
        if (tilesTarget <= 0)
            throw new ArgumentOutOfRangeException(nameof(tilesTarget), tilesTarget, "tilesTarget должен быть > 0");
        return (int)Math.Round(Math.Sqrt(tilesTarget / Math.PI));
    }

    public static HashSet<(int, int)> BuildDisc(int cx, int cy, int r)
    {
        if (r < 0)
            throw new ArgumentOutOfRangeException(nameof(r), r, "Радиус диска не может быть отрицательным");
        var set = new HashSet<(int, int)>();
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (dx * dx + dy * dy <= r * r)
                    set.Add((cx + dx, cy + dy));
        return set;
    }

    /// <summary>
    /// Инвариант: JobTypeId строго &lt; 64. Без проверки 1UL &lt;&lt; t при t >= 64
    /// в C# маскирует счётчик сдвига (t &amp; 63) — матч стал бы молча неверным.
    /// </summary>
    public bool Matches(JobTypeId t)
    {
        Debug.Assert((int)t >= 0 && (int)t < 64);
        return (int)t >= 0 && (int)t < 64 && (JobMask & (1UL << (int)t)) != 0;
    }

    /// <summary>
    /// Протокол AssignedCount: RegisterAssigned — во время параллельной фазы
    /// (диспатч/воркеры); ResetAssigned — СТРОГО в фазе до параллельной работы,
    /// после join прошлой фазы. Иначе сброс съедает только что учтённое назначение.
    /// </summary>
    public void RegisterAssigned() => Interlocked.Increment(ref AssignedCount);

    /// <summary>См. комментарий к RegisterAssigned: сброс только вне параллельной фазы.</summary>
    public void ResetAssigned() => Volatile.Write(ref AssignedCount, 0);
}
