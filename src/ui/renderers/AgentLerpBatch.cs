using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Game.UI;

/// <summary>
/// Lerp-батч заливки инстансов агентов (PLAN.md §15.5, шаг G3-вторая половина).
/// Чистый C#, без зависимости от Godot (нет using Godot) — тестируется без движка.
/// Вынесенная из <c>AgentRenderer._Process</c> рендерная логика: интерполяция
/// prev→target и заливка MultiMesh-буфера (8 float на инстанс).
/// Zero-alloc: только Span поверх существующих массивов рендерера (stack-only
/// ref struct) и Vector&lt;float&gt; (struct, куча не трогается); heap-аллокаций нет.
/// </summary>
/// <remarks>
/// Формула lerp зафиксирована: <c>prev + (target - prev) * factor</c> покомпонентно,
/// что совпадает со скалярной семантикой <c>Mathf.Lerp(a, b, t) = a + (b - a) * t</c>
/// (Godot Mathf.Lerp именно такой). Порядок операций внутри компоненты тот же,
/// SIMD не переупорядочивает вычисления внутри линии — результат побитово равен
/// старому скалярному циклу. При NaN в позициях обе версии дают NaN тем же путём
/// (NaN-детерминизм через саму формулу, без ConditionalSelect: позиции NaN быть
/// не должно — симуляция пишет конечные значения, а factor валидируется).
/// Что векторизовано реально: <see cref="Interpolate"/> — плотный покомпонентный
/// проход по reinterpret-касту <c>MemoryMarshal.Cast&lt;Vector2, float&gt;</c>
/// (System.Numerics.Vector2 — два float подряд, Sequential layout) через
/// Vector&lt;float&gt;; при !Vector.IsHardwareAccelerated — скалярный fallback,
/// хвост — всегда скаляр. Что осталось скаляром честно: <see cref="FillRenderBuffer"/>
/// и <see cref="LerpFill"/> — заливка со stride-8 (scatter 8 float на инстанс:
/// единичная матрица + трансляция) memory-bound, SIMD-гейна нет (потолок —
/// пропускная способность памяти, а не ALU); единый цикл даёт выигрыш только
/// как тестируемая точка без дублирования, а не как векторизация.
/// </remarks>
public static class AgentLerpBatch
{
    /// <summary>
    /// Векторизованная фиксация интерполяции: <c>prev[i] = prev[i] + (target[i] - prev[i]) * factor</c>
    /// покомпонентно. Замена скалярного цикла drain в <c>AgentRenderer._Process</c>
    /// (фиксация текущего положения перед подменой снапшота).
    /// </summary>
    /// <exception cref="ArgumentException">Длины prev/target не совпадают; factor — NaN или Infinity.</exception>
    public static void Interpolate(Span<Vector2> prev, ReadOnlySpan<Vector2> target, float factor)
    {
        if (prev.Length != target.Length)
            throw new ArgumentException("Длины prev и target обязаны совпадать.");
        ValidateFactor(factor);
        if (prev.Length == 0)
            return;

        // Плотный float-вид: 2 float на позицию, без копий и аллокаций.
        var prevF = MemoryMarshal.Cast<Vector2, float>(prev);
        var targetF = MemoryMarshal.Cast<Vector2, float>(target);

        if (Vector.IsHardwareAccelerated && prevF.Length >= Vector<float>.Count)
        {
            int n = Vector<float>.Count;
            int limit = prevF.Length - prevF.Length % n;
            var vFactor = new Vector<float>(factor);
            for (int k = 0; k < limit; k += n)
            {
                var vPrev = new Vector<float>(prevF.Slice(k));
                var vTarget = new Vector<float>(targetF.Slice(k));
                (vPrev + (vTarget - vPrev) * vFactor).CopyTo(prevF.Slice(k));
            }
            // Хвост — скаляр, та же формула.
            for (int k = limit; k < prevF.Length; k++)
                prevF[k] = prevF[k] + (targetF[k] - prevF[k]) * factor;
        }
        else
        {
            for (int k = 0; k < prevF.Length; k++)
                prevF[k] = prevF[k] + (targetF[k] - prevF[k]) * factor;
        }
    }

    /// <summary>
    /// Заливка MultiMesh-буфера из уже интерполированных позиций: 8 float на инстанс
    /// (1,0,0,px, 0,1,0,py — единичная матрица + трансляция).
    /// РЕАЛИЗАЦИЯ — СКАЛЯРНЫЙ цикл, и это честно: stride-8 scatter memory-bound
    /// (потолок — bandwidth записи ~320KB/кадр на 10k, а не ALU), векторизация
    /// через Vector&lt;float&gt; гейна не даёт. Метод существует чтобы убрать
    /// дублирование заливки и дать точку для тестов без движка.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">count &lt; 0.</exception>
    /// <exception cref="ArgumentException">count больше длин спанов; buffer короче count*8.</exception>
    public static void FillRenderBuffer(Span<float> buffer, ReadOnlySpan<Vector2> positions, int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "count не может быть отрицательным.");
        if (count > positions.Length)
            throw new ArgumentException("count больше длины positions.");
        if ((long)buffer.Length < (long)count * 8)
            throw new ArgumentException("buffer короче count*8.");
        if (count == 0)
            return;

        for (int i = 0; i < count; i++)
        {
            float px = positions[i].X;
            float py = positions[i].Y;
            int idx = i * 8;
            buffer[idx + 0] = 1.0f;
            buffer[idx + 1] = 0.0f;
            buffer[idx + 2] = 0.0f;
            buffer[idx + 3] = px;
            buffer[idx + 4] = 0.0f;
            buffer[idx + 5] = 1.0f;
            buffer[idx + 6] = 0.0f;
            buffer[idx + 7] = py;
        }
    }

    /// <summary>
    /// Слитый проход главного цикла <c>AgentRenderer._Process</c>: lerp
    /// <c>px = prev + (target - prev) * factor</c> на лету + заливка 8 float
    /// на инстанс в буфер. Без промежуточного массива позиций (как и раньше —
    /// px/py нигде не хранятся). prev/target НЕ мутируются (в отличие от
    /// <see cref="Interpolate"/>). РЕАЛИЗАЦИЯ — СКАЛЯРНАЯ (см. remarks класса:
    /// stride-8 scatter memory-bound); единство цикла — для тестируемости,
    /// а не для ALU-выигрыша.
    /// Теневая заливка сюда НЕ входит (шаг G4, отдельный проход в рендерере).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">count &lt; 0.</exception>
    /// <exception cref="ArgumentException">
    /// count больше длин спанов позиций; buffer короче count*8; factor — NaN или Infinity.
    /// </exception>
    public static void LerpFill(
        ReadOnlySpan<Vector2> prev,
        ReadOnlySpan<Vector2> target,
        float factor,
        Span<float> buffer,
        int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "count не может быть отрицательным.");
        if (count > prev.Length || count > target.Length)
            throw new ArgumentException("count больше длин спанов позиций.");
        if ((long)buffer.Length < (long)count * 8)
            throw new ArgumentException("buffer короче count*8.");
        ValidateFactor(factor);
        if (count == 0)
            return;

        for (int i = 0; i < count; i++)
        {
            float px = prev[i].X + (target[i].X - prev[i].X) * factor;
            float py = prev[i].Y + (target[i].Y - prev[i].Y) * factor;
            int idx = i * 8;
            buffer[idx + 0] = 1.0f;
            buffer[idx + 1] = 0.0f;
            buffer[idx + 2] = 0.0f;
            buffer[idx + 3] = px;
            buffer[idx + 4] = 0.0f;
            buffer[idx + 5] = 1.0f;
            buffer[idx + 6] = 0.0f;
            buffer[idx + 7] = py;
        }
    }

    /// <summary>Проверка factor: NaN/Infinity запрещены. Диапазон не ограничиваем
    /// (экстраполяция — валидная математика); рендерер и так клампит 0..1.</summary>
    /// <exception cref="ArgumentException">NaN или Infinity.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateFactor(float factor)
    {
        if (float.IsNaN(factor) || float.IsInfinity(factor))
            throw new ArgumentException("factor обязан быть конечным числом.", nameof(factor));
    }
}
