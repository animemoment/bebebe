using System;
using System.Collections.Generic;
using Godot;
using Game.Core;

namespace Game.Simulation;

public class StockpileManager
{
    public static StockpileManager Instance { get; } = new();

    private const int ChunkShift = 4;
    private const int ChunkSize = 16;
    private const int ChunkDim = 32;
    private const float ChunkSizePx = ChunkSize * 64f;
    private const int MaxChunkRadius = 32;

    private readonly object _lock = new();
    private readonly HashSet<(int X, int Y)> _zones = new(1024);
    private readonly HashSet<(int X, int Y)>[,] _zoneChunks = new HashSet<(int X, int Y)>[ChunkDim, ChunkDim];
    private readonly Dictionary<(int X, int Y), (ItemId Item, int Count, int ReservedIncoming)> _storage = new(1024);

    private int _freeSlotsCount = 0;
    public bool HasFreeSpace => _freeSlotsCount > 0 && _zones.Count > 0;

    public event Action<(int X, int Y)> OnZoneTileAdded;
    public event Action<(int X, int Y)> OnZoneTileRemoved;
    public event Action<ItemId, int> OnItemCountChanged;

    public StockpileManager()
    {
        for (int cx = 0; cx < ChunkDim; cx++)
        {
            for (int cy = 0; cy < ChunkDim; cy++)
            {
                _zoneChunks[cx, cy] = new HashSet<(int X, int Y)>();
            }
        }
    }

    private static (int CX, int CY) GetChunkCoord(int x, int y)
    {
        int cx = Math.Clamp(x >> ChunkShift, 0, ChunkDim - 1);
        int cy = Math.Clamp(y >> ChunkShift, 0, ChunkDim - 1);
        return (cx, cy);
    }

    public void AddZoneTile(int x, int y)
    {
        lock (_lock)
        {
            if (_zones.Add((x, y)))
            {
                if (!_storage.ContainsKey((x, y)))
                {
                    _storage[(x, y)] = (ItemId.None, 0, 0);
                    _freeSlotsCount++;
                }

                var (cx, cy) = GetChunkCoord(x, y);
                _zoneChunks[cx, cy].Add((x, y));

                Callable.From(() => OnZoneTileAdded?.Invoke((x, y))).CallDeferred();
            }
        }
    }

    public void RemoveZoneTile(int x, int y)
    {
        lock (_lock)
        {
            if (_zones.Remove((x, y)))
            {
                var (cx, cy) = GetChunkCoord(x, y);
                _zoneChunks[cx, cy].Remove((x, y));

                if (_storage.TryGetValue((x, y), out var entry))
                {
                    _storage.Remove((x, y));
                    var def = entry.Item != ItemId.None ? ItemRegistry.Get(entry.Item) : ItemRegistry.Log;
                    if (entry.Count + entry.ReservedIncoming < def.MaxStack)
                    {
                        _freeSlotsCount = Math.Max(0, _freeSlotsCount - 1);
                    }

                    if (entry.Item != ItemId.None && entry.Count > 0)
                    {
                        var item = entry.Item;
                        int total = GetTotalItemCountInternal(item);
                        Callable.From(() => OnItemCountChanged?.Invoke(item, total)).CallDeferred();
                    }
                }

                Callable.From(() => OnZoneTileRemoved?.Invoke((x, y))).CallDeferred();
            }
        }
    }

    public bool IsZoneTile(int x, int y)
    {
        lock (_lock)
        {
            return _zones.Contains((x, y));
        }
    }

    public int GetTotalItemCount(ItemId itemId)
    {
        lock (_lock)
        {
            return GetTotalItemCountInternal(itemId);
        }
    }

    private int GetTotalItemCountInternal(ItemId itemId)
    {
        int total = 0;
        foreach (var entry in _storage.Values)
        {
            if (entry.Item == itemId)
            {
                total += entry.Count;
            }
        }
        return total;
    }

    public bool TryReserveStockpileSlot(System.Numerics.Vector2 agentPos, ItemId itemId, int countToDeposit, out (int X, int Y) slot, out int acceptedCount)
    {
        slot = (-1, -1);
        acceptedCount = 0;

        if (_zones.Count == 0 || _freeSlotsCount <= 0) 
            return false;

        lock (_lock)
        {
            if (_zones.Count == 0 || _freeSlotsCount <= 0) 
                return false;

            var def = ItemRegistry.Get(itemId);
            float bestDistSq = float.MaxValue;
            int agentTileX = (int)(agentPos.X / 64f);
            int agentTileY = (int)(agentPos.Y / 64f);

            int startCx = Math.Clamp((int)(agentPos.X / ChunkSizePx), 0, ChunkDim - 1);
            int startCy = Math.Clamp((int)(agentPos.Y / ChunkSizePx), 0, ChunkDim - 1);

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

                        var chunk = _zoneChunks[cx, cy];
                        if (chunk.Count == 0) continue;

                        foreach (var tile in chunk)
                        {
                            if (!_storage.TryGetValue(tile, out var entry))
                            {
                                entry = (ItemId.None, 0, 0);
                                _storage[tile] = entry;
                            }

                            int totalCount = entry.Count + entry.ReservedIncoming;

                            if (totalCount < def.MaxStack && (entry.Item == ItemId.None || entry.Item == itemId))
                            {
                                float dx = tile.X - agentTileX;
                                float dy = tile.Y - agentTileY;
                                float distSq = dx * dx + dy * dy;

                                if (distSq < bestDistSq)
                                {
                                    bestDistSq = distSq;
                                    slot = tile;
                                    acceptedCount = Math.Min(countToDeposit, def.MaxStack - totalCount);
                                }
                            }
                        }
                    }
                }

                // Ранний выход как только нашли подходящий слот в текущем радиусе
                if (slot.X != -1 && acceptedCount > 0)
                    break;
            }

            if (slot.X != -1 && acceptedCount > 0)
            {
                var entry = _storage[slot];
                int newTotal = entry.Count + entry.ReservedIncoming + acceptedCount;
                _storage[slot] = (itemId, entry.Count, entry.ReservedIncoming + acceptedCount);

                if (newTotal >= def.MaxStack)
                {
                    _freeSlotsCount = Math.Max(0, _freeSlotsCount - 1);
                }
                return true;
            }

            return false;
        }
    }

    public void CancelReservation(int x, int y, int count)
    {
        lock (_lock)
        {
            if (_storage.TryGetValue((x, y), out var entry))
            {
                var def = entry.Item != ItemId.None ? ItemRegistry.Get(entry.Item) : ItemRegistry.Log;
                int wasTotal = entry.Count + entry.ReservedIncoming;

                int newReserved = Math.Max(0, entry.ReservedIncoming - count);
                _storage[(x, y)] = (entry.Item, entry.Count, newReserved);

                if (wasTotal >= def.MaxStack && (entry.Count + newReserved) < def.MaxStack)
                {
                    _freeSlotsCount++;
                }
            }
        }
    }

    public void DepositItems(int x, int y, ItemId itemId, int count)
    {
        lock (_lock)
        {
            if (_storage.TryGetValue((x, y), out var entry))
            {
                var def = ItemRegistry.Get(itemId);
                int wasTotal = entry.Count + entry.ReservedIncoming;

                int newReserved = Math.Max(0, entry.ReservedIncoming - count);
                int newCount = entry.Count + count;
                _storage[(x, y)] = (itemId, newCount, newReserved);

                if (wasTotal >= def.MaxStack && (newCount + newReserved) < def.MaxStack)
                {
                    _freeSlotsCount++;
                }

                int total = GetTotalItemCountInternal(itemId);
                Callable.From(() => OnItemCountChanged?.Invoke(itemId, total)).CallDeferred();
            }
        }
    }

    public int WithdrawItems(int x, int y, int count)
    {
        lock (_lock)
        {
            if (_storage.TryGetValue((x, y), out var entry) && entry.Count > 0)
            {
                var def = ItemRegistry.Get(entry.Item);
                int wasTotal = entry.Count + entry.ReservedIncoming;

                int toTake = Math.Min(entry.Count, count);
                int newCount = entry.Count - toTake;
                var item = newCount == 0 ? ItemId.None : entry.Item;
                _storage[(x, y)] = (item, newCount, entry.ReservedIncoming);

                if (wasTotal >= def.MaxStack && (newCount + entry.ReservedIncoming) < def.MaxStack)
                {
                    _freeSlotsCount++;
                }

                var oldItem = entry.Item;
                int total = GetTotalItemCountInternal(oldItem);
                Callable.From(() => OnItemCountChanged?.Invoke(oldItem, total)).CallDeferred();
                return toTake;
            }
            return 0;
        }
    }
}