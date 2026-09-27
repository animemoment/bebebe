using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Game.Core;

namespace Game.Simulation;

public sealed class CropGrowthManager
{
    public static CropGrowthManager Instance { get; } = new();

    private const float StageDuration = 60.0f; // 1 минута на каждую фазу
    private const int MaxCrops = 4096;

    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), CropData> _crops = new(MaxCrops);

    private bool _isDirty = true;

    // Finding 6: ring из 3 снапшот-буферов (см. GroundItemManager).
    // CropRenderer drain'ит очередь до последнего (аналогично GroundItemRenderer),
    // cap = 2, ротация = 3: перезаписи читаемого буфера нет.
    private readonly Vector2[][][] _snapRing = new Vector2[3][][];
    private readonly int[][] _snapCountsRing = new int[3][];
    private int _snapCursor;

    public ConcurrentQueue<(Vector2[][] PositionsByStage, int[] Counts)> SnapshotQueue { get; } = new();

    public CropGrowthManager()
    {
    }

    public void PlantCrop(int x, int y, int zoneId)
    {
        lock (_lock)
        {
            _crops[(x, y)] = new CropData
            {
                X = x,
                Y = y,
                ZoneId = zoneId,
                Stage = 1,
                GrowthTimer = 0f,
                IsHarvestQueued = false,
                IsPlantingQueued = false
            };
            _isDirty = true;
        }
    }

    public bool HasCrop(int x, int y)
    {
        lock (_lock)
        {
            return _crops.ContainsKey((x, y));
        }
    }

    public bool TryGetCrop(int x, int y, out CropData crop)
    {
        lock (_lock)
        {
            return _crops.TryGetValue((x, y), out crop);
        }
    }

    public void UpdateGrowth(float deltaTime, SimulationContext ctx)
    {
        // D55: сколько культур/переходов/готово за проход.
        // Без аллокаций в горячем пути: итерируем словарь напрямую (struct enumerator),
        // готовые к сбору складываем в ThreadLocal-буфер вместо new List каждый тик.
        System.Collections.Generic.List<(int X, int Y)> readyToHarvest = null;
        int grown = 0;

        // Двухфазно: снимок под коротким lock, итерация — без hold.
        // §20.3: земля на скорость не влияет, humidity/fertility здесь не читаем.
        // Снимок ключей под коротким lock — итерация идёт без hold.
        KeyValuePair<(int X, int Y), CropData>[] snap;
        lock (_lock)
        {
            if (_crops.Count == 0) return;
            snap = new KeyValuePair<(int X, int Y), CropData>[_crops.Count];
            int si = 0;
            foreach (var kvp in _crops) snap[si++] = kvp;
        }
        // Вне лока: считаем приросты.
        // §20.3: скорость роста везде одинаковая — земля влияет только на УРОЖАЙ
        // (считается в HarvestCrop через FarmYield). humidity/fertility здесь не читаем.
        var grownList = new System.Collections.Generic.List<((int X, int Y) Pos, CropData Crop)>(64);
        for (int i = 0; i < snap.Length; i++)
        {
            var pos = snap[i].Key;
            var crop = snap[i].Value;
            if (crop.Stage < 1 || crop.Stage >= 4)
                continue;
            // #15: while вместо if — при большом dt (100x: за тик приходит
            // сразу секундами) стадия росла максимум на 1 за проход, рост
            // искусственно тормозился в разы. Переносим весь накопленный
            // таймер через несколько стадий; готовым (Stage 4) — в harvest.
            crop.GrowthTimer += deltaTime;
            while (crop.GrowthTimer >= StageDuration && crop.Stage >= 1 && crop.Stage < 4)
            {
                crop.GrowthTimer -= StageDuration;
                crop.Stage++;
                grown++;
                if (crop.Stage == 4 && !crop.IsHarvestQueued)
                {
                    crop.IsHarvestQueued = true;
                    readyToHarvest ??= new System.Collections.Generic.List<(int X, int Y)>(16);
                    readyToHarvest.Add(pos);
                }
            }
            grownList.Add((pos, crop));
        }
        // Короткий writeback под lock: только изменившиеся.
        lock (_lock)
        {
            for (int i = 0; i < grownList.Count; i++)
            {
                var (pos, crop) = grownList[i];
                if (!_crops.TryGetValue(pos, out var cur))
                    continue; // конкурентный Harvest удалил — не воскрешаем
                // #15: while-проход выше мог продвинуть на несколько стадий:
                // пишем только вперёд (стадия и таймер новее), иначе затёрли бы
                // чужой конкурентный апдейт старым снимком.
                bool stageNewer = crop.Stage > cur.Stage;
                bool sameStageTimerNewer = crop.Stage == cur.Stage && crop.GrowthTimer > cur.GrowthTimer;
                if (stageNewer || sameStageTimerNewer)
                {
                    _crops[pos] = crop;
                    _isDirty = true;
                }
            }
        }

        // P0-1: без счётчиков — проход и так под lock, лишний lock(_cLock).
        if (readyToHarvest == null)
        {
            return;
        }
        foreach (var (x, y) in readyToHarvest)
        {
            JobBroker.Instance.RegisterHarvest(x, y);
        }
    }

    public void HarvestCrop(int x, int y, SimulationContext ctx)
    {
        int zoneId = -1;

        lock (_lock)
        {
            if (_crops.TryGetValue((x, y), out var crop))
            {
                zoneId = crop.ZoneId;
                _crops.Remove((x, y));
                _isDirty = true;
            }
        }

        // Выпадение 5-40 единиц зерна по качеству земли (§20.3):
        // влажно + плодородно = много, сухо + бедно = мало.
        // ParallelRng: ctx.Random не thread-safe
        // (HarvestCrop вызывается из параллельного Commit).
        int hum = ctx?.Humidity != null ? ctx.Humidity.Get(x, y) : 100;
        int fert = ctx?.Fertility != null ? ctx.Fertility.Get(x, y) : 100;
        float humMul = HumidityMap.GrowthMultiplier(hum);
        float fertMul = FertilityMap.GrowthMultiplier(fert);
        int dropAmount = FarmYield.RollDrop(humMul, fertMul, ParallelRng.NextDouble());
        GroundItemManager.Instance.SpawnItems(x, y, ItemId.Grain, dropAmount);

        // §20.4: сбор истощает клетку — плодородие × 0.8.
        ctx?.Fertility?.DrainAfterHarvest(x, y);

        // Если автопосадка включена — ставим задачу на новую посадку
        if (zoneId != -1 && FarmZoneManager.Instance.TryGetZoneById(zoneId, out var zone) && zone.AutoPlantEnabled)
        {
            JobBroker.Instance.RegisterPlanting(x, y, zoneId);
        }
    }

    public void RemoveCrop(int x, int y)
    {
        lock (_lock)
        {
            if (_crops.Remove((x, y)))
            {
                _isDirty = true;
            }
        }
    }

    public void GenerateSnapshot()
    {
        // Копируем записи под коротким lock, буфер строим вне критической секции.
        KeyValuePair<(int X, int Y), CropData>[] copy;
        lock (_lock)
        {
            if (!_isDirty) return;
            _isDirty = false;
            copy = new KeyValuePair<(int X, int Y), CropData>[_crops.Count];
            int ci = 0;
            foreach (var kvp in _crops) copy[ci++] = kvp;
        }

        // Finding 6: ring-буфер вместо fresh-аллокации (см. GroundItemManager).
        int slot = _snapCursor % 3;
        _snapCursor++;
        var currentSnap = _snapRing[slot];
        var currentCounts = _snapCountsRing[slot];
        if (currentSnap == null)
        {
            currentSnap = new Vector2[4][];
            for (int s = 0; s < 4; s++)
                currentSnap[s] = new Vector2[MaxCrops];
            currentCounts = new int[4];
            _snapRing[slot] = currentSnap;
            _snapCountsRing[slot] = currentCounts;
        }
        else
        {
            Array.Clear(currentCounts, 0, currentCounts.Length);
        }

        for (int i = 0; i < copy.Length; i++)
        {
            var x = copy[i].Key.X; var y = copy[i].Key.Y; var crop = copy[i].Value;
            if (crop.Stage >= 1 && crop.Stage <= 4)
            {
                int stageIdx = crop.Stage - 1;
                int count = currentCounts[stageIdx];
                if (count < MaxCrops)
                {
                    currentSnap[stageIdx][count] = new Vector2(x * 64f + 32f, y * 64f + 32f);
                    currentCounts[stageIdx]++;
                }
            }
        }

        SnapshotQueue.Enqueue((currentSnap, currentCounts));

        while (SnapshotQueue.Count > 2)
            SnapshotQueue.TryDequeue(out _);
    }
}