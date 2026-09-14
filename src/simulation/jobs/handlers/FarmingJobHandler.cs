using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

public sealed class FarmingJobHandler : IJobHandler
{
    /// <summary>Длительность вспашки грядки (игровые секунды). ~1/4 игрового часа = 125 геймсек.</summary>
    public const float TillDuration = WorldTime.SecondsPerHour * 0.25f;
    private const float ReachDist = 48.0f;

    public JobTypeId TypeId => JobTypeId.Farming;
    public JobExecutionType ExecutionType => JobExecutionType.Stationary;
    public JobPriorityTier DefaultPriority => JobPriorityTier.Farming;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        return !ctx.TreeOnGrass[job.TargetX, job.TargetY] && !ctx.SolidWalls[job.TargetX, job.TargetY];
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
        }
    }

    public void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        var state = pool.States[agentIndex];
        int px = pool.TargetCellX[agentIndex];
        int py = pool.TargetCellY[agentIndex];

        if (state == AgentState.MovingToSource)
        {
            Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);
            if ((pool.GetPosition(agentIndex) - target).Length() <= ReachDist)
            {
                pool.States[agentIndex] = AgentState.Working;
            }
            else if (pool.StuckTimer[agentIndex] >= 3.0f)
            {
                JobDispatcher.Instance.ReleaseJobWorkerForStuck(agentIndex, pool, ctx);
                pool.JobSearchTimer[agentIndex] = 4.0f + (float)ctx.Random.NextDouble() * 4.0f;
            }
        }
        else if (state == AgentState.Working && pool.WorkProgress[agentIndex] >= TillDuration)
        {
            FarmJobManager.Instance.CompletePlot(px, py);
            // RemoveJob под _registerLock уже уменьшает _unclaimedCount, если
            // работа была небоевой (assigned < max): дополнительный
            // ReleaseWorkerClaim после удачного Unregister УДВОИЛ бы
            // восстановление счётчика (разбалансировка вверх).
            int jobId = pool.CurrentJobId[agentIndex];
            if (jobId != -1)
            {
                if (!JobDispatcher.Instance.TryUnregisterJob(jobId))
                    JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(jobId);
            }
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
            pool.States[agentIndex] = AgentState.Idle;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
        }
    }

    public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx) { }
}
