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

    // --- ФИКС ФРИЗА: лимиты отчёта (было: 300 сек × все методы + Substring на каждое
    // имя = сотни МБ строка + ClipboardSet с ней же = фриз 20-30 сек) ---
    /// <summary>Сколько секунд лога класть в отчёт по умолчанию (было 300).</summary>
    public const int DefaultReportSeconds = 60;
    /// <summary>Сколько топ-методов на секунду класть в отчёт (было: все).</summary>
    public const int DefaultReportTopMethods = 15;
    /// <summary>Сколько нажатий класть в отчёт (было 200).</summary>
    public const int DefaultReportInputs = 50;

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
    // Журнал ввода: теперь со штампом времени и видом (key/mouse/button/func),
    // чтобы в CSV было видно, на какую кнопку нажал и какую функцию включил.
    private readonly Queue<InputEntry> _inputs = new();
    /// <summary>Одна запись журнала ввода.</summary>
    public sealed class InputEntry
    {
        public double AtSec;     // секунда сессии
        public string Kind;      // key | mouse | button | func
        public string Text;      // описание (клавиша / кнопка / функция)
        public override string ToString() => $"[{FormatClockStatic(AtSec)}] [{Kind}] {Text}";
    }
    private double _windowAccum;
    private double _sessionSecs;
    private bool _paused;

    // --- Маркеры/заметки/пороги/корзина: новые фичи логера ---
    private readonly List<(double AtSec, string Text)> _markers = new();
    private readonly List<(double AtSec, string Text)> _notes = new();
    private readonly List<(double AtSec, string Method, double PeakMs)> _spikes = new();
    private readonly Dictionary<string, (double TotalMs, int Methods, double MaxMs)> _scriptTotals = new(StringComparer.Ordinal);
    private double _scriptTotalsSecs;
    /// <summary>Порог пика (мс) для автофиксации спайков. 0 = выкл.</summary>
    public double SpikeThresholdMs { get; set; } = 8.0;
    /// <summary>Сколько секунд держать в окне (кольцо). По умолчанию всё.</summary>
    public int KeepSeconds { get; set; } = MaxRecordedSeconds;
    /// <summary>Глубина отчёта: секунды (0 = DefaultReportSeconds).</summary>
    public int ReportSeconds { get; set; }
    /// <summary>Глубина отчёта: топ-методов на секунду (0 = DefaultReportTopMethods).</summary>
    public int ReportTopMethods { get; set; }
    /// <summary>Виды событий для экспорта CSV (null/пусто = все).</summary>
    public string CsvEventFilter { get; set; }
    public int MarkerCount => _markers.Count;
    public int NoteCount => _notes.Count;
    public int SpikeCount => _spikes.Count;

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
        // Инкрементальная сводка по скриптам: O(методов) раз в секунду вместо
        // O(секунды × методы) при каждом BuildReport — это и был фриз.
        foreach (var (name, e) in _current)
        {
            var (script, _) = SplitName(name);
            if (!_scriptTotals.TryGetValue(script, out var s))
                s = (0d, 0, 0d);
            s.TotalMs += e.TotalMs;
            s.Methods++;
            if (e.MaxMs > s.MaxMs) s.MaxMs = e.MaxMs;
            _scriptTotals[script] = s;
        }
        _scriptTotalsSecs += 1.0;
        // Автофиксация спайков: метод с пиком выше порога — в журнал спайков.
        if (SpikeThresholdMs > 0.0)
        {
            foreach (var (name, e) in _current)
            {
                if (e.MaxMs >= SpikeThresholdMs)
                    _spikes.Add((_sessionSecs, name, e.MaxMs));
            }
            while (_spikes.Count > 2000)
                _spikes.RemoveAt(0);
        }
        _current = new Dictionary<string, SecondEntry>(StringComparer.Ordinal);

        while (_seconds.Count > KeepSeconds)
        {
            _seconds.RemoveAt(0);
            _scriptTotalsSecs = Math.Max(0.0, _scriptTotalsSecs - 1.0);
        }
        if (_seconds.Count > MaxRecordedSeconds)
            _seconds.RemoveAt(0);
    }

    /// <summary>Записывает описание события ввода (клавиша / кнопка мыши).</summary>
    public void RecordInput(string description)
    {
        RecordInput("key", description);
    }

    /// <summary>
    /// Записывает событие ввода с видом: key | mouse | button | func.
    /// Кнопки UI зовут RecordButton(имя), включение функций — RecordFunction(имя, вкл/выкл).
    /// </summary>
    public void RecordInput(string kind, string description)
    {
        if (_paused || string.IsNullOrEmpty(description))
            return;

        _inputs.Enqueue(new InputEntry { AtSec = _sessionSecs + _windowAccum, Kind = kind ?? "key", Text = description });
        while (_inputs.Count > MaxInputs)
            _inputs.Dequeue();
        _reportCache = null;
    }

    /// <summary>Нажата кнопка UI: имя кнопки + на какой вкладке/панели.</summary>
    public void RecordButton(string buttonName, string panel = null)
    {
        string text = string.IsNullOrEmpty(panel) ? buttonName : $"{panel}: {buttonName}";
        RecordInput("button", text);
    }

    /// <summary>Включена/выключена функция: имя + новое состояние.</summary>
    public void RecordFunction(string functionName, bool enabled)
    {
        RecordInput("func", $"{functionName} → {(enabled ? "ВКЛ" : "ВЫКЛ")}");
    }

    /// <summary>Включена/выключена функция с деталями (порог, значение).</summary>
    public void RecordFunction(string functionName, string detail)
    {
        RecordInput("func", string.IsNullOrEmpty(detail) ? functionName : $"{functionName} → {detail}");
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
        _markers.Clear();
        _notes.Clear();
        _spikes.Clear();
        _scriptTotals.Clear();
        _scriptTotalsSecs = 0.0;
        _current.Clear();
        _windowAccum = 0f;
        _sessionSecs = 0f;
        _reportCache = null;
        _reportCacheKey = 0;
    }

    // --- Новые фичи логера (маркеры, заметки, срезы, CSV, дифф, кэш отчёта) ---

    /// <summary>Поставить маркер на текущей секунде сессии.</summary>
    public void AddMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _markers.Add((_sessionSecs, text.Trim()));
        _reportCache = null;
    }

    /// <summary>Добавить заметку на текущей секунде сессии.</summary>
    public void AddNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _notes.Add((_sessionSecs, text.Trim()));
        _reportCache = null;
    }

    public IReadOnlyList<(double AtSec, string Text)> GetMarkers() => _markers;
    public IReadOnlyList<(double AtSec, string Text)> GetNotes() => _notes;
    public IReadOnlyList<(double AtSec, string Method, double PeakMs)> GetSpikes() => _spikes;

    public void ClearMarkers() { _markers.Clear(); _reportCache = null; }
    public void ClearNotes() { _notes.Clear(); _reportCache = null; }
    public void ClearSpikes() { _spikes.Clear(); }

    /// <summary>
    /// Топ-методов за последние <paramref name="lastSeconds"/> секунд окна.
    /// Возвращает (имя, суммарно мс, пик мс). Без аллокаций словаря наружу.
    /// </summary>
    public List<(string Name, double TotalMs, double MaxMs)> GetTopMethods(int lastSeconds, int topN)
    {
        var agg = new Dictionary<string, (double TotalMs, double MaxMs)>(StringComparer.Ordinal);
        int start = Math.Max(0, _seconds.Count - Math.Max(1, lastSeconds));
        for (int i = start; i < _seconds.Count; i++)
        {
            foreach (var (name, e) in _seconds[i])
            {
                if (!agg.TryGetValue(name, out var a)) a = (0d, 0d);
                a.TotalMs += e.TotalMs;
                if (e.MaxMs > a.MaxMs) a.MaxMs = e.MaxMs;
                agg[name] = a;
            }
        }
        var list = new List<(string Name, double TotalMs, double MaxMs)>(agg.Count);
        foreach (var (name, a) in agg) list.Add((name, a.TotalMs, a.MaxMs));
        list.Sort(static (a, b) => b.TotalMs.CompareTo(a.TotalMs));
        if (list.Count > topN) list.RemoveRange(topN, list.Count - topN);
        return list;
    }

    /// <summary>
    /// Ряд суммарной нагрузки (мс) по секундам окна для мини-графика.
    /// </summary>
    public float[] GetLoadSeries(int maxPoints)
    {
        int n = Math.Min(maxPoints, _seconds.Count);
        var out_ = new float[n];
        int start = _seconds.Count - n;
        for (int i = 0; i < n; i++)
        {
            double sum = 0.0;
            foreach (var (_, e) in _seconds[start + i]) sum += e.TotalMs;
            out_[i] = (float)sum;
        }
        return out_;
    }

    /// <summary>
    /// Ряд одного метода (сумма мс) по секундам окна. Пустой метод = суммарный ряд.
    /// </summary>
    public float[] GetMethodSeries(string methodName, int maxPoints)
    {
        int n = Math.Min(maxPoints, _seconds.Count);
        var out_ = new float[n];
        int start = _seconds.Count - n;
        for (int i = 0; i < n; i++)
        {
            double sum = 0.0;
            foreach (var (name, e) in _seconds[start + i])
            {
                if (string.IsNullOrEmpty(methodName) || name.EndsWith(methodName, StringComparison.Ordinal))
                    sum += e.TotalMs;
            }
            out_[i] = (float)sum;
        }
        return out_;
    }

    /// <summary>
    /// Дифф двух секунд окна: что выросло/упало по суммарным мс. Топ по модулю дельты.
    /// </summary>
    public List<(string Name, double DeltaMs)> DiffSeconds(int secA, int secB, int topN)
    {
        var result = new List<(string Name, double DeltaMs)>();
        if (secA < 0 || secB < 0 || secA >= _seconds.Count || secB >= _seconds.Count)
            return result;
        var agg = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, e) in _seconds[secA]) agg[name] = -e.TotalMs;
        foreach (var (name, e) in _seconds[secB])
        {
            if (!agg.TryGetValue(name, out var v)) v = 0.0;
            agg[name] = v + e.TotalMs;
        }
        foreach (var (name, d) in agg) result.Add((name, d));
        result.Sort(static (a, b) => Math.Abs(b.DeltaMs).CompareTo(Math.Abs(a.DeltaMs)));
        if (result.Count > topN) result.RemoveRange(topN, result.Count - topN);
        return result;
    }

    /// <summary>CSV-дамп: две секции — метрики окна + журнал ввода с кнопками/функциями.</summary>
    public string BuildCsv()
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("sec;method;total_ms;peak_ms;calls");
        string filter = CsvEventFilter;
        for (int i = 0; i < _seconds.Count; i++)
        {
            foreach (var (name, e) in _seconds[i])
            {
                if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.Append(i).Append(';').Append(name).Append(';')
                    .Append(e.TotalMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(';')
                    .Append(e.MaxMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(';')
                    .Append(e.Calls).AppendLine();
            }
        }
        // Секция 2: на какую кнопку нажал / какую функцию включил (со штампом времени).
        sb.AppendLine();
        sb.AppendLine("at_sec;kind;text");
        foreach (var in_ in _inputs)
        {
            if (!string.IsNullOrEmpty(filter) &&
                !in_.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !in_.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append(in_.AtSec.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(';')
                .Append(in_.Kind).Append(';').Append(in_.Text).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Короткая сводка для статуса копирования (без тяжёлого отчёта).</summary>
    public string BuildSummaryLine()
    {
        double logTotal = 0.0;
        string topScript = "—";
        double topMs = 0.0;
        foreach (var (script, s) in _scriptTotals)
        {
            logTotal += s.TotalMs;
            if (s.TotalMs > topMs) { topMs = s.TotalMs; topScript = script; }
        }
        return $"сессия {FormatDuration(_sessionSecs)} | окно {_seconds.Count}с | топ-скрипт {topScript} ({topMs / 1000.0:F1}s) | маркеров {_markers.Count} | спайков {_spikes.Count} | заметок {_notes.Count}";
    }

    private string _reportCache;
    private long _reportCacheKey;

    /// <summary>Записывает отчёт в файл (абсолютный путь, папки создаются).</summary>
    public void SaveToFile(string absolutePath)
    {
        var dir = System.IO.Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(absolutePath, BuildReport());
    }

    /// <summary>
    /// Строит компактный отчёт: сводка по скриптам (инкрементальная, O(скриптов)),
    /// посекундный срез (по умолчанию последние 60 сек × топ-15 методов),
    /// маркеры/спайки/заметки/нажатия. Кэшируется: повторный Copy без новых
    /// секунд отдаёт готовую строку мгновенно.
    /// Полный несрезанный дамп — через BuildCsv().
    /// </summary>
    public string BuildReport()
    {
        int wantSecs = ReportSeconds > 0 ? ReportSeconds : DefaultReportSeconds;
        int wantTop = ReportTopMethods > 0 ? ReportTopMethods : DefaultReportTopMethods;
        long key = ((long)_seconds.Count << 32) ^ (long)_inputs.Count ^ ((long)wantSecs << 48) ^ ((long)_markers.Count << 40);
        if (_reportCache != null && key == _reportCacheKey)
            return _reportCache;

        var sb = new StringBuilder(8192);
        sb.AppendLine("=== PROFILER LOG REPORT ===");
        sb.AppendLine($"Сформировано: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Сессия: {FormatDuration(_sessionSecs)} | окно лога: {_seconds.Count} сек | нажатий: {_inputs.Count} | маркеров: {_markers.Count} | спайков: {_spikes.Count} | заметок: {_notes.Count}");
        sb.AppendLine($"Срез: последние {Math.Min(wantSecs, _seconds.Count)} сек × топ-{wantTop} (полный дамп — CSV)");
        sb.AppendLine();

        // ---- 1. Сводка по скриптам: инкрементальная, без прохода по секундам ----
        double logTotalMs = 0.0;
        foreach (var (_, s) in _scriptTotals) logTotalMs += s.TotalMs;

        sb.AppendLine("--- СВОДКА ПО СКРИПТАМ (всё окно лога) ---");
        if (_scriptTotals.Count == 0)
        {
            sb.AppendLine("  (данных ещё нет)");
        }
        else
        {
            foreach (var (script, s) in _scriptTotals)
            {
                double share = logTotalMs > 0.0 ? s.TotalMs / logTotalMs * 100.0 : 0.0;
                sb.Append(script).Append(' ', Math.Max(1, 34 - script.Length))
                    .Append("методов: ").Append(s.Methods.ToString().PadLeft(3))
                    .Append(" | суммарно: ").Append((s.TotalMs / 1000.0).ToString("F2").PadLeft(8)).Append('s')
                    .Append(" | пик: ").Append(s.MaxMs.ToString("F2").PadLeft(6)).Append("ms")
                    .Append(" | доля: ").Append(share.ToString("F1").PadLeft(5)).AppendLine("%");
            }
        }
        sb.AppendLine();

        // ---- 2. Посекундный срез: только топ-N методов на секунду ----
        sb.AppendLine($"--- ПОСЕКУНДНЫЙ СРЕЗ (последние {Math.Min(wantSecs, _seconds.Count)} сек × топ-{wantTop}) ---");
        int start = Math.Max(0, _seconds.Count - wantSecs);
        // Топ-N отбором без сортировки всего окна: держим отсортированный по
        // убыванию TotalMs буфер размера wantTop, вставка — пузырьком вверх.
        var topBuf = new List<(string Name, double TotalMs, double MaxMs, int Calls, int Snaps)>(Math.Max(1, wantTop));
        for (int i = start; i < _seconds.Count; i++)
        {
            double secTime = _sessionSecs - (_seconds.Count - i);
            sb.Append('[').Append(FormatClock(secTime)).AppendLine("]");
            var window = _seconds[i];
            topBuf.Clear();
            foreach (var (name, e) in window)
            {
                int pos;
                if (topBuf.Count < wantTop)
                {
                    topBuf.Add((name, e.TotalMs, e.MaxMs, e.Calls, e.SnapshotCount));
                    pos = topBuf.Count - 1;
                }
                else
                {
                    // ищем минимум в буфере топа
                    int minIdx = 0;
                    for (int m = 1; m < wantTop; m++)
                        if (topBuf[m].TotalMs < topBuf[minIdx].TotalMs) minIdx = m;
                    if (e.TotalMs <= topBuf[minIdx].TotalMs) continue;
                    topBuf[minIdx] = (name, e.TotalMs, e.MaxMs, e.Calls, e.SnapshotCount);
                    pos = minIdx;
                }
                // пузырьком вверх по TotalMs
                while (pos > 0 && topBuf[pos].TotalMs > topBuf[pos - 1].TotalMs)
                {
                    var tmp = topBuf[pos]; topBuf[pos] = topBuf[pos - 1]; topBuf[pos - 1] = tmp;
                    pos--;
                }
            }
            for (int t = 0; t < topBuf.Count; t++)
            {
                var (name, total, peak, calls, snaps) = topBuf[t];
                double perFramePct = snaps > 0 ? total / snaps / 16.667 * 100.0 : 0.0;
                sb.Append("    ").Append(name).Append(' ', Math.Max(1, 40 - name.Length))
                    .Append("Σ ").Append(total.ToString("F1").PadLeft(7)).Append("ms | пик ")
                    .Append(peak.ToString("F2").PadLeft(6)).Append("ms | вызовов ")
                    .Append(calls.ToString().PadLeft(6)).Append(" | %кадра ")
                    .Append(perFramePct.ToString("F1").PadLeft(5)).AppendLine();
            }
        }
        sb.AppendLine();

        // ---- 3. Маркеры ----
        if (_markers.Count > 0)
        {
            sb.AppendLine("--- МАРКЕРЫ ---");
            foreach (var (at, text) in _markers)
                sb.Append("  [").Append(FormatClock(at)).Append("] ").AppendLine(text);
            sb.AppendLine();
        }

        // ---- 4. Спайки (последние 50) ----
        if (_spikes.Count > 0)
        {
            sb.AppendLine($"--- СПАЙКИ (пик ≥ {SpikeThresholdMs:F1}ms, последние 50) ---");
            int s0 = Math.Max(0, _spikes.Count - 50);
            for (int i = s0; i < _spikes.Count; i++)
            {
                var (at, method, peak) = _spikes[i];
                sb.Append("  [").Append(FormatClock(at)).Append("] ")
                    .Append(method).Append(" пик ").Append(peak.ToString("F2")).AppendLine("ms");
            }
            sb.AppendLine();
        }

        // ---- 5. Заметки ----
        if (_notes.Count > 0)
        {
            sb.AppendLine("--- ЗАМЕТКИ ---");
            foreach (var (at, text) in _notes)
                sb.Append("  [").Append(FormatClock(at)).Append("] ").AppendLine(text);
            sb.AppendLine();
        }

        // ---- 6. Журнал нажатий/кнопок/функций (хвост, по лимиту отчёта) ----
        sb.AppendLine($"--- ЖУРНАЛ ВВОДА: кнопки и функции (последние {DefaultReportInputs}) ---");
        int inputStart = Math.Max(0, _inputs.Count - DefaultReportInputs);
        int k = 0;
        foreach (var input in _inputs)
        {
            if (k++ < inputStart) continue;
            sb.Append("  ").AppendLine(input.ToString());
        }
        _reportCache = sb.ToString();
        _reportCacheKey = key;
        return _reportCache;
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

    private static string FormatClockStatic(double seconds) => FormatClock(seconds);
}
