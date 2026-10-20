using System;
using Game.Core;

namespace Game.Simulation;

// ============================================================================
// §119 P2: СИСТЕМА РАБОТЫ Worker'а зоны. Worker берёт работу ТОЛЬКО из очереди
// СВОЕЙ зоны: ZoneJobRouter.DequeueNearest(zoneId, x, y) — ближайшая к рабочему
// (не FIFO). Пирамидный диспатчер Worker'ов не трогает (не в Idle-пуле).
//
// Поток выдачи (спека §119 + заметка из P1):
//  1) DequeueNearest → ZoneJob (id/тип/клетка);
//  2) САМОВАЛИДАЦИЯ: TryGetJob(id) — жива ли работа в реестре; тип и клетка
//     совпадают; хендлер CanAgentExecute (дерево на месте, стройка допустима...).
//     Отвалилась → Unassign назад в очередь зоны и повтор на следующем тике.
//  3) TryAssignWorkerToZoneJob — клейм в реестре + OnStart хендлера.
//
// Прерывание нуждами (спека: «нужды прерывают, работа возвращается в очередь
// зоны, ПРОГРЕСС сохраняется»): пороги те же, что у Laborer (AgentNeedsConfig),
// но с +5 гистерезисом — Worker чуть устойчивее к срывам (постоянство состава
// бригады важнее реакции). Прогресс живёт в клетке (P7б CellProgressStore) —
// возврат ничего не сбрасывает.
// Watchdog: существующий ReleaseJobWorkerForStuck уже возвращает работу Worker'а
// в очередь зоны (хук в JobDispatcher, P2).
// ============================================================================

public sealed class WorkerZoneJobSystem
{
    public static WorkerZoneJobSystem Instance { get; } = new();

    private const float ReachDist = 20.0f;

    /// <summary>Как часто Idle-Worker пробует взять работу из зоны (сек sim-времени).</summary>
    public const float JobSearchIntervalSec = 2.0f;

    /// <summary>
    /// Выдача работы Idle-Worker'у его зоны. Вызывается из sim-книгкипа
    /// (тайм-слайсинг, как у Laborer). true — работа назначена.
    /// </summary>
    public bool TryAssignZoneJob(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        if (pool.Classes[agentIndex] != AgentClass.Worker)
            return false; // выдача из зоны — только для Worker'ов
        int zoneId = pool.AssignedZoneId[agentIndex];
        if (zoneId <= 0)
            return false;
        if (!ZoneManager.Instance.TryGetZoneById(zoneId, out var zone) || zone == null)
        {
            // Зона исчезла: разжать (согласование сделает Reconcile, но не ждём).
            WorkerAssignmentSystem.Instance.Reconcile(pool);
            return false;
        }

        var router = ZoneJobRouter.Instance;
        int wx = pool.CurrentCellX[agentIndex];
        int wy = pool.CurrentCellY[agentIndex];
        if (!router.DequeueNearest(zoneId, wx, wy, out var zjob))
            return false;

        // САМОВАЛИДАЦИЯ после выдачи (реестр — истина; P1-заметка).
        if (!JobDispatcher.Instance.JobIndex.TryGetJob(zjob.JobId, out var jd)
            || !jd.IsActive
            || jd.TypeId != zjob.Type
            || jd.TargetX != zjob.X || jd.TargetY != zjob.Y)
        {
            router.Unassign(zjob.JobId); // фантом/несоответствие — назад в очередь
            return false;
        }

        if (JobRegistry.TryGetHandler(jd.TypeId, out var handler) && !handler.CanAgentExecute(agentIndex, jd, pool, ctx))
        {
            // Объект работы отвалился (дерево срубили и т.п.): назад в очередь —
            // Sweep вычистит, если реально мертва; здесь не решаем судьбу сами.
            router.Unassign(zjob.JobId);
            return false;
        }

        // Клейм в реестре + OnStart. Неудача — работа уже возвращена в очередь
        // (внутри TryAssignWorkerToZoneJob), агент просто попробует позже.
        return JobDispatcher.Instance.TryAssignWorkerToZoneJob(agentIndex, zjob.JobId, pool, ctx);
    }

    /// <summary>
    /// §119 P2: прерывание Worker'а нуждами (голод/сон/усталость/окружение) —
    /// те же пороги, что у Laborer'а, +5 гистерезис на каждый порог. Работа
    /// возвращается в очередь зоны (ReleaseJobWorker → Unassign-хук), прогресс
    /// в клетке не теряется. true — прерван (агент ушёл в Evacuating).
    /// Вызывается из sim-книгкипа для РАБОТАЮЩЕГО Worker'а (тайм-слайсинг).
    /// </summary>
    public bool TryInterruptForNeeds(int agentIndex, AgentDataPool pool, SimulationContext ctx)
    {
        if (pool.Classes[agentIndex] != AgentClass.Worker)
            return false;
        if (pool.NeedsBehavior[agentIndex] != NeedBehavior.None)
            return false;
        if (pool.States[agentIndex] == AgentState.Idle || pool.States[agentIndex] == AgentState.Evacuating)
            return false; // не работает — прерывать нечего

        const float hyst = 5f;
        bool interrupt =
            pool.Hunger[agentIndex] > AgentNeedsConfig.HungerSeekThreshold + hyst ||
            pool.Sleep[agentIndex] > AgentNeedsConfig.SleepRestThreshold + hyst ||
            pool.Fatigue[agentIndex] > AgentNeedsConfig.FatigueRestThreshold + hyst;

        if (!interrupt)
            return false;

        // Легаси-снятие: OnCancel хендлера + возврат работы в очередь зоны
        // (Unassign-хук в ReleaseJobWorker) + агент в Idle... затем Needs-поведение
        // забирает его в Evacuating тем же API, что у Laborer'а.
        JobDispatcher.Instance.ReleaseJobWorker(agentIndex, pool, ctx);
        return true;
    }

    /// <summary>
    /// Тик работы Worker'а: движение к цели/работа делегируются ЛЕГАСИ-хендлеру
    /// (UpdateSingleAgentInner уже зовёт handler.ExecuteParallel независимо от
    /// класса) — здесь ничего дублировать не нужно. Метод оставлен пустым явным
    /// контрактом:Worker отличается только ИСТОЧНИКОМ работ и прерыванием.
    /// </summary>
    public void TickWorking(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
    {
        // (делегировано легаси-хендлеру — см. UpdateSingleAgentInner)
    }
}
