using Godot;
using Game.Core;
using Game.Simulation;
using Game.Simulation.Gpu;
using Game.UI.Tools;
using System;
using System.Globalization;

namespace Game.UI;

public partial class HUDController : Control
{
    public static HUDController Instance { get; private set; }

    private Button _buttonConstruct;

    private Control _selectionContainer;
    private Control _buildContainer;
    private Control _orderContainer;
    private Control _wallContainer;
    private Control _zoneContainer;
    private Control _industrialContainer;

    private Button _buildVariantButton;
    private Button _orderVariantButton;

    private Button _wallVariantButton;
    private Button _zonesVariantButton;
    private Button _industrialItemsVariantButton;

    private Button _otherButton;
    private Control _additionallyBox;
    private Button _humidityButton;
    private Label _humidityLegendLabel;

    private Button _woodWallButton;
    private Button _warehouseAreaButton;
    private Button _farmingButton;
    private Button _workTableButton;
    private Button _treeFellingButton;
    private Button _prioritetButton;

    private Label _populationLabel;
    private Label _employmentLabel;
    private Label _woodAmountLabel;
    private Label _worldTimeLabel;
    private Control _amountOfResourcesPanel;

    private Control _speedButtonsContainer;
    private Button _stopButton;
    private Button _speed1xButton;
    private Button _speed5xButton;
    private Button _speed25xButton;
    private Button _speed100xButton;

    private float _statsSyncTimer;
    private float _timeSyncTimer;
    private string _lastPopText = "";
    private string _lastWoodText = "";
    private string _lastTimeText = "";
    private int _totalPopulation = 550;

    private Button _activeToolButton;
    private Panel _activeHighlightPanel;
    private PriorityBoardController _priorityBoard;
    private GardenController _gardenController;

    public override void _Ready()
    {
        Instance = this;
        _buttonConstruct = GetNodeOrNull<Button>("ButtonConstruct") ?? FindChild("ButtonConstruct", true, false) as Button;

        _selectionContainer  = FindChild("SelectionContainer", true, false) as Control;
        _buildContainer      = FindChild("BuildContainer", true, false) as Control;
        _orderContainer      = FindChild("OrderContainer", true, false) as Control;
        _wallContainer       = FindChild("WallContainer", true, false) as Control;
        _zoneContainer       = FindChild("ZoneContainer", true, false) as Control;
        _industrialContainer = FindChild("IndustrialContainer", true, false) as Control;

        _buildVariantButton = FindChild("BuildVariant", true, false) as Button;
        _orderVariantButton = (FindChild("OrderVariant", true, false) as Button) ?? (FindChild("OrderVarinat", true, false) as Button);

        _wallVariantButton            = FindChild("WallVariant", true, false) as Button;
        _zonesVariantButton           = FindChild("ZonesVariant", true, false) as Button;
        _industrialItemsVariantButton = FindChild("IndustrialItemsVariant", true, false) as Button;

        _woodWallButton      = FindChild("WoodWallButton", true, false) as Button;
        _warehouseAreaButton = FindChild("WarehouseArea", true, false) as Button;
        _farmingButton       = FindChild("Farming", true, false) as Button;
        _workTableButton     = FindChild("WorkTableButton", true, false) as Button;
        _treeFellingButton   = (FindChild("TreeFellingButton", true, false) as Button) ?? (FindChild("TreeFelingButton", true, false) as Button);
        _prioritetButton     = FindChild("PrioritetButton", true, false) as Button ?? FindChild("PriorityButton", true, false) as Button;

        // Режим карты: кнопка other (шторка >/<) + additionally-бокс с влажностью.
        _otherButton = FindChild("other", true, false) as Button;
        _additionallyBox = FindChild("HBoxContainer", true, false) as Control;
        if (_additionallyBox == null)
        {
            // HBoxContainer с кнопкой humidity — ищем по ребёнку, если имя другое.
            var humidityProbe = FindChild("humidity", true, false) as Control;
            _additionallyBox = humidityProbe?.GetParent() as Control;
        }
        _humidityButton = FindChild("humidity", true, false) as Button;
        SetupMapModeControls();
        // Легенда создаётся ПОСЛЕ SetMouseFilterRecursive (он ставит Ignore всем
        // не-кнопкам): легенде нужен Stop? Нет — Ignore, но создавать надо после,
        // иначе рекурсия её уже обработала. Порядок: рекурсия ниже, легенда внутри
        // SetupMapModeControls — ок, но тултип-лейбл создаём отдельно после.
        SetupHumidityTooltip();

        _populationLabel = (FindChild("Population", true, false) as Label) ?? GetNodeOrNull<Label>("Population");
        _employmentLabel = (FindChild("Employment", true, false) as Label) ?? GetNodeOrNull<Label>("Employment");
        _woodAmountLabel = FindChild("WoodAmount", true, false) as Label;
        _worldTimeLabel = FindChild("WorldTimeLabel", true, false) as Label;
        _amountOfResourcesPanel = FindChild("AmountOfResources", true, false) as Control;

        _stopButton      = FindChild("stop", true, false) as Button ?? FindChild("StopTime", true, false) as Button;
        _speed1xButton   = FindChild("1x", true, false) as Button ?? FindChild("1xTime", true, false) as Button;
        _speed5xButton   = FindChild("5x", true, false) as Button ?? FindChild("5xTime", true, false) as Button;
        _speed25xButton  = FindChild("25x", true, false) as Button ?? FindChild("25xTime", true, false) as Button;
        _speed100xButton = FindChild("100x", true, false) as Button ?? FindChild("100xTime", true, false) as Button;

        SetupSpeedControlsLayer();
        SetupHighlightPanel();
        SetupPriorityBoard();
        SetupGardenController();
        SetMouseFilterRecursive(this);

        if (_buttonConstruct != null)
        {
            _buttonConstruct.MouseFilter = MouseFilterEnum.Stop;
            _buttonConstruct.Pressed += OnButtonConstructPressed;
        }

        if (_buildVariantButton != null)
        {
            _buildVariantButton.MouseFilter = MouseFilterEnum.Stop;
            _buildVariantButton.Pressed += OnBuildVariantPressed;
        }

        if (_orderVariantButton != null)
        {
            _orderVariantButton.MouseFilter = MouseFilterEnum.Stop;
            _orderVariantButton.Pressed += OnOrderVariantPressed;
        }

        if (_wallVariantButton != null)
        {
            _wallVariantButton.MouseFilter = MouseFilterEnum.Stop;
            _wallVariantButton.Pressed += () => ToggleSubContainer(_wallContainer);
        }

        if (_zonesVariantButton != null)
        {
            _zonesVariantButton.MouseFilter = MouseFilterEnum.Stop;
            _zonesVariantButton.Pressed += () => ToggleSubContainer(_zoneContainer);
        }

        if (_industrialItemsVariantButton != null)
        {
            _industrialItemsVariantButton.MouseFilter = MouseFilterEnum.Stop;
            _industrialItemsVariantButton.Pressed += () => ToggleSubContainer(_industrialContainer);
        }

        if (_woodWallButton != null)
        {
            _woodWallButton.MouseFilter = MouseFilterEnum.Stop;
            _woodWallButton.Pressed += OnWoodWallPressed;
        }

        if (_warehouseAreaButton != null)
        {
            _warehouseAreaButton.MouseFilter = MouseFilterEnum.Stop;
            _warehouseAreaButton.Pressed += OnWarehouseAreaPressed;
        }

        if (_farmingButton != null)
        {
            _farmingButton.MouseFilter = MouseFilterEnum.Stop;
            _farmingButton.Pressed += OnFarmingPressed;
        }

        if (_workTableButton != null)
        {
            _workTableButton.MouseFilter = MouseFilterEnum.Stop;
            _workTableButton.Pressed += OnWorkTablePressed;
        }

        if (_treeFellingButton != null)
        {
            _treeFellingButton.MouseFilter = MouseFilterEnum.Stop;
            _treeFellingButton.Pressed += OnTreeFellingPressed;
        }

        if (_prioritetButton != null)
        {
            _prioritetButton.MouseFilter = MouseFilterEnum.Stop;
            _prioritetButton.Pressed += OnPrioritetButtonPressed;
        }

        BindTimeButton(_stopButton, GameSpeed.Paused);
        BindTimeButton(_speed1xButton, GameSpeed.Normal);
        BindTimeButton(_speed5xButton, GameSpeed.Fast5);
        BindTimeButton(_speed25xButton, GameSpeed.Fast25);
        BindTimeButton(_speed100xButton, GameSpeed.Fast100);

        StockpileManager.Instance.OnItemCountChanged += OnStockpileItemCountChanged;
        UpdateWoodDisplay(StockpileManager.Instance.GetTotalItemCount(ItemId.Log));

        if (PlayerInteractionManager.Instance != null)
        {
            PlayerInteractionManager.Instance.OnToolReset += ClearActiveToolButton;
        }

        CloseAllMenus();
    }

    /// <summary>
    /// Кнопка other тогглит ">" / "&lt;" и видимость additionally-бокса.
    /// Кнопка humidity тогглит режим карты Влажность (слой поверх тайлов).
    /// </summary>
    private void SetupMapModeControls()
    {
        if (_additionallyBox != null)
            _additionallyBox.Visible = false;

        if (_otherButton != null)
        {
            _otherButton.MouseFilter = MouseFilterEnum.Stop;
            _otherButton.Pressed += () =>
            {
                if (_additionallyBox == null) return;
                bool show = !_additionallyBox.Visible;
                _additionallyBox.Visible = show;
                _otherButton.Text = show ? "<" : ">";
            };
        }

        if (_humidityButton != null)
        {
            _humidityButton.MouseFilter = MouseFilterEnum.Stop;
            _humidityButton.Pressed += () =>
            {
                var map = MapRenderer.Instance;
                if (map == null) return;
                bool enable = map.CurrentMapMode != MapMode.Humidity;
                map.SetMapMode(enable ? MapMode.Humidity : MapMode.Normal);
                // Подсветка активной кнопки: яркая когда вкл, тусклая когда выкл.
                _humidityButton.Modulate = enable ? new Color(0.5f, 1f, 1f, 1f) : Color.Color8(255, 255, 255, 255);
                UpdateHumidityLegend(enable);
            };
        }

        // Легенда-шкала влажности (текст, создаём кодом — сцену не трогаем).
        // Позиция — левый низ (600,1020 при 1920×1080), поверх мира, ZIndex выше.
        _humidityLegendLabel = new Label
        {
            Name = "HumidityLegend",
            Text = "Влажность: светлое — сухо, тёмное — мокро",
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 100
        };
        _humidityLegendLabel.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _humidityLegendLabel.Position = new Vector2(20, -60);
        _humidityLegendLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        _humidityLegendLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.9f));
        _humidityLegendLabel.AddThemeConstantOverride("shadow_offset_x", 1);
        _humidityLegendLabel.AddThemeConstantOverride("shadow_offset_y", 1);
        AddChild(_humidityLegendLabel);
    }

    private Label _humidityTooltip;

    /// <summary>
    /// Всплывашка у курсора с % влажности. Создаётся после SetMouseFilterRecursive,
    /// иначе рекурсия выставит ей Ignore... нет, ей и нужен Ignore (не жрёт клики).
    /// TopLevel + ZIndex 200 — летает поверх всего за мышкой.
    /// </summary>
    private void SetupHumidityTooltip()
    {
        _humidityTooltip = new Label
        {
            Name = "HumidityTooltip",
            Text = "",
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 200,
            TopLevel = true
        };
        _humidityTooltip.AddThemeColorOverride("font_color", new Color(0.7f, 0.95f, 1f));
        _humidityTooltip.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.9f));
        _humidityTooltip.AddThemeConstantOverride("shadow_offset_x", 1);
        _humidityTooltip.AddThemeConstantOverride("shadow_offset_y", 1);
        _humidityTooltip.AddThemeFontSizeOverride("font_size", 20);
        AddChild(_humidityTooltip);
    }

    private void UpdateHumidityLegend(bool visible)
    {
        if (_humidityLegendLabel != null)
            _humidityLegendLabel.Visible = visible;
        if (!visible && _humidityTooltip != null)
            _humidityTooltip.Visible = false;
    }

    /// <summary>
    /// Показать % влажности клетки всплывашкой у курсора.
    /// Только в режиме Humidity и только грунт: на воде/мёртвой клетке —
    /// прячем (это вода, и так видно что мокро).
    /// </summary>
    public void ShowHumidityAt(int x, int y)
    {
        if (_humidityTooltip == null) return;
        var map = MapRenderer.Instance;
        if (map?.Humidity == null || map?.MapData?.Ground == null
            || map.CurrentMapMode != MapMode.Humidity)
        {
            _humidityTooltip.Visible = false;
            return;
        }
        var ground = map.MapData.Ground;
        if ((uint)x >= (uint)ground.GetLength(0) || (uint)y >= (uint)ground.GetLength(1)
            || ground[x, y] != TileType.Grass)
        {
            _humidityTooltip.Visible = false;
            return;
        }
        int m = map.Humidity.Get(x, y);
        string word = m < 40 ? "сухо" : m <= 130 ? "норма" : "мокро";
        _humidityTooltip.Text = $"{m}% {word}";
        // Позиция у курсора (+16px вправо-вниз, чтобы не закрывать клетку).
        _humidityTooltip.Position = GetGlobalMousePosition() + new Vector2(16, 16);
        _humidityTooltip.Visible = true;
    }

    private void SetupSpeedControlsLayer()
    {
        // Находим родительский контейнер кнопок времени или поднимаем сами кнопки на верхний слой (ZIndex = 50)
        Control parent = _stopButton?.GetParent() as Control;
        if (parent != null && parent != this)
        {
            _speedButtonsContainer = parent;
            _speedButtonsContainer.ZIndex = 50;
        }
        else
        {
            if (_stopButton != null) _stopButton.ZIndex = 50;
            if (_speed1xButton != null) _speed1xButton.ZIndex = 50;
            if (_speed5xButton != null) _speed5xButton.ZIndex = 50;
            if (_speed25xButton != null) _speed25xButton.ZIndex = 50;
            if (_speed100xButton != null) _speed100xButton.ZIndex = 50;
        }
    }

    private void SetupGardenController()
    {
        _gardenController = new GardenController { Name = "GardenController" };
        AddChild(_gardenController);

        _gardenController.OnOpened += () =>
        {
            CloseAllMenus();
            ClearActiveToolButton();
            SetMainHUDVisible(false);
        };

        _gardenController.OnClosed += () =>
        {
            SetMainHUDVisible(true);
        };
    }

    public void SetMainHUDVisible(bool visible)
    {
        if (_buttonConstruct != null) _buttonConstruct.Visible = visible;
        if (_prioritetButton != null) _prioritetButton.Visible = visible;
        if (_populationLabel != null) _populationLabel.Visible = visible;
        if (_employmentLabel != null) _employmentLabel.Visible = visible;

        if (_amountOfResourcesPanel != null)
        {
            bool shouldShowResources = visible && (_priorityBoard == null || !_priorityBoard.IsOpen);
            _amountOfResourcesPanel.Visible = shouldShowResources;
        }

        if (!visible)
        {
            CloseAllMenus();
        }
    }

    private void SetupHighlightPanel()
    {
        _activeHighlightPanel = new Panel
        {
            Name = "ActiveButtonHighlight",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _activeHighlightPanel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.35f),
            DrawCenter = true,
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            BorderColor = new Color(0.1f, 0.1f, 0.1f, 0.95f),
            BorderBlend = false
        };
        _activeHighlightPanel.AddThemeStyleboxOverride("panel", style);
    }

    private void SetupPriorityBoard()
    {
        _priorityBoard = FindChild("Control", true, false) as PriorityBoardController
                         ?? FindChild("PriorityBoard", true, false) as PriorityBoardController;

        if (_priorityBoard == null)
        {
            var scenePaths = new[]
            {
                "res://ui/Prioritet.tscn",
                "res://scenes/ui/Prioritet.tscn",
                "res://Prioritet.tscn",
                "res://scenes/Prioritet.tscn"
            };

            foreach (var path in scenePaths)
            {
                if (ResourceLoader.Exists(path))
                {
                    var scene = ResourceLoader.Load<PackedScene>(path);
                    if (scene != null)
                    {
                        var inst = scene.Instantiate();
                        if (inst is Control ctrl)
                        {
                            AddChild(ctrl);
                            _priorityBoard = new PriorityBoardController();
                            AddChild(_priorityBoard);
                            _priorityBoard.Initialize(ctrl);
                            break;
                        }
                    }
                }
            }
        }
    }

    private void OnPrioritetButtonPressed()
    {
        if (_priorityBoard == null) return;

        if (_priorityBoard.IsOpen)
        {
            _priorityBoard.Close();
            ClearActiveToolButton();
            if (_amountOfResourcesPanel != null && (_gardenController == null || !_gardenController.IsOpen))
                _amountOfResourcesPanel.Visible = true;
        }
        else
        {
            ToggleToolButton(_prioritetButton, () =>
            {
                FarmZoneManager.Instance.DeselectZone();
                if (_amountOfResourcesPanel != null)
                    _amountOfResourcesPanel.Visible = false;
                _priorityBoard.Open();
            });
        }
    }

    private void ToggleToolButton(Button button, Action activateAction)
    {
        if (_activeToolButton == button)
        {
            ClearActiveToolButton();
            if (_priorityBoard != null && _priorityBoard.IsOpen)
            {
                _priorityBoard.Close();
                if (_amountOfResourcesPanel != null && (_gardenController == null || !_gardenController.IsOpen))
                    _amountOfResourcesPanel.Visible = true;
            }
            PlayerInteractionManager.Instance?.ResetToDefault();
            return;
        }

        ClearActiveToolButton();
        FarmZoneManager.Instance.DeselectZone();
        _activeToolButton = button;

        if (_activeToolButton != null && _activeHighlightPanel != null)
        {
            if (_activeHighlightPanel.GetParent() != null)
                _activeHighlightPanel.GetParent().RemoveChild(_activeHighlightPanel);

            _activeToolButton.AddChild(_activeHighlightPanel);
            _activeHighlightPanel.Visible = true;
        }

        activateAction?.Invoke();
    }

    public void ClearActiveToolButton()
    {
        if (_activeHighlightPanel != null && _activeHighlightPanel.GetParent() != null)
        {
            _activeHighlightPanel.GetParent().RemoveChild(_activeHighlightPanel);
            _activeHighlightPanel.Visible = false;
        }
        _activeToolButton = null;
    }

    // Всплывашка влажности следует за курсором каждый кадр, пока видима.
    // Text не трогаем здесь (только Position) — дёшево.
    private void FollowHumidityTooltip()
    {
        if (_humidityTooltip != null && _humidityTooltip.Visible)
            _humidityTooltip.Position = GetGlobalMousePosition() + new Vector2(16, 16);
    }

    public override void _Process(double delta)
    {
        FollowHumidityTooltip();
        _statsSyncTimer += (float)delta;
        // П.8: некритичные показатели (население/занятость) — 1с вместо 250мс,
        // время мира — отдельно 0.5с (дёргать Label каждый кадр незачем).
        if (_statsSyncTimer >= 1.0f)
        {
            _statsSyncTimer = 0f;
            RefreshStats();
        }
        _timeSyncTimer += (float)delta;
        if (_timeSyncTimer >= 0.5f)
        {
            _timeSyncTimer = 0f;
            UpdateWorldTimeDisplay();
        }
    }

    private void RefreshStats()
    {
        int total = _totalPopulation;
        int unemployed = JobDispatcher.Instance.IdleWorkers.TotalIdleCount;
        int employed = Math.Max(0, total - unemployed);

        string pop = FormatAmount(total);
        if (pop != _lastPopText) { _lastPopText = pop; if (_populationLabel != null) _populationLabel.Text = pop; }
        string emp = FormatAmount(employed) + "/" + FormatAmount(unemployed);
        // П.5: GPU-редукция (средний голод/настроение) — ТОЛЬКО если есть свежий
        // снапшот (Last != null). Без GPU строка бит-в-бит как раньше. Существующий
        // путь (тоталы, Text-при-изменении) не тронут: формат дописывается в ту же
        // строку, новых Node в сцене нет.
        var stats = GpuStatsReduce.Instance.Last;
        if (stats != null)
            emp += $" H:{stats.AvgHunger:0} M:{stats.AvgMood:0}";
        if (_employmentLabel != null && _employmentLabel.Text != emp) _employmentLabel.Text = emp;

        UpdateWorldTimeDisplayThrottled();
    }

    private void UpdateWorldTimeDisplayThrottled()
    {
        // Время обновляется своим таймером 0.5с — здесь только если таймер уже тикнул.
        if (_timeSyncTimer <= 0.001f) UpdateWorldTimeDisplay();
    }

    /// <summary>
    /// Обновляет «время в мире»: день и часы/минуты суток (сутки = 24 игровых часа).
    /// Работает при любой скорости: на 100x сутки идут ровно 2 реальные минуты.
    /// </summary>
    private void UpdateWorldTimeDisplay()
    {
        if (_worldTimeLabel == null || TimeManager.Instance == null)
            return;

        var tm = TimeManager.Instance;
        int day = tm.DayNumber;
        float tod = tm.TimeOfDaySeconds;
        int hours = (int)(tod / WorldTime.SecondsPerHour);
        int minutes = (int)((tod % WorldTime.SecondsPerHour) / WorldTime.SecondsPerHour * 60f);
        string icon = DayNightCycle.IsNight(tod) ? "☾" : "☀";

        string text = $"{icon} День {day}, {hours:00}:{minutes:00}";
        if (text == _lastTimeText) return;
        _lastTimeText = text;
        _worldTimeLabel.Text = text;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            bool handled = false;

            if (_gardenController != null && _gardenController.IsOpen)
            {
                FarmZoneManager.Instance.DeselectZone();
                handled = true;
            }

            if ((_selectionContainer != null && _selectionContainer.Visible) || _activeToolButton != null || (_priorityBoard != null && _priorityBoard.IsOpen))
            {
                CloseAllMenus();
                if (_priorityBoard != null && _priorityBoard.IsOpen)
                {
                    _priorityBoard.Close();
                    if (_amountOfResourcesPanel != null && (_gardenController == null || !_gardenController.IsOpen))
                        _amountOfResourcesPanel.Visible = true;
                }
                ClearActiveToolButton();
                PlayerInteractionManager.Instance?.ResetToDefault();
                handled = true;
            }

            if (handled)
            {
                GetViewport().SetInputAsHandled();
            }
        }
    }

    private void OnStockpileItemCountChanged(ItemId itemId, int totalCount)
    {
        if (itemId == ItemId.Log)
        {
            UpdateWoodDisplay(totalCount);
        }
    }

    private void UpdateWoodDisplay(int count)
    {
        if (_woodAmountLabel != null)
        {
            string text = FormatAmount(count);
            if (text == _lastWoodText) return;
            _lastWoodText = text;
            _woodAmountLabel.Text = text;
        }
    }

    private static string FormatAmount(int count)
    {
        if (count < 1000)
            return count.ToString();

        if (count < 1_000_000)
        {
            float kValue = count / 1000f;
            return kValue.ToString("0.##", CultureInfo.InvariantCulture) + "k";
        }

        float mValue = count / 1_000_000f;
        return mValue.ToString("0.##", CultureInfo.InvariantCulture) + "M";
    }

    private void OnButtonConstructPressed()
    {
        if (_selectionContainer != null && _selectionContainer.Visible)
        {
            CloseAllMenus();
            ClearActiveToolButton();
            PlayerInteractionManager.Instance?.ResetToDefault();
        }
        else
        {
            CloseAllMenus();
            ShowContainer(_selectionContainer);
        }
    }

    private void OnBuildVariantPressed()
    {
        if (_buildContainer != null && _buildContainer.Visible)
        {
            CloseTier2AndSub();
        }
        else
        {
            CloseTier2AndSub();
            HideContainer(_orderContainer);
            ShowContainer(_buildContainer);
        }
    }

    private void OnOrderVariantPressed()
    {
        if (_orderContainer != null && _orderContainer.Visible)
        {
            HideContainer(_orderContainer);
        }
        else
        {
            CloseTier2AndSub();
            ShowContainer(_orderContainer);
        }
    }

    private void ToggleSubContainer(Control targetSub)
    {
        if (targetSub != null && targetSub.Visible)
        {
            HideContainer(targetSub);
        }
        else
        {
            HideAllSubContainers();
            ShowContainer(targetSub);
        }
    }

    private static void ShowContainer(Control control)
    {
        if (control == null) return;
        control.Visible = true;
    }

    private static void HideContainer(Control control)
    {
        if (control == null) return;
        control.Visible = false;
    }

    private void CloseTier2AndSub()
    {
        HideContainer(_buildContainer);
        HideAllSubContainers();
    }

    private void HideAllSubContainers()
    {
        HideContainer(_wallContainer);
        HideContainer(_zoneContainer);
        HideContainer(_industrialContainer);
    }

    private void CloseAllMenus()
    {
        HideContainer(_selectionContainer);
        HideContainer(_buildContainer);
        HideContainer(_orderContainer);
        HideAllSubContainers();
    }

    private void OnWoodWallPressed()
    {
        ToggleToolButton(_woodWallButton, () =>
        {
            var interaction = PlayerInteractionManager.Instance;
            var mapRenderer = MapRenderer.Instance;
            if (interaction == null || mapRenderer?.WallBuildManager == null || mapRenderer?.MapData == null) return;

            var buildTool = new BuildTool(
                mapRenderer.WallBuildManager,
                mapRenderer.GhostLayer,
                mapRenderer.MapData,
                BuildingType.WoodWall,
                MapRenderer.SourceWall
            );
            interaction.SetTool(buildTool);
        });
    }

    private void OnWorkTablePressed()
    {
        ToggleToolButton(_workTableButton, () =>
        {
            var interaction = PlayerInteractionManager.Instance;
            var mapRenderer = MapRenderer.Instance;
            if (interaction == null || mapRenderer?.WallBuildManager == null || mapRenderer?.MapData == null) return;

            var tableTool = new BuildTool(
                mapRenderer.WallBuildManager,
                mapRenderer.GhostLayer,
                mapRenderer.MapData,
                BuildingType.WorkTable,
                MapRenderer.SourceWorkTable
            );
            interaction.SetTool(tableTool);
        });
    }

    private void OnWarehouseAreaPressed()
    {
        ToggleToolButton(_warehouseAreaButton, () =>
        {
            var interaction = PlayerInteractionManager.Instance;
            if (interaction == null) return;

            var warehouseTool = new WarehouseTool(interaction.Selection);
            interaction.SetTool(warehouseTool);
        });
    }

    private void OnFarmingPressed()
    {
        ToggleToolButton(_farmingButton, () =>
        {
            var interaction = PlayerInteractionManager.Instance;
            var mapRenderer = MapRenderer.Instance;
            if (interaction == null || mapRenderer?.MapData == null || mapRenderer?.WallBuildManager == null) return;

            var farmTool = new FarmingTool(interaction.Selection, mapRenderer.MapData, mapRenderer.WallBuildManager);
            interaction.SetTool(farmTool);
        });
    }

    private void OnTreeFellingPressed()
    {
        ToggleToolButton(_treeFellingButton, () =>
        {
            var interaction = PlayerInteractionManager.Instance;
            var mapRenderer = MapRenderer.Instance;
            if (interaction == null || mapRenderer?.MapData == null) return;

            var treeTool = new TreeFellingTool(interaction.Selection, mapRenderer.MapData);
            interaction.SetTool(treeTool);
        });
    }

    private void BindTimeButton(Button button, GameSpeed speed)
    {
        if (button != null)
        {
            button.MouseFilter = MouseFilterEnum.Stop;
            button.Pressed += () => TimeManager.Instance?.SetSpeed(speed);
        }
    }

    public void Setup(PlayerInteractionManager interactionManager, MapRenderer mapRenderer, int totalPopulation = 550)
    {
        _totalPopulation = totalPopulation;
        if (interactionManager != null)
        {
            interactionManager.OnToolReset += ClearActiveToolButton;
        }
        RefreshStats();
    }

    private static void SetMouseFilterRecursive(Control control)
    {
        if (control is not BaseButton)
        {
            control.MouseFilter = MouseFilterEnum.Ignore;
        }

        foreach (Node child in control.GetChildren())
        {
            if (child is Control childControl)
                SetMouseFilterRecursive(childControl);
        }
    }

    public void UpdateUI(CityState state)
    {
        if (_populationLabel != null)
            _populationLabel.Text = state.Population.ToString();
        if (_employmentLabel != null)
            _employmentLabel.Text = $"{state.Employed}/{state.Unemployed}";
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
        StockpileManager.Instance.OnItemCountChanged -= OnStockpileItemCountChanged;
        if (PlayerInteractionManager.Instance != null)
        {
            PlayerInteractionManager.Instance.OnToolReset -= ClearActiveToolButton;
        }
        base._ExitTree();
    }
}