namespace Game.Core;

/// <summary>
/// Поведенческая реакция агента на потребности (лёгкий NeedsJobSystem, без новых JobType).
/// Хранится в SoA <see cref="AgentDataPool.NeedsBehavior"/>. Транспорт — существующее
/// состояние <see cref="AgentState.Evacuating"/> (диспетчер его игнорирует),
/// поэтому агент не будет отобран на работу посреди удовлетворения нужды.
/// </summary>
public enum NeedBehavior : byte
{
    None = 0,
    /// <summary>Идёт к ближайшему зерну (земля/склад) и ест. Триггер: Hunger &gt; 70.</summary>
    SeekingFood = 1,
    /// <summary>Стоит/отдыхает на месте (кроватей/скамеек в игре нет). Триггер: Sleep &gt; 80 или Fatigue &gt; 80.</summary>
    Resting = 2,
    /// <summary>Миграция внутри города при плохом окружении. Триггер: Environment &lt; 40.</summary>
    Migrating = 3
}
