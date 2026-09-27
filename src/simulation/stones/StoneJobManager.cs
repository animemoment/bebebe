using System;
using System.Collections.Generic;
using Godot;

namespace Game.Simulation;

/// <summary>
/// Метки каменных россыпей под добычу (близнец TreeJobManager, но для Mining).
/// Работает парой с MiningJobHandler: метка → JobBroker.RegisterStoneMineBatch →
/// добыча → CompleteStone (камень исчезает, падает ItemId.Stone на землю).
/// </summary>
public class StoneJobManager
{
    public static StoneJobManager Instance { get; } = new();

    private readonly object _lock = new();
    private readonly HashSet<(int X, int Y)> _markedStones = new(4096);

    public event Action<(int X, int Y)> OnStoneMarked;
    public event Action<List<(int X, int Y)>> OnStonesBatchMarked;
    public event Action<(int X, int Y)> OnStoneUnmarked;
    public event Action<List<(int X, int Y)>> OnStonesBatchUnmarked;
    public event Action<(int X, int Y)> OnStoneMined;

    public void MarkStone(int x, int y, bool[,] stoneOnGrass)
    {
        lock (_lock)
        {
            if (stoneOnGrass != null && !stoneOnGrass[x, y])
                return;
            if (_markedStones.Add((x, y)))
            {
                JobBroker.Instance.RegisterStoneMine(x, y);
                Callable.From(() => OnStoneMarked?.Invoke((x, y))).CallDeferred();
            }
        }
    }

    public void MarkStonesBatch(List<(int X, int Y)> stones, bool[,] stoneOnGrass = null, SimulationContext ctx = null)
    {
        if (stones == null || stones.Count == 0) return;

        var addedList = new List<(int X, int Y)>(stones.Count);
        lock (_lock)
        {
            foreach (var pos in stones)
            {
                if (stoneOnGrass != null)
                {
                    if ((uint)pos.X >= (uint)stoneOnGrass.GetLength(0) || (uint)pos.Y >= (uint)stoneOnGrass.GetLength(1))
                        continue;
                    if (!stoneOnGrass[pos.X, pos.Y])
                        continue;
                }
                if (_markedStones.Add(pos))
                    addedList.Add(pos);
            }
        }

        if (addedList.Count > 0)
        {
            JobBroker.Instance.RegisterStoneMineBatch(addedList, ctx);
            Callable.From(() => OnStonesBatchMarked?.Invoke(addedList)).CallDeferred();
        }
    }

    public void UnmarkStone(int x, int y)
    {
        lock (_lock)
        {
            if (_markedStones.Remove((x, y)))
            {
                JobBroker.Instance.UnregisterStoneMine(x, y);
                Callable.From(() => OnStoneUnmarked?.Invoke((x, y))).CallDeferred();
            }
        }
    }

    public void UnmarkStonesBatch(List<(int X, int Y)> stones)
    {
        if (stones == null || stones.Count == 0) return;

        var removedList = new List<(int X, int Y)>(stones.Count);
        lock (_lock)
        {
            foreach (var pos in stones)
            {
                if (_markedStones.Remove(pos))
                    removedList.Add(pos);
            }
        }

        if (removedList.Count > 0)
        {
            JobBroker.Instance.UnregisterStoneMineBatch(removedList);
            Callable.From(() => OnStonesBatchUnmarked?.Invoke(removedList)).CallDeferred();
        }
    }

    public bool IsStoneMarked(int x, int y)
    {
        lock (_lock)
        {
            return _markedStones.Contains((x, y));
        }
    }

    /// <summary>Копия всех меток (для JobValidator — аудит потерь задач).</summary>
    public HashSet<(int X, int Y)> GetAllMarkedStones()
    {
        lock (_lock)
        {
            return new HashSet<(int X, int Y)>(_markedStones);
        }
    }

    public void CompleteStone(int x, int y)
    {
        lock (_lock)
        {
            _markedStones.Remove((x, y));
            JobBroker.Instance.UnregisterStoneMine(x, y);
            Callable.From(() => OnStoneMined?.Invoke((x, y))).CallDeferred();
        }
    }
}
