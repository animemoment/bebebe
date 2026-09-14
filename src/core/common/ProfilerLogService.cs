using System;
using System.Collections.Generic;
using System.Text;

namespace Game.Core;

/// <summary>
/// Побочная «база» профилирования: накапливает 10Гц-снапшоты <see cref="GameProfiler"/>
/// в посекундные окна, хранит журнал нажатий и строит полный текстовый отчёт.
/// Чистый C# без Godot-зависимостей — не живёт в горячем пути симуляции.
/// </summary>
public sealed class ProfilerLogService
{
    public static ProfilerLogService Instance { get; } = new();

    /// <summary>Максимальный размер посекундного лога (~4 часа при 1 сек/окно).</summary>
    public const int MaxRecordedSeconds = 14_400;

    /// <summary>Максимальный размер журнала нажатий.</summary>
    public const int MaxInputs = 50_000;

    /// <summary>Посекундные агрегаты одного метода.</summary>
    public sealed class SecondEntry
    {
        public double TotalMs;      // сумма времени метода за 1 секунду
        public double MaxMs;        // максимум одиночного замера за секунду
        public int Calls;           // суммарное число вызовов за секунду
        public int SnapshotCount;   // сколько 10Гц-снапшотов легло в окно
    }

    private Dictionary<string, SecondEntry> _current = new(StringComparer.Ordinal);
    private readonly List<Dictionary<string, SecondEntry>> _seconds = new();
    private readonly Queue<string> _inputs = new();
    private double _windowAccum;
    private double _sessionSecs;
    private bool _paused;

    public bool IsPaused => _paused;
    public double SessionSeconds => _sessionSecs;
    public int SecondCount => _seconds.Count;
    public int InputCount => _inputs.Count;

    /// <summary>
    /// Принимает очередной 10Гц-снапшот и накапливает его в текущее секундное окно.
    /// Вызывается из <c>_Process</c> оверлея независимо от видимости панели.
    /// </summary>
    public void Accumulate(GameProfiler.MetricSnapshot[] metrics, float delta)
    {
        if (_paused || metrics == null || metrics.Length == 0)
            return;

        _windowAccum += delta;
        double deltaSec = Math.Max(0.016f, delta);

        foreach (var m in metrics)
        {
            if (m.AvgMs < 0.0005 && m.MaxMs < 0.0005 && m.CallsPerSec == 0)
                continue; // пропускаем «мёртвые» записи

            if (!_current.TryGetValue(m.Name, out var entry))
            {
                entry = new SecondEntry();
                _current[m.Name] = entry;
            }

            entry.TotalMs += m.AvgMs;
            if (m.MaxMs > entry.MaxMs) entry.MaxMs = m.MaxMs;
            entry.Calls += (int)(m.CallsPerSec * deltaSec);
            entry.SnapshotCount++;
        }

        if (_windowAccum >= 1.0f)
            FlushWindow();
    }

    private void FlushWindow()
    {
        _sessionSecs += _windowAccum;
        _windowAccum = 0f;
        _seconds.Add(_current);
        _current = new Dictionary<string, SecondEntry>(StringComparer.Ordinal);

        if (_seconds.Count > MaxRecordedSeconds)
            _seconds.RemoveAt(0);
    }

    /// <summary>Записывает описание события ввода (клавиша / кнопка мыши).</summary>
    public void RecordInput(string description)
    {
        if (_paused || string.IsNullOrEmpty(description))
            return;

        _inputs.Enqueue(description);
        while (_inputs.Count > MaxInputs)
            _inputs.Dequeue();
    }

    public void Pause()
    {
        _paused = true;
        _windowAccum = 0f;
        _current.Clear();
    }

    public void Resume()
    {
        _paused = false;
        _windowAccum = 0f;
        _current.Clear();
    }

    public void TogglePause()
    {
        if (_paused) Resume();
        else Pause();
    }

    /// <summary>Полный сброс: очищает лог, журнал и счётчик сессии.</summary>
    public void Reset()
    {
        _seconds.Clear();
        _inputs.Clear();
        _current.Clear();
        _windowAccum = 0f;
        _sessionSecs = 0f;
    }

    /// <summary>Записывает отчёт в файл (абсолютный путь, папки создаются).</summary>
    public void SaveToFile(string absolutePath)
    {
        var dir = System.IO.Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(absolutePath, BuildReport());
    }

    /// <summary>
    /// Строит полный текстовый отчёт: сводка по скриптам, детальный посекундный
    /// лог всех методов и журнал нажатий. Готов для копирования в буфер обмена.
    /// </summary>
    public string BuildReport()
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("=== PROFILER LOG REPORT ===");
        sb.AppendLine($"Сформировано: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Сессия: {FormatDuration(_sessionSecs)} | окно лога: {_seconds.Count} сек | нажатий: {_inputs.Count}");
        sb.AppendLine();

        // ---- 1. Сводка по скриптам за всё окно лога ----
        var scripts = new Dictionary<string, (double TotalMs, int Methods, double MaxMs)>(StringComparer.Ordinal);
        double logTotalMs = 0.0;
        foreach (var window in _seconds)
        {
            foreach (var (name, e) in window)
            {
                var (script, _) = SplitName(name);
                if (!scripts.TryGetValue(script, out var s))
                    s = (0d, 0, 0d);
                s.TotalMs += e.TotalMs;
                s.Methods++;
                if (e.MaxMs > s.MaxMs) s.MaxMs = e.MaxMs;
                scripts[script] = s;
                logTotalMs += e.TotalMs;
            }
        }

        sb.AppendLine("--- СВОДКА ПО СКРИПТАМ (всё окно лога) ---");
        if (scripts.Count == 0)
        {
            sb.AppendLine("  (данных ещё нет)");
        }
        else
        {
            foreach (var (script, s) in scripts)
            {
                double share = logTotalMs > 0.0 ? s.TotalMs / logTotalMs * 100.0 : 0.0;
                sb.AppendLine($"{script,-34} методов: {s.Methods,3} | суммарно: {s.TotalMs / 1000.0,8:F2}s | пик: {s.MaxMs,6:F2}ms | доля: {share,5:F1}%");
            }
        }
        sb.AppendLine();

        // ---- 2. Посекундный лог всех методов (последние 300 сек) ----
        sb.AppendLine("--- ПОСЕКУНДНЫЙ ЛОГ МЕТОДОВ (последние 300 сек) ---");
        int start = Math.Max(0, _seconds.Count - 300);
        for (int i = start; i < _seconds.Count; i++)
        {
            double secTime = _sessionSecs - (_seconds.Count - i);
            sb.AppendLine($"[{FormatClock(secTime)}]");
            foreach (var (name, e) in _seconds[i])
            {
                double perFramePct = e.SnapshotCount > 0
                    ? e.TotalMs / e.SnapshotCount / 16.667 * 100.0 : 0.0;
                sb.AppendLine($"    {name,-40} Σ {e.TotalMs,7:F1}ms | пик {e.MaxMs,6:F2}ms | вызовов {e.Calls,6} | %кадра {perFramePct,5:F1}");
            }
        }
        sb.AppendLine();

        // ---- 3. Журнал нажатий (последние 200) ----
        sb.AppendLine("--- ЖУРНАЛ НАЖАТИЙ (последние 200) ---");
        int inputStart = Math.Max(0, _inputs.Count - 200);
        int k = 0;
        foreach (var input in _inputs)
        {
            if (k++ < inputStart) continue;
            sb.AppendLine($"  {input}");
        }
        return sb.ToString();
    }

    private static (string Script, string Method) SplitName(string fullName)
    {
        int dot = fullName.LastIndexOf('.');
        return dot > 0
            ? (fullName.Substring(0, dot), fullName.Substring(dot + 1))
            : (fullName, "");
    }

    private static string FormatDuration(double totalSeconds)
    {
        int total = (int)totalSeconds;
        return $"{total / 3600:D2}:{(total / 60) % 60:D2}:{total % 60:D2}";
    }

    private static string FormatClock(double seconds)
    {
        int total = Math.Max(0, (int)seconds);
        return $"{total / 3600:D2}:{(total / 60) % 60:D2}:{total % 60:D2}";
    }
}
