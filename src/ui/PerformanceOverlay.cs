using Godot;
using Game.Core;
using Game.Simulation;
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
	private ScrollContainer _scroll;
	private RichTextLabel _label;
	private Label _copyStatus;
	private Button _pauseButton;
	private bool _isVisible = false;
	private float _refreshTimer;
	private float _trendTimer;
	private float _copyStatusTimer;

	// Окно снапшота
	private double _sinceSnapshot;
	private int _framesSinceSnapshot;
	private double _lastWindowSec = 0.1;
	private int _lastFrames = 6;

	private GameProfiler.MetricSnapshot[] _metrics = Array.Empty<GameProfiler.MetricSnapshot>();
	private readonly List<ScriptGroup> _groups = new();
	private int _pageIndex;

	// Кольцевые буферы трендов (FPS и % нагрузки)
	private readonly float[] _fpsHistory = new float[TrendLength];
	private readonly float[] _loadHistory = new float[TrendLength];
	private int _trendIndex;
	private int _trendFilled;

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

	// Перетаскивание (Drag & Drop)
	private bool _isDragging = false;
	private Vector2 _dragOffset = Vector2.Zero;

	// Режим Mini-HUD
	private bool _isMiniMode = false;
	private Button _miniButton;
	private HBoxContainer _tabsRow;
	private HBoxContainer _toolsRow;
	private HBoxContainer _marksRow;

	// Автопауза при спайке
	private bool _freezeOnSpike = false;
	private Button _freezeButton;

	private Button _fpsButton;
	private Button _simBackendButton;
	private int _fpsPresetIndex = 0;
	private static readonly (int MaxFps, DisplayServer.VSyncMode Vsync, string Label)[] FpsPresets = new[]
	{
		(1000, DisplayServer.VSyncMode.Disabled, "FPS: 1000"),
		(60, DisplayServer.VSyncMode.Enabled, "FPS: VSync 60"),
		(144, DisplayServer.VSyncMode.Disabled, "FPS: 144"),
		(240, DisplayServer.VSyncMode.Disabled, "FPS: 240")
	};

	public override void _Ready()
	{
		Layer = 120;

		_panel = new PanelContainer
		{
			Name = "ProfilerPanel",
			Position = new Vector2(16, 16),
			CustomMinimumSize = new Vector2(780, 430),
			MouseFilter = Control.MouseFilterEnum.Stop // клики внутри панели не пробивают в мир
		};
		_panel.AddThemeStyleboxOverride("panel", MakePanelStyle());

		// Разблокируем до 1000 FPS и отключаем VSync
		Engine.MaxFps = 1000;
		DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

		var root = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
		root.AddThemeConstantOverride("separation", 6);
		_panel.AddChild(root);

		// Ряд 1: навигация, управление, перетаскивание, копирование
		var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
		header.AddThemeConstantOverride("separation", 4);
		root.AddChild(header);

		header.AddChild(MakeTrackedButton("<", "пагинация", () => _pageIndex = Math.Max(0, _pageIndex - 1)));
		header.AddChild(MakeTrackedButton(">", "пагинация", () => _pageIndex = Math.Min(_groups.Count - 1, _pageIndex + 1)));

		_fpsButton = MakeTrackedButton(FpsPresets[_fpsPresetIndex].Label, "fps_limit", () =>
		{
			_fpsPresetIndex = (_fpsPresetIndex + 1) % FpsPresets.Length;
			var preset = FpsPresets[_fpsPresetIndex];
			Engine.MaxFps = preset.MaxFps;
			DisplayServer.WindowSetVsyncMode(preset.Vsync);
			_fpsButton.Text = preset.Label;
			ProfilerLogService.Instance.RecordFunction("FPS Limit", preset.Label);
		});
		header.AddChild(_fpsButton);

		_simBackendButton = MakeTrackedButton(AgentSimulationThread.SimBackendLabel, "sim_backend", () =>
		{
			AgentSimulationThread.ActiveBackend = AgentSimulationThread.ActiveBackend == AgentSimulationThread.SimulationBackend.CpuSimd
				? AgentSimulationThread.SimulationBackend.GpuCompute
				: AgentSimulationThread.SimulationBackend.CpuSimd;
			_simBackendButton.Text = AgentSimulationThread.SimBackendLabel;
			ProfilerLogService.Instance.RecordFunction("Simulation Backend", AgentSimulationThread.SimBackendLabel);
		});
		header.AddChild(_simBackendButton);

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

		// Кнопка [Copy AI] — главный инструмент экспорта для AI
		var copyAiBtn = MakeTrackedButton("Copy AI", "шапка", () =>
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var ctx = GatherAiContext();
			string report = ProfilerLogService.Instance.BuildAiReport(ctx);
			DisplayServer.ClipboardSet(report);
			sw.Stop();
			_lastCopyMs = sw.Elapsed.TotalMilliseconds;
			_copyStatus.Text = $"AI report copied ({_lastCopyMs:F0}ms)";
			_copyStatusTimer = 2.5f;
		});
		copyAiBtn.Modulate = new Color(0.4f, 1.0f, 0.6f);
		header.AddChild(copyAiBtn);

		// Кнопка Copy — обычный текстовый срез
		header.AddChild(MakeTrackedButton("Copy", "шапка", () =>
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			DisplayServer.ClipboardSet(ProfilerLogService.Instance.BuildReport());
			sw.Stop();
			_lastCopyMs = sw.Elapsed.TotalMilliseconds;
			_copyStatus.Text = $"copied {_lastCopyMs:F0}ms";
			_copyStatusTimer = 2.5f;
		}));

		// Кнопка CSV
		header.AddChild(MakeTrackedButton("CSV", "шапка", () =>
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			DisplayServer.ClipboardSet(ProfilerLogService.Instance.BuildCsv());
			sw.Stop();
			_copyStatus.Text = $"csv {sw.Elapsed.TotalMilliseconds:F0}ms";
			_copyStatusTimer = 2.5f;
		}));

		// Кнопка Save — сохранение в user://logs/
		header.AddChild(MakeTrackedButton("Save", "шапка", () =>
		{
			try
			{
				string dir = ProjectSettings.GlobalizePath("user://logs");
				string path = System.IO.Path.Combine(dir, $"profiler_ai_{DateTime.Now:yyyyMMdd_HHmmss}.md");
				ProfilerLogService.Instance.SaveAiReportToFile(path, GatherAiContext());
				_copyStatus.Text = "saved .md";
				_copyStatusTimer = 2.5f;
			}
			catch (Exception ex)
			{
				_copyStatus.Text = "save err";
				_copyStatusTimer = 2.5f;
				GD.PrintErr(ex.Message);
			}
		}));

		// Кнопка Mini-HUD
		_miniButton = MakeTrackedButton("Mini", "шапка", ToggleMiniMode);
		header.AddChild(_miniButton);

		_copyStatus = new Label
		{
			Text = "",
			Modulate = new Color(0.6f, 0.95f, 0.6f),
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		header.AddChild(_copyStatus);

		// Ряд 2: вкладки
		_tabsRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
		_tabsRow.AddThemeConstantOverride("separation", 4);
		root.AddChild(_tabsRow);
		for (int t = 0; t < TabNames.Length; t++)
		{
			int idx = t;
			_tabsRow.AddChild(MakeTrackedButton(TabNames[t], "вкладки", () => _tabIndex = idx));
		}
		_tabLabel = new Label { Text = "", MouseFilter = Control.MouseFilterEnum.Ignore };
		_tabsRow.AddChild(_tabLabel);

		// Ряд 3: поиск + фильтры + Freeze on Spike
		_toolsRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
		_toolsRow.AddThemeConstantOverride("separation", 4);
		root.AddChild(_toolsRow);

		_searchBox = new LineEdit { PlaceholderText = "фильтр…", CustomMinimumSize = new Vector2(140, 26) };
		_searchBox.TextChanged += text => { _searchFilter = text ?? ""; _pageIndex = 0; };
		_toolsRow.AddChild(_searchBox);

		_toolsRow.AddChild(MakeTrackedButton("10s", "окно топа", () => _topWindowSecs = 10));
		_toolsRow.AddChild(MakeTrackedButton("30s", "окно топа", () => _topWindowSecs = 30));
		_toolsRow.AddChild(MakeTrackedButton("60s", "окно топа", () => _topWindowSecs = 60));

		_toolsRow.AddChild(MakeTrackedButton("спайк+", "порог", () =>
		{
			_spikeMinMs = Math.Min(100f, _spikeMinMs + 2f);
			ProfilerLogService.Instance.SpikeThresholdMs = _spikeMinMs;
		}));
		_toolsRow.AddChild(MakeTrackedButton("спайк-", "порог", () =>
		{
			_spikeMinMs = Math.Max(1f, _spikeMinMs - 2f);
			ProfilerLogService.Instance.SpikeThresholdMs = _spikeMinMs;
		}));

		_freezeButton = MakeTrackedButton("Freeze: OFF", "шапка", () =>
		{
			_freezeOnSpike = !_freezeOnSpike;
			_freezeButton.Text = _freezeOnSpike ? "Freeze: ON" : "Freeze: OFF";
			ProfilerLogService.Instance.RecordFunction("Freeze on Spike", _freezeOnSpike);
		});
		_toolsRow.AddChild(_freezeButton);

		// Ряд 4: метки + заметки
		_marksRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
		_marksRow.AddThemeConstantOverride("separation", 4);
		root.AddChild(_marksRow);

		_marksRow.AddChild(MakeTrackedButton("Метка", "метки", () =>
		{
			ProfilerLogService.Instance.AddMarker($"метка #{ProfilerLogService.Instance.MarkerCount + 1}");
			_copyStatus.Text = "метка+";
			_copyStatusTimer = 2.0f;
		}));

		_noteBox = new LineEdit { PlaceholderText = "заметка…", CustomMinimumSize = new Vector2(180, 26) };
		_marksRow.AddChild(_noteBox);
		_marksRow.AddChild(MakeTrackedButton("Заметка+", "метки", () =>
		{
			ProfilerLogService.Instance.AddNote(_noteBox.Text);
			_noteBox.Text = "";
			_copyStatus.Text = "заметка+";
			_copyStatusTimer = 2.0f;
		}));

		_csvFilterBox = new LineEdit { PlaceholderText = "csv-фильтр…", CustomMinimumSize = new Vector2(120, 26) };
		_csvFilterBox.TextChanged += text => ProfilerLogService.Instance.CsvEventFilter = text;
		_marksRow.AddChild(_csvFilterBox);

		_marksRow.AddChild(MakeTrackedButton("Чист.метки", "метки", () => ProfilerLogService.Instance.ClearMarkers()));
		_marksRow.AddChild(MakeTrackedButton("Отчёт 30с", "глубина", () =>
		{
			ProfilerLogService.Instance.ReportSeconds = 30;
			ProfilerLogService.Instance.RecordFunction("Глубина отчёта", "30с");
		}));
		_marksRow.AddChild(MakeTrackedButton("Отчёт 120с", "глубина", () =>
		{
			ProfilerLogService.Instance.ReportSeconds = 120;
			ProfilerLogService.Instance.RecordFunction("Глубина отчёта", "120с");
		}));

		// Скролл-контейнер для таблицы методов (предотвращает обрезание)
		_scroll = new ScrollContainer
		{
			Name = "ProfilerScroll",
			CustomMinimumSize = new Vector2(760, 290),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			MouseFilter = Control.MouseFilterEnum.Pass
		};
		root.AddChild(_scroll);

		_label = new RichTextLabel
		{
			BbcodeEnabled = true,
			FitContent = true,
			AutowrapMode = TextServer.AutowrapMode.Off,
			CustomMinimumSize = new Vector2(740, 0),
			MouseFilter = Control.MouseFilterEnum.Pass
		};
		_scroll.AddChild(_label);

		AddChild(_panel);
		_panel.Visible = _isVisible;
	}

	private void ToggleMiniMode()
	{
		_isMiniMode = !_isMiniMode;
		_miniButton.Text = _isMiniMode ? "Full" : "Mini";
		_tabsRow.Visible = !_isMiniMode;
		_toolsRow.Visible = !_isMiniMode;
		_marksRow.Visible = !_isMiniMode;
		_scroll.Visible = !_isMiniMode;
		_panel.CustomMinimumSize = _isMiniMode ? new Vector2(500, 48) : new Vector2(780, 430);
	}

	public override void _Input(InputEvent @event)
	{
		// Переключение видимости по F3
		if (@event is InputEventKey key && key.Pressed && !key.Echo && (key.Keycode == Key.F3 || key.PhysicalKeycode == Key.F3))
		{
			_isVisible = !_isVisible;
			_panel.Visible = _isVisible;
			GetViewport().SetInputAsHandled();
			return;
		}

		// Перетаскивание панели мышью за шапку
		if (@event is InputEventMouseButton mb)
		{
			if (mb.ButtonIndex == MouseButton.Left)
			{
				if (mb.Pressed && _isVisible && _panel.GetGlobalRect().HasPoint(mb.Position))
				{
					// Если клик в верхней части панели (первые 36px) — начинаем перетаскивание
					if (mb.Position.Y <= _panel.Position.Y + 36)
					{
						_isDragging = true;
						_dragOffset = mb.Position - _panel.Position;
					}
				}
				else if (!mb.Pressed)
				{
					_isDragging = false;
				}
			}
		}
		else if (@event is InputEventMouseMotion mm && _isDragging)
		{
			_panel.Position = mm.Position - _dragOffset;
			return;
		}

		// Фильтрация шума: не логируем нажатия, если фокус в поле ввода
		var focus = GetViewport()?.GuiGetFocusOwner();
		if (focus is LineEdit or TextEdit)
			return;

		// Не логируем клики мыши внутри панели оверлея как клики по миру
		if (@event is InputEventMouseButton mouse)
		{
			if (_isVisible && _panel.GetGlobalRect().HasPoint(mouse.Position))
				return;

			if (mouse.Pressed)
				ProfilerLogService.Instance.RecordInput("mouse", $"Mouse {mouse.ButtonIndex} @ ({mouse.Position.X:0},{mouse.Position.Y:0})");
			return;
		}

		// Клавиатура: убираем шум повторов (Echo)
		if (@event is InputEventKey keyLog && keyLog.Pressed && !keyLog.Echo)
		{
			ProfilerLogService.Instance.RecordInput("key", $"Key {keyLog.Keycode}");
		}
	}

	public override void _Process(double delta)
	{
		// Плавное затухание статуса копирования
		if (_copyStatusTimer > 0f)
		{
			_copyStatusTimer -= (float)delta;
			if (_copyStatusTimer <= 0f)
				_copyStatus.Text = "";
		}

		_refreshTimer += (float)delta;
		_sinceSnapshot += delta;
		_framesSinceSnapshot++;
		if (_refreshTimer < 0.10f)
			return;
		_refreshTimer = 0f;

		double windowSec = _sinceSnapshot > 0.0 ? _sinceSnapshot : 0.1;
		int frames = _framesSinceSnapshot;
		_sinceSnapshot = 0.0;
		_framesSinceSnapshot = 0;
		_lastWindowSec = windowSec;
		_lastFrames = frames;

		GameProfiler.SnapshotMetrics(out _metrics, windowSec, frames);
		ProfilerLogService.Instance.Accumulate(_metrics, windowSec, frames);

		// Автопауза при обнаружении спайка
		if (_freezeOnSpike)
		{
			foreach (var m in _metrics)
			{
				if (m.MaxMs >= _spikeMinMs)
				{
					_freezeOnSpike = false;
					_freezeButton.Text = "Freeze: OFF";
					ProfilerLogService.Instance.AddMarker($"AUTO-FREEZE SPIKE: {m.Name} {m.MaxMs:F1}ms");
					_copyStatus.Text = $"FROZEN ON SPIKE: {m.MaxMs:F0}ms!";
					_copyStatusTimer = 5.0f;
					if (TimeManager.Instance != null && !TimeManager.Instance.IsPaused)
					{
						TimeManager.Instance.SetSpeed(GameSpeed.Paused);
						ProfilerLogService.Instance.RecordFunction("Simulation Auto-Freeze", "Spike detected");
					}
					break;
				}
			}
		}

		// Тренды: 1 точка в секунду
		_trendTimer += (float)delta;
		if (_trendTimer >= 1.0f)
		{
			_trendTimer = 0f;
			double totalMsPerFrame = 0.0;
			foreach (var m in _metrics) totalMsPerFrame += m.MsPerFrame;

			double currentBudgetMs = 1000.0 / Math.Max(1.0, Engine.GetFramesPerSecond());
			_fpsHistory[_trendIndex] = (float)Engine.GetFramesPerSecond();
			_loadHistory[_trendIndex] = (float)(totalMsPerFrame / currentBudgetMs * 100.0);
			_trendIndex = (_trendIndex + 1) % TrendLength;
			_trendFilled = Math.Min(_trendFilled + 1, TrendLength);
		}

		if (!_isVisible)
			return;

		if (_isMiniMode)
		{
			double fps = Engine.GetFramesPerSecond();
			long ram = GC.GetTotalMemory(false) / (1024 * 1024);
			long dc = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
			GameProfiler.MetricSnapshot simCycle = default;
			foreach (var m in _metrics) if (m.Name == "Simulation.TotalStepCycle") { simCycle = m; break; }
			_copyStatus.Text = $"FPS: {fps:F0} | Sim: {simCycle.AvgMs:F2}ms | RAM: {ram}MB | DC: {dc}";
			return;
		}

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

	private AiReportContext GatherAiContext()
	{
		double fps = Engine.GetFramesPerSecond();
		double frameTimeMs = 1000.0 / Math.Max(1.0, fps);
		long ramMb = GC.GetTotalMemory(false) / (1024 * 1024);

		long drawCalls = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
		long objects = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
		long prims = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
		double vramMb = (double)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0);
		double processTimeMs = (double)Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
		double physicsTimeMs = (double)Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;

		GameProfiler.MetricSnapshot simCycle = default;
		GameProfiler.MetricSnapshot p2Metric = default;
		GameProfiler.MetricSnapshot p3aMetric = default;
		GameProfiler.MetricSnapshot p3bMetric = default;
		GameProfiler.MetricSnapshot snapMetric = default;
		foreach (var m in _metrics)
		{
			if (m.Name == "Simulation.TotalStepCycle") simCycle = m;
			else if (m.Name.Contains("Phase2")) p2Metric = m;
			else if (m.Name.Contains("Phase3a")) p3aMetric = m;
			else if (m.Name.Contains("Phase3b")) p3bMetric = m;
			else if (m.Name.Contains("Snapshots")) snapMetric = m;
		}

		GameProfiler.SnapshotBalance(out var balance);

		float minFps = 1000f;
		int filled = Math.Max(1, _trendFilled);
		for (int i = 0; i < filled; i++)
		{
			float f = _fpsHistory[i];
			if (f > 0 && f < minFps) minFps = f;
		}

		var cam = GetViewport()?.GetCamera2D();
		string camPos = cam != null ? $"({cam.Position.X:0}, {cam.Position.Y:0})" : "(0, 0)";
		string camZoom = cam != null ? $"{cam.Zoom.X:F2}x" : "1.00x";

		int day = TimeManager.Instance != null ? TimeManager.Instance.DayNumber : 1;
		float timeOfDay = TimeManager.Instance != null ? TimeManager.Instance.TimeOfDaySeconds : 0f;
		int hour = (int)(timeOfDay / 3600f);
		int minute = (int)((timeOfDay % 3600f) / 60f);

		int activeJobs = JobBroker.Instance != null ? JobBroker.Instance.ActiveBlueprintCount : 0;
		int groundItems = GroundItemManager.Instance != null ? GroundItemManager.Instance.TotalGroundPilesCount : 0;

		int gc0 = GC.CollectionCount(0);
		int gc1 = GC.CollectionCount(1);
		int gc2 = GC.CollectionCount(2);
		double gcPause = GC.GetTotalPauseDuration().TotalMilliseconds;

		return new AiReportContext
		{
			OsDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
			CpuCores = System.Environment.ProcessorCount,
			EngineMaxFps = Engine.MaxFps,
			Fps = fps,
			FrameTimeMs = frameTimeMs,
			FpsMin = minFps < 999f ? minFps : fps,
			Fps1PercentLow = Math.Max(1.0, (minFps < 999f ? minFps : fps) * 0.95),
			ProcessMs = processTimeMs,
			PhysicsMs = physicsTimeMs,
			DrawCalls = drawCalls,
			RenderObjects = objects,
			Primitives = prims,
			VramMb = vramMb,
			RamMb = ramMb,

			GcGen0Count = gc0,
			GcGen1Count = gc1,
			GcGen2Count = gc2,
			GcTotalPauseMs = gcPause,

			GameDay = day,
			GameHour = hour,
			GameMinute = minute,
			CameraPos = camPos,
			CameraZoom = camZoom,
			ActiveJobsCount = activeJobs,
			GroundPilesCount = groundItems,

			AgentCount = 10000,
			SimBackend = AgentSimulationThread.SimBackendLabel,
			SimSpeed = 1.0,
			SimCycleAvgMs = simCycle.AvgMs,
			SimCycleMaxMs = simCycle.MaxMs,
			SimStepsPerSec = simCycle.CallsPerSec,
			SimMsPerFrame = simCycle.MsPerFrame,
			Phase2AvgMs = p2Metric.AvgMs,
			Phase3aAvgMs = p3aMetric.AvgMs,
			Phase3bAvgMs = p3bMetric.AvgMs,
			SnapshotAvgMs = snapMetric.AvgMs,
			Balance = balance
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
			if (m.TotalMs < 0.005 && m.Calls == 0) continue;
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
			group.TotalMs += m.MsPerFrame;
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

		long drawCalls = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
		long objects = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
		long prims = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
		double vramMb = (double)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0);
		double processTimeMs = (double)Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
		double physicsTimeMs = (double)Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
		double currentBudgetMs = 1000.0 / Math.Max(1.0, fps);

		GameProfiler.MetricSnapshot simCycle = default;
		GameProfiler.MetricSnapshot p2Metric = default;
		GameProfiler.MetricSnapshot p3aMetric = default;
		GameProfiler.MetricSnapshot p3bMetric = default;
		GameProfiler.MetricSnapshot snapMetric = default;
		foreach (var m in _metrics)
		{
			if (m.Name == "Simulation.TotalStepCycle") simCycle = m;
			else if (m.Name.Contains("Phase2")) p2Metric = m;
			else if (m.Name.Contains("Phase3a")) p3aMetric = m;
			else if (m.Name.Contains("Phase3b")) p3bMetric = m;
			else if (m.Name.Contains("Snapshots")) snapMetric = m;
		}

		var sb = new StringBuilder(2048);
		sb.Append("[b][color=#61afef]=== SCRIPT & GPU PERFORMANCE PROFILER (F3) ===[/color][/b]\n");
		sb.Append($"[b][color=#98c379]● MAIN THREAD (RENDER):[/color][/b] " +
				  $"[b]FPS:[/b] {FormatFps(fps)} ({frameTimeMs,4:F1}ms) [color=#5c6370][Cap: {Engine.MaxFps}][/color]  |  " +
				  $"[b]Process:[/b] {processTimeMs,4:F2}ms  |  [b]Physics:[/b] {physicsTimeMs,4:F2}ms\n");
		sb.Append($"  [color=#98c379]DrawCalls:[/color] {drawCalls}  |  " +
				  $"[color=#61afef]Objects (CanvasItems):[/color] {objects:N0}  |  " +
				  $"[color=#e5c07b]Primitives:[/color] {prims:N0}  |  " +
				  $"[b]VRAM:[/b] {vramMb:F1} MB  |  [b]RAM:[/b] {ramMb} MB\n");

		if (simCycle.Calls > 0 || simCycle.CallsPerSec > 0)
		{
			string avgCol = simCycle.AvgMs <= 1.0 ? "#98c379" : simCycle.AvgMs <= 1.5 ? "#e5c07b" : "#e06c75";
			string peakCol = simCycle.MaxMs <= 1.5 ? "#98c379" : simCycle.MaxMs <= 3.0 ? "#e5c07b" : "#e06c75";
			sb.Append($"[b][color=#e5c07b]● SIMULATION THREAD (C# BACKGROUND):[/color][/b] " +
					  $"[b]1-Step (1 tick):[/b] [color={avgCol}][b]{simCycle.AvgMs:F3}ms[/b][/color]  |  " +
					  $"[b]Peak Max:[/b] [color={peakCol}]{simCycle.MaxMs:F2}ms[/color]  |  " +
					  $"[b]Steps/sec:[/b] {simCycle.CallsPerSec:N0}  |  " +
					  $"[b]Sim/Frame:[/b] {simCycle.MsPerFrame:F2}ms\n");
			sb.Append($"  [color=#abb2bf]Фазы шага: " +
					  $"P2(AI/Движ): {p2Metric.AvgMs:F3}ms  |  " +
					  $"P3a(Нужды): {p3aMetric.AvgMs:F3}ms  |  " +
					  $"P3b(Ресурсы): {p3bMetric.AvgMs:F3}ms  |  " +
					  $"Снапшот: {snapMetric.AvgMs:F3}ms[/color]\n");
		}
		else
		{
			sb.Append("[b][color=#e5c07b]● SIMULATION THREAD (C# BACKGROUND):[/color][/b] [color=#5c6370]Симуляция ожидает или на паузе[/color]\n");
		}

		if (log.IsPaused)
			sb.Append("  [b][color=#e06c75]PAUSED[/color][/b]\n");

		// Символьные тренды
		float maxFps = MaxOf(_fpsHistory, _trendFilled);
		sb.Append($"[color=#98c379]FPS  :[/color] {RenderTrend(_fpsHistory, maxFps)}  [color=#5c6370](max {maxFps:F0})[/color]\n");
		sb.Append($"[color=#e5c07b]LOAD :[/color] {RenderTrend(_loadHistory, 100f)}  [color=#5c6370](% бюджета кадра)[/color]\n");

		// Баланс параллельных фаз
		GameProfiler.SnapshotBalance(out var balance);
		if (balance.Length > 0)
		{
			sb.Append("[color=#c678dd][b]BALANCE[/b] (wall/cpu на кадр; idle — простой ядер):[/color]\n");
			foreach (var b in balance)
			{
				string imb = b.ImbalancePct >= 40 ? $"[color=#e06c75][b]{b.ImbalancePct}%[/b][/color]"
					: b.ImbalancePct >= 15 ? $"[color=#e5c07b]{b.ImbalancePct}%[/color]"
					: $"[color=#98c379]{b.ImbalancePct}%[/color]";
				string tail = b.MaxBatchMs >= 3.0 ? $"[color=#e06c75]{b.MaxBatchMs,5:F2}ms[/color]"
					: b.MaxBatchMs >= 1.5 ? $"[color=#e5c07b]{b.MaxBatchMs,5:F2}ms[/color]"
					: $"[color=#98c379]{b.MaxBatchMs,5:F2}ms[/color]";
				sb.Append($"  {FormatMethodName(b.Name, 30)} W {b.WallMsPerFrame,6:F2} C {b.CpuMsPerFrame,6:F2} мс/кадр idle {imb} max {tail} strag {b.StragglerCount}\n");
			}
		}
		sb.Append("[color=#3e4451]--------------------------------------------------------------------------------[/color]\n");

		if (_groups.Count == 0)
		{
			sb.Append("[color=#5c6370]  Нет активных измерений нагрузки...[/color]\n");
			return sb.ToString();
		}

		var group = _groups[_pageIndex];
		sb.Append($"[b][color=#61afef]=== {group.Script}[/color][/b]  [color=#5c6370](стр. {_pageIndex + 1}/{_groups.Count} | методов: {group.Methods.Count} | Σ {group.TotalMs:F2} мс/кадр)[/color]\n");
		sb.Append("[color=#abb2bf][b]МЕТОД                            мс/кадр  %кадра   выз/с    avg/выз   max/выз   load[/b][/color]\n");

		foreach (var m in group.Methods)
		{
			if (m.TotalMs < 0.005 && m.Calls == 0) continue;

			string name = FormatMethodName(m.Name, 31);
			double framePct = m.MsPerFrame / currentBudgetMs * 100.0;
			sb.Append($"{name}  {FormatMs(m.MsPerFrame, 7)}  {FormatLoad(framePct, 5)}  {FormatCalls(m.CallsPerSec, 7)}  {FormatMs(m.AvgMs, 8)}  {FormatMs(m.MaxMs, 8)}  {FormatLoad(m.PercentLoad, 4)}\n");
		}
		sb.Append("\n");

		// Легенда-пагинация
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
		if (log.SecondCount >= 2)
		{
			var diff = log.DiffSeconds(log.SecondCount - 2, log.SecondCount - 1, 5);
			sb.Append("[color=#c678dd]--- скачок за последнюю сек ---[/color]\n");
			foreach (var (name, d) in diff)
				sb.Append($"  {FormatMethodName(name, 36)} {d:+0.0;-0.0}ms\n");
		}
		return sb.ToString();
	}

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
