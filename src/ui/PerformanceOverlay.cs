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

	// --- Новые фичи панели: вкладки, поиск, топ, маркеры, порог, окно ---
	// Вкладки: 0=Методы, 1=Топ-10, 2=Спайки, 3=Маркеры/Заметки, 4=График
	private int _tabIndex;
	private static readonly string[] TabNames = { "Методы", "Топ-10", "Спайки", "Метки", "График" };
	private string _searchFilter = "";
	private LineEdit _searchBox;
	private LineEdit _noteBox;
	private LineEdit _csvFilterBox;
	private Label _tabLabel;
	private int _topWindowSecs = 10;
	private float _spikeMinMs = 8f;
	private double _lastCopyMs;

	public override void _Ready()
	{
		Layer = 120;

		_panel = new PanelContainer
		{
			Name = "ProfilerPanel",
			Position = new Vector2(16, 16),
			CustomMinimumSize = new Vector2(780, 430),
			MouseFilter = Control.MouseFilterEnum.Pass // клики мимо кнопок проходят в игру
		};
		_panel.AddThemeStyleboxOverride("panel", MakePanelStyle());

		var root = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
		root.AddThemeConstantOverride("separation", 6);
		_panel.AddChild(root);

		// Ряд 1: навигация по скриптам + управление базой
		var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
		header.AddThemeConstantOverride("separation", 4);
		root.AddChild(header);

		header.AddChild(MakeTrackedButton("<", "пагинация", () => _pageIndex = Math.Max(0, _pageIndex - 1)));
		header.AddChild(MakeTrackedButton(">", "пагинация", () => _pageIndex = Math.Min(_groups.Count - 1, _pageIndex + 1)));

		_pauseButton = MakeButton("Pause", () =>
		{
			ProfilerLogService.Instance.TogglePause();
			bool paused = ProfilerLogService.Instance.IsPaused;
			_pauseButton.Text = paused ? "Resume" : "Pause";
			ProfilerLogService.Instance.RecordFunction("Запись лога", !paused);
		});
		header.AddChild(_pauseButton);

		header.AddChild(MakeTrackedButton("Reset", "шапка", () =>
		{
			ProfilerLogService.Instance.Reset();
			_pageIndex = 0;
		}));

		// Фича 1: быстрое Copy — компактный срез с замером времени (фриз виден сразу).
		header.AddChild(MakeTrackedButton("Copy", "шапка", () =>
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			DisplayServer.ClipboardSet(ProfilerLogService.Instance.BuildReport());
			sw.Stop();
			_lastCopyMs = sw.Elapsed.TotalMilliseconds;
			_copyStatus.Text = $"copied {_lastCopyMs:F0}ms";
		}));

		// Фича 2: Copy CSV — полный дамп окна + журнал кнопок/функций для Excel.
		header.AddChild(MakeTrackedButton("CSV", "шапка", () =>
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			DisplayServer.ClipboardSet(ProfilerLogService.Instance.BuildCsv());
			sw.Stop();
			_copyStatus.Text = $"csv {sw.Elapsed.TotalMilliseconds:F0}ms";
		}));

		// Фича 3: Save — отчёт в user://logs с именем по времени.
		header.AddChild(MakeTrackedButton("Save", "шапка", () =>
		{
			try
			{
				string dir = ProjectSettings.GlobalizePath("user://logs");
				string path = System.IO.Path.Combine(dir, $"profiler_manual_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
				ProfilerLogService.Instance.SaveToFile(path);
				_copyStatus.Text = "saved";
			}
			catch (Exception ex) { _copyStatus.Text = "save err"; GD.PrintErr(ex.Message); }
		}));

		_copyStatus = new Label
		{
			Text = "",
			Modulate = new Color(0.6f, 0.95f, 0.6f),
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		header.AddChild(_copyStatus);

		// Ряд 2: вкладки + поиск + окно топа
		var tabs = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
		tabs.AddThemeConstantOverride("separation", 4);
		root.AddChild(tabs);
		for (int t = 0; t < TabNames.Length; t++)
		{
			int idx = t;
			// Переключение вкладок тоже пишется в лог как функция.
			tabs.AddChild(MakeTrackedButton(TabNames[t], "вкладки", () => _tabIndex = idx));
		}
		_tabLabel = new Label { Text = "", MouseFilter = Control.MouseFilterEnum.Ignore };
		tabs.AddChild(_tabLabel);

		// Ряд 3: поиск по методам + окно топа + порог спайков
		var tools = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
		tools.AddThemeConstantOverride("separation", 4);
		root.AddChild(tools);
		// Фича 4: живой поиск-фильтр по имени метода.
		_searchBox = new LineEdit { PlaceholderText = "фильтр…", CustomMinimumSize = new Vector2(140, 26) };
		_searchBox.TextChanged += text => _searchFilter = text ?? "";
		tools.AddChild(_searchBox);
		// Фича 5: окно агрегации топа 10/30/60 сек.
		tools.AddChild(MakeTrackedButton("10s", "окно топа", () => _topWindowSecs = 10));
		tools.AddChild(MakeTrackedButton("30s", "окно топа", () => _topWindowSecs = 30));
		tools.AddChild(MakeTrackedButton("60s", "окно топа", () => _topWindowSecs = 60));
		// Фича 6: порог спайков прямо из панели.
		tools.AddChild(MakeTrackedButton("спайк+", "порог", () =>
		{
			_spikeMinMs = Math.Min(100f, _spikeMinMs + 2f);
			ProfilerLogService.Instance.SpikeThresholdMs = _spikeMinMs;
		}));
		tools.AddChild(MakeTrackedButton("спайк-", "порог", () =>
		{
			_spikeMinMs = Math.Max(1f, _spikeMinMs - 2f);
			ProfilerLogService.Instance.SpikeThresholdMs = _spikeMinMs;
		}));

		// Ряд 4: маркер + заметка + CSV-фильтр
		var marks = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
		marks.AddThemeConstantOverride("separation", 4);
		root.AddChild(marks);
		// Фича 7: маркер одним кликом. Фича 8: заметка с текстом.
		marks.AddChild(MakeTrackedButton("Метка", "метки", () =>
		{
			ProfilerLogService.Instance.AddMarker($"метка #{ProfilerLogService.Instance.MarkerCount + 1}");
			_copyStatus.Text = "метка+";
		}));
		_noteBox = new LineEdit { PlaceholderText = "заметка…", CustomMinimumSize = new Vector2(180, 26) };
		marks.AddChild(_noteBox);
		marks.AddChild(MakeTrackedButton("Заметка+", "метки", () =>
		{
			ProfilerLogService.Instance.AddNote(_noteBox.Text);
			_noteBox.Text = "";
			_copyStatus.Text = "заметка+";
		}));
		// Фича 9: фильтр CSV-экспорта.
		_csvFilterBox = new LineEdit { PlaceholderText = "csv-фильтр…", CustomMinimumSize = new Vector2(120, 26) };
		_csvFilterBox.TextChanged += text => ProfilerLogService.Instance.CsvEventFilter = text;
		marks.AddChild(_csvFilterBox);
		// Фича 10: очистка меток/спаеков. Фича 11: глубина отчёта 30/120 сек.
		marks.AddChild(MakeTrackedButton("Чист.метки", "метки", () => ProfilerLogService.Instance.ClearMarkers()));
		marks.AddChild(MakeTrackedButton("Отчёт 30с", "глубина", () =>
		{
			ProfilerLogService.Instance.ReportSeconds = 30;
			ProfilerLogService.Instance.RecordFunction("Глубина отчёта", "30с");
		}));
		marks.AddChild(MakeTrackedButton("Отчёт 120с", "глубина", () =>
		{
			ProfilerLogService.Instance.ReportSeconds = 120;
			ProfilerLogService.Instance.RecordFunction("Глубина отчёта", "120с");
		}));

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
	/// Клавиши/мышь — с видом key/mouse; кнопки панели пишут себя сами через
	/// MakeButton (вид button + имя), вкл/выкл функций — через RecordFunction.
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey key && key.Pressed)
		{
			ProfilerLogService.Instance.RecordInput("key", $"Key {key.Keycode} (phys {key.PhysicalKeycode})");
		}
		else if (@event is InputEventMouseButton mouse && mouse.Pressed)
		{
			ProfilerLogService.Instance.RecordInput("mouse", $"Mouse {mouse.ButtonIndex} @ ({mouse.Position.X:0},{mouse.Position.Y:0})");
		}
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
		_tabLabel.Text = $"[{TabNames[_tabIndex]}] топ-окно {_topWindowSecs}с спайк≥{_spikeMinMs:F0}мс";
		_label.Text = _tabIndex switch
		{
			1 => BuildTopText(),
			2 => BuildSpikesText(),
			3 => BuildMarksText(),
			4 => BuildGraphText(),
			_ => BuildPanelText(),
		};
	}

	/// <summary>Группирует живой снапшот по скриптам, сортирует по нагрузке.</summary>
	private void UpdateGroups()
	{
		_groups.Clear();
		string filter = _searchFilter?.Trim() ?? "";
		bool useFilter = filter.Length > 0;
		foreach (var m in _metrics)
		{
			if (m.AvgMs < 0.005 && m.CallsPerSec == 0) continue;
			// Фича 4: фильтр применяется и к живой таблице, не только к отчёту.
			if (useFilter && !m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

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
		// Баланс параллельных фаз: wall vs cpu, дисбаланс = простой ядер (straggler).
		GameProfiler.SnapshotBalance(out var balance);
		if (balance.Length > 0)
		{
			sb.Append("[color=#c678dd][b]BALANCE[/b] (wall/cpu/простой):[/color]\n");
			foreach (var b in balance)
			{
				string imb = b.ImbalancePct >= 40 ? $"[color=#e06c75][b]{b.ImbalancePct}%[/b][/color]"
					: b.ImbalancePct >= 15 ? $"[color=#e5c07b]{b.ImbalancePct}%[/color]"
					: $"[color=#98c379]{b.ImbalancePct}%[/color]";
				string tail = b.MaxBatchMs >= 3.0 ? $"[color=#e06c75]{b.MaxBatchMs,5:F2}ms[/color]"
					: b.MaxBatchMs >= 1.5 ? $"[color=#e5c07b]{b.MaxBatchMs,5:F2}ms[/color]"
					: $"[color=#98c379]{b.MaxBatchMs,5:F2}ms[/color]";
				sb.Append($"  {FormatMethodName(b.Name, 30)} wall {b.WallMs,6:F2}ms cpu {b.CpuMs,7:F2}ms idle {imb} max {tail} strag {b.StragglerCount}\n");
			}
		}
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
		if (_lastCopyMs > 0.5)
			sb.Append($"\n[color=#5c6370]copy: {_lastCopyMs:F0}ms (было 20-30с до фикса среза)[/color]\n");

		return sb.ToString();
	}

	// --- Фича 12: вкладка Топ-10 за окно (агрегация окна, не живой снапшот) ---
	private string BuildTopText()
	{
		var log = ProfilerLogService.Instance;
		var top = log.GetTopMethods(_topWindowSecs, 10);
		var sb = new StringBuilder(1024);
		sb.Append($"[b][color=#61afef]=== ТОП-10 за {_topWindowSecs}с ===[/color][/b]\n");
		if (top.Count == 0) { sb.Append("[color=#5c6370]  пока пусто…[/color]\n"); return sb.ToString(); }
		double grand = 0; foreach (var t in top) grand += t.TotalMs;
		for (int i = 0; i < top.Count; i++)
		{
			double share = grand > 0 ? top[i].TotalMs / grand * 100.0 : 0;
			sb.Append($"{i + 1,2}. {FormatMethodName(top[i].Name, 36)} Σ {top[i].TotalMs,7:F1}ms пик {top[i].MaxMs,6:F2}ms {share,5:F1}%\n");
		}
		return sb.ToString();
	}

	// --- Фича 13: вкладка Спайки (пики выше порога, последние 30) ---
	private string BuildSpikesText()
	{
		var log = ProfilerLogService.Instance;
		var spikes = log.GetSpikes();
		var sb = new StringBuilder(1024);
		sb.Append($"[b][color=#e06c75]=== СПАЙКИ ≥{_spikeMinMs:F0}ms ({spikes.Count}) ===[/color][/b]\n");
		if (spikes.Count == 0) { sb.Append("[color=#5c6370]  спайков нет — всё ровно[/color]\n"); return sb.ToString(); }
		int s0 = Math.Max(0, spikes.Count - 30);
		for (int i = spikes.Count - 1; i >= s0; i--)
			sb.Append($"  {FormatMethodName(spikes[i].Method, 40)} пик {spikes[i].PeakMs,6:F2}ms\n");
		return sb.ToString();
	}

	// --- Фича 14: вкладка Метки/Заметки/Нажатия ---
	private string BuildMarksText()
	{
		var log = ProfilerLogService.Instance;
		var sb = new StringBuilder(1024);
		sb.Append("[b][color=#e5c07b]=== МЕТКИ / ЗАМЕТКИ ===[/color][/b]\n");
		var marks = log.GetMarkers();
		var notes = log.GetNotes();
		if (marks.Count == 0 && notes.Count == 0)
			sb.Append("[color=#5c6370]  жми «Метка» в интересный момент[/color]\n");
		foreach (var (at, text) in marks)
			sb.Append($"  [M] {text}\n");
		foreach (var (at, text) in notes)
			sb.Append($"  [N] {text}\n");
		sb.Append($"[color=#5c6370]сводка: {log.BuildSummaryLine()}[/color]\n");
		return sb.ToString();
	}

	// --- Фича 15: вкладка График — символьный ряд нагрузки окна (60 точек) ---
	private string BuildGraphText()
	{
		var log = ProfilerLogService.Instance;
		var series = log.GetLoadSeries(60);
		var sb = new StringBuilder(1024);
		sb.Append("[b][color=#98c379]=== НАГРУЗКА ОКНА (мс/с) ===[/color][/b]\n");
		if (series.Length == 0) { sb.Append("[color=#5c6370]  пока пусто…[/color]\n"); return sb.ToString(); }
		float max = 1f;
		foreach (float v in series) if (v > max) max = v;
		const string bar = " .:-=+*#%@";
		foreach (float v in series)
		{
			int level = (int)(v / max * (bar.Length - 1));
			sb.Append(bar[Math.Clamp(level, 0, bar.Length - 1)]);
		}
		sb.Append($"\n[color=#5c6370]max {max:F1}ms/s, точек {series.Length}[/color]\n");
		// Фича 16: дифф двух последних секунд — что скакнуло.
		if (log.SecondCount >= 2)
		{
			var diff = log.DiffSeconds(log.SecondCount - 2, log.SecondCount - 1, 5);
			sb.Append("[color=#c678dd]--- скачок за последнюю сек ---[/color]\n");
			foreach (var (name, d) in diff)
				sb.Append($"  {FormatMethodName(name, 36)} {d:+0.0;-0.0}ms\n");
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

	/// <summary>
	/// Кнопка с автозаписью в лог: каждое нажатие панели падает в CSV как
	/// [button] «панель: имя», а вкл/выкл-обработчики дописывают [func].
	/// Обычный MakeButton оставлен для мест, где запись не нужна.
	/// </summary>
	private static Button MakeTrackedButton(string text, string panel, Action onClick)
	{
		var button = new Button
		{
			Text = text,
			FocusMode = Control.FocusModeEnum.None,
			CustomMinimumSize = new Vector2(0, 26)
		};
		button.Pressed += () =>
		{
			ProfilerLogService.Instance.RecordButton(text, panel);
			onClick();
		};
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
