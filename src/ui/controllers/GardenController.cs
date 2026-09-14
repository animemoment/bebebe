using Godot;
using Game.Simulation;
using System;

namespace Game.UI;

public partial class GardenController : Control
{
    public static GardenController Instance { get; private set; }

    private Control _rootPanel;
    private Label _titleLabel;
    private Label _infoLabel;
    private BaseButton _escapeButton;
    private BaseButton _checkMarkButton;

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

        FarmZoneManager.Instance.OnZoneSelected += OnZoneSelected;
        FarmZoneManager.Instance.OnZoneDeselected += Close;
    }

    private void LoadScene()
    {
        var paths = new[]
        {
            "res://Garden.tscn",
            "res://scenes/ui/Garden.tscn",
            "res://ui/Garden.tscn"
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

                    _titleLabel = _rootPanel.FindChild("Label", true, false) as Label;
                    _infoLabel = _rootPanel.FindChild("NumFarm", true, false) as Label;

                    _escapeButton = _rootPanel.FindChild("Escape", true, false) as BaseButton
                                   ?? _rootPanel.FindChild("Close", true, false) as BaseButton
                                   ?? _rootPanel.FindChild("Exit", true, false) as BaseButton
                                   ?? _rootPanel.FindChild("Back", true, false) as BaseButton;

                    if (_escapeButton != null)
                    {
                        _escapeButton.Pressed += () => FarmZoneManager.Instance.DeselectZone();
                    }

                    _checkMarkButton = _rootPanel.FindChild("CheckMark", true, false) as BaseButton
                                      ?? _rootPanel.FindChild("Checkmark", true, false) as BaseButton
                                      ?? _rootPanel.FindChild("CheckBox", true, false) as BaseButton;

                    if (_checkMarkButton != null)
                    {
                        _checkMarkButton.Pressed += OnCheckMarkPressed;
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

    private void OnZoneSelected(FarmZone zone)
    {
        if (_rootPanel == null || zone == null) return;

        if (_titleLabel != null)
        {
            _titleLabel.Text = $"{zone.Name} ({zone.TotalTiles} кл.)";
        }

        if (_infoLabel != null)
        {
            _infoLabel.Text = $"Зерно: 1/кл.";
        }

        UpdateCheckMarkState(zone.AutoPlantEnabled);

        _rootPanel.Visible = true;
        OnOpened?.Invoke();
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
        FarmZoneManager.Instance.OnZoneSelected -= OnZoneSelected;
        FarmZoneManager.Instance.OnZoneDeselected -= Close;
        base._ExitTree();
    }
}