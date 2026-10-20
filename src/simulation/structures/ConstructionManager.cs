using System;
using System.Collections.Generic;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Единый менеджер строительных площадок и чертежей.
/// Полностью заменяет старые BlueprintManager, ConstructionPipeline и WorkProgressTracker.
/// Управляет жизненным циклом от расчистки до доставки и сборки.
/// </summary>
public sealed class ConstructionManager
{
    public static ConstructionManager Instance { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), ConstructionSite> _sites = new(2048);
    private int _nextGroupId = 1;
    private readonly Dictionary<int, HashSet<(int X, int Y)>> _groupToCells = new();
    private readonly Dictionary<(int X, int Y), int> _cellToGroup = new();
    private readonly Dictionary<int, List<(int X, int Y)>> _groupToPendingWarehouse = new();

    public event Action<ConstructionSite> OnSiteAdded;
    public event Action<List<ConstructionSite>> OnSitesBatchAdded;
    public event Action<(int X, int Y)> OnSiteRemoved;
    public event Action<List<(int X, int Y)>> OnSitesBatchRemoved;
    public event Action<ConstructionSite> OnSiteCompleted;
    public event Action<(int X, int Y), int> OnProgressStageChanged;

    private long _lastReconcileTicks;
    private static readonly long ReconcileIntervalTicks = TimeSpan.FromSeconds(5).Ticks;

    public bool IsSiteAt(int x, int y)
    {
        lock (_lock) return _sites.ContainsKey((x, y));
    }

    public bool TryGetSite(int x, int y, out ConstructionSite site)
    {
        lock (_lock) return _sites.TryGetValue((x, y), out site);
    }

    public Dictionary<(int X, int Y), ConstructionSite> GetAllSites()
    {
        lock (_lock) return new Dictionary<(int X, int Y), ConstructionSite>(_sites);
    }

    public HashSet<(int X, int Y)> GetWallSites()
    {
        lock (_lock)
        {
            var walls = new HashSet<(int X, int Y)>();
            foreach (var kvp in _sites)
            {
                if (kvp.Value.Definition.IsWall)
                    walls.Add(kvp.Key);
            }
            return walls;
        }
    }

    public bool IsWallSiteAt(int x, int y)
    {
        lock (_lock)
        {
            return _sites.TryGetValue((x, y), out var s) && s.Definition.IsWall;
        }
    }

    /// <summary>
    /// Проверяет, разрешена ли данная стадия работы на строительной площадке (x, y).
    /// </summary>
    public bool IsStageAllowed(int x, int y, JobTypeId typeId)
    {
        if (!TryGetSite(x, y, out var site))
            return true; // Вне чертежей разрешено

        if (typeId == JobTypeId.BlueprintDelivery)
            return site.Stage == ConstructionStage.Supplying;

        if (typeId == JobTypeId.Construction)
            return site.Stage == ConstructionStage.Assembling;

        return true;
    }

    public void NotifyWallBuilt(int x, int y)
    {
        // Прогресс гаснет автоматически при завершении площадки
    }

    public void NotifySupplyDone(int x, int y)
    {
        // В новой логике обработано внутри DeliverMaterial
    }

    public bool StartSite(int x, int y, StructureDefinition def, MapData mapData)
    {
        if (def == null) return false;
        int gid = StartSitesBatch(new List<(int X, int Y)> { (x, y) }, def, mapData);
        if (gid > 0 && TryGetSite(x, y, out var site))
        {
            Callable.From(() => OnSiteAdded?.Invoke(site)).CallDeferred();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Старт стройки по списку клеток с автоматической постановкой расчистки и снабжения.
    /// Если указан pendingWarehouseZone, клетки склада активируются только после постройки всех стен здания.
    /// </summary>
    public int StartSitesBatch(List<(int X, int Y)> cells, StructureDefinition def, MapData mapData, List<(int X, int Y)> pendingWarehouseZone = null)
    {
        if (def == null || (cells == null || cells.Count == 0) && (pendingWarehouseZone == null || pendingWarehouseZone.Count == 0))
            return 0;

        var addedSites = new List<ConstructionSite>();
        var treesToChop = new List<(int X, int Y)>();
        var stonesToMine = new List<(int X, int Y)>();
        var itemsToHaul = new List<(int X, int Y)>();

        bool[,] treeMask = mapData?.TreeOnGrass;
        bool[,] stoneMask = mapData?.StoneOnGrass;
        int groupId;

        lock (_lock)
        {
            groupId = _nextGroupId++;
            var groupCells = new HashSet<(int X, int Y)>();
            _groupToCells[groupId] = groupCells;
            if (pendingWarehouseZone != null && pendingWarehouseZone.Count > 0)
            {
                _groupToPendingWarehouse[groupId] = new List<(int X, int Y)>(pendingWarehouseZone);
                foreach (var pos in pendingWarehouseZone)
                {
                    bool hasTree = treeMask != null && (uint)pos.X < (uint)treeMask.GetLength(0) && (uint)pos.Y < (uint)treeMask.GetLength(1) && treeMask[pos.X, pos.Y];
                    bool hasStone = stoneMask != null && (uint)pos.X < (uint)stoneMask.GetLength(0) && (uint)pos.Y < (uint)stoneMask.GetLength(1) && stoneMask[pos.X, pos.Y];
                    bool hasItems = GroundItemManager.Instance.HasItemsAt(pos.X, pos.Y);

                    if (hasTree) treesToChop.Add(pos);
                    if (hasStone) stonesToMine.Add(pos);
                    if (hasItems) itemsToHaul.Add(pos);
                }
            }

            if (cells != null)
            {
                foreach (var pos in cells)
                {
                    if (StructureManager.Instance.HasStructureAt(pos.X, pos.Y) || _sites.ContainsKey(pos))
                        continue;

                    var site = new ConstructionSite(pos.X, pos.Y, def)
                    {
                        SiteId = groupId
                    };

                    bool hasTree = treeMask != null && (uint)pos.X < (uint)treeMask.GetLength(0) && (uint)pos.Y < (uint)treeMask.GetLength(1) && treeMask[pos.X, pos.Y];
                    bool hasStone = stoneMask != null && (uint)pos.X < (uint)stoneMask.GetLength(0) && (uint)pos.Y < (uint)stoneMask.GetLength(1) && stoneMask[pos.X, pos.Y];
                    bool hasItems = GroundItemManager.Instance.HasItemsAt(pos.X, pos.Y);

                    site.NeedTree = hasTree;
                    site.NeedStone = hasStone;
                    site.NeedHaul = hasItems;

                    if (!site.HasObstacles)
                    {
                        site.Stage = ConstructionStage.Supplying;
                    }

                    _sites[pos] = site;
                    _cellToGroup[pos] = groupId;
                    groupCells.Add(pos);
                    addedSites.Add(site);

                    if (hasTree) treesToChop.Add(pos);
                    if (hasStone) stonesToMine.Add(pos);
                    if (hasItems) itemsToHaul.Add(pos);
                }
            }
        }

        // Запуск работ расчистки вне lock
        if (treesToChop.Count > 0)
            TreeJobManager.Instance.MarkTreesBatch(treesToChop);
        if (stonesToMine.Count > 0)
            StoneJobManager.Instance.MarkStonesBatch(stonesToMine, stoneMask);
        if (itemsToHaul.Count > 0)
        {
            if (StockpileManager.Instance.HasFreeSpace)
            {
                RegisterHaulBatch(itemsToHaul);
            }
            else
            {
                int mw = mapData?.Width ?? 512;
                int mh = mapData?.Height ?? 512;
                foreach (var (ix, iy) in itemsToHaul)
                {
                    GroundItemManager.Instance.TrySpillItemsAdjacent(ix, iy, mw, mh);
                }
            }
        }

        // Регистрация работ доставки для клеток, готовых сразу (без помех)
        var readySupply = new List<(int X, int Y)>();
        foreach (var s in addedSites)
        {
            if (s.Stage == ConstructionStage.Supplying)
                readySupply.Add((s.X, s.Y));
        }

        if (readySupply.Count > 0)
        {
            JobBroker.Instance.RegisterBlueprintBatch(readySupply, def.Type, def.TotalRequiredItems);
        }

        if (addedSites.Count > 0)
        {
            Callable.From(() => OnSitesBatchAdded?.Invoke(addedSites)).CallDeferred();
        }

        return groupId;
    }

    private static void RegisterHaulBatch(List<(int X, int Y)> items)
    {
        var batch = new List<JobData>(items.Count);
        foreach (var (x, y) in items)
        {
            batch.Add(new JobData
            {
                TypeId = JobTypeId.StockpileHauling,
                ExecutionType = JobExecutionType.Hauling,
                PriorityTier = JobPriorityTier.TreeChopping,
                SourceX = x,
                SourceY = y,
                TargetX = x,
                TargetY = y,
                StandX = x,
                StandY = y,
                MaxWorkers = 1
            });
        }
        JobDispatcher.Instance.RegisterBatch(batch);
    }

    public void NotifyTreeCleared(int x, int y)
    {
        CheckAndAdvanceObstacles(x, y, s => s.NeedTree = false);
    }

    public void NotifyStoneCleared(int x, int y)
    {
        CheckAndAdvanceObstacles(x, y, s => s.NeedStone = false);
    }

    public void NotifyHaulCleared(int x, int y)
    {
        CheckAndAdvanceObstacles(x, y, s => s.NeedHaul = false);
    }

    private void CheckAndAdvanceObstacles(int x, int y, Action<ConstructionSite> clearAction)
    {
        ConstructionSite site = null;
        bool becameReady = false;
        lock (_lock)
        {
            if (_sites.TryGetValue((x, y), out site))
            {
                clearAction(site);
                if (site.Stage == ConstructionStage.Clearance && !site.HasObstacles)
                {
                    site.Stage = ConstructionStage.Supplying;
                    becameReady = true;
                }
            }
        }

        if (becameReady && site != null)
        {
            JobBroker.Instance.RegisterBlueprint(x, y, site.Definition.Type, site.Definition.TotalRequiredItems);
        }
    }

    /// <summary>
    /// Прием доставленного ресурса агентом.
    /// </summary>
    public bool DeliverMaterial(int x, int y, ItemId item, int count, out int accepted, out bool isReadyToBuild)
    {
        accepted = 0;
        isReadyToBuild = false;
        ConstructionSite site;
        int groupId = 0;

        lock (_lock)
        {
            if (!_sites.TryGetValue((x, y), out site))
                return false;

            accepted = site.AddDelivered(item, count);
            if (site.IsFullySupplied)
            {
                site.Stage = BuildConfig.WaitForAllSiteResources ? ConstructionStage.WaitingAll : ConstructionStage.Assembling;
                _cellToGroup.TryGetValue((x, y), out groupId);
            }
        }

        if (site.IsFullySupplied)
        {
            if (!BuildConfig.WaitForAllSiteResources)
            {
                isReadyToBuild = true;
                JobBroker.Instance.RegisterConstructionAt(x, y);
            }
            else if (groupId > 0)
            {
                CheckGroupFullSupply(groupId);
            }
        }

        return accepted > 0;
    }

    private void CheckGroupFullSupply(int groupId)
    {
        List<(int X, int Y)> toAssemble = null;
        lock (_lock)
        {
            if (!_groupToCells.TryGetValue(groupId, out var cells))
                return;

            bool allReady = true;
            foreach (var pos in cells)
            {
                if (!_sites.TryGetValue(pos, out var s) || (!s.IsFullySupplied && s.Stage != ConstructionStage.Completed))
                {
                    allReady = false;
                    break;
                }
            }

            if (allReady)
            {
                toAssemble = new List<(int X, int Y)>();
                foreach (var pos in cells)
                {
                    if (_sites.TryGetValue(pos, out var s) && s.Stage == ConstructionStage.WaitingAll)
                    {
                        s.Stage = ConstructionStage.Assembling;
                        toAssemble.Add(pos);
                    }
                }
            }
        }

        if (toAssemble != null)
        {
            foreach (var (x, y) in toAssemble)
            {
                JobBroker.Instance.RegisterConstructionAt(x, y);
            }
        }
    }

    public void ReportWorkProgress(int x, int y, float progressFraction)
    {
        int stage = (int)(Math.Clamp(progressFraction, 0f, 1f) * 6);
        if (stage > 5) stage = 5;

        lock (_lock)
        {
            if (_sites.TryGetValue((x, y), out var site))
            {
                site.WorkProgress = progressFraction;
                if (site.WorkProgressStage == stage)
                    return;
                site.WorkProgressStage = stage;
            }
        }

        OnProgressStageChanged?.Invoke((x, y), stage);
    }

    public bool CompleteConstruction(int x, int y, SimulationContext ctx, out StructureDefinition completedDef)
    {
        return CompleteConstruction(x, y, ctx, null, out completedDef);
    }

    public bool CompleteConstruction(int x, int y, SimulationContext ctx, AgentDataPool pool, out StructureDefinition completedDef)
    {
        ConstructionSite site;
        List<(int X, int Y)> pendingWarehouseToActivate = null;
        lock (_lock)
        {
            if (!_sites.TryGetValue((x, y), out site))
            {
                completedDef = null;
                return false;
            }

            site.Stage = ConstructionStage.Completed;
            _sites.Remove((x, y));
            if (_cellToGroup.TryGetValue((x, y), out int gid))
            {
                _cellToGroup.Remove((x, y));
                if (_groupToCells.TryGetValue(gid, out var gcells))
                {
                    gcells.Remove((x, y));
                    if (gcells.Count == 0)
                    {
                        _groupToCells.Remove(gid);
                        _groupToPendingWarehouse.Remove(gid, out pendingWarehouseToActivate);
                    }
                }
            }
        }

        completedDef = site.Definition;
        JobBroker.Instance.UnregisterBlueprint(x, y);

        // Гасим спрайт прогресса
        OnProgressStageChanged?.Invoke((x, y), -1);

        // Добавляем готовую структуру и выталкиваем агентов из возведенной стены
        StructureManager.Instance.AddStructure(x, y, completedDef, ctx, pool);

        // Если здание склада полностью достроено — активируем его зону
        if (pendingWarehouseToActivate != null && pendingWarehouseToActivate.Count > 0)
        {
            StockpileManager.Instance.ActivatePendingZone(pendingWarehouseToActivate);
        }

        Callable.From(() => OnSiteCompleted?.Invoke(site)).CallDeferred();
        return true;
    }

    public void CancelSite(int x, int y)
    {
        ConstructionSite site;
        List<(int X, int Y)> pendingToRemove = null;
        lock (_lock)
        {
            if (!_sites.Remove((x, y), out site))
                return;

            if (_cellToGroup.TryGetValue((x, y), out int gid))
            {
                _cellToGroup.Remove((x, y));
                if (_groupToCells.TryGetValue(gid, out var gcells))
                {
                    gcells.Remove((x, y));
                    if (gcells.Count == 0)
                    {
                        _groupToCells.Remove(gid);
                        _groupToPendingWarehouse.Remove(gid, out pendingToRemove);
                    }
                }
            }
        }

        if (pendingToRemove != null && pendingToRemove.Count > 0)
        {
            StockpileManager.Instance.RemoveZoneTilesBatch(pendingToRemove);
        }

        UnregisterSiteJobsAndRefund(x, y, site);
        OnProgressStageChanged?.Invoke((x, y), -1);
        Callable.From(() => OnSiteRemoved?.Invoke((x, y))).CallDeferred();
    }

    public void CancelSitesBatch(List<(int X, int Y)> cells)
    {
        if (cells == null || cells.Count == 0) return;

        var removed = new List<((int X, int Y) Pos, ConstructionSite Site)>();
        var pendingBatchesToRemove = new List<List<(int X, int Y)>>();
        lock (_lock)
        {
            foreach (var pos in cells)
            {
                if (_sites.Remove(pos, out var site))
                {
                    removed.Add((pos, site));
                    if (_cellToGroup.TryGetValue(pos, out int gid))
                    {
                        _cellToGroup.Remove(pos);
                        if (_groupToCells.TryGetValue(gid, out var gcells))
                        {
                            gcells.Remove(pos);
                            if (gcells.Count == 0)
                            {
                                _groupToCells.Remove(gid);
                                if (_groupToPendingWarehouse.Remove(gid, out var pending))
                                {
                                    pendingBatchesToRemove.Add(pending);
                                }
                            }
                        }
                    }
                }
            }
        }

        foreach (var pList in pendingBatchesToRemove)
        {
            StockpileManager.Instance.RemoveZoneTilesBatch(pList);
        }

        var removedPosList = new List<(int X, int Y)>(removed.Count);
        foreach (var (pos, site) in removed)
        {
            removedPosList.Add(pos);
            UnregisterSiteJobsAndRefund(pos.X, pos.Y, site);
            OnProgressStageChanged?.Invoke(pos, -1);
        }

        if (removedPosList.Count > 0)
        {
            Callable.From(() => OnSitesBatchRemoved?.Invoke(removedPosList)).CallDeferred();
        }
    }

    private static void UnregisterSiteJobsAndRefund(int x, int y, ConstructionSite site)
    {
        TreeJobManager.Instance.UnmarkTree(x, y);
        StoneJobManager.Instance.UnmarkStone(x, y);
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.TreeChopping);
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Mining);
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.StockpileHauling);
        JobBroker.Instance.UnregisterBlueprint(x, y);
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Construction);

        // Возврат привезенных ресурсов на землю рядом
        if (site != null)
        {
            foreach (var (item, count) in site.GetAllDelivered())
            {
                if (count > 0)
                {
                    (int X, int Y) stand = (x, y);
                    for (int oy = -2; oy <= 2; oy++)
                        for (int ox = -2; ox <= 2; ox++)
                        {
                            if (ox == 0 && oy == 0) continue;
                            if (!GroundItemManager.Instance.HasItemsAt(x + ox, y + oy))
                            {
                                stand = (x + ox, y + oy);
                                break;
                            }
                        }
                    GroundItemManager.Instance.SpawnItems(stand.X, stand.Y, item, count);
                }
            }
        }
    }

    public void ClearAll()
    {
        List<ConstructionSite> snapshot;
        lock (_lock)
        {
            snapshot = new List<ConstructionSite>(_sites.Values);
            _sites.Clear();
            _groupToCells.Clear();
            _cellToGroup.Clear();
            _groupToPendingWarehouse.Clear();
        }

        var positions = new List<(int X, int Y)>(snapshot.Count);
        foreach (var s in snapshot)
        {
            positions.Add((s.X, s.Y));
            UnregisterSiteJobsAndRefund(s.X, s.Y, s);
            OnProgressStageChanged?.Invoke((s.X, s.Y), -1);
        }

        if (positions.Count > 0)
        {
            Callable.From(() => OnSitesBatchRemoved?.Invoke(positions)).CallDeferred();
        }
    }

    public void ReconcileTick(SimulationContext ctx)
    {
        long now = DateTime.UtcNow.Ticks;
        if (now - _lastReconcileTicks < ReconcileIntervalTicks)
            return;
        _lastReconcileTicks = now;
        if (ctx == null) return;

        List<ConstructionSite> snapshot;
        lock (_lock)
        {
            snapshot = new List<ConstructionSite>(_sites.Values);
        }

        var idx = JobDispatcher.Instance.JobIndex;
        bool[,] treeArr = ctx.TreeOnGrass;
        bool[,] stoneArr = ctx.StoneOnGrass;

        foreach (var site in snapshot)
        {
            int x = site.X, y = site.Y;
            if (StructureManager.Instance.HasStructureAt(x, y))
            {
                CompleteConstruction(x, y, ctx, out _);
                continue;
            }

            if (site.Stage == ConstructionStage.Clearance)
            {
                bool hasTree = treeArr != null && (uint)x < (uint)treeArr.GetLength(0) && (uint)y < (uint)treeArr.GetLength(1) && treeArr[x, y];
                bool hasStone = stoneArr != null && (uint)x < (uint)stoneArr.GetLength(0) && (uint)y < (uint)stoneArr.GetLength(1) && stoneArr[x, y];
                bool hasItems = GroundItemManager.Instance.HasItemsAt(x, y);

                site.NeedTree = hasTree;
                site.NeedStone = hasStone;
                site.NeedHaul = hasItems;

                if (hasItems && !StockpileManager.Instance.HasFreeSpace)
                {
                    GroundItemManager.Instance.TrySpillItemsAdjacent(x, y, ctx.MapWidth, ctx.MapHeight, ctx.SolidWalls);
                }

                if (!site.HasObstacles)
                {
                    site.Stage = ConstructionStage.Supplying;
                    JobBroker.Instance.RegisterBlueprint(x, y, site.Definition.Type, site.Definition.TotalRequiredItems);
                }
            }
            else if (site.Stage == ConstructionStage.Supplying)
            {
                if (!idx.HasJobAt(x, y, JobTypeId.BlueprintDelivery))
                {
                    JobBroker.Instance.RegisterBlueprint(x, y, site.Definition.Type, site.Definition.TotalRequiredItems);
                }
            }
            else if (site.Stage == ConstructionStage.Assembling)
            {
                if (!idx.HasJobAt(x, y, JobTypeId.Construction))
                {
                    JobBroker.Instance.RegisterConstructionAt(x, y);
                }
            }
        }
    }
}
