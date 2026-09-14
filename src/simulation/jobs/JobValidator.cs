using System;
using System.Collections.Generic;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Периодический аудит индекса работ: ищет «мёртвые» задачи, которые юниты
/// выполнить не могут, и чинит рассинхрон между менеджерами меток и индексом.
/// Проверяет ТОЛЬКО уже отмеченные клетки (метки менеджеров + задачи индекса),
/// а не все 262k клеток карты. Тикает из AgentSimulationThread раз в реальные
/// 30 секунд порциями (chunk-slicing), чтобы не стопорить симуляцию.
/// </summary>
public sealed class JobValidator
{
    public static JobValidator Instance { get; } = new();

    /// <summary>Реальный интервал между аудитами, секунды wall-clock.</summary>
    public const float AuditIntervalRealSec = 30f;

    // Порция за один тик: не больше N задач индекса за проход.
    private const int MaxJobsPerTick = 2048;
    // Порция меток менеджеров за один тик.
    private const int MaxMarksPerTick = 2048;

    private readonly List<int> _jobSlice = new(MaxJobsPerTick);
    private int _jobCursor;
    private int _markCursor;

    private JobValidator() { }

    /// <summary>
    /// Один тик аудита. Возвращает число исправлений (0 — всё чисто).
    /// Вызывается из фонового sim-потока — Godot API не трогаем, только менеджеры.
    /// </summary>
    public int Tick(AgentDataPool pool, SimulationContext ctx)
    {
        int fixed_ = 0;
        try
        {
            fixed_ += AuditIndexSlice(pool, ctx);
            fixed_ += AuditMarksSlice();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[JobValidator] Ошибка аудита: {ex.Message}");
        }
        return fixed_;
    }

    /// <summary>
    /// Проход A: слайс задач индекса. Для каждой проверяем:
    /// 1) CanAgentExecute хотя бы для одного idle-агента (probe первых 8 idle).
    ///    Если НИКТО не может — задача мёртвая: на короткий кулдаун, чтобы не
    ///    долбить claim-сканы каждый dispatch-тик (каждый TryClaim дергает
    ///    CanAgentExecute с lock'ами менеджеров). Полное удаление — только если
    ///    метки в менеджере уже нет (рассинхрон).
    /// 2) Метка в менеджере есть, а задачи в индексе нет — см. AuditMarksSlice.
    /// </summary>
    private int AuditIndexSlice(AgentDataPool pool, SimulationContext ctx)
    {
        var index = JobDispatcher.Instance.JobIndex;
        var jobs = index.SnapshotActiveJobs(_jobSlice, MaxJobsPerTick, ref _jobCursor);
        if (jobs.Count == 0)
            return 0;

        // Probe-агенты: первые N idle (без аллокаций — по сетке чанков нельзя,
        // там lock; просто линейно первые idle из пула, это дёшево).
        int[] probes = CollectProbeAgents(pool, 8);

        int fixed_ = 0;
        foreach (int jobId in jobs)
        {
            if (!index.TryGetJob(jobId, out var job) || !job.IsActive)
                continue;

            if (!JobRegistry.TryGetHandler(job.TypeId, out var handler))
            {
                // Хендлера нет вообще — задача невыполнима никогда. Удаляем.
                index.RemoveJob(jobId, out _);
                fixed_++;
                continue;
            }

            bool anyoneCan = false;
            for (int p = 0; p < probes.Length; p++)
            {
                int agentIndex = probes[p];
                if (agentIndex < 0)
                    break;
                try
                {
                    if (handler.CanAgentExecute(agentIndex, job, pool, ctx))
                    {
                        anyoneCan = true;
                        break;
                    }
                }
                catch
                {
                    // CanAgentExecute не должен кидать, но аудит не должен ронять сим.
                    anyoneCan = true;
                    break;
                }
            }

            if (!anyoneCan)
            {
                // Мёртвая задача: никто из probe не может. Два подслучая:
                // а) метки в менеджере уже нет (удалили инструмент, а задача
                //    осталась) — удаляем задачу, это и есть «лист забился»;
                // б) метка есть, но условие временно ложно (дерево ещё стоит,
                //    склад полон) — ставим кулдаун подлиннее, чтобы dispatch
                //    не жег CPU на заведомо ложных claim-попытках.
                if (!HasManagerMark(job))
                {
                    index.RemoveJob(jobId, out _);
                    fixed_++;
                }
                else
                {
                    index.MarkJobUnreachable(jobId, 30000);
                }
            }
        }
        return fixed_;
    }

    /// <summary>
    /// Проход B: слайс меток менеджеров. Метка есть, а задачи в индексе нет —
    /// пересоздаём задачу (потеря при stale-завершении / краше OnStart).
    /// Идём по трём менеджерам по очереди (round-robin по _markCursor),
    /// чтобы не копировать все метки каждый тик.
    /// </summary>
    private int AuditMarksSlice()
    {
        int fixed_ = 0;
        int phase = _markCursor % 3;
        _markCursor++;

        var index = JobDispatcher.Instance.JobIndex;
        switch (phase)
        {
            case 0:
            {
                var plots = FarmJobManager.Instance.GetAllMarkedPlots();
                int n = 0;
                foreach (var (x, y) in plots)
                {
                    if (++n > MaxMarksPerTick)
                        break;
                    if (!index.HasJobAt(x, y, JobTypeId.Farming))
                    {
                        JobBroker.Instance.RegisterFarmPlot(x, y);
                        fixed_++;
                    }
                }
                break;
            }
            case 1:
            {
                // Деревья: меток может быть много, идём copy-on-read под lock
                // внутри GetAllMarkedTrees (менеджер отдаёт копию).
                var trees = TreeJobManager.Instance.GetAllMarkedTrees();
                int n = 0;
                foreach (var (x, y) in trees)
                {
                    if (++n > MaxMarksPerTick)
                        break;
                    if (!index.HasJobAt(x, y, JobTypeId.TreeChopping))
                    {
                        JobBroker.Instance.RegisterTreeChop(x, y);
                        fixed_++;
                    }
                }
                break;
            }
            default:
            {
                var blueprints = BlueprintManager.Instance.GetAllBlueprints();
                int n = 0;
                foreach (var (cell, type) in blueprints)
                {
                    if (++n > MaxMarksPerTick)
                        break;
                    int target = type == BuildingType.WorkTable ? 25 : 15;
                    if (!index.HasJobAt(cell.X, cell.Y, JobTypeId.BlueprintDelivery) &&
                        !index.HasJobAt(cell.X, cell.Y, JobTypeId.Construction))
                    {
                        JobBroker.Instance.RegisterBlueprint(cell.X, cell.Y, type, target);
                        fixed_++;
                    }
                }
                break;
            }
        }
        return fixed_;
    }

    private static int[] CollectProbeAgents(AgentDataPool pool, int max)
    {
        // ThreadLocal-буфер нельзя (Tick из одного sim-потока — можно static,
        // но проще маленький стековый массив: max=8 — дёшево).
        var probes = new int[max];
        Array.Fill(probes, -1);
        int found = 0;
        int cap = pool.Capacity;
        // Линейный скан с шагом: покрываем весь пул равномерно, а не первых N.
        int step = Math.Max(1, cap / max);
        for (int i = 0; i < cap && found < max; i += step)
        {
            if (pool.States[i] == AgentState.Idle && pool.CurrentJobId[i] == -1)
                probes[found++] = i;
        }
        return probes;
    }

    private static bool HasManagerMark(JobData job)
    {
        return job.TypeId switch
        {
            JobTypeId.Farming => FarmJobManager.Instance.IsPlotMarked(job.TargetX, job.TargetY),
            JobTypeId.TreeChopping => TreeJobManager.Instance.IsTreeMarked(job.TargetX, job.TargetY),
            JobTypeId.BlueprintDelivery or JobTypeId.Construction =>
                BlueprintManager.Instance.IsBlueprintAt(job.TargetX, job.TargetY),
            // Planting/Harvest/Haul — меток в менеджерах нет (одноразовые),
            // считаем метку всегда существующей, чтобы не удалять их здесь.
            _ => true
        };
    }
}
