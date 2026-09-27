using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Тип зоны в унифицированном ZoneManager.
/// Farm — грядка (фермерство, AutoPlant); Work — универсальная рабочая зона-бригадир.
/// </summary>
public enum ZoneKind : byte
{
    Farm = 0,
    Work = 1
}

/// <summary>
/// Единая зона: одно множество тайлов, один владелец.
/// Farm-специфика (AutoPlant/семена) и Work-специфика (маска/лимиты/диспатч)
/// живут в одной сущности, различаются по Kind.
/// </summary>
public sealed class Zone
{
    public int Id { get; }
    public string Name { get; set; }
    public ZoneKind Kind { get; }

    // Геометрия:_tiles — единственный источник, заменяется ссылкой под локом менеджера.
    public HashSet<(int X, int Y)> Tiles { get; internal set; }

    // --- Farm-поля (актуальны при Kind == Farm) ---
    public ItemId RequiredSeedItem { get; set; } = ItemId.Grain;
    public bool AutoPlantEnabled { get; set; } = false;
    public int PlantedCount { get; set; }

    // --- Work-поля (актуальны при Kind == Work) ---
    public int CenterX { get; }
    public int CenterY { get; }
    public int RadiusTiles { get; internal set; }
    public int TilesTarget { get; internal set; }
    public int MaxWorkers { get; internal set; }
    public ulong JobMask { get; internal set; }
    public int? PriorityOverride { get; internal set; }
    public int AssignedCount;
    public List<int> ChunkIndices { get; internal set; }

    public int TotalTiles => Tiles.Count;

    internal Zone(int id, string name, ZoneKind kind, HashSet<(int X, int Y)> tiles)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Tiles = tiles;
    }

    internal Zone(int id, string name, int centerX, int centerY, int radiusTiles,
        int tilesTarget, int maxWorkers, ulong jobMask,
        HashSet<(int, int)> tiles, List<int> chunkIndices)
        : this(id, name, ZoneKind.Work, new HashSet<(int X, int Y)>())
    {
        CenterX = centerX;
        CenterY = centerY;
        RadiusTiles = radiusTiles;
        TilesTarget = tilesTarget;
        MaxWorkers = maxWorkers;
        JobMask = jobMask;
        PriorityOverride = null;
        AssignedCount = 0;
        // (int,int) и (int X, int Y) — один и тот же ValueTuple, прямое присваивание.
        Tiles = tiles;
        ChunkIndices = chunkIndices;
    }

    public bool Matches(JobTypeId t) => (JobMask & (1UL << (int)t)) != 0;

    public void RegisterAssigned() => System.Threading.Interlocked.Increment(ref AssignedCount);

    public void ResetAssigned() => System.Threading.Volatile.Write(ref AssignedCount, 0);

    public static int CalcRadius(int tilesTarget) => (int)Math.Round(Math.Sqrt(tilesTarget / Math.PI));

    public static HashSet<(int, int)> BuildDisc(int cx, int cy, int r)
    {
        var set = new HashSet<(int, int)>();
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (dx * dx + dy * dy <= r * r)
                    set.Add((cx + dx, cy + dy));
        return set;
    }
}
