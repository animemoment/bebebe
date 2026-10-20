using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

public enum DemandType : byte
{
    Blueprint = 0,
    Crafting = 1,
}

public sealed class DemandTicket
{
    public DemandType Type { get; set; }
    public int TargetX { get; set; }
    public int TargetY { get; set; }
    public ItemId Item { get; set; }
    public int NeededCount { get; set; }
    public int DeliveredCount { get; set; }
    public int ReservedCount { get; set; }
    public int Priority { get; set; }

    public int RemainingNeeded => Math.Max(0, NeededCount - DeliveredCount - ReservedCount);
}

/// <summary>
/// Реестр активных потребностей (чертежи, стройплощадки, станки).
/// Служит приемником Push-уведомлений о появлении ресурсов.
/// </summary>
public sealed class DemandRegistry
{
    public static DemandRegistry Instance { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y, ItemId Item), DemandTicket> _demands = new();

    public void RegisterDemand(DemandType type, int targetX, int targetY, ItemId item, int neededCount, int priority = 0)
    {
        if (neededCount <= 0 || item == ItemId.None) return;

        lock (_lock)
        {
            var key = (targetX, targetY, item);
            if (_demands.TryGetValue(key, out var existing))
            {
                existing.NeededCount = neededCount;
                existing.Priority = priority;
            }
            else
            {
                _demands[key] = new DemandTicket
                {
                    Type = type,
                    TargetX = targetX,
                    TargetY = targetY,
                    Item = item,
                    NeededCount = neededCount,
                    DeliveredCount = 0,
                    ReservedCount = 0,
                    Priority = priority
                };
            }
        }
    }

    public void UnregisterDemand(int targetX, int targetY, ItemId item)
    {
        lock (_lock)
        {
            _demands.Remove((targetX, targetY, item));
        }
    }

    public void UpdateDelivered(int targetX, int targetY, ItemId item, int deliveredCount)
    {
        lock (_lock)
        {
            var key = (targetX, targetY, item);
            if (_demands.TryGetValue(key, out var ticket))
            {
                ticket.DeliveredCount += deliveredCount;
                ticket.ReservedCount = Math.Max(0, ticket.ReservedCount - deliveredCount);
                if (ticket.DeliveredCount >= ticket.NeededCount)
                {
                    _demands.Remove(key);
                }
            }
        }
    }

    public bool TryGetHighestPriorityDemand(ItemId item, int sourceX, int sourceY, out DemandTicket bestDemand)
    {
        bestDemand = null;
        lock (_lock)
        {
            float bestScore = float.MinValue;
            foreach (var ticket in _demands.Values)
            {
                if (ticket.Item != item || ticket.RemainingNeeded <= 0)
                    continue;

                float distSq = (ticket.TargetX - sourceX) * (ticket.TargetX - sourceX) + (ticket.TargetY - sourceY) * (ticket.TargetY - sourceY);
                float progressFrac = ticket.NeededCount > 0 ? (float)ticket.DeliveredCount / ticket.NeededCount : 0f;
                // Приоритет отдается стройкам ближе к завершению (не плодить недострои) и ближе к источнику
                float score = (progressFrac * 1000f) + ticket.Priority - (distSq * 0.01f);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDemand = ticket;
                }
            }
            return bestDemand != null;
        }
    }

    public bool HasDemandFor(ItemId item)
    {
        lock (_lock)
        {
            foreach (var ticket in _demands.Values)
            {
                if (ticket.Item == item && ticket.RemainingNeeded > 0)
                    return true;
            }
            return false;
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _demands.Clear();
        }
    }
}
