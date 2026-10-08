using System;
using System.Collections.Generic;

namespace Game.Core.WorldStreaming;

public readonly record struct WorldFeatureId(ulong Value);

public enum WorldFeatureKind : byte
{
    River = 1,
    Lake = 2
}

/// <summary>Полилиния реки в глобальных координатах; толщина задаётся радиусом в клетках.</summary>
public readonly record struct RiverFeature(
    WorldFeatureId Id,
    WorldCell P0,
    WorldCell P1,
    WorldCell P2,
    WorldCell P3,
    WorldCell P4,
    int HalfWidth,
    WorldRect Bounds)
{
    public WorldFeatureKind Kind => WorldFeatureKind.River;

    public bool ContainsCell(long x, long y)
    {
        if (!Bounds.Contains(x, y))
            return false;

        long radiusSquared = (long)HalfWidth * HalfWidth;
        return NearSegment(P0, P1, x, y, radiusSquared)
            || NearSegment(P1, P2, x, y, radiusSquared)
            || NearSegment(P2, P3, x, y, radiusSquared)
            || NearSegment(P3, P4, x, y, radiusSquared);
    }

    private static bool NearSegment(WorldCell a, WorldCell b, long px, long py, long radiusSquared)
    {
        long vx = b.X - a.X;
        long vy = b.Y - a.Y;
        long wx = px - a.X;
        long wy = py - a.Y;
        long lengthSquared = vx * vx + vy * vy;
        if (lengthSquared == 0)
            return wx * wx + wy * wy <= radiusSquared;

        long dot = wx * vx + wy * vy;
        if (dot <= 0)
            return wx * wx + wy * wy <= radiusSquared;
        if (dot >= lengthSquared)
        {
            long dx = px - b.X;
            long dy = py - b.Y;
            return dx * dx + dy * dy <= radiusSquared;
        }

        long cross = wx * vy - wy * vx;
        return cross * cross <= radiusSquared * lengthSquared;
    }
}

/// <summary>Круглое озеро с целочисленным радиусом, центр и ID глобальны.</summary>
public readonly record struct LakeFeature(
    WorldFeatureId Id,
    WorldCell Center,
    int Radius,
    WorldRect Bounds)
{
    public WorldFeatureKind Kind => WorldFeatureKind.Lake;

    public bool ContainsCell(long x, long y)
    {
        if (!Bounds.Contains(x, y))
            return false;

        long dx = x - Center.X;
        long dy = y - Center.Y;
        return dx * dx + dy * dy <= (long)Radius * Radius;
    }
}

/// <summary>Детерминированный набор пересекающих bounds features для одного запроса.</summary>
public sealed class WorldChunkFeatures
{
    private readonly RiverFeature[] _rivers;
    private readonly LakeFeature[] _lakes;

    public IReadOnlyList<RiverFeature> Rivers => _rivers;
    public IReadOnlyList<LakeFeature> Lakes => _lakes;

    internal WorldChunkFeatures(List<RiverFeature> rivers, List<LakeFeature> lakes)
    {
        rivers.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        lakes.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        _rivers = rivers.ToArray();
        _lakes = lakes.ToArray();
    }

    public bool IsWaterAt(long worldX, long worldY)
    {
        foreach (RiverFeature river in _rivers)
            if (river.Bounds.Contains(worldX, worldY) && river.ContainsCell(worldX, worldY))
                return true;
        foreach (LakeFeature lake in _lakes)
            if (lake.Bounds.Contains(worldX, worldY) && lake.ContainsCell(worldX, worldY))
                return true;
        return false;
    }
}
