using System;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

public sealed class JobPriorityManager
{
    public static JobPriorityManager Instance { get; } = new();

    private readonly int[] _priorities = new int[(int)JobCategory.Count];

    public event Action<JobCategory, int> OnPriorityChanged;

    public JobPriorityManager()
    {
        for (int i = 0; i < _priorities.Length; i++)
        {
            _priorities[i] = 1; // По умолчанию приоритет равен 1
        }
    }

    public int GetPriority(JobCategory category)
    {
        int index = (int)category;
        if (index < 0 || index >= _priorities.Length) return 1;
        return Volatile.Read(ref _priorities[index]);
    }

    public void SetPriority(JobCategory category, int value)
    {
        int index = (int)category;
        if (index < 0 || index >= _priorities.Length) return;
        int clamped = Math.Max(0, value);
        Volatile.Write(ref _priorities[index], clamped);
        OnPriorityChanged?.Invoke(category, clamped);
    }

    public JobCategory GetCategory(JobTypeId typeId)
    {
        return typeId switch
        {
            JobTypeId.BlueprintDelivery => JobCategory.Logistics,
            JobTypeId.StockpileHauling  => JobCategory.Logistics,
            JobTypeId.TreeChopping      => JobCategory.Lumberjack,
            JobTypeId.Construction      => JobCategory.Construction,
            JobTypeId.Farming           => JobCategory.Farming,
            _                           => JobCategory.Logistics
        };
    }

    public int GetPriorityForJobType(JobTypeId typeId)
    {
        return GetPriority(GetCategory(typeId));
    }
}