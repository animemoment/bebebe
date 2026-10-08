using Godot;
using Game.Simulation;
using System;

namespace Game.UI;

/// <summary>
/// Окно фермы (Farm.tscn). Открывается при клике по клетке фермы.
/// Показывает название («Ферма #5»), число клеток, пшеницу, галочку
/// автопосадки. Кнопка ButtonForJobDisplay ведёт в JobDisplay (§20.6),
/// кнопка Infoormation — тумблер подсказки про клетку (§20.5).
/// Замена старому GardenController — Garden.tscn удалён.
/// </summary>
public partial class FarmController : Control
{
    public static FarmController Instance { get; private set; }

    private Control _rootPanel;
    private Label _titleLabel;
    private Label _tilesLabel;
    private Label _resourcesLabel;
    private BaseButton _escapeButton;
    private BaseButton _checkMarkButton;
    private BaseButton _infoButton;
    private BaseButton _jobDisplayButton;

    // Всплывашка про клетку (§20.5) и тумблер кнопки Infoormation (шаг 4).
    private Label _cellTooltip;
    private bool _infoMode;
    private int _lastTipX = int.MinValue;
    private int _lastTipY = int.MinValue;
    private string _lastTipText = "";

    public event Action OnOpened;
    public event Action OnClosed;

    public bool IsOpen => _rootPanel != null && _rootPanel.Visible;

    /// <summary>Включён ли режим подсказок про клетки (кнопка Infoormation).</summary>
    public bool InfoMode => _infoMode;

    public override void _Ready()
    {
        Instance = this;
        ZIndex = 20;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        LoadScene();

        FarmZoneManager.Instance.OnZoneSelected += OnZoneSelected;
        FarmZoneManager.Instance.OnZoneDeselected += Close;
    }

    private void LoadScene()
    {
        var paths = new[]
        {
            "res://scenes/ui/Farm.tscn",
            "res://Farm.tscn",
            "res://ui/Farm.tscn",
        };

        foreach (var path in paths)
        {
            if (!ResourceLoader.Exists(path))
                continue;
            var packed = ResourceLoader.Load<PackedScene>(path);
            if (packed == null)
                continue;
            _rootPanel = packed.Instantiate<Control>();
            AddChild(_rootPanel);
            _rootPanel.Visible = false;

            ConfigureInputBlocking(_rootPanel);
            BindNodes(_rootPanel);

            if (_escapeButton != null)
                _escapeButton.Pressed += () => FarmZoneManager.Instance.DeselectZone();
            if (_checkMarkButton != null)
                _checkMarkButton.Pressed += OnCheckMarkPressed;
            if (_infoButton != null)
                _infoButton.Pressed += OnInfoButtonPressed;
            if (_jobDisplayButton != null)
                _jobDisplayButton.Pressed += OnJobDisplayPressed;

            BuildCellTooltip();
            break;
        }

        if (_rootPanel == null)
            GD.PushWarning("[Farm] Farm.tscn не найден ни по одному пути.");
    }

    /// <summary>
    /// Привязка нод Farm.tscn. Внимание: имя «Label» встречается дважды
    /// (заголовок — прямой ребёнок корня, «N клеток» — внутри ScrollContainer),
    /// имя «CheckMark» — тоже дважды (корневая кнопка и внутренняя в списке).
    /// Поэтому заголовок и кнопки берём среди ПРЯМЫХ детей корня, а счётчик
    /// клеток — внутри ScrollContainer. Рекурсивный FindChild здесь врёт.
    /// </summary>
    private void BindNodes(Control root)
    {
        // Прямые дети корня: заголовок + кнопки.
        foreach (Node child in root.GetChildren())
        {
            if (child is not Control ctrl)
                continue;
            switch (ctrl.Name)
            {
                case "Label" when _titleLabel == null && child.GetParent() == root:
                    _titleLabel = ctrl as Label;
                    break;
                case "CheckMark" when _checkMarkButton == null && child.GetParent() == root:
                    _checkMarkButton = ctrl as BaseButton;
                    break;
                case "esc" when _escapeButton == null:
                    _escapeButton = ctrl as BaseButton;
                    break;
                case "Infoormation" when _infoButton == null:
                    // В сцене опечатка с двумя «o» — ищем именно так.
                    _infoButton = ctrl as BaseButton;
                    break;
                case "ButtonForJobDisplay" when _jobDisplayButton == null:
                    _jobDisplayButton = ctrl as BaseButton;
                    break;
            }
        }

        // Запасные варианты (если структура сцены другая, напр. старая Garden).
        _titleLabel ??= root.FindChild("Label", true, false) as Label;
        _escapeButton ??= root.FindChild("Escape", true, false) as BaseButton
            ?? root.FindChild("Close", true, false) as BaseButton
            ?? root.FindChild("Exit", true, false) as BaseButton
            ?? root.FindChild("Back", true, false) as BaseButton;
        _checkMarkButton ??= root.FindChild("CheckMark", true, false) as BaseButton
            ?? root.FindChild("Checkmark", true, false) as BaseButton
            ?? root.FindChild("CheckBox", true, false) as BaseButton;

        // Счётчик клеток — внутри ScrollContainer (не путать с заголовком).
        var scroll = root.FindChild("ScrollContainer", true, false) as Control;
        if (scroll != null)
            _tilesLabel = scroll.FindChild("Label", true, false) as Label;
        _tilesLabel ??= root.FindChild("NumFarm", true, false) as Label;

        _resourcesLabel = root.FindChild("AmountOfResources", true, false) as Label;

        // Внутренний CheckMark из списка культур — прячем, работает корневой.
        // (У него то же имя, BindNodes выше взял корневой среди прямых детей.)
        if (scroll != null)
        {
            var innerCheck = scroll.FindChild("CheckMark", true, false) as Control;
            if (innerCheck != null && innerCheck != _checkMarkButton)
                innerCheck.Visible = false;
        }

        // InfoWindow из сцены лежит за краем окна (offset_left = −236) и не виден.
        // Сцены не трогаем — вместо него свой тултип (BuildCellTooltip).
        var sceneInfo = root.FindChild("InfoWindow", true, false) as Control;
        if (sceneInfo != null)
            sceneInfo.Visible = false;
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

    private void OnCheckMarkPressed()
    {
        var zone = FarmZoneManager.Instance.SelectedZone;
        if (zone == null) return;

        bool newState = !zone.AutoPlantEnabled;
        FarmZoneManager.Instance.SetAutoPlant(zone.Id, newState);
        UpdateCheckMarkState(newState);
    }

    private void UpdateCheckMarkState(bool isEnabled)
    {
        if (_checkMarkButton != null)
        {
            if (_checkMarkButton is Button btn)
            {
                btn.Modulate = isEnabled ? new Color(0.4f, 1.0f, 0.4f, 1.0f) : new Color(1f, 1f, 1f, 0.5f);
            }
            else if (_checkMarkButton is CheckBox chk)
            {
                chk.ButtonPressed = isEnabled;
            }
        }
    }

    /// <summary>Тумблер подсказки про клетку (кнопка Infoormation, §20.5).</summary>
    private void OnInfoButtonPressed()
    {
        _infoMode = !_infoMode;
        if (_infoButton is Button btn)
            btn.Modulate = _infoMode ? new Color(0.6f, 1f, 1f, 1f) : new Color(1f, 1f, 1f, 1f);
        if (!_infoMode && _cellTooltip != null)
            _cellTooltip.Visible = false;
    }

    /// <summary>Кнопка ButtonForJobDisplay — открыть JobDisplay для этой фермы (§20.6).</summary>
    private void OnJobDisplayPressed()
    {
        var zone = FarmZoneManager.Instance.SelectedZone;
        if (zone == null) return;
        var work = WorkZoneController.Instance;
        if (work == null) return;
        Close();
        work.ShowForFarmZone(zone.Id);
    }

    private void OnZoneSelected(FarmZone zone)
    {
        if (_rootPanel == null || zone == null) return;

        // Единый выбор уже в ZoneManager: закрываем окно Work-зоны если открыто.
        var work = WorkZoneController.Instance;
        if (work != null && work.IsOpen)
            work.Close();

        if (_titleLabel != null)
            _titleLabel.Text = zone.Name;

        if (_tilesLabel != null)
            _tilesLabel.Text = $"{zone.TotalTiles} кл.";

        if (_resourcesLabel != null)
            _resourcesLabel.Text = $"Нужно зерна: {zone.TotalTiles}";

        UpdateCheckMarkState(zone.AutoPlantEnabled);
        ResetCellTooltip();

        _rootPanel.Visible = true;
        OnOpened?.Invoke();
    }

    public void Close()
    {
        if (_cellTooltip != null)
            _cellTooltip.Visible = false;
        if (_rootPanel != null && _rootPanel.Visible)
        {
            _rootPanel.Visible = false;
            OnClosed?.Invoke();
        }
    }

    public override void _Process(double delta)
    {
        if (_rootPanel == null || !_rootPanel.Visible)
            return;
        TickCellTooltip();
        TickPlantedCount();
    }

    // --- Подсказка про клетку (§20.5, шаг 4) ---

    private double _plantedTimer;
    private string _lastPlantedText = "";

    /// <summary>Строка «растёт K/N» — обновляется раз в секунду, Text только при изменении.</summary>
    private void TickPlantedCount()
    {
        _plantedTimer += 1.0 / 60.0;
        if (_plantedTimer < 1.0)
            return;
        _plantedTimer = 0.0;
        var zone = FarmZoneManager.Instance.SelectedZone;
        if (zone == null || _resourcesLabel == null)
            return;
        int growing = 0;
        foreach (var (x, y) in zone.Tiles)
        {
            if (CropGrowthManager.Instance.HasCrop(x, y))
                growing++;
        }
        string text = $"Нужно зерна: {zone.TotalTiles} · растёт {growing}/{zone.TotalTiles}";
        if (text != _lastPlantedText)
        {
            _lastPlantedText = text;
            _resourcesLabel.Text = text;
        }
    }

    /// <summary>Свой тултип вместо сценичного InfoWindow (тот за краем окна).</summary>
    private void BuildCellTooltip()
    {
        _cellTooltip = new Label
        {
            Name = "FarmCellTooltip",
            Text = "",
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 200,
            TopLevel = true
        };
        _cellTooltip.AddThemeColorOverride("font_color", new Color(0.8f, 1f, 0.8f));
        _cellTooltip.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.9f));
        _cellTooltip.AddThemeConstantOverride("shadow_offset_x", 1);
        _cellTooltip.AddThemeConstantOverride("shadow_offset_y", 1);
        _cellTooltip.AddThemeFontSizeOverride("font_size", 20);
        AddChild(_cellTooltip);
    }

    private void ResetCellTooltip()
    {
        _lastTipX = int.MinValue;
        _lastTipY = int.MinValue;
        _lastTipText = "";
        if (_cellTooltip != null)
            _cellTooltip.Visible = false;
    }

    /// <summary>
    /// Подсказка рядом с мышкой: влажность, плодородие, что растёт.
    /// Видна только при включённом InfoMode и мышке над клеткой своей фермы.
    /// </summary>
    private void TickCellTooltip()
    {
        if (_cellTooltip == null || !_infoMode)
        {
            if (_cellTooltip != null && _cellTooltip.Visible)
                _cellTooltip.Visible = false;
            return;
        }
        var zone = FarmZoneManager.Instance.SelectedZone;
        var map = MapRenderer.Instance;
        if (zone == null || map?.MapData?.Ground == null)
        {
            _cellTooltip.Visible = false;
            return;
        }
        Vector2 mouse = GetGlobalMousePosition();
        int tx = (int)(mouse.X / MapRenderer.TileSizePx);
        int ty = (int)(mouse.Y / MapRenderer.TileSizePx);
        var ground = map.MapData.Ground;
        if ((uint)tx >= (uint)ground.GetLength(0) || (uint)ty >= (uint)ground.GetLength(1))
        {
            _cellTooltip.Visible = false;
            return;
        }
        // Только своя ферма — чужие клетки и мимо зон прячут тултип.
        if (!zone.Tiles.Contains((tx, ty)))
        {
            _cellTooltip.Visible = false;
            _lastTipX = int.MinValue;
            return;
        }
        // Текст — только при смене клетки, позиция — каждый кадр.
        if (tx != _lastTipX || ty != _lastTipY)
        {
            _lastTipX = tx;
            _lastTipY = ty;
            string text = BuildCellText(tx, ty, map);
            if (text != _lastTipText)
            {
                _lastTipText = text;
                _cellTooltip.Text = text;
            }
        }
        if (string.IsNullOrEmpty(_lastTipText))
        {
            _cellTooltip.Visible = false;
            return;
        }
        _cellTooltip.GlobalPosition = GetViewport().GetMousePosition() + new Vector2(16, 16);
        _cellTooltip.Visible = true;
    }

    private static string BuildCellText(int x, int y, MapRenderer map)
    {
        int m = map.Humidity != null ? map.Humidity.Get(x, y) : 100;
        int f = map.Fertility != null ? map.Fertility.Get(x, y) : 100;
        string mWord = m < 40 ? "сухо" : m <= 130 ? "норма" : "мокро";
        string fWord = f < 60 ? "бедно" : f <= 140 ? "норма" : "богато";
        string crop;
        if (CropGrowthManager.Instance.TryGetCrop(x, y, out var c))
            crop = $"Пшеница (фаза {c.Stage}/4)";
        else
            crop = "Пусто";
        return $"Влага: {m}% {mWord}\nПлодородие: {f} {fWord}\n{crop}";
    }

    public override void _ExitTree()
    {
        FarmZoneManager.Instance.OnZoneSelected -= OnZoneSelected;
        FarmZoneManager.Instance.OnZoneDeselected -= Close;
        base._ExitTree();
    }
}
