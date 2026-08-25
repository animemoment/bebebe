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

    public event Action<(int X, int Y)> OnPlotMarked;
    public event Action<List<(int X, int Y)>> OnPlotsBatchMarked;
    public event Action<(int X, int Y)> OnPlotUnmarked;
    public event Action<List<(int X, int Y)>> OnPlotsBatchUnmarked;
    public event Action<(int X, int Y)> OnPlotCompleted;

    public void MarkPlot(int x, int y, bool[,] treeOnGrass)
    {
        lock (_lock)
        {
            if (_completedBeds.Contains((x, y))) return;

            if (_markedPlots.Add((x, y)))
            {
                if (treeOnGrass != null && treeOnGrass[x, y])
                {
                    TreeJobManager.Instance.MarkTree(x, y);
                }

                JobBroker.Instance.RegisterFarmPlot(x, y);
                Callable.From(() => OnPlotMarked?.Invoke((x, y))).CallDeferred();
            }
        }
    }

    public void MarkPlotsBatch(List<(int X, int Y)> plots, bool[,] treeOnGrass)
    {
        if (plots == null || plots.Count == 0) return;

        var addedList = new List<(int X, int Y)>(plots.Count);
        var treesToChop = new List<(int X, int Y)>();

        lock (_lock)
        {
            foreach (var pos in plots)
            {
                if (!_completedBeds.Contains(pos) && _markedPlots.Add(pos))
                {
                    addedList.Add(pos);
                    if (treeOnGrass != null && treeOnGrass[pos.X, pos.Y])
                    {
                        treesToChop.Add(pos);
                    }
                }
            }
        }

        if (addedList.Count > 0)
        {
            JobBroker.Instance.RegisterFarmPlotBatch(addedList);
            if (treesToChop.Count > 0)
            {
                TreeJobManager.Instance.MarkTreesBatch(treesToChop);
            }
            Callable.From(() => OnPlotsBatchMarked?.Invoke(addedList)).CallDeferred();
        }
    }

    public void UnmarkPlot(int x, int y)
    {
        lock (_lock)
        {
            if (_markedPlots.Remove((x, y)))
            {
                JobBroker.Instance.UnregisterFarmPlot(x, y);
                Callable.From(() => OnPlotUnmarked?.Invoke((x, y))).CallDeferred();
            }
        }
    }

    public void UnmarkPlotsBatch(List<(int X, int Y)> plots)
    {
        if (plots == null || plots.Count == 0) return;

        var removedList = new List<(int X, int Y)>(plots.Count);
        lock (_lock)
        {
            foreach (var pos in plots)
            {
                if (_markedPlots.Remove(pos))
                {
                    removedList.Add(pos);
                }
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
            JobBroker.Instance.UnregisterFarmPlot(x, y);
            Callable.From(() => OnPlotCompleted?.Invoke((x, y))).CallDeferred();
        }
    }
}
