using System;
using System.Collections.Generic;
using Godot;
using Game.Core.WorldStreaming.Integration;
using Game.Simulation;

namespace Game.Core.WorldStreaming.Mutation;

// ============================================================================
// §119 P7б (task-17 п.4): STATE-REPLAY островных дельт — восстановление
// СОСТОЯНИЯ (не только занятости) в менеджерах домена при загрузке колонии.
//
// ПОРЯДОК ЗАГРУЗКИ (почему этот класс существует):
//   1. IslandDeltaApplicator.Apply (в Task.Run) — восстанавливает занятость в
//      менеджерах домена (фаза 5 IslandDomainReplay).
//   2. Main.ResetColonyState — чистит ВСЕ менеджеры (в т.ч. то, что сделал шаг 1:
//      прте-существующий порядок затирал реплей!).
//   3. ЭТОТ Replay — идемпотентно ПОВТОРЯЕТ доменные метки (занятость) и
//      разворачивает payload'ы (стадия культур, содержимое склада, доставка
//      стройки, прогресс монтажа/рубки, кучки на земле).
//   → вызывается из Main ПОСЛЕ ResetColonyState и ДО старта сима (ctx == null).
//
// Гарантии: идемпотентность (вторая прокрутка не дублирует), исключения домена
// ловятся, недоступное состояние честно логируется, занятость всегда восстановлена.
// ============================================================================

/// <summary>Результат state-replay (для лога Main и тестов).</summary>
public readonly struct IslandStateReplayResult
{
    public readonly int DomainApplied;
    public readonly int DomainSkipped;
    public readonly int CropsRestored;
    public readonly int StorageSlotsRestored;
    public readonly int GroundPilesRestored;
    public readonly int SitesStateRestored;
    public readonly int ProgressCellsRestored;

    public IslandStateReplayResult(int domainApplied, int domainSkipped, int cropsRestored,
        int storageSlotsRestored, int groundPilesRestored, int sitesStateRestored, int progressCellsRestored)
    {
        DomainApplied = domainApplied;
        DomainSkipped = domainSkipped;
        CropsRestored = cropsRestored;
        StorageSlotsRestored = storageSlotsRestored;
        GroundPilesRestored = groundPilesRestored;
        SitesStateRestored = sitesStateRestored;
        ProgressCellsRestored = progressCellsRestored;
    }

    public override string ToString()
        => $"domain={DomainApplied}/skip={DomainSkipped} crops={CropsRestored} storage={StorageSlotsRestored} " +
           $"ground={GroundPilesRestored} siteState={SitesStateRestored} progress={ProgressCellsRestored}";
}

public static class IslandStateReplay
{
    /// <summary>
    /// Восстановить состояние колонии из дельт стора в менеджерах домена.
    /// originX/originY — мировой origin зоны (UnifiedWorldGenerator.WorldOriginX/Y).
    /// </summary>
    public static IslandStateReplayResult Replay(IslandOverrideStore store, long originX, long originY)
    {
        if (store == null)
            return new IslandStateReplayResult(0, 0, 0, 0, 0, 0, 0);

        long maxX = originX + ActiveArea.Size - 1;
        long maxY = originY + ActiveArea.Size - 1;

        var cells = new List<(byte Kind, long CellX, long CellY)>(store.Count);
        store.Enumerate(originX, originY, maxX, maxY, 0, cells);

        // --- 1. Доменные метки (занятость) — идемпотентный повтор фазы 5. ---
        var domainCells = new List<(byte Kind, long X, long Y)>();
        foreach ((byte kind, long x, long y) in cells)
        {
            if (kind == WorldDeltaKind.GroundItems)
                continue; // «массивный» вид — не домен
            if (IslandDomainReplay.IsDomainKind(kind))
                domainCells.Add((kind, x, y));
        }
        (int domainApplied, int domainSkipped) = IslandDomainReplay.ReplayAll(domainCells, null);

        // --- 2. Состояние по kind'ам. ---
        int crops = 0, storageSlots = 0, ground = 0, siteState = 0, progress = 0;

        foreach ((byte kind, long wx, long wy) in cells)
        {
            int x = checked((int)(wx - originX));
            int y = checked((int)(wy - originY));
            if (x < 0 || y < 0 || x >= ActiveArea.Size || y >= ActiveArea.Size)
                continue;
            store.TryGet(kind, wx, wy, out byte[] payload);
            try
            {
                switch (kind)
                {
                    case WorldDeltaKind.Stockpile:
                    {
                        if (!IslandPayload.TryReadStockpile(payload, out ItemId item, out int count))
                            break; // пустой слот/битый payload — занятость уже восстановлена
                        StockpileManager.Instance.RestoreSlotRaw(x, y, item, count);
                        storageSlots++;
                        break;
                    }
                    case WorldDeltaKind.Crop:
                    {
                        if (!IslandPayload.TryReadCrop(payload, out int stage, out float timer, out int zoneId))
                            break; // готовая грядка без культуры — метка уже реплеена
                        CropGrowthManager.Instance.RestoreCrop(x, y, zoneId, stage, timer);
                        crops++;
                        break;
                    }
                    case WorldDeltaKind.WallPlan:
                    case WorldDeltaKind.BuildingPlan:
                    {
                        if (!IslandPayload.TryReadBuildPlan(payload, out var stage, out var delivered, out float wp))
                            break; // null payload = свежий план (дефолт фазы 5 уже применён)
                        ConstructionSite site = ConstructionManager.Instance.TryGetSite(x, y, out var s) ? s : null;
                        if (site == null)
                            break; // сайт сорвался (занятость важнее) — не критично
                        if (stage == ConstructionStage.Completed)
                            stage = ConstructionStage.Assembling; // завершение = зеркало снимет метку
                        foreach (var d in delivered)
                            site.AddDelivered(d.Item, d.Count);
                        site.Stage = stage;
                        site.WorkProgress = Math.Clamp(wp, 0f, 1f);
                        site.WorkProgressStage = -1; // визуальная стадия пересчитается
                        // Перезапуск jobs-трубки стадии (jobs стёрты ResetColonyState):
                        if (stage == ConstructionStage.Assembling)
                        {
                            JobBroker.Instance.RegisterConstructionAt(x, y);
                        }
                        else if (stage == ConstructionStage.Supplying || stage == ConstructionStage.WaitingAll)
                        {
                            JobBroker.Instance.RegisterBlueprint(x, y, site.Definition.Type, site.Definition.TotalRequiredItems);
                        }
                        siteState++;
                        break;
                    }
                    case WorldDeltaKind.CutTreePlan:
                    {
                        if (IslandPayload.TryReadProgressFraction(payload, out float f) && f > 0f)
                        {
                            TreeJobManager.Instance.ChopProgress.SaveFraction(x, y, f);
                            progress++;
                        }
                        break;
                    }
                    case WorldDeltaKind.MinedStonePlan:
                    {
                        if (IslandPayload.TryReadProgressFraction(payload, out float f) && f > 0f)
                        {
                            StoneJobManager.Instance.MineProgress.SaveFraction(x, y, f);
                            progress++;
                        }
                        break;
                    }
                    case WorldDeltaKind.GroundItems:
                    {
                        if (!IslandPayload.TryReadGroundItems(payload, out ItemId item, out int count))
                            break;
                        GroundItemManager.Instance.SpawnItems(x, y, item, count);
                        ground++;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Дельта не убивает загрузку: лог + честный учёт.
                GD.PushWarning($"[IslandStateReplay] kind=0x{kind:X2} ({x},{y}): {ex.Message}");
            }
        }

        var result = new IslandStateReplayResult(domainApplied, domainSkipped, crops, storageSlots, ground, siteState, progress);
        GD.Print($"[World] state-replay острова: {result}");
        return result;
    }
}
