using System;

namespace Game.Core.WorldClimate;

/// <summary>
/// Q0.16 арифметика (0..65535 == 0.0..1.0). Чистые целочисленные операции —
/// результат байт-в-байт одинаков на любой платформе и при любом порядке вызовов.
/// </summary>
public static class Q16
{
    public const int One = 65535;
    public const int Half = 32768;

    public static ushort FromFloat(float value)
        => (ushort)Math.Clamp((int)(value * One + 0.5f), 0, One);

    public static float ToFloat(ushort value) => value / (float)One;

    public static ushort ClampAdd(ushort value, int delta)
        => (ushort)Math.Clamp((int)value + delta, 0, One);

    /// <summary>Произведение двух Q16 (a*b), без переполнения.</summary>
    public static ushort Mul(ushort a, ushort b)
        => (ushort)(((long)a * b + Half) / One);

    /// <summary>Линейная интерполяция a..b с весом t (Q16).</summary>
    public static ushort Lerp(ushort a, ushort b, uint t)
    {
        long delta = (long)b - a;
        long value = a + (delta * t + (delta >= 0 ? Half : -Half)) / One;
        return (ushort)Math.Clamp(value, 0L, One);
    }

    /// <summary>Взвешенное среднее двух значений (веса — произвольные целые).</summary>
    public static ushort Weighted(ushort a, int weightA, ushort b, int weightB)
    {
        long sum = (long)a * weightA + (long)b * weightB;
        return (ushort)((sum + (weightA + weightB) / 2) / (weightA + weightB));
    }

    /// <summary>
    /// Смещение значения «bias»-ом по центру: bias == Half — без изменений,
    /// bias == One сдвигает на +strength, bias == 0 — на -strength.
    /// </summary>
    public static ushort ApplyBias(ushort value, ushort bias, int strength)
        => ClampAdd(value, (int)(((long)bias - Half) * strength / Half));

    public static ushort SmoothStep(ushort t)
    {
        ulong x = t;
        ulong x2 = (x * x + Half) / (ulong)One;
        ulong shaped = x2 * (3ul * One - 2ul * x) + (ulong)Half;
        return (ushort)Math.Min(One, shaped / (ulong)One);
    }

    /// <summary>Нормировка t из диапазона [lo,hi] в Q16 (loat/hi — Q16, lo может быть &gt; hi).</summary>
    public static ushort InverseLerp(int lo, int hi, int value)
    {
        if (lo == hi) return value >= hi ? (ushort)One : (ushort)0;
        int clamped = Math.Clamp(value, Math.Min(lo, hi), Math.Max(lo, hi));
        long t = ((long)clamped - lo) * One / (hi - lo);
        return (ushort)Math.Clamp(t, 0L, One);
    }
}