using Godot;
using Game.Core;
using Game.Simulation;
using Game.UI.Tools;
using System;
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
    // §28: клетки черновика за кромкой острова (мир) — зона и кольцо стен.
    private readonly HashSet<Vector2I> _worldZoneCells = new(1024);
    private readonly HashSet<Vector2I> _worldWallCells = new(1024);
    private readonly HashSet<Vector2I> _removedWalls = new(64);

    // Баг дублей: RebuildDraft чистил _zoneCells/_wallCells ДО ClearGhost, поэтому
    // стирались только новые клетки, а старые ghost-клетки оставались навсегда.
    // _painted — всё, что реально нарисовано в ghost-слое (зона+стены), стираем по нему.
    private readonly HashSet<Vector2I> _painted = new(2048);
    private readonly List<Vector2I> _ringBuffer = new(256);

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
    public bool HasDraft => _zoneCells.Count > 0 || _wallCells.Count > 0
        || _worldZoneCells.Count > 0 || _worldWallCells.Count > 0;

    public void SetWallMaterial(string id) => _wallMaterialId = string.IsNullOrEmpty(id) ? "wood" : id;

    /// <summary>Мир за кромкой острова (§28): зона и кольцо стен ставятся метками мира.</summary>
    public IToolWorldPlacement WorldPlacement { get; set; }
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
            if (_wallCells.Remove(p) || _worldWallCells.Remove(p))
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

    /// <summary>
    /// Прервать активное черчение, НЕ снося черновик: включили режим двери —
    /// текущий драг сбрасываем, накопленные прямоугольники и ghost остаются
    /// (пользователь вернётся в зона-режим и продолжит дописывать).
    /// </summary>
    public void StopDrawing()
    {
        if (!_dragging)
            return;
        _dragging = false;
        _selectionBox?.CancelSelection();
        _selectionBox?.ResetDefaultStyle();
        // RepaintGhost рисует накопленное уже без активного драга.
        RepaintGhost();
    }

    public void Cancel()
    {
        _dragging = false;
        ClearGhost();
        _zoneCells.Clear();
        _wallCells.Clear();
        _removedWalls.Clear();
        _worldZoneCells.Clear();
        _worldWallCells.Clear();
        _selectionBox?.CancelSelection();
        _selectionBox?.ResetDefaultStyle();
    }

    /// <summary>
    /// Дописать прямоугольник зоны в накопленный черновик (без стен).
    /// Непригодные клетки (вода/гора) отрезаются сразу — в черновик не попадают.
    /// </summary>
    private void AccumulateRect(Vector2I a, Vector2I b)
    {
        // §28: выделение больше не клампится к острову — клетки за кромкой копятся
        // отдельно и на Commit уходят метками мира (зона склада/фермы, кольцо стен).
        int minX = Math.Min(a.X, b.X);
        int maxX = Math.Max(a.X, b.X);
        int minY = Math.Min(a.Y, b.Y);
        int maxY = Math.Max(a.Y, b.Y);
        long scanned = 0;
        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                if (++scanned > WorldPlacementPass.MaxScannedCells)
                    return;
                if (WorldPlacementPass.IsIslandCell(x, y))
                {
                    if (CanCommitZoneCell(x, y))
                        _zoneCells.Add(new Vector2I(x, y));
                }
                else if (WorldPlacement != null && WorldPlacement.IsWorldCell(x, y))
                {
                    // Лимит, как у остальных инструментов: иначе один драг 500×500 положит
                    // сотни тысяч меток мира за один Commit (фриз + раздутая дельта).
                    if (_worldZoneCells.Count >= WorldPlacementPass.MaxAppliedCells)
                        return;
                    // Гейт тот же, что на Commit: в черновик (и в ghost) попадает только то,
                    // что реально встанет — вода/гора/сухая земля за кромкой не обещаются.
                    bool warehouse = _zoneKind == "warehouse";
                    if (warehouse ? WorldPlacement.CanPlaceStockpile(x, y) : WorldPlacement.CanPlaceFarm(x, y))
                        _worldZoneCells.Add(new Vector2I(x, y));
                }
            }
        }
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
        _worldWallCells.Clear();
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
                    var p = new Vector2I(x, y);
                    if (_zoneCells.Contains(p) || _wallCells.Contains(p) || _worldWallCells.Contains(p))
                        continue;
                    if (_removedWalls.Contains(p))
                        continue;
                    if (WorldPlacementPass.IsIslandCell(x, y))
                    {
                        if (!CanPlaceWall(x, y))
                            continue;
                        _wallCells.Add(p);
                        continue;
                    }
                    // Стык с миром: у зоны, упирающейся в кромку острова, кольцо раньше
                    // обрывалось — мировые соседи островных клеток пропускались.
                    if (WorldPlacement != null && WorldPlacement.IsWorldCell(x, y))
                        _worldWallCells.Add(p);
                }
        }

        // Кольцо стен вокруг мировых клеток зоны (§28) — клетки за кромкой острова.
        if (_worldZoneCells.Count == 0 || WorldPlacement == null)
            return;
        foreach (Vector2I c in _worldZoneCells)
        {
            for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                {
                    if (ox == 0 && oy == 0)
                        continue;
                    var p = new Vector2I(c.X + ox, c.Y + oy);
                    if (_worldZoneCells.Contains(p) || _worldWallCells.Contains(p))
                        continue;
                    if (_removedWalls.Contains(p))
                        continue;
                    // Стык с островом: мировая зона, упёршаяся в кромку, получает стену и на
                    // островной стороне — иначе кольцо обрывалось ровно по кромке.
                    if (WorldPlacementPass.IsIslandCell(p.X, p.Y))
                    {
                        if (CanPlaceWall(p.X, p.Y))
                            _wallCells.Add(p);
                        continue;
                    }
                    if (!WorldPlacement.IsWorldCell(p.X, p.Y))
                        continue;
                    if (!WorldPlacement.CanPlaceWall(p.X, p.Y))
                        continue; // ghost без гейта обещал стену на воде — Commit её пропускал
                    _worldWallCells.Add(p);
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
        // §28: мировые клетки черновика тоже рисуем — иначе за кромкой «зона не ставится»:
        // она ставилась, но её не было видно.
        foreach (var c in _worldZoneCells)
        {
            _ghostLayer.SetCell(c, zoneSource, Vector2I.Zero);
            _painted.Add(c);
        }
        if (!WallsForbidden)
        {
            PaintWallGhost(_wallCells);
            PaintWallGhost(_worldWallCells);
        }
        // Активный драг — превью поверх накопленного (в множества не пишем).
        if (_dragging)
        {
            // ПЕРЕСЕЧЕНИЕ с островом, а не кламп: драг целиком за кромкой рисовал фантомную
            // клетку (0,0) и кольцо стен, которых в черновике нет.
            int minX = Mathf.Max(Mathf.Min(_dragStart.X, _dragCurrent.X), 0);
            int maxX = Mathf.Min(Mathf.Max(_dragStart.X, _dragCurrent.X), MapRenderer.MapWidth - 1);
            int minY = Mathf.Max(Mathf.Min(_dragStart.Y, _dragCurrent.Y), 0);
            int maxY = Mathf.Min(Mathf.Max(_dragStart.Y, _dragCurrent.Y), MapRenderer.MapHeight - 1);
            if (minX > maxX || minY > maxY)
            {
                // Островной части у драга нет — рисуем только мир.
                PaintWorldDragPreview(zoneSource);
                return;
            }
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
                _ringBuffer.Clear();
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
                        _ringBuffer.Add(p);
                    }
                PaintWallGhost(_ringBuffer);
            }
            // Мировая часть активного драга: только клетки, которые пройдут гейт мира.
            PaintWorldDragPreview(zoneSource);
        }
    }

    /// <summary>Превью мировых клеток активного драга — по тем же гейтам, что на Commit.</summary>
    private void PaintWorldDragPreview(int zoneSource)
    {
        if (WorldPlacement == null)
            return;
        bool warehouse = _zoneKind == "warehouse";
        int minX = Math.Min(_dragStart.X, _dragCurrent.X);
        int maxX = Math.Max(_dragStart.X, _dragCurrent.X);
        int minY = Math.Min(_dragStart.Y, _dragCurrent.Y);
        int maxY = Math.Max(_dragStart.Y, _dragCurrent.Y);
        long scanned = 0;
        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                if (++scanned > WorldPlacementPass.MaxScannedCells)
                    return;
                if (WorldPlacementPass.IsIslandCell(x, y))
                    continue;
                if (!WorldPlacement.IsWorldCell(x, y))
                    continue;
                if (!(warehouse ? WorldPlacement.CanPlaceStockpile(x, y) : WorldPlacement.CanPlaceFarm(x, y)))
                    continue;
                var p = new Vector2I(x, y);
                _ghostLayer.SetCell(p, zoneSource, Vector2I.Zero);
                _painted.Add(p);
            }
        }
    }

    /// <summary>
    /// Клетка островной зоны, которая реально встанет на Commit. Один гейт для ghost и Commit:
    /// раньше ghost показывал траву, а Commit молча отбрасывал клетку в здании/на грядке/
    /// на чертеже/в чужой зоне — «поставил», и ничего не появлялось.
    /// </summary>
    private bool CanCommitZoneCell(int x, int y)
        => CanPlaceZone(x, y)
            && !BuildingManager.Instance.HasBuildingAt(x, y)
            && !FarmJobManager.Instance.IsGardenBed(x, y)
            && !FarmJobManager.Instance.IsPlotMarked(x, y)
            && !BlueprintManager.Instance.IsBlueprintAt(x, y)
            && !StockpileManager.Instance.IsZoneTile(x, y);

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
            // Общий гейт с ghost (CanCommitZoneCell): терраин мог измениться после драга,
            // плюс зона внутри здания / на грядке / на чертеже / на чужой зоне — не ставим.
            if (!CanCommitZoneCell(c.X, c.Y))
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

        // §28: мировая часть черновика — зона (метки посева/склада) и кольцо стен.
        if (_worldZoneCells.Count > 0 && WorldPlacement != null)
        {
            bool warehouse = _zoneKind == "warehouse";
            foreach (Vector2I c in _worldZoneCells)
            {
                if (warehouse)
                    WorldPlacement.TryPlaceStockpile(c.X, c.Y);
                else
                    WorldPlacement.TryPlaceFarmPlot(c.X, c.Y);
            }
        }
        if (_worldWallCells.Count > 0 && !WallsForbidden && WorldPlacement != null)
        {
            foreach (Vector2I w in _worldWallCells)
                WorldPlacement.TryPlaceWall(w.X, w.Y);
        }

        ClearGhost();
        _zoneCells.Clear();
        _wallCells.Clear();
        _removedWalls.Clear();
        _worldZoneCells.Clear();
        _worldWallCells.Clear();
        _dragging = false;
        _selectionBox?.CancelSelection();
        _selectionBox?.ResetDefaultStyle();
        return true;
    }

    /// <summary>
    /// Отмена окна по крестику. Черновик ДО Commit в мир ничего не ставит: _zoneCells/_wallCells —
    /// это только ghost-превью, поэтому сносить по ним существующие зоны и чертежи нельзя
    /// (крестик стирал чужую складскую зону/грядки, попавшие под драг). После Commit инструмент
    /// отбрасывается (HUDController.ConfirmDraft → _draftTool = null), так что сюда попадает
    /// только отмена ещё не подтверждённого превью.
    /// </summary>
    public void Discard() => Cancel();
}
