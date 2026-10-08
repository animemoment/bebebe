using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Game.Core;

/// <summary>
/// Профайлер: (1) замеры scope'ов (Scope/ScopeCustom) и (2) баланс параллельных
/// фаз планировщика.
///
/// ГЛАВНОЕ ПРО ОКНА (исправление «врущего» оверлея): снапшот берётся НЕ каждый
/// кадр, а по таймеру оверлея (~0.1 с). Поэтому «сырая» накопленная сумма — это
/// миллисекунды ЗА ОКНО снапшота, а не за вызов и не за кадр. Раньше наружу
/// отдавалось одно поле AvgMs, которое трактовалось то как «на вызов», то как
/// «за кадр» — отсюда завышение в ~6 раз (6 кадров в окне 0.1 с при 60 FPS).
/// Теперь наружу отдаются только явные величины:
///   TotalMs      — мс за окно снапшота;
///   MsPerFrame   — вклад в ОДИН кадр (TotalMs / кадров в окне);
///   AvgMs        — среднее на ОДИН вызов (TotalMs / Calls);
///   MaxMs        — пик одиночного вызова;
///   Calls        — вызовов за окно;
///   CallsPerSec  — истинная частота (Calls / windowSec);
///   PercentLoad  — доля от всего замеренного времени окна.
/// Вызывающий ОБЯЗАН передать реальную длину окна (windowSec) и число кадров
/// в нём (frames) — иначе нормировка снова начнёт врать.
/// </summary>
public static class GameProfiler
{
    public struct MetricSnapshot
    {
        public string Name;
        /// <summary>Суммарно мс за окно снапшота.</summary>
        public double TotalMs;
        /// <summary>Вклад в один кадр: TotalMs / число кадров в окне.</summary>
        public double MsPerFrame;
        /// <summary>Среднее на один вызов.</summary>
        public double AvgMs;
        /// <summary>Пик одиночного вызова.</summary>
        public double MaxMs;
        /// <summary>Вызовов за окно.</summary>
        public int Calls;
        /// <summary>Вызовов в секунду (истинное значение).</summary>
        public int CallsPerSec;
        /// <summary>Доля от всего замеренного времени окна, %.</summary>
        public double PercentLoad;
    }

    private sealed class MetricEntry
    {
        public long TotalTicks;
        public int CallCount;
        public long MaxTicks;
        /// <summary>Сглаженные мс за окно (EMA).</summary>
        public double SmoothedTotalMs;
        /// <summary>Сглаженный пик одиночного вызова (мс).</summary>
        public double SmoothedMaxMs;
        /// <summary>Сглаженное число кадров в окне — для нормировки на кадр.</summary>
        public double SmoothedFrames;
    }

    private static readonly ConcurrentDictionary<string, MetricEntry> _metrics = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string File, string Member), string> _nameCache = new();

    /// <summary>
    /// Баланс параллельных фаз (пишет симуляция). WallMs/CpuMs — это НАКОПЛЕННЫЕ
    /// суммы с последнего снапшота баланса; наружу отдаются дельты за окно и
    /// нормировка на кадр. ImbalancePct = простой ядер (0 = идеал).
    /// MaxBatchMs = худший батч окна, StragglerCount = страгглеров за окно.
    /// Хранится отдельно от _metrics, чтобы не искажать PercentLoad.
    /// </summary>
    public struct BalanceSnapshot
    {
        public string Name;
        /// <summary>Wall-time фазы за окно (сумма по суб-степам), мс.</summary>
        public double WallMs;
        /// <summary>CPU-время фазы за окно (сумма по потокам), мс.</summary>
        public double CpuMs;
        /// <summary>Wall на один кадр, мс.</summary>
        public double WallMsPerFrame;
        /// <summary>CPU на один кадр, мс.</summary>
        public double CpuMsPerFrame;
        public int ImbalancePct;
        /// <summary>Самый медленный батч окна, мс.</summary>
        public double MaxBatchMs;
        /// <summary>Число батчей дольше HeavyMs за окно.</summary>
        public int StragglerCount;
    }

    private sealed class BalanceEntry
    {
        // Накопленные (кумулятивные) значения — только растут.
        public double WallMs;
        public double CpuMs;
        // Курсор последнего снапшота: наружу отдаётся разница.
        public double PrevWallMs;
        public double PrevCpuMs;
        // Оконные накопители (сбрасываются на каждом снапшоте баланса).
        public double WindowMaxBatchMs;
        public int WindowStragglers;
    }

    private static readonly ConcurrentDictionary<string, BalanceEntry> _balance = new(StringComparer.Ordinal);
    private static BalanceSnapshot[] _balanceCache = Array.Empty<BalanceSnapshot>();

    /// <summary>
    /// Аккумуляция одного суб-степа фазы в кумулятивные суммы scope'а.
    /// Вызывается планировщиком (Publish) из sim-потока после join воркеров.
    /// Битые значения санитизируются.
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
        if (double.IsFinite(maxBatchMs) && maxBatchMs > entry.WindowMaxBatchMs)
            entry.WindowMaxBatchMs = maxBatchMs;
        if (stragglerCount > 0)
            Interlocked.Add(ref entry.WindowStragglers, stragglerCount);
    }

    /// <summary>
    /// Пересчитывает дельты баланса за прошедшее окно (окно = windowSec секунд
    /// и frames кадров). Зовётся из SnapshotMetrics, поэтому окно баланса всегда
    /// совпадает с окном метрик и не «копится», пока панель скрыта (F3).
    /// </summary>
    private static void RefreshBalance(double windowSec, int frames)
    {
        int effDop = Game.Simulation.Scheduling.DynamicWorkScheduler.ComputeEffectiveDop(Environment.ProcessorCount);
        double perFrameDivisor = frames > 0
            ? frames
            : Math.Max(1.0, windowSec * 60.0); // панель не считала кадры — оценка по 60 FPS

        var list = new BalanceSnapshot[_balance.Count];
        int idx = 0;
        foreach (var (name, entry) in _balance)
        {
            double curWall = entry.WallMs;
            double curCpu = entry.CpuMs;
            double dWall = curWall - entry.PrevWallMs;
            double dCpu = curCpu - entry.PrevCpuMs;
            // Защита от внешней перезаписи/сброса счётчиков: ушло в минус — берём как есть.
            if (dWall < 0.0) dWall = curWall;
            if (dCpu < 0.0) dCpu = curCpu;
            entry.PrevWallMs = curWall;
            entry.PrevCpuMs = curCpu;

            double maxBatch = Interlocked.Exchange(ref entry.WindowMaxBatchMs, 0.0);
            int stragglers = Interlocked.Exchange(ref entry.WindowStragglers, 0);

            // Фаза в этом окне не публиковалась — не мусорим строкой.
            if (dWall <= 0.0 && dCpu <= 0.0)
                continue;

            int imb = 0;
            if (dWall > 0.001 && dCpu > 0.001)
            {
                double ideal = dCpu / Math.Max(1, effDop);
                imb = (int)Math.Round(Math.Max(0.0, (dWall - ideal) / dWall) * 100.0);
                imb = Math.Clamp(imb, 0, 100);
            }

            list[idx++] = new BalanceSnapshot
            {
                Name = name,
                WallMs = dWall,
                CpuMs = dCpu,
                WallMsPerFrame = dWall / perFrameDivisor,
                CpuMsPerFrame = dCpu / perFrameDivisor,
                ImbalancePct = imb,
                MaxBatchMs = maxBatch,
                StragglerCount = stragglers,
            };
        }

        if (idx != list.Length)
            Array.Resize(ref list, idx);
        Array.Sort(list, static (a, b) => b.WallMsPerFrame.CompareTo(a.WallMsPerFrame));
        _balanceCache = list;
    }

    /// <summary>
    /// Баланс фаз по последнему окну (результат последнего SnapshotMetrics).
    /// Читает кэш и НЕ двигает курсоры дельт — можно звать сколько угодно раз
    /// (например, только когда панель видима), значения от этого не поедут.
    /// </summary>
    public static void SnapshotBalance(out BalanceSnapshot[] results)
    {
        results = _balanceCache;
    }

    public readonly ref struct ProfileScope
    {
        private readonly string _name;
        private readonly long _startTimestamp;
        // A1+A5+A7: иерархия + alloc на скоуп. Путь/alloc пишутся в SimEvents
        // только для значимых (там фильтр), сюда — как было (без overhead).
        private readonly long _alloc0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ProfileScope(string name)
        {
            _name = name;
            SimEvents.PushScope(name);
            _startTimestamp = Stopwatch.GetTimestamp();
            _alloc0 = GC.GetAllocatedBytesForCurrentThread();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - _startTimestamp;
            long alloc = GC.GetAllocatedBytesForCurrentThread() - _alloc0;
            SimEvents.PopScope();
            // A7: alloc>64КБ на один скоуп — сразу в канал (видно жирные места).
            if (alloc > 65536)
                SimEvents.Record("scope", SimEvents.CurrentPath + ">" + _name, 0, alloc, 0, "big_alloc");
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

    /// <summary>Именованный замер для произвольных блоков: using (GameProfiler.ScopeCustom("MyCategory: Task")) { ... }</summary>
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
    /// Сбор снапшота метрик за прошедшее окно.
    /// </summary>
    /// <param name="results">Метрики, отсортированные по вкладу в кадр (убывание).</param>
    /// <param name="windowSec">Реальная длина окна в секундах (сколько прошло с прошлого снапшота).</param>
    /// <param name="frames">Сколько кадров отрисовано за это окно (для нормировки на кадр).</param>
    public static void SnapshotMetrics(out MetricSnapshot[] results, double windowSec, int frames)
    {
        if (!double.IsFinite(windowSec) || windowSec <= 0.0)
            windowSec = 0.016;
        if (frames < 1)
            frames = 1;

        // Баланс фаз нормируется по тому же окну — иначе его цифры «плыли»
        // относительно таблицы методов (это и путало: 0.67ms против 19.62ms).
        RefreshBalance(windowSec, frames);

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
            entry.SmoothedTotalMs = entry.SmoothedTotalMs * 0.82 + currentMs * 0.18;
            entry.SmoothedMaxMs = Math.Max(currentMaxMs, entry.SmoothedMaxMs * 0.75);
            entry.SmoothedFrames = entry.SmoothedFrames * 0.82 + frames * 0.18;

            double framesInWindow = entry.SmoothedFrames > 0.5 ? entry.SmoothedFrames : frames;
            int callsPerSec = (int)Math.Round(calls / windowSec);
            double avgPerCall = calls > 0 ? currentMs / calls : 0.0;

            totalRecordedMs += entry.SmoothedTotalMs;

            list[idx++] = new MetricSnapshot
            {
                Name = name,
                TotalMs = entry.SmoothedTotalMs,
                MsPerFrame = entry.SmoothedTotalMs / framesInWindow,
                AvgMs = avgPerCall,
                MaxMs = entry.SmoothedMaxMs,
                Calls = calls,
                CallsPerSec = callsPerSec,
                PercentLoad = 0.0
            };
        }

        // Вычисление % нагрузки относительно суммарно зафиксированного времени
        if (totalRecordedMs > 0.001)
        {
            for (int i = 0; i < list.Length; i++)
            {
                list[i].PercentLoad = (list[i].TotalMs / totalRecordedMs) * 100.0;
            }
        }

        // Сортировка: самые «дорогие» для кадра методы — в самом верху
        Array.Sort(list, static (a, b) => b.MsPerFrame.CompareTo(a.MsPerFrame));
        results = list;
    }
}
