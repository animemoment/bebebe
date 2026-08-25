using Godot;
using Game.Core;
using Game.Simulation;
using Game.UI.Tools;
using System;
using System.Globalization;

namespace Game.UI;

public partial class HUDController : Control
{
    private const float VerticalTierOffset = 75.0f;

    private Button _buttonConstruct;

    private Control _selectionContainer;
    private Control _buildContainer;
    private Control _orderContainer;
    private Control _wallContainer;
    private Control _zoneContainer;
    private Control _industrialContainer;

    private Vector2 _selectionInitialPos;
    private Vector2 _buildInitialPos;
    private Vector2 _orderInitialPos;
    private Vector2 _wallInitialPos;
    private Vector2 _zoneInitialPos;
    private Vector2 _industrialInitialPos;

    private Button _buildVariantButton;
    private Button _orderVariantButton;

    private Button _wallVariantButton;
    private Button _zonesVariantButton;
    private Button _industrialItemsVariantButton;

    private Button _woodWallButton;
    private Button _warehouseAreaButton;
    private Button _farmingButton;
    private Button _workTableButton;
    private Button _treeFellingButton;
    private Button _prioritetButton;

    private Label _populationLabel;
    private Label _employmentLabel;
    private Label _woodAmountLabel;
    private Control _amountOfResourcesPanel;

    private Button _stopButton;
    private Button _speed1xButton;
    private Button _speed5xButton;
    private Button _speed25xButton;
    private Button _speed100xButton;

    private float _statsSyncTimer;
    private int _totalPopulation = 550;

    private Button _activeToolButton;
    private Panel _activeHighlightPanel;
    private PriorityBoardController _priorityBoard;

    public override void _Ready()
    {
        _buttonConstruct = GetNodeOrNull<Button>("ButtonConstruct") ?? FindChild("ButtonConstruct", true, false) as Button;

        _selectionContainer  = FindChild("SelectionContainer", true, false) as Control;
        _buildContainer      = FindChild("BuildContainer", true, false) as Control;
        _orderContainer      = FindChild("OrderContainer", true, false) as Control;
        _wallContainer       = FindChild("WallContainer", true, false) as Control;
        _zoneContainer       = FindChild("ZoneContainer", true, false) as Control;
        _industrialContainer = FindChild("IndustrialContainer", true, false) as Control;

        if (_selectionContainer != null)  _selectionInitialPos  = _selectionContainer.Position;
        if (_buildContainer != null)      _buildInitialPos      = _buildContainer.Position;
        if (_orderContainer != null)      _orderInitialPos      = _orderContainer.Position;
        if (_wallContainer != null)       _wallInitialPos       = _wallContainer.Position;
        if (_zoneContainer != null)       _zoneInitialPos       = _zoneContainer.Position;
        if (_industrialContainer != null) _industrialInitialPos = _industrialContainer.Position;

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

        _populationLabel = (FindChild("Population", true, false) as Label) ?? GetNodeOrNull<Label>("Population");
        _employmentLabel = (FindChild("Employment", true, false) as Label) ?? GetNodeOrNull<Label>("Employment");
        _woodAmountLabel = FindChild("WoodAmount", true, false) as Label;
        _amountOfResourcesPanel = FindChild("AmountOfResources", true, false) as Control;

        _stopButton      = FindChild("stop", true, false) as Button ?? FindChild("StopTime", true, false) as Button;
        _speed1xButton   = FindChild("1x", true, false) as Button ?? FindChild("1xTime", true, false) as Button;
        _speed5xButton   = FindChild("5x", true, false) as Button ?? FindChild("5xTime", true, false) as Button;
        _speed25xButton  = FindChild("25x", true, false) as Button ?? FindChild("25xTime", true, false) as Button;
        _speed100xButton = FindChild("100x", true, false) as Button ?? FindChild("100xTime", true, false) as Button;

        SetupHighlightPanel();
        SetupPriorityBoard();
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
            _wallVariantButton.Pressed += () => ToggleSubContainer(_wallContainer, _wallInitialPos);
        }

        if (_zonesVariantButton != null)
        {
            _zonesVariantButton.MouseFilter = MouseFilterEnum.Stop;
            _zonesVariantButton.Pressed += () => ToggleSubContainer(_zoneContainer, _zoneInitialPos);
        }

        if (_industrialItemsVariantButton != null)
        {
            _industrialItemsVariantButton.MouseFilter = MouseFilterEnum.Stop;
            _industrialItemsVariantButton.Pressed += () => ToggleSubContainer(_industrialContainer, _industrialInitialPos);
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
            if (_amountOfResourcesPanel != null) _amountOfResourcesPanel.Visible = true;
        }
        else
        {
            ToggleToolButton(_prioritetButton, () =>
            {
                if (_amountOfResourcesPanel != null) _amountOfResourcesPanel.Visible = false;
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
                if (_amountOfResourcesPanel != null) _amountOfResourcesPanel.Visible = true;
            }
            PlayerInteractionManager.Instance?.ResetToDefault();
            return;
        }

        ClearActiveToolButton();
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

    public override void _Process(double delta)
    {
        _statsSyncTimer += (float)delta;
        if (_statsSyncTimer >= 0.25f)
        {
            _statsSyncTimer = 0f;
            RefreshStats();
        }
    }

    private void RefreshStats()
    {
        int total = _totalPopulation;
        int unemployed = JobDispatcher.Instance.IdleWorkers.TotalIdleCount;
        int employed = Math.Max(0, total - unemployed);

        if (_populationLabel != null)
            _populationLabel.Text = FormatAmount(total);

        if (_employmentLabel != null)
            _employmentLabel.Text = $"{FormatAmount(employed)}/{FormatAmount(unemployed)}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            if ((_selectionContainer != null && _selectionContainer.Visible) || _activeToolButton != null || (_priorityBoard != null && _priorityBoard.IsOpen))
            {
                CloseAllMenus();
                if (_priorityBoard != null && _priorityBoard.IsOpen)
                {
                    _priorityBoard.Close();
                    if (_amountOfResourcesPanel != null) _amountOfResourcesPanel.Visible = true;
                }
                ClearActiveToolButton();
                PlayerInteractionManager.Instance?.ResetToDefault();
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
            _woodAmountLabel.Text = FormatAmount(count);
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
            ShowContainer(_selectionContainer, _selectionInitialPos);
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
            HideContainer(_orderContainer, _orderInitialPos);

            Vector2 targetPos = new Vector2(
                _buildInitialPos.X,
                _selectionContainer != null ? _selectionContainer.Position.Y - VerticalTierOffset : _buildInitialPos.Y - VerticalTierOffset
            );
            ShowContainer(_buildContainer, targetPos);
        }
    }

    private void OnOrderVariantPressed()
    {
        if (_orderContainer != null && _orderContainer.Visible)
        {
            HideContainer(_orderContainer, _orderInitialPos);
        }
        else
        {
            CloseTier2AndSub();
            Vector2 targetPos = new Vector2(
                _orderInitialPos.X,
                _selectionContainer != null ? _selectionContainer.Position.Y - VerticalTierOffset : _orderInitialPos.Y - VerticalTierOffset
            );
            ShowContainer(_orderContainer, targetPos);
        }
    }

    private void ToggleSubContainer(Control targetSub, Vector2 baseInitialPos)
    {
        if (targetSub != null && targetSub.Visible)
        {
            HideContainer(targetSub, baseInitialPos);
        }
        else
        {
            HideAllSubContainers();
            Vector2 targetPos = new Vector2(
                baseInitialPos.X,
                _buildContainer != null ? _buildContainer.Position.Y - VerticalTierOffset : baseInitialPos.Y - VerticalTierOffset
            );
            ShowContainer(targetSub, targetPos);
        }
    }

    private static void ShowContainer(Control control, Vector2 position)
    {
        if (control == null) return;
        control.Position = position;
        control.Visible = true;
    }

    private static void HideContainer(Control control, Vector2 baseInitialPos)
    {
        if (control == null) return;
        control.Visible = false;
        control.Position = baseInitialPos;
    }

    private void CloseTier2AndSub()
    {
        HideContainer(_buildContainer, _buildInitialPos);
        HideAllSubContainers();
    }

    private void HideAllSubContainers()
    {
        HideContainer(_wallContainer, _wallInitialPos);
        HideContainer(_zoneContainer, _zoneInitialPos);
        HideContainer(_industrialContainer, _industrialInitialPos);
    }

    private void CloseAllMenus()
    {
        HideContainer(_selectionContainer, _selectionInitialPos);
        HideContainer(_buildContainer, _buildInitialPos);
        HideContainer(_orderContainer, _orderInitialPos);
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
        StockpileManager.Instance.OnItemCountChanged -= OnStockpileItemCountChanged;
        if (PlayerInteractionManager.Instance != null)
        {
            PlayerInteractionManager.Instance.OnToolReset -= ClearActiveToolButton;
        }
        base._ExitTree();
    }
}