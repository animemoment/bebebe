using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// ЕДИНАЯ стройка для всего (стены, мебель, верстак — всё идёт сюда).
/// Простыми словами, порядок всегда один:
///   Шаг 1 РАСЧИСТКА: чистим ВСЕ клетки стройки (и стены, и внутренность зоны):
///     деревья рубят, камни добывают, вещи увозят. Других работ по этим
///     клеткам в это время НЕТ.
///   Шаг 2 ПОДНОС: ставим чертежи, агенты везут бревна.
///   Шаг 3 СТРОЙКА: строить можно ТОЛЬКО когда привезены ВСЕ 100% бревен
///     на ВСЮ стройку (а не по частям). Иначе — ждать, строить нельзя.
/// Что именно чистим — смотри BuildObstacles.NeedsClear (список легко дополнить).
/// Все цифры — в BuildConfig (один файл настроек).
/// Состояние хранится ПО КЛЕТКАМ (а не счётчиками): повторный вызов — без
/// вреда, пропущенное чинит Reconcile раз в 5 секунд.
/// Порядок блокировок: lock сайта НИКОГДА не держим во время вызовов
/// внешних менеджеров.
/// </summary>
public sealed class ConstructionPipeline
{
    public static ConstructionPipeline Instance { get; } = new();

    /// <summary>Шаг одной клетки стройки (строго по порядку).</summary>
    public enum CellStage : byte
    {
        Clearing = 1, // расчистка
        Supply = 2,   // поднос материалов (чертёж + доставка)
        WaitingAll = 3, // всё привезли на клетку, ждём остальные клетки стройки
        Building = 4, // стройка (разрешена: 100% ресурсов всей стройки на месте)
        Done = 5,     // построено / клетка выбыла
    }

    private sealed class CellState
    {
        public CellStage Stage = CellStage.Clearing;
        public bool NeedTree;
        public bool NeedStone;
        public bool NeedHaul;
    }

    private sealed class SiteState
    {
        public int Id;
        public BuildingType WallType = BuildingType.WoodWall;
        public Dictionary<(int X, int Y), CellState> Cells = new();
        // Сколько бревен надо ВСЕГО на стройку и сколько уже привезли.
        // Когда привезли все — открываем шаг Building сразу всем клеткам.
        public int LogsNeeded;
        public int LogsDelivered;
        public bool AllSupplied;
    }

    private readonly object _lock = new();
    private int _nextSiteId = 1;
    private readonly Dictionary<int, SiteState> _sites = new();
    // Обратный индекс клетка → сайт (чтобы FindSiteByCell был O(1)).
    private readonly Dictionary<(int X, int Y), int> _cellToSite = new();
    private long _lastReconcileTicks;
    private static readonly long ReconcileIntervalTicks = System.TimeSpan.FromSeconds(5).Ticks;

    /// <summary>
    /// Старт стройки. wallCells — клетки стен, zoneCells — внутренность зоны
    /// (её тоже расчищаем: деревья/камни/вещи внутри будущей комнаты убираем).
    /// Возвращает id стройки (0 — нечего строить).
    /// </summary>
    public int StartSite(List<(int X, int Y)> wallCells, BuildingType wallType, MapData mapData,
        List<(int X, int Y)> zoneCells = null)
    {
        if ((wallCells == null || wallCells.Count == 0) && (zoneCells == null || zoneCells.Count == 0))
            return 0;

        bool[,] treeMask = mapData?.TreeOnGrass;
        bool[,] stoneMask = mapData?.StoneOnGrass;
        var site = new SiteState { WallType = wallType };
        var trees = new List<(int X, int Y)>();
        var stones = new List<(int X, int Y)>();
        var items = new List<(int X, int Y)>();

        lock (_lock)
        {
            site.Id = _nextSiteId++;
            // Стены: чистим + потом строим. Зона внутри: ТОЛЬКО чистим
            // (строить там нечего, зона ставится сразу инструментом).
            RegisterCells(wallCells, true, site, treeMask, stoneMask, trees, stones, items);
            RegisterCells(zoneCells, false, site, treeMask, stoneMask, trees, stones, items);
            if (site.Cells.Count == 0)
                return 0;
            // Сколько бревен надо на всю стройку (только стены, зона бревен не ест).
            int perCell = BuildConfig.LogsFor(wallType);
            int wallCount = 0;
            foreach (var kv in site.Cells)
                if (IsWallCell(site, kv.Key)) wallCount++;
            site.LogsNeeded = wallCount * perCell;
            if (site.LogsNeeded <= 0)
            {
                // Стен нет (например чистая зона без стен) — строить нечего,
                // но расчистку всё равно делаем, дальше клетки просто уйдут.
                site.AllSupplied = true;
            }
            _sites[site.Id] = site;
        }

        if (trees.Count > 0)
            TreeJobManager.Instance.MarkTreesBatch(trees);
        if (stones.Count > 0)
            StoneJobManager.Instance.MarkStonesBatch(stones, stoneMask);
        if (items.Count > 0)
            RegisterHaulBatch(items);

        PromoteReadyCells(site.Id);
        MaybeOpenBuilding(site.Id);
        return site.Id;
    }

    // Клетки зоны помечаем NeedBuild=false через отдельный набор.
    private readonly HashSet<(int Site, int X, int Y)> _noBuildCells = new();

    private void RegisterCells(List<(int X, int Y)> cells, bool needBuild, SiteState site,
        bool[,] treeMask, bool[,] stoneMask,
        List<(int X, int Y)> trees, List<(int X, int Y)> stones, List<(int X, int Y)> items)
    {
        if (cells == null) return;
        foreach (var (x, y) in cells)
        {
            if (BuildObstacles.IsBlockedByHandmade(x, y))
                continue;
            if (_cellToSite.ContainsKey((x, y)))
                continue;
            var cs = new CellState();
            bool hasTree = treeMask != null && (uint)x < (uint)treeMask.GetLength(0) && (uint)y < (uint)treeMask.GetLength(1) && treeMask[x, y];
            bool hasStone = stoneMask != null && (uint)x < (uint)stoneMask.GetLength(0) && (uint)y < (uint)stoneMask.GetLength(1) && stoneMask[x, y];
            bool hasItems = GroundItemManager.Instance.HasItemsAt(x, y);
            cs.NeedTree = hasTree;
            cs.NeedStone = hasStone;
            cs.NeedHaul = hasItems;
            site.Cells[(x, y)] = cs;
            _cellToSite[(x, y)] = site.Id;
            if (!needBuild)
                _noBuildCells.Add((site.Id, x, y));
            if (hasTree) trees.Add((x, y));
            if (hasStone) stones.Add((x, y));
            if (hasItems) items.Add((x, y));
        }
    }

    private bool IsWallCell(SiteState site, (int X, int Y) cell)
    {
        return !_noBuildCells.Contains((site.Id, cell.X, cell.Y));
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

    /// <summary>
    /// Клетки без расчистки — в Supply (чертёж + доставка по одной клетке).
    /// Клетки зоны (не стены) чертежей не получают — им только расчистка.
    /// </summary>
    private void PromoteReadyCells(int siteId)
    {
        List<(int X, int Y)> toSupply = null;
        BuildingType wallType;
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var site))
                return;
            wallType = site.WallType;
            foreach (var kv in site.Cells)
            {
                var cs = kv.Value;
                if (cs.Stage == CellStage.Clearing && !cs.NeedTree && !cs.NeedStone && !cs.NeedHaul)
                {
                    if (!IsWallCell(site, kv.Key))
                    {
                        // Внутренняя клетка зоны: чистить больше нечего — выбывает.
                        cs.Stage = CellStage.Done;
                        continue;
                    }
                    cs.Stage = CellStage.Supply;
                    toSupply ??= new List<(int X, int Y)>();
                    toSupply.Add(kv.Key);
                }
            }
        }
        // Выбывшие клетки зоны чистим из индексов (вне lock — аккуратный проход).
        CleanupDoneCells(siteId);
        if (toSupply == null || toSupply.Count == 0)
            return;
        var map = Game.UI.MapRenderer.Instance?.MapData;
        var clean = new List<(int X, int Y)>(toSupply.Count);
        foreach (var (x, y) in toSupply)
        {
            if (map != null && ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height
                || map.Ground[x, y] != TileType.Grass
                || map.TreeOnGrass[x, y]
                || map.HasStone(x, y)
                || GroundItemManager.Instance.HasItemsAt(x, y)))
            {
                RemoveCell(siteId, x, y);
                continue;
            }
            clean.Add((x, y));
        }
        if (clean.Count > 0)
            BlueprintManager.Instance.AddBlueprintsBatch(clean, wallType, map?.TreeOnGrass, map?.StoneOnGrass);
    }

    private void CleanupDoneCells(int siteId)
    {
        List<(int X, int Y)> done = null;
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var site))
                return;
            foreach (var kv in site.Cells)
                if (kv.Value.Stage == CellStage.Done)
                {
                    done ??= new List<(int X, int Y)>();
                    done.Add(kv.Key);
                }
            if (done == null) return;
            foreach (var c in done)
            {
                site.Cells.Remove(c);
                _cellToSite.Remove(c);
                _noBuildCells.Remove((siteId, c.X, c.Y));
            }
            if (site.Cells.Count == 0)
                _sites.Remove(siteId);
        }
    }

    /// <summary>Дерево срублено: снять флаг, при готовности — в Supply.</summary>
    public void NotifyTreeCleared(int x, int y)
    {
        int site = FindSiteByCell(x, y);
        if (site == 0) return;
        lock (_lock)
        {
            if (_sites.TryGetValue(site, out var s) && s.Cells.TryGetValue((x, y), out var cs))
                cs.NeedTree = false;
        }
        PromoteReadyCells(site);
    }

    /// <summary>Камень добыт: снять флаг, при готовности — в Supply.</summary>
    public void NotifyStoneCleared(int x, int y)
    {
        int site = FindSiteByCell(x, y);
        if (site == 0) return;
        lock (_lock)
        {
            if (_sites.TryGetValue(site, out var s) && s.Cells.TryGetValue((x, y), out var cs))
                cs.NeedStone = false;
        }
        PromoteReadyCells(site);
    }

    /// <summary>Предметы вывезены: снять флаг, при готовности — в Supply.</summary>
    public void NotifyHaulCleared(int x, int y)
    {
        int site = FindSiteByCell(x, y);
        if (site == 0) return;
        lock (_lock)
        {
            if (_sites.TryGetValue(site, out var s) && s.Cells.TryGetValue((x, y), out var cs))
                cs.NeedHaul = false;
        }
        PromoteReadyCells(site);
    }

    [System.Obsolete("Use NotifyTreeCleared/NotifyStoneCleared/NotifyHaulCleared per cell")]
    public void NotifyClearDone(int siteId) { }

    /// <summary>
    /// Доставка завершена (чертёж снабжён → создан Construction).
    /// Клетка НЕ идёт сразу в стройку: она ждёт остальные (WaitingAll),
    /// стройка откроется всем разом через MaybeOpenBuilding (100% правило).
    /// </summary>
    public void NotifySupplyDone(int x, int y)
    {
        int site = FindSiteByCell(x, y);
        if (site == 0) return;
        lock (_lock)
        {
            if (_sites.TryGetValue(site, out var s) && s.Cells.TryGetValue((x, y), out var cs))
            {
                if (cs.Stage == CellStage.Supply)
                    cs.Stage = CellStage.WaitingAll;
                // Считаем привезённые бревна по факту чертежа.
                s.LogsDelivered = CountDeliveredLocked(s);
                if (BuildConfig.WaitForAllSiteResources)
                {
                    if (s.LogsDelivered >= s.LogsNeeded && s.LogsNeeded > 0)
                        s.AllSupplied = true;
                }
                else
                {
                    // Старое поведение (по клеткам) — выключено по умолчанию.
                    cs.Stage = CellStage.Building;
                    return;
                }
            }
        }
        MaybeOpenBuilding(site);
    }

    private static int CountDeliveredLocked(SiteState s)
    {
        // Считаем по чертежам менеджера: сколько уже довезли на клетки сайта.
        int sum = 0;
        foreach (var cell in s.Cells.Keys)
        {
            sum += BlueprintManager.Instance.GetDelivered(cell.X, cell.Y);
            // Клетки в WaitingAll/Building уже снабжены полностью.
            if (s.Cells.TryGetValue(cell, out var cs)
                && (cs.Stage == CellStage.WaitingAll || cs.Stage == CellStage.Building))
                sum += 0; // GetDelivered уже вернул Target (чертёж снабжён)
        }
        return sum;
    }

    /// <summary>
    /// Гейт 100%: если вся стройка снабжена — перевести ВСЕ ждущие клетки
    /// в Building разом. Иначе — ничего не делать (строить нельзя).
    /// </summary>
    private void MaybeOpenBuilding(int siteId)
    {
        List<(int X, int Y)> toBuild = null;
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var s))
                return;
            if (!BuildConfig.WaitForAllSiteResources)
                return;
            // Пересчёт: сколько клеток уже полностью снабжены.
            int ready = 0, total = 0;
            foreach (var kv in s.Cells)
            {
                if (!IsWallCell(s, kv.Key)) continue;
                total++;
                if (kv.Value.Stage == CellStage.WaitingAll || kv.Value.Stage == CellStage.Building)
                    ready++;
            }
            if (total == 0) return;
            if (ready < total) return; // ещё не все 100% — строить НЕЛЬЗЯ
            s.AllSupplied = true;
            foreach (var kv in s.Cells)
            {
                if (!IsWallCell(s, kv.Key)) continue;
                if (kv.Value.Stage == CellStage.WaitingAll)
                {
                    kv.Value.Stage = CellStage.Building;
                    toBuild ??= new List<(int X, int Y)>();
                    toBuild.Add(kv.Key);
                }
            }
        }
        if (toBuild == null) return;
        // Construction-работы создаём вне lock (порядок блокировок).
        foreach (var (x, y) in toBuild)
            JobBroker.Instance.RegisterConstructionAt(x, y);
    }

    [System.Obsolete("Use NotifySupplyDone(x, y) per cell")]
    public void NotifySupplyDone(int siteId) { }

    /// <summary>Стена построена: клетка Done, пустые сайты чистим.</summary>
    public void NotifyWallBuilt(int x, int y)
    {
        lock (_lock)
        {
            if (!_cellToSite.TryGetValue((x, y), out int site))
                return;
            if (!_sites.TryGetValue(site, out var s))
            {
                _cellToSite.Remove((x, y));
                return;
            }
            if (s.Cells.TryGetValue((x, y), out var cs))
                cs.Stage = CellStage.Done;
            s.Cells.Remove((x, y));
            _cellToSite.Remove((x, y));
            _noBuildCells.Remove((site, x, y));
            if (s.Cells.Count == 0)
                _sites.Remove(site);
        }
        // Прогресс-спрайт гаснет всегда здесь (единая точка — не залипает).
        WorkProgressTracker.Instance.Clear(x, y);
    }

    /// <summary>Исключить клетку из стройки. Чистит работы и чертежи. Без вреда при повторе.</summary>
    public void RemoveCell(int siteId, int x, int y)
    {
        bool had;
        lock (_lock)
        {
            had = _sites.TryGetValue(siteId, out var s) && s.Cells.Remove((x, y));
            _cellToSite.Remove((x, y));
            _noBuildCells.Remove((siteId, x, y));
            if (had && s.Cells.Count == 0)
                _sites.Remove(siteId);
        }
        if (!had) return;
        TreeJobManager.Instance.UnmarkTree(x, y);
        StoneJobManager.Instance.UnmarkStone(x, y);
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.TreeChopping);
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Mining);
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.StockpileHauling);
        BlueprintManager.Instance.RemoveBlueprint(x, y, out _);
        WorkProgressTracker.Instance.Clear(x, y);
    }

    /// <summary>
    /// Самопочинка раз в 5 сек (зовёт sim-поток): сверяет флаги с фактом мира,
    /// пересоздаёт потерянные работы, добивает гейт 100%.
    /// </summary>
    public void ReconcileTick(SimulationContext ctx)
    {
        long now = System.DateTime.UtcNow.Ticks;
        if (now - _lastReconcileTicks < ReconcileIntervalTicks)
            return;
        _lastReconcileTicks = now;
        if (ctx == null)
            return;

        List<(int Site, int X, int Y)> snapshot;
        lock (_lock)
        {
            snapshot = new List<(int, int, int)>(_cellToSite.Count);
            foreach (var kv in _cellToSite)
                snapshot.Add((kv.Value, kv.Key.X, kv.Key.Y));
        }
        var idx = JobDispatcher.Instance.JobIndex;
        bool[,] treeArr = ctx.TreeOnGrass;
        bool[,] stoneArr = ctx.StoneOnGrass;
        int mapW = ctx.MapWidth, mapH = ctx.MapHeight;
        foreach (var (site, x, y) in snapshot)
        {
            CellStage stage;
            bool needTree, needStone, needHaul;
            lock (_lock)
            {
                if (!_sites.TryGetValue(site, out var s) || !s.Cells.TryGetValue((x, y), out var cs))
                    continue;
                stage = cs.Stage;
                needTree = cs.NeedTree; needStone = cs.NeedStone; needHaul = cs.NeedHaul;
            }
            bool inBounds = (uint)x < (uint)mapW && (uint)y < (uint)mapH;
            bool hasTree = inBounds && treeArr != null && (uint)x < (uint)treeArr.GetLength(0) && (uint)y < (uint)treeArr.GetLength(1)
                && treeArr[x, y];
            bool hasStone = inBounds && stoneArr != null
                && (uint)x < (uint)stoneArr.GetLength(0) && (uint)y < (uint)stoneArr.GetLength(1) && stoneArr[x, y];
            bool hasItems = GroundItemManager.Instance.HasItemsAt(x, y);
            bool hasWall = BuildingManager.Instance.HasBuildingAt(x, y)
                || (Game.UI.MapRenderer.Instance?.WallBuildManager?.IsWallAt(x, y) ?? false);
            if (hasWall)
            {
                NotifyWallBuilt(x, y);
                continue;
            }
            if (stage == CellStage.Clearing)
            {
                if (needTree && !hasTree && !idx.HasJobAt(x, y, JobTypeId.TreeChopping)
                    && !TreeJobManager.Instance.IsTreeMarked(x, y))
                    NotifyTreeCleared(x, y);
                else if (needTree && hasTree && !idx.HasJobAt(x, y, JobTypeId.TreeChopping)
                    && !TreeJobManager.Instance.IsTreeMarked(x, y))
                    TreeJobManager.Instance.MarkTree(x, y);
                if (needStone && !hasStone && !idx.HasJobAt(x, y, JobTypeId.Mining)
                    && !StoneJobManager.Instance.IsStoneMarked(x, y))
                    NotifyStoneCleared(x, y);
                else if (needStone && hasStone && !idx.HasJobAt(x, y, JobTypeId.Mining)
                    && !StoneJobManager.Instance.IsStoneMarked(x, y))
                    StoneJobManager.Instance.MarkStone(x, y, ctx.StoneOnGrass);
                if (needHaul && !hasItems && !idx.HasJobAt(x, y, JobTypeId.StockpileHauling))
                    NotifyHaulCleared(x, y);
                else if (needHaul && hasItems && !idx.HasJobAt(x, y, JobTypeId.StockpileHauling))
                    RegisterHaulBatch(new List<(int X, int Y)> { (x, y) });
                if (!needTree && hasTree)
                {
                    lock (_lock)
                    {
                        if (_sites.TryGetValue(site, out var s2) && s2.Cells.TryGetValue((x, y), out var cs2)
                            && cs2.Stage == CellStage.Clearing && !cs2.NeedTree)
                        {
                            cs2.NeedTree = true;
                            TreeJobManager.Instance.MarkTree(x, y);
                        }
                    }
                }
                if (!needStone && hasStone)
                {
                    lock (_lock)
                    {
                        if (_sites.TryGetValue(site, out var s3) && s3.Cells.TryGetValue((x, y), out var cs3)
                            && cs3.Stage == CellStage.Clearing && !cs3.NeedStone)
                        {
                            cs3.NeedStone = true;
                            StoneJobManager.Instance.MarkStone(x, y, ctx.StoneOnGrass);
                        }
                    }
                }
                if (!needHaul && hasItems)
                {
                    lock (_lock)
                    {
                        if (_sites.TryGetValue(site, out var s4) && s4.Cells.TryGetValue((x, y), out var cs4)
                            && cs4.Stage == CellStage.Clearing && !cs4.NeedHaul)
                            cs4.NeedHaul = true;
                    }
                    RegisterHaulBatch(new List<(int X, int Y)> { (x, y) });
                }
            }
            else if (stage == CellStage.Supply)
            {
                if (!BlueprintManager.Instance.IsBlueprintAt(x, y)
                    && !idx.HasJobAt(x, y, JobTypeId.BlueprintDelivery)
                    && !idx.HasJobAt(x, y, JobTypeId.Construction))
                {
                    BuildingType wt;
                    lock (_lock)
                    {
                        if (!_sites.TryGetValue(site, out var s5)) continue;
                        wt = s5.WallType;
                    }
                    BlueprintManager.Instance.AddBlueprintsBatch(
                        new List<(int X, int Y)> { (x, y) }, wt, ctx.TreeOnGrass, ctx.StoneOnGrass);
                }
            }
            else if (stage == CellStage.WaitingAll)
            {
                // Ждём остальные клетки — Construction здесь запрещён гейтом.
                // Если чертёж снабжён, а работа стройки уже висит (старый сейв/
                // гонка) — сносим её: строить до 100% нельзя.
                if (idx.HasJobAt(x, y, JobTypeId.Construction))
                    JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Construction);
            }
            else if (stage == CellStage.Building)
            {
                if (!idx.HasJobAt(x, y, JobTypeId.Construction)
                    && !BlueprintManager.Instance.IsBlueprintAt(x, y))
                {
                    JobBroker.Instance.RegisterConstructionAt(x, y);
                }
            }
        }
        List<int> siteIds;
        lock (_lock) { siteIds = new List<int>(_sites.Keys); }
        foreach (int id in siteIds)
        {
            PromoteReadyCells(id);
            MaybeOpenBuilding(id);
        }
    }

    /// <summary>Отмена стройки: снять работы, удалить чертежи, погасить прогресс.</summary>
    public void CancelSite(int siteId)
    {
        List<(int X, int Y)> cells;
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var site))
                return;
            cells = new List<(int X, int Y)>(site.Cells.Keys);
            foreach (var c in cells)
            {
                _cellToSite.Remove(c);
                _noBuildCells.Remove((siteId, c.X, c.Y));
            }
            _sites.Remove(siteId);
        }
        var trees = new List<(int X, int Y)>();
        var stones = new List<(int X, int Y)>();
        foreach (var (x, y) in cells)
        {
            if (TreeJobManager.Instance.IsTreeMarked(x, y))
                trees.Add((x, y));
            if (StoneJobManager.Instance.IsStoneMarked(x, y))
                stones.Add((x, y));
        }
        if (trees.Count > 0)
            TreeJobManager.Instance.UnmarkTreesBatch(trees);
        if (stones.Count > 0)
            StoneJobManager.Instance.UnmarkStonesBatch(stones);
        JobDispatcher.Instance.UnregisterBatchByPositions(cells, JobTypeId.TreeChopping);
        JobDispatcher.Instance.UnregisterBatchByPositions(cells, JobTypeId.Mining);
        JobDispatcher.Instance.UnregisterBatchByPositions(cells, JobTypeId.StockpileHauling);
        BlueprintManager.Instance.RemoveBlueprintsBatch(cells);
        foreach (var (x, y) in cells)
            WorkProgressTracker.Instance.Clear(x, y);
    }

    /// <summary>Найти стройку по клетке (для Notify-точек хендлеров). O(1).</summary>
    public int FindSiteByCell(int x, int y)
    {
        lock (_lock)
        {
            return _cellToSite.TryGetValue((x, y), out int site) ? site : 0;
        }
    }

    /// <summary>
    /// ЖЁСТКИЙ ГЕЙТ шагов: работа этого типа по клетке сейчас разрешена?
    /// Расчистка — только Clearing, доставка — Supply, стройка — Building.
    /// WaitingAll — строить/везти уже НЕЛЬЗЯ (ждём 100%).
    /// Вне стройки — разрешено всё.
    /// </summary>
    public bool IsStageAllowed(int x, int y, JobTypeId type)
    {
        CellStage stage;
        lock (_lock)
        {
            if (!_cellToSite.TryGetValue((x, y), out int site)
                || !_sites.TryGetValue(site, out var s)
                || !s.Cells.TryGetValue((x, y), out var cs))
                return true;
            stage = cs.Stage;
        }
        return type switch
        {
            JobTypeId.TreeChopping or JobTypeId.Mining or JobTypeId.StockpileHauling
                => stage == CellStage.Clearing,
            JobTypeId.BlueprintDelivery => stage == CellStage.Supply,
            // Стройка — только когда ВСЯ стройка снабжена (шаг Building).
            // WaitingAll = клетка готова, но остальные нет — строить нельзя.
            JobTypeId.Construction => stage == CellStage.Building,
            _ => true,
        };
    }

    /// <summary>Сколько процентов ресурсов привезено на стройку (для окна/подсказок).</summary>
    public int GetSupplyPercent(int x, int y)
    {
        lock (_lock)
        {
            if (!_cellToSite.TryGetValue((x, y), out int site)
                || !_sites.TryGetValue(site, out var s) || s.LogsNeeded <= 0)
                return 100;
            int ready = 0, total = 0;
            foreach (var kv in s.Cells)
            {
                if (!IsWallCell(s, kv.Key)) continue;
                total++;
                if (kv.Value.Stage == CellStage.WaitingAll || kv.Value.Stage == CellStage.Building)
                    ready++;
            }
            if (total == 0) return 100;
            return ready * 100 / total;
        }
    }
}
