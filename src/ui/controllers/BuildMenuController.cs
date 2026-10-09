using Godot;
using System;
using System.Collections.Generic;
using Game.UI.Tools;

namespace Game.UI;

/// <summary>
/// Универсальное окно строительства (Build.tscn).
/// Открывается при выборе зоны в HUD (ZoneContainer: ферма, склад, будущие).
/// Верхний HBox — режимы: DoorButton (тумблер проёма), WallMaterialButton (попап
/// материалов), ZoneButton (черновик зоны+стен в мире, окно остаётся открытым).
/// Нижний список — программное содержимое по зонам (ряды по 5 кнопок).
/// CheckMark — коммит черновика (зона по типу + стены чертежами), окно закрыть.
/// </summary>
public partial class BuildMenuController : Control
{
    public static BuildMenuController Instance { get; private set; }

    /// <summary>Сколько кнопок максимум в одном HBox-ряду нижнего списка.</summary>
    public const int MaxButtonsPerRow = 5;

    /// <summary>Отступ между кнопками в нижнем списке (как в сцене у HBoxContainer2).</summary>
    public const int RowSeparation = 63;

    /// <summary>Попап материалов: ряды по 5 кнопок 50x50, отступ 10.</summary>
    public const int MaterialPerRow = 5;

    public const int MaterialSeparation = 10;
    public const int MaterialButtonSize = 50;

    private Control _rootPanel;
    private VBoxContainer _vbox;
    private Label _titleLabel;
    private BaseButton _escButton;
    private BaseButton _checkMarkButton;

    /// <summary>Кнопка-тумблер «Дверь». Управление режимом полностью на коде (_doorMode +
    /// OnDoorPressed): встроенный toggle_mode сцены гасим в LoadScene, а pressed-состояние
    /// синхронизируем вручную (UpdateModeButtons/ResetDoorMode) — иначе двойное управление
    /// разъезжает визуальное «нажатие» с фактическим режимом.</summary>
    private Button _doorButton;
    private Button _wallMaterialButton;
    private Button _zoneButton;

    /// <summary>Шаблонный ряд из сцены (HBoxContainer2 с 5 заглушками). Не удаляем, прячем при динамике.</summary>
    private HBoxContainer _templateRow;

    /// <summary>Динамически созданные ряды (для очистки при Rebuild).</summary>
    private readonly List<HBoxContainer> _dynamicRows = new();

    // Попап материалов: панель поверх окна, тумблер кнопкой, клик мимо закрывает.
    private PanelContainer _materialPopup;
    private VBoxContainer _materialRows;
    private string _selectedMaterialId = "wood";
    private readonly Dictionary<string, Button> _materialButtons = new();

    private string _currentZoneId = "";
    public string CurrentZoneId => _currentZoneId;
    public string SelectedMaterialId => _selectedMaterialId;

    private bool _doorMode;
    public bool DoorMode => _doorMode;

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
    }

    private void LoadScene()
    {
        var paths = new[]
        {
            "res://scenes/ui/Build.tscn",
            "res://ui/Build.tscn",
            "res://Build.tscn"
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

            _titleLabel = _rootPanel.FindChild("Name", true, false) as Label;
            _vbox = _rootPanel.FindChild("VBoxContainer", true, false) as VBoxContainer;
            _templateRow = _rootPanel.FindChild("HBoxContainer2", true, false) as HBoxContainer;

            _doorButton = _rootPanel.FindChild("DoorButton", true, false) as Button;
            _wallMaterialButton = _rootPanel.FindChild("WallMaterialButton", true, false) as Button;
            _zoneButton = _rootPanel.FindChild("ZoneButton", true, false) as Button;

            if (_doorButton != null)
            {
                _doorButton.MouseFilter = MouseFilterEnum.Stop;
                // FIX (рассинхрон тумблера «Дверь»): в Build.tscn у DoorButton включён
                // встроенный toggle_mode — Godot сам меняет ButtonState при клике, а
                // OnDoorPressed ведёт собственный флаг _doorMode. Двойное управление:
                // если внешние вызовы (ResetDoorMode/ApplyZoneVisibility) не синхронизированы
                // с визуальным pressed-состоянием кнопки, следующий клик даёт ложную
                // инверсию — режим и подсветка разъезжаются. Оставляем ЕДИНСТВЕННЫЙ источник
                // истины — код (_doorMode), а сценический toggle гасим.
                _doorButton.ToggleMode = false;
                _doorButton.ButtonPressed = false;
                _doorButton.Pressed += OnDoorPressed;
            }
            if (_wallMaterialButton != null)
            {
                _wallMaterialButton.MouseFilter = MouseFilterEnum.Stop;
                _wallMaterialButton.Pressed += ToggleMaterialPopup;
            }
            if (_zoneButton != null)
            {
                _zoneButton.MouseFilter = MouseFilterEnum.Stop;
                _zoneButton.Pressed += OnZoneBuildPressed;
            }

            _escButton = _rootPanel.FindChild("esc", true, false) as BaseButton
                         ?? _rootPanel.FindChild("Escape", true, false) as BaseButton
                         ?? _rootPanel.FindChild("Close", true, false) as BaseButton;
            if (_escButton != null)
                _escButton.Pressed += OnDiscardPressed;

            _checkMarkButton = _rootPanel.FindChild("CheckMark", true, false) as BaseButton
                               ?? _rootPanel.FindChild("Checkmark", true, false) as BaseButton;
            if (_checkMarkButton != null)
                _checkMarkButton.Pressed += OnCheckMarkPressed;

            BuildMaterialPopup();
            break;
        }

        if (_rootPanel == null)
            GD.PushWarning("[BuildMenu] Build.tscn не найден ни по одному пути.");
    }

    private static void ConfigureInputBlocking(Control node)
    {
        if (node == null)
            return;
        if (node is Panel || node is PanelContainer || node is ScrollContainer || node is ItemList)
            node.MouseFilter = MouseFilterEnum.Stop;
        foreach (Node child in node.GetChildren())
        {
            if (child is Control childCtrl)
                ConfigureInputBlocking(childCtrl);
        }
    }

    /// <summary>
    /// Открыть окно для зоны. zoneId: "farming", "warehouse", будущие.
    /// Повторный вызов с тем же zoneId — обновить заголовок, окно остаётся открытым.
    /// Для фермы прячем материал стен и дверь (стен у фермы нет).
    /// </summary>
    public void Open(string zoneId, string zoneTitle)
    {
        if (_rootPanel == null)
            return;
        _currentZoneId = zoneId ?? "";
        if (_titleLabel != null)
            _titleLabel.Text = string.IsNullOrEmpty(zoneTitle) ? "Строительство" : zoneTitle;

        ApplyZoneVisibility();
        _rootPanel.Visible = true;
        OnOpened?.Invoke();
    }

    /// <summary>
    /// У фермы нет стен — прячем кнопки материала и двери (только для фермы,
    /// склад и остальные — как было). Сцену не трогаем, только Visible.
    /// </summary>
    private void ApplyZoneVisibility()
    {
        bool isFarm = _currentZoneId == "farming" || _currentZoneId == "farm";
        if (_wallMaterialButton != null)
            _wallMaterialButton.Visible = !isFarm;
        if (_doorButton != null)
            _doorButton.Visible = !isFarm;
        if (isFarm)
            ResetDoorMode();
    }

    public void Close()
    {
        CloseMaterialPopup();
        if (_rootPanel != null && _rootPanel.Visible)
        {
            _rootPanel.Visible = false;
            _currentZoneId = "";
            OnClosed?.Invoke();
        }
    }

    /// <summary>Закрыть окно без OnClosed (чтобы HUD не трогал другие меню при Discard/esc).</summary>
    private void CloseSilent()
    {
        CloseMaterialPopup();
        if (_rootPanel != null)
            _rootPanel.Visible = false;
        _currentZoneId = "";
    }

    /// <summary>esc: отмена черновика (ничего в мир не ставили) + закрыть окно.</summary>
    private void OnEscPressed()
    {
        CancelDraft();
        CloseSilent();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && _materialPopup != null && _materialPopup.Visible)
        {
            // Клик мимо попапа — закрыть (попап поверх окна, Stop-фильтр).
            Vector2 mp = _materialPopup.GetGlobalMousePosition();
            if (!_materialPopup.GetGlobalRect().HasPoint(mp))
                CloseMaterialPopup();
        }
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape && IsOpen)
        {
            CancelDraft();
            CloseSilent();
            GetViewport().SetInputAsHandled();
        }
    }

    // --- Режимы (только ОДИН активен: зона-режим или дверь-режим) ---

    /// <summary>
    /// ZoneButton: включить черновик зоны+стен, окно остаётся открытым.
    /// Повторный нажим — выключить (тумблер). Включение зоны гасит дверь-режим.
    /// </summary>
    private void OnZoneBuildPressed()
    {
        var hud = HUDController.Instance;
        bool nowDrafting = hud != null && hud.IsDrafting;
        if (nowDrafting)
        {
            // Был черновик — выключаем (инструмент в обычный, призрак гаснет).
            hud.CancelDraft();
            ResetDoorMode();
            hud.SetDraftDoorMode(false);
            UpdateModeButtons();
            return;
        }
        // Включаем зону, дверь-режим гасим (два режима сразу нельзя).
        ResetDoorMode();
        hud?.SetDraftDoorMode(false);
        hud?.StartZoneDraft(_currentZoneId);
        UpdateModeButtons();
    }

    /// <summary>
    /// DoorButton: тумблер режима проёма (клик по ghost-стене снимает её).
    /// Включение двери гасит зону-режим черчения (два режима сразу нельзя).
    /// </summary>
    private void OnDoorPressed()
    {
        _doorMode = !_doorMode;
        var hud = HUDController.Instance;
        if (_doorMode)
        {
            // Дверь включилась — зону-черчение останавливаем (черновик живёт).
            hud?.StopDraftDrawing();
        }
        hud?.SetDraftDoorMode(_doorMode);
        UpdateModeButtons();
    }

    /// <summary>Подсветка: активный режим — красным, неактивный — белым.</summary>
    private void UpdateModeButtons()
    {
        var hud = HUDController.Instance;
        bool drafting = hud != null && hud.IsDrafting && !_doorMode;
        if (_zoneButton != null)
            _zoneButton.Modulate = drafting ? new Color(0.6f, 1f, 0.6f, 1f) : Color.Color8(255, 255, 255, 255);
        if (_doorButton != null)
        {
            _doorButton.Modulate = _doorMode ? new Color(1f, 0.6f, 0.6f, 1f) : Color.Color8(255, 255, 255, 255);
            // Синхронизируем pressed-состояние с ЕДИНСТВЕННЫМ источником истины (_doorMode).
            // ToggleMode выключен в LoadScene, поэтому ButtonPressed меняем сами: иначе
            // визуальное «нажатие» разъезжается с фактическим режимом после внешних сбросов.
            _doorButton.ButtonPressed = _doorMode;
        }
    }

    internal void ResetDoorMode()
    {
        _doorMode = false;
        if (_doorButton != null)
        {
            _doorButton.Modulate = Color.Color8(255, 255, 255, 255);
            _doorButton.ButtonPressed = false;
        }
    }

    /// <summary>CheckMark: коммит черновика, окно закрыть, инструмент сбросить.</summary>
    private void OnCheckMarkPressed()
    {
        var hud = HUDController.Instance;
        hud?.ConfirmDraft();
    }

    private void CancelDraft()
    {
        var hud = HUDController.Instance;
        hud?.CancelDraft();
        ResetDoorMode();
    }

    /// <summary>Крестик окна (esc-кнопка сцены): удалить поставленные чертежи+зону.</summary>
    private void OnDiscardPressed()
    {
        var hud = HUDController.Instance;
        hud?.DiscardDraft();
        ResetDoorMode();
        CloseSilent();
    }

    // --- Попап материалов ---

    private void BuildMaterialPopup()
    {
        if (_rootPanel == null)
            return;
        _materialPopup = new PanelContainer { Name = "MaterialPopup", Visible = false };
        _materialPopup.MouseFilter = MouseFilterEnum.Stop;
        _materialPopup.ZIndex = 50;
        _materialRows = new VBoxContainer();
        _materialRows.AddThemeConstantOverride("separation", MaterialSeparation);
        _materialPopup.AddChild(_materialRows);
        _rootPanel.AddChild(_materialPopup);
        RebuildMaterialPopup();
        UpdateMaterialSelection();
    }

    private void RebuildMaterialPopup()
    {
        if (_materialRows == null)
            return;
        foreach (Node c in _materialRows.GetChildren())
            c.QueueFree();
        _materialButtons.Clear();

        HBoxContainer row = null;
        int inRow = 0;
        foreach (var mat in WallMaterialRegistry.All)
        {
            if (row == null || inRow >= MaterialPerRow)
            {
                row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", MaterialSeparation);
                row.MouseFilter = MouseFilterEnum.Stop;
                _materialRows.AddChild(row);
                inRow = 0;
            }
            var btn = new Button
            {
                Text = mat.Title,
                CustomMinimumSize = new Vector2(MaterialButtonSize, MaterialButtonSize),
                MouseFilter = MouseFilterEnum.Stop,
                TooltipText = mat.Title
            };
            string id = mat.Id;
            btn.Pressed += () => SelectMaterial(id);
            row.AddChild(btn);
            _materialButtons[id] = btn;
            inRow++;
        }
    }

    private void ToggleMaterialPopup()
    {
        if (_materialPopup == null)
            return;
        if (_materialPopup.Visible)
        {
            CloseMaterialPopup();
            return;
        }
        RebuildMaterialPopup();
        UpdateMaterialSelection();
        // Позиция: под кнопкой материала, поверх всего.
        Vector2 at = _wallMaterialButton != null
            ? _wallMaterialButton.GetGlobalRect().Position + new Vector2(0, _wallMaterialButton.Size.Y + 8)
            : new Vector2(120, 200);
        _materialPopup.Position = at;
        _materialPopup.Visible = true;
    }

    private void CloseMaterialPopup()
    {
        if (_materialPopup != null)
            _materialPopup.Visible = false;
    }

    private void SelectMaterial(string id)
    {
        _selectedMaterialId = string.IsNullOrEmpty(id) ? "wood" : id;
        UpdateMaterialSelection();
        var hud = HUDController.Instance;
        hud?.SetDraftMaterial(_selectedMaterialId);
        CloseMaterialPopup();
    }

    private void UpdateMaterialSelection()
    {
        foreach (var kv in _materialButtons)
        {
            bool sel = kv.Key == _selectedMaterialId;
            kv.Value.Modulate = sel ? new Color(0.6f, 1f, 0.6f, 1f) : Color.Color8(255, 255, 255, 255);
        }
    }

    /// <summary>
    /// Программная пересборка нижнего списка: по максимум MaxButtonsPerRow кнопок в HBox,
    /// каждый следующий ряд — новый HBoxContainer с separation RowSeparation.
    /// Пустой список — вернуть шаблон сцены (5 заглушек) как есть.
    /// </summary>
    public void RebuildButtons(IReadOnlyList<BuildMenuItem> items)
    {
        if (_vbox == null)
            return;
        foreach (var row in _dynamicRows)
            row.QueueFree();
        _dynamicRows.Clear();

        if (items == null || items.Count == 0)
        {
            if (_templateRow != null)
                _templateRow.Visible = true;
            return;
        }

        // Есть динамика — шаблон прячем, строим ряды по 5.
        if (_templateRow != null)
            _templateRow.Visible = false;

        HBoxContainer currentRow = null;
        int inRow = 0;
        foreach (var item in items)
        {
            if (currentRow == null || inRow >= MaxButtonsPerRow)
            {
                currentRow = new HBoxContainer();
                currentRow.AddThemeConstantOverride("separation", RowSeparation);
                _vbox.AddChild(currentRow);
                _dynamicRows.Add(currentRow);
                inRow = 0;
            }
            var btn = new Button
            {
                Text = item.Title,
                CustomMinimumSize = new Vector2(50, 50),
                MouseFilter = MouseFilterEnum.Stop
            };
            btn.Pressed += () => GetViewport().SetInputAsHandled();
            currentRow.AddChild(btn);
            inRow++;
        }
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
        base._ExitTree();
    }
}

/// <summary>Одна кнопка стройобъекта в универсальном окне. Реестр зона->кнопки добавится позже.</summary>
public readonly struct BuildMenuItem
{
    public readonly string Id;
    public readonly string Title;
    public BuildMenuItem(string id, string title)
    {
        Id = id;
        Title = title;
    }
}
