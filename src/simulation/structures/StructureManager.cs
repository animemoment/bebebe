using System;
using System.Collections.Generic;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Единый менеджер для ВСЕХ возведенных в мире структур (стены любых типов,
/// мебель, станки, двери и т.д.).
/// Полностью заменяет разрозненные WallBuildManager и BuildingManager.
/// Потокобезопасен. Автоматически обновляет физику SolidWalls и кэш навигации.
/// </summary>
public sealed class StructureManager
{
    public static StructureManager Instance { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), StructureInstance> _structures = new(4096);

    // События для рендера и подсистем
    public event Action<StructureInstance> OnStructureAdded;
    public event Action<(int X, int Y), StructureDefinition> OnStructureRemoved;
    public event Action<List<(int X, int Y)>> OnWallTilesUpdated;

    /// <summary>
    /// Хук сноса центрального блока (§119 P5): снесён CentralBlock в (x, y) → снести
    /// рабочую зону с центром в этой клетке (работы падают в непокрытые). Подписчик —
    /// WorkZoneManager (static ctor подключает RemoveZoneAt); до первого доступа к
    /// синглтону хук null — вызов молча пропускается.
    /// </summary>
    public static Action<int, int> CentralBlockRemovedHook { get; set; }

    // Дебаунс уведомлений для автотайлинга стен (перенесено из WallBuildManager)
    private readonly HashSet<(int X, int Y)> _pendingWallUpdates = new();
    private long _lastFlushTicks;
    private const long DebounceMs = 200;

    public bool HasStructureAt(int x, int y)
    {
        lock (_lock)
        {
            return _structures.ContainsKey((x, y));
        }
    }

    public bool IsWallAt(int x, int y)
    {
        lock (_lock)
        {
            return _structures.TryGetValue((x, y), out var s) && s.Definition.IsWall;
        }
    }

    public bool TryGetStructure(int x, int y, out StructureInstance structure)
    {
        lock (_lock)
        {
            return _structures.TryGetValue((x, y), out structure);
        }
    }

    /// <summary>
    /// Проверяет, возведена ли структура и готова ли она к эксплуатации (не находится на этапе стройки).
    /// </summary>
    public bool IsStructureOperational(int x, int y)
    {
        if (ConstructionManager.Instance.IsSiteAt(x, y))
            return false;

        lock (_lock)
        {
            return _structures.TryGetValue((x, y), out var s) && s.IsOperational;
        }
    }

    /// <summary>
    /// Получить готовую к эксплуатации структуру (станки, мебель, производство).
    /// Если объект еще строится (есть в ConstructionManager), возвращает false.
    /// </summary>
    public bool TryGetOperationalStructure(int x, int y, out StructureInstance structure)
    {
        structure = null;
        if (ConstructionManager.Instance.IsSiteAt(x, y))
            return false;

        lock (_lock)
        {
            if (_structures.TryGetValue((x, y), out structure) && structure.IsOperational)
                return true;
        }
        structure = null;
        return false;
    }

    public bool AddStructure(int x, int y, StructureDefinition def, SimulationContext ctx = null, AgentDataPool pool = null)
    {
        if (def == null) return false;

        StructureInstance instance;
        lock (_lock)
        {
            if (_structures.ContainsKey((x, y)))
                return false;

            instance = new StructureInstance(x, y, def);
            _structures[(x, y)] = instance;

            if (def.IsWall)
            {
                NotifyWallChangedLocked(x, y);
            }
        }

        // Обновление физической проходимости и навигации
        if (def.BlocksMovement && ctx != null)
        {
            if ((uint)x < (uint)ctx.MapWidth && (uint)y < (uint)ctx.MapHeight)
            {
                ctx.SolidWalls[x, y] = true;
            }
            FlowFieldManager.Instance?.ClearCache();
            HierarchicalPathfinder.Instance?.Invalidate();

            // Выселение агентов, если они стояли на месте возведенной стены
            if (ctx.SpatialGrid != null && ctx.Movement != null && pool != null)
            {
                int insideAgent = ctx.SpatialGrid.GetFirstAgent(x, y);
                while (insideAgent != -1)
                {
                    ctx.Movement.EjectFromWall(insideAgent, x, y, pool, ctx);
                    insideAgent = ctx.SpatialGrid.GetNextAgent(insideAgent, pool);
                }
            }
        }

        Callable.From(() => OnStructureAdded?.Invoke(instance)).CallDeferred();
        return true;
    }

    public bool RemoveStructure(int x, int y, SimulationContext ctx = null)
    {
        StructureInstance removed;
        lock (_lock)
        {
            if (!_structures.Remove((x, y), out removed))
                return false;

            if (removed.Definition.IsWall)
            {
                NotifyWallChangedLocked(x, y);
            }
        }

        if (removed.Definition.BlocksMovement && ctx != null)
        {
            if ((uint)x < (uint)ctx.MapWidth && (uint)y < (uint)ctx.MapHeight)
            {
                ctx.SolidWalls[x, y] = false;
            }
            FlowFieldManager.Instance?.ClearCache();
            HierarchicalPathfinder.Instance?.Invalidate();
        }

        var def = removed.Definition;
        // §119 P5: снос центрального блока = снос зоны, чьим якорем он был.
        if (def.Type == BuildingType.CentralBlock)
        {
            try
            {
                CentralBlockRemovedHook?.Invoke(x, y);
            }
            catch (Exception hookEx)
            {
                GD.PrintErr($"[StructureManager] CentralBlockRemovedHook failed at ({x},{y}): {hookEx.Message}");
            }
        }
        Callable.From(() => OnStructureRemoved?.Invoke((x, y), def)).CallDeferred();
        return true;
    }

    public HashSet<(int X, int Y)> GetAllWalls()
    {
        lock (_lock)
        {
            var walls = new HashSet<(int X, int Y)>();
            foreach (var kvp in _structures)
            {
                if (kvp.Value.Definition.IsWall)
                    walls.Add(kvp.Key);
            }
            return walls;
        }
    }

    public Dictionary<(int X, int Y), StructureInstance> GetAllStructures()
    {
        lock (_lock)
        {
            return new Dictionary<(int X, int Y), StructureInstance>(_structures);
        }
    }

    public void ClearAll(SimulationContext ctx = null)
    {
        List<StructureInstance> snapshot;
        lock (_lock)
        {
            snapshot = new List<StructureInstance>(_structures.Values);
            _structures.Clear();
            _pendingWallUpdates.Clear();
        }

        foreach (var s in snapshot)
        {
            if (s.Definition.BlocksMovement && ctx != null)
            {
                if ((uint)s.X < (uint)ctx.MapWidth && (uint)s.Y < (uint)ctx.MapHeight)
                    ctx.SolidWalls[s.X, s.Y] = false;
            }
            var def = s.Definition;
            var pos = (s.X, s.Y);
            Callable.From(() => OnStructureRemoved?.Invoke(pos, def)).CallDeferred();
        }

        if (ctx != null)
        {
            FlowFieldManager.Instance?.ClearCache();
            HierarchicalPathfinder.Instance?.Invalidate();
        }
    }

    private void NotifyWallChangedLocked(int x, int y)
    {
        _pendingWallUpdates.Add((x, y));
        _pendingWallUpdates.Add((x, y - 1));
        _pendingWallUpdates.Add((x, y + 1));
        _pendingWallUpdates.Add((x - 1, y));
        _pendingWallUpdates.Add((x + 1, y));

        long now = System.Environment.TickCount64;
        if (now - _lastFlushTicks >= DebounceMs)
        {
            _lastFlushTicks = now;
            var changed = new List<(int X, int Y)>(_pendingWallUpdates);
            _pendingWallUpdates.Clear();
            Callable.From(() => OnWallTilesUpdated?.Invoke(changed)).CallDeferred();
        }
    }

    public void FlushPendingWallUpdates()
    {
        List<(int X, int Y)> changed = null;
        lock (_lock)
        {
            if (_pendingWallUpdates.Count == 0)
                return;
            _lastFlushTicks = System.Environment.TickCount64;
            changed = new List<(int X, int Y)>(_pendingWallUpdates);
            _pendingWallUpdates.Clear();
        }

        if (changed != null && changed.Count > 0)
        {
            Callable.From(() => OnWallTilesUpdated?.Invoke(changed)).CallDeferred();
        }
    }
}
