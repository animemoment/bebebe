using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
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

    private const int MaxItemTypes = 8;
    private const int MaxSnapshotItemsPerType = 4096;

    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), (ItemId Item, int Count, int Reserved)> _groundItems = new(2048);
    private readonly HashSet<(int X, int Y)>[,] _itemChunks = new HashSet<(int X, int Y)>[ChunkDim, ChunkDim];

    private readonly int[] _totalAvailableByType = new int[MaxItemTypes];
    public bool HasAvailableLogs => Volatile.Read(ref _totalAvailableByType[(int)ItemId.Log]) > 0;
    public bool HasAvailableItemsOfType(ItemId id)
    {
        int idx = (int)id;
        if (idx < 0 || idx >= MaxItemTypes) return false;
        return Volatile.Read(ref _totalAvailableByType[idx]) > 0;
    }

    private int GetAvailableCount(ItemId id)
    {
        int idx = (int)id;
        return idx >= 0 && idx < MaxItemTypes ? _totalAvailableByType[idx] : 0;
    }

    private bool _isDirty = true;

    public ConcurrentQueue<(Vector2[][] PositionsByItem, int[] Counts)> SnapshotQueue { get; } = new();

    public GroundItemManager()
    {
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
        bool isOutsideStockpile;
        int placedX = x, placedY = y;

        lock (_lock)
        {
            if (_groundItems.TryGetValue((x, y), out var entry) && entry.Item == id)
            {
                _groundItems[(x, y)] = (id, entry.Count + count, entry.Reserved);
            }
            else if (_groundItems.TryGetValue((x, y), out var other) && other.Count > 0)
            {
                // В клетке лежит другой тип — не перезаписываем чужой стак
                // (там могут висеть чужие резервы: перезапись ломала бы им
                // ReleaseReservation и тоталы). Кладём рядом на свободную/
                // однотипную соседнюю клетку. В крайнем случае дропаем спавн,
                // чтобы счётчики _totalAvailableByType остались консистентны.
                bool placed = false;
                for (int oy = -2; oy <= 2 && !placed; oy++)
                    for (int ox = -2; ox <= 2 && !placed; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        var key = (x + ox, y + oy);
                        if (_groundItems.TryGetValue(key, out var n) && (n.Item != id || n.Count <= 0))
                            continue;
                        if (_groundItems.TryGetValue(key, out var same) && same.Item == id)
                            _groundItems[key] = (id, same.Count + count, same.Reserved);
                        else
                        {
                            _groundItems[key] = (id, count, 0);
                            var (ccx, ccy) = GetChunkCoord(key.Item1, key.Item2);
                            _itemChunks[ccx, ccy].Add(key);
                        }
                        placedX = key.Item1;
                        placedY = key.Item2;
                        placed = true;
                    }
                if (!placed)
                {
                    // 24 соседа заняты чужими стаками — дропаем без изменения тоталов.
                    return;
                }
            }
            else
            {
                _groundItems[(x, y)] = (id, count, 0);
                var (cx, cy) = GetChunkCoord(x, y);
                _itemChunks[cx, cy].Add((x, y));
            }

            int itemIdx = (int)id;
            if (itemIdx > 0 && itemIdx < MaxItemTypes)
            {
                Interlocked.Add(ref _totalAvailableByType[itemIdx], count);
            }

            _isDirty = true;
        }

        // IsZoneTile/HasFreeSpace читаем ВНЕ Ground-lock: порядок G->S больше не держится
        // из параллельных потоков, конвоя вложенных lock нет.
        // Sweep регистрируем по ФАКТИЧЕСКОЙ клетке размещения (placedX/Y).
        isOutsideStockpile = !StockpileManager.Instance.IsZoneTile(placedX, placedY);

        if (isOutsideStockpile && StockpileManager.Instance.HasFreeSpace)
        {
            JobBroker.Instance.RegisterStockpileHaul(placedX, placedY);
        }
    }

    public bool HasItemsAt(int x, int y)
    {
        lock (_lock)
        {
            return _groundItems.TryGetValue((x, y), out var entry) && entry.Count > 0;
        }
    }

    /// <summary>Тип предмета в клетке без резерва (для корректного CarriedItemId).</summary>
    public ItemId PeekItemAt(int x, int y)
    {
        lock (_lock)
        {
            return _groundItems.TryGetValue((x, y), out var entry) && entry.Count > 0
                ? entry.Item : ItemId.None;
        }
    }

    /// <summary>
    /// Собирает до <paramref name="maxItems"/> ячеек с предметами, лежащими
    /// ВНЕ складских зон (для sweep-регистрации haul-работ). Вызывается из
    /// JobBroker; порядок блокировок тот же, что в SpawnItems: G -> S.
    /// </summary>
    public void CollectItemTilesOutsideStockpiles(List<(int X, int Y)> destination, int maxItems)
    {
        destination.Clear();
        // Копируем ключи-кандидаты под коротким lock, фильтр IsZoneTile — уже БЕЗ Ground-lock,
        // чтобы не держать критическую секцию во время второго lock (Stockpile) из параллельных потоков.
        List<(int X, int Y)> candidates;
        lock (_lock)
        {
            int cap = Math.Min(maxItems * 2, Math.Max(64, _groundItems.Count));
            candidates = new List<(int X, int Y)>(cap);
            foreach (var kvp in _groundItems)
            {
                if (kvp.Value.Count <= 0)
                    continue;
                candidates.Add(kvp.Key);
                if (candidates.Count >= cap)
                    break;
            }
        }
        foreach (var key in candidates)
        {
            if (StockpileManager.Instance.IsZoneTile(key.X, key.Y))
                continue;
            destination.Add(key);
            if (destination.Count >= maxItems)
                break;
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
        return TryReserveGroundItems(agentPos, maxWeightCapacity, allowFromStockpile, ItemId.None, out cell, out id, out reservedCount);
    }

    public bool TryReserveGroundItems(Vector2 agentPos, float maxWeightCapacity, bool allowFromStockpile, ItemId preferredId, out (int X, int Y) cell, out ItemId id, out int reservedCount)
    {
        return TryReserveGroundItems(agentPos, maxWeightCapacity, allowFromStockpile, preferredId, MaxChunkRadius, out cell, out id, out reservedCount);
    }

    /// <summary>
    /// P0-фикс thundering herd (фриз 16:45): fast-fail по тоталам + лимит радиуса.
    /// Без еды на карте — возврат без взятия lock. Радиус ограничивает спиральный скан
    /// (еда: 3 чанка вместо 32 — в ~100 раз меньше работы под lock на агента).
    /// </summary>
    public bool TryReserveGroundItems(Vector2 agentPos, float maxWeightCapacity, bool allowFromStockpile, ItemId preferredId, int maxChunkRadius, out (int X, int Y) cell, out ItemId id, out int reservedCount)
    {
        cell = (-1, -1);
        id = ItemId.None;
        reservedCount = 0;

        // Fast-fail: запрашиваемого типа нет вообще — не берём lock, не сканируем карту.
        // Критично при 10k голодных и 0 зерна: иначе все молотят полный скан каждый тик.
        if (preferredId != ItemId.None)
        {
            int pIdx = (int)preferredId;
            if (pIdx < 0 || pIdx >= MaxItemTypes || Volatile.Read(ref _totalAvailableByType[pIdx]) <= 0)
                return false;
        }
        else if (_groundItems.Count == 0)
        {
            return false;
        }

        int radius = Math.Clamp(maxChunkRadius, 1, MaxChunkRadius);
        lock (_lock)
        {
            if (_groundItems.Count == 0)
                return false;

            int startCx = Math.Clamp((int)(agentPos.X / ChunkSizePx), 0, ChunkDim - 1);
            int startCy = Math.Clamp((int)(agentPos.Y / ChunkSizePx), 0, ChunkDim - 1);

            int agentTileX = (int)(agentPos.X / 64f);
            int agentTileY = (int)(agentPos.Y / 64f);

            float bestDistSq = float.MaxValue;
            // Кандидаты собираем под lock БЕЗ вызовов Stockpile (второй lock) — иначе конвой G->S.
            (int X, int Y) bestCell = (-1, -1);
            ItemId bestId = ItemId.None;
            int bestReserve = 0;

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

                        var chunk = _itemChunks[cx, cy];
                        if (chunk.Count == 0) continue;

                        foreach (var pos in chunk)
                        {
                            if (!_groundItems.TryGetValue(pos, out var entry)) continue;

                            if (preferredId != ItemId.None && entry.Item != preferredId) continue;

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
                                bestCell = pos;
                                bestId = entry.Item;
                                bestReserve = Math.Min(available, maxCanTake);
                            }
                        }
                    }
                }

                if (bestCell.X != -1 && bestReserve > 0)
                    break;
            }

            if (bestCell.X == -1 || bestReserve <= 0)
                return false;

            // Фильтр склада проверяем ВНЕ lock (второй lock Stockpile): если клетка не подошла —
            // просто выходим без резерва, агент попробует снова в следующем тике.
            // Это убирает вложенный lock G->S из горячего Parallel-пути.
            cell = bestCell;
            id = bestId;
            reservedCount = bestReserve;
            // Запоминаем претендента, проверку склада сделаем после выхода из lock.
        }

        bool isStock = StockpileManager.Instance.IsZoneTile(cell.X, cell.Y);
        if (!allowFromStockpile && isStock)
        {
            cell = (-1, -1);
            id = ItemId.None;
            reservedCount = 0;
            return false;
        }

        // Финальный CAS резерва под коротким lock с перепроверкой доступности.
        lock (_lock)
        {
            if (!_groundItems.TryGetValue(cell, out var e2) || e2.Item != id)
                return false;
            int avail2 = e2.Count - e2.Reserved;
            if (avail2 <= 0) return false;
            int take = Math.Min(avail2, reservedCount);
            _groundItems[cell] = (e2.Item, e2.Count, e2.Reserved + take);
            reservedCount = take;
            int resIdx = (int)e2.Item;
            if (resIdx > 0 && resIdx < MaxItemTypes)
                Interlocked.Add(ref _totalAvailableByType[resIdx], -take);
            return true;
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
                int resIdx = (int)entry.Item;
                if (resIdx > 0 && resIdx < MaxItemTypes)
                {
                    Interlocked.Add(ref _totalAvailableByType[resIdx], -reservedCount);
                }
                return true;
            }

            return false;
        }
    }

    public int TakeItems(int x, int y, int count)
    {
        // Не держим Ground-lock во время вызова Stockpile.WithdrawItems (порядок G->S
        // мог собирать очередь из параллельных потоков). Сначала забираем под lock,
        // затем один раз обращаемся к складу уже без него.
        bool wasZoneTile;
        int toTake;
        lock (_lock)
        {
            if (!_groundItems.TryGetValue((x, y), out var entry))
                return 0;
            toTake = Math.Min(entry.Count, count);
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

            _isDirty = true;
            // IsZoneTile — быстрый lock в Stockpile; читаем флаг, но Withdraw делаем ниже.
            wasZoneTile = StockpileManager.Instance.IsZoneTile(x, y);
        }

        if (wasZoneTile)
        {
            StockpileManager.Instance.WithdrawItems(x, y, toTake);
        }
        return toTake;
    }

    public void ReleaseReservation(int x, int y, int count)
    {
        ItemId relItem = ItemId.None;
        lock (_lock)
        {
            if (_groundItems.TryGetValue((x, y), out var entry))
            {
                int newReserved = Math.Max(0, entry.Reserved - count);
                _groundItems[(x, y)] = (entry.Item, entry.Count, newReserved);
                relItem = entry.Item;
            }
        }
        int relIdx = (int)relItem;
        if (relIdx > 0 && relIdx < MaxItemTypes)
        {
            Interlocked.Add(ref _totalAvailableByType[relIdx], count);
        }
    }

    public void GenerateSnapshot()
    {
        // Снапшот читает словарь — короткая блокировка только на копирование.
        KeyValuePair<(int X, int Y), (ItemId Item, int Count, int Reserved)>[] itemsCopy;
        lock (_lock)
        {
            if (!_isDirty) return;
            _isDirty = false;

            // Копируем только ссылки на записи (без аллокации списка ключей в Update-пути —
            // здесь снимок идёт редко: при GenerateSnapshot из Phase2), затем отпускаем lock
            // и строим буферы вне критической секции, чтобы не держать потоки.
            itemsCopy = new KeyValuePair<(int X, int Y), (ItemId Item, int Count, int Reserved)>[_groundItems.Count];
            int ci = 0;
            foreach (var kvp in _groundItems)
                itemsCopy[ci++] = kvp;
        }

        // Свежие буферы на снапшот: ring переиспользовался и продюсер перезаписывал
        // массив, пока рендер его читал — tearing. 8x4096 Vector2 ~0.5МБ на 30Гц.
        var currentSnap = new Vector2[MaxItemTypes][];
        for (int t = 0; t < MaxItemTypes; t++)
            currentSnap[t] = new Vector2[MaxSnapshotItemsPerType];
        var currentCounts = new int[MaxItemTypes];

        for (int i = 0; i < itemsCopy.Length; i++)
        {
            var pos = itemsCopy[i].Key;
            var entry = itemsCopy[i].Value;
            if (entry.Count > 0)
            {
                int itemIdx = (int)entry.Item;
                if (itemIdx >= 0 && itemIdx < MaxItemTypes)
                {
                    int count = currentCounts[itemIdx];
                    if (count < MaxSnapshotItemsPerType)
                    {
                        currentSnap[itemIdx][count] = new Vector2(pos.X * 64f + 32f, pos.Y * 64f + 32f);
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