using Godot;
using Game.Core;
using Game.Simulation;
using Game.UI.Tools;
using System.Collections.Generic;

namespace Game.UI.Tools;

/// <summary>
/// Черновик зоны со стенами (режим окна стройки).
/// Мульти-прямоугольники: каждый завершённый ЛКМ-драг ДОПИСЫВАЕТСЯ к черновику,
/// старое не сносится. Ghost перестраивается живьём во время драга.
/// ПКМ во время драга — отменить только текущий драг (накопленное не трогаем).
/// DoorMode: ЛКМ-клик по ghost-стене снимает её из черновика (будущий проём).
/// Commit (CheckMark): зона по типу + стены чертежами WoodWall. Cancel: чистка ghost.
/// </summary>
public class ZoneDraftTool : ITool
{
    private readonly SelectionBox _selectionBox;
    private readonly MapData _mapData;
    private readonly TileMapLayer _ghostLayer;

    private readonly string _zoneKind;
    private string _wallMaterialId = "wood";
    private bool _doorMode;

    private bool _dragging;
    private Vector2I _dragStart;
    private Vector2I _dragCurrent;

    // Накопленный черновик (все завершённые прямоугольники) + активный драг.
    // Активный драг считается живьём из _dragStart/_dragCurrent и НЕ пишется
    // в множества до Release — поэтому второй ЛКМ ничего старого не сносит.

    private readonly HashSet<Vector2I> _zoneCells = new(1024);
    private readonly HashSet<Vector2I> _wallCells = new(1024);
    private readonly HashSet<Vector2I> _removedWalls = new(64);

    // Баг дублей: RebuildDraft чистил _zoneCells/_wallCells ДО ClearGhost, поэтому
    // стирались только новые клетки, а старые ghost-клетки оставались навсегда.
    // _painted — всё, что реально нарисовано в ghost-слое (зона+стены), стираем по нему.
    private readonly HashSet<Vector2I> _painted = new(2048);

    private static readonly Color ZoneFill = new(0.4f, 0.6f, 0.2f, 0.35f);
    private static readonly Color ZoneBorder = new(0.5f, 0.8f, 0.2f, 0.9f);

    public ZoneDraftTool(SelectionBox selectionBox, MapData mapData, TileMapLayer ghostLayer, string zoneKind)
    {
        _selectionBox = selectionBox;
        _mapData = mapData;
        _ghostLayer = ghostLayer;
        _zoneKind = zoneKind ?? "";
    }

    public bool DoorMode => _doorMode;
    public string ZoneKind => _zoneKind;
    public string WallMaterialId => _wallMaterialId;
    public bool HasDraft => _zoneCells.Count > 0 || _wallCells.Count > 0;

    public void SetWallMaterial(string id) => _wallMaterialId = string.IsNullOrEmpty(id) ? "wood" : id;
    public void SetDoorMode(bool enabled) => _doorMode = enabled;

    public void OnHover(Vector2I tilePos, Vector2 worldPos) { }

    public void OnClick(Vector2I tilePos, Vector2 worldPos, bool isLeftClick)
    {
        if (!isLeftClick)
        {
            // ПКМ: отменить только текущий драг (если идёт). Накопленный черновик
            // НЕ трогаем — пользователь такого не просил.
            if (_dragging)
            {
                _dragging = false;
                _selectionBox?.CancelSelection();
                _selectionBox?.ResetDefaultStyle();
                RepaintGhost();
            }
            return;
        }

        // Режим двери: клик по ghost-стене снимает её из черновика.
        if (_doorMode)
        {
            var p = tilePos;
            if (_wallCells.Remove(p))
            {
                _removedWalls.Add(p);
                _ghostLayer?.EraseCell(p);
                _painted.Remove(p);
            }
            return;
        }

        // Новый драг: старое НЕ чистим, дописываем по Release.
        _dragging = true;
        _dragStart = tilePos;
        _dragCurrent = tilePos;
        if (_selectionBox != null)
        {
            _selectionBox.SetStyle(ZoneFill, ZoneBorder);
            _selectionBox.StartSelection(worldPos);
        }
        RepaintGhost();
    }

    public void OnDrag(Vector2I startTile, Vector2I currentTile, Vector2 currentWorldPos)
    {
        if (!_dragging || _doorMode)
            return;
        _dragCurrent = currentTile;
        _selectionBox?.UpdateSelection(currentWorldPos);
        RepaintGhost();
    }

    public void OnRelease(Vector2I startTile, Vector2I endTile, Vector2 worldPos, bool isLeftClick)
    {
        if (!isLeftClick || !_dragging || _doorMode)
            return;
        _dragging = false;
        _dragCurrent = endTile;
        _selectionBox?.EndSelection();
        _selectionBox?.ResetDefaultStyle();
        // Фиксируем прямоугольник: дописать клетки, пересчитать кольцо, перекрасить.
        AccumulateRect(_dragStart, _dragCurrent);
        RecalcWalls();
        RepaintGhost();
    }

    public void Cancel()
    {
        _dragging = false;
        ClearGhost();
        _zoneCells.Clear();
        _wallCells.Clear();
        _removedWalls.Clear();
        _selectionBox?.CancelSelection();
        _selectionBox?.ResetDefaultStyle();
    }

    /// <summary>
    /// Дописать прямоугольник зоны в накопленный черновик (без стен).
    /// Непригодные клетки (вода/гора) отрезаются сразу — в черновик не попадают.
    /// </summary>
    private void AccumulateRect(Vector2I a, Vector2I b)
    {
        int minX = Mathf.Clamp(Mathf.Min(a.X, b.X), 0, MapRenderer.MapWidth - 1);
        int maxX = Mathf.Clamp(Mathf.Max(a.X, b.X), 0, MapRenderer.MapWidth - 1);
        int minY = Mathf.Clamp(Mathf.Min(a.Y, b.Y), 0, MapRenderer.MapHeight - 1);
        int maxY = Mathf.Clamp(Mathf.Max(a.Y, b.Y), 0, MapRenderer.MapHeight - 1);
        for (int x = minX; x <= maxX; x++)
            for (int y = minY; y <= maxY; y++)
                if (CanPlaceZone(x, y))
                    _zoneCells.Add(new Vector2I(x, y));
    }

    /// <summary>
    /// Пересчитать кольцо стен по ВСЕМ клеткам зоны: замкнутый контур с углами —
    /// каждая клетка зоны даёт своих 8 соседей (с дедупом), клетки зоны исключены,
    /// снятые дверью и непригодные — исключены.
    /// </summary>
    /// <summary>Фермерству стены не положены: кольцо не генерируем вообще.</summary>
    private bool WallsForbidden => _zoneKind == "farming" || _zoneKind == "farm";

    private void RecalcWalls()
    {
        _wallCells.Clear();
        if (WallsForbidden)
            return;
        foreach (var c in _zoneCells)
        {
            for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                {
                    if (ox == 0 && oy == 0)
                        continue;
                    int x = c.X + ox, y = c.Y + oy;
                    if (x < 0 || y < 0 || x >= MapRenderer.MapWidth || y >= MapRenderer.MapHeight)
                        continue;
                    var p = new Vector2I(x, y);
                    if (_zoneCells.Contains(p))
                        continue;
                    if (_removedWalls.Contains(p))
                        continue;
                    if (!CanPlaceWall(x, y))
                        continue;
                    _wallCells.Add(p);
                }
        }
    }

    /// <summary>
    /// Перерисовать ghost: накопленное + активный драг (если идёт) живьём.
    /// Множества НЕ трогаем — только рисуем.
    /// </summary>
    // Ghost-превью стен: движковый автотайлинг (как в BuildTool) —
    // SetCellsTerrainConnect сам считает состыковку, код не нужен.
    private const int GhostTerrainSetId = 0;
    private const int GhostWallTerrainId = 0;

    private void PaintWallGhost(IEnumerable<Vector2I> cells)
    {
        if (_ghostLayer == null || _ghostLayer.TileSet == null) return;
        var arr = new Godot.Collections.Array<Vector2I>();
        foreach (var c in cells)
            arr.Add(c);
        if (arr.Count == 0) return;
        _ghostLayer.SetCellsTerrainConnect(arr, GhostTerrainSetId, GhostWallTerrainId, true);
        foreach (var c in cells)
            _painted.Add(c);
    }

    private void RepaintGhost()
    {
        if (_ghostLayer == null)
            return;
        ClearGhost();
        // Зону рисуем ghost-Source (дубликат TileSet с травой/грядкой).
        // SourceGrass/SourceGardenBed здесь НЕЛЬЗЯ: в ghost-наборе под этими
        // номерами сидят стены — призрак склада заливался стенами.
        int zoneSource = MapRenderer.GhostSourceForZone(_zoneKind);
        foreach (var c in _zoneCells)
        {
            _ghostLayer.SetCell(c, zoneSource, Vector2I.Zero);
            _painted.Add(c);
        }
        if (!WallsForbidden)
        {
            PaintWallGhost(_wallCells);
        }
        // Активный драг — превью поверх накопленного (в множества не пишем).
        if (_dragging)
        {
            int minX = Mathf.Clamp(Mathf.Min(_dragStart.X, _dragCurrent.X), 0, MapRenderer.MapWidth - 1);
            int maxX = Mathf.Clamp(Mathf.Max(_dragStart.X, _dragCurrent.X), 0, MapRenderer.MapWidth - 1);
            int minY = Mathf.Clamp(Mathf.Min(_dragStart.Y, _dragCurrent.Y), 0, MapRenderer.MapHeight - 1);
            int maxY = Mathf.Clamp(Mathf.Max(_dragStart.Y, _dragCurrent.Y), 0, MapRenderer.MapHeight - 1);
            for (int x = minX; x <= maxX; x++)
                for (int y = minY; y <= maxY; y++)
                {
                    var p = new Vector2I(x, y);
                    _ghostLayer.SetCell(p, zoneSource, Vector2I.Zero);
                    _painted.Add(p);
                }
            // Кольцо превью: соседи прямоугольника драга (для фермы — нет кольца).
            if (!WallsForbidden)
            {
                var ring = new List<Vector2I>();
                for (int x = minX - 1; x <= maxX + 1; x++)
                    for (int y = minY - 1; y <= maxY + 1; y++)
                    {
                        if (x < 0 || y < 0 || x >= MapRenderer.MapWidth || y >= MapRenderer.MapHeight)
                            continue;
                        bool inside = x >= minX && x <= maxX && y >= minY && y <= maxY;
                        if (inside)
                            continue;
                        var p = new Vector2I(x, y);
                        if (_removedWalls.Contains(p) || !CanPlaceWall(x, y))
                            continue;
                        ring.Add(p);
                    }
                PaintWallGhost(ring);
            }
        }
    }

    /// <summary>Клетка пригодна под зону: только трава (вода/гора запрещены).</summary>
    private bool CanPlaceZone(int x, int y)
    {
        if (_mapData == null)
            return x >= 0 && y >= 0 && x < MapRenderer.MapWidth && y < MapRenderer.MapHeight;
        if ((uint)x >= (uint)_mapData.Width || (uint)y >= (uint)_mapData.Height)
            return false;
        return _mapData.Ground[x, y] == TileType.Grass;
    }

    private bool CanPlaceWall(int x, int y)
    {
        if (_mapData != null && _mapData.Ground[x, y] != TileType.Grass)
            return false;
        // Камень НЕ исключаем: этап 1 пайплайна его добудет (Mining), стена встанет после.
        var walls = MapRenderer.Instance?.WallBuildManager;
        if (walls != null && walls.IsWallAt(x, y))
            return false;
        if (BuildingManager.Instance.HasBuildingAt(x, y))
            return false;
        if (BlueprintManager.Instance.IsBlueprintAt(x, y))
            return false;
        // Рукотворные клетки не сносим: грядка под стеной — стена не ставится.
        if (FarmJobManager.Instance.IsGardenBed(x, y) || FarmJobManager.Instance.IsPlotMarked(x, y))
            return false;
        return true;
    }

    private void ClearGhost()
    {
        if (_ghostLayer == null)
            return;
        foreach (var p in _painted)
            _ghostLayer.EraseCell(p);
        _painted.Clear();
        foreach (var r in _removedWalls)
            _ghostLayer.EraseCell(r);
    }

    /// <summary>
    /// Подтверждение (CheckMark): зона ставится СРАЗУ (её не строят), стены идут
    /// в трёхэтапный пайплайн: 1) расчистка → 2) чертежи+доставка → 3) стройка.
    /// Запреты: зона не ставится внутри зданий/на рукотворных клетках; в пределах
    /// одной стройки запрещены раздельные зоны (второй коммит того же типа поверх
    /// стройки — только через новый черновик, пересечения отрезаются здесь).
    /// </summary>
    public bool Commit()
    {
        if (!HasDraft)
            return false;
        var zoneList = new List<(int X, int Y)>(_zoneCells.Count);
        foreach (var c in _zoneCells)
        {
            // Повторная защита на коммите: терраин мог измениться после драга
            // (река/терраформинг) — воду/гору отрезаем и здесь.
            if (!CanPlaceZone(c.X, c.Y))
                continue;
            // Зона внутри здания / на грядке / на чертеже — не ставим.
            if (BuildingManager.Instance.HasBuildingAt(c.X, c.Y)
                || FarmJobManager.Instance.IsGardenBed(c.X, c.Y)
                || FarmJobManager.Instance.IsPlotMarked(c.X, c.Y)
                || BlueprintManager.Instance.IsBlueprintAt(c.X, c.Y)
                || StockpileManager.Instance.IsZoneTile(c.X, c.Y))
                continue;
            zoneList.Add((c.X, c.Y));
        }
        var wallList = new List<(int X, int Y)>(_wallCells.Count);
        foreach (var w in _wallCells)
            wallList.Add((w.X, w.Y));

        if (zoneList.Count > 0)
        {
            if (_zoneKind == "warehouse")
            {
                StockpileManager.Instance.AddZoneTilesBatch(zoneList);
            }
            else
            {
                FarmJobManager.Instance.MarkPlotsBatch(zoneList, _mapData?.TreeOnGrass, _mapData?.StoneOnGrass);
                FarmZoneManager.Instance.CreateZone(zoneList);
            }
        }

        if (wallList.Count > 0 && !WallsForbidden)
        {
            var mat = WallMaterialRegistry.ById(_wallMaterialId);
            ConstructionPipeline.Instance.StartSite(wallList, mat.Type, _mapData);
        }

        ClearGhost();
        _zoneCells.Clear();
        _wallCells.Clear();
        _removedWalls.Clear();
        _dragging = false;
        _selectionBox?.CancelSelection();
        _selectionBox?.ResetDefaultStyle();
        return true;
    }

    /// <summary>
    /// Отмена окна по крестику: убрать ЧЕРТЕЖИ стен черновика (не трогая чужие),
    /// удалить зону-черновик, почистить ghost.
    /// </summary>
    public void Discard()
    {
        if (_wallCells.Count > 0)
        {
            var walls = new List<(int X, int Y)>(_wallCells.Count);
            foreach (var w in _wallCells)
                walls.Add((w.X, w.Y));
            BlueprintManager.Instance.RemoveBlueprintsBatch(walls);
        }
        if (_zoneCells.Count > 0)
        {
            var zones = new List<(int X, int Y)>(_zoneCells.Count);
            foreach (var c in _zoneCells)
                zones.Add((c.X, c.Y));
            if (_zoneKind == "warehouse")
                StockpileManager.Instance.RemoveZoneTilesBatch(zones);
            else
            {
                FarmJobManager.Instance.UnmarkPlotsBatch(zones);
                FarmZoneManager.Instance.RemoveTiles(zones);
            }
        }
        Cancel();
    }
}
