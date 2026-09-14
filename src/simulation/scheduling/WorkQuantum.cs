using System;
using System.Diagnostics;

namespace Game.Simulation.Scheduling;

/// <summary>
/// Квант времени батча для кооперативного прерывания долгой задачи (Шаг 4).
/// Передаётся в splittable-body по значению (struct, zero-alloc).
/// Body проверяет <see cref="ShouldYield"/> каждые <see cref="YieldEveryN"/>
/// элементов и при истечении возвращает число обработанных элементов —
/// остаток планировщик ставит обратно в очередь с CostHint×1.5.
/// </summary>
public readonly struct WorkQuantum
{
    /// <summary>Жёсткий потолок кванта (мс). Большее значение clamp'ится.</summary>
    public const double MaxQuantumMs = 100.0;

    /// <summary>Дедлайн батча в тиках Stopwatch.GetTimestamp().</summary>
    public readonly long DeadlineTicks;

    /// <summary>Проверять время не чаще чем каждые N элементов (напр. 8).</summary>
    public readonly int YieldEveryN;

    /// <summary>
    /// Создаёт квант длительностью <paramref name="quantumMs"/> миллисекунд.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Если quantumMs — NaN/Infinity/&lt;=0. Значение &gt; <see cref="MaxQuantumMs"/> clamp'ится.
    /// </exception>
    public WorkQuantum(double quantumMs, int yieldEveryN = 8)
    {
        if (!double.IsFinite(quantumMs) || quantumMs <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(quantumMs), "Квант должен быть конечным и > 0.");
        double q = Math.Min(quantumMs, MaxQuantumMs);
        long now = Stopwatch.GetTimestamp();
        long quantumTicks = (long)(q * Stopwatch.Frequency / 1000.0);
        DeadlineTicks = now + quantumTicks;
        YieldEveryN = yieldEveryN > 0 ? yieldEveryN : 8;
    }

    /// <summary>
    /// True, если обработано достаточно элементов для проверки И дедлайн истёк.
    /// Один Stopwatch.GetTimestamp (~20–30 нс) только после YieldEveryN элементов.
    /// </summary>
    public bool ShouldYield(int processedSinceCheck)
    {
        if (processedSinceCheck < YieldEveryN)
            return false;
        return Stopwatch.GetTimestamp() >= DeadlineTicks;
    }

    // FIX круг-2 №3: публичный флаг истечения кванта без счётчика —
    // планировщик принудительно урезает батч, даже если body игнорирует yield.
    /// <summary>
    /// True, если дедлайн кванта уже истёк (без счётчика processed).
    /// Для принудительного enforcement в планировщике и для cached-паттерна (§4).
    /// </summary>
    public bool IsExpired => Stopwatch.GetTimestamp() >= DeadlineTicks;

    // FIX круг-2 №4: паттерн для SingleAgentTail — timestamp кэшируется каждые 8,
    // но дедлайн проверяется ПЕРЕД КАЖДЫМ агентом по кэшированному значению.
    // Пример использования в body:
    //   long cached = 0; int sinceCheck = 0;
    //   for (int i = s; i &lt; e; i++) {
    //       if (q.ShouldYieldCached(ref cached, ref sinceCheck)) break; // проверка каждый i, тики — каждые 8
    //       UpdateSingleAgent(i, ...); // даже 1 тяжёлый агент: следующий i сразу yield'ит
    //       sinceCheck++;
    //   }
    // Хвост ограничен ~1 тяжёлым агентом + квант, а не 7 тяжёлыми.
    /// <summary>
    /// Cached-проверка дедлайна: Stopwatch.GetTimestamp вызывается не чаще чем
    /// каждые <see cref="YieldEveryN"/> элементов (через <paramref name="cachedTicks"/>),
    /// но сравнение с дедлайном — при КАЖДОМ вызове. Ловит 1 тяжёлого агента:
    /// после него следующий i сразу yield'ит, а не через 7.
    /// </summary>
    /// <param name="cachedTicks">Кэш timestamp (инициализировать 0; 0 = «ещё не брали»).</param>
    /// <param name="processedSinceRefresh">Счётчик с последнего refresh; обнуляется внутри.</param>
    public bool ShouldYieldCached(ref long cachedTicks, ref int processedSinceRefresh)
    {
        if (processedSinceRefresh >= YieldEveryN || cachedTicks == 0)
        {
            cachedTicks = Stopwatch.GetTimestamp();
            processedSinceRefresh = 0;
        }
        return cachedTicks >= DeadlineTicks;
    }
}
