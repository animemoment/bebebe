using System;
using System.Numerics;
using Game.Core;

namespace Game.Simulation.Jobs;

public sealed class BlueprintDeliveryJobHandler : IJobHandler
{
    private const float ReachDist = 20.0f;
    private const float MaxCarryWeight = 25.0f;

    public JobTypeId TypeId => JobTypeId.BlueprintDelivery;
    public JobExecutionType ExecutionType => JobExecutionType.Hauling;
    public JobPriorityTier DefaultPriority => JobPriorityTier.BlueprintSupply;
    public ToolRequirement RequiredTool => ToolRequirement.None;

    public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        if (!GroundItemManager.Instance.HasAvailableLogs)
            return false;
        // ЖЁСТКИЙ ЭТАП 2: поднос — только на клетках в стадии Supply.
        // (вне стройки IsStageAllowed=true — обычные чертежи мебели не страдают).
        if (!ConstructionPipeline.Instance.IsStageAllowed(job.TargetX, job.TargetY, JobTypeId.BlueprintDelivery))
            return false;
        // ������� ������� �������: ���� ������ �� ��������� (������/������/��������
        // �� �����) � �������� �� ��������, ����� ����� �������� ����� �� �����.
        if (ctx != null
            && (uint)job.TargetX < (uint)ctx.MapWidth && (uint)job.TargetY < (uint)ctx.MapHeight)
        {
            if (ctx.TreeOnGrass[job.TargetX, job.TargetY])
                return false;
            if (ctx.StoneOnGrass != null && ctx.StoneOnGrass[job.TargetX, job.TargetY])
                return false;
            if (GroundItemManager.Instance.HasItemsAt(job.TargetX, job.TargetY))
                return false;
        }
        return true;
    }

    public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
    {
        Vector2 pos = pool.GetPosition(agentIndex);
        // #2: не тащим больше, чем осталось довезти (target - delivered):
        // носильщик вмещает до 29 брёвен, а чертежу нужно 10–25 — излишек
        // раньше испарялся в TryAddJobProgress/AddDeliveredLogs. Второй пояс —
        // кламп в DeliverLogsToBlueprint (снимок job мог устареть между claim и OnStart).
        int remaining = job.TargetItemCount - job.CurrentDeliveredCount;
        if (remaining <= 0)
        {
            // Снимок протух (чертёж почти довезли конкуренты) — не тащим ничего.
            int failId = pool.CurrentJobId[agentIndex];
            if (failId != -1)
            {
                JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(failId);
                pool.CurrentJobId[agentIndex] = -1;
                pool.CurrentJobType[agentIndex] = JobTypeId.None;
            }
            pool.States[agentIndex] = AgentState.Idle;
            pool.JobSearchTimer[agentIndex] = 4.0f + (float)ParallelRng.NextDouble() * 4.0f;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
            return;
        }
        float needWeight = Math.Min(MaxCarryWeight, remaining * ItemRegistry.Get(ItemId.Log).Weight);
        // P0.3: ����� ������� 4 ����� (~64 �����) ������ ������� 32: Dispatcher � ���
        // ������������ ������� ����� �����, � ������ ���� 1024 ������ ��� lock � ������.
        if (GroundItemManager.Instance.TryReserveGroundItems(pos, needWeight, true, ItemId.Log, 4, out var itemCell, out var itemId, out int resCount))
        {
            pool.States[agentIndex] = AgentState.MovingToSource;
            pool.SourceCellX[agentIndex] = itemCell.X;
            pool.SourceCellY[agentIndex] = itemCell.Y;
            pool.TargetCellX[agentIndex] = job.TargetX;
            pool.TargetCellY[agentIndex] = job.TargetY;
            pool.ReservedItemCount[agentIndex] = resCount;
            pool.TargetPositionX[agentIndex] = itemCell.X * ctx.TileSize + 32f;
            pool.TargetPositionY[agentIndex] = itemCell.Y * ctx.TileSize + 32f;
        }
        else
        {
            // ������������ ���� �������: ������ ��������� + ������� (P0.2, ��� � Hauling).
            int failId = pool.CurrentJobId[agentIndex];
            if (failId != -1)
            {
                JobDispatcher.Instance.JobIndex.MarkJobUnreachable(failId, 5000);
                JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(failId);
                pool.CurrentJobId[agentIndex] = -1;
                pool.CurrentJobType[agentIndex] = JobTypeId.None;
            }
            pool.States[agentIndex] = AgentState.Idle;
            pool.JobSearchTimer[agentIndex] = 4.0f + (float)ParallelRng.NextDouble() * 4.0f;
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
        }
    }

    public void ExecuteParallel(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);
        ctx.Movement.MoveTowards(agentIndex, target, ReachDist, deltaTime, pool, ctx);
    }

    public void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        var state = pool.States[agentIndex];
        Vector2 target = new(pool.TargetPositionX[agentIndex], pool.TargetPositionY[agentIndex]);

        if ((pool.GetPosition(agentIndex) - target).Length() <= ReachDist)
        {
            pool.StuckTimer[agentIndex] = 0f;
            if (state == AgentState.MovingToSource)
            {
                int taken = GroundItemManager.Instance.TakeItems(pool.SourceCellX[agentIndex], pool.SourceCellY[agentIndex], pool.ReservedItemCount[agentIndex]);
                if (taken > 0)
                {
                    pool.CarriedItemId[agentIndex] = ItemId.Log;
                    pool.CarriedItemCount[agentIndex] = taken;
                    pool.States[agentIndex] = AgentState.MovingToTarget;
                    pool.TargetPositionX[agentIndex] = pool.TargetCellX[agentIndex] * ctx.TileSize + 32f;
                    pool.TargetPositionY[agentIndex] = pool.TargetCellY[agentIndex] * ctx.TileSize + 32f;
                }
                else
                {
                    JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
                }
            }
            else if (state == AgentState.MovingToTarget)
            {
                int tx = pool.TargetCellX[agentIndex];
                int ty = pool.TargetCellY[agentIndex];

                // ��� ������������ TryGetJobByPos: ����� ��� � Deliver ����� ���
                // ����������� ����������� (TOCTOU). DeliverLogsToBlueprint ���
                // ������ ������������ �� ����� �����, ���� ������ ��� ���.
                int carried = pool.CarriedItemCount[agentIndex];
                ItemId carriedId = pool.CarriedItemId[agentIndex];
                if (carried > 0 && carriedId != ItemId.None)
                {
                    JobBroker.Instance.DeliverLogsToBlueprint(tx, ty, carried);
                }

                pool.CarriedItemCount[agentIndex] = 0;
                pool.CarriedItemId[agentIndex] = ItemId.None;
                JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
            }
        }
        else if (pool.StuckTimer[agentIndex] >= 3.0f)
        {
            // ������� �� ���� (� ��������� ��� ����): ����������� claim � ���������,
            // ����� ����� ����� �����, � �����/������ �� �������������.
            // OnCancel ������ ������/�������� carry.
            JobDispatcher.Instance.ReleaseJobWorkerForStuck(agentIndex, pool, ctx);
        }
    }

    public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        if (pool.States[agentIndex] == AgentState.MovingToSource)
        {
            GroundItemManager.Instance.ReleaseReservation(pool.SourceCellX[agentIndex], pool.SourceCellY[agentIndex], pool.ReservedItemCount[agentIndex]);
        }
        else if (pool.CarriedItemCount[agentIndex] > 0)
        {
            int cx = Math.Clamp((int)(pool.PositionX[agentIndex] / ctx.TileSize), 0, ctx.MapWidth - 1);
            int cy = Math.Clamp((int)(pool.PositionY[agentIndex] / ctx.TileSize), 0, ctx.MapHeight - 1);
            GroundItemManager.Instance.SpawnItems(cx, cy, pool.CarriedItemId[agentIndex], pool.CarriedItemCount[agentIndex]);
            pool.CarriedItemCount[agentIndex] = 0;
            pool.CarriedItemId[agentIndex] = ItemId.None;
        }
    }
}