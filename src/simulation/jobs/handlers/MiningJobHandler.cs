using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

/// <summary>
/// Добыча камня из россыпи (близнец TreeChoppingJobHandler).
/// Россыпь — препятствие: стоим рядом (FindStandPosition), долбим 4 игровых
/// часа, россыпь исчезает (StoneOnGrass=false), падает ItemId.Stone 8-15 шт.
/// </summary>
public sealed class MiningJobHandler : IJobHandler
{
    private const float MineDuration = WorldTime.SecondsPerHour * 4f; // 4 игровых часа = 2000 гейм-секунд
    private const float ReachDist = 20.0f;

    public JobTypeId TypeId => JobTypeId.Mining;
    public JobExecutionType ExecutionType => JobExecutionType.Stationary;
    public JobPriorityTier DefaultPriority => JobPriorityTier.TreeChopping;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        if (ctx?.StoneOnGrass == null)
            return false;
        if ((uint)job.TargetX >= (uint)ctx.MapWidth || (uint)job.TargetY >= (uint)ctx.MapHeight)
            return false;
        // ЖЁСТКИЙ ЭТАП 1: добыча на стройке — только стадия Clearing.
        if (!ConstructionPipeline.Instance.IsStageAllowed(job.TargetX, job.TargetY, JobTypeId.Mining))
            return false;
        return ctx.StoneOnGrass[job.TargetX, job.TargetY];
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        pool.States[agentIndex] = AgentState.MovingToSource;
        pool.TargetCellX[agentIndex] = job.TargetX;
        pool.TargetCellY[agentIndex] = job.TargetY;
        pool.TargetPositionX[agentIndex] = job.StandX * ctx.TileSize + 32f;
        pool.TargetPositionY[agentIndex] = job.StandY * ctx.TileSize + 32f;
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
            // Визуал добычи камня: 6 стадий ProcessOfWork.
            WorkProgressTracker.Instance.ReportFraction(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex],
                pool.WorkProgress[agentIndex] / MineDuration);
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
        else if (state == AgentState.Working && pool.WorkProgress[agentIndex] >= MineDuration)
        {
            if (ctx?.StoneOnGrass != null && (uint)tx < (uint)ctx.MapWidth && (uint)ty < (uint)ctx.MapHeight)
                ctx.StoneOnGrass[tx, ty] = false;
            StoneJobManager.Instance.CompleteStone(tx, ty);
            WorkProgressTracker.Instance.Clear(tx, ty);

            // Этап 1 пайплайна стройки: камень добыт — снять флаг клетки.
            ConstructionPipeline.Instance.NotifyStoneCleared(tx, ty);

            // ParallelRng: ctx.Random не thread-safe (Commit идёт из параллели).
            int dropCount = ParallelRng.Next(8, 16);
            GroundItemManager.Instance.SpawnItems(tx, ty, ItemId.Stone, dropCount);

            // Эталон Farming: stale unregister → освободить claim вручную.
            int mineJobId = pool.CurrentJobId[agentIndex];
            if (mineJobId != -1)
            {
                if (!JobDispatcher.Instance.TryUnregisterJob(mineJobId))
                    JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(mineJobId);
            }
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
            pool.States[agentIndex] = AgentState.Idle;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
        }
    }

    public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        WorkProgressTracker.Instance.Clear(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex]);
    }
}
