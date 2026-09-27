using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

public sealed class ConstructionJobHandler : IJobHandler
{
    private static float BuildDuration => BuildConfig.BuildDurationSec;
    private static float ReachDist => BuildConfig.ReachDist;

    public JobTypeId TypeId => JobTypeId.Construction;
    public JobExecutionType ExecutionType => JobExecutionType.Stationary;
    public JobPriorityTier DefaultPriority => JobPriorityTier.Construction;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        if (GroundItemManager.Instance.HasItemsAt(job.TargetX, job.TargetY))
            return false;
        // ЖЁСТКИЙ ЭТАП 3: стройка — только клетки в стадии Building
        // (чертёж полностью снабжён). Вне стройки IsStageAllowed=true.
        if (!ConstructionPipeline.Instance.IsStageAllowed(job.TargetX, job.TargetY, JobTypeId.Construction))
            return false;
        // ������� ������� �������: ������� ����� ������ �� ������ ������.
        // ������/������ ��� ����� � ������� �����/������, ������� ���.
        if (ctx != null
            && (uint)job.TargetX < (uint)ctx.MapWidth && (uint)job.TargetY < (uint)ctx.MapHeight)
        {
            if (ctx.TreeOnGrass[job.TargetX, job.TargetY])
                return false;
            if (ctx.StoneOnGrass != null && ctx.StoneOnGrass[job.TargetX, job.TargetY])
                return false;
            // �������� �� ��������� � ������� ��� � ���������, ������� ������.
            if (!BlueprintManager.Instance.IsBlueprintAt(job.TargetX, job.TargetY))
                return false;
        }
        return true;
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        (int X, int Y) stand = (job.TargetX, job.TargetY);
        Game.Simulation.JobBroker.FindStandPosition(job.TargetX, job.TargetY, ctx, out stand);

        pool.States[agentIndex] = AgentState.MovingToSource;
        pool.TargetCellX[agentIndex] = job.TargetX;
        pool.TargetCellY[agentIndex] = job.TargetY;
        pool.TargetPositionX[agentIndex] = stand.X * ctx.TileSize + 32f;
        pool.TargetPositionY[agentIndex] = stand.Y * ctx.TileSize + 32f;
        pool.WorkProgress[agentIndex] = 0f;
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
            // Визуал стройки: 6 стадий ProcessOfWork по доле стройки.
            // Единый финиш — через WorkProgressTracker.Finish (гасит спрайт везде).
            WorkProgressTracker.Instance.ReportFraction(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex],
                pool.WorkProgress[agentIndex] / BuildDuration);
        }
    }

    public void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        var state = pool.States[agentIndex];
        int sx = pool.TargetCellX[agentIndex];
        int sy = pool.TargetCellY[agentIndex];

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
                // ������� �� ����� ��������� �� ������������� (������������ ������/������)
                // � ����������� ���, ����� �� �� ����� ����� � �����.
                JobDispatcher.Instance.ReleaseJobWorkerForStuck(agentIndex, pool, ctx);
                pool.JobSearchTimer[agentIndex] = 4.0f + (float)ParallelRng.NextDouble() * 4.0f;
            }
        }
        else if (state == AgentState.Working && pool.WorkProgress[agentIndex] >= BuildDuration)
        {
            // #3: тип постройки НЕ выбрасываем — стены ставят SolidWalls и
            // выкидывают клетку из сайта, мебель/верстак идут в BuildingManager.
            // Раньше всё (включая WorkTable/Bed) становилось невидимой
            // непроходимой стеной + NotifyWallBuilt портил сайт.
            if (BlueprintManager.Instance.CompleteConstruction(sx, sy, out var builtType))
            {
                if (builtType == Game.Core.BuildingType.WoodWall)
                {
                    ctx.SolidWalls[sx, sy] = true;
                    FlowFieldManager.Instance.ClearCache();
                    HierarchicalPathfinder.Instance.Invalidate();
                    // Этап 3 закрыт: NotifyWallBuilt сам гасит прогресс-спрайт.
                    ConstructionPipeline.Instance.NotifyWallBuilt(sx, sy);
                    // GPU-���� �����. Node API ������� (TileSet/MarkDirty/AddCaster)
                    // ��������� ������ �� �������� ������ ����� CallDeferred.

                    int insideAgent = ctx.SpatialGrid.GetFirstAgent(sx, sy);
                    while (insideAgent != -1)
                    {
                        ctx.Movement.EjectFromWall(insideAgent, sx, sy, pool, ctx);
                        insideAgent = ctx.SpatialGrid.GetNextAgent(insideAgent, pool);
                    }
                }
                else
                {
                    // Мебель/верстак — проходимый объект, а не стена.
                    BuildingManager.Instance.AddBuilding(sx, sy, builtType);
                    WorkProgressTracker.Instance.Finish(sx, sy);
                }
            }

            JobDispatcher.Instance.TryUnregisterJob(pool.CurrentJobId[agentIndex]);
            // Claim �� ����������� ��������: RemoveJob ��� �������� _unclaimedCount.
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
            pool.States[agentIndex] = AgentState.Idle;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
        }
    }

    public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        // Единый финиш прогресса: агент ушёл — спрайт гаснет, залипаний нет.
        WorkProgressTracker.Instance.Finish(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex]);
    }
}
