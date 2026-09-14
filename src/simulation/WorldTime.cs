namespace Game.Simulation;

/// <summary>
/// Стандарт мирового времени (время в мире).
/// Сутки = 24 игровых часа = 12000 игровых секунд (геймсек).
/// На скорости 100x сутки идут ровно 2 реальные минуты (12000 / 100 = 120 с),
/// на 1x — 12000 реальных секунд (~3 ч 20 м).
/// 1 игровой час = 500 геймсек. Длительности работ задаются в игровых часах
/// через <see cref="SecondsPerHour"/>, чтобы пропорции к суткам сохранялись
/// при любой скорости симуляции.
/// </summary>
public static class WorldTime
{
    /// <summary>Игровых секунд в одних сутках.</summary>
    public const float SecondsPerDay = 12000f;

    /// <summary>Игровых часов в сутках.</summary>
    public const int HoursPerDay = 24;

    /// <summary>Игровых секунд в одном игровом часе (24 * 500 = 12000).</summary>
    public const float SecondsPerHour = SecondsPerDay / HoursPerDay;

    /// <summary>Перевод игровых часов в игровые секунды.</summary>
    public static float HoursToSeconds(float hours) => hours * SecondsPerHour;

    /// <summary>Время суток в секундах в диапазоне [0, SecondsPerDay).</summary>
    public static float TimeOfDaySeconds(float gameTimeSeconds) => gameTimeSeconds % SecondsPerDay;

    /// <summary>Час суток в диапазоне [0, 24).</summary>
    public static float HourOfDay(float gameTimeSeconds) => TimeOfDaySeconds(gameTimeSeconds) / SecondsPerHour;

    /// <summary>Номер дня, начиная с 1.</summary>
    public static int DayNumber(float gameTimeSeconds) => (int)(gameTimeSeconds / SecondsPerDay) + 1;
}