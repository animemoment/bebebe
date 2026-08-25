using System;
using System.Collections.Generic;
using Godot;

namespace Game.Simulation;

public class TreeJobManager
{
    public static TreeJobManager Instance { get; } = new();

    private readonly object _lock = new();
    private readonly HashSet<(int X, int Y)> _markedTrees = new(4096);

    public event Action<(int X, int Y)> OnTreeMarked;
    public event Action<List<(int X, int Y)>> OnTreesBatchMarked;
    public event Action<(int X, int Y)> OnTreeUnmarked;
    public event Action<List<(int X, int Y)>> OnTreesBatchUnmarked;
    public event Action<(int X, int Y)> OnTreeChopped;

    public void MarkTree(int x, int y)
    {
        lock (_lock)
        {
            if (_markedTrees.Add((x, y)))
            {
                JobBroker.Instance.RegisterTreeChop(x, y);
                Callable.From(() => OnTreeMarked?.Invoke((x, y))).CallDeferred();
            }
        }
    }

    public void MarkTreesBatch(List<(int X, int Y)> trees, SimulationContext ctx = null)
    {
        if (trees == null || trees.Count == 0) return;

        var addedList = new List<(int X, int Y)>(trees.Count);
        lock (_lock)
        {
            foreach (var pos in trees)
            {
                if (_markedTrees.Add(pos))
                {
                    addedList.Add(pos);
                }
            }
        }

        if (addedList.Count > 0)
        {
            JobBroker.Instance.RegisterTreeChopBatch(addedList, ctx);
            Callable.From(() => OnTreesBatchMarked?.Invoke(addedList)).CallDeferred();
        }
    }

    public void UnmarkTree(int x, int y)
    {
        lock (_lock)
        {
            if (_markedTrees.Remove((x, y)))
            {
                JobBroker.Instance.UnregisterTreeChop(x, y);
                Callable.From(() => OnTreeUnmarked?.Invoke((x, y))).CallDeferred();
            }
        }
    }

    public void UnmarkTreesBatch(List<(int X, int Y)> trees)
    {
        if (trees == null || trees.Count == 0) return;

        var removedList = new List<(int X, int Y)>(trees.Count);
        lock (_lock)
        {
            foreach (var pos in trees)
            {
                if (_markedTrees.Remove(pos))
                {
                    removedList.Add(pos);
                }
            }
        }

        if (removedList.Count > 0)
        {
            JobBroker.Instance.UnregisterTreeChopBatch(removedList);
            Callable.From(() => OnTreesBatchUnmarked?.Invoke(removedList)).CallDeferred();
        }
    }

    public bool IsTreeMarked(int x, int y)
    {
        lock (_lock)
        {
            return _markedTrees.Contains((x, y));
        }
    }

    public void CompleteTree(int x, int y)
    {
        lock (_lock)
        {
            _markedTrees.Remove((x, y));
            JobBroker.Instance.UnregisterTreeChop(x, y);
            Callable.From(() => OnTreeChopped?.Invoke((x, y))).CallDeferred();
        }
    }
}
