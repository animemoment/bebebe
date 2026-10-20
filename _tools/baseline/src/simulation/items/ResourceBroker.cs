using System;
using Game.Core;
using Game.Simulation.Jobs;

namespace Game.Simulation;

/// <summary>
/// Двусторонняя шина ресурсов:
/// 1) Push: уведомляет ожидающие заказы (чертежи) при появлении новых ресурсов.
/// 2) Pull: ищет и резервирует уже существующие свободные ресурсы при создании чертежа.
/// </summary>
public sealed class ResourceBroker
{
    public static ResourceBroker Instance { get; } = new();

    /// <summary>
    /// Push-уведомление: ресурс появился в мире (срубили дерево, раскололи камень, разгрузили).
    /// </summary>
    public bool NotifyResourceAvailable(ItemId item, int sourceX, int sourceY, int count)
    {
        if (count <= 0 || item == ItemId.None) return false;

        // Проверяем наличие ожидающих потребителей в реестре заказов
        if (DemandRegistry.Instance.TryGetHighestPriorityDemand(item, sourceX, sourceY, out var demand))
        {
            // Снимаем кулдаун недостижимости с целевой задачи (BlueprintDelivery)
            JobDispatcher.Instance.JobIndex.ClearJobCooldownAt(demand.TargetX, demand.TargetY, JobTypeId.BlueprintDelivery);

            // Пробуждаем диспетчер для немедленного назначения свободных носильщиков
            JobDispatcher.Instance.TriggerImmediateDispatch();
            return true;
        }

        return false;
    }
}
