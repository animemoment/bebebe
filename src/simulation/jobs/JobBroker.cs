using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

public sealed class JobBroker
{
    public static JobBroker Instance { get; } = new();

    public int ActiveBlueprintCount => JobDispatcher.Instance.JobIndex.TotalCount;

    /// <summary>
    /// Находит клетку стояния рядом с целью (не вода, не стена, не дерево).
    /// Нужно, чтобы рабочий не стоял прямо в клетке-цели (будущей стене/грядке),
    /// из-за чего он зависал «внутри» механики и толпился в стене.
    /// Возвращает true, если найдена свободная соседняя клетка, иначе false
    /// (тогда вызывающий должен использовать саму клетку цели).
    /// </summary>
    // Направления соседей — static (без new int[] на вызов из горячего OnStart).
    private static readonly int[] StandDx = { 0, 0, -1, 1 };
    private static readonly int[] StandDy = { -1, 1, 0, 0 };

    public static bool FindStandPosition(int tx, int ty, SimulationContext ctx, out (int X, int Y) stand)
    {
        if (ctx == null)
        {
            stand = (tx, ty);
            return false;
        }

        // 1. Кардинальные соседи (расстояние 1)
        for (int i = 0; i < 4; i++)
        {
            int nx = tx + StandDx[i];
            int ny = ty + StandDy[i];
            if (IsWalkable(nx, ny, ctx))
            {
                stand = (nx, ny);
                return true;
            }
        }

        // 2. Диагональные соседи (расстояние 1)
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = tx + dx;
                int ny = ty + dy;
                if (IsWalkable(nx, ny, ctx))
                {
                    stand = (nx, ny);
                    return true;
                }
            }
        }

        // 3. Расширенный радиус до 3 клеток
        for (int r = 2; r <= 3; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (IsWalkable(nx, ny, ctx))
                    {
                        stand = (nx, ny);
                        return true;
                    }
                }
            }
        }

        stand = (tx, ty);
        return false;
    }

    private static bool IsWalkable(int x, int y, SimulationContext ctx)
    {
        if ((uint)x >= (uint)ctx.MapWidth || (uint)y >= (uint)ctx.MapHeight)
            return false;
        if (ctx.SolidWalls != null && ctx.SolidWalls[x, y])
            return false;
        if (ctx.TreeOnGrass != null && ctx.TreeOnGrass[x, y])
            return false;
        if (ctx.StoneOnGrass != null && ctx.StoneOnGrass[x, y])
            return false;
        if (ctx.Ground != null)
        {
            var g = ctx.Ground[x, y];
            if (g == TileType.Water || g == TileType.Mountain)
                return false;
        }
        return true;
    }

    public void RegisterTreeChop(int x, int y, SimulationContext ctx = null)
    {
        var standPos = (x, y);
        if (ctx != null)
        {
            GridHelper.TryFindAdjacentWalkable(x, y, ctx.Ground, ctx.SolidWalls, ctx.TreeOnGrass, out standPos);
        }

        JobDispatcher.Instance.RegisterJob(new JobData
        {
            TypeId = JobTypeId.TreeChopping,
            ExecutionType = JobExecutionType.Stationary,
            PriorityTier = JobPriorityTier.TreeChopping,
            TargetX = x,
            TargetY = y,
            StandX = standPos.Item1,
            StandY = standPos.Item2,
            MaxWorkers = 1,
            WorkDuration = 8.0f
        });
        // §119 P1: пуш роутеру зон (пуш = уведомление; реестр — источник истины).
        int treeJobId = JobDispatcher.Instance.JobIndex.TryGetJobIdByPos(x, y, JobTypeId.TreeChopping, out _);
        if (treeJobId >= 0)
            ZoneJobRouter.Instance.Push(JobTypeId.TreeChopping, x, y, treeJobId);
    }

    public void RegisterTreeChopBatch(List<(int X, int Y)> trees, SimulationContext ctx = null)
    {
        if (trees == null || trees.Count == 0) return;

        var batch = new List<JobData>(trees.Count);
        foreach (var (x, y) in trees)
        {
            var standPos = (x, y);
            if (ctx != null)
            {
                GridHelper.TryFindAdjacentWalkable(x, y, ctx.Ground, ctx.SolidWalls, ctx.TreeOnGrass, out standPos);
            }

            batch.Add(new JobData
            {
                TypeId = JobTypeId.TreeChopping,
                ExecutionType = JobExecutionType.Stationary,
                PriorityTier = JobPriorityTier.TreeChopping,
                TargetX = x,
                TargetY = y,
                StandX = standPos.Item1,
                StandY = standPos.Item2,
                MaxWorkers = 1,
                WorkDuration = 8.0f
            });
        }

        JobDispatcher.Instance.RegisterBatch(batch);

        // §119 P1: батч-пуш роутеру зон (реестр — источник истины, id по позиции).
        var treeIndex = JobDispatcher.Instance.JobIndex;
        foreach (var (x, y) in trees)
        {
            int jid = treeIndex.TryGetJobIdByPos(x, y, JobTypeId.TreeChopping, out _);
            if (jid > 0)
                ZoneJobRouter.Instance.Push(JobTypeId.TreeChopping, x, y, jid);
        }
    }

    public void UnregisterTreeChop(int x, int y) =>
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.TreeChopping);

    public void UnregisterTreeChopBatch(List<(int X, int Y)> trees) =>
        JobDispatcher.Instance.UnregisterBatchByPositions(trees, JobTypeId.TreeChopping);

    /// <summary>
    /// Добыча камня (Mining, россыпи StoneJobManager). Stand — соседняя свободная
    /// клетка (не камень/дерево/стена), иначе рабочий встаёт в саму россыпь.
    /// </summary>
    public void RegisterStoneMine(int x, int y, SimulationContext ctx = null)
    {
        var standPos = (x, y);
        if (ctx != null)
        {
            GridHelper.TryFindAdjacentWalkable(x, y, ctx.Ground, ctx.SolidWalls, ctx.TreeOnGrass, ctx.StoneOnGrass, out standPos);
        }

        JobDispatcher.Instance.RegisterJob(new JobData
        {
            TypeId = JobTypeId.Mining,
            ExecutionType = JobExecutionType.Stationary,
            PriorityTier = JobPriorityTier.TreeChopping,
            TargetX = x,
            TargetY = y,
            StandX = standPos.Item1,
            StandY = standPos.Item2,
            MaxWorkers = 1,
            WorkDuration = 8.0f
        });
        // §119 P1: пуш роутеру зон.
        int mineJobId = JobDispatcher.Instance.JobIndex.TryGetJobIdByPos(x, y, JobTypeId.Mining, out _);
        if (mineJobId >= 0)
            ZoneJobRouter.Instance.Push(JobTypeId.Mining, x, y, mineJobId);
    }

    public void RegisterStoneMineBatch(List<(int X, int Y)> stones, SimulationContext ctx = null)
    {
        if (stones == null || stones.Count == 0) return;

        var batch = new List<JobData>(stones.Count);
        foreach (var (x, y) in stones)
        {
            var standPos = (x, y);
            if (ctx != null)
            {
                GridHelper.TryFindAdjacentWalkable(x, y, ctx.Ground, ctx.SolidWalls, ctx.TreeOnGrass, ctx.StoneOnGrass, out standPos);
            }

            batch.Add(new JobData
            {
                TypeId = JobTypeId.Mining,
                ExecutionType = JobExecutionType.Stationary,
                PriorityTier = JobPriorityTier.TreeChopping,
                TargetX = x,
                TargetY = y,
                StandX = standPos.Item1,
                StandY = standPos.Item2,
                MaxWorkers = 1,
                WorkDuration = 8.0f
            });
        }

        JobDispatcher.Instance.RegisterBatch(batch);

        // §119 P1: батч-пуш роутеру зон.
        var stoneIndex = JobDispatcher.Instance.JobIndex;
        foreach (var (x, y) in stones)
        {
            int jid = stoneIndex.TryGetJobIdByPos(x, y, JobTypeId.Mining, out _);
            if (jid > 0)
                ZoneJobRouter.Instance.Push(JobTypeId.Mining, x, y, jid);
        }
    }

    public void UnregisterStoneMine(int x, int y) =>
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Mining);

    public void UnregisterStoneMineBatch(List<(int X, int Y)> stones) =>
        JobDispatcher.Instance.UnregisterBatchByPositions(stones, JobTypeId.Mining);

    public void RegisterStockpileHaul(int x, int y)
    {
        JobDispatcher.Instance.RegisterJob(new JobData
        {
            TypeId = JobTypeId.StockpileHauling,
            ExecutionType = JobExecutionType.Hauling,
            PriorityTier = JobPriorityTier.StockpileHauling,
            SourceX = x,
            SourceY = y,
            TargetX = x,
            TargetY = y,
            StandX = x,
            StandY = y,
            MaxWorkers = 1
        });
        // §119 P1: пуш роутеру зон.
        int haulJobId = JobDispatcher.Instance.JobIndex.TryGetJobIdByPos(x, y, JobTypeId.StockpileHauling, out _);
        if (haulJobId >= 0)
            ZoneJobRouter.Instance.Push(JobTypeId.StockpileHauling, x, y, haulJobId);
    }

    /// <summary>
    /// Гарантирует, что для каждого предмета, лежащего вне складской зоны,
    /// зарегистрирована haul-работа «отнести на склад». Закрывает пробел:
    /// склад может быть нарисован ПОСЛЕ того, как брёвна/зерно выпали на землю —
    /// в этом случае SpawnItems не создал работу, а sweep её добьёт.
    /// </summary>
    // P0-2: wall-clock троттлинг sweep — не чаще раза в 2с реального времени.
    // Sweep рос 0.1→23 мс total: скан Ground-словаря + IsZoneTile на кандидата
    // на каждый проход. Опоздание регистрации на 2с безвредно (работы и так
    // копятся быстрее разбора — см. рост Dispatch ×135).
    private long _lastSweepWallTicks;
    private static readonly TimeSpan SweepMinInterval = TimeSpan.FromSeconds(2);
    // P0-2: переиспользуемый буфер ТОЛЬКО для sim-потока. Sweep зовётся и из
    // главного потока (AddZoneTile при рисовании склада) — там свой локальный
    // список (редкий вызов, аллокация не важна), общий буфер означал бы гонку.
    private readonly List<(int X, int Y)> _sweepTiles = new(128);

    public void SweepStockpileHaulJobs()
    {
        if (!StockpileManager.Instance.HasFreeSpace)
        {
            return;
        }

        long now = DateTime.UtcNow.Ticks;
        if (now - _lastSweepWallTicks < SweepMinInterval.Ticks)
            return;
        _lastSweepWallTicks = now;

        // P0-2: кап 128 вместо 512 — проход дешевле в 4 раза; хвост дождётся
        // следующего прохода через 2с. Skip, если работ и так навалом:
        // регистрировать новые поверх тысяч unclaimed бессмысленно.
        if (JobDispatcher.Instance.JobIndex.UnclaimedCount > 2048)
            return;

        // Главный поток (рисование склада) — со своим списком, без общего буфера.
        bool isSimThread = System.Threading.Thread.CurrentThread.IsThreadPoolThread
            || System.Threading.Thread.CurrentThread.IsBackground;
        var itemTiles = isSimThread ? _sweepTiles
            : new List<(int X, int Y)>(128);
        GroundItemManager.Instance.CollectItemTilesOutsideStockpiles(itemTiles, 128);

        if (itemTiles.Count == 0)
        {
            return;
        }

        // Батч: N× lock(_registerLock) → 1 lock. Дедуп по _posMap внутри
        // RegisterBatch, семантика та же (stale/двойные — no-op).
        var batch = new System.Collections.Generic.List<JobData>(itemTiles.Count);
        for (int i = 0; i < itemTiles.Count; i++)
        {
            var (x, y) = itemTiles[i];
            batch.Add(new JobData
            {
                TypeId = JobTypeId.StockpileHauling,
                ExecutionType = JobExecutionType.Hauling,
                PriorityTier = JobPriorityTier.StockpileHauling,
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
        // §119 P1: батч-пуш haul-работ роутеру зон (sweep-регистрации тоже идут через роутер).
        var haulIndex = JobDispatcher.Instance.JobIndex;
        for (int i = 0; i < itemTiles.Count; i++)
        {
            var (x, y) = itemTiles[i];
            int jid = haulIndex.TryGetJobIdByPos(x, y, JobTypeId.StockpileHauling, out _);
            if (jid > 0)
                ZoneJobRouter.Instance.Push(JobTypeId.StockpileHauling, x, y, jid);
        }
    }

    public void RegisterBlueprint(int x, int y, BuildingType type, int targetLogs)
    {
        DemandRegistry.Instance.RegisterDemand(DemandType.Blueprint, x, y, ItemId.Log, targetLogs);
        JobDispatcher.Instance.RegisterJob(new JobData
        {
            TypeId = JobTypeId.BlueprintDelivery,
            ExecutionType = JobExecutionType.Hauling,
            PriorityTier = JobPriorityTier.BlueprintSupply,
            TargetX = x,
            TargetY = y,
            StandX = x,
            StandY = y,
            TargetItemId = ItemId.Log,
            TargetItemCount = targetLogs,
            MaxWorkers = 1
        });
        // §119 P1: пуш роутеру зон.
        int bpJobId = JobDispatcher.Instance.JobIndex.TryGetJobIdByPos(x, y, JobTypeId.BlueprintDelivery, out _);
        if (bpJobId >= 0)
            ZoneJobRouter.Instance.Push(JobTypeId.BlueprintDelivery, x, y, bpJobId);
        JobDispatcher.Instance.TriggerImmediateDispatch();
    }

    public void RegisterBlueprintBatch(List<(int X, int Y)> cells, BuildingType type, int targetLogs)
    {
        if (cells == null || cells.Count == 0) return;
        var batch = new List<JobData>(cells.Count);
        foreach (var (x, y) in cells)
        {
            DemandRegistry.Instance.RegisterDemand(DemandType.Blueprint, x, y, ItemId.Log, targetLogs);
            batch.Add(new JobData
            {
                TypeId = JobTypeId.BlueprintDelivery,
                ExecutionType = JobExecutionType.Hauling,
                PriorityTier = JobPriorityTier.BlueprintSupply,
                TargetX = x,
                TargetY = y,
                StandX = x,
                StandY = y,
                TargetItemId = ItemId.Log,
                TargetItemCount = targetLogs,
                MaxWorkers = 1
            });
        }
        JobDispatcher.Instance.RegisterBatch(batch);
        // §119 P1: батч-пуш роутеру зон.
        var bpIndex = JobDispatcher.Instance.JobIndex;
        foreach (var (x, y) in cells)
        {
            int jid = bpIndex.TryGetJobIdByPos(x, y, JobTypeId.BlueprintDelivery, out _);
            if (jid > 0)
                ZoneJobRouter.Instance.Push(JobTypeId.BlueprintDelivery, x, y, jid);
        }
        JobDispatcher.Instance.TriggerImmediateDispatch();
    }

    public void UnregisterBlueprint(int x, int y)
    {
        DemandRegistry.Instance.UnregisterDemand(x, y, ItemId.Log);
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.BlueprintDelivery);
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Construction);
    }

    public void UnregisterBlueprintBatch(List<(int X, int Y)> cells)
    {
        if (cells != null)
        {
            foreach (var (x, y) in cells)
            {
                DemandRegistry.Instance.UnregisterDemand(x, y, ItemId.Log);
            }
        }
        JobDispatcher.Instance.UnregisterBatchByPositions(cells, JobTypeId.BlueprintDelivery);
        JobDispatcher.Instance.UnregisterBatchByPositions(cells, JobTypeId.Construction);
    }

    public void DeliverLogsToBlueprint(int x, int y, int count)
    {
        // Сначала прогресс в индексе (под _registerLock): если чертежа уже нет
        // (завершён/удалён конкурентом) — TryAdd вернёт false и логи НЕ должны
        // уходить в BlueprintManager, иначе они исчезнут без возврата.
        // Возвращаем перепоставку на землю рядом, чтобы ничего не терять.
        // #2: кламп — носильщик несёт до 29 брёвен при target 10–25; излишек
        // складываем рядом через FindStandPosition, а не испаряем.
        if (JobDispatcher.Instance.JobIndex.TryAddJobProgressClamped(x, y, JobTypeId.BlueprintDelivery, count, out int accepted, out bool isCompleted, out var job))
        {
            if (accepted > 0)
            {
                DemandRegistry.Instance.UpdateDelivered(x, y, ItemId.Log, accepted);
                ConstructionManager.Instance.DeliverMaterial(x, y, ItemId.Log, accepted, out _, out _);
            }
            int overflow = count - accepted;
            if (overflow > 0)
            {
                // #2: излишек — на землю рядом (НЕ на клетку цели: там будущая
                // стройка, а Construction.Can=!HasItemsAt заблокировал бы её).
                // Ищем первую свободную соседнюю клетку локально: HasItemsAt —
                // короткий Ground-lock, без ctx (менеджерный уровень).
                (int X, int Y) spill = (x, y);
                for (int oy = -2; oy <= 2; oy++)
                {
                    for (int ox = -2; ox <= 2; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        if (!GroundItemManager.Instance.HasItemsAt(x + ox, y + oy))
                        {
                            spill = (x + ox, y + oy);
                            oy = 3;
                            break;
                        }
                    }
                }
                GroundItemManager.Instance.SpawnItems(spill.X, spill.Y, ItemId.Log, overflow);
            }
            if (isCompleted && accepted > 0)
            {
                JobDispatcher.Instance.UnregisterJob(job.Id);

                // Этап 2→3 пайплайна стройки: клетка в Building (идемпотентно
                // по клетке — счётчиков больше нет).
                ConstructionManager.Instance.NotifySupplyDone(x, y);

                if (!BuildConfig.WaitForAllSiteResources)
                {
                    RegisterConstructionAt(x, y);
                }
            }
        }
        else
        {
            GroundItemManager.Instance.SpawnItems(x, y, ItemId.Log, count);
        }
    }

    /// <summary>
    /// Создать Construction-работу на клетке (этап 3 пайплайна + reconcile).
    /// Дедуп по индексу: повтор — no-op.
    /// </summary>
    public void RegisterConstructionAt(int x, int y)
    {
        if (JobDispatcher.Instance.JobIndex.HasJobAt(x, y, JobTypeId.Construction))
            return;
        JobDispatcher.Instance.RegisterJob(new JobData
        {
            TypeId = JobTypeId.Construction,
            ExecutionType = JobExecutionType.Stationary,
            PriorityTier = JobPriorityTier.Construction,
            TargetX = x,
            TargetY = y,
            StandX = x,
            StandY = y,
            MaxWorkers = 1,
            WorkDuration = WorldTime.SecondsPerHour * 4f
        });
        // §119 P1: пуш роутеру зон (ConstructionManager — внешняя точка, спека task-15).
        int consJobId = JobDispatcher.Instance.JobIndex.TryGetJobIdByPos(x, y, JobTypeId.Construction, out _);
        if (consJobId >= 0)
            ZoneJobRouter.Instance.Push(JobTypeId.Construction, x, y, consJobId);
    }

    public void RegisterFarmPlot(int x, int y)
    {
        JobDispatcher.Instance.RegisterJob(new JobData
        {
            TypeId = JobTypeId.Farming,
            ExecutionType = JobExecutionType.Stationary,
            PriorityTier = JobPriorityTier.Farming,
            TargetX = x,
            TargetY = y,
            StandX = x,
            StandY = y,
            MaxWorkers = 1,
            WorkDuration = Game.Simulation.Jobs.FarmingJobHandler.TillDuration
        });
        // §119 P1: пуш роутеру зон.
        int farmJobId = JobDispatcher.Instance.JobIndex.TryGetJobIdByPos(x, y, JobTypeId.Farming, out _);
        if (farmJobId >= 0)
            ZoneJobRouter.Instance.Push(JobTypeId.Farming, x, y, farmJobId);
    }

    public void RegisterFarmPlotBatch(List<(int X, int Y)> plots)
    {
        if (plots == null || plots.Count == 0) return;
        var batch = new List<JobData>(plots.Count);
        foreach (var (x, y) in plots)
        {
            batch.Add(new JobData
            {
                TypeId = JobTypeId.Farming,
                ExecutionType = JobExecutionType.Stationary,
                PriorityTier = JobPriorityTier.Farming,
                TargetX = x,
                TargetY = y,
                StandX = x,
                StandY = y,
                MaxWorkers = 1,
                WorkDuration = Game.Simulation.Jobs.FarmingJobHandler.TillDuration
            });
        }
        JobDispatcher.Instance.RegisterBatch(batch);
        // §119 P1: батч-пуш роутеру зон.
        var farmIndex = JobDispatcher.Instance.JobIndex;
        foreach (var (x, y) in plots)
        {
            int jid = farmIndex.TryGetJobIdByPos(x, y, JobTypeId.Farming, out _);
            if (jid > 0)
                ZoneJobRouter.Instance.Push(JobTypeId.Farming, x, y, jid);
        }
    }

    public void UnregisterFarmPlot(int x, int y) =>
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Farming);

    public void UnregisterFarmPlotBatch(List<(int X, int Y)> plots) =>
        JobDispatcher.Instance.UnregisterBatchByPositions(plots, JobTypeId.Farming);

    public void RegisterPlanting(int x, int y, int zoneId)
    {
        JobDispatcher.Instance.RegisterJob(new JobData
        {
            TypeId = JobTypeId.Planting,
            ExecutionType = JobExecutionType.Hauling,
            PriorityTier = JobPriorityTier.Farming,
            TargetX = x,
            TargetY = y,
            StandX = x,
            StandY = y,
            MaxWorkers = 1,
            WorkDuration = 10.0f
        });
        // §119 P1: пуш роутеру зон.
        int plantJobId = JobDispatcher.Instance.JobIndex.TryGetJobIdByPos(x, y, JobTypeId.Planting, out _);
        if (plantJobId >= 0)
            ZoneJobRouter.Instance.Push(JobTypeId.Planting, x, y, plantJobId);
    }

    public void UnregisterPlanting(int x, int y) =>
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Planting);

    public void RegisterHarvest(int x, int y)
    {
        JobDispatcher.Instance.RegisterJob(new JobData
        {
            TypeId = JobTypeId.Harvesting,
            ExecutionType = JobExecutionType.Stationary,
            PriorityTier = JobPriorityTier.Farming,
            TargetX = x,
            TargetY = y,
            StandX = x,
            StandY = y,
            MaxWorkers = 1,
            WorkDuration = 5.0f
        });
        // §119 P1: пуш роутеру зон.
        int harvJobId = JobDispatcher.Instance.JobIndex.TryGetJobIdByPos(x, y, JobTypeId.Harvesting, out _);
        if (harvJobId >= 0)
            ZoneJobRouter.Instance.Push(JobTypeId.Harvesting, x, y, harvJobId);
    }

    public void UnregisterHarvest(int x, int y) =>
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Harvesting);
}