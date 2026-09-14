using System;
using System.Numerics;

namespace Game.Core;

/// <summary>
/// SoA (Structure of Arrays) пул данных всех агентов с поддержкой универсальной системы задач.
/// </summary>
public sealed class AgentDataPool
{
    public readonly int Capacity;

    // Позиции и перемещение
    public readonly float[] PositionX;
    public readonly float[] PositionY;
    public readonly float[] TargetPositionX;
    public readonly float[] TargetPositionY;
    public readonly float[] LastPositionX;
    public readonly float[] LastPositionY;
    public readonly float[] Speed;

    // Состояния и задачи
    public readonly AgentState[] States;
    public readonly JobTypeId[] CurrentJobType;
    public readonly int[] CurrentJobId;
    public readonly float[] StuckTimer;
    public readonly float[] WorkProgress;
    public readonly float[] CellStayTime;
    public readonly float[] JobSearchTimer;
    /// <summary>
    /// P0-фикс thundering herd: кулдаун повторной попытки поиска еды после неудачи (30-60с).
    /// Отдельно от JobSearchTimer, чтобы backoff не затирался таймером блуждания (6с).
    /// Тикает только в Idle-бакете (x4), проверяется до TryAssignNeedsBehavior.
    /// </summary>
    public readonly float[] NeedsRetryTimer;

    // Универсальные координаты задачи
    public readonly int[] SourceCellX;
    public readonly int[] SourceCellY;
    public readonly int[] TargetCellX;
    public readonly int[] TargetCellY;
    public readonly int[] ReservedItemCount;

    // Текущая клетка
    public readonly int[] CurrentCellX;
    public readonly int[] CurrentCellY;

    // Инвентарь
    public readonly ItemId[] CarriedItemId;
    public readonly int[] CarriedItemCount;
    public readonly ToolRequirement[] EquippedTools;

    // Связный список для пространственной сетки занятых агентов
    public readonly int[] NextInSpatialCell;

    // Иерархический поиск пути: буфер путевых точек (упакованные тайловые координаты).
    // Инвариант: для агента i его точки лежат в срезах [i*MaxWaypoints .. i*MaxWaypoints + WaypointCount[i]).
    public readonly short[] WaypointX;
    public readonly short[] WaypointY;
    public readonly byte[] WaypointCount;
    public readonly byte[] WaypointIndex;
    /// <summary>Кулдаун повторного запроса пути при неудаче (сек). 0 — можно запрашивать.</summary>
    public readonly float[] PathRequestCooldown;

    // Категория последней взятой работы (JobCategory) — для «профессиональной» привязки
    // при раздаче задач (снижает частоту переключений между профессиями).
    public readonly int[] LastJobCategory;

    // Связный список для сетки свободных агентов (IdleWorkerSpatialGrid)
    public readonly int[] NextInIdleCell;
    public readonly int[] PrevInIdleCell;

    // Базовые потребности (SoA-расширение): все в диапазоне 0–100.
    /// <summary>Голод: 0 = сыт, 100 = голоден. Растёт +0.5/игровую минуту.</summary>
    public readonly float[] Hunger;
    /// <summary>Сонливость: 0 = отдохнул, 100 = хочет спать. Растёт +0.3/мин (пока нет состояния сна — всегда).</summary>
    public readonly float[] Sleep;
    /// <summary>Усталость: накапливается от работы (+0.2/мин в Working), восстанавливается в Idle.</summary>
    public readonly float[] Fatigue;
    /// <summary>Удовлетворённость окружением: 0 = ужасно, 100 = отлично. Обновляется редко (раз в игровой час).</summary>
    public readonly float[] EnvironmentSatisfaction;
    /// <summary>Общее настроение: вычисляется из всех факторов, 0 = депрессия, 100 = счастлив.</summary>
    public readonly float[] Mood;
    /// <summary>
    /// Текущая поведенческая реакция на потребности (лёгкий NeedsJobSystem).
    /// None = обычное поведение (работа/блуждание). Остальное — агент занят нуждой
    /// (движется в состоянии Evacuating, диспетчер его не трогает).
    /// </summary>
    public readonly NeedBehavior[] NeedsBehavior;

    public AgentDataPool(int capacity)
    {
        Capacity = capacity;

        PositionX = new float[capacity];
        PositionY = new float[capacity];
        TargetPositionX = new float[capacity];
        TargetPositionY = new float[capacity];
        LastPositionX = new float[capacity];
        LastPositionY = new float[capacity];
        Speed = new float[capacity];

        States = new AgentState[capacity];
        CurrentJobType = new JobTypeId[capacity];
        CurrentJobId = new int[capacity];
        Array.Fill(CurrentJobId, -1);

        StuckTimer = new float[capacity];
        WorkProgress = new float[capacity];
        CellStayTime = new float[capacity];
        JobSearchTimer = new float[capacity];
        NeedsRetryTimer = new float[capacity];

        SourceCellX = new int[capacity];
        SourceCellY = new int[capacity];
        TargetCellX = new int[capacity];
        TargetCellY = new int[capacity];
        ReservedItemCount = new int[capacity];

        CurrentCellX = new int[capacity];
        CurrentCellY = new int[capacity];

        CarriedItemId = new ItemId[capacity];
        CarriedItemCount = new int[capacity];
        EquippedTools = new ToolRequirement[capacity];

        NextInSpatialCell = new int[capacity];
        NextInIdleCell = new int[capacity];
        PrevInIdleCell = new int[capacity];
        Array.Fill(NextInIdleCell, -1);
        Array.Fill(PrevInIdleCell, -1);

        WaypointX = new short[capacity * AgentPathConfig.MaxWaypoints];
        WaypointY = new short[capacity * AgentPathConfig.MaxWaypoints];
        WaypointCount = new byte[capacity];
        WaypointIndex = new byte[capacity];
        PathRequestCooldown = new float[capacity];

        LastJobCategory = new int[capacity];
        Array.Fill(LastJobCategory, -1);

        // Базовые потребности: старт сытый/отдохнувший, окружение нейтрально-хорошее.
        Hunger = new float[capacity];
        Sleep = new float[capacity];
        Fatigue = new float[capacity];
        EnvironmentSatisfaction = new float[capacity];
        Array.Fill(EnvironmentSatisfaction, AgentNeedsConfig.InitialEnvironmentSatisfaction);
        Mood = new float[capacity];
        Array.Fill(Mood, AgentNeedsConfig.InitialMood);
        NeedsBehavior = new NeedBehavior[capacity];
    }

    public Vector2 GetPosition(int index) => new(PositionX[index], PositionY[index]);

    public void SetPosition(int index, Vector2 pos)
    {
        PositionX[index] = pos.X;
        PositionY[index] = pos.Y;
    }

    public void CopyPositionsTo(Vector2[] destination)
    {
        int count = Math.Min(Capacity, destination.Length);
        for (int i = 0; i < count; i++)
        {
            destination[i] = new Vector2(PositionX[i], PositionY[i]);
        }
    }
}

/// <summary>
/// Настройки иерархического поиска пути для агентов.
/// Отдельный статический класс — по образцу WorldTime (в Godot .NET константы
/// статического класса доступны по имени класса из любого места).
/// </summary>
public static class AgentPathConfig
{
    /// <summary>Максимум путевых точек на агента (96 х 2 байта = путь через всю карту 512x512).</summary>
    public const int MaxWaypoints = 96;
}

/// <summary>
/// Параметры роста базовых потребностей (всё в ИГРОВОМ времени — deltaTime шагов
/// симуляции уже включает скорость через accumulator, отдельно на _speedMultiplier
/// домножать НЕ нужно).
/// Диапазоны всех потребностей: 0–100.
/// </summary>
public static class AgentNeedsConfig
{
    /// <summary>Прирост голода за игровую секунду (+0.5/игровую минуту).</summary>
    public const float HungerPerGameSec = 0.5f / 60f;
    /// <summary>Прирост сонливости за игровую секунду (+0.3/мин, пока нет состояния сна — всегда).</summary>
    public const float SleepPerGameSec = 0.3f / 60f;
    /// <summary>Прирост усталости за игровую секунду работы (+0.2/мин в Working).</summary>
    public const float FatigueWorkPerGameSec = 0.2f / 60f;
    /// <summary>Восстановление усталости за игровую секунду безделья (-0.4/мин в Idle).</summary>
    public const float FatigueIdleRecoveryPerGameSec = 0.4f / 60f;
    /// <summary>Замедление движения от усталости: Fatigue &gt; 80 → скорость x0.85.</summary>
    public const float FatigueSlowThreshold = 80f;
    public const float FatigueSlowMultiplier = 0.85f;
    /// <summary>Пороги поведенческих реакций (лёгкий NeedsJobSystem).</summary>
    public const float HungerSeekThreshold = 70f;
    public const float SleepRestThreshold = 80f;
    public const float FatigueRestThreshold = 80f;
    public const float EnvironmentMigrateThreshold = 40f;
    /// <summary>Съеденное зерно снимает голода: единиц зерна за приём пищи.</summary>
    public const int FoodEatGrainCount = 2;
    public const float FoodEatHungerRestore = 60f;
    /// <summary>Скорость отдыха на месте (сон/усталость в секунду): 10/сон, 15/усталость.</summary>
    public const float RestSleepRecoveryPerGameSec = 10f;
    public const float RestFatigueRecoveryPerGameSec = 15f;
    /// <summary>Радиус поиска еды/миграции в тайлах (вокруг агента).</summary>
    public const int FoodSearchRadiusTiles = 40;
    public const int MigrateRadiusTiles = 24;
    /// <summary>Период обновления окружения в игровых секундах (= 1 игровой час).</summary>
    public const float EnvironmentUpdatePeriodGameSec = 500f;
    /// <summary>Заглушка окружения, пока нет семплинга качества (дом/красота/вода).</summary>
    public const float DefaultEnvironmentSatisfaction = 80f;
    public const float InitialEnvironmentSatisfaction = 80f;
    public const float InitialMood = 100f;
    /// <summary>Веса штрафа настроения: Mood = 100 - (Hunger*Wh + Sleep*Ws + Fatigue*Wf + (100-Env)*We).</summary>
    public const float MoodWeightHunger = 0.35f;
    public const float MoodWeightSleep = 0.25f;
    public const float MoodWeightFatigue = 0.20f;
    public const float MoodWeightEnvironment = 0.20f;
}