using System;
using System.Threading;

namespace Game.Simulation;

/// <summary>
/// Lock-free ThreadLocal RNG для параллельных фаз симуляции.
/// Random.Shared держит внутренний lock (contention на 16 ядрах),
/// ctx.Random вообще не thread-safe (data race в Parallel).
/// XorShift: ~2нс на NextDouble, состояние — 64 бита на поток, зерно —
/// смесь TickCount + ManagedThreadId (детерминизм Parallel и так условен).
/// </summary>
public static class ParallelRng
{
    [ThreadStatic]
    private static ulong _state;

    private static ulong Seed()
    {
        ulong s = (ulong)(Environment.TickCount ^ (Thread.CurrentThread.ManagedThreadId * 7919));
        if (s == 0) s = 0x9E3779B97F4A7C15UL;
        // Прогрев: первые значения сырого LCG коррелируют между потоками.
        s ^= s >> 29; s *= 0xBF58476D1CE4E5B9UL;
        s ^= s >> 32;
        return s;
    }

    private static ulong Next()
    {
        ulong s = _state;
        if (s == 0)
        {
            s = Seed();
            _state = s;
        }
        // xorshift64*: один mul + три xor — без lock.
        s ^= s >> 12;
        s ^= s << 25;
        s ^= s >> 27;
        _state = s;
        return s * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>[0, 1) без lock и без аллокаций.</summary>
    public static double NextDouble() => (Next() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>[minInclusive, maxExclusive) без lock. Равномерно через
    /// rejection sampling (#9: наивный Next()%range даёт modulo-смещение —
    /// для больших range старшие значения выпадают реже; здесь отсев
    /// переполняющего хвоста 2^64 mod range, распределение равномерное).</summary>
    public static int Next(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive) return minInclusive;
        uint range = (uint)(maxExclusive - minInclusive);
        // Граница отсева: значения r >= limit отбрасываем (иначе modulo-смещение).
        ulong limit = (0xFFFFFFFFFFFFFFFFUL / range) * range;
        while (true)
        {
            ulong r = Next();
            if (r < limit)
                return minInclusive + (int)(r % range);
        }
    }
}
