using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Numerics;
using Game.Core;

namespace Game.Simulation;

public class GroundItemManager
{
    public static GroundItemManager Instance { get; } = new();

    private const int ChunkShift = 4;
    private const int ChunkSize = 16;
    private const int ChunkDim = 32;
    private const float ChunkSizePx = ChunkSize * 64f;
    private const int MaxChunkRadius = 32;

    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), (ItemId Item, int Count, int Reserved)> _groundItems = new(2048);
    private readonly HashSet<(int X, int Y)>[,] _itemChunks = new HashSet<(int X, int Y)>[ChunkDim, ChunkDim];

    private int _totalAvailableLogs = 0;
    public bool HasAvailableLogs { get { lock (_lock) return _totalAvailableLogs > 0; } }

    private const int MaxSnapshotItems = 8192;
    private const int RingSize = 4;
    private readonly Vector2[][] _snapshotRing = new Vector2[RingSize][];
    private int _ringIndex = 0;
    private bool _isDirty = true;

    public ConcurrentQueue<(Vector2[] Buffer, int Count)> ItemPositionsQueue { get; } = new();

    public GroundItemManager()
    {
        for (int i = 0; i < RingSize; i++)
        {
            _snapshotRing[i] = new Vector2[MaxSnapshotItems];
        }

        for (int cx = 0; cx < ChunkDim; cx++)
        {
            for (int cy = 0; cy < ChunkDim; cy++)
            {
                _itemChunks[cx, cy] = new HashSet<(int X, int Y)>();
            }
        }
    }

    private static (int CX, int CY) GetChunkCoord(int x, int y)
    {
        int cx = Math.Clamp(x >> ChunkShift, 0, ChunkDim - 1);
        int cy = Math.Clamp(y >> ChunkShift, 0, ChunkDim - 1);
        return (cx, cy);
    }

    public void SpawnItems(int x, int y, ItemId id, int count)
    {
        bool isOutsideStockpile = false;

        lock (_lock)
        {
            if (_groundItems.TryGetValue((x, y), out var entry) && entry.Item == id)
            {
                _groundItems[(x, y)] = (id, entry.Count + count, entry.Reserved);
            }
            else
            {
                _groundItems[(x, y)] = (id, count, 0);
                var (cx, cy) = GetChunkCoord(x, y);
                _itemChunks[cx, cy].Add((x, y));
            }

            if (id == ItemId.Log)
            {
                _totalAvailableLogs += count;
            }

            _isDirty = true;
            isOutsideStockpile = !StockpileManager.Instance.IsZoneTile(x, y);
        }

        if (isOutsideStockpile && StockpileManager.Instance.HasFreeSpace)
        {
            JobBroker.Instance.RegisterStockpileHaul(x, y);
        }
    }

    public bool HasItemsAt(int x, int y)
    {
        lock (_lock)
        {
            return _groundItems.TryGetValue((x, y), out var entry) && entry.Count > 0;
        }
    }

    public int GetItemCountAt(int x, int y)
    {
        lock (_lock)
        {
            return _groundItems.TryGetValue((x, y), out var entry) ? entry.Count : 0;
        }
    }

    public bool TryReserveGroundItems(Vector2 agentPos, float maxWeightCapacity, bool allowFromStockpile, out (int X, int Y) cell, out ItemId id, out int reservedCount)
    {
        lock (_lock)
        {
            cell = (-1, -1);
            id = ItemId.None;
            reservedCount = 0;

            if (_totalAvailableLogs <= 0 || _groundItems.Count == 0)
                return false;

            int startCx = Math.Clamp((int)(agentPos.X / ChunkSizePx), 0, ChunkDim - 1);
            int startCy = Math.Clamp((int)(agentPos.Y / ChunkSizePx), 0, ChunkDim - 1);

            int agentTileX = (int)(agentPos.X / 64f);
            int agentTileY = (int)(agentPos.Y / 64f);

            float bestDistSq = float.MaxValue;

            for (int r = 0; r < MaxChunkRadius; r++)
            {
                int minCx = Math.Max(0, startCx - r);
                int maxCx = Math.Min(ChunkDim - 1, startCx + r);
                int minCy = Math.Max(0, startCy - r);
                int maxCy = Math.Min(ChunkDim - 1, startCy + r);

                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    for (int cy = minCy; cy <= maxCy; cy++)
                    {
                        if (r > 0 && cx > minCx && cx < maxCx && cy > minCy && cy < maxCy)
                            continue;

                        var chunk = _itemChunks[cx, cy];
                        if (chunk.Count == 0) continue;

                        foreach (var pos in chunk)
                        {
                            if (!_groundItems.TryGetValue(pos, out var entry)) continue;

                            bool isStockpile = StockpileManager.Instance.IsZoneTile(pos.X, pos.Y);
                            if (!allowFromStockpile && isStockpile) continue;

                            int available = entry.Count - entry.Reserved;
                            if (available <= 0) continue;

                            var def = ItemRegistry.Get(entry.Item);
                            int maxCanTake = (int)(maxWeightCapacity / def.Weight);
                            if (maxCanTake <= 0) continue;

                            float dx = pos.X - agentTileX;
                            float dy = pos.Y - agentTileY;
                            float distSq = dx * dx + dy * dy;

                            if (distSq < bestDistSq)
                            {
                                bestDistSq = distSq;
                                cell = pos;
                                id = entry.Item;
                                reservedCount = Math.Min(available, maxCanTake);
                            }
                        }
                    }
                }

                // Ранний выход как только нашли ресурс в текущем радиусе
                if (cell.X != -1 && reservedCount > 0)
                    break;
            }

            if (cell.X != -1 && reservedCount > 0)
            {
                var entry = _groundItems[cell];
                _groundItems[cell] = (entry.Item, entry.Count, entry.Reserved + reservedCount);
                if (entry.Item == ItemId.Log)
                {
                    _totalAvailableLogs = Math.Max(0, _totalAvailableLogs - reservedCount);
                }
                return true;
            }

            return false;
        }
    }

    public bool TryReserveSpecificCell(int x, int y, float maxWeightCapacity, out ItemId id, out int reservedCount)
    {
        lock (_lock)
        {
            id = ItemId.None;
            reservedCount = 0;

            if (_groundItems.TryGetValue((x, y), out var entry))
            {
                int available = entry.Count - entry.Reserved;
                if (available <= 0) return false;

                var def = ItemRegistry.Get(entry.Item);
                int maxCanTake = (int)(maxWeightCapacity / def.Weight);
                if (maxCanTake <= 0) return false;

                id = entry.Item;
                reservedCount = Math.Min(available, maxCanTake);
                _groundItems[(x, y)] = (entry.Item, entry.Count, entry.Reserved + reservedCount);
                if (entry.Item == ItemId.Log)
                {
                    _totalAvailableLogs = Math.Max(0, _totalAvailableLogs - reservedCount);
                }
                return true;
            }

            return false;
        }
    }

    public int TakeItems(int x, int y, int count)
    {
        lock (_lock)
        {
            if (_groundItems.TryGetValue((x, y), out var entry))
            {
                int toTake = Math.Min(entry.Count, count);
                int newCount = entry.Count - toTake;
                int newReserved = Math.Max(0, entry.Reserved - toTake);

                if (newCount <= 0)
                {
                    _groundItems.Remove((x, y));
                    var (cx, cy) = GetChunkCoord(x, y);
                    _itemChunks[cx, cy].Remove((x, y));
                }
                else
                {
                    _groundItems[(x, y)] = (entry.Item, newCount, newReserved);
                }

                if (StockpileManager.Instance.IsZoneTile(x, y))
                {
                    StockpileManager.Instance.WithdrawItems(x, y, toTake);
                }

                _isDirty = true;
                return toTake;
            }
            return 0;
        }
    }

    public void ReleaseReservation(int x, int y, int count)
    {
        lock (_lock)
        {
            if (_groundItems.TryGetValue((x, y), out var entry))
            {
                int newReserved = Math.Max(0, entry.Reserved - count);
                _groundItems[(x, y)] = (entry.Item, entry.Count, newReserved);
                if (entry.Item == ItemId.Log)
                {
                    _totalAvailableLogs += count;
                }
            }
        }
    }

    public void GenerateSnapshot()
    {
        lock (_lock)
        {
            if (!_isDirty) return;
            _isDirty = false;

            var buffer = _snapshotRing[_ringIndex];
            int count = 0;

            foreach (var (pos, entry) in _groundItems)
            {
                if (entry.Count > 0 && count < MaxSnapshotItems)
                {
                    buffer[count++] = new Vector2(pos.X * 64f + 32f, pos.Y * 64f + 32f);
                }
            }

            _ringIndex = (_ringIndex + 1) % RingSize;
            ItemPositionsQueue.Enqueue((buffer, count));

            while (ItemPositionsQueue.Count > 2)
                ItemPositionsQueue.TryDequeue(out _);
        }
    }
}