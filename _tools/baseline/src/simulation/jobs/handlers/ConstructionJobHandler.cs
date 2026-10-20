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
        {
            if (ctx != null)
                GroundItemManager.Instance.TrySpillItemsAdjacent(job.TargetX, job.TargetY, ctx.MapWidth, ctx.MapHeight, ctx.SolidWalls);
            if (GroundItemManager.Instance.HasItemsAt(job.TargetX, job.TargetY))
                return false;
        }

        // Разрешено только если площадка в стадии Assembling
        if (ConstructionManager.Instance.TryGetSite(job.TargetX, job.TargetY, out var site))
        {
            if (site.Stage != ConstructionStage.Assembling)
                return false;
        }
        else if (!ConstructionManager.Instance.IsStageAllowed(job.TargetX, job.TargetY, JobTypeId.Construction))
        {
            return false;
        }

        if (ctx != null && (uint)job.TargetX < (uint)ctx.MapWidth && (uint)job.TargetY < (uint)ctx.MapHeight)
        {
            if (ctx.TreeOnGrass[job.TargetX, job.TargetY])
                return false;
            if (ctx.StoneOnGrass != null && ctx.StoneOnGrass[job.TargetX, job.TargetY])
                return false;
            if (!ConstructionManager.Instance.IsSiteAt(job.TargetX, job.TargetY) && !BlueprintManager.Instance.IsBlueprintAt(job.TargetX, job.TargetY))
                return false;
        }
        return true;
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        pool.WaypointCount[agentIndex] = 0;
        pool.WaypointIndex[agentIndex] = 0;

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
            float frac = pool.WorkProgress[agentIndex] / BuildDuration;
            WorkProgressTracker.Instance.ReportFraction(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex], frac);
            ConstructionManager.Instance.ReportWorkProgress(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex], frac);
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
                JobDispatcher.Instance.ReleaseJobWorkerForStuck(agentIndex, pool, ctx);
                pool.JobSearchTimer[agentIndex] = 4.0f + (float)ParallelRng.NextDouble() * 4.0f;
            }
        }
        else if (state == AgentState.Working && pool.WorkProgress[agentIndex] >= BuildDuration)
        {
            // Единое завершение через ConstructionManager + StructureManager (с передачей pool для выталкивания)
            if (ConstructionManager.Instance.CompleteConstruction(sx, sy, ctx, pool, out var completedDef))
            {
                ConstructionManager.Instance.NotifyWallBuilt(sx, sy);
                WorkProgressTracker.Instance.Finish(sx, sy);
            }
            else if (BlueprintManager.Instance.CompleteConstruction(sx, sy, out var builtType))
            {
                var def = StructureRegistry.Get(builtType);
                StructureManager.Instance.AddStructure(sx, sy, def, ctx, pool);
                if (builtType == BuildingType.WoodWall)
                {
                    ConstructionManager.Instance.NotifyWallBuilt(sx, sy);
                }
                else
                {
                    WorkProgressTracker.Instance.Finish(sx, sy);
                }
            }

            // Выталкиваем строителя, если он остался стоять на месте стены
            if (ctx?.Movement != null)
            {
                int curAx = (int)(pool.PositionX[agentIndex] / ctx.TileSize);
                int curAy = (int)(pool.PositionY[agentIndex] / ctx.TileSize);
                if (curAx == sx && curAy == sy)
                {
                    ctx.Movement.EjectFromWall(agentIndex, sx, sy, pool, ctx);
                }
            }

            JobDispatcher.Instance.TryUnregisterJob(pool.CurrentJobId[agentIndex]);
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
            pool.States[agentIndex] = AgentState.Idle;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
        }
    }

    public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        WorkProgressTracker.Instance.Finish(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex]);
        ConstructionManager.Instance.ReportWorkProgress(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex], 0f);
    }
}
