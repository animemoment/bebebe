using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
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

    private const int MaxItemTypes = 8;
    private const int MaxSnapshotItemsPerType = 4096;

    private bool _isDirty = true;

    public ConcurrentQueue<(System.Numerics.Vector2[][] PositionsByItem, int[] Counts)> SnapshotQueue { get; } = new();

    private int _freeSlotsCount = 0;
    private int _zoneCount = 0;
    public bool HasFreeSpace => Volatile.Read(ref _freeSlotsCount) > 0 && Volatile.Read(ref _zoneCount) > 0;

    // Троттлинг событий OnItemCountChanged: тоталы копятся под lock, рассылка не чаще 200мс.
    private const float ItemEventThrottleSec = 0.2f;
    private readonly Dictionary<ItemId, int> _pendingItemTotals = new(8);
    private readonly object _pendingItemLock = new();
    private float _itemEventTimer;
    private bool _itemEventDirty;

    public event Action<(int X, int Y)> OnZoneTileAdded;
    public event Action<(int X, int Y)> OnZoneTileRemoved;
    public event Action<ItemId, int> OnItemCountChanged;

    // Кэш тоталов для HUD без O(N) скана (обновляется под _lock, читается lock-free).
    private readonly int[] _cachedTotals = new int[8];
    public int GetTotalItemCount(ItemId id) => Volatile.Read(ref _cachedTotals[(int)id]);

    /// <summary>Тик троттлинга событий склада. Вызывать из фонового потока раз в тик Phase2.</summary>
    public void TickEventThrottle(float deltaTime)
    {
        if (!_itemEventDirty) return;
        _itemEventTimer += deltaTime;
        if (_itemEventTimer < ItemEventThrottleSec) return;
        _itemEventTimer = 0f;
        _itemEventDirty = false;
        KeyValuePair<ItemId, int>[] batch;
        lock (_pendingItemLock)
        {
            batch = new KeyValuePair<ItemId, int>[_pendingItemTotals.Count];
            int i = 0;
            foreach (var kvp in _pendingItemTotals) batch[i++] = kvp;
            _pendingItemTotals.Clear();
        }
        for (int i = 0; i < batch.Length; i++)
        {
            var kvp = batch[i];
            Godot.Callable.From(() => OnItemCountChanged?.Invoke(kvp.Key, kvp.Value)).CallDeferred();
        }
    }

    private void QueueItemCountChanged(ItemId item, int total)
    {
        Volatile.Write(ref _cachedTotals[(int)item], total);
        lock (_pendingItemLock)
        {
            _pendingItemTotals[item] = total;
        }
        _itemEventDirty = true;
    }

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
                Interlocked.Increment(ref _zoneCount);
                if (!_storage.ContainsKey((x, y)))
                {
                    _storage[(x, y)] = (ItemId.None, 0, 0);
                    _freeSlotsCount++;
                }

                var (cx, cy) = GetChunkCoord(x, y);
                _zoneChunks[cx, cy].Add((x, y));

                _isDirty = true;

                Callable.From(() => OnZoneTileAdded?.Invoke((x, y))).CallDeferred();
            }
        }

        // Только что нарисованный склад: немедленно добиваем haul-работы для
        // предметов, лежащих на земле (иначе ждать плановый sweep ~1 секунду).
        JobBroker.Instance.SweepStockpileHaulJobs();
    }

    public void RemoveZoneTile(int x, int y)
    {
        lock (_lock)
        {
            if (_zones.Remove((x, y)))
            {
                Interlocked.Decrement(ref _zoneCount);
                var (cx, cy) = GetChunkCoord(x, y);
                _zoneChunks[cx, cy].Remove((x, y));

                if (_storage.TryGetValue((x, y), out var entry))
                {
                    _storage.Remove((x, y));
                    _isDirty = true;
                    var def = entry.Item != ItemId.None ? ItemRegistry.Get(entry.Item) : ItemRegistry.Log;
                    if (entry.Count + entry.ReservedIncoming < def.MaxStack)
                    {
                        _freeSlotsCount = Math.Max(0, _freeSlotsCount - 1);
                    }

                    if (entry.Item != ItemId.None && entry.Count > 0)
                    {
                        var item = entry.Item;
                        int total = GetTotalItemCountInternal(item);
                        QueueItemCountChanged(item, total);
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
                _isDirty = true;

                if (wasTotal >= def.MaxStack && (newCount + newReserved) < def.MaxStack)
                {
                    _freeSlotsCount++;
                }

                int total = GetTotalItemCountInternal(itemId);
                QueueItemCountChanged(itemId, total);
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
                _isDirty = true;

                if (wasTotal >= def.MaxStack && (newCount + entry.ReservedIncoming) < def.MaxStack)
                {
                    _freeSlotsCount++;
                }

                var oldItem = entry.Item;
                int total = GetTotalItemCountInternal(oldItem);
                QueueItemCountChanged(oldItem, total);
                return toTake;
            }
            return 0;
        }
    }

    /// <summary>
    /// Снапшот содержимого склада для StockpileItemRenderer.
    /// По одной иконке на НЕПУСТУЮ клетку (Count &gt; 0), позиция = тайл*64+32.
    /// Образец — GroundItemManager.GenerateSnapshot: копия под коротким lock,
    /// построение буферов ВНЕ секции, без Godot API.
    /// </summary>
    public void GenerateSnapshot()
    {
        KeyValuePair<(int X, int Y), (ItemId Item, int Count, int ReservedIncoming)>[] storageCopy;
        lock (_lock)
        {
            if (!_isDirty) return;
            _isDirty = false;

            storageCopy = new KeyValuePair<(int X, int Y), (ItemId Item, int Count, int ReservedIncoming)>[_storage.Count];
            int ci = 0;
            foreach (var kvp in _storage)
                storageCopy[ci++] = kvp;
        }

        var currentSnap = new System.Numerics.Vector2[MaxItemTypes][];
        for (int t = 0; t < MaxItemTypes; t++)
            currentSnap[t] = new System.Numerics.Vector2[MaxSnapshotItemsPerType];
        var currentCounts = new int[MaxItemTypes];

        for (int i = 0; i < storageCopy.Length; i++)
        {
            var pos = storageCopy[i].Key;
            var entry = storageCopy[i].Value;
            if (entry.Count > 0 && entry.Item != ItemId.None)
            {
                int itemIdx = (int)entry.Item;
                if (itemIdx >= 0 && itemIdx < MaxItemTypes)
                {
                    int count = currentCounts[itemIdx];
                    if (count < MaxSnapshotItemsPerType)
                    {
                        currentSnap[itemIdx][count] = new System.Numerics.Vector2(pos.X * 64f + 32f, pos.Y * 64f + 32f);
                        currentCounts[itemIdx]++;
                    }
                }
            }
        }

        SnapshotQueue.Enqueue((currentSnap, currentCounts));

        while (SnapshotQueue.Count > 2)
            SnapshotQueue.TryDequeue(out _);
    }
}