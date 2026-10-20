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

    // Геометрия: _tiles — единственный источник, заменяется ссылкой под локом менеджера.
    private volatile HashSet<(int X, int Y)> _tiles;
    public HashSet<(int X, int Y)> Tiles
    {
        get => _tiles;
        internal set => _tiles = value;
    }

    // --- Farm-поля (актуальны при Kind == Farm) ---
    public ItemId RequiredSeedItem { get; set; } = ItemId.Grain;
    
    private int _autoPlantEnabled;
    public bool AutoPlantEnabled
    {
        get => System.Threading.Volatile.Read(ref _autoPlantEnabled) != 0;
        set => System.Threading.Volatile.Write(ref _autoPlantEnabled, value ? 1 : 0);
    }

    private int _plantedCount;
    public int PlantedCount
    {
        get => System.Threading.Volatile.Read(ref _plantedCount);
        set => System.Threading.Volatile.Write(ref _plantedCount, value);
    }

    // --- Work-поля (актуальны при Kind == Work) ---
    public int CenterX { get; }
    public int CenterY { get; }
    
    private int _radiusTiles;
    public int RadiusTiles
    {
        get => System.Threading.Volatile.Read(ref _radiusTiles);
        internal set => System.Threading.Volatile.Write(ref _radiusTiles, value);
    }

    private int _tilesTarget;
    public int TilesTarget
    {
        get => System.Threading.Volatile.Read(ref _tilesTarget);
        internal set => System.Threading.Volatile.Write(ref _tilesTarget, value);
    }

    private int _maxWorkers;
    public int MaxWorkers
    {
        get => System.Threading.Volatile.Read(ref _maxWorkers);
        internal set => System.Threading.Volatile.Write(ref _maxWorkers, value);
    }

    private long _jobMask;
    public ulong JobMask
    {
        get => (ulong)System.Threading.Volatile.Read(ref _jobMask);
        internal set => System.Threading.Volatile.Write(ref _jobMask, (long)value);
    }

    private int _priorityOverrideValue = -1;
    public int? PriorityOverride
    {
        get
        {
            int val = System.Threading.Volatile.Read(ref _priorityOverrideValue);
            return val >= 0 ? val : null;
        }
        internal set
        {
            System.Threading.Volatile.Write(ref _priorityOverrideValue, value.HasValue ? value.Value : -1);
        }
    }
    public int AssignedCount;
    public List<int> ChunkIndices { get; internal set; }

    public int TotalTiles => _tiles?.Count ?? 0;

    public bool ContainsTile(int x, int y)
    {
        var snap = _tiles;
        return snap != null && snap.Contains((x, y));
    }

    public HashSet<(int X, int Y)> GetTilesSnapshot()
    {
        var snap = _tiles;
        return snap != null ? new HashSet<(int X, int Y)>(snap) : new HashSet<(int X, int Y)>();
    }

    internal Zone(int id, string name, ZoneKind kind, HashSet<(int X, int Y)> tiles)
    {
        Id = id;
        Name = name;
        Kind = kind;
        _tiles = tiles;
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
        _tiles = tiles;
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
