using Godot;
using Game.Core;
using Game.Simulation;
using System;
using System.Collections.Generic;

namespace Game.UI;

public partial class WorkZoneController : Control
{
    public static WorkZoneController Instance { get; private set; }

    private Control _rootPanel;
    private Label _titleLabel;
    private Label _infoLabel;
    private BaseButton _escapeButton;

    private HSlider _tilesSlider;
    private Label _tilesNumber;
    private HSlider _workerSlider;
    private Label _workerNumber;
    private HSlider _prioritySlider;
    private Label _priorityNumber;
    private BaseButton _farmBackButton;

    /// <summary>
    /// Id фермы, с которой перешли в JobDisplay. −1 = пришли не с фермы,
    /// кнопка ButtonForFarm скрыта (§20.6).
    /// </summary>
    public static int CameFromFarmZoneId = -1;

    /// <summary>Открыт ли JobDisplay сейчас для фермы (а не для Work-зоны).</summary>
    private bool _farmMode;

    private bool _updating;
    private long _lastTilesMs;
    private int _pendingTiles = -1;
    private string _lastInfo = "";
    private double _infoTimer;

    private const long TilesThrottleMs = 150;

    public event Action OnOpened;
    public event Action OnClosed;

    public bool IsOpen => _rootPanel != null && _rootPanel.Visible;

    public override void _Ready()
    {
        Instance = this;
        ZIndex = 20;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        LoadScene();

        WorkZoneManager.Instance.OnZoneSelected += OnZoneSelectedFacade;
        WorkZoneManager.Instance.OnZoneDeselected += Close;
        WorkZoneManager.Instance.OnZonesUpdated += RefreshInfo;
    }

    // Единый ZoneManager шлёт WorkZone через фасад; фильтр Kind — страховка:
    // фасад уже отдаёт только Work, но если прилетит Farm/null — игнорим,
    // окно открывает только FarmController.
    private void OnZoneSelectedFacade(WorkZone zone)
    {
        if (zone == null) return;
        OnZoneSelected(zone);
    }

    private void LoadScene()
    {
        var paths = new[]
        {
            "res://scenes/ui/JobDisplay.tscn",
            "res://JobDisplay.tscn",
            "res://ui/JobDisplay.tscn"
        };

        foreach (var path in paths)
        {
            if (ResourceLoader.Exists(path))
            {
                var packed = ResourceLoader.Load<PackedScene>(path);
                if (packed != null)
                {
                    _rootPanel = packed.Instantiate<Control>();
                    AddChild(_rootPanel);
                    _rootPanel.Visible = false;

                    ConfigureInputBlocking(_rootPanel);

                    _titleLabel = _rootPanel.FindChild("NameWork", true, false) as Label;
                    _infoLabel = _rootPanel.FindChild("Information", true, false) as Label;

                    var tilesRow = _rootPanel.FindChild("Tiles", true, false) as Control;
                    var workerRow = _rootPanel.FindChild("Worker", true, false) as Control;
                    var priorityRow = _rootPanel.FindChild("Prioritet", true, false) as Control;

                    if (tilesRow != null)
                    {
                        _tilesSlider = tilesRow.FindChild("HSlider", true, false) as HSlider;
                        _tilesNumber = tilesRow.FindChild("Number", true, false) as Label;
                        if (_tilesSlider != null)
                        {
                            _tilesSlider.MinValue = 128;
                            _tilesSlider.MaxValue = 2048;
                            _tilesSlider.Step = 128;
                            _tilesSlider.ValueChanged += OnTilesSliderChanged;
                        }
                    }
                    if (workerRow != null)
                    {
                        _workerSlider = workerRow.FindChild("HSlider", true, false) as HSlider;
                        _workerNumber = workerRow.FindChild("Number", true, false) as Label;
                        if (_workerSlider != null)
                        {
                            _workerSlider.MinValue = 0;
                            _workerSlider.MaxValue = 64;
                            _workerSlider.Step = 1;
                            _workerSlider.ValueChanged += OnWorkerSliderChanged;
                        }
                    }
                    if (priorityRow != null)
                    {
                        _prioritySlider = priorityRow.FindChild("HSlider", true, false) as HSlider;
                        _priorityNumber = priorityRow.FindChild("Number", true, false) as Label;
                        if (_prioritySlider != null)
                        {
                            _prioritySlider.MinValue = 1;
                            _prioritySlider.MaxValue = 10;
                            _prioritySlider.Step = 1;
                            _prioritySlider.ValueChanged += OnPrioritySliderChanged;
                        }
                    }

                    _escapeButton = _rootPanel.FindChild("Escape", true, false) as BaseButton
                                    ?? _rootPanel.FindChild("Close", true, false) as BaseButton
                                    ?? _rootPanel.FindChild("Exit", true, false) as BaseButton
                                    ?? _rootPanel.FindChild("Back", true, false) as BaseButton;

                    if (_escapeButton != null)
                    {
                        _escapeButton.Pressed += () => WorkZoneManager.Instance.DeselectZone();
                    }

                    _farmBackButton = _rootPanel.FindChild("ButtonForFarm", true, false) as BaseButton;
                    if (_farmBackButton != null)
                    {
                        _farmBackButton.Visible = false;
                        _farmBackButton.Pressed += OnFarmBackPressed;
                    }
                    break;
                }
            }
        }
    }

    private static void ConfigureInputBlocking(Control node)
    {
        if (node == null) return;

        if (node is Panel || node is PanelContainer || node is ScrollContainer || node is ItemList)
        {
            node.MouseFilter = MouseFilterEnum.Stop;
        }

        foreach (Node child in node.GetChildren())
        {
            if (child is Control childCtrl)
            {
                ConfigureInputBlocking(childCtrl);
            }
        }
    }

    private void OnTilesSliderChanged(double value)
    {
        if (_updating) return;
        var zone = WorkZoneManager.Instance.SelectedZone;
        if (zone == null) return;
        int v = (int)value;
        if (_tilesNumber != null) _tilesNumber.Text = v.ToString();
        long now = System.Environment.TickCount64;
        if (now - _lastTilesMs < TilesThrottleMs)
        {
            _pendingTiles = v;
            return;
        }
        _lastTilesMs = now;
        _pendingTiles = -1;
        WorkZoneManager.Instance.SetTiles(zone.Id, v);
    }

    private void OnWorkerSliderChanged(double value)
    {
        if (_updating) return;
        var zone = WorkZoneManager.Instance.SelectedZone;
        if (zone == null) return;
        int v = (int)value;
        if (_workerNumber != null) _workerNumber.Text = v.ToString();
        WorkZoneManager.Instance.SetWorkers(zone.Id, v);
    }

    private void OnPrioritySliderChanged(double value)
    {
        if (_updating) return;
        int v = (int)value;
        if (_priorityNumber != null) _priorityNumber.Text = v.ToString();
        if (_farmMode)
        {
            // Режим фермы: слайдера Work-зоны нет — пишем прямо в Farming.
            JobPriorityManager.Instance.SetPriority(JobCategory.Farming, v);
            return;
        }
        var zone = WorkZoneManager.Instance.SelectedZone;
        if (zone == null) return;
        WorkZoneManager.Instance.SetPriorityOverride(zone.Id, v);
    }

    /// <summary>
    /// Открыть JobDisplay для Farm-зоны (§20.6): название + число клеток
    /// (только чтение), рабочих «∞», приоритет Farming — рабочий.
    /// Кнопка ButtonForFarm видна (назад — в ферму).
    /// </summary>
    public void ShowForFarmZone(int farmZoneId)
    {
        if (_rootPanel == null) return;
        if (!FarmZoneManager.Instance.TryGetZoneById(farmZoneId, out var farm) || farm == null)
            return;

        // Закрываем окно фермы — окна взаимоисключающие (как Farm/Work).
        var farmCtrl = FarmController.Instance;
        if (farmCtrl != null && farmCtrl.IsOpen)
            farmCtrl.Close();

        CameFromFarmZoneId = farmZoneId;
        _farmMode = true;

        _updating = true;
        try
        {
            if (_titleLabel != null)
                _titleLabel.Text = farm.Name;
            // Тайлы фермы менять через работы нельзя — только чтение.
            if (_tilesSlider != null) _tilesSlider.Editable = false;
            if (_tilesNumber != null) _tilesNumber.Text = $"{farm.TotalTiles} кл.";
            // Лимита рабочих у фермы нет — «∞».
            if (_workerSlider != null) _workerSlider.Editable = false;
            if (_workerNumber != null) _workerNumber.Text = "∞";
            int prio = JobPriorityManager.Instance.GetPriority(JobCategory.Farming);
            if (_prioritySlider != null) _prioritySlider.Value = prio;
            if (_priorityNumber != null) _priorityNumber.Text = prio.ToString();
            _pendingTiles = -1;
            _lastInfo = "";
        }
        finally
        {
            _updating = false;
        }

        if (_farmBackButton != null)
            _farmBackButton.Visible = true;

        _rootPanel.Visible = true;
        OnOpened?.Invoke();
        RefreshInfo();
    }

    /// <summary>Кнопка ButtonForFarm — назад в окно фермы (§20.6).</summary>
    private void OnFarmBackPressed()
    {
        int farmId = CameFromFarmZoneId;
        CameFromFarmZoneId = -1;
        _farmMode = false;
        if (_farmBackButton != null)
            _farmBackButton.Visible = false;
        // Разблокировать слайдеры для обычных Work-зон.
        if (_tilesSlider != null) _tilesSlider.Editable = true;
        if (_workerSlider != null) _workerSlider.Editable = true;
        Close();
        if (farmId < 0)
            return;
        // Переоткрыть окно фермы: найти любую клетку зоны и выбрать её.
        if (FarmZoneManager.Instance.TryGetZoneById(farmId, out var farm) && farm != null)
        {
            foreach (var (x, y) in farm.Tiles)
            {
                ZoneManager.Instance.SelectZoneAt(x, y);
                break;
            }
        }
    }

    private void OnZoneSelected(WorkZone zone)
    {
        if (_rootPanel == null || zone == null) return;

        // Обычный выбор Work-зоны — режим фермы сбрасываем, кнопка назад скрыта.
        _farmMode = false;
        CameFromFarmZoneId = -1;
        if (_farmBackButton != null)
            _farmBackButton.Visible = false;
        if (_tilesSlider != null) _tilesSlider.Editable = true;
        if (_workerSlider != null) _workerSlider.Editable = true;

        // Единый выбор уже в ZoneManager: чужой выбор (Farm) сюда не приходит
        // (фасад отдаёт только Work). Закрываем окно фермы если открыто.
        var garden = FarmController.Instance;
        if (garden != null && garden.IsOpen)
            garden.Close();

        _updating = true;
        try
        {
            if (_titleLabel != null)
                _titleLabel.Text = zone.Name;
            if (_tilesSlider != null) _tilesSlider.Value = zone.TilesTarget;
            if (_tilesNumber != null) _tilesNumber.Text = zone.TilesTarget.ToString();
            if (_workerSlider != null) _workerSlider.Value = zone.MaxWorkers;
            if (_workerNumber != null) _workerNumber.Text = zone.MaxWorkers.ToString();
            // T4: override ?? живой глобальный (мин по покрытым категориям).
            int prio = zone.PriorityOverride ?? GetLiveMinPriority(zone);
            if (_prioritySlider != null) _prioritySlider.Value = prio;
            if (_priorityNumber != null) _priorityNumber.Text = prio.ToString();
            _pendingTiles = -1;
            _lastTilesMs = System.Environment.TickCount64;
            _lastInfo = "";
        }
        finally
        {
            _updating = false;
        }

        _rootPanel.Visible = true;
        OnOpened?.Invoke();
        RefreshInfo();
    }

    private static int GetLiveMinPriority(WorkZone zone)
    {
        int min = int.MaxValue;
        for (int t = 1; t <= 10; t++)
        {
            if (!zone.Matches((JobTypeId)t)) continue;
            int p = JobPriorityManager.Instance.GetPriorityForJobType((JobTypeId)t);
            if (p < min) min = p;
        }
        return min == int.MaxValue ? 1 : min; // маска пуста → 1
    }

    private static Dictionary<JobCategory, int> CollectCategoryGlobals(WorkZone zone)
    {
        var dict = new Dictionary<JobCategory, int>();
        for (int t = 1; t <= 10; t++)
        {
            var typeId = (JobTypeId)t;
            if (!zone.Matches(typeId)) continue;
            var cat = JobPriorityManager.Instance.GetCategory(typeId);
            if (!dict.ContainsKey(cat))
                dict[cat] = JobPriorityManager.Instance.GetPriority(cat);
        }
        return dict;
    }

    private void RefreshInfo()
    {
        if (_infoLabel == null) return;
        // Режим фермы: показываем данные фермы (клетки + приоритет Farming).
        if (_farmMode && CameFromFarmZoneId >= 0)
        {
            if (!FarmZoneManager.Instance.TryGetZoneById(CameFromFarmZoneId, out var farm) || farm == null)
                return;
            int prio = JobPriorityManager.Instance.GetPriority(JobCategory.Farming);
            string farmText = $"Ферма: {farm.TotalTiles} кл. · приоритет Farming={prio}";
            if (farmText != _lastInfo)
            {
                _lastInfo = farmText;
                _infoLabel.Text = farmText;
            }
            return;
        }
        var zone = WorkZoneManager.Instance.SelectedZone;
        if (zone == null) return;
        int total = 0;
        var chunks = zone.ChunkIndices;
        if (chunks != null)
        {
            foreach (int c in chunks)
                total += JobDispatcher.Instance.JobIndex.GetChunkJobCount(c);
        }
        // T8: "Назначено {perPass} · Работ в чанках {total} · лимит {N/∞}".
        int perPass = System.Threading.Volatile.Read(ref zone.AssignedCount);
        string limit = zone.MaxWorkers > 0 ? zone.MaxWorkers.ToString() : "∞";
        // T4/T5: строка конфликта — покрытые категории + их текущие глобальные значения.
        var globals = CollectCategoryGlobals(zone);
        var parts = new List<string>();
        foreach (var kv in globals)
            parts.Add($"{kv.Key}={kv.Value}");
        string conflict = parts.Count > 0 ? string.Join(", ", parts) : "—";
        string text = $"Назначено {perPass} · Работ в чанках {total} · лимит {limit} (лимит {zone.MaxWorkers} / ∞)\nглобально: {conflict}";
        if (text != _lastInfo)
        {
            _lastInfo = text;
            _infoLabel.Text = text;
        }
    }

    public override void _Process(double delta)
    {
        if (_rootPanel == null || !_rootPanel.Visible) return;
        if (_farmMode)
        {
            // Режим фермы: слайдеры заблокированы, тикает только инфо-строка.
            _infoTimer += delta;
            if (_infoTimer >= 1.0)
            {
                _infoTimer = 0.0;
                RefreshInfo();
            }
            return;
        }
        var zone = WorkZoneManager.Instance.SelectedZone;
        if (zone == null) return;
        if (_pendingTiles >= 0 && System.Environment.TickCount64 - _lastTilesMs >= TilesThrottleMs)
        {
            _lastTilesMs = System.Environment.TickCount64;
            int v = _pendingTiles;
            _pendingTiles = -1;
            WorkZoneManager.Instance.SetTiles(zone.Id, v);
        }
        _infoTimer += delta;
        if (_infoTimer >= 1.0)
        {
            _infoTimer = 0.0;
            RefreshInfo();
        }
    }

    public void Close()
    {
        if (_rootPanel != null && _rootPanel.Visible)
        {
            _rootPanel.Visible = false;
            OnClosed?.Invoke();
        }
    }

    public override void _ExitTree()
    {
        WorkZoneManager.Instance.OnZoneSelected -= OnZoneSelectedFacade;
        WorkZoneManager.Instance.OnZoneDeselected -= Close;
        WorkZoneManager.Instance.OnZonesUpdated -= RefreshInfo;
        base._ExitTree();
    }
}
