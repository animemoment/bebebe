using System;
using System.Collections.Generic;
using Godot;
using Game.Core.WorldStreaming.Integration;
using Game.Simulation;

namespace Game.Core.WorldStreaming.Mutation;

// ============================================================================
// §118 фаза 5: DOMAIN REPLAY островных дельт — восстановление состояния
// ПОСТРОЕК/ФЕРМ/СКЛАдов/ПЛАНОВ в менеджерах домена (не в массивах зоны).
//
// КЛЮЧЕВОЕ ОГРАНИЧЕНИЕ (docs/MapDataWriterAudit.md §6.1, соблюдается строго):
// мутации острова НЕЛЬЗЯ писать в чанк-стриминг — StreamingWorldView.AddBlock
// принимает запись только для ЗАГРУЖЕННОГО чанка (StreamingWorldView.cs:282-283),
// остров = 64×64 = 4096 чанков при конечном окне стриминга → мутации дальнего
// края молча отбрасываются. Реплей пишет ТОЛЬКО в менеджеры острова
// (StructureManager/ConstructionManager/StockpileManager/FarmJobManager/
// TreeJobManager/StoneJobManager) через их ПУБЛИЧНЫЕ методы — никаких прямых
// записей в приватные коллекции домена (иначе разъедется с доменом).
//
// ИДЕМПОТЕНТНОСТЬ (§6.6): каждый публичный метод-приёмник сам идемпотентен
// (StructureManager.AddStructure — ContainsKey; StartSitesBatch — IsSiteAt/
// HasStructureAt; StockpileManager-батчи — _zones/_pendingZones.Add;
// FarmJobManager — _markedPlots/_completedBeds; Tree/StoneJobManager — метки).
// Повторный реплей НЕ дублирует состояние. Для подсчёта used pre-count через
// публичные предикаты (HasStructureAt/IsSiteAt/IsZoneTile/IsPlotMarked/
// IsGardenBed/IsStoneMarked) — «применится сейчас» = «домена ещё нет».
//
// ГРАНИЦЫ ВОССТАНОВЛЕНИЯ (что НЕ восстановимо из дельты сейчас — §6 «payload
// opaque до появления DTO/schema»):
//  * Building: ТИП постройки не сохранён в payload → реплей дефолтом
//    StructureRegistry.WorkTable (точные типы — после building DTO).
//  * Wall: материал не сохранён → дефолт StructureRegistry.WoodWall
//    (единственный материал WallMaterialRegistry.All, игра-поведение то же).
//  * Crop: стадия культуры не сохранена → готовая грядка (_completedBeds).
// Каждый такой дефолт — решение фазы 5, не потеря: занятость/блокировка
// восстанавливаются точно, тип — при появлении schema.
// ============================================================================

/// <summary>Домен восстановления для kind (чистая классификация — тестируемая).</summary>
public enum IslandReplayDomain
{
    /// <summary>Не доменная метка (состояние живёт в массивах ActiveArea).</summary>
    NotDomain,
    /// <summary>Вид не известен реплею (новый kind без домена) — остаётся skipped.</summary>
    Unknown,
    Wall,
    WallPlan,
    Building,
    BuildingPlan,
    Stockpile,
    StockpilePlan,
    Crop,
    CropPlan,
    CutTreePlan,
    MinedStonePlan
}

/// <summary>
/// Реплей доменных дельт через публичные API менеджеров острова. Вызывается
/// IslandDeltaApplicator во время загрузки колонии (после Fill, до/при старте
/// сима — ctx передаётся, когда SimulationContext уже существует).
/// </summary>
public static class IslandDomainReplay
{
    /// <summary>Дефолт стены (материал не сохранён в payload — §6 audit).</summary>
    public static StructureDefinition DefaultWallDefinition => StructureRegistry.WoodWall;

    /// <summary>Дефолт постройки (тип не сохранён в payload — §6 audit).</summary>
    public static StructureDefinition DefaultBuildingDefinition => StructureRegistry.WorkTable;

    /// <summary>Классификация kind → домен. CutTree/MinedStone — массивы зоны (NotDomain).</summary>
    public static IslandReplayDomain Classify(byte kind) => kind switch
    {
        WorldDeltaKind.Wall => IslandReplayDomain.Wall,
        WorldDeltaKind.WallPlan => IslandReplayDomain.WallPlan,
        WorldDeltaKind.Building => IslandReplayDomain.Building,
        WorldDeltaKind.BuildingPlan => IslandReplayDomain.BuildingPlan,
        WorldDeltaKind.Stockpile => IslandReplayDomain.Stockpile,
        WorldDeltaKind.StockpilePlan => IslandReplayDomain.StockpilePlan,
        WorldDeltaKind.Crop => IslandReplayDomain.Crop,
        WorldDeltaKind.CropPlan => IslandReplayDomain.CropPlan,
        WorldDeltaKind.CutTreePlan => IslandReplayDomain.CutTreePlan,
        WorldDeltaKind.MinedStonePlan => IslandReplayDomain.MinedStonePlan,
        // §119 P7б: GroundItems — «массивный» вид (состояние в менеджере),
        // доменного реплея не требует; применяется IslandStateReplay.
        WorldDeltaKind.CutTree or WorldDeltaKind.MinedStone
            or WorldDeltaKind.GroundItems => IslandReplayDomain.NotDomain,
        _ => IslandReplayDomain.Unknown
    };

    /// <summary>Это доменная метка (требует реплея в менеджер)?</summary>
    public static bool IsDomainKind(byte kind)
    {
        IslandReplayDomain d = Classify(kind);
        return d != IslandReplayDomain.NotDomain && d != IslandReplayDomain.Unknown;
    }

    /// <summary>
    /// Восстановить ДОМЕННЫЕ метки группами (по kind). cells — уже отфильтрованные
    /// доменные kind'ы (Classify != NotDomain/Unknown). ctx — симуляционный контекст,
    /// если существует (AddStructure запишет SolidWalls + выселит агентов); при
    /// реплее ДО старта сима ctx == null — занятость/события всё равно восстановятся.
    /// Возвращает (applied, skipped): applied — клетки, чьё состояние реально
    /// восстановлено этим вызовом (идемпотентные отказы не считаются ни там, ни там);
    /// skipped — клетки, которые всё ещё НЕ восстановлены (исключение домена).
    /// Исключения домена ЛОВЯТСЯ (GD.PushWarning) — дельта не убивает генерацию карты.
    /// </summary>
    public static (int Applied, int Skipped) ReplayAll(
        List<(byte Kind, long X, long Y)> cells, SimulationContext ctx = null)
    {
        int applied = 0;
        int skipped = 0;
        if (cells == null || cells.Count == 0)
            return (0, 0);

        // Группировка по kind: батч-домены (склады/планы строек) получают ОДИН
        // батч-вызов (§6.8: драг до 250k клеток — позаклеточно нельзя).
        var groups = new Dictionary<byte, List<(long X, long Y)>>();
        for (int i = 0; i < cells.Count; i++)
        {
            (byte kind, long x, long y) = cells[i];
            if (!groups.TryGetValue(kind, out var list))
                groups[kind] = list = new List<(long, long)>();
            list.Add((x, y));
        }

        foreach (KeyValuePair<byte, List<(long X, long Y)>> group in groups)
        {
            byte kind = group.Key;
            List<(long X, long Y)> list = group.Value;
            try
            {
                switch (Classify(kind))
                {
                    case IslandReplayDomain.Wall:
                        applied += AddStructures(list, DefaultWallDefinition, ctx, kind);
                        break;
                    case IslandReplayDomain.Building:
                        applied += AddStructures(list, DefaultBuildingDefinition, ctx, kind);
                        break;
                    case IslandReplayDomain.WallPlan:
                        applied += StartPlanSites(list, DefaultWallDefinition, kind);
                        break;
                    case IslandReplayDomain.BuildingPlan:
                        applied += StartPlanSites(list, DefaultBuildingDefinition, kind);
                        break;
                    case IslandReplayDomain.Stockpile:
                        applied += AddStockpileZones(list, pending: false, kind);
                        break;
                    case IslandReplayDomain.StockpilePlan:
                        applied += AddStockpileZones(list, pending: true, kind);
                        break;
                    case IslandReplayDomain.Crop:
                        applied += CompleteGardenBeds(list, kind);
                        break;
                    case IslandReplayDomain.CropPlan:
                        applied += MarkFarmPlots(list, kind);
                        break;
                    case IslandReplayDomain.CutTreePlan:
                        // План рубки = метка TreeJobManager (идемпотентна, батч).
                        var cutList = ToIntList(list);
                        TreeJobManager.Instance.MarkTreesBatch(cutList);
                        applied += cutList.Count;
                        break;
                    case IslandReplayDomain.MinedStonePlan:
                        // План добычи = метка StoneJobManager (идемпотентна, батч;
                        // маска камней не нужна — план уже подтверждён прошлой сессией).
                        var mineList = ToIntList(list);
                        StoneJobManager.Instance.MarkStonesBatch(mineList, null, ctx);
                        applied += mineList.Count;
                        break;
                    case IslandReplayDomain.Unknown:
                    case IslandReplayDomain.NotDomain:
                    default:
                        // Не должен сюда попадать (фильтр в применителе) — защита.
                        skipped += list.Count;
                        break;
                }
            }
            catch (Exception ex)
            {
                // Дельта не должна убивать мир: логируем, считаем невосстановленным.
                skipped += list.Count;
                GD.PushWarning($"[IslandReplay] kind=0x{kind:X2}: домен не принял {list.Count} клеток: {ex.Message}");
            }
        }

        return (applied, skipped);
    }

    // ------------------- домены: Wall / Building ------------------- //

    private static int AddStructures(
        List<(long X, long Y)> cells, StructureDefinition def, SimulationContext ctx, byte kind)
    {
        int applied = 0;
        foreach ((long x, long y) in cells)
        {
            int ix = checked((int)x);
            int iy = checked((int)y);
            if (StructureManager.Instance.HasStructureAt(ix, iy))
                continue; // идемпотентный отказ: уже восстановлено (не skipped)
            if (StructureManager.Instance.AddStructure(ix, iy, def, ctx, null))
                applied++;
            else
                GD.PushWarning($"[IslandReplay] kind=0x{kind:X2} ({ix},{iy}): AddStructure отклонён (сайт стройки?) — не восстановлено");
        }
        return applied;
    }

    // --------------- домены: WallPlan / BuildingPlan --------------- //

    private static int StartPlanSites(
        List<(long X, long Y)> cells, StructureDefinition def, byte kind)
    {
        // План стены/постройки = сайт стройки (чертёж + доставка + монтаж) —
        // ЕДИНСТВЕННЫЙ публичный путь плана (ConstructionManager.StartSitesBatch).
        var batch = new List<(int X, int Y)>(cells.Count);
        foreach ((long x, long y) in cells)
        {
            int ix = checked((int)x);
            int iy = checked((int)y);
            if (StructureManager.Instance.HasStructureAt(ix, iy)
                || ConstructionManager.Instance.IsSiteAt(ix, iy))
                continue; // уже восстановлено (готовое или другой план)
            batch.Add((ix, iy));
        }
        if (batch.Count == 0)
            return 0;
        // mapData = null: препятствия уже вычищены дельтами CutTree/MinedStone,
        // не запускаем лишних работ расчистки. Возвращает groupId.
        ConstructionManager.Instance.StartSitesBatch(batch, def, null);
        return batch.Count;
    }

    // --------------- домены: Stockpile / StockpilePlan --------------- //

    private static int AddStockpileZones(
        List<(long X, long Y)> cells, bool pending, byte kind)
    {
        var batch = new List<(int X, int Y)>(cells.Count);
        foreach ((long x, long y) in cells)
        {
            int ix = checked((int)x);
            int iy = checked((int)y);
            // IsZoneTile покрывает и боевую зону, и отложенную (общая битмаска) —
            // true значит «состояние уже восстановлено» (идемпотентный отказ).
            if (StockpileManager.Instance.IsZoneTile(ix, iy))
                continue;
            batch.Add((ix, iy));
        }
        if (batch.Count == 0)
            return 0;
        if (pending)
            StockpileManager.Instance.AddPendingZoneTilesBatch(batch);
        else
            StockpileManager.Instance.AddZoneTilesBatch(batch);
        return batch.Count;
    }

    // --------------- домены: Crop / CropPlan --------------- //

    private static int CompleteGardenBeds(List<(long X, long Y)> cells, byte kind)
    {
        int applied = 0;
        foreach ((long x, long y) in cells)
        {
            int ix = checked((int)x);
            int iy = checked((int)y);
            if (FarmJobManager.Instance.IsGardenBed(ix, iy))
                continue; // идемпотентный отказ
            // Готовая грядка: CompletePlot снимает пометку (если была) и ставит
            // в _completedBeds — идемпотентно (HashSet.Add).
            FarmJobManager.Instance.CompletePlot(ix, iy);
            applied++;
        }
        return applied;
    }

    private static int MarkFarmPlots(List<(long X, long Y)> cells, byte kind)
    {
        int applied = 0;
        foreach ((long x, long y) in cells)
        {
            int ix = checked((int)x);
            int iy = checked((int)y);
            if (FarmJobManager.Instance.IsPlotMarked(ix, iy)
                || FarmJobManager.Instance.IsGardenBed(ix, iy))
                continue; // идемпотентный отказ
            // План грядки = разметка (маски препятствий null: дельты вычистки
            // применены отдельно; MarkPlot сам проверит предметы и запустит
            // нужную расчистку через свои домены).
            FarmJobManager.Instance.MarkPlot(ix, iy, null, null);
            applied++;
        }
        return applied;
    }

    private static List<(int X, int Y)> ToIntList(List<(long X, long Y)> cells)
    {
        var list = new List<(int X, int Y)>(cells.Count);
        foreach ((long x, long y) in cells)
            list.Add((checked((int)x), checked((int)y)));
        return list;
    }
}
