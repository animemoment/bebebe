using Godot;
using Game.Core;
using System;
using System.Collections.Generic;
using System.Text;

namespace Game.UI;

public partial class PerformanceOverlay : CanvasLayer
{
    private const int TrendLength = 60;              // точек тренда (1/сек -> 60 секунд)
    private const double FrameBudgetMs = 16.667;     // бюджет кадра при 60 FPS

    /// <summary>Группа методов одного скрипта для пагинации.</summary>
    private sealed class ScriptGroup
    {
        public string Script;
        public double TotalMs;
        public readonly List<GameProfiler.MetricSnapshot> Methods = new();
    }

    private PanelContainer _panel;
    private RichTextLabel _label;
    private Label _copyStatus;
    private Button _pauseButton;
    private bool _isVisible = true;
    private float _refreshTimer;
    private float _trendTimer;

    private GameProfiler.MetricSnapshot[] _metrics = Array.Empty<GameProfiler.MetricSnapshot>();
    private readonly List<ScriptGroup> _groups = new();
    private int _pageIndex;

    // Кольцевые буферы трендов (FPS и % нагрузки от бюджета кадра)
    private readonly float[] _fpsHistory = new float[TrendLength];
    private readonly float[] _loadHistory = new float[TrendLength];
    private int _trendIndex;
    private int _trendFilled;

    public override void _Ready()
    {
        Layer = 120;

        _panel = new PanelContainer
        {
            Name = "ProfilerPanel",
            Position = new Vector2(16, 16),
            CustomMinimumSize = new Vector2(780, 380),
            MouseFilter = Control.MouseFilterEnum.Pass // клики мимо кнопок проходят в игру
        };
        _panel.AddThemeStyleboxOverride("panel", MakePanelStyle());

        var root = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        root.AddThemeConstantOverride("separation", 6);
        _panel.AddChild(root);

        // Заголовок: навигация по скриптам + управление базой
        var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        header.AddThemeConstantOverride("separation", 4);
        root.AddChild(header);

        header.AddChild(MakeButton("<", () => _pageIndex = Math.Max(0, _pageIndex - 1)));
        header.AddChild(MakeButton(">", () => _pageIndex = Math.Min(_groups.Count - 1, _pageIndex + 1)));

        _pauseButton = MakeButton("Pause", () =>
        {
            ProfilerLogService.Instance.TogglePause();
            _pauseButton.Text = ProfilerLogService.Instance.IsPaused ? "Resume" : "Pause";
        });
        header.AddChild(_pauseButton);

        header.AddChild(MakeButton("Reset", () =>
        {
            ProfilerLogService.Instance.Reset();
            _pageIndex = 0;
        }));

        header.AddChild(MakeButton("Copy", () =>
        {
            DisplayServer.ClipboardSet(ProfilerLogService.Instance.BuildReport());
            _copyStatus.Text = "copied";
        }));

        _copyStatus = new Label
        {
            Text = "",
            Modulate = new Color(0.6f, 0.95f, 0.6f),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        header.AddChild(_copyStatus);

        _label = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            AutowrapMode = TextServer.AutowrapMode.Off,
            CustomMinimumSize = new Vector2(740, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        root.AddChild(_label);

        AddChild(_panel);
    }

    /// <summary>
    /// Запись ВСЕХ нажатий (включая клики по HUD-кнопкам, которые _UnhandledInput не видит)
    /// в базу. Работает независимо от видимости панели.
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        string description = null;

        if (@event is InputEventKey key && key.Pressed)
        {
            description = $"Key {key.Keycode} (phys {key.PhysicalKeycode})";
        }
        else if (@event is InputEventMouseButton mouse && mouse.Pressed)
        {
            description = $"Mouse {mouse.ButtonIndex} @ ({mouse.Position.X:0},{mouse.Position.Y:0})";
        }

        if (description != null)
            ProfilerLogService.Instance.RecordInput(description);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.F3)
        {
            _isVisible = !_isVisible;
            _panel.Visible = _isVisible;
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        _refreshTimer += (float)delta;
        if (_refreshTimer < 0.10f) // 10 раз в секунду
            return;
        _refreshTimer = 0f;

        // Снапшот и запись в базу идут ВСЕГДА, независимо от видимости панели (F3)
        GameProfiler.SnapshotMetrics(out _metrics, (float)delta);
        ProfilerLogService.Instance.Accumulate(_metrics, (float)delta);

        // Тренды: 1 точка в секунду (кольцевой буфер на 60 сек)
        _trendTimer += (float)delta;
        if (_trendTimer >= 1.0f)
        {
            _trendTimer = 0f;
            double totalMs = 0.0;
            foreach (var m in _metrics) totalMs += m.AvgMs;

            _fpsHistory[_trendIndex] = (float)Engine.GetFramesPerSecond();
            _loadHistory[_trendIndex] = (float)(totalMs / FrameBudgetMs * 100.0);
            _trendIndex = (_trendIndex + 1) % TrendLength;
            _trendFilled = Math.Min(_trendFilled + 1, TrendLength);
        }

        if (!_isVisible)
            return;

        UpdateGroups();
        _label.Text = BuildPanelText();
    }

    /// <summary>Группирует живой снапшот по скриптам, сортирует по нагрузке.</summary>
    private void UpdateGroups()
    {
        _groups.Clear();
        foreach (var m in _metrics)
        {
            if (m.AvgMs < 0.005 && m.CallsPerSec == 0) continue;

            int dot = m.Name.LastIndexOf('.');
            string script = dot > 0 ? m.Name.Substring(0, dot) : m.Name;

            var group = _groups.Find(g => g.Script == script);
            if (group == null)
            {
                group = new ScriptGroup { Script = script };
                _groups.Add(group);
            }

            group.Methods.Add(m);
            group.TotalMs += m.AvgMs;
        }

        _groups.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
        _pageIndex = Math.Clamp(_pageIndex, 0, Math.Max(0, _groups.Count - 1));
    }

    private string BuildPanelText()
    {
        double fps = Engine.GetFramesPerSecond();
        double frameTimeMs = 1000.0 / Math.Max(1.0, fps);
        long ramMb = GC.GetTotalMemory(false) / (1024 * 1024);
        var log = ProfilerLogService.Instance;

        var sb = new StringBuilder(2048);
        sb.Append("[b][color=#61afef]=== SCRIPT PERFORMANCE PROFILER (F3) ===[/color][/b]\n");
        sb.Append($"[b]FPS:[/b] {FormatFps(fps)} ({frameTimeMs,4:F1}ms)  |  [b]RAM:[/b] {ramMb} MB  |  [b]База:[/b] {log.SecondCount} сек");
        if (log.IsPaused)
            sb.Append("  [b][color=#e06c75]PAUSED[/color][/b]");
        sb.Append("\n");

        // Символьные тренды за последние 60 секунд
        float maxFps = MaxOf(_fpsHistory, _trendFilled);
        sb.Append($"[color=#98c379]FPS  :[/color] {RenderTrend(_fpsHistory, maxFps)}  [color=#5c6370](max {maxFps:F0})[/color]\n");
        sb.Append($"[color=#e5c07b]LOAD :[/color] {RenderTrend(_loadHistory, 100f)}  [color=#5c6370](% бюджета кадра)[/color]\n");
        sb.Append("[color=#3e4451]--------------------------------------------------------------------------------[/color]\n");

        if (_groups.Count == 0)
        {
            sb.Append("[color=#5c6370]  Нет активных измерений нагрузки...[/color]\n");
            return sb.ToString();
        }

        var group = _groups[_pageIndex];
        sb.Append($"[b][color=#61afef]=== {group.Script}[/color][/b]  [color=#5c6370](стр. {_pageIndex + 1}/{_groups.Count} | методов: {group.Methods.Count} | Σ {group.TotalMs:F1}ms)[/color]\n");
        sb.Append("[color=#abb2bf][b]МЕТОД                           AVG       MAX     CALLS/s    LOAD   %КАДРА[/b][/color]\n");

        foreach (var m in group.Methods)
        {
            if (m.AvgMs < 0.005 && m.CallsPerSec == 0) continue;

            string name = FormatMethodName(m.Name, 31);
            double framePct = m.AvgMs / FrameBudgetMs * 100.0;
            sb.Append($"{name}  {FormatMs(m.AvgMs, 6)}  {FormatMs(m.MaxMs, 6)}  {FormatCalls(m.CallsPerSec, 8)}  {FormatLoad(m.PercentLoad, 5)}  {framePct,5:F0}%\n");
        }
        sb.Append("\n");

        // Легенда-пагинация: по 2 скрипта в строке, текущий подсвечен
        sb.Append("[color=#3e4451]--- СКРИПТЫ ---[/color]\n");
        for (int i = 0; i < _groups.Count; i++)
        {
            string marker = i == _pageIndex ? "[color=#e5c07b][b]>[/b][/color] " : "  ";
            string sname = FormatMethodName(_groups[i].Script, 24);
            sb.Append($"{marker}{sname} {_groups[i].Methods.Count}ф.  ");
            if ((i & 1) == 1) sb.Append("\n");
        }

        return sb.ToString();
    }

    /// <summary>Рисует символьный тренд (10 уровней плотности) из кольцевого буфера.</summary>
    private static string RenderTrend(float[] history, float maxValue)
    {
        const string bar = " .:-=+*#%@";
        var sb = new StringBuilder(TrendLength + 2);
        for (int k = 0; k < TrendLength; k++)
        {
            float v = history[k];
            int level = maxValue > 0.001f ? (int)(v / maxValue * (bar.Length - 1)) : 0;
            sb.Append(bar[Math.Clamp(level, 0, bar.Length - 1)]);
        }
        return sb.ToString();
    }

    private static float MaxOf(float[] values, int count)
    {
        float max = 1f;
        for (int i = 0; i < count; i++)
            if (values[i] > max) max = values[i];
        return max;
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 26)
        };
        button.Pressed += onClick;
        return button;
    }

    private static StyleBoxFlat MakePanelStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.04f, 0.07f, 0.92f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.25f, 0.30f, 0.45f, 0.85f)
        };
    }

    /// <summary>Автосохранение лога при закрытии сцены/игры в user://logs/.</summary>
    public override void _ExitTree()
    {
        try
        {
            string dir = ProjectSettings.GlobalizePath("user://logs");
            string path = System.IO.Path.Combine(dir, $"profiler_session_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            ProfilerLogService.Instance.SaveToFile(path);
            GD.Print($"[ProfilerOverlay] Лог сохранён: {path}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ProfilerOverlay] Не удалось сохранить лог: {ex.Message}");
        }
    }

    private static string FormatMethodName(string str, int targetLen)
    {
        if (string.IsNullOrEmpty(str)) return new string(' ', targetLen);
        if (str.Length > targetLen)
            return str.Substring(0, targetLen - 2) + "..";
        return str.PadRight(targetLen);
    }

    private static string FormatCalls(int calls, int width)
    {
        string text;
        if (calls >= 1_000_000)
            text = $"{calls / 1_000_000f:F2}M/s";
        else if (calls >= 10_000)
            text = $"{calls / 1_000f:F1}k/s";
        else
            text = $"{calls}/s";

        string padded = text.PadLeft(width);
        return calls > 500_000 ? $"[color=#e06c75]{padded}[/color]" : $"[color=#abb2bf]{padded}[/color]";
    }

    private static string FormatLoad(double percent, int width)
    {
        string text = $"{percent,4:F0}%".PadLeft(width);
        if (percent >= 35.0)
            return $"[color=#e06c75][b]{text}[/b][/color]";
        if (percent >= 15.0)
            return $"[color=#e5c07b]{text}[/color]";
        return $"[color=#61afef]{text}[/color]";
    }

    private static string FormatMs(double ms, int width)
    {
        string text = $"{ms,5:F2}ms".PadLeft(width);
        if (ms >= 10.0)
            return $"[color=#e06c75][b]{text}[/b][/color]";
        if (ms >= 2.0)
            return $"[color=#e5c07b]{text}[/color]";
        return $"[color=#98c379]{text}[/color]";
    }

    private static string FormatFps(double fps)
    {
        if (fps >= 55) return $"[color=#98c379]{fps:F0}[/color]";
        if (fps >= 30) return $"[color=#e5c07b]{fps:F0}[/color]";
        return $"[color=#e06c75][b]{fps:F0}[/b][/color]";
    }
}

