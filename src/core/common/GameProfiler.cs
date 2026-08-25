using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Game.Core;

public static class GameProfiler
{
    public struct MetricSnapshot
    {
        public string Name;
        public double AvgMs;
        public double MaxMs;
        public int CallsPerSec;
        public double PercentLoad;
    }

    private sealed class MetricEntry
    {
        public long TotalTicks;
        public int CallCount;
        public long MaxTicks;
        public double SmoothedMs;
        public double SmoothedMaxMs;
    }

    private static readonly ConcurrentDictionary<string, MetricEntry> _metrics = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string File, string Member), string> _nameCache = new();

    public readonly ref struct ProfileScope
    {
        private readonly string _name;
        private readonly long _startTimestamp;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ProfileScope(string name)
        {
            _name = name;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - _startTimestamp;
            RecordElapsedTicks(_name, elapsedTicks);
        }
    }

    /// <summary>
    /// Автоматический замер: using (GameProfiler.Scope()) { ... }
    /// Автоматически извлекает имя скрипта и метода без аллокаций памяти.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ProfileScope Scope([CallerMemberName] string member = "", [CallerFilePath] string file = "")
    {
        return new ProfileScope(GetCachedName(file, member));
    }

    /// <summary>
    /// Именованный замер для произвольных блоков: using (GameProfiler.ScopeCustom("MyCategory: Task")) { ... }
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ProfileScope ScopeCustom(string customName)
    {
        return new ProfileScope(customName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetCachedName(string filePath, string memberName)
    {
        return _nameCache.GetOrAdd((filePath, memberName), static key =>
        {
            string className = Path.GetFileNameWithoutExtension(key.File);
            if (string.IsNullOrEmpty(className)) className = "UnknownScript";
            return $"{className}.{key.Member}";
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordElapsedTicks(string name, long elapsedTicks)
    {
        var entry = _metrics.GetOrAdd(name, static _ => new MetricEntry());
        Interlocked.Add(ref entry.TotalTicks, elapsedTicks);
        Interlocked.Increment(ref entry.CallCount);

        long currentMax = Volatile.Read(ref entry.MaxTicks);
        while (elapsedTicks > currentMax)
        {
            long prev = Interlocked.CompareExchange(ref entry.MaxTicks, elapsedTicks, currentMax);
            if (prev == currentMax) break;
            currentMax = prev;
        }
    }

    /// <summary>
    /// Сбор снапшота метрик с сортировкой по нагрузке (AvgMs descending).
    /// </summary>
    public static void SnapshotMetrics(out MetricSnapshot[] results, float delta)
    {
        var list = new MetricSnapshot[_metrics.Count];
        int idx = 0;
        double totalRecordedMs = 0.0;

        foreach (var (name, entry) in _metrics)
        {
            long ticks = Interlocked.Exchange(ref entry.TotalTicks, 0);
            int calls = Interlocked.Exchange(ref entry.CallCount, 0);
            long maxTicks = Interlocked.Exchange(ref entry.MaxTicks, 0);

            double currentMs = (ticks / (double)Stopwatch.Frequency) * 1000.0;
            double currentMaxMs = (maxTicks / (double)Stopwatch.Frequency) * 1000.0;

            // Экспоненциальное сглаживание
            entry.SmoothedMs = entry.SmoothedMs * 0.82 + currentMs * 0.18;
            entry.SmoothedMaxMs = Math.Max(currentMaxMs, entry.SmoothedMaxMs * 0.75);

            int callsPerSec = (int)(calls / Math.Max(0.016f, delta));
            totalRecordedMs += entry.SmoothedMs;

            list[idx++] = new MetricSnapshot
            {
                Name = name,
                AvgMs = entry.SmoothedMs,
                MaxMs = entry.SmoothedMaxMs,
                CallsPerSec = callsPerSec,
                PercentLoad = 0.0
            };
        }

        // Вычисление % нагрузки относительно суммарно зафиксированного времени
        if (totalRecordedMs > 0.001)
        {
            for (int i = 0; i < list.Length; i++)
            {
                list[i].PercentLoad = (list[i].AvgMs / totalRecordedMs) * 100.0;
            }
        }

        // Сортировка: самые тяжелые методы — в самом верху
        Array.Sort(list, static (a, b) => b.AvgMs.CompareTo(a.AvgMs));
        results = list;
    }
}