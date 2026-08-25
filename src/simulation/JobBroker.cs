using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

public sealed class JobBroker
{
    public static JobBroker Instance { get; } = new();

    public int ActiveBlueprintCount => JobDispatcher.Instance.JobIndex.TotalCount;

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
        BlueprintManager.Instance.AddDeliveredLogs(x, y, count);

        if (JobDispatcher.Instance.JobIndex.TryAddJobProgress(x, y, JobTypeId.BlueprintDelivery, count, out bool isCompleted, out var job))
        {
            if (isCompleted)
            {
                JobDispatcher.Instance.UnregisterJob(job.Id);

                // Создаём задачу постройки строго после доставки всех материалов
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
                    WorkDuration = 10.0f
                });
            }
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
            WorkDuration = 10.0f
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
                WorkDuration = 10.0f
            });
        }
        JobDispatcher.Instance.RegisterBatch(batch);
    }

    public void UnregisterFarmPlot(int x, int y) =>
        JobDispatcher.Instance.UnregisterJobByPos(x, y, JobTypeId.Farming);

    public void UnregisterFarmPlotBatch(List<(int X, int Y)> plots) =>
        JobDispatcher.Instance.UnregisterBatchByPositions(plots, JobTypeId.Farming);
}