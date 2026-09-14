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
    public static bool FindStandPosition(int tx, int ty, SimulationContext ctx, out (int X, int Y) stand)
    {
        int[] dx = { 0, 0, -1, 1 };
        int[] dy = { -1, 1, 0, 0 };
        for (int i = 0; i < 4; i++)
        {
            int nx = tx + dx[i];
            int ny = ty + dy[i];
            if ((uint)nx < (uint)ctx.MapWidth && (uint)ny < (uint)ctx.MapHeight &&
                ctx.Ground[nx, ny] == TileType.Grass &&
                !ctx.SolidWalls[nx, ny] &&
                !ctx.TreeOnGrass[nx, ny])
            {
                stand = (nx, ny);
                return true;
            }
        }

        stand = (tx, ty);
        return false;
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
    }

    public void UnregisterTreeChop(int x, int y) =>
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.TreeChopping);

    public void UnregisterTreeChopBatch(List<(int X, int Y)> trees) =>
        JobDispatcher.Instance.UnregisterBatchByPositions(trees, JobTypeId.TreeChopping);

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
    }

    /// <summary>
    /// Гарантирует, что для каждого предмета, лежащего вне складской зоны,
    /// зарегистрирована haul-работа «отнести на склад». Закрывает пробел:
    /// склад может быть нарисован ПОСЛЕ того, как брёвна/зерно выпали на землю —
    /// в этом случае SpawnItems не создал работу, а sweep её добьёт.
    /// </summary>
    public void SweepStockpileHaulJobs()
    {
        if (!StockpileManager.Instance.HasFreeSpace)
            return;

        // Локальный список: Sweep вызывается и из симуляционного потока
        // (каждую игровую секунду), и из главного (при рисовании склада) —
        // общий переиспользуемый буфер означал бы гонку.
        var itemTiles = new List<(int X, int Y)>(64);
        GroundItemManager.Instance.CollectItemTilesOutsideStockpiles(itemTiles, 512);

        if (itemTiles.Count == 0)
            return;

        for (int i = 0; i < itemTiles.Count; i++)
        {
            var (x, y) = itemTiles[i];
            // RegisterStockpileHaul дедуплицирует по (x, y, тип) через _posMap,
            // поэтому безопасно вызывать и для уже зарегистрированных работ.
            RegisterStockpileHaul(x, y);
        }
    }

    public void RegisterBlueprint(int x, int y, BuildingType type, int targetLogs)
    {
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
    }

    public void RegisterBlueprintBatch(List<(int X, int Y)> cells, BuildingType type, int targetLogs)
    {
        if (cells == null || cells.Count == 0) return;
        var batch = new List<JobData>(cells.Count);
        foreach (var (x, y) in cells)
        {
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
    }

    public void UnregisterBlueprint(int x, int y)
    {
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.BlueprintDelivery);
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Construction);
    }

    public void UnregisterBlueprintBatch(List<(int X, int Y)> cells)
    {
        JobDispatcher.Instance.UnregisterBatchByPositions(cells, JobTypeId.BlueprintDelivery);
        JobDispatcher.Instance.UnregisterBatchByPositions(cells, JobTypeId.Construction);
    }

    public void DeliverLogsToBlueprint(int x, int y, int count)
    {
        // Сначала прогресс в индексе (под _registerLock): если чертежа уже нет
        // (завершён/удалён конкурентом) — TryAdd вернёт false и логи НЕ должны
        // уходить в BlueprintManager, иначе они исчезнут без возврата.
        // Возвращаем перепоставку на землю рядом, чтобы ничего не терять.
        if (JobDispatcher.Instance.JobIndex.TryAddJobProgress(x, y, JobTypeId.BlueprintDelivery, count, out bool isCompleted, out var job))
        {
            BlueprintManager.Instance.AddDeliveredLogs(x, y, count);
            if (isCompleted)
            {
                JobDispatcher.Instance.UnregisterJob(job.Id);

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
            }
        }
        else
        {
            GroundItemManager.Instance.SpawnItems(x, y, ItemId.Log, count);
        }
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
    }

    public void UnregisterHarvest(int x, int y) =>
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Harvesting);
}