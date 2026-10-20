using System;
using System.Collections.Generic;
using Game.Core.WorldStreaming.Integration;
using Game.Simulation;

namespace Game.Core.WorldStreaming.Mutation;

// ============================================================================
// §118 фаза 4: зеркало островных мутаций (PLAN.md §118.1, аудит §5.3).
//
// Централизованная подписка на СУЩЕСТВУЮЩИЕ события менеджеров: правки хендлеров
// не нужны. Покрыты 6 доменов из 11 (аудит §5.3: у CropGrowthManager,
// GroundItemManager, HumidityMap, FertilityMap событий нет — они вне этой фазы).
//
// ПИШЕТ ТОЛЬКО В IWorldMutationSink (островной стор) и НИКОГДА в чанк-стриминг:
// AddBlock принимает запись лишь для загруженного чанка, остров = 4096 чанков,
// окно конечно → мутации дальнего края молча терялись бы (аудит §6.1).
//
// Батч-события (OnPlotsBatch*, OnZoneTilesBatch*, OnSitesBatch*) обрабатываются
// батч-путём стора: драг размечает до 250 000 клеток, позаклеточный API дал бы
// репинт-шторм (аудит §6.8).
//
// Соответствие «событие → kind» — стабильные теги WorldDeltaKind, БЕЗ своих номеров.
// План (чертёж) ↔ готовое используют WorldDeltaKind.DoneFor/PlanFor:
//   * разметка/начало стройки → *Plan; завершение → снять Plan и поставить готовое.
// ============================================================================

// ВИДИМОСТЬ: обработчики internal, а не private, намеренно. События менеджеров
// поднимаются через Callable.From(...).CallDeferred() (нужны кадры главного цикла),
// поэтому в headless-тесте на _init() их доставку не проверить. Тесты живут в той же
// сборке (Game.csproj) и вызывают обработчики напрямую — так проверяется именно
// отображение «событие → вид дельты → стор», без зависимости от кадров.

/// <summary>
/// Зеркалит события менеджеров острова в единый канал мутаций (IWorldMutationSink).
/// Attach/Detach идемпотентны; повторный Attach не даёт двойной подписки.
/// </summary>
public sealed class IslandMutationMirror : IDisposable
{
    private readonly IWorldMutationSink _sink;
    private bool _attached;
    // Мировой origin активной зоны. Менеджеры домена работают в ЛОКАЛЬНЫХ координатах
    // острова (0..1023, как MapRenderer/инструменты), а стор — в МИРОВЫХ клетках
    // (как остальные WorldDelta-записи). Без этого перевода дельты ложились бы мимо зоны.
    private long _originX;
    private long _originY;

    public IslandMutationMirror(IWorldMutationSink sink)
        => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>Привязать зеркало к мировому origin активной зоны (перед подпиской/первой мутацией).</summary>
    public void SetZoneOrigin(long worldOriginX, long worldOriginY)
    {
        _originX = worldOriginX;
        _originY = worldOriginY;
    }

    /// <summary>Текущее число записей зеркала (диагностика).</summary>
    public long Mirrored { get; private set; }

    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;

        // --- лес / камень: разметка = план, выполнение = готовое ---
        // (разметка зеркалится как *Plan — ровно как это уже делает мир-слой
        //  через StreamingToolPlacement; иначе после перезапуска разметка пропадала бы)
        TreeJobManager.Instance.OnTreeMarked += OnTreeMarked;
        TreeJobManager.Instance.OnTreeUnmarked += OnTreeUnmarked;
        TreeJobManager.Instance.OnTreesBatchMarked += OnTreesBatchMarked;
        TreeJobManager.Instance.OnTreesBatchUnmarked += OnTreesBatchUnmarked;
        TreeJobManager.Instance.OnTreeChopped += OnTreeChopped;
        StoneJobManager.Instance.OnStoneMarked += OnStoneMarked;
        StoneJobManager.Instance.OnStoneUnmarked += OnStoneUnmarked;
        StoneJobManager.Instance.OnStonesBatchMarked += OnStonesBatchMarked;
        StoneJobManager.Instance.OnStonesBatchUnmarked += OnStonesBatchUnmarked;
        StoneJobManager.Instance.OnStoneMined += OnStoneMined;

        // --- ферма: разметка грядки (план) → снятие → готовое ---
        FarmJobManager.Instance.OnPlotMarked += OnPlotMarked;
        FarmJobManager.Instance.OnPlotUnmarked += OnPlotUnmarked;
        FarmJobManager.Instance.OnPlotCompleted += OnPlotCompleted;
        FarmJobManager.Instance.OnPlotsBatchMarked += OnPlotsBatchMarked;
        FarmJobManager.Instance.OnPlotsBatchUnmarked += OnPlotsBatchUnmarked;

        // --- склад: зона склада ---
        StockpileManager.Instance.OnZoneTileAdded += OnZoneTileAdded;
        StockpileManager.Instance.OnZoneTileRemoved += OnZoneTileRemoved;
        StockpileManager.Instance.OnZoneTilesBatchAdded += OnZoneTilesBatchAdded;
        StockpileManager.Instance.OnZoneTilesBatchRemoved += OnZoneTilesBatchRemoved;

        // --- постройки: готовое ---
        StructureManager.Instance.OnStructureAdded += OnStructureAdded;
        StructureManager.Instance.OnStructureRemoved += OnStructureRemoved;

        // --- стройка: чертёж → готовое ---
        ConstructionManager.Instance.OnSiteAdded += OnSiteAdded;
        ConstructionManager.Instance.OnSiteRemoved += OnSiteRemoved;
        ConstructionManager.Instance.OnSitesBatchAdded += OnSitesBatchAdded;
        ConstructionManager.Instance.OnSitesBatchRemoved += OnSitesBatchRemoved;
        ConstructionManager.Instance.OnSiteCompleted += OnSiteCompleted;
    }

    public void Detach()
    {
        if (!_attached)
            return;
        _attached = false;

        TreeJobManager.Instance.OnTreeMarked -= OnTreeMarked;
        TreeJobManager.Instance.OnTreeUnmarked -= OnTreeUnmarked;
        TreeJobManager.Instance.OnTreesBatchMarked -= OnTreesBatchMarked;
        TreeJobManager.Instance.OnTreesBatchUnmarked -= OnTreesBatchUnmarked;
        TreeJobManager.Instance.OnTreeChopped -= OnTreeChopped;
        StoneJobManager.Instance.OnStoneMarked -= OnStoneMarked;
        StoneJobManager.Instance.OnStoneUnmarked -= OnStoneUnmarked;
        StoneJobManager.Instance.OnStonesBatchMarked -= OnStonesBatchMarked;
        StoneJobManager.Instance.OnStonesBatchUnmarked -= OnStonesBatchUnmarked;
        StoneJobManager.Instance.OnStoneMined -= OnStoneMined;

        FarmJobManager.Instance.OnPlotMarked -= OnPlotMarked;
        FarmJobManager.Instance.OnPlotUnmarked -= OnPlotUnmarked;
        FarmJobManager.Instance.OnPlotCompleted -= OnPlotCompleted;
        FarmJobManager.Instance.OnPlotsBatchMarked -= OnPlotsBatchMarked;
        FarmJobManager.Instance.OnPlotsBatchUnmarked -= OnPlotsBatchUnmarked;

        StockpileManager.Instance.OnZoneTileAdded -= OnZoneTileAdded;
        StockpileManager.Instance.OnZoneTileRemoved -= OnZoneTileRemoved;
        StockpileManager.Instance.OnZoneTilesBatchAdded -= OnZoneTilesBatchAdded;
        StockpileManager.Instance.OnZoneTilesBatchRemoved -= OnZoneTilesBatchRemoved;

        StructureManager.Instance.OnStructureAdded -= OnStructureAdded;
        StructureManager.Instance.OnStructureRemoved -= OnStructureRemoved;

        ConstructionManager.Instance.OnSiteAdded -= OnSiteAdded;
        ConstructionManager.Instance.OnSiteRemoved -= OnSiteRemoved;
        ConstructionManager.Instance.OnSitesBatchAdded -= OnSitesBatchAdded;
        ConstructionManager.Instance.OnSitesBatchRemoved -= OnSitesBatchRemoved;
        ConstructionManager.Instance.OnSiteCompleted -= OnSiteCompleted;
    }

    public void Dispose() => Detach();

    // ------------------------------------------------------------------ //
    //  Чистое отображение «постройка/стройка → kind» (тестируется отдельно) //
    // ------------------------------------------------------------------ //

    /// <summary>Готовый вид постройки: стена или здание (по категории определения).</summary>
    public static byte DoneKindFor(StructureDefinition definition)
        => definition != null && definition.IsWall ? WorldDeltaKind.Wall : WorldDeltaKind.Building;

    /// <summary>Вид чертежа постройки: план стены или план здания.</summary>
    public static byte PlanKindFor(StructureDefinition definition)
        => WorldDeltaKind.PlanFor(DoneKindFor(definition));

    // ------------------------------------------------------------------ //
    //  Обработчики: лес / камень                                          //
    // ------------------------------------------------------------------ //

    internal void OnTreeChopped((int X, int Y) pos)
    {
        // CompleteTree снимает метку из _markedTrees, НЕ стреляя OnTreeUnmarked
        // (TreeJobManager.cs:107-115) → план обязан сняться здесь, иначе в сторе
        // навсегда остался бы «план рубки» на уже срубленной клетке.
        Remove(WorldDeltaKind.CutTreePlan, pos.X, pos.Y);
        Apply(WorldDeltaKind.CutTree, pos.X, pos.Y);
    }

    internal void OnStoneMined((int X, int Y) pos)
    {
        Remove(WorldDeltaKind.MinedStonePlan, pos.X, pos.Y);
        Apply(WorldDeltaKind.MinedStone, pos.X, pos.Y);
    }

    // Разметка = чертёж (план): план без готового = незакрытая задача (см. §29).
    internal void OnTreeMarked((int X, int Y) pos) => Apply(WorldDeltaKind.CutTreePlan, pos.X, pos.Y);

    internal void OnTreeUnmarked((int X, int Y) pos) => Remove(WorldDeltaKind.CutTreePlan, pos.X, pos.Y);

    internal void OnTreesBatchMarked(List<(int X, int Y)> trees)
        => ApplyMany(WorldDeltaKind.CutTreePlan, trees);

    internal void OnTreesBatchUnmarked(List<(int X, int Y)> trees)
        => RemoveMany(new[] { WorldDeltaKind.CutTreePlan }, trees);

    internal void OnStoneMarked((int X, int Y) pos) => Apply(WorldDeltaKind.MinedStonePlan, pos.X, pos.Y);

    internal void OnStoneUnmarked((int X, int Y) pos) => Remove(WorldDeltaKind.MinedStonePlan, pos.X, pos.Y);

    internal void OnStonesBatchMarked(List<(int X, int Y)> stones)
        => ApplyMany(WorldDeltaKind.MinedStonePlan, stones);

    internal void OnStonesBatchUnmarked(List<(int X, int Y)> stones)
        => RemoveMany(new[] { WorldDeltaKind.MinedStonePlan }, stones);

    // ------------------------------------------------------------------ //
    //  Обработчики: ферма (разметка = план, завершение = готовое)          //
    // ------------------------------------------------------------------ //

    internal void OnPlotMarked((int X, int Y) pos) => Apply(WorldDeltaKind.CropPlan, pos.X, pos.Y);

    internal void OnPlotUnmarked((int X, int Y) pos)
    {
        // ВАЖНО (найдено тестом): UnmarkPlot снимает НЕ только разметку, но и
        // «готовую грядку» (_completedBeds.Remove, FarmJobManager.cs:213), поэтому в
        // сторе обязаны уйти ОБА состояния — план и готовый вид. Убрать только план
        // значило бы оставить в мире призрачную готовую грядку после отмены зоны.
        Remove(WorldDeltaKind.CropPlan, pos.X, pos.Y);
        Remove(WorldDeltaKind.Crop, pos.X, pos.Y);
    }

    internal void OnPlotCompleted((int X, int Y) pos)
    {
        Remove(WorldDeltaKind.CropPlan, pos.X, pos.Y);
        Apply(WorldDeltaKind.Crop, pos.X, pos.Y);
    }

    internal void OnPlotsBatchMarked(List<(int X, int Y)> plots) => ApplyMany(WorldDeltaKind.CropPlan, plots);

    internal void OnPlotsBatchUnmarked(List<(int X, int Y)> plots)
        => RemoveMany(new[] { WorldDeltaKind.CropPlan, WorldDeltaKind.Crop }, plots);

    // ------------------------------------------------------------------ //
    //  Обработчики: склад                                                  //
    // ------------------------------------------------------------------ //

    internal void OnZoneTileAdded((int X, int Y) pos) => Apply(WorldDeltaKind.Stockpile, pos.X, pos.Y);

    internal void OnZoneTileRemoved((int X, int Y) pos) => Remove(WorldDeltaKind.Stockpile, pos.X, pos.Y);

    internal void OnZoneTilesBatchAdded((int X, int Y)[] tiles) => ApplyMany(WorldDeltaKind.Stockpile, tiles);

    internal void OnZoneTilesBatchRemoved((int X, int Y)[] tiles)
        => RemoveMany(new[] { WorldDeltaKind.Stockpile }, tiles);

    // ------------------------------------------------------------------ //
    //  Обработчики: постройки (готовое)                                    //
    // ------------------------------------------------------------------ //

    internal void OnStructureAdded(StructureInstance structure)
    {
        if (structure?.Definition == null)
            return;
        Apply(DoneKindFor(structure.Definition), structure.X, structure.Y);
    }

    internal void OnStructureRemoved((int X, int Y) pos, StructureDefinition definition)
        => Remove(DoneKindFor(definition), pos.X, pos.Y);

    // ------------------------------------------------------------------ //
    //  Обработчики: стройка (чертёж → готовое)                             //
    // ------------------------------------------------------------------ //

    internal void OnSiteAdded(ConstructionSite site)
    {
        if (site?.Definition == null)
            return;
        Apply(PlanKindFor(site.Definition), site.X, site.Y);
    }

    internal void OnSiteRemoved((int X, int Y) pos)
    {
        // Событие не несёт определение → снимаем оба возможных вида чертежа
        // (идемпотентно: лишний Remove просто вернёт false).
        Remove(WorldDeltaKind.WallPlan, pos.X, pos.Y);
        Remove(WorldDeltaKind.BuildingPlan, pos.X, pos.Y);
    }

    internal void OnSitesBatchAdded(List<ConstructionSite> sites)
    {
        if (sites == null || sites.Count == 0)
            return;
        var batch = new WorldMutation[sites.Count];
        int n = 0;
        foreach (ConstructionSite site in sites)
        {
            if (site?.Definition == null)
                continue;
            batch[n++] = new WorldMutation(PlanKindFor(site.Definition), site.X, site.Y);
        }
        if (n == 0)
            return;
        if (n != batch.Length)
            Array.Resize(ref batch, n);
        Mirrored += _sink.ApplyBatch(batch);
    }

    internal void OnSitesBatchRemoved(List<(int X, int Y)> sites)
        => RemoveMany(new[] { WorldDeltaKind.WallPlan, WorldDeltaKind.BuildingPlan }, sites);

    internal void OnSiteCompleted(ConstructionSite site)
    {
        if (site?.Definition == null)
            return;
        byte plan = PlanKindFor(site.Definition);
        Remove(plan, site.X, site.Y);
        Apply(WorldDeltaKind.DoneFor(plan), site.X, site.Y);
    }

    // ------------------------------------------------------------------ //
    //  Мелкие помощники (одна точка — один вызов стора)                    //
    // ------------------------------------------------------------------ //

    private void Apply(byte kind, int localX, int localY)
    {
        if (_sink.TryApply(kind, _originX + localX, _originY + localY))
            Mirrored++;
    }

    private void Remove(byte kind, int localX, int localY)
    {
        if (_sink.TryRemove(kind, _originX + localX, _originY + localY))
            Mirrored++;
    }

    private void ApplyMany(byte kind, IReadOnlyList<(int X, int Y)> cells)
    {
        if (cells == null || cells.Count == 0)
            return;
        var batch = new WorldMutation[cells.Count];
        for (int i = 0; i < cells.Count; i++)
            batch[i] = new WorldMutation(kind, _originX + cells[i].X, _originY + cells[i].Y);
        Mirrored += _sink.ApplyBatch(batch);
    }

    private void RemoveMany(byte[] kinds, IReadOnlyList<(int X, int Y)> cells)
    {
        if (cells == null || cells.Count == 0 || kinds == null || kinds.Length == 0)
            return;
        var batch = new WorldMutation[cells.Count * kinds.Length];
        int n = 0;
        for (int i = 0; i < cells.Count; i++)
            for (int k = 0; k < kinds.Length; k++)
                batch[n++] = new WorldMutation(kinds[k], _originX + cells[i].X, _originY + cells[i].Y);
        Mirrored += _sink.RemoveBatch(batch);
    }
}