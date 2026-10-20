using System;
using System.Collections.Generic;
using Godot;

namespace Game.Simulation;

public class FarmJobManager
{
    public static FarmJobManager Instance { get; } = new();

    private readonly object _lock = new();
    private readonly HashSet<(int X, int Y)> _markedPlots = new(1024);
    private readonly HashSet<(int X, int Y)> _completedBeds = new(1024);
    private readonly Dictionary<(int X, int Y), (bool NeedTree, bool NeedStone, bool NeedItem)> _plotsNeedingClearance = new();

    /// <summary>
    /// P7б §119: per-cell прогресс вскопки 0..1 — переживает прерывание агента.
    /// Пишет OnCancel хендлера, читает OnStart, чистят Unmark*/CompletePlot/ClearAll.
    /// </summary>
    public CellProgressStore TillProgress { get; } = new();

    public event Action<(int X, int Y)> OnPlotMarked;
    public event Action<List<(int X, int Y)>> OnPlotsBatchMarked;
    public event Action<(int X, int Y)> OnPlotUnmarked;
    public event Action<List<(int X, int Y)>> OnPlotsBatchUnmarked;
    public event Action<(int X, int Y)> OnPlotCompleted;

    public void MarkPlot(int x, int y, bool[,] treeOnGrass, bool[,] stoneOnGrass = null)
    {
        bool spillItems = false;
        bool markTree = false;
        bool markStone = false;
        bool registerPlot = false;
        bool fireEvent = false;

        lock (_lock)
        {
            if (_completedBeds.Contains((x, y))) return;

            if (_markedPlots.Add((x, y)))
            {
                bool hasTree = treeOnGrass != null && (uint)x < (uint)treeOnGrass.GetLength(0) && (uint)y < (uint)treeMaskHeight(treeOnGrass) && treeOnGrass[x, y];
                bool hasStone = stoneOnGrass != null && (uint)x < (uint)stoneOnGrass.GetLength(0) && (uint)y < (uint)stoneMaskHeight(stoneOnGrass) && stoneOnGrass[x, y];
                bool hasItems = GroundItemManager.Instance.HasItemsAt(x, y);

                if (hasTree || hasStone || hasItems)
                {
                    _plotsNeedingClearance[(x, y)] = (hasTree, hasStone, hasItems);
                    markTree = hasTree;
                    markStone = hasStone;
                    spillItems = hasItems;
                }
                else
                {
                    registerPlot = true;
                }
                fireEvent = true;
            }
        }

        if (spillItems) GroundItemManager.Instance.TrySpillItemsAdjacent(x, y, 512, 512);
        if (markTree) TreeJobManager.Instance.MarkTree(x, y);
        if (markStone) StoneJobManager.Instance.MarkStone(x, y, stoneOnGrass);
        if (registerPlot) JobBroker.Instance.RegisterFarmPlot(x, y);
        if (fireEvent) Callable.From(() => OnPlotMarked?.Invoke((x, y))).CallDeferred();
    }

    public void MarkPlotsBatch(List<(int X, int Y)> plots, bool[,] treeOnGrass, bool[,] stoneOnGrass = null)
    {
        if (plots == null || plots.Count == 0) return;

        var addedList = new List<(int X, int Y)>(plots.Count);
        var readyToPlow = new List<(int X, int Y)>();
        var treesToChop = new List<(int X, int Y)>();
        var stonesToMine = new List<(int X, int Y)>();
        var itemsToClear = new List<(int X, int Y)>();

        lock (_lock)
        {
            foreach (var pos in plots)
            {
                if (!_completedBeds.Contains(pos) && _markedPlots.Add(pos))
                {
                    addedList.Add(pos);
                    bool hasTree = treeOnGrass != null && (uint)pos.X < (uint)treeOnGrass.GetLength(0) && (uint)pos.Y < (uint)treeMaskHeight(treeOnGrass) && treeOnGrass[pos.X, pos.Y];
                    bool hasStone = stoneOnGrass != null && (uint)pos.X < (uint)stoneOnGrass.GetLength(0) && (uint)pos.Y < (uint)stoneMaskHeight(stoneOnGrass) && stoneOnGrass[pos.X, pos.Y];
                    bool hasItems = GroundItemManager.Instance.HasItemsAt(pos.X, pos.Y);

                    if (hasTree || hasStone || hasItems)
                    {
                        _plotsNeedingClearance[pos] = (hasTree, hasStone, hasItems);
                        if (hasTree) treesToChop.Add(pos);
                        if (hasStone) stonesToMine.Add(pos);
                        if (hasItems) itemsToClear.Add(pos);
                    }
                    else
                    {
                        readyToPlow.Add(pos);
                    }
                }
            }
        }

        if (itemsToClear.Count > 0)
        {
            foreach (var (ix, iy) in itemsToClear)
            {
                GroundItemManager.Instance.TrySpillItemsAdjacent(ix, iy, 512, 512);
            }
        }

        if (stonesToMine.Count > 0)
        {
            StoneJobManager.Instance.MarkStonesBatch(stonesToMine, stoneOnGrass);
        }

        if (treesToChop.Count > 0)
        {
            TreeJobManager.Instance.MarkTreesBatch(treesToChop);
        }

        if (readyToPlow.Count > 0)
        {
            JobBroker.Instance.RegisterFarmPlotBatch(readyToPlow);
        }

        if (addedList.Count > 0)
        {
            Callable.From(() => OnPlotsBatchMarked?.Invoke(addedList)).CallDeferred();
        }
    }

    private static int treeMaskHeight(bool[,] mask) => mask != null ? mask.GetLength(1) : 0;
    private static int stoneMaskHeight(bool[,] mask) => mask != null ? mask.GetLength(1) : 0;

    public void NotifyTreeCleared(int x, int y)
    {
        bool becameReady = false;
        lock (_lock)
        {
            if (_plotsNeedingClearance.TryGetValue((x, y), out var req))
            {
                req.NeedTree = false;
                req.NeedItem = GroundItemManager.Instance.HasItemsAt(x, y);
                if (!req.NeedStone && !req.NeedItem)
                {
                    _plotsNeedingClearance.Remove((x, y));
                    becameReady = _markedPlots.Contains((x, y));
                }
                else
                {
                    _plotsNeedingClearance[(x, y)] = req;
                }
            }
        }
        if (becameReady)
        {
            JobBroker.Instance.RegisterFarmPlot(x, y);
        }
    }

    public void NotifyStoneCleared(int x, int y)
    {
        bool becameReady = false;
        lock (_lock)
        {
            if (_plotsNeedingClearance.TryGetValue((x, y), out var req))
            {
                req.NeedStone = false;
                req.NeedItem = GroundItemManager.Instance.HasItemsAt(x, y);
                if (!req.NeedTree && !req.NeedItem)
                {
                    _plotsNeedingClearance.Remove((x, y));
                    becameReady = _markedPlots.Contains((x, y));
                }
                else
                {
                    _plotsNeedingClearance[(x, y)] = req;
                }
            }
        }
        if (becameReady)
        {
            JobBroker.Instance.RegisterFarmPlot(x, y);
        }
    }

    public void NotifyItemCleared(int x, int y)
    {
        bool becameReady = false;
        lock (_lock)
        {
            if (_plotsNeedingClearance.TryGetValue((x, y), out var req))
            {
                req.NeedItem = false;
                if (!req.NeedTree && !req.NeedStone)
                {
                    _plotsNeedingClearance.Remove((x, y));
                    becameReady = _markedPlots.Contains((x, y));
                }
                else
                {
                    _plotsNeedingClearance[(x, y)] = req;
                }
            }
        }
        if (becameReady)
        {
            JobBroker.Instance.RegisterFarmPlot(x, y);
        }
    }

    public void UnmarkPlot(int x, int y)
    {
        bool unregister = false;
        lock (_lock)
        {
            _plotsNeedingClearance.Remove((x, y));
            _completedBeds.Remove((x, y));
            TillProgress.Clear(x, y);
            if (_markedPlots.Remove((x, y)))
            {
                unregister = true;
            }
        }
        if (unregister)
        {
            JobBroker.Instance.UnregisterFarmPlot(x, y);
        }
        Callable.From(() => OnPlotUnmarked?.Invoke((x, y))).CallDeferred();
    }

    public void UnmarkPlotsBatch(List<(int X, int Y)> plots)
    {
        if (plots == null || plots.Count == 0) return;

        var removedList = new List<(int X, int Y)>(plots.Count);
        lock (_lock)
        {
            foreach (var pos in plots)
            {
                _plotsNeedingClearance.Remove(pos);
                _completedBeds.Remove(pos);
                _markedPlots.Remove(pos);
                TillProgress.Clear(pos.X, pos.Y);
                removedList.Add(pos);
            }
        }

        if (removedList.Count > 0)
        {
            JobBroker.Instance.UnregisterFarmPlotBatch(removedList);
            Callable.From(() => OnPlotsBatchUnmarked?.Invoke(removedList)).CallDeferred();
        }
    }

    public bool IsPlotMarked(int x, int y)
    {
        lock (_lock)
        {
            return _markedPlots.Contains((x, y));
        }
    }

    public bool IsGardenBed(int x, int y)
    {
        lock (_lock)
        {
            return _completedBeds.Contains((x, y));
        }
    }

    /// <summary>
    /// Снимок грядок в bool-маску W×H (один lock на всех, а не 262k).
    /// Для тика влажности: читается из Parallel.For без локов.
    /// OOB грядки отбрасываются.
    /// </summary>
    public bool[,] BuildGardenBedMask(int w, int h)
    {
        var mask = new bool[w, h];
        lock (_lock)
        {
            foreach (var (x, y) in _completedBeds)
            {
                if ((uint)x < (uint)w && (uint)y < (uint)h)
                    mask[x, y] = true;
            }
        }
        return mask;
    }

    /// <summary>
    /// Flat-версия маски (bool[w*h], индекс y*w+x) для HumidityMap.Tick:
    /// flat-чтение без bounds-check ×2 и вычисления индекса [,].
    /// </summary>
    public bool[] BuildGardenBedFlatMask(int w, int h)
    {
        var mask = new bool[w * h];
        lock (_lock)
        {
            foreach (var (x, y) in _completedBeds)
            {
                if ((uint)x < (uint)w && (uint)y < (uint)h)
                    mask[y * w + x] = true;
            }
        }
        return mask;
    }

    /// <summary>
    /// Заполнение переиспользуемого буфера маски (bool[w*h], индекс y*w+x) без аллокаций.
    /// </summary>
    public void FillGardenBedFlatMask(bool[] mask, int w, int h)
    {
        Array.Clear(mask, 0, mask.Length);
        lock (_lock)
        {
            foreach (var (x, y) in _completedBeds)
            {
                if ((uint)x < (uint)w && (uint)y < (uint)h)
                    mask[y * w + x] = true;
            }
        }
    }

    public HashSet<(int X, int Y)> GetAllMarkedPlots()
    {
        lock (_lock)
        {
            return new HashSet<(int X, int Y)>(_markedPlots);
        }
    }

    public HashSet<(int X, int Y)> GetAllCompletedBeds()
    {
        lock (_lock)
        {
            return new HashSet<(int X, int Y)>(_completedBeds);
        }
    }

    public void CompletePlot(int x, int y)
    {
        lock (_lock)
        {
            _markedPlots.Remove((x, y));
            _completedBeds.Add((x, y));
            TillProgress.Clear(x, y);
            JobBroker.Instance.UnregisterFarmPlot(x, y);
            Callable.From(() => OnPlotCompleted?.Invoke((x, y))).CallDeferred();
        }

        if (FarmZoneManager.Instance.TryGetZoneAt(x, y, out var zone) && zone != null && zone.AutoPlantEnabled)
        {
            JobBroker.Instance.RegisterPlanting(x, y, zone.Id);
        }
    }

    /// <summary>Очистить все грядки и разметку при смене региона / перезапуске карты.</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            var oldPlots = new List<(int X, int Y)>(_markedPlots);
            foreach (var bed in _completedBeds)
            {
                if (!oldPlots.Contains(bed))
                    oldPlots.Add(bed);
            }
            _markedPlots.Clear();
            _plotsNeedingClearance.Clear();
            _completedBeds.Clear();
            TillProgress.ClearAll();
            if (oldPlots.Count > 0)
            {
                Callable.From(() => OnPlotsBatchUnmarked?.Invoke(oldPlots)).CallDeferred();
            }
        }
    }
}
