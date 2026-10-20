using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

public enum ConstructionStage : byte
{
    Clearance = 0,
    Supplying = 1,
    WaitingAll = 2,
    Assembling = 3,
    Completed = 4
}

public sealed class ConstructionSite
{
    public int SiteId { get; set; }
    public int X { get; }
    public int Y { get; }
    public StructureDefinition Definition { get; }
    public ConstructionStage Stage { get; set; } = ConstructionStage.Clearance;

    // Препятствия
    public bool NeedTree { get; set; }
    public bool NeedStone { get; set; }
    public bool NeedHaul { get; set; }

    // Доставка материалов
    private readonly Dictionary<ItemId, int> _delivered = new();
    private readonly Dictionary<ItemId, int> _required = new();

    // Прогресс монтажа
    public float WorkProgress { get; set; }
    public int WorkProgressStage { get; set; } = -1;

    public ConstructionSite(int x, int y, StructureDefinition definition)
    {
        X = x;
        Y = y;
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));

        foreach (var req in definition.RequiredMaterials)
        {
            _required[req.Item] = req.Count;
            _delivered[req.Item] = 0;
        }
    }

    public int GetDelivered(ItemId item) => _delivered.TryGetValue(item, out int c) ? c : 0;
    public int GetRequired(ItemId item) => _required.TryGetValue(item, out int c) ? c : 0;

    public int TotalDelivered
    {
        get
        {
            int sum = 0;
            foreach (var kvp in _delivered) sum += kvp.Value;
            return sum;
        }
    }

    public int TotalRequired
    {
        get
        {
            int sum = 0;
            foreach (var kvp in _required) sum += kvp.Value;
            return sum;
        }
    }

    public bool IsFullySupplied
    {
        get
        {
            if (_required.Count == 0) return true;
            foreach (var (item, reqCount) in _required)
            {
                if (!_delivered.TryGetValue(item, out int delCount) || delCount < reqCount)
                    return false;
            }
            return true;
        }
    }

    public int AddDelivered(ItemId item, int count)
    {
        if (count <= 0) return 0;
        int req = GetRequired(item);
        int cur = GetDelivered(item);
        int canAccept = Math.Max(0, req - cur);
        int accepted = Math.Min(canAccept, count);
        if (accepted > 0)
        {
            _delivered[item] = cur + accepted;
        }
        return accepted;
    }

    public bool HasObstacles => NeedTree || NeedStone || NeedHaul;

    public Dictionary<ItemId, int> GetAllDelivered() => new(_delivered);
}
