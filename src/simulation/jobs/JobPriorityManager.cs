using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

public sealed class JobPriorityManager
{
    public static JobPriorityManager Instance { get; } = new();

    private readonly int[] _priorities = new int[(int)JobCategory.Count];

    // task-2/perf: таблица «тип работы → категория» вместо switch. GetCategory вызывается
    // в САМОМ внутреннем цикле подбора работы (JobSpatialPyramid.TryClaimInChunk: две
    // выборки на каждого оценённого кандидата — priority + affinity), то есть до тысяч раз
    // за один claim-скан агента. Switch (до 8 сравнений) заменён на один индекс массива.
    // Значения обязаны совпадать со старым switch: всё, кроме явно перечисленных типов
    // (None/ClearSite/Crafting и любые будущие), — Logistics.
    private static readonly JobCategory[] CategoryByType = BuildCategoryTable();

    private static JobCategory[] BuildCategoryTable()
    {
        var table = new JobCategory[256];
        for (int i = 0; i < table.Length; i++)
            table[i] = JobCategory.Logistics;

        table[(byte)JobTypeId.TreeChopping] = JobCategory.Lumberjack;
        table[(byte)JobTypeId.Mining] = JobCategory.Lumberjack;
        table[(byte)JobTypeId.Construction] = JobCategory.Construction;
        table[(byte)JobTypeId.Farming] = JobCategory.Farming;
        table[(byte)JobTypeId.Planting] = JobCategory.Farming;
        table[(byte)JobTypeId.Harvesting] = JobCategory.Farming;
        table[(byte)JobTypeId.BlueprintDelivery] = JobCategory.Logistics;
        table[(byte)JobTypeId.StockpileHauling] = JobCategory.Logistics;
        return table;
    }

    public event Action<JobCategory, int> OnPriorityChanged;

    public JobPriorityManager()
    {
        for (int i = 0; i < _priorities.Length; i++)
        {
            _priorities[i] = 1;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JobCategory GetCategory(JobTypeId typeId)
    {
        // (byte)typeId всегда в [0, 255] — границ массива не касается.
        return CategoryByType[(byte)typeId];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPriorityForJobType(JobTypeId typeId)
    {
        return GetPriority(CategoryByType[(byte)typeId]);
    }

    public void Reset()
    {
        for (int i = 0; i < _priorities.Length; i++)
        {
            Volatile.Write(ref _priorities[i], 1);
        }
    }
}