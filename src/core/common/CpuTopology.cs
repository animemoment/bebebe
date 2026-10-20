namespace Game.Core;

/// <summary>
/// Топология CPU: единая политика эффективного DOP для симуляции и профайлера.
/// Мост D (PLAN.md §112.2): раньше Game.Core.GameProfiler тянул
/// Game.Simulation.Scheduling.DynamicWorkScheduler только ради этой функции —
/// одно из четырёх рёбер, замыкавших C# SCC на 128 файлов. Логика — листовой
/// модуль без зависимостей; DynamicWorkScheduler делегирует сюда.
/// </summary>
public static class CpuTopology
{
    /// <summary>
    /// Эффективный DOP: резервируем одно ядро под рендер/Godot main loop.
    /// DOP=ProcessorCount на 4 ядрах даёт оркестратор+воркеры+Godot = 6 потоков
    /// на 4 ядра → рендер голодает. P-1 оставляет гарантированное ядро рендеру.
    /// </summary>
    public static int ComputeEffectiveDop(int processorCount)
    {
        if (processorCount <= 1)
            return 1;
        // 2 ядра: ужиматься до 1 бессмысленно (симуляция встанет) — оставляем 2.
        if (processorCount <= 2)
            return 2;
        return processorCount - 1;
    }
}
