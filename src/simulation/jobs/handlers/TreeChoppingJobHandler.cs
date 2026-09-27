using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

public sealed class TreeChoppingJobHandler : IJobHandler
{
    private const float ChopDuration = WorldTime.SecondsPerHour * 6f; // 6 ������� ����� = 3000 ����-������
    private const float ReachDist = 20.0f;

    public JobTypeId TypeId => JobTypeId.TreeChopping;
    public JobExecutionType ExecutionType => JobExecutionType.Stationary;
    public JobPriorityTier DefaultPriority => JobPriorityTier.TreeChopping;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        // Bounds-guard: ����� jobId/���� ����� �� probe ���������� � ��� ���� IndexOutOfRange.
        if (ctx?.TreeOnGrass == null)
            return false;
        if ((uint)job.TargetX >= (uint)ctx.MapWidth || (uint)job.TargetY >= (uint)ctx.MapHeight)
            return false;
        // ЖЁСТКИЙ ЭТАП 1: расчистка идёт только на клетках в стадии Clearing.
        if (!ConstructionPipeline.Instance.IsStageAllowed(job.TargetX, job.TargetY, JobTypeId.TreeChopping))
            return false;
        return ctx.TreeOnGrass[job.TargetX, job.TargetY];
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
            // Визуал ломки: 6 стадий ProcessOfWork по доле рубки.
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

            // Этап 1 пайплайна стройки: дерево убрано — снять флаг клетки
            // (идемпотентно; reconcile добьёт пропущенное).
            ConstructionPipeline.Instance.NotifyTreeCleared(tx, ty);

            // ParallelRng: ctx.Random �� thread-safe (Commit ��� �� ���������).
            int dropCount = ParallelRng.Next(21, 39);
            GroundItemManager.Instance.SpawnItems(tx, ty, ItemId.Log, dropCount);

            // ������ Farming: stale unregister (false) > ���������� claim �������,
            // ����� claim ����� � ������ ������ �� �������� (������ �����).
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
        WorkProgressTracker.Instance.Clear(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex]);
    }
}
