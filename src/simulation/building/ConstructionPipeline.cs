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
///   ЗОНА (ферма/склад внутри контура): работает ТОЛЬКО когда все её стены
///   построены (site.IsComplete). Пока здание не достроено — фермеры и
///   носильщики внутрь не пускаются (IsZoneWorkAllowed).
/// Правило «не завершив шаг на 100% — дальше нельзя» держится СТАДИЯМИ
/// клетки: работа другого шага просто не допускается гейтом IsStageAllowed,
/// а потерянное догоняет ReconcileTick.
/// Что именно чистим — смотри BuildObstacles.NeedsClear (список легко дополнить).
/// Все цифры — в BuildConfig (один файл настроек).
/// Состояние хранится ПО КЛЕТКАМ: повторный вызов — без вреда, пропущенное
/// чинит Reconcile раз в 5 секунд.
/// Порядок блокировок: lock пайплайна (_lock) НИКОГДА не держим во время
/// вызовов внешних менеджеров; lock сайта (_sitesLock) — только для
/// микросекундных операций над словарями (никогда не держим во время _lock).
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
        // Стена или внутренняя клетка зоны (зоны только чистятся, не строятся).
        public bool IsWall;
    }

    private sealed class SiteState
    {
        public int Id;
        public BuildingType WallType = BuildingType.WoodWall;
        public Dictionary<(int X, int Y), CellState> Cells = new();
        // Внутренние клетки зоны этого сайта (для гейта IsZoneWorkAllowed).
        public List<(int X, int Y)> ZoneCells;
        // Все ли стены построены (зоновые клетки к этому не относятся).
        public bool WallsBuilt;
        // Сайт полностью завершён (все клетки выбыли) — флаг для дёшевого
        // чтения из-под _lock (словари сайта при этом ещё живы).
        public bool Completed;
    }

    // Один замок: все критические секции — микросекундные операции над
    // словарями БЕЗ внешних вызовов внутри (проверено по всему классу).
    // Горячие гейты (IsStageAllowed/IsZoneWorkAllowed) берут его на одно
    // чтение; внешние менеджеры (BlueprintManager, JobDispatcher, …) всегда
    // вызываются вне lock — порядок блокировок G→S→P не нарушается.
    private readonly object _lock = new();
    private int _nextSiteId = 1;
    private readonly Dictionary<int, SiteState> _sites = new();
    // Обратный индекс клетка → сайт (чтобы FindSiteByCell был O(1)).
    private readonly Dictionary<(int X, int Y), int> _cellToSite = new();
    // Сайты с живой расчисткой/снабжением: PromoteReadyCells/MaybeOpenBuilding
    // зовём только для них, а не перебираем все сайты каждый reconcile-тик.
    private readonly HashSet<int> _activeClearSites = new();
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
        bool anyClearing = false;

        lock (_lock)
        {
            site.Id = _nextSiteId++;
            // Стены: чистим + потом строим. Зона внутри: ТОЛЬКО чистим
            // (строить там нечего, зона ставится сразу инструментом).
            RegisterCells(wallCells, true, site, treeMask, stoneMask, trees, stones, items);
            RegisterCells(zoneCells, false, site, treeMask, stoneMask, trees, stones, items);
            if (site.Cells.Count == 0)
                return 0;
            foreach (var cs in site.Cells.Values)
                if (cs.Stage == CellStage.Clearing) { anyClearing = true; break; }
            if (anyClearing)
                _activeClearSites.Add(site.Id);
            // Нет стен — «здание» считается построенным сразу (иначе зона
            // под открытым небом никогда не получила бы разрешение работать).
            if (!HasAnyWallLocked(site))
            {
                site.WallsBuilt = true;
                site.Completed = true;
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
            var cs = new CellState { IsWall = needBuild };
            bool hasTree = MaskHas(treeMask, x, y);
            bool hasStone = MaskHas(stoneMask, x, y);
            bool hasItems = GroundItemManager.Instance.HasItemsAt(x, y);
            cs.NeedTree = hasTree;
            cs.NeedStone = hasStone;
            cs.NeedHaul = hasItems;
            site.Cells[(x, y)] = cs;
            _cellToSite[(x, y)] = site.Id;
            if (!needBuild)
            {
                site.ZoneCells ??= new List<(int X, int Y)>();
                site.ZoneCells.Add((x, y));
                _zoneCellsGlobal.Add((x, y));
            }
            if (hasTree) trees.Add((x, y));
            if (hasStone) stones.Add((x, y));
            if (hasItems) items.Add((x, y));
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool MaskHas(bool[,] mask, int x, int y)
        => mask != null && (uint)x < (uint)mask.GetLength(0) && (uint)y < (uint)mask.GetLength(1)
           && mask[x, y];

    // Есть ли у сайта ещё живые стеновые клетки (зовётся из-под _lock).
    private static bool HasAnyWallLocked(SiteState s)
    {
        foreach (var kv in s.Cells)
            if (kv.Value.IsWall) return true;
        return false;
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
    /// Вызывается из-под _lock НЕ должен; сам берёт _lock на микросекунды.
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
                    if (!cs.IsWall)
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
        // Выбывшие клетки зоны чистим из индексов.
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
                _zoneCellsGlobal.Remove(c);
            }
            if (site.Cells.Count == 0)
                DropSiteLocked(siteId, site);
        }
    }

    // Удалить сайт из всех реестров (зовётся из-под _lock). Словарь сайта
    // при этом остаётся живым: ссылки на него в кэше гейтов сами истлеют
    // через GatewayTtlMs (见 TryGetGateway).
    private void DropSiteLocked(int siteId, SiteState site)
    {
        site.Completed = true;
        _sites.Remove(siteId);
        _activeClearSites.Remove(siteId);
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
                if (cs.Stage != CellStage.Supply)
                    return; // повтор/гонка — игнорируем
                cs.Stage = CellStage.WaitingAll;
                if (!BuildConfig.WaitForAllSiteResources)
                {
                    // Старое поведение (по клеткам) — выключено по умолчанию.
                    cs.Stage = CellStage.Building;
                    return;
                }
            }
        }
        MaybeOpenBuilding(site);
    }

    /// <summary>
    /// Гейт 100%: если ВСЕ стеновые клетки снабжены полностью — перевести их
    /// в Building разом. Иначе — ничего не делать (строить нельзя).
    /// Основание — СТАДИИ клеток (факт полной доставки каждой), а не счётчик
    /// брёвен: 0.99% недолива не пропустит.
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
            int totalWalls = 0, readyWalls = 0;
            foreach (var kv in s.Cells)
            {
                if (!kv.Value.IsWall) continue;
                totalWalls++;
                var st = kv.Value.Stage;
                if (st == CellStage.WaitingAll || st == CellStage.Building)
                    readyWalls++;
            }
            if (totalWalls == 0 || readyWalls < totalWalls)
                return; // ещё не все 100% — строить НЕЛЬЗЯ
            foreach (var kv in s.Cells)
            {
                if (!kv.Value.IsWall) continue;
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

    /// <summary>Стена построена: клетка Done; все стены готовы — открываем зону.</summary>
    public void NotifyWallBuilt(int x, int y)
    {
        bool zoneJustOpened = false;
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
            _zoneCellsGlobal.Remove((x, y));
            if (!HasAnyWallLocked(s) && !s.WallsBuilt)
            {
                // Последняя стена сайта готова — зона внутри получает право
                // работать (открываем работы ниже, вне lock).
                s.WallsBuilt = true;
                if (s.Cells.Count == 0)
                    zoneJustOpened = true; // Completed поставит DropSiteLocked
            }
            if (s.Cells.Count == 0)
                DropSiteLocked(site, s);
        }
        // Прогресс-спрайт гаснет всегда здесь (единая точка — не залипает).
        WorkProgressTracker.Instance.Clear(x, y);
        if (zoneJustOpened)
            OpenZoneJobs(x, y);
    }

    // Момент, когда здание достроено: немедленно выводим клетки зоны из
    // диспетчерского кулдауна (агенты берут работу за ~2 сек вместо дождика
    // reconcile-тик 5 сек / аудита 30 сек).
    private void OpenZoneJobs(int wallX, int wallY)
    {
        // Найденный выше сайт уже удалён — координаты зоны берём из кэша
        // гейта последнего сайта (TryGetGateway ниже его же и обновит).
        if (!TryGetGateway(wallX, wallY, out _, out var zoneCells) || zoneCells == null)
            return;
        var idx = JobDispatcher.Instance.JobIndex;
        foreach (var (x, y) in zoneCells)
        {
            idx.ResetCooldownAt(x, y, JobTypeId.Farming);
            idx.ResetCooldownAt(x, y, JobTypeId.Planting);
            idx.ResetCooldownAt(x, y, JobTypeId.Harvesting);
            idx.ResetCooldownAt(x, y, JobTypeId.StockpileHauling);
        }
    }

    /// <summary>Исключить клетку из стройки. Чистит работы и чертежи. Без вреда при повторе.</summary>
    public void RemoveCell(int siteId, int x, int y)
    {
        bool had;
        lock (_lock)
        {
            had = _sites.TryGetValue(siteId, out var s) && s.Cells.Remove((x, y));
            _cellToSite.Remove((x, y));
            _zoneCellsGlobal.Remove((x, y));
            if (had && s.Cells.Count == 0)
                DropSiteLocked(siteId, s);
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
    /// пересоздаёт потерянные работы, добивает гейт 100%, открывает зоны после
    /// достройки стен.
    /// Поток один (sim), но state меняется из других потоков — все словари
    /// читаем ТОЛЬКО под _lock короткими снимками (без внешних вызовов внутри).
    /// </summary>
    public void ReconcileTick(SimulationContext ctx)
    {
        long now = System.DateTime.UtcNow.Ticks;
        if (now - _lastReconcileTicks < ReconcileIntervalTicks)
            return;
        _lastReconcileTicks = now;
        if (ctx == null)
            return;

        // Снимок клеток: стадия + флаги + тип клетки — одним проходом под lock.
        List<(int Site, int X, int Y, CellStage Stage, bool IsWall, bool NeedTree, bool NeedStone, bool NeedHaul)> snapshot;
        List<(int SiteId, BuildingType WallType)> sites;
        List<(int SiteId, List<(int X, int Y)> ZoneCells)> completeSitesToOpen;
        lock (_lock)
        {
            snapshot = new(_cellToSite.Count);
            foreach (var kv in _cellToSite)
            {
                int siteId = kv.Value;
                if (!_sites.TryGetValue(siteId, out var s)
                    || !s.Cells.TryGetValue(kv.Key, out var cs))
                    continue;
                snapshot.Add((siteId, kv.Key.X, kv.Key.Y, cs.Stage, cs.IsWall,
                    cs.NeedTree, cs.NeedStone, cs.NeedHaul));
            }
            sites = new List<(int, BuildingType)>(_sites.Count);
            completeSitesToOpen = null;
            foreach (var kv in _sites)
            {
                sites.Add((kv.Key, kv.Value.WallType));
                // Здание достроено, но зона ещё не открылась (пропущенный
                // NotifyWallBuilt / сейв-загрузка) — чиним здесь.
                if (kv.Value.WallsBuilt && !kv.Value.Completed && kv.Value.ZoneCells != null)
                {
                    completeSitesToOpen ??= new List<(int, List<(int X, int Y)>)>();
                    completeSitesToOpen.Add((kv.Key, kv.Value.ZoneCells));
                }
            }
        }

        var idx = JobDispatcher.Instance.JobIndex;
        bool[,] treeArr = ctx.TreeOnGrass;
        bool[,] stoneArr = ctx.StoneOnGrass;
        int mapW = ctx.MapWidth, mapH = ctx.MapHeight;
        var wallTypeBySite = new Dictionary<int, BuildingType>(sites.Count);
        foreach (var (id, wt) in sites)
            wallTypeBySite[id] = wt;

        foreach (var (site, x, y, stage, isWall, needTree, needStone, needHaul) in snapshot)
        {
            bool inBounds = (uint)x < (uint)mapW && (uint)y < (uint)mapH;
            bool hasTree = MaskHas(treeArr, x, y);
            bool hasStone = MaskHas(stoneArr, x, y);
            bool hasItems = GroundItemManager.Instance.HasItemsAt(x, y);
            bool hasWall = BuildingManager.Instance.HasBuildingAt(x, y)
                || (Game.UI.MapRenderer.Instance?.WallBuildManager?.IsWallAt(x, y) ?? false);
            if (hasWall && isWall)
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
                // Препятствие появилось на уже «чистой» клетке — вернуть флаг
                // и работу (дерево выросло/принесли вещи после расчистки).
                if (!needTree && hasTree && inBounds && MarkObstacleBack(site, x, y, Obstacle.Tree))
                    TreeJobManager.Instance.MarkTree(x, y);
                if (!needStone && hasStone && inBounds && MarkObstacleBack(site, x, y, Obstacle.Stone))
                    StoneJobManager.Instance.MarkStone(x, y, ctx.StoneOnGrass);
                if (!needHaul && hasItems && inBounds && MarkObstacleBack(site, x, y, Obstacle.Items))
                    RegisterHaulBatch(new List<(int X, int Y)> { (x, y) });
            }
            else if (stage == CellStage.Supply && isWall)
            {
                if (!BlueprintManager.Instance.IsBlueprintAt(x, y)
                    && !idx.HasJobAt(x, y, JobTypeId.BlueprintDelivery)
                    && !idx.HasJobAt(x, y, JobTypeId.Construction))
                {
                    BuildingType wt = wallTypeBySite.TryGetValue(site, out var w) ? w : BuildingType.WoodWall;
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

        // Достроенные здания: открыть зону сразу (не ждать, пока агенты сами
        // соберутся через кулдауны).
        if (completeSitesToOpen != null)
        {
            foreach (var (_, zoneCells) in completeSitesToOpen)
                ResetZoneCooldowns(zoneCells);
            lock (_lock)
            {
                foreach (var (siteId, _) in completeSitesToOpen)
                    if (_sites.TryGetValue(siteId, out var s))
                        s.WallsBuilt = true; // не открывать повторно
            }
        }

        // Promote/MaybeOpen — только для сайтов с живой расчисткой/снабжением.
        List<int> activeIds;
        lock (_lock) { activeIds = new List<int>(_activeClearSites); }
        foreach (int id in activeIds)
        {
            PromoteReadyCells(id);
            MaybeOpenBuilding(id);
        }
    }

    private enum Obstacle : byte { Tree, Stone, Items }

    // Вернуть флаг препятствия клетке (true — если реально обновили).
    private bool MarkObstacleBack(int siteId, int x, int y, Obstacle kind)
    {
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var s)
                || !s.Cells.TryGetValue((x, y), out var cs)
                || cs.Stage != CellStage.Clearing)
                return false;
            switch (kind)
            {
                case Obstacle.Tree:
                    if (cs.NeedTree) return false;
                    cs.NeedTree = true; break;
                case Obstacle.Stone:
                    if (cs.NeedStone) return false;
                    cs.NeedStone = true; break;
                default:
                    if (cs.NeedHaul) return false;
                    cs.NeedHaul = true; break;
            }
            return true;
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
                _zoneCellsGlobal.Remove(c);
            }
            DropSiteLocked(siteId, site);
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
    /// Горячий путь (каждый claim каждого агента) — поэтому сначала быстрая
    /// проверка «вообще нет активных сайтов» без спина лока.
    /// </summary>
    public bool IsStageAllowed(int x, int y, JobTypeId type)
    {
        CellStage stage;
        bool isWall;
        lock (_lock)
        {
            if (_sites.Count == 0)
                return true;
            if (!_cellToSite.TryGetValue((x, y), out int site)
                || !_sites.TryGetValue(site, out var s)
                || !s.Cells.TryGetValue((x, y), out var cs))
                return true;
            stage = cs.Stage;
            isWall = cs.IsWall;
        }
        switch (type)
        {
            case JobTypeId.TreeChopping:
            case JobTypeId.Mining:
                return stage == CellStage.Clearing;
            case JobTypeId.StockpileHauling:
                // Уборка мусора разрешена и на стенах, и на клетках зоны.
                return stage == CellStage.Clearing;
            case JobTypeId.BlueprintDelivery:
                return isWall && stage == CellStage.Supply;
            case JobTypeId.Construction:
                // Стройка — только когда ВСЯ стройка снабжена (шаг Building).
                // WaitingAll = клетка готова, но остальные нет — строить нельзя.
                return isWall && stage == CellStage.Building;
            default:
                return true;
        }
    }

    // ────────────────────────── ГЕЙТ ЗОНЫ ──────────────────────────
    // Ферма/склад внутри контура работает ТОЛЬКО когда все стены построены.
    // Кэш «клетка → (готов ли сайт, список зонных клеток)»: горячий путь
    // (CanAgentExecute + sweep индекса) ходит только в него, без аллокаций
    // и без толкания с Notify-вызовами. Обновление кэша — по TTL 250 мс
    // (стена строится ~игровые 4 часа, задержка незаметна).

    private sealed class GatewayInfo
    {
        public int SiteId;      // 0 = клетка вне стройки (разрешено всё)
        public bool Allowed;    // сайт завершён (все стены построены)
        public List<(int X, int Y)> ZoneCells; // клетки зоны того же сайта
        public long StampMs;    // Environment.TickCount64 последнего обновления
    }

    private const long GatewayTtlMs = 250;
    private readonly Dictionary<(int X, int Y), GatewayInfo> _gatewayCache = new(256);
    private readonly Queue<(int X, int Y)> _gatewayKeys = new(256);
    // Снимок списка сайтов для кэша (обновляется под _lock редко).
    private long _lastGatewayRefreshMs;

    /// <summary>
    /// Можно ли работать на этой клетке (ферма/посадка/сбор/уборка склада)?
    /// False — клетка внутри недостроенного здания. Вне стройки — всегда True.
    /// </summary>
    public bool IsZoneWorkAllowed(int x, int y)
    {
        if (TryGetGateway(x, y, out bool known, out _) && known)
            return known && TryGetGatewayAllowed(x, y);
        return true;
    }

    // Возвращает (есть ли кэш-запись, разрешена ли работа).
    private bool TryGetGatewayAllowed(int x, int y)
    {
        lock (_lock)
        {
            return _gatewayCache.TryGetValue((x, y), out var info) && (info.SiteId == 0 || info.Allowed);
        }
    }

    /// <summary>
    /// Обслуживание кэша гейта: найти запись или обновить её по事实 из _sites.
    /// Вызывается из hot path; сам lock держит микросекунды (только чтение
    /// словарей пайплайна, внешних вызовов нет).
    /// </summary>
    private bool TryGetGateway(int x, int y, out bool allowed, out List<(int X, int Y)> zoneCells)
    {
        allowed = true;
        zoneCells = null;
        long nowMs = System.Environment.TickCount64;
        lock (_lock)
        {
            var key = (x, y);
            if (_gatewayCache.TryGetValue(key, out var info))
            {
                if (nowMs - info.StampMs < GatewayTtlMs)
                {
                    allowed = info.SiteId == 0 || info.Allowed;
                    zoneCells = info.ZoneCells;
                    return true;
                }
            }
            else
            {
                info = new GatewayInfo();
                _gatewayCache[key] = info;
                _gatewayKeys.Enqueue(key);
                // Защита от роста кэша на бесконечном числе посещаемых клеток.
                while (_gatewayKeys.Count > 4096)
                {
                    var old = _gatewayKeys.Dequeue();
                    if (!Equals(old, key))
                        _gatewayCache.Remove(old);
                }
            }
            info.StampMs = nowMs;
            if (_cellToSite.TryGetValue(key, out int site) && _sites.TryGetValue(site, out var s))
            {
                info.SiteId = site;
                info.Allowed = s.WallsBuilt;
                info.ZoneCells = s.ZoneCells;
            }
            else
            {
                info.SiteId = 0;
                info.Allowed = true;
                info.ZoneCells = null;
            }
            allowed = info.Allowed;
            zoneCells = info.ZoneCells;
            return true;
        }
    }

    // Открыть зону: сброс кулдаунов диспетчера по клеткам зоны (вне _lock).
    private void ResetZoneCooldowns(List<(int X, int Y)> zoneCells)
    {
        if (zoneCells == null) return;
        var idx = JobDispatcher.Instance.JobIndex;
        foreach (var (x, y) in zoneCells)
        {
            idx.ResetCooldownAt(x, y, JobTypeId.Farming);
            idx.ResetCooldownAt(x, y, JobTypeId.Planting);
            idx.ResetCooldownAt(x, y, JobTypeId.Harvesting);
            idx.ResetCooldownAt(x, y, JobTypeId.StockpileHauling);
        }
    }

    // Момент, когда здание достроено: немедленно выводим клетки зоны из
    // диспетчерского кулдауна (агенты берут работу за ~2 сек вместо дождика
    // reconcile-тик 5 сек / аудита 30 сек).
    private void OpenZoneJobs(int wallX, int wallY)
    {
        // Сайт к этому моменту уже удалён из _sites — координаты зоны берём
        // из кэша гейтов (запись живёт до TTL, ссылка на List сохранена).
        if (!TryGetGateway(wallX, wallY, out _, out var zoneCells))
            return;
        ResetZoneCooldowns(zoneCells);
        // Инвалидируем кэш по клеткам зоны: следующий вопрос должен увидеть
        // «завершено» мгновенно, а не через TTL.
        lock (_lock)
        {
            if (zoneCells != null)
                foreach (var c in zoneCells)
                    _gatewayCache.Remove(c);
            _gatewayCache.Remove((wallX, wallY));
        }
    }

    /// <summary>Сколько процентов ресурсов привезено на стройку (для окна/подсказок).</summary>
    public int GetSupplyPercent(int x, int y)
    {
        lock (_lock)
        {
            if (!_cellToSite.TryGetValue((x, y), out int site)
                || !_sites.TryGetValue(site, out var s))
                return 100;
            int ready = 0, total = 0;
            foreach (var kv in s.Cells)
            {
                if (!kv.Value.IsWall) continue;
                total++;
                if (kv.Value.Stage == CellStage.WaitingAll || kv.Value.Stage == CellStage.Building)
                    ready++;
            }
            if (total == 0) return 100;
            return ready * 100 / total;
        }
    }
}
