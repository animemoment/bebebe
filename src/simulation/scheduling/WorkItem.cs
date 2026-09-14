using System;

namespace Game.Simulation.Scheduling;

/// <summary>
/// Классификация единицы работы для выбора стартового размера батча.
/// Fast — дешёвая равномерная работа (движение, арифметика нужд, commit).
/// Heavy — потенциально долгая (построение пути, BFS-детур, резервы под lock).
/// В v1 (Шаг 1) kind принимается, но пока игнорируется — все батчи режутся одинаково.
/// Учёт начнётся на Шаге 5 (CostHint).
/// </summary>
public enum WorkKind : byte
{
    Fast = 0,
    Heavy = 1,
}

/// <summary>
/// Дескриптор неделимой единицы кражи. readonly struct — ноль аллокаций в очередях.
/// Инвариант: Count &gt; 0, Start &gt;= 0.
/// </summary>
public readonly struct WorkItem
{
    /// <summary>Начальный индекс (inclusive).</summary>
    public readonly int Start;

    /// <summary>Число элементов (&gt; 0).</summary>
    public readonly int Count;

    /// <summary>Оценка стоимости: 1.0 = средний агент, 4.0 = чанк с работами.</summary>
    public readonly float CostHint;

    /// <summary>Классификация работы (Fast / Heavy).</summary>
    public readonly WorkKind Kind;

    /// <summary>Конец диапазона (exclusive).</summary>
    public int EndExclusive => Start + Count;

    /// <summary>
    /// Создаёт дескриптор диапазона [start, start+count).
    /// Некорректный hint (NaN/Infinity/&lt;=0) санитизируется в 1.0f (фикс @destroyer №5),
    /// чтобы эскалация CostHint×1.5 при requeue никогда не давала мусор.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Если start &lt; 0 или count &lt;= 0.</exception>
    public WorkItem(int start, int count, float costHint = 1.0f, WorkKind kind = WorkKind.Fast)
    {
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start), "Start должен быть >= 0.");
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count должен быть > 0.");
        Start = start;
        Count = count;
        CostHint = (!float.IsFinite(costHint) || costHint <= 0f) ? 1.0f : costHint;
        Kind = kind;
    }
}
