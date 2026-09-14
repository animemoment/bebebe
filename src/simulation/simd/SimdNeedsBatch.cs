using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Game.Core;

namespace Game.Simulation.Simd;

/// <summary>
/// Векторизованный батч обновления потребностей агентов (PLAN.md §15.2, шаг G2).
/// Чистый C#, без зависимости от Godot — тестируется без движка.
/// Семантика побайтово равна скалярному AgentSimulationThread.UpdateNeedsSingleAgent:
/// те же формулы, те же clamp'ы; Mood считается ПОСЛЕ обновления всех компонентов
/// (штраф берётся из уже обновлённых Hunger/Sleep/Fatigue/EnvironmentSatisfaction).
/// Что векторизовано: Hunger/Sleep (чистые FMA + верхний clamp 100) и Mood
/// (взвешенная сумма + clamp 0..100) — через System.Numerics.Vector&lt;float&gt;
/// (переносимый SIMD; при !Vector.IsHardwareAccelerated — скалярный fallback).
/// Clamp'ы — NaN-детерминированные через Vector.GreaterThan/LessThan +
/// Vector.ConditionalSelect (без Vector.Min/Max, у которых платформенно-зависимая
/// NaN-семантика): при NaN предикат false и выбирается исходное значение,
/// как в скалярном тернарнике `v &gt; 100f ? 100f : v`; -0.0 также сохраняется.
/// Что осталось скаляром и почему: ветка Fatigue зависит от AgentState[i]
/// (Working — рост, Idle — восстановление, остальные — без изменений);
/// ветвление по enum не векторизуется, поэтому Fatigue — скалярный подпроход.
/// Zero-alloc: только Span поверх массивов пула (stack-only ref struct)
/// и Vector&lt;float&gt; (struct, куча не трогается); heap-аллокаций нет.
/// </summary>
public static class SimdNeedsBatch
{
    /// <summary>
    /// Поэлементный путь: один агент. Та же семантика, что старый
    /// AgentSimulationThread.UpdateNeedsSingleAgent (скаляр, AggressiveInlining).
    /// Используется из BookkeepSingleAgent (поэлементный проход Phase3a).
    /// </summary>
    /// <exception cref="ArgumentNullException">pool is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">index вне [0, Capacity); deltaTime &lt; 0.</exception>
    /// <exception cref="ArgumentException">deltaTime — NaN или Infinity.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateNeedsSingle(AgentDataPool pool, int index, float deltaTime, bool updateEnv)
    {
        if (pool == null)
            throw new ArgumentNullException(nameof(pool));
        if ((uint)index >= (uint)pool.Capacity)
            throw new ArgumentOutOfRangeException(nameof(index), "index вне [0, Capacity).");
        ValidateDeltaTime(deltaTime);

        // Голод: +0.5/игровую минуту (только верхний clamp — как в скаляре).
        float hunger = pool.Hunger[index] + AgentNeedsConfig.HungerPerGameSec * deltaTime;
        pool.Hunger[index] = hunger > 100f ? 100f : hunger;

        // Сон: +0.3/мин (только верхний clamp — как в скаляре).
        float sleep = pool.Sleep[index] + AgentNeedsConfig.SleepPerGameSec * deltaTime;
        pool.Sleep[index] = sleep > 100f ? 100f : sleep;

        // Усталость: +0.2/мин в Working (верхний clamp), восстановление в Idle (нижний clamp).
        AgentState state = pool.States[index];
        if (state == AgentState.Working)
        {
            float fatigue = pool.Fatigue[index] + AgentNeedsConfig.FatigueWorkPerGameSec * deltaTime;
            pool.Fatigue[index] = fatigue > 100f ? 100f : fatigue;
        }
        else if (state == AgentState.Idle)
        {
            float fatigue = pool.Fatigue[index] - AgentNeedsConfig.FatigueIdleRecoveryPerGameSec * deltaTime;
            pool.Fatigue[index] = fatigue < 0f ? 0f : fatigue;
        }

        // Окружение: редко (раз в игровой час). Пока заглушка.
        if (updateEnv)
        {
            pool.EnvironmentSatisfaction[index] = AgentNeedsConfig.DefaultEnvironmentSatisfaction;
        }

        // Настроение: 100 - взвешенная сумма штрафов (clamp 0..100).
        float penalty =
            pool.Hunger[index] * AgentNeedsConfig.MoodWeightHunger +
            pool.Sleep[index] * AgentNeedsConfig.MoodWeightSleep +
            pool.Fatigue[index] * AgentNeedsConfig.MoodWeightFatigue +
            (100f - pool.EnvironmentSatisfaction[index]) * AgentNeedsConfig.MoodWeightEnvironment;
        float mood = 100f - penalty;
        pool.Mood[index] = mood < 0f ? 0f : (mood > 100f ? 100f : mood);
    }

    /// <summary>
    /// Диапазонный векторизованный путь для i в [start, end):
    /// интегрирован в Phase3a батчинг (шаг G3: два диапазонных прохода
    /// через DynamicWorkBalancer.ForEachRange + поэлементный остаток).
    /// Порядок сложений в Mood — тот же, что в скаляре
    /// (((H*Wh + S*Ws) + F*Wf) + (100-E)*We), поэтому результат побитово совпадает.
    /// </summary>
    /// <exception cref="ArgumentNullException">pool is null.</exception>
    /// <exception cref="ArgumentNullException">Массив пула Hunger is null.</exception>
    /// <exception cref="ArgumentNullException">Массив пула Sleep is null.</exception>
    /// <exception cref="ArgumentNullException">Массив пула States is null.</exception>
    /// <exception cref="ArgumentNullException">Массив пула Fatigue is null.</exception>
    /// <exception cref="ArgumentNullException">Массив пула EnvironmentSatisfaction is null.</exception>
    /// <exception cref="ArgumentNullException">Массив пула Mood is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">start/end вне [0, Capacity]; end &lt; start; deltaTime &lt; 0; длина массива пула меньше end.</exception>
    /// <exception cref="ArgumentException">deltaTime — NaN или Infinity.</exception>
    public static void UpdateNeeds(AgentDataPool pool, int start, int end, float deltaTime, bool updateEnv)
    {
        if (pool == null)
            throw new ArgumentNullException(nameof(pool));
        if (start < 0 || start > pool.Capacity)
            throw new ArgumentOutOfRangeException(nameof(start), "start вне [0, Capacity].");
        if (end < 0 || end > pool.Capacity)
            throw new ArgumentOutOfRangeException(nameof(end), "end вне [0, Capacity].");
        if (end < start)
            throw new ArgumentOutOfRangeException(nameof(end), "end < start.");
        ValidateDeltaTime(deltaTime);

        // G3-F3: validate-then-mutate — все массивы проверяются ДО любых записей,
        // чтобы повреждённый пул давал fail-fast без единой записи (частичной мутации нет).
        // Per-i try/catch здесь запрещён (убил бы векторизацию).
        if (pool.Hunger == null)
            throw new ArgumentNullException(nameof(pool.Hunger), "Массив пула Hunger is null.");
        if (pool.Sleep == null)
            throw new ArgumentNullException(nameof(pool.Sleep), "Массив пула Sleep is null.");
        if (pool.States == null)
            throw new ArgumentNullException(nameof(pool.States), "Массив пула States is null.");
        if (pool.Fatigue == null)
            throw new ArgumentNullException(nameof(pool.Fatigue), "Массив пула Fatigue is null.");
        if (pool.EnvironmentSatisfaction == null)
            throw new ArgumentNullException(nameof(pool.EnvironmentSatisfaction), "Массив пула EnvironmentSatisfaction is null.");
        if (pool.Mood == null)
            throw new ArgumentNullException(nameof(pool.Mood), "Массив пула Mood is null.");
        if (pool.Hunger.Length < end)
            throw new ArgumentOutOfRangeException(nameof(pool.Hunger), "Длина массива пула Hunger меньше end.");
        if (pool.Sleep.Length < end)
            throw new ArgumentOutOfRangeException(nameof(pool.Sleep), "Длина массива пула Sleep меньше end.");
        if (pool.States.Length < end)
            throw new ArgumentOutOfRangeException(nameof(pool.States), "Длина массива пула States меньше end.");
        if (pool.Fatigue.Length < end)
            throw new ArgumentOutOfRangeException(nameof(pool.Fatigue), "Длина массива пула Fatigue меньше end.");
        if (pool.EnvironmentSatisfaction.Length < end)
            throw new ArgumentOutOfRangeException(nameof(pool.EnvironmentSatisfaction), "Длина массива пула EnvironmentSatisfaction меньше end.");
        if (pool.Mood.Length < end)
            throw new ArgumentOutOfRangeException(nameof(pool.Mood), "Длина массива пула Mood меньше end.");

        int len = end - start;
        if (len == 0)
            return;

        // Добавки предвычисляются один раз — тот же порядок операций, что в скаляре
        // (rate * deltaTime, затем + к текущему значению поэлементно).
        float hungerAdd = AgentNeedsConfig.HungerPerGameSec * deltaTime;
        float sleepAdd = AgentNeedsConfig.SleepPerGameSec * deltaTime;

        // Span поверх массивов пула — без аллокаций (stack-only).
        var spanHunger = new Span<float>(pool.Hunger, start, len);
        var spanSleep = new Span<float>(pool.Sleep, start, len);

        bool vectorized = Vector.IsHardwareAccelerated && len >= Vector<float>.Count;
        if (vectorized)
        {
            AddCapped(spanHunger, hungerAdd);
            AddCapped(spanSleep, sleepAdd);
        }
        else
        {
            AddCappedScalar(spanHunger, hungerAdd);
            AddCappedScalar(spanSleep, sleepAdd);
        }

        // Усталость — скалярный подпроход: ветвление по AgentState не векторизуется.
        float fatigueWorkAdd = AgentNeedsConfig.FatigueWorkPerGameSec * deltaTime;
        float fatigueIdleSub = AgentNeedsConfig.FatigueIdleRecoveryPerGameSec * deltaTime;
        AgentState[] states = pool.States;
        float[] fatigue = pool.Fatigue;
        for (int i = start; i < end; i++)
        {
            AgentState state = states[i];
            if (state == AgentState.Working)
            {
                float f = fatigue[i] + fatigueWorkAdd;
                fatigue[i] = f > 100f ? 100f : f;
            }
            else if (state == AgentState.Idle)
            {
                float f = fatigue[i] - fatigueIdleSub;
                fatigue[i] = f < 0f ? 0f : f;
            }
        }

        // Окружение — одна заливка на диапазон (Span.Fill векторизован внутри).
        var spanEnv = new Span<float>(pool.EnvironmentSatisfaction, start, len);
        if (updateEnv)
        {
            spanEnv.Fill(AgentNeedsConfig.DefaultEnvironmentSatisfaction);
        }

        // Настроение — ПОСЛЕ обновления всех компонентов (как в скаляре).
        var spanFatigue = new Span<float>(pool.Fatigue, start, len);
        var spanMood = new Span<float>(pool.Mood, start, len);
        if (vectorized)
        {
            UpdateMoodVectorized(spanHunger, spanSleep, spanFatigue, spanEnv, spanMood);
        }
        else
        {
            UpdateMoodScalar(spanHunger, spanSleep, spanFatigue, spanEnv, spanMood);
        }
    }

    /// <summary>
    /// Поэлементный cell-tracking: один агент. Та же семантика, что скалярный блок
    /// AgentSimulationThread.BookkeepSingleAgent (строки cell-tracking): вычисляет
    /// cx/cy из PositionX/Y как `(int)pos >> 6`, сравнивает с CurrentCellX/Y;
    /// совпало — CellStayTime += deltaTime, иначе — запись новой клетки,
    /// сброс CellStayTime в 0 и возврат true (cellChanged).
    /// Возвращает cellChanged для Idle-ветки (UpdateWorkerChunk) вызывающей стороны.
    /// </summary>
    /// <exception cref="ArgumentNullException">pool is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">index вне [0, Capacity); deltaTime &lt; 0.</exception>
    /// <exception cref="ArgumentException">deltaTime — NaN или Infinity.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateCellSingle(AgentDataPool pool, int index, float deltaTime)
    {
        if (pool == null)
            throw new ArgumentNullException(nameof(pool));
        if ((uint)index >= (uint)pool.Capacity)
            throw new ArgumentOutOfRangeException(nameof(index), "index вне [0, Capacity).");
        ValidateDeltaTime(deltaTime);

        int cx = (int)pool.PositionX[index] >> 6;
        int cy = (int)pool.PositionY[index] >> 6;

        if (pool.CurrentCellX[index] == cx && pool.CurrentCellY[index] == cy)
        {
            pool.CellStayTime[index] += deltaTime;
            return false;
        }

        pool.CurrentCellX[index] = cx;
        pool.CurrentCellY[index] = cy;
        pool.CellStayTime[index] = 0f;
        return true;
    }

    /// <summary>
    /// Диапазонный cell-tracking для i в [start, end): семантика побайтово равна
    /// скалярному блоку BookkeepSingleAgent (те же cx/cy, то же Stay += dt / сброс в 0).
    /// Интегрирован в Phase3a батчинг (шаг G3, проход (б) через ForEachRange).
    /// РЕАЛИЗАЦИЯ — СКАЛЯРНЫЙ цикл, и это честно: (1) float→int каст + арифметический
    /// сдвиг `>> 6` не векторизуется через Vector&lt;float&gt; (нужны integer-векторы
    /// Vector&lt;int&gt; с отдельной загрузкой/конвертацией — выигрыш съедается);
    /// (2) ветвящаяся запись в int-массивы CurrentCellX/Y (gather/scatter без gain);
    /// (3) всего ~5 операций на агента — проход memory-bound (потолок — пропускная
    /// способность памяти, а не ALU). Выигрыш G3 — не SIMD, а батчинг: вынос Needs+Cells
    /// в два последовательных диапазонных прохода улучшает локальность кэша
    /// (каждый проход трогает свой набор массивов целиком) против чередования
    /// в поэлементном пути. Zero-alloc: heap-аллокаций нет.
    /// </summary>
    /// <exception cref="ArgumentNullException">pool is null.</exception>
    /// <exception cref="ArgumentNullException">Массив пула PositionX is null.</exception>
    /// <exception cref="ArgumentNullException">Массив пула PositionY is null.</exception>
    /// <exception cref="ArgumentNullException">Массив пула CurrentCellX is null.</exception>
    /// <exception cref="ArgumentNullException">Массив пула CurrentCellY is null.</exception>
    /// <exception cref="ArgumentNullException">Массив пула CellStayTime is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">start/end вне [0, Capacity]; end &lt; start; deltaTime &lt; 0; длина массива пула меньше end.</exception>
    /// <exception cref="ArgumentException">deltaTime — NaN или Infinity.</exception>
    public static void UpdateCells(AgentDataPool pool, int start, int end, float deltaTime)
    {
        if (pool == null)
            throw new ArgumentNullException(nameof(pool));
        if (start < 0 || start > pool.Capacity)
            throw new ArgumentOutOfRangeException(nameof(start), "start вне [0, Capacity].");
        if (end < 0 || end > pool.Capacity)
            throw new ArgumentOutOfRangeException(nameof(end), "end вне [0, Capacity].");
        if (end < start)
            throw new ArgumentOutOfRangeException(nameof(end), "end < start.");
        ValidateDeltaTime(deltaTime);

        // G3-F3: validate-then-mutate — все массивы проверяются ДО любых записей,
        // чтобы повреждённый пул давал fail-fast без единой записи (частичной мутации нет).
        // Per-i try/catch здесь запрещён (убил бы векторизацию).
        if (pool.PositionX == null)
            throw new ArgumentNullException(nameof(pool.PositionX), "Массив пула PositionX is null.");
        if (pool.PositionY == null)
            throw new ArgumentNullException(nameof(pool.PositionY), "Массив пула PositionY is null.");
        if (pool.CurrentCellX == null)
            throw new ArgumentNullException(nameof(pool.CurrentCellX), "Массив пула CurrentCellX is null.");
        if (pool.CurrentCellY == null)
            throw new ArgumentNullException(nameof(pool.CurrentCellY), "Массив пула CurrentCellY is null.");
        if (pool.CellStayTime == null)
            throw new ArgumentNullException(nameof(pool.CellStayTime), "Массив пула CellStayTime is null.");
        if (pool.PositionX.Length < end)
            throw new ArgumentOutOfRangeException(nameof(pool.PositionX), "Длина массива пула PositionX меньше end.");
        if (pool.PositionY.Length < end)
            throw new ArgumentOutOfRangeException(nameof(pool.PositionY), "Длина массива пула PositionY меньше end.");
        if (pool.CurrentCellX.Length < end)
            throw new ArgumentOutOfRangeException(nameof(pool.CurrentCellX), "Длина массива пула CurrentCellX меньше end.");
        if (pool.CurrentCellY.Length < end)
            throw new ArgumentOutOfRangeException(nameof(pool.CurrentCellY), "Длина массива пула CurrentCellY меньше end.");
        if (pool.CellStayTime.Length < end)
            throw new ArgumentOutOfRangeException(nameof(pool.CellStayTime), "Длина массива пула CellStayTime меньше end.");

        if (end == start)
            return;

        float[] posX = pool.PositionX;
        float[] posY = pool.PositionY;
        int[] curX = pool.CurrentCellX;
        int[] curY = pool.CurrentCellY;
        float[] stay = pool.CellStayTime;
        for (int i = start; i < end; i++)
        {
            int cx = (int)posX[i] >> 6;
            int cy = (int)posY[i] >> 6;
            if (curX[i] == cx && curY[i] == cy)
            {
                stay[i] += deltaTime;
            }
            else
            {
                curX[i] = cx;
                curY[i] = cy;
                stay[i] = 0f;
            }
        }
    }

    /// <summary>Проверка deltaTime: NaN/Infinity запрещены, отрицательные запрещены.</summary>
    /// <exception cref="ArgumentException">NaN или Infinity.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Отрицательное значение.</exception>
    private static void ValidateDeltaTime(float deltaTime)
    {
        if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
            throw new ArgumentException("deltaTime обязан быть конечным числом.", nameof(deltaTime));
        if (deltaTime < 0f)
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "deltaTime не может быть отрицательным.");
    }

    /// <summary>Векторизованное: span[i] = span[i] + add &gt; 100 ? 100 : span[i] + add. Только верхний clamp (как в скаляре). NaN-детерминировано через ConditionalSelect (без Vector.Min).</summary>
    private static void AddCapped(Span<float> span, float add)
    {
        int n = Vector<float>.Count;
        int limit = span.Length - span.Length % n;
        var vAdd = new Vector<float>(add);
        var vCap = new Vector<float>(100f);
        for (int k = 0; k < limit; k += n)
        {
            Vector<float> t = new Vector<float>(span.Slice(k)) + vAdd;
            var mask = Vector.GreaterThan(t, vCap);
            Vector<float> r = Vector.ConditionalSelect(mask, vCap, t);
            r.CopyTo(span.Slice(k));
        }
        for (int k = limit; k < span.Length; k++)
        {
            float v = span[k] + add;
            span[k] = v > 100f ? 100f : v;
        }
    }

    /// <summary>Скалярный fallback для AddCapped (нет SIMD или короткий диапазон).</summary>
    private static void AddCappedScalar(Span<float> span, float add)
    {
        for (int k = 0; k < span.Length; k++)
        {
            float v = span[k] + add;
            span[k] = v > 100f ? 100f : v;
        }
    }

    /// <summary>
    /// Векторизованное Mood. Порядок сложений — как в скаляре:
    /// ((H*Wh + S*Ws) + F*Wf) + (100-E)*We; затем 100 - penalty, clamp 0..100.
    /// </summary>
    private static void UpdateMoodVectorized(
        Span<float> hunger, Span<float> sleep, Span<float> fatigue, Span<float> env, Span<float> mood)
    {
        int n = Vector<float>.Count;
        int limit = mood.Length - mood.Length % n;
        var wH = new Vector<float>(AgentNeedsConfig.MoodWeightHunger);
        var wS = new Vector<float>(AgentNeedsConfig.MoodWeightSleep);
        var wF = new Vector<float>(AgentNeedsConfig.MoodWeightFatigue);
        var wE = new Vector<float>(AgentNeedsConfig.MoodWeightEnvironment);
        var hundred = new Vector<float>(100f);
        Vector<float> zero = Vector<float>.Zero;
        for (int k = 0; k < limit; k += n)
        {
            var h = new Vector<float>(hunger.Slice(k));
            var s = new Vector<float>(sleep.Slice(k));
            var f = new Vector<float>(fatigue.Slice(k));
            var e = new Vector<float>(env.Slice(k));
            Vector<float> p = h * wH + s * wS;
            p += f * wF;
            p += (hundred - e) * wE;
            Vector<float> m = hundred - p;
            var hi = Vector.GreaterThan(m, hundred);
            m = Vector.ConditionalSelect(hi, hundred, m);
            var lo = Vector.LessThan(m, zero);
            m = Vector.ConditionalSelect(lo, zero, m);
            m.CopyTo(mood.Slice(k));
        }
        UpdateMoodScalar(hunger.Slice(limit), sleep.Slice(limit), fatigue.Slice(limit), env.Slice(limit), mood.Slice(limit));
    }

    /// <summary>Скалярный Mood (fallback + хвост векторизованного прохода). Формула — как в скаляре.</summary>
    private static void UpdateMoodScalar(
        Span<float> hunger, Span<float> sleep, Span<float> fatigue, Span<float> env, Span<float> mood)
    {
        for (int k = 0; k < mood.Length; k++)
        {
            float penalty =
                hunger[k] * AgentNeedsConfig.MoodWeightHunger +
                sleep[k] * AgentNeedsConfig.MoodWeightSleep +
                fatigue[k] * AgentNeedsConfig.MoodWeightFatigue +
                (100f - env[k]) * AgentNeedsConfig.MoodWeightEnvironment;
            float m = 100f - penalty;
            mood[k] = m < 0f ? 0f : (m > 100f ? 100f : m);
        }
    }
}
