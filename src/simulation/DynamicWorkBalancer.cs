using System;
using Game.Simulation.Scheduling;

namespace Game.Simulation;

/// <summary>
/// Тонкий фасад-делегат к DynamicWorkScheduler.Shared (PLAN.md §8.2, Шаг 1).
/// Поведение не меняется: та же логика курсора, но теперь живёт в scheduling/.
/// Старые сигнатуры сохранены 1-в-1, чтобы AgentSimulationThread и JobDispatcher
/// компилировались без правок. [Obsolete] — только после перевода всех вызовов (Шаг 7).
/// </summary>
public static class DynamicWorkBalancer
{
    /// <summary>Стартовый размер батча (прокси к DynamicWorkScheduler).</summary>
    public const int InitialBatchSize = DynamicWorkScheduler.InitialBatchSize;

    /// <inheritdoc cref="DynamicWorkScheduler.MinBatchSize"/>
    public const int MinBatchSize = DynamicWorkScheduler.MinBatchSize;

    /// <inheritdoc cref="DynamicWorkScheduler.MaxBatchSize"/>
    public const int MaxBatchSize = DynamicWorkScheduler.MaxBatchSize;

    /// <summary>
    /// Выполняет body(i) для i в [0, count) с динамическим распределением.
    /// Потокобезопасно для SoA-записей по непересекающимся индексам.
    /// </summary>
    public static void ForEach(int count, Action<int> body, string profilerScope = null)
    {
        DynamicWorkScheduler.Shared.ForEach(count, body, WorkKind.Fast, profilerScope);
    }

    /// <summary>
    /// Range-вариант: body(start, endExclusive) вызывается на батч целиком.
    /// Удобен для Dispatcher (обработка диапазона чанков одним вызовом).
    /// </summary>
    public static int ForEachRange(int count, Action<int, int> body, string profilerScope = null)
    {
        return DynamicWorkScheduler.Shared.ForEachRange(count, body, WorkKind.Fast, profilerScope);
    }

    /// <summary>
    /// Сброс EMA-состояния (например, при смене скорости симуляции).
    /// </summary>
    public static void Reset()
    {
        DynamicWorkScheduler.Shared.Reset();
    }
}
