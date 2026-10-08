using System;
using System.Collections.Generic;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Геометрические v1 river/lake features. Якоря сетки мира имеют сторону 512;
/// геометрия ограничена своим anchor cell и никогда не обрезается границей 64-tile chunk.
/// </summary>
public static class WorldFeatureGenerator
{
    public const uint FeatureSchemaVersion = 1;
    public const int FeatureCellSize = 512;

    /// <summary>
    /// Возвращает features с геометрическим footprint, пересекающим bounds чанка.
    /// Одинаковые ID/геометрия выдаются при любом порядке запросов; каждый набор
    /// упорядочен по ID. Реки v1 — самостоятельные цельные полилинии без drainage graph.
    /// </summary>
    public static WorldChunkFeatures GetFeaturesForChunk(ulong worldSeed, uint generatorVersion, ChunkKey key)
    {
        WorldRect bounds = WorldCoordinates.ChunkBounds(key);
        long minAnchorX = WorldCoordinates.FloorDiv(bounds.MinX, FeatureCellSize);
        long maxAnchorX = WorldCoordinates.FloorDiv(bounds.MaxX - 1, FeatureCellSize);
        long minAnchorY = WorldCoordinates.FloorDiv(bounds.MinY, FeatureCellSize);
        long maxAnchorY = WorldCoordinates.FloorDiv(bounds.MaxY - 1, FeatureCellSize);

        var rivers = new List<RiverFeature>();
        var lakes = new List<LakeFeature>();
        for (long anchorY = minAnchorY; anchorY <= maxAnchorY; anchorY++)
        {
            for (long anchorX = minAnchorX; anchorX <= maxAnchorX; anchorX++)
            {
                AddFeaturesForAnchor(worldSeed, generatorVersion, anchorX, anchorY,
                    bounds, rivers, lakes);
            }
        }

        return new WorldChunkFeatures(rivers, lakes);
    }

    internal static bool IsWaterAtCell(ulong worldSeed, uint generatorVersion, long worldX, long worldY)
    {
        long anchorX = WorldCoordinates.FloorDiv(worldX, FeatureCellSize);
        long anchorY = WorldCoordinates.FloorDiv(worldY, FeatureCellSize);
        ulong riverPresence = CoordinateHash.Hash(worldSeed, generatorVersion,
            GenerationDomain.RiverPresence, anchorX, anchorY, FeatureSchemaVersion);
        if (riverPresence % 4ul == 0)
        {
            long originX = checked(anchorX * FeatureCellSize);
            long originY = checked(anchorY * FeatureCellSize);
            WorldFeatureId id = MakeId(worldSeed, generatorVersion,
                GenerationDomain.RiverGeometry, anchorX, anchorY, 0);
            RiverFeature river = CreateRiver(worldSeed, generatorVersion,
                anchorX, anchorY, originX, originY, id);
            if (river.Bounds.Contains(worldX, worldY) && river.ContainsCell(worldX, worldY))
                return true;
        }

        ulong lakePresence = CoordinateHash.Hash(worldSeed, generatorVersion,
            GenerationDomain.LakePresence, anchorX, anchorY, FeatureSchemaVersion);
        if (lakePresence % 7ul == 0)
        {
            long originX = checked(anchorX * FeatureCellSize);
            long originY = checked(anchorY * FeatureCellSize);
            WorldFeatureId id = MakeId(worldSeed, generatorVersion,
                GenerationDomain.LakeGeometry, anchorX, anchorY, 1);
            LakeFeature lake = CreateLake(worldSeed, generatorVersion,
                anchorX, anchorY, originX, originY, id);
            if (lake.Bounds.Contains(worldX, worldY) && lake.ContainsCell(worldX, worldY))
                return true;
        }
        return false;
    }

    private static void AddFeaturesForAnchor(ulong worldSeed, uint generatorVersion,
        long anchorX, long anchorY, WorldRect queryBounds,
        List<RiverFeature> rivers, List<LakeFeature> lakes)
    {
        long originX = checked(anchorX * FeatureCellSize);
        long originY = checked(anchorY * FeatureCellSize);
        WorldFeatureId riverId = MakeId(worldSeed, generatorVersion,
            GenerationDomain.RiverGeometry, anchorX, anchorY, 0);
        WorldFeatureId lakeId = MakeId(worldSeed, generatorVersion,
            GenerationDomain.LakeGeometry, anchorX, anchorY, 1);

        // Presence и геометрия имеют разные домены: добавление lake-поля не меняет river.
        ulong riverPresence = CoordinateHash.Hash(worldSeed, generatorVersion,
            GenerationDomain.RiverPresence, anchorX, anchorY, FeatureSchemaVersion);
        if (riverPresence % 4ul == 0)
        {
            RiverFeature river = CreateRiver(worldSeed, generatorVersion,
                anchorX, anchorY, originX, originY, riverId);
            if (river.Bounds.Intersects(queryBounds))
                rivers.Add(river);
        }

        ulong lakePresence = CoordinateHash.Hash(worldSeed, generatorVersion,
            GenerationDomain.LakePresence, anchorX, anchorY, FeatureSchemaVersion);
        if (lakePresence % 7ul == 0)
        {
            LakeFeature lake = CreateLake(worldSeed, generatorVersion,
                anchorX, anchorY, originX, originY, lakeId);
            if (lake.Bounds.Intersects(queryBounds))
                lakes.Add(lake);
        }
    }

    private static RiverFeature CreateRiver(ulong worldSeed, uint generatorVersion,
        long anchorX, long anchorY, long originX, long originY, WorldFeatureId id)
    {
        // X monotonically traverses its macrocell. Y control points make a stable,
        // multi-segment meander; endpoints are interior to the macrocell, not chunk cuts.
        long x0 = checked(originX + 32);
        long x1 = checked(originX + 144);
        long x2 = checked(originX + 256);
        long x3 = checked(originX + 368);
        long x4 = checked(originX + 480);
        long y0 = RiverY(worldSeed, generatorVersion, anchorX, anchorY, 0, originY);
        long y1 = RiverY(worldSeed, generatorVersion, anchorX, anchorY, 1, originY);
        long y2 = RiverY(worldSeed, generatorVersion, anchorX, anchorY, 2, originY);
        long y3 = RiverY(worldSeed, generatorVersion, anchorX, anchorY, 3, originY);
        long y4 = RiverY(worldSeed, generatorVersion, anchorX, anchorY, 4, originY);
        var p0 = new WorldCell(x0, y0);
        var p1 = new WorldCell(x1, y1);
        var p2 = new WorldCell(x2, y2);
        var p3 = new WorldCell(x3, y3);
        var p4 = new WorldCell(x4, y4);
        int width = 2 + (int)(CoordinateHash.Hash(worldSeed, generatorVersion,
            GenerationDomain.RiverGeometry, anchorX, anchorY, 6) % 4ul);
        WorldRect bounds = BoundsForPolyline(p0, p1, p2, p3, p4, width);
        return new RiverFeature(id, p0, p1, p2, p3, p4, width, bounds);
    }

    private static long RiverY(ulong worldSeed, uint generatorVersion,
        long anchorX, long anchorY, ulong point, long originY)
    {
        ulong hash = CoordinateHash.Hash(worldSeed, generatorVersion,
            GenerationDomain.RiverGeometry, anchorX, anchorY,
            16 + FeatureSchemaVersion * 8ul + point);
        long offset = 48 + (long)(hash % 416ul); // 48..463: keeps stroke in anchor cell.
        long y = checked(originY + offset);
        // Avoid endpoint coordinates exactly on a 64-tile chunk boundary.
        return WorldCoordinates.FloorMod(y, WorldCoordinates.ChunkSize) == 0
            ? checked(y + 1)
            : y;
    }

    private static LakeFeature CreateLake(ulong worldSeed, uint generatorVersion,
        long anchorX, long anchorY, long originX, long originY, WorldFeatureId id)
    {
        ulong centerHash = CoordinateHash.Hash(worldSeed, generatorVersion,
            GenerationDomain.LakeGeometry, anchorX, anchorY, FeatureSchemaVersion * 3ul);
        ulong radiusHash = CoordinateHash.Hash(worldSeed, generatorVersion,
            GenerationDomain.LakeGeometry, anchorX, anchorY, FeatureSchemaVersion * 3ul + 1);
        long centerX = checked(originX + 96 + (long)(centerHash % 320ul)); // 96..415
        long centerY = checked(originY + 96 + (long)((centerHash >> 32) % 320ul));
        int radius = 12 + (int)(radiusHash % 49ul); // 12..60
        var center = new WorldCell(centerX, centerY);
        var bounds = new WorldRect(
            checked(centerX - radius), checked(centerY - radius),
            checked(centerX + radius + 1L), checked(centerY + radius + 1L));
        return new LakeFeature(id, center, radius, bounds);
    }

    private static WorldRect BoundsForPolyline(WorldCell p0, WorldCell p1, WorldCell p2,
        WorldCell p3, WorldCell p4, int halfWidth)
    {
        long minX = Math.Min(Math.Min(Math.Min(Math.Min(p0.X, p1.X), p2.X), p3.X), p4.X);
        long minY = Math.Min(Math.Min(Math.Min(Math.Min(p0.Y, p1.Y), p2.Y), p3.Y), p4.Y);
        long maxX = Math.Max(Math.Max(Math.Max(Math.Max(p0.X, p1.X), p2.X), p3.X), p4.X);
        long maxY = Math.Max(Math.Max(Math.Max(Math.Max(p0.Y, p1.Y), p2.Y), p3.Y), p4.Y);
        return new WorldRect(
            checked(minX - halfWidth), checked(minY - halfWidth),
            checked(maxX + halfWidth + 1L), checked(maxY + halfWidth + 1L));
    }

    private static WorldFeatureId MakeId(ulong worldSeed, uint generatorVersion,
        GenerationDomain domain, long anchorX, long anchorY, ulong featureOrdinal)
    {
        ulong lane = ((ulong)FeatureSchemaVersion << 32) | featureOrdinal;
        return new WorldFeatureId(CoordinateHash.Hash(worldSeed, generatorVersion,
            domain, anchorX, anchorY, lane));
    }
}
