using Godot;
using Game.Simulation;
using Game.UI;

namespace Game.UI.Tools;

public class SelectTool : ITool
{
    private readonly SelectionBox _selectionBox;
    private Vector2 _startMousePos;
    private bool _hasDragged;

    public SelectTool(SelectionBox selectionBox)
    {
        _selectionBox = selectionBox;
    }

    private ICellProbeOverlay _probe;

    /// <summary>
    /// Источник подсказок клетки (влажность/плодородие). Узкий интерфейс вместо
    /// HUDController.Instance — иначе цикл SelectTool → HUDController → PIM → SelectTool.
    /// </summary>
    public void BindCellProbe(ICellProbeOverlay probe) => _probe = probe;

    private int _lastHumidityX = int.MinValue;
    private int _lastHumidityY = int.MinValue;

    public void OnHover(Vector2I tilePos, Vector2 worldPos)
    {
        FarmZoneManager.Instance.SetHoveredTile(tilePos.X, tilePos.Y);
        // В режиме влажности — показывать % клетки под курсором.
        // Дёргаем HUD только при смене клетки: MouseMotion идёт каждый кадр,
        // а Get+Text при стоящем курсоре — зря жечь CPU.
        if (tilePos.X == _lastHumidityX && tilePos.Y == _lastHumidityY)
            return;
        _lastHumidityX = tilePos.X;
        _lastHumidityY = tilePos.Y;
        _probe?.ShowHumidityAt(tilePos.X, tilePos.Y);
        _probe?.ShowFertilityAt(tilePos.X, tilePos.Y);
    }

    // §113.5/B2: ПКМ в дефолтном инструменте ничего не делает — удаление
    // чертежей живёт только в окнах стройки (BuildTool/ZoneDraftTool и т.п.).

    public void OnClick(Vector2I tilePos, Vector2 worldPos, bool isLeftClick)
    {
        if (!isLeftClick)
            return;

        _startMousePos = worldPos;
        _hasDragged = false;
        _selectionBox?.ResetDefaultStyle();
        _selectionBox?.StartSelection(worldPos);
    }

    public void OnDrag(Vector2I startTile, Vector2I currentTile, Vector2 currentWorldPos)
    {
        if (_selectionBox != null && _selectionBox.IsSelecting)
        {
            if ((currentWorldPos - _startMousePos).LengthSquared() > 40.0f)
            {
                _hasDragged = true;
            }
            _selectionBox.UpdateSelection(currentWorldPos);
        }
    }

    public void OnRelease(Vector2I startTile, Vector2I endTile, Vector2 worldPos, bool isLeftClick)
    {
        // §113.5/B2: ПКМ-рамка дефолтного инструмента больше ничего не сносит
        // (раньше удаляла чертежи И построенное через WorldPlacement.TryRemove*).
        if (!isLeftClick)
            return;

        if (_selectionBox != null && _selectionBox.IsSelecting)
        {
            _selectionBox.EndSelection();
            _selectionBox.ResetDefaultStyle();
        }

        if (!_hasDragged)
        {
            // Единый ZoneManager: один выбор на все виды зон.
            // SelectZoneAt сам снимает выбор при промахе; контроллеры
            // открывают окно по OnZoneSelected (Garden — Farm, WorkZone — Work).
            ZoneManager.Instance.SelectZoneAt(endTile.X, endTile.Y);
        }
    }

    public void Cancel()
    {
        _selectionBox?.CancelSelection();
        _selectionBox?.ResetDefaultStyle();
        ZoneManager.Instance.SetHoveredTile(-1, -1);
        ZoneManager.Instance.DeselectZone();
    }
}