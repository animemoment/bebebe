using System;
using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

public sealed class TreeChoppingJobHandler : IJobHandler
{
    private const float ChopDuration = WorldTime.SecondsPerHour * 6f;
    private const float ReachDist = 20.0f;

    public JobTypeId TypeId => JobTypeId.TreeChopping;
    public JobExecutionType ExecutionType => JobExecutionType.Stationary;
    public JobPriorityTier DefaultPriority => JobPriorityTier.TreeChopping;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        if (ctx?.TreeOnGrass == null)
            return false;
        if ((uint)job.TargetX >= (uint)ctx.MapWidth || (uint)job.TargetY >= (uint)ctx.MapHeight)
            return false;
        if (!ConstructionManager.Instance.IsStageAllowed(job.TargetX, job.TargetY, JobTypeId.TreeChopping))
            return false;
        return ctx.TreeOnGrass[job.TargetX, job.TargetY];
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        pool.WaypointCount[agentIndex] = 0;
        pool.WaypointIndex[agentIndex] = 0;

        pool.States[agentIndex] = AgentState.MovingToSource;
        pool.TargetCellX[agentIndex] = job.TargetX;
        pool.TargetCellY[agentIndex] = job.TargetY;
        pool.TargetPositionX[agentIndex] = job.StandX * ctx.TileSize + 32f;
        pool.TargetPositionY[agentIndex] = job.StandY * ctx.TileSize + 32f;
        // P7б §119: продолжаем с per-cell прогресса (прерывание ≠ сброс на ноль).
        pool.WorkProgress[agentIndex] =
            TreeJobManager.Instance.ChopProgress.GetFraction(job.TargetX, job.TargetY) * ChopDuration;
        pool.StuckTimer[agentIndex] = 0f;
    }

    public void ExecuteParallel(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        var state = pool.States[agentIndex];
        if (state == AgentState.MovingToSource)
        {
            Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);
            ctx.Movement.MoveTowards(agentIndex, target, ReachDist, deltaTime, pool, ctx);
        }
        else if (state == AgentState.Working)
        {
            pool.WorkProgress[agentIndex] += deltaTime;
            WorkProgressTracker.Instance.ReportFraction(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex],
                pool.WorkProgress[agentIndex] / ChopDuration);
        }
    }

    public void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        var state = pool.States[agentIndex];
        int tx = pool.TargetCellX[agentIndex];
        int ty = pool.TargetCellY[agentIndex];

        if (state == AgentState.MovingToSource)
        {
            Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);
            if ((pool.GetPosition(agentIndex) - target).Length() <= ReachDist)
            {
                pool.States[agentIndex] = AgentState.Working;
                pool.StuckTimer[agentIndex] = 0f;
            }
            else if (pool.StuckTimer[agentIndex] >= 3.0f)
            {
                JobDispatcher.Instance.ReleaseJobWorkerForStuck(agentIndex, pool, ctx);
            }
        }
        else if (state == AgentState.Working && pool.WorkProgress[agentIndex] >= ChopDuration)
        {
            ctx.TreeOnGrass[tx, ty] = false;
            TreeJobManager.Instance.CompleteTree(tx, ty);
            WorkProgressTracker.Instance.Clear(tx, ty);

            // Этап 1 пайплайна стройки и фермы: дерево убрано
            ConstructionManager.Instance.NotifyTreeCleared(tx, ty);
            FarmJobManager.Instance.NotifyTreeCleared(tx, ty);

            int dropCount = ParallelRng.Next(21, 39);

            // Если на клетке находится стройплощадка, требующая брёвен — сразу направляем дерево в стройку
            if (ConstructionManager.Instance.TryGetSite(tx, ty, out var site) && site.Stage == ConstructionStage.Supplying)
            {
                int needed = site.Definition.TotalRequiredItems - site.TotalDelivered;
                int deliver = Math.Min(needed, dropCount);
                if (deliver > 0)
                {
                    JobBroker.Instance.DeliverLogsToBlueprint(tx, ty, deliver);
                    dropCount -= deliver;
                }
            }

            // Оставшиеся брёвна: если клетка занята стройкой или грядкой, выгружаем на соседнюю свободную клетку
            (int X, int Y) dropPos = (tx, ty);
            if (site != null || FarmJobManager.Instance.IsPlotMarked(tx, ty) || FarmJobManager.Instance.IsGardenBed(tx, ty))
            {
                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        int nx = tx + ox, ny = ty + oy;
                        if (nx >= 0 && ny >= 0 && nx < ctx.MapWidth && ny < ctx.MapHeight &&
                            ctx.Ground[nx, ny] == TileType.Grass &&
                            !ctx.SolidWalls[nx, ny] &&
                            !ConstructionManager.Instance.IsSiteAt(nx, ny) &&
                            !FarmJobManager.Instance.IsPlotMarked(nx, ny))
                        {
                            dropPos = (nx, ny);
                            oy = 2;
                            break;
                        }
                    }
                }
            }

            if (dropCount > 0)
            {
                GroundItemManager.Instance.SpawnItems(dropPos.X, dropPos.Y, ItemId.Log, dropCount);
            }

            int treeJobId = pool.CurrentJobId[agentIndex];
            if (treeJobId != -1)
            {
                if (!JobDispatcher.Instance.TryUnregisterJob(treeJobId))
                    JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(treeJobId);
            }
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
            pool.States[agentIndex] = AgentState.Idle;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
        }
    }

    public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        // P7б §119: прерывание (нужды/stuck/отмена/перераздача) сохраняет долю
        // готовности в клетку — следующий агент продолжит с неё, а не с нуля.
        TreeJobManager.Instance.ChopProgress.SaveFraction(
            pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex],
            pool.WorkProgress[agentIndex] / ChopDuration);
        WorkProgressTracker.Instance.Clear(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex]);
    }
}
