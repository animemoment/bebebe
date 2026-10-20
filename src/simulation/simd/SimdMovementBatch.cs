using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Game.Core;

namespace Game.Simulation.Simd;

/// <summary>
/// Векторизованный SIMD-процессор кинематического шага агентов (PLAN.md §42).
/// Работает в паре с GpuAgentTransformSimulator:
/// - На каждом шаге читает ТОЛЬКО плоские непрерывные массивы PositionX/Y и StepTargetX/Y (0 обращений к 38.4 МБ массиву WaypointX/Y).
/// - Выполняет пакетное вычисление расстояния и нормализацию через аппаратные регистры Vector&lt;float&gt; (AVX2/SSE).
/// - Только для ~1-2% агентов, достигших промежуточной точки (dsq &lt;= reachSq), выставляет бит в StepReachedMask (12.5 КБ)
///   и подгружает следующую путевую точку из WaypointX/Y.
/// </summary>
public static class SimdMovementBatch
{
    /// <summary>
    /// Первичная синхронизация активных промежуточных точек StepTargetX/Y из текущих Waypoint'ов.
    /// Вызывается при инициализации или смене маршрута.
    /// </summary>
    public static void SyncAllStepTargets(AgentDataPool pool, int start, int end, float tileSize = 64f)
    {
        if (pool == null || start >= end)
            return;

        start = Math.Max(0, start);
        end = Math.Min(pool.Capacity, end);

        float[] stepX = pool.StepTargetX;
        float[] stepY = pool.StepTargetY;
        float[] tarX = pool.TargetPositionX;
        float[] tarY = pool.TargetPositionY;
        short[] wpX = pool.WaypointX;
        short[] wpY = pool.WaypointY;
        byte[] wpCount = pool.WaypointCount;
        byte[] wpIndex = pool.WaypointIndex;
        float halfTile = tileSize * 0.5f;

        for (int i = start; i < end; i++)
        {
            byte cnt = wpCount[i];
            byte idx = wpIndex[i];
            if (cnt > 0 && idx < cnt)
            {
                int wpPos = i * AgentPathConfig.MaxWaypoints + idx;
                stepX[i] = wpX[wpPos] * tileSize + halfTile;
                stepY[i] = wpY[wpPos] * tileSize + halfTile;
            }
            else
            {
                stepX[i] = tarX[i];
                stepY[i] = tarY[i];
            }
        }
    }

    /// <summary>
    /// Пакетное векторизованное обновление позиций в диапазоне [start, end).
    /// Возвращает количество агентов, достигших текущей промежуточной точки.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int UpdatePositionsRange(
        AgentDataPool pool,
        int start,
        int end,
        float stepLen,
        float reachSq = 256f,
        float tileSize = 64f)
    {
        if (pool == null || start >= end)
            return 0;

        start = Math.Max(0, start);
        end = Math.Min(pool.Capacity, end);

        float[] posX = pool.PositionX;
        float[] posY = pool.PositionY;
        float[] stepX = pool.StepTargetX;
        float[] stepY = pool.StepTargetY;
        float[] tarX = pool.TargetPositionX;
        float[] tarY = pool.TargetPositionY;
        short[] wpX = pool.WaypointX;
        short[] wpY = pool.WaypointY;
        byte[] wpCount = pool.WaypointCount;
        byte[] wpIndex = pool.WaypointIndex;
        uint[] reachedMask = pool.StepReachedMask;

        int vecSize = Vector<float>.Count;
        var vStepLen = new Vector<float>(stepLen);
        var vReachSq = new Vector<float>(reachSq);
        var vEps = new Vector<float>(1e-6f);

        int reachedCount = 0;
        int k = start;
        int simdEnd = end - (end - start) % vecSize;

        for (; k < simdEnd; k += vecSize)
        {
            var vCx = new Vector<float>(posX, k);
            var vCy = new Vector<float>(posY, k);
            var vTx = new Vector<float>(stepX, k);
            var vTy = new Vector<float>(stepY, k);

            var vDx = vTx - vCx;
            var vDy = vTy - vCy;
            var vDsq = vDx * vDx + vDy * vDy;

            // Быстрый путь (98%+ векторов): никто в пачке еще не дошел до точки.
            // Ни одного обращения к 38.4 МБ массиву WaypointX/Y!
            if (!Vector.LessThanOrEqualAny(vDsq, vReachSq))
            {
                var vDist = Vector.SquareRoot(Vector.Max(vDsq, vEps));
                var vScale = vStepLen / vDist;
                (vCx + vDx * vScale).CopyTo(posX, k);
                (vCy + vDy * vScale).CopyTo(posY, k);
            }
            else
            {
                for (int lane = 0; lane < vecSize; lane++)
                {
                    int i = k + lane;
                    if (StepOrAdvanceSingle(i, posX, posY, stepX, stepY, tarX, tarY, wpX, wpY, wpCount, wpIndex, reachedMask, stepLen, reachSq, tileSize))
                    {
                        reachedCount++;
                    }
                }
            }
        }

        // Остаток
        for (; k < end; k++)
        {
            if (StepOrAdvanceSingle(k, posX, posY, stepX, stepY, tarX, tarY, wpX, wpY, wpCount, wpIndex, reachedMask, stepLen, reachSq, tileSize))
            {
                reachedCount++;
            }
        }

        return reachedCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StepOrAdvanceSingle(
        int i,
        float[] posX,
        float[] posY,
        float[] stepX,
        float[] stepY,
        float[] tarX,
        float[] tarY,
        short[] wpX,
        short[] wpY,
        byte[] wpCount,
        byte[] wpIndex,
        uint[] reachedMask,
        float stepLen,
        float reachSq,
        float tileSize)
    {
        float cx = posX[i];
        float cy = posY[i];
        float tx = stepX[i];
        float ty = stepY[i];

        float dx = tx - cx;
        float dy = ty - cy;
        float dsq = dx * dx + dy * dy;

        if (dsq > reachSq)
        {
            float inv = stepLen / MathF.Sqrt(dsq);
            posX[i] = cx + dx * inv;
            posY[i] = cy + dy * inv;
            return false;
        }

        // Агент достиг текущей промежуточной точки: отмечаем в битовой маске и переключаем Waypoint
        int wordIdx = i >> 5;
        if ((uint)wordIdx < (uint)reachedMask.Length)
        {
            Interlocked.Or(ref reachedMask[wordIdx], 1u << (i & 31));
        }

        AdvanceAgentWaypoint(i, stepX, stepY, tarX, tarY, wpX, wpY, wpCount, wpIndex, tileSize);
        return true;
    }

    /// <summary>
    /// Переключает агента на следующую путевую точку и обновляет кэш StepTargetX/Y.
    /// Вызывается ТОЛЬКО при срабатывании бита в StepReachedMask (~1% агентов за шаг).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AdvanceAgentWaypoint(
        int i,
        float[] stepX,
        float[] stepY,
        float[] tarX,
        float[] tarY,
        short[] wpX,
        short[] wpY,
        byte[] wpCount,
        byte[] wpIndex,
        float tileSize = 64f)
    {
        byte cnt = wpCount[i];
        if (cnt > 0)
        {
            int nextIdx = wpIndex[i] + 1;
            if (nextIdx >= cnt)
            {
                wpCount[i] = 0;
                wpIndex[i] = 0;
                stepX[i] = tarX[i];
                stepY[i] = tarY[i];
            }
            else
            {
                wpIndex[i] = (byte)nextIdx;
                int wpPos = i * AgentPathConfig.MaxWaypoints + nextIdx;
                float halfTile = tileSize * 0.5f;
                stepX[i] = wpX[wpPos] * tileSize + halfTile;
                stepY[i] = wpY[wpPos] * tileSize + halfTile;
            }
        }
        else
        {
            stepX[i] = tarX[i];
            stepY[i] = tarY[i];
        }
    }

    /// <summary>
    /// Обрабатывает компактную битовую маску прибытия (StepReachedMask), полученную с GPU,
    /// и обновляет StepTargetX/Y только для тех агентов, у которых бит равен 1.
    /// </summary>
    public static int ProcessReachedBitmask(AgentDataPool pool, int agentCount, float tileSize = 64f)
    {
        if (pool == null || agentCount <= 0)
            return 0;

        agentCount = Math.Min(agentCount, pool.Capacity);
        int wordCount = (agentCount + 31) >> 5;
        uint[] mask = pool.StepReachedMask;
        float[] stepX = pool.StepTargetX;
        float[] stepY = pool.StepTargetY;
        float[] tarX = pool.TargetPositionX;
        float[] tarY = pool.TargetPositionY;
        short[] wpX = pool.WaypointX;
        short[] wpY = pool.WaypointY;
        byte[] wpCount = pool.WaypointCount;
        byte[] wpIndex = pool.WaypointIndex;

        int processed = 0;
        for (int w = 0; w < wordCount; w++)
        {
            uint word = mask[w];
            if (word == 0u)
                continue;

            mask[w] = 0u; // Сбрасываем маску после обработки
            int baseIdx = w << 5;
            while (word != 0u)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                int agentIdx = baseIdx + bit;
                if (agentIdx < agentCount)
                {
                    AdvanceAgentWaypoint(agentIdx, stepX, stepY, tarX, tarY, wpX, wpY, wpCount, wpIndex, tileSize);
                    processed++;
                }
                word &= word - 1u; // Сброс младшего установленного бита
            }
        }

        return processed;
    }
}
