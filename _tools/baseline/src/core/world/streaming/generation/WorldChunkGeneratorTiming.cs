using System;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Статистика таймингов генерации чанков. Чистый C#, без зависимости от Godot API.
/// Thread-safe кольцевой буфер фиксированного размера: запись не аллоцирует память,
/// поэтому замеры не искажают GC-профиль генератора.
/// </summary>
public static class WorldChunkGeneratorTiming
{
    /// <summary>Размер кольцевого буфера (последние N замеров).</summary>
    public const int Capacity = 4096;

    private static readonly object Gate = new();
    private static readonly double[] Samples = new double[Capacity];
    private static readonly ChunkKey[] Keys = new ChunkKey[Capacity];
    private static int _next;
    private static int _count;

    /// <summary>Сколько замеров сейчас хранится (не больше <see cref="Capacity"/>).</summary>
    public static int Count
    {
        get { lock (Gate) return _count; }
    }

    /// <summary>Зафиксировать время генерации одного чанка.</summary>
    internal static void Record(ChunkKey key, double elapsedMs)
    {
        lock (Gate)
        {
            Samples[_next] = elapsedMs;
            Keys[_next] = key;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity)
                _count++;
        }
    }

    /// <summary>Последний зафиксированный тайминг. false — замеров ещё не было.</summary>
    public static bool TryGetLast(out ChunkTimings timing)
    {
        lock (Gate)
        {
            if (_count == 0)
            {
                timing = default;
                return false;
            }

            int index = (_next - 1 + Capacity) % Capacity;
            timing = new ChunkTimings(Keys[index], Samples[index], WorldChunk.CellCount);
            return true;
        }
    }

    /// <summary>
    /// Статистика по последним <paramref name="count"/> замерам (или по всем, если их меньше).
    /// </summary>
    public static TimingStatistics Snapshot(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "count должен быть положительным.");

        var buffer = new double[Capacity];
        int n;
        lock (Gate)
        {
            n = Math.Min(count, _count);
            if (n > 0)
            {
                int start = (_next - n + Capacity) % Capacity;
                for (int i = 0; i < n; i++)
                    buffer[i] = Samples[(start + i) % Capacity];
            }
        }

        if (n == 0)
            return TimingStatistics.Zero;

        Array.Sort(buffer, 0, n);

        double sum = 0d;
        for (int i = 0; i < n; i++)
            sum += buffer[i];

        return new TimingStatistics(
            MeanMs: (float)(sum / n),
            P50Ms: Percentile(buffer, n, 0.50f),
            P95Ms: Percentile(buffer, n, 0.95f),
            P99Ms: Percentile(buffer, n, 0.99f),
            MaxMs: (float)buffer[n - 1],
            Samples: n);
    }

    /// <summary>Очистить буфер (для бенчмарков/тестов).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Array.Clear(Samples);
            Array.Clear(Keys);
            _next = 0;
            _count = 0;
        }
    }

    private static float Percentile(double[] sorted, int length, float quantile)
    {
        int index = (int)MathF.Round((length - 1) * quantile, MidpointRounding.AwayFromZero);
        if (index < 0)
            index = 0;
        else if (index >= length)
            index = length - 1;
        return (float)sorted[index];
    }

    /// <summary>Один замер: ключ чанка, время генерации и число клеток.</summary>
    public readonly record struct ChunkTimings(ChunkKey Key, double ElapsedMs, int CellCount);

    /// <summary>Агрегированная статистика замеров.</summary>
    public readonly record struct TimingStatistics(
        float MeanMs, float P50Ms, float P95Ms, float P99Ms, float MaxMs, int Samples)
    {
        public static readonly TimingStatistics Zero = new(0f, 0f, 0f, 0f, 0f, 0);
    }
}
