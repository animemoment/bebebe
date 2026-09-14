using System.Numerics;
using Game.Core;
using Game.Simulation.Gpu;

namespace Game.Simulation.Jobs;

public sealed class ConstructionJobHandler : IJobHandler
{
    private const float BuildDuration = WorldTime.SecondsPerHour * 4f; // 4 игровых часа = 2000 гейм-секунд
    private const float ReachDist = 48.0f;

    public JobTypeId TypeId => JobTypeId.Construction;
    public JobExecutionType ExecutionType => JobExecutionType.Stationary;
    public JobPriorityTier DefaultPriority => JobPriorityTier.Construction;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        return !GroundItemManager.Instance.HasItemsAt(job.TargetX, job.TargetY);
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
                // Рабочий не может добраться до стройплощадки (заблокирован стеной/толпой)
                // — освобождаем его, чтобы он не висел вечно у стены.
                JobDispatcher.Instance.ReleaseJobWorkerForStuck(agentIndex, pool, ctx);
                pool.JobSearchTimer[agentIndex] = 4.0f + (float)ctx.Random.NextDouble() * 4.0f;
            }
        }
        else if (state == AgentState.Working && pool.WorkProgress[agentIndex] >= BuildDuration)
        {
            if (BlueprintManager.Instance.CompleteConstruction(sx, sy, out _))
            {
                ctx.SolidWalls[sx, sy] = true;
                FlowFieldManager.Instance.ClearCache();
                HierarchicalPathfinder.Instance.Invalidate();
                // Карта блоков сменилась — GPU-поле протухает, следующий TryCompute
                // посчитает заново (Invalidate — атомарная запись ссылки, потокобезопасен).
                // БАГ B (#5): дубль НЕ убираем. CompleteConstruction шлёт
                // OnBlueprintCompleted через CallDeferred (главный поток), поэтому
                // WallBuildManager.AddWall вызывается позже и тоже инвалидирует поле.
                // Но не-стены (WorkTable через BuildingManager) вообще НЕ идут через
                // AddWall — без этого вызова их блокировка осталась бы в свежем поле.
                // Node API цепочка (TileSet/MarkDirty/AddCaster) трогается только
                // из главного потока через CallDeferred — из sim-потока идёт лишь
                // Invalidate (чистый C#, без Godot-нод) — безопасно.
                GpuFlowField.Instance.Invalidate();

                int insideAgent = ctx.SpatialGrid.GetFirstAgent(sx, sy);
                while (insideAgent != -1)
                {
                    ctx.Movement.EjectFromWall(insideAgent, sx, sy, pool, ctx);
                    insideAgent = ctx.SpatialGrid.GetNextAgent(insideAgent, pool);
                }
            }

            JobDispatcher.Instance.TryUnregisterJob(pool.CurrentJobId[agentIndex]);
            // Claim НЕ освобождаем отдельно: RemoveJob уже поправил _unclaimedCount.
            pool.CurrentJobId[agentIndex] = -1;
            pool.CurrentJobType[agentIndex] = JobTypeId.None;
            pool.States[agentIndex] = AgentState.Idle;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
        }
    }

    public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx) { }
}
