using System;
using System.Collections.Generic;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

public sealed class WorkZone
{
    public int Id { get; }
    public string Name { get; set; }
    public int CenterX { get; }
    public int CenterY { get; }
    public int RadiusTiles { get; internal set; }
    public int TilesTarget { get; internal set; }
    public int MaxWorkers { get; internal set; }
    public ulong JobMask { get; internal set; }
    public int? PriorityOverride { get; internal set; }
    public int AssignedCount;
    public HashSet<(int, int)> Tiles { get; internal set; }
    public List<int> ChunkIndices { get; internal set; }

    public WorkZone(int id, string name, int centerX, int centerY, int radiusTiles, int tilesTarget, int maxWorkers, ulong jobMask, HashSet<(int, int)> tiles, List<int> chunkIndices)
    {
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

    public bool Matches(JobTypeId t) => (JobMask & (1UL << (int)t)) != 0;

    public void RegisterAssigned() => Interlocked.Increment(ref AssignedCount);

    public void ResetAssigned() => Volatile.Write(ref AssignedCount, 0);
}
