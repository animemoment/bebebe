using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Адаптер обратной совместимости. Вся логика пайплайна теперь в ConstructionManager.
/// </summary>
[Obsolete("Use ConstructionManager.Instance directly.")]
public sealed class ConstructionPipeline
{
    public static ConstructionPipeline Instance { get; } = new();

    public int StartSite(List<(int X, int Y)> wallCells, BuildingType wallType, MapData mapData, List<(int X, int Y)> zoneCells = null)
    {
        var def = StructureRegistry.Get(wallType);
        return ConstructionManager.Instance.StartSitesBatch(wallCells, def, mapData, zoneCells);
    }

    public void NotifyTreeCleared(int x, int y)
    {
        ConstructionManager.Instance.NotifyTreeCleared(x, y);
    }

    public void NotifyStoneCleared(int x, int y)
    {
        ConstructionManager.Instance.NotifyStoneCleared(x, y);
    }

    public void NotifyHaulCleared(int x, int y)
    {
        ConstructionManager.Instance.NotifyHaulCleared(x, y);
    }

    public void NotifySupplyDone(int x, int y)
    {
        ConstructionManager.Instance.NotifySupplyDone(x, y);
    }

    public void NotifyWallBuilt(int x, int y)
    {
        ConstructionManager.Instance.NotifyWallBuilt(x, y);
    }

    public void RemoveCell(int siteId, int x, int y)
    {
        ConstructionManager.Instance.CancelSite(x, y);
    }

    public void CancelSite(int siteId)
    {
        // ConstructionManager управляет отменой
    }

    public bool IsStageAllowed(int x, int y, JobTypeId typeId)
    {
        return ConstructionManager.Instance.IsStageAllowed(x, y, typeId);
    }

    public void ReconcileTick(SimulationContext ctx)
    {
        ConstructionManager.Instance.ReconcileTick(ctx);
    }
}
