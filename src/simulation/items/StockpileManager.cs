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

    // Finding 6: ring из 3 снапшот-буферов (см. GroundItemManager) — ноль
    // аллокаций на снапшот после первого прохода. Рендерер drain'ит очередь
    // до последнего, cap = 2, ротация = 3: перезаписи читаемого нет.
    private readonly System.Numerics.Vector2[][][] _snapRing = new System.Numerics.Vector2[3][][];
    private readonly int[][] _snapCountsRing = new int[3][];
    private int _snapCursor;

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

    /// <summary>
    /// Батчевое добавление зоны склада (drag 100×100 = 10k клеток одним вызовом).
    /// Раньше WarehouseTool звал AddZoneTile на клетку: 10k lock + 10k CallDeferred
    /// + 10k SweepStockpileHaulJobs (каждый — O(N) скан склада) = фриз.
    /// Здесь: один lock, один CallDeferred-батч, один sweep в конце.
    /// </summary>
    public void AddZoneTilesBatch(System.Collections.Generic.List<(int X, int Y)> tiles)
    {
        if (tiles == null || tiles.Count == 0) return;
        var added = new System.Collections.Generic.List<(int X, int Y)>(tiles.Count);
        lock (_lock)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                var (x, y) = tiles[i];
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
                    added.Add((x, y));
                }
            }
            if (added.Count > 0)
                _isDirty = true;
        }
        if (added.Count > 0)
        {
            var snapshot = added.ToArray();
            Callable.From(() => OnZoneTilesBatchAdded?.Invoke(snapshot)).CallDeferred();
            JobBroker.Instance.SweepStockpileHaulJobs();
        }
    }

    /// <summary>Событие батчевого добавления тайлов зоны (один CallDeferred на drag).</summary>
    public event Action<(int X, int Y)[]> OnZoneTilesBatchAdded;

    /// <summary>
    /// Батчевое удаление зоны склада: один lock, один sweep не нужен
    /// (удаление не создаёт работ).
    /// </summary>
    public void RemoveZoneTilesBatch(System.Collections.Generic.List<(int X, int Y)> tiles)
    {
        if (tiles == null || tiles.Count == 0) return;
        var removed = new System.Collections.Generic.List<(int X, int Y)>(tiles.Count);
        lock (_lock)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                var (x, y) = tiles[i];
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
                            AddLiveTotal(entry.Item, -entry.Count);
                            int total = GetTotalItemCountInternal(entry.Item);
                            QueueItemCountChanged(entry.Item, total);
                        }
                    }
                    removed.Add((x, y));
                }
            }
        }
        if (removed.Count > 0)
        {
            var snapshot = removed.ToArray();
            Callable.From(() => OnZoneTilesBatchRemoved?.Invoke(snapshot)).CallDeferred();
        }
    }

    /// <summary>Событие батчевого удаления тайлов зоны.</summary>
    public event Action<(int X, int Y)[]> OnZoneTilesBatchRemoved;

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

    // Инкрементальные тоталы: GetTotalItemCountInternal делал O(N)-скан всего
    // _storage под _lock на КАЖДЫЙ Deposit/Withdraw (сотни раз/с при активной
    // работе склада × 1000 записей = 100k итераций/с под глобальным lock).
    // Ведём +=/-= в точках изменения, чтение — Volatile (уже есть _cachedTotals).
    private readonly int[] _liveTotals = new int[8];

    private int GetTotalItemCountInternal(ItemId itemId)
    {
        int idx = (int)itemId;
        if ((uint)idx < 8)
            return Volatile.Read(ref _liveTotals[idx]);
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

    private void AddLiveTotal(ItemId item, int delta)
    {
        int idx = (int)item;
        if (item != ItemId.None && (uint)idx < 8)
            _liveTotals[idx] += delta;
    }

    public bool TryReserveStockpileSlot(System.Numerics.Vector2 agentPos, ItemId itemId, int countToDeposit, out (int X, int Y) slot, out int acceptedCount)
    {
        slot = (-1, -1);
        acceptedCount = 0;

        // #7: early-out только через Volatile.Read атомарных счётчиков.
        // _zones.Count (HashSet.Count) без lock читать нельзя — гонка даёт
        // stale-значение; используем _zoneCount (ведётся через Interlocked).
        if (Volatile.Read(ref _zoneCount) <= 0 || Volatile.Read(ref _freeSlotsCount) <= 0)
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

    /// <summary>
    /// #1: резерв еды со склада для NeedsJobSystem. Складская еда живёт только
    /// в _storage (DepositItems НЕ пишет в GroundItemManager), поэтому голодный
    /// агент при полном складе и пустой земле раньше уходил в backoff 30–60с.
    /// Атомарно резервирует eatCount зерна ближайшей клетки (спираль по чанкам,
    /// как TryReserveStockpileSlot) и сразу списывает из _storage+тоталов:
    /// резервация == взятие, отдельного release не нужно (еда съедается сразу).
    /// Возвращает false — еды на складе нет / не хватило.
    /// </summary>
    public bool TryTakeStockpileFood(System.Numerics.Vector2 agentPos, ItemId itemId, int eatCount, int maxTileRadius, out (int X, int Y) cell)
    {
        cell = (-1, -1);
        if (eatCount <= 0 || itemId == ItemId.None) return false;
        if (Volatile.Read(ref _zoneCount) <= 0) return false;
        if (Volatile.Read(ref _liveTotals[(int)itemId]) < eatCount) return false;

        lock (_lock)
        {
            if (_zones.Count == 0) return false;
            if (Volatile.Read(ref _liveTotals[(int)itemId]) < eatCount) return false;

            int agentTileX = (int)(agentPos.X / 64f);
            int agentTileY = (int)(agentPos.Y / 64f);
            int startCx = Math.Clamp((int)(agentPos.X / ChunkSizePx), 0, ChunkDim - 1);
            int startCy = Math.Clamp((int)(agentPos.Y / ChunkSizePx), 0, ChunkDim - 1);
            int radius = Math.Clamp(maxTileRadius <= 0 ? MaxChunkRadius : (maxTileRadius + ChunkSize - 1) / ChunkSize, 1, MaxChunkRadius);

            float bestDistSq = float.MaxValue;
            (int X, int Y) best = (-1, -1);
            for (int r = 0; r < radius; r++)
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
                            if (!_storage.TryGetValue(tile, out var entry)) continue;
                            if (entry.Item != itemId || entry.Count < eatCount) continue;
                            float dx = tile.X - agentTileX;
                            float dy = tile.Y - agentTileY;
                            float distSq = dx * dx + dy * dy;
                            if (distSq < bestDistSq)
                            {
                                bestDistSq = distSq;
                                best = tile;
                            }
                        }
                    }
                }
                if (best.X != -1) break;
            }
            if (best.X == -1) return false;

            var e = _storage[best];
            if (e.Item != itemId || e.Count < eatCount) return false;
            var def = ItemRegistry.Get(e.Item);
            int wasTotal = e.Count + e.ReservedIncoming;
            int newCount = e.Count - eatCount;
            var kept = newCount == 0 ? ItemId.None : e.Item;
            _storage[best] = (kept, newCount, e.ReservedIncoming);
            _isDirty = true;
            if (wasTotal >= def.MaxStack && (newCount + e.ReservedIncoming) < def.MaxStack)
                _freeSlotsCount++;
            AddLiveTotal(e.Item, -eatCount);
            QueueItemCountChanged(e.Item, Volatile.Read(ref _liveTotals[(int)e.Item]));
            cell = best;
            return true;
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

                AddLiveTotal(itemId, count);
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
                AddLiveTotal(oldItem, -toTake);
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

        // Finding 6: ring-буфер вместо fresh-аллокации (см. GroundItemManager).
        int slot = _snapCursor % 3;
        _snapCursor++;
        var currentSnap = _snapRing[slot];
        var currentCounts = _snapCountsRing[slot];
        if (currentSnap == null)
        {
            currentSnap = new System.Numerics.Vector2[MaxItemTypes][];
            for (int t = 0; t < MaxItemTypes; t++)
                currentSnap[t] = new System.Numerics.Vector2[MaxSnapshotItemsPerType];
            currentCounts = new int[MaxItemTypes];
            _snapRing[slot] = currentSnap;
            _snapCountsRing[slot] = currentCounts;
        }
        else
        {
            Array.Clear(currentCounts, 0, currentCounts.Length);
        }

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