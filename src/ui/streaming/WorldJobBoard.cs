using System;
using System.Collections.Generic;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;

namespace Game.UI.Streaming;

/// <summary>
/// Очередь работ мира (§29): чертежи загруженных чанков → задачи людям. Прогресс не
/// сериализуется: «чертёж без готового» и есть незакрытая задача, готовое — метка мира.
/// Невыгруженные чанки в очередь не попадают → оффскрин работы не тикают.
/// Только main thread.
/// </summary>
public sealed class WorldJobBoard
{
    private readonly StreamingWorldView _view;
    private readonly StreamingWorldManager _manager;
    private readonly List<WorldPlan> _pending = new(512);
    private readonly HashSet<(long X, long Y)> _claimed = new();

    public WorldJobBoard(StreamingWorldView view, StreamingWorldManager manager)
    {
        _view = view;
        _manager = manager;
    }

    /// <summary>Сколько чертежей ждёт людей (в загруженных чанках).</summary>
    public int PendingCount { get; private set; }

    /// <summary>По скольким клеткам уже работает человек.</summary>
    public int ClaimedCount => _claimed.Count;

    /// <summary>Сколько чертежей потеряно (готовое не встало И чертёж не вернулся) — диагностика.</summary>
    public int LostPlans { get; private set; }

    public bool HasFreeWork => PendingCount > ClaimedCount;

    /// <summary>Обновить очередь: только загруженные чанки, ближайшие к фокусу первыми.</summary>
    public void Refresh(long focusX, long focusY, int max = 256)
        => PendingCount = _view.CollectPendingPlans(_pending, focusX, focusY, max);

    /// <summary>
    /// Взять ближайшую незанятую задачу (клетка помечается занятой). <paramref name="allow"/>
    /// позволяет пропустить задачи, которые сейчас брать нельзя (например недостижимые):
    /// иначе ближайшая «мёртвая» задача загораживает очередь.
    /// </summary>
    public bool TryTakeNearest(float fromX, float fromY, out WorldPlan plan, Func<WorldPlan, bool> allow = null)
    {
        plan = default;
        float best = float.MaxValue;
        bool found = false;
        for (int i = 0; i < _pending.Count; i++)
        {
            WorldPlan candidate = _pending[i];
            if (_claimed.Contains((candidate.CellX, candidate.CellY)))
                continue;
            if (allow != null && !allow(candidate))
                continue;
            float dx = candidate.CellX + 0.5f - fromX;
            float dy = candidate.CellY + 0.5f - fromY;
            float distance = dx * dx + dy * dy;
            if (distance >= best)
                continue;
            best = distance;
            plan = candidate;
            found = true;
        }
        if (found)
            _claimed.Add((plan.CellX, plan.CellY));
        return found;
    }

    /// <summary>
    /// Задача выполнена: снять чертёж и поставить готовое (§29). Если готовое не встало
    /// (террейн изменился/клетка занята) — чертёж возвращается, задачу попробуют позже.
    /// </summary>
    public bool Complete(WorldPlan plan)
    {
        _claimed.Remove((plan.CellX, plan.CellY));
        if (!_manager.TryRemoveBlock(plan.PlanKind, plan.CellX, plan.CellY))
            return false;
        if (_manager.TryPlaceBlock(plan.DoneKind, plan.CellX, plan.CellY))
            return true;
        // Готовое не встало: вернуть чертёж. Если и это не удалось — задача потеряна;
        // считаем такие случаи (молча терять чертежи нельзя).
        if (!_manager.TryPlaceBlock(plan.PlanKind, plan.CellX, plan.CellY))
            LostPlans++;
        return false;
    }

    /// <summary>Человек бросил задачу (чертёж снят или чанк выгрузился): снять пометку.</summary>
    public void Release(WorldPlan plan) => _claimed.Remove((plan.CellX, plan.CellY));

    /// <summary>Метка ещё актуальна: чанк загружен и чертёж на месте.</summary>
    public bool IsStillPlanned(WorldPlan plan)
        => _manager.IsChunkLoaded(WorldCoordinates.ChunkForTile(plan.CellX, plan.CellY))
            && _view.HasBlock(plan.PlanKind, plan.CellX, plan.CellY);
}
