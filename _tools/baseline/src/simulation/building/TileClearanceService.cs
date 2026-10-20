using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

[Flags]
public enum ObstacleMask : ushort
{
    None              = 0,
    Tree              = 1 << 0,  // Дерево на траве
    Stone             = 1 << 1,  // Камень на траве
    LooseItems        = 1 << 2,  // Предметы на земле
    Debris            = 1 << 3,  // Мусор / сорняки / остатки
    HandmadeStructure = 1 << 4,  // Стена, постройка, станок
    ImpassableTerrain = 1 << 5,  // Вода, гора, лава
    GardenBed         = 1 << 6,  // Существующая грядка
    BlueprintOrSite   = 1 << 7,  // Активный чертеж / стройплощадка
}

public enum ClearancePolicy : byte
{
    FarmPlot,           // Требует чистую землю: устранить Tree, Stone, LooseItems, Debris. Запрещены HandmadeStructure, ImpassableTerrain, GardenBed, BlueprintOrSite.
    ConstructionSite,   // Требует чистую площадку под стену/постройку: устранить Tree, Stone, LooseItems, Debris. Запрещены ImpassableTerrain, GardenBed.
    WarehouseZone,      // Пол склада: устранить Tree, Stone, Debris. Запрещены ImpassableTerrain.
}

/// <summary>
/// Единый авторитет проверки препятствий и чистоты клеток.
/// Устраняет разрозненный хардкод по всему проекту.
/// </summary>
public static class TileClearanceService
{
    public static ObstacleMask GetObstacles(int x, int y, SimulationContext ctx)
    {
        if (ctx == null) return ObstacleMask.None;
        if ((uint)x >= (uint)ctx.MapWidth || (uint)y >= (uint)ctx.MapHeight)
            return ObstacleMask.ImpassableTerrain;

        ObstacleMask mask = ObstacleMask.None;

        if (ctx.Ground != null && ctx.Ground[x, y] != TileType.Grass)
            mask |= ObstacleMask.ImpassableTerrain;

        if (ctx.TreeOnGrass != null && ctx.TreeOnGrass[x, y])
            mask |= ObstacleMask.Tree;

        if (ctx.StoneOnGrass != null && ctx.StoneOnGrass[x, y])
            mask |= ObstacleMask.Stone;

        if (ctx.SolidWalls != null && ctx.SolidWalls[x, y])
            mask |= ObstacleMask.HandmadeStructure;

        // Если клетка уже заблокирована террейном, деревьями или стенами —
        // быстрый выход без захвата блокировок 4 менеджеров (F-010).
        if (mask != ObstacleMask.None)
            return mask;

        if (GroundItemManager.Instance.HasItemsAt(x, y))
            mask |= ObstacleMask.LooseItems;

        if (FarmJobManager.Instance.IsGardenBed(x, y))
            mask |= ObstacleMask.GardenBed;

        if (ConstructionManager.Instance.IsSiteAt(x, y) || BlueprintManager.Instance.IsBlueprintAt(x, y))
            mask |= ObstacleMask.BlueprintOrSite;

        return mask;
    }

    public static void GetObstaclesBatch(IReadOnlyList<(int X, int Y)> tiles, ObstacleMask[] outMasks, SimulationContext ctx)
    {
        if (tiles == null || outMasks == null) return;
        int count = Math.Min(tiles.Count, outMasks.Length);

        for (int i = 0; i < count; i++)
        {
            var (x, y) = tiles[i];
            if (ctx == null || (uint)x >= (uint)ctx.MapWidth || (uint)y >= (uint)ctx.MapHeight)
            {
                outMasks[i] = ObstacleMask.ImpassableTerrain;
                continue;
            }

            ObstacleMask m = ObstacleMask.None;
            if (ctx.Ground != null && ctx.Ground[x, y] != TileType.Grass)
                m |= ObstacleMask.ImpassableTerrain;
            if (ctx.TreeOnGrass != null && ctx.TreeOnGrass[x, y])
                m |= ObstacleMask.Tree;
            if (ctx.StoneOnGrass != null && ctx.StoneOnGrass[x, y])
                m |= ObstacleMask.Stone;
            if (ctx.SolidWalls != null && ctx.SolidWalls[x, y])
                m |= ObstacleMask.HandmadeStructure;
            outMasks[i] = m;
        }

        for (int i = 0; i < count; i++)
        {
            if (outMasks[i] != ObstacleMask.None)
                continue;

            var (x, y) = tiles[i];
            if (GroundItemManager.Instance.HasItemsAt(x, y))
                outMasks[i] |= ObstacleMask.LooseItems;
            if (FarmJobManager.Instance.IsGardenBed(x, y))
                outMasks[i] |= ObstacleMask.GardenBed;
            if (ConstructionManager.Instance.IsSiteAt(x, y) || BlueprintManager.Instance.IsBlueprintAt(x, y))
                outMasks[i] |= ObstacleMask.BlueprintOrSite;
        }
    }

    public static bool HasBlockingObstacles(int x, int y, ClearancePolicy policy, SimulationContext ctx)
    {
        if (ctx == null) return false;
        if ((uint)x >= (uint)ctx.MapWidth || (uint)y >= (uint)ctx.MapHeight)
            return true;

        // F-010 fast-path: чтение плоских 2D-массивов симуляции без единой блокировки
        if (ctx.Ground != null && ctx.Ground[x, y] != TileType.Grass)
            return true;
        if (ctx.SolidWalls != null && ctx.SolidWalls[x, y])
            return true;
        if (ctx.TreeOnGrass != null && ctx.TreeOnGrass[x, y])
            return true;
        if (ctx.StoneOnGrass != null && ctx.StoneOnGrass[x, y])
            return true;

        var mask = GetObstacles(x, y, ctx);
        return IsBlocked(mask, policy);
    }

    public static ObstacleMask GetObstacles(int x, int y, MapData map)
    {
        if (map == null) return ObstacleMask.None;
        if ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height)
            return ObstacleMask.ImpassableTerrain;

        ObstacleMask mask = ObstacleMask.None;

        if (map.Ground != null && map.Ground[x, y] != TileType.Grass)
            mask |= ObstacleMask.ImpassableTerrain;

        if (map.TreeOnGrass != null && (uint)x < (uint)map.TreeOnGrass.GetLength(0) && (uint)y < (uint)map.TreeOnGrass.GetLength(1) && map.TreeOnGrass[x, y])
            mask |= ObstacleMask.Tree;

        if (map.HasStone(x, y))
            mask |= ObstacleMask.Stone;

        if (GroundItemManager.Instance.HasItemsAt(x, y))
            mask |= ObstacleMask.LooseItems;

        if (StructureManager.Instance.HasStructureAt(x, y) || BuildingManager.Instance.HasBuildingAt(x, y))
            mask |= ObstacleMask.HandmadeStructure;

        if (FarmJobManager.Instance.IsGardenBed(x, y))
            mask |= ObstacleMask.GardenBed;

        if (ConstructionManager.Instance.IsSiteAt(x, y) || BlueprintManager.Instance.IsBlueprintAt(x, y))
            mask |= ObstacleMask.BlueprintOrSite;

        return mask;
    }

    public static bool HasBlockingObstacles(int x, int y, ClearancePolicy policy, MapData map)
    {
        var mask = GetObstacles(x, y, map);
        return IsBlocked(mask, policy);
    }

    public static bool IsBlocked(ObstacleMask mask, ClearancePolicy policy)
    {
        switch (policy)
        {
            case ClearancePolicy.FarmPlot:
                // Для грядки мешает ВСЁ: деревья, камни, вещи на земле, мусор, стены, вода, чужая стройка
                return (mask & (ObstacleMask.Tree | ObstacleMask.Stone | ObstacleMask.LooseItems | ObstacleMask.Debris | ObstacleMask.HandmadeStructure | ObstacleMask.ImpassableTerrain | ObstacleMask.GardenBed | ObstacleMask.BlueprintOrSite)) != 0;

            case ClearancePolicy.ConstructionSite:
                // Для стройки стены/здания мешают: деревья, камни, вещи на земле, мусор, непроходимый рельеф, готовые грядки
                return (mask & (ObstacleMask.Tree | ObstacleMask.Stone | ObstacleMask.LooseItems | ObstacleMask.Debris | ObstacleMask.ImpassableTerrain | ObstacleMask.GardenBed)) != 0;

            case ClearancePolicy.WarehouseZone:
                // Для склада мешают деревья, камни, мусор, непроходимый рельеф
                return (mask & (ObstacleMask.Tree | ObstacleMask.Stone | ObstacleMask.Debris | ObstacleMask.ImpassableTerrain)) != 0;

            default:
                return mask != ObstacleMask.None;
        }
    }
}
