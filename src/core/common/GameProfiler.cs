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

    /// <summary>
    /// Баланс параллельных фаз (пишет симуляция, один вызов на фазу —
    /// contention нет). AvgMs = wall-time фазы, MaxMs = суммарное CPU-время всех
    /// потоков, CallsPerSec = дисбаланс в % (0 = идеал, &gt;0 = простой ядер).
    /// Хранится отдельно от _metrics, чтобы не искажать PercentLoad.
    /// MaxBatchMs = самый медленный батч фазы (хвост), StragglerCount = число
    /// батчей дольше HeavyMs (Шаг 2, без contention — пишет только sim-поток).
    /// </summary>
    public struct BalanceSnapshot
    {
        public string Name;
        public double WallMs;
        public double CpuMs;
        public int ImbalancePct;
        public double MaxBatchMs;
        public int StragglerCount;
    }

    private sealed class BalanceEntry
    {
        public double WallMs;
        public double CpuMs;
        public double MaxBatchMs;
        public int StragglerCount;
    }

    private static readonly ConcurrentDictionary<string, BalanceEntry> _balance = new(StringComparer.Ordinal);

    /// <summary>
    /// Публикация баланса фазы. Вызывать ОДИН раз на фазу (не на батч!).
    /// Lock-free: GetOrAdd + прямые записи (гонка записей одного ключа
    /// невозможна — фазы идут последовательно в sim-потоке).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordPhaseBalance(string name, double wallMs, double cpuMs)
    {
        var entry = _balance.GetOrAdd(name, static _ => new BalanceEntry());
        entry.WallMs = wallMs;
        entry.CpuMs = cpuMs;
    }

    /// <summary>
    /// Публикация баланса фазы с метриками хвоста (Шаг 2).
    /// Вызывать ОДИН раз на фазу из sim-потока. Битые wall/cpu (NaN/Inf/&lt;0)
    /// санитизируются в 0, ImbalancePct clamp'ится в [0,100].
    /// </summary>
    public static void RecordSchedulerStats(string name, double wallMs, double cpuMs, double maxBatchMs, int stragglerCount)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));
        var entry = _balance.GetOrAdd(name, static _ => new BalanceEntry());
        entry.WallMs = double.IsFinite(wallMs) && wallMs > 0.0 ? wallMs : 0.0;
        entry.CpuMs = double.IsFinite(cpuMs) && cpuMs > 0.0 ? cpuMs : 0.0;
        entry.MaxBatchMs = double.IsFinite(maxBatchMs) && maxBatchMs >= 0.0 ? maxBatchMs : 0.0;
        entry.StragglerCount = Math.Max(0, stragglerCount);
    }

    // FIX круг-2 №6: Publish планировщика раньше ПЕРЕЗАПИСЫВАЛ баланс 8 раз за тик
    // (по числу sub-steps) — overlay видел последний sub-step, а не сумму.
    // Теперь: сброс в начале тика (ResetTickBalance) + аккумуляция каждого sub-step
    // (AccumulateSchedulerStats: sum wall/cpu, max MaxBatchMs, sum StragglerCount).
    /// <summary>
    /// Сброс аккумулятора баланса в начале тика (перед циклом sub-steps).
    /// Вызывать один раз на тик из sim-потока для каждого публикуемого scope.
    /// </summary>
    public static void ResetTickBalance(string name)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));
        var entry = _balance.GetOrAdd(name, static _ => new BalanceEntry());
        entry.WallMs = 0.0;
        entry.CpuMs = 0.0;
        entry.MaxBatchMs = 0.0;
        entry.StragglerCount = 0;
    }

    /// <summary>
    /// Аккумуляция баланса sub-step в тиковый тотал (зовёт планировщик из Publish):
    /// wall/cpu суммируются, MaxBatchMs берётся максимумом, StragglerCount суммируется.
    /// Overlay видит сумму тика, а не последний sub-step. Битые значения санитизируются.
    /// Вызывать из sim-потока после join (гонки записей одного ключа нет —
    /// фазы идут последовательно).
    /// </summary>
    public static void AccumulateSchedulerStats(string name, double wallMs, double cpuMs, double maxBatchMs, int stragglerCount)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));
        var entry = _balance.GetOrAdd(name, static _ => new BalanceEntry());
        if (double.IsFinite(wallMs) && wallMs > 0.0)
            entry.WallMs += wallMs;
        if (double.IsFinite(cpuMs) && cpuMs > 0.0)
            entry.CpuMs += cpuMs;
        if (double.IsFinite(maxBatchMs) && maxBatchMs > entry.MaxBatchMs)
            entry.MaxBatchMs = maxBatchMs;
        if (stragglerCount > 0)
            entry.StragglerCount += stragglerCount;
    }

    public static void SnapshotBalance(out BalanceSnapshot[] results)
    {
        var list = new BalanceSnapshot[_balance.Count];
        int idx = 0;
        foreach (var (name, entry) in _balance)
        {
            double wall = entry.WallMs;
            double cpu = entry.CpuMs;
            int imb = 0;
            if (wall > 0.001 && cpu > 0.001)
            {
                double ideal = cpu / Math.Max(1, Environment.ProcessorCount);
                imb = (int)Math.Round(Math.Max(0.0, (wall - ideal) / wall) * 100.0);
                imb = Math.Clamp(imb, 0, 100);
            }
            list[idx++] = new BalanceSnapshot
            {
                Name = name,
                WallMs = wall,
                CpuMs = cpu,
                ImbalancePct = imb,
                MaxBatchMs = entry.MaxBatchMs,
                StragglerCount = entry.StragglerCount,
            };
        }
        Array.Sort(list, static (a, b) => b.WallMs.CompareTo(a.WallMs));
        results = list;
    }

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
