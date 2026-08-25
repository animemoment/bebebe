using Godot;
using Game.Core;
using System;
using System.Text;

namespace Game.UI;

public partial class PerformanceOverlay : CanvasLayer
{
    private PanelContainer _panel;
    private RichTextLabel _label;
    private bool _isVisible = true;
    private float _refreshTimer = 0f;

    public override void _Ready()
    {
        Layer = 120;

        _panel = new PanelContainer
        {
            Name = "ProfilerPanel",
            Position = new Vector2(16, 16),
            CustomMinimumSize = new Vector2(440, 260),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.04f, 0.06f, 0.88f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.2f, 0.25f, 0.35f, 0.8f)
        };
        _panel.AddThemeStyleboxOverride("panel", style);

        _label = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            CustomMinimumSize = new Vector2(420, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _panel.AddChild(_label);
        AddChild(_panel);
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
        if (!_isVisible) return;

        _refreshTimer += (float)delta;
        if (_refreshTimer < 0.09f)
            return;

        _refreshTimer = 0f;

        double fps = Engine.GetFramesPerSecond();
        double frameTimeMs = 1000.0 / Math.Max(1.0, fps);
        long totalMemoryMb = GC.GetTotalMemory(false) / (1024 * 1024);
        int gen0 = GC.CollectionCount(0);

        GameProfiler.SnapshotMetrics(out var metrics, (float)delta);

        var sb = new StringBuilder(2048);

        sb.Append("[b][color=#61afef]=== SCRIPT PERFORMANCE PROFILER (F3) ===[/color][/b]\n");
        sb.Append($"[b]FPS:[/b] {FormatFps(fps)} ({frameTimeMs:F1} ms)  |  [b]RAM (GC):[/b] {totalMemoryMb} MB (Gen0: {gen0})\n");
        sb.Append("[color=#4b5263]------------------------------------------------------------[/color]\n");
        sb.Append("[color=#abb2bf][b]SCRIPT / METHOD                      AVG     MAX    CALLS/s  LOAD[/b][/color]\n");

        int displayedCount = 0;
        foreach (var m in metrics)
        {
            if (m.AvgMs < 0.01 && m.CallsPerSec == 0) continue;
            if (displayedCount++ >= 16) break; // Топ-16 самых нагруженных методов

            bool isHeavy = m.AvgMs >= 1.5 || m.MaxMs >= 5.0 || m.PercentLoad >= 25.0;
            string tag = isHeavy ? "[color=#e06c75][HEAVY][/color] " : "        ";
            string name = TruncateOrPad(m.Name, 28);

            string avgCol = FormatMs(m.AvgMs);
            string maxCol = FormatMs(m.MaxMs);
            string callsCol = $"{m.CallsPerSec,5}";
            string loadCol = $"{m.PercentLoad,4:F0}%";

            sb.Append($"{tag}[color=#e5c07b]{name}[/color] {avgCol}  {maxCol} {callsCol}  [color=#61afef]{loadCol}[/color]\n");
        }

        if (displayedCount == 0)
        {
            sb.Append("[color=#5c6370]  Нет активных измерений нагрузки...[/color]\n");
        }

        _label.Text = sb.ToString();
    }

    private static string TruncateOrPad(string str, int len)
    {
        if (str.Length > len)
            return str.Substring(0, len - 2) + "..";
        return str.PadRight(len);
    }

    private static string FormatMs(double ms)
    {
        if (ms < 0.5)
            return $"[color=#98c379]{ms,5:F2}ms[/color]";
        if (ms < 2.0)
            return $"[color=#e5c07b]{ms,5:F2}ms[/color]";
        return $"[color=#e06c75][b]{ms,5:F2}ms[/b][/color]";
    }

    private static string FormatFps(double fps)
    {
        if (fps >= 55) return $"[color=#98c379]{fps:F0}[/color]";
        if (fps >= 30) return $"[color=#e5c07b]{fps:F0}[/color]";
        return $"[color=#e06c75][b]{fps:F0}[/b][/color]";
    }
}    