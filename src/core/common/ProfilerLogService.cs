using System;
using System.Collections.Generic;
using System.Text;

namespace Game.Core;

/// <summary>
/// Структура контекста кадра, рендера, железа, состояния мира и симуляции для отчёта AI.
/// Передаётся из PerformanceOverlay при нажатии кнопки [Copy AI].
/// </summary>
public struct AiReportContext
{
    // Железо и платформа
    public string OsDescription;
    public int CpuCores;
    public int EngineMaxFps;
    public double Fps;
    public double FrameTimeMs;
    public double FpsMin;
    public double Fps1PercentLow;
    public double ProcessMs;
    public double PhysicsMs;
    public long DrawCalls;
    public long RenderObjects;
    public long Primitives;
    public double VramMb;
    public long RamMb;

    // Память и сборщик мусора (.NET GC)
    public int GcGen0Count;
    public int GcGen1Count;
    public int GcGen2Count;
    public double GcTotalPauseMs;

    // Состояние мира и геймплея
    public int GameDay;
    public int GameHour;
    public int GameMinute;
    public string CameraPos;
    public string CameraZoom;
    public int ActiveJobsCount;
    public int GroundPilesCount;

    // Симуляция и потоки
    public int AgentCount;
    public string SimBackend;
    public double SimSpeed;
    public double SimCycleAvgMs;
    public double SimCycleMaxMs;
    public int SimStepsPerSec;
    public double SimMsPerFrame;
    public double Phase2AvgMs;
    public double Phase3aAvgMs;
    public double Phase3bAvgMs;
    public double SnapshotAvgMs;

    public GameProfiler.BalanceSnapshot[] Balance;
}

/// <summary>
/// Запись о спайке с корреляцией и контекстом (что вызвало).
/// </summary>
public sealed class SpikeRecord
{
    public double AtSec;
    public string Method;
    public double PeakMs;
    public string CauseContext;
}

/// <summary>
/// Побочная «база» профилирования: накапливает 10Гц-снапшоты <see cref="GameProfiler"/>
/// в посекундные окна, хранит журнал нажатий и строит текстовые и AI-отчёты.
/// Потокобезопасна — защищена блокировкой.
/// </summary>
public sealed class ProfilerLogService
{
    public static ProfilerLogService Instance { get; } = new();

    private readonly object _lock = new();

    /// <summary>Максимальный размер посекундного лога (~4 часа при 1 сек/окно).</summary>
    public const int MaxRecordedSeconds = 14_400;

    /// <summary>Максимальный размер журнала нажатий.</summary>
    public const int MaxInputs = 50_000;

    public const int DefaultReportSeconds = 60;
    public const int DefaultReportTopMethods = 15;
    public const int DefaultReportInputs = 50;

    /// <summary>Посекундные агрегаты одного метода.</summary>
    public sealed class SecondEntry
    {
        public double TotalMs;      // сумма времени метода за 1 секунду (мс/с)
        public double MaxMs;        // максимум одиночного замера за секунду
        public int Calls;           // суммарное число вызовов за секунду
        public int Frames;          // сколько кадров отрисовано за эту секунду
        public int SnapshotCount;   // сколько 10Гц-снапшотов легло в окно
    }

    /// <summary>Сводка скрипта за окно.</summary>
    public sealed class ScriptSummary
    {
        public string Script;
        public double TotalMs;
        public double MaxMs;
        public readonly HashSet<string> Methods = new(StringComparer.Ordinal);
    }

    private Dictionary<string, SecondEntry> _current = new(StringComparer.Ordinal);
    private readonly List<Dictionary<string, SecondEntry>> _seconds = new();
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

    private readonly List<(double AtSec, string Text)> _markers = new();
    private readonly List<(double AtSec, string Text)> _notes = new();
    private readonly List<SpikeRecord> _spikes = new();

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

    public int MarkerCount { get { lock (_lock) return _markers.Count; } }
    public int NoteCount { get { lock (_lock) return _notes.Count; } }
    public int SpikeCount { get { lock (_lock) return _spikes.Count; } }
    public bool IsPaused { get { lock (_lock) return _paused; } }
    public double SessionSeconds { get { lock (_lock) return _sessionSecs; } }
    public int SecondCount { get { lock (_lock) return _seconds.Count; } }
    public int InputCount { get { lock (_lock) return _inputs.Count; } }

    /// <summary>
    /// Принимает очередной снапшот профайлера и накапливает его в текущее посекундное окно.
    /// </summary>
    public void Accumulate(GameProfiler.MetricSnapshot[] metrics, double windowSec, int frames)
    {
        if (metrics == null || metrics.Length == 0)
            return;

        if (!double.IsFinite(windowSec) || windowSec <= 0.0)
            windowSec = 0.016;
        if (frames < 0) frames = 0;

        lock (_lock)
        {
            if (_paused) return;

            _windowAccum += windowSec;

            foreach (var m in metrics)
            {
                if (m.TotalMs < 0.0005 && m.MaxMs < 0.0005 && m.Calls == 0)
                    continue;

                if (!_current.TryGetValue(m.Name, out var entry))
                {
                    entry = new SecondEntry();
                    _current[m.Name] = entry;
                }

                entry.TotalMs += m.TotalMs;
                if (m.MaxMs > entry.MaxMs) entry.MaxMs = m.MaxMs;
                entry.Calls += m.Calls;
                entry.Frames += frames;
                entry.SnapshotCount++;
            }

            if (_windowAccum >= 1.0)
                FlushWindow();
        }
    }

    private void FlushWindow()
    {
        // Вызывается строго под _lock
        _sessionSecs += _windowAccum;
        _windowAccum = 0f;
        _seconds.Add(_current);

        // Автофиксация спайков с поиском контекста/причины
        if (SpikeThresholdMs > 0.0)
        {
            foreach (var (name, e) in _current)
            {
                if (e.MaxMs >= SpikeThresholdMs)
                {
                    string context = GetRecentContext(_sessionSecs, name);
                    _spikes.Add(new SpikeRecord
                    {
                        AtSec = _sessionSecs,
                        Method = name,
                        PeakMs = e.MaxMs,
                        CauseContext = context
                    });
                }
            }
            while (_spikes.Count > 2000)
                _spikes.RemoveAt(0);
        }
        _current = new Dictionary<string, SecondEntry>(StringComparer.Ordinal);

        while (_seconds.Count > KeepSeconds)
            _seconds.RemoveAt(0);
        if (_seconds.Count > MaxRecordedSeconds)
            _seconds.RemoveAt(0);
    }

    private string GetRecentContext(double atSec, string methodName)
    {
        string subExplanation = GetKnownSubsystemExplanation(methodName);

        string userAction = null;
        foreach (var input in _inputs)
        {
            double dt = atSec - input.AtSec;
            if (dt >= 0 && dt <= 3.5)
            {
                userAction = $"{input.Kind}: {input.Text} (за {dt:F1}с)";
            }
        }

        if (userAction != null && subExplanation != null)
            return $"{subExplanation} | Триггер: {userAction}";
        if (userAction != null)
            return $"Триггер: {userAction}";
        return subExplanation ?? "Периодическая фоновая активность";
    }

    private static string GetKnownSubsystemExplanation(string methodName)
    {
        if (methodName.Contains("Humidity"))
            return "Периодический тик карты влажности 512×512 (CPU скалярный обход)";
        if (methodName.Contains("Snapshots"))
            return "Генерация снапшотов мира (30 Гц: предметы, склады, урожай)";
        if (methodName.Contains("Phase3b_Rebuild"))
            return "Перестроение пространственной сетки агентов (SpatialGrid)";
        if (methodName.Contains("Phase3b_Commit") || methodName.Contains("Phase3b_SequentialCommit"))
            return "Пакетная фиксация перемещений и передача ресурсов";
        if (methodName.Contains("Phase3a_Fused") || methodName.Contains("Phase3a_ParallelBookkeeping"))
            return "Пакетный пересчёт потребностей и сканирование еды";
        if (methodName.Contains("Phase2_ParallelUpdate") || methodName.Contains("Phase2"))
            return "Кинематика 10k агентов и поиск путей A* (HierarchicalPathfinder)";
        if (methodName.Contains("MultiMesh") || methodName.Contains("Agent MultiMesh"))
            return "Обновление буферов инстансинга MultiMesh на GPU";
        if (methodName.Contains("Shadows"))
            return "Отрисовка и батчинг теней сущностей";
        return null;
    }

    /// <summary>Записывает описание события ввода (клавиша / кнопка мыши).</summary>
    public void RecordInput(string description) => RecordInput("key", description);

    /// <summary>
    /// Записывает событие ввода с видом: key | mouse | button | func.
    /// </summary>
    public void RecordInput(string kind, string description)
    {
        if (string.IsNullOrEmpty(description))
            return;

        lock (_lock)
        {
            if (_paused) return;
            _inputs.Enqueue(new InputEntry { AtSec = _sessionSecs + _windowAccum, Kind = kind ?? "key", Text = description });
            while (_inputs.Count > MaxInputs)
                _inputs.Dequeue();
            _reportCache = null;
        }
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
        lock (_lock)
        {
            _paused = true;
            _windowAccum = 0f;
            _current.Clear();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _paused = false;
            _windowAccum = 0f;
            _current.Clear();
        }
    }

    public void TogglePause()
    {
        lock (_lock)
        {
            if (_paused) Resume();
            else Pause();
        }
    }

    /// <summary>Полный сброс: очищает лог, журнал и счётчик сессии.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _seconds.Clear();
            _inputs.Clear();
            _markers.Clear();
            _notes.Clear();
            _spikes.Clear();
            _current.Clear();
            _windowAccum = 0f;
            _sessionSecs = 0f;
            _reportCache = null;
            _reportCacheKey = 0;
        }
    }

    public void AddMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_lock)
        {
            _markers.Add((_sessionSecs, text.Trim()));
            _reportCache = null;
        }
    }

    public void AddNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_lock)
        {
            _notes.Add((_sessionSecs, text.Trim()));
            _reportCache = null;
        }
    }

    public List<(double AtSec, string Text)> GetMarkers()
    {
        lock (_lock) return new List<(double AtSec, string Text)>(_markers);
    }

    public List<(double AtSec, string Text)> GetNotes()
    {
        lock (_lock) return new List<(double AtSec, string Text)>(_notes);
    }

    public List<SpikeRecord> GetSpikes()
    {
        lock (_lock) return new List<SpikeRecord>(_spikes);
    }

    public void ClearMarkers() { lock (_lock) { _markers.Clear(); _reportCache = null; } }
    public void ClearNotes() { lock (_lock) { _notes.Clear(); _reportCache = null; } }
    public void ClearSpikes() { lock (_lock) { _spikes.Clear(); } }

    /// <summary>
    /// Топ-методов за последние <paramref name="lastSeconds"/> секунд окна.
    /// </summary>
    public List<(string Name, double TotalMs, double MaxMs)> GetTopMethods(int lastSeconds, int topN)
    {
        lock (_lock)
        {
            return GetTopMethodsInternal(lastSeconds, topN);
        }
    }

    private List<(string Name, double TotalMs, double MaxMs)> GetTopMethodsInternal(int lastSeconds, int topN)
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
    /// Вычисляет точную и актуальную сводку по скриптам за указанное окно секунд без утечек и накопления мусора.
    /// </summary>
    public List<ScriptSummary> GetScriptSummaries(int lastSeconds = 0)
    {
        lock (_lock)
        {
            int wantSecs = lastSeconds > 0 ? lastSeconds : _seconds.Count;
            int start = Math.Max(0, _seconds.Count - wantSecs);
            var map = new Dictionary<string, ScriptSummary>(StringComparer.Ordinal);
            for (int i = start; i < _seconds.Count; i++)
            {
                foreach (var (name, e) in _seconds[i])
                {
                    var (script, method) = SplitName(name);
                    if (!map.TryGetValue(script, out var s))
                    {
                        s = new ScriptSummary { Script = script };
                        map[script] = s;
                    }
                    s.TotalMs += e.TotalMs;
                    if (e.MaxMs > s.MaxMs) s.MaxMs = e.MaxMs;
                    if (!string.IsNullOrEmpty(method)) s.Methods.Add(method);
                }
            }
            var list = new List<ScriptSummary>(map.Values);
            list.Sort(static (a, b) => b.TotalMs.CompareTo(a.TotalMs));
            return list;
        }
    }

    /// <summary>Ряд суммарной нагрузки (мс) по секундам окна для мини-графика.</summary>
    public float[] GetLoadSeries(int maxPoints)
    {
        lock (_lock)
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
    }

    /// <summary>Дифф двух секунд окна: что выросло/упало по суммарным мс.</summary>
    public List<(string Name, double DeltaMs)> DiffSeconds(int secA, int secB, int topN)
    {
        lock (_lock)
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
    }

    /// <summary>CSV-дамп: метрики окна + журнал ввода с кнопками/функциями.</summary>
    public string BuildCsv()
    {
        lock (_lock)
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
    }

    public string BuildSummaryLine()
    {
        lock (_lock)
        {
            var summaries = GetScriptSummaries(60);
            string topScript = summaries.Count > 0 ? summaries[0].Script : "—";
            double topMs = summaries.Count > 0 ? summaries[0].TotalMs : 0.0;
            return $"сессия {FormatDuration(_sessionSecs)} | окно {_seconds.Count}с | топ-скрипт {topScript} ({topMs / 1000.0:F1}s) | маркеров {_markers.Count} | спайков {_spikes.Count} | заметок {_notes.Count}";
        }
    }

    private string _reportCache;
    private long _reportCacheKey;

    public void SaveToFile(string absolutePath)
    {
        var dir = System.IO.Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(absolutePath, BuildReport());
    }

    public void SaveAiReportToFile(string absolutePath, AiReportContext ctx)
    {
        var dir = System.IO.Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(absolutePath, BuildAiReport(ctx));
    }

    /// <summary>
    /// Строит глубокий структурированный отчёт в Markdown для анализа языковой моделью (AI).
    /// Включает состояние рендера, железа, геймплея, мира, GC, симуляции, балансировки ядер и спайков с причинами.
    /// </summary>
    public string BuildAiReport(AiReportContext ctx, int windowSecs = 60)
    {
        lock (_lock)
        {
            int realWindow = Math.Min(windowSecs, Math.Max(1, _seconds.Count));
            var sb = new StringBuilder(6144);
            sb.AppendLine("```markdown");
            sb.AppendLine("### 🖥️ СИСТЕМНОЕ ОКРУЖЕНИЕ & НАСТРОЙКИ");
            sb.AppendLine($"- **Платформа**: {ctx.OsDescription ?? "Windows"} | **CPU Cores**: {ctx.CpuCores} | **Godot**: .NET 8 (Forward+)");
            sb.AppendLine($"- **Симуляция**: {ctx.AgentCount:N0} агентов | **Бэкенд**: {ctx.SimBackend ?? "CPU SIMD"} | **Скорость**: {ctx.SimSpeed:F1}x | **FPS Cap**: {ctx.EngineMaxFps}");
            sb.AppendLine();

            sb.AppendLine("### 📊 КАДР & РЕНДЕР (MAIN THREAD)");
            sb.AppendLine($"- **FPS**: {ctx.Fps:F0} (кадр {ctx.FrameTimeMs:F1}ms | 1% Low: {ctx.Fps1PercentLow:F0} FPS | Min: {ctx.FpsMin:F0} FPS)");
            sb.AppendLine($"- **Время кадра**: Process: {ctx.ProcessMs:F2}ms | Physics: {ctx.PhysicsMs:F2}ms");
            sb.AppendLine($"- **Рендер**: DrawCalls: {ctx.DrawCalls:N0} | Objects: {ctx.RenderObjects:N0} | Primitives: {ctx.Primitives:N0}");
            sb.AppendLine($"- **Память**: RAM (GC Heap): {ctx.RamMb} MB | VRAM: {ctx.VramMb:F1} MB");
            sb.AppendLine($"- **Сборщик мусора (.NET GC)**: Gen0: {ctx.GcGen0Count} | Gen1: {ctx.GcGen1Count} | Gen2: {ctx.GcGen2Count} | Общая пауза GC: {ctx.GcTotalPauseMs:F1}ms");
            sb.AppendLine();

            sb.AppendLine("### 🌍 СОСТОЯНИЕ МИРА & ГЕЙМПЛЕЙ");
            sb.AppendLine($"- **Время в мире**: День {ctx.GameDay}, {ctx.GameHour:D2}:{ctx.GameMinute:D2}");
            sb.AppendLine($"- **Камера игрока**: Pos: {ctx.CameraPos ?? "(0,0)"} | Zoom: {ctx.CameraZoom ?? "1.0x"}");
            sb.AppendLine($"- **Задачи и сущности**: Активных задач: {ctx.ActiveJobsCount} | Предметов на земле: {ctx.GroundPilesCount:N0}");
            sb.AppendLine();

            sb.AppendLine("### ⚙️ СИМУЛЯЦИЯ (C# WORKERS & THREADPOOL)");
            sb.AppendLine($"- **1-Step (1 tick)**: avg {ctx.SimCycleAvgMs:F3}ms | peak {ctx.SimCycleMaxMs:F2}ms | steps/sec: {ctx.SimStepsPerSec:N0} | sim/frame: {ctx.SimMsPerFrame:F2}ms");
            sb.AppendLine($"- **Фазы шага**: P2(AI/Движ): {ctx.Phase2AvgMs:F3}ms | P3a(Нужды): {ctx.Phase3aAvgMs:F3}ms | P3b(Ресурсы): {ctx.Phase3bAvgMs:F3}ms | Снапшот: {ctx.SnapshotAvgMs:F3}ms");
            if (ctx.Balance != null && ctx.Balance.Length > 0)
            {
                sb.AppendLine("- **Балансировка ядер (DynamicWorkScheduler)**:");
                foreach (var b in ctx.Balance)
                {
                    sb.AppendLine($"  - `{b.Name}`: Wall {b.WallMsPerFrame:F2}ms/frame | CPU {b.CpuMsPerFrame:F2}ms/frame | Простой (Idle): {b.ImbalancePct}% | Худший батч: {b.MaxBatchMs:F2}ms | Stragglers: {b.StragglerCount}");
                }
            }
            sb.AppendLine();

            sb.AppendLine($"### 🚨 ТОП УЗКИХ МЕСТ (последние {realWindow}с)");
            var top = GetTopMethodsInternal(windowSecs, 10);
            if (top.Count == 0)
            {
                sb.AppendLine("  (нет активных методов)");
            }
            else
            {
                double totalWindowMs = 0;
                foreach (var t in top) totalWindowMs += t.TotalMs;
                for (int i = 0; i < top.Count; i++)
                {
                    var t = top[i];
                    double share = totalWindowMs > 0 ? t.TotalMs / totalWindowMs * 100.0 : 0;
                    sb.AppendLine($"{i + 1}. `{t.Name}` — Σ {t.TotalMs:F1}ms ({share:F1}%) | пик {t.MaxMs:F2}ms | avg/s {(t.TotalMs / realWindow):F2}ms/s");
                }
            }
            sb.AppendLine();

            if (_spikes.Count > 0)
            {
                sb.AppendLine($"### ⚡ ХРОНИКА СПАЙКОВ С ПРИЧИНАМИ (пик ≥ {SpikeThresholdMs:F1}ms, последние 10)");
                int sStart = Math.Max(0, _spikes.Count - 10);
                for (int i = _spikes.Count - 1; i >= sStart; i--)
                {
                    var sp = _spikes[i];
                    sb.AppendLine($"- `[{FormatClock(sp.AtSec)}]` ⚡ **`{sp.Method}`** — пик **{sp.PeakMs:F2}ms**");
                    if (!string.IsNullOrEmpty(sp.CauseContext))
                    {
                        sb.AppendLine($"  └─ 🎯 Контекст/причина: {sp.CauseContext}");
                    }
                }
                sb.AppendLine();
            }

            if (_markers.Count > 0 || _notes.Count > 0)
            {
                sb.AppendLine("### 📌 МЕТКИ И ЗАМЕТКИ");
                foreach (var (at, text) in _markers)
                    sb.AppendLine($"- `[{FormatClock(at)}]` [Метка] {text}");
                foreach (var (at, text) in _notes)
                    sb.AppendLine($"- `[{FormatClock(at)}]` [Заметка] {text}");
                sb.AppendLine();
            }

            // Автодиагностика
            sb.AppendLine("### 💡 АВТО-ДИАГНОСТИКА AI");
            bool detected = false;
            if (ctx.DrawCalls > 800)
            {
                sb.AppendLine($"- ⚠️ **Высокие DrawCalls**: {ctx.DrawCalls} вызовов отрисовки! Рекомендуется объединить геометрию в MultiMesh.");
                detected = true;
            }
            if (ctx.ProcessMs > 20.0)
            {
                sb.AppendLine($"- ⚠️ **Задержка главного потока (Process: {ctx.ProcessMs:F1}ms)**: Возможно накопление фоновых вызовов или тяжелая отрисовка CanvasItem.");
                detected = true;
            }
            if (ctx.SimMsPerFrame > 12.0)
            {
                sb.AppendLine($"- ⚠️ **Симуляция перегружает кадр**: Sim/Frame = {ctx.SimMsPerFrame:F2}ms (> 12ms бюджет кадра).");
                detected = true;
            }
            if (ctx.GcGen2Count > 0)
            {
                sb.AppendLine($"- ⚠️ **Зафиксированы Full GC (Gen2: {ctx.GcGen2Count})**: Сборщик мусора вызывал полную паузу всех потоков (Stop-The-World).");
                detected = true;
            }
            if (ctx.Balance != null)
            {
                foreach (var b in ctx.Balance)
                {
                    if (b.ImbalancePct >= 30)
                    {
                        sb.AppendLine($"- ⚠️ **Дисбаланс потоков в {b.Name}**: {b.ImbalancePct}% простоя ядер (Amdahl tail). Худший батч: {b.MaxBatchMs:F2}ms, stragglers: {b.StragglerCount}.");
                        detected = true;
                    }
                }
            }
            if (!detected)
            {
                sb.AppendLine("- ✅ Критических перекосов и узких мест в кадре не зафиксировано.");
            }

            sb.AppendLine("```");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Строит стандартный текстовый отчёт лога.
    /// </summary>
    public string BuildReport()
    {
        lock (_lock)
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

            // 1. Сводка по скриптам
            var scriptList = GetScriptSummaries(wantSecs);
            double logTotalMs = 0.0;
            foreach (var s in scriptList) logTotalMs += s.TotalMs;

            sb.AppendLine($"--- СВОДКА ПО СКРИПТАМ (окно {Math.Min(wantSecs, _seconds.Count)} сек) ---");
            if (scriptList.Count == 0)
            {
                sb.AppendLine("  (данных ещё нет)");
            }
            else
            {
                foreach (var s in scriptList)
                {
                    double share = logTotalMs > 0.0 ? s.TotalMs / logTotalMs * 100.0 : 0.0;
                    sb.Append(s.Script).Append(' ', Math.Max(1, 34 - s.Script.Length))
                        .Append("методов: ").Append(s.Methods.Count.ToString().PadLeft(3))
                        .Append(" | суммарно: ").Append((s.TotalMs / 1000.0).ToString("F2").PadLeft(8)).Append('s')
                        .Append(" | пик: ").Append(s.MaxMs.ToString("F2").PadLeft(6)).Append("ms")
                        .Append(" | доля: ").Append(share.ToString("F1").PadLeft(5)).AppendLine("%");
                }
            }
            sb.AppendLine();

            // 2. Посекундный срез (только топ-методы, отсортированные корректно)
            sb.AppendLine($"--- ПОСЕКУНДНЫЙ СРЕЗ (последние {Math.Min(wantSecs, _seconds.Count)} сек × топ-{wantTop}) ---");
            int start = Math.Max(0, _seconds.Count - wantSecs);
            var topList = new List<(string Name, double TotalMs, double MaxMs, int Calls, int Frames)>();

            for (int i = start; i < _seconds.Count; i++)
            {
                double secTime = _sessionSecs - (_seconds.Count - i);
                sb.Append('[').Append(FormatClock(secTime)).AppendLine("]");
                var window = _seconds[i];
                topList.Clear();
                foreach (var (name, e) in window)
                    topList.Add((name, e.TotalMs, e.MaxMs, e.Calls, e.Frames));

                topList.Sort(static (a, b) => b.TotalMs.CompareTo(a.TotalMs));
                int count = Math.Min(wantTop, topList.Count);

                for (int t = 0; t < count; t++)
                {
                    var (name, total, peak, calls, frames) = topList[t];
                    double msPerFrame = frames > 0 ? total / frames : 0.0;
                    double perFramePct = msPerFrame / 16.667 * 100.0;
                    sb.Append("    ").Append(name).Append(' ', Math.Max(1, 40 - name.Length))
                        .Append("Σ ").Append(total.ToString("F1").PadLeft(7)).Append("ms/с | пик ")
                        .Append(peak.ToString("F2").PadLeft(6)).Append("ms | вызовов ")
                        .Append(calls.ToString().PadLeft(6)).Append(" | мс/кадр ")
                        .Append(msPerFrame.ToString("F2").PadLeft(6)).Append(" | %кадра ")
                        .Append(perFramePct.ToString("F1").PadLeft(5)).AppendLine();
                }
            }
            sb.AppendLine();

            // 3. Маркеры
            if (_markers.Count > 0)
            {
                sb.AppendLine("--- МАРКЕРЫ ---");
                foreach (var (at, text) in _markers)
                    sb.Append("  [").Append(FormatClock(at)).Append("] ").AppendLine(text);
                sb.AppendLine();
            }

            // 4. Спайки
            if (_spikes.Count > 0)
            {
                sb.AppendLine($"--- СПАЙКИ (пик ≥ {SpikeThresholdMs:F1}ms, последние 50) ---");
                int s0 = Math.Max(0, _spikes.Count - 50);
                for (int i = s0; i < _spikes.Count; i++)
                {
                    var sp = _spikes[i];
                    sb.Append("  [").Append(FormatClock(sp.AtSec)).Append("] ")
                        .Append(sp.Method).Append(" пик ").Append(sp.PeakMs.ToString("F2")).Append("ms");
                    if (!string.IsNullOrEmpty(sp.CauseContext))
                        sb.Append(" (").Append(sp.CauseContext).Append(")");
                    sb.AppendLine();
                }
                sb.AppendLine();
            }

            // 5. Заметки
            if (_notes.Count > 0)
            {
                sb.AppendLine("--- ЗАМЕТКИ ---");
                foreach (var (at, text) in _notes)
                    sb.Append("  [").Append(FormatClock(at)).Append("] ").AppendLine(text);
                sb.AppendLine();
            }

            // 6. Журнал ввода
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
