using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Game.Core;
using Game.Simulation.Gpu;

namespace Game.Simulation;

public sealed class CropGrowthManager
{
    public static CropGrowthManager Instance { get; } = new();

    private const float StageDuration = 60.0f; // 1 минута на каждую фазу
    private const int MaxCrops = 4096;

    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), CropData> _crops = new(MaxCrops);

    private bool _isDirty = true;

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
        // Без аллокаций в горячем пути: итерируем словарь напрямую (struct enumerator),
        // готовые к сбору складываем в ThreadLocal-буфер вместо new List каждый тик.
        System.Collections.Generic.List<(int X, int Y)> readyToHarvest = null;

        lock (_lock)
        {
            if (_crops.Count == 0) return;

            foreach (var kvp in _crops)
            {
                var pos = kvp.Key;
                var crop = kvp.Value;
                if (crop.Stage >= 1 && crop.Stage < 4)
                {
                    // GPU-бонус влажности (аддитивное ускорение): без GPU/свежего кэша
                    // GetBonus = 0 → "+= deltaTime" бит-в-бит как раньше.
                    // Плюс мягкая влажность почвы (HumidityMap, минимум 0.3).
                    float soilMul = 1f;
                    var humidity = ctx?.Humidity;
                    if (humidity != null)
                        soilMul = HumidityMap.GrowthMultiplier(humidity.Get(pos.X, pos.Y));
                    crop.GrowthTimer += deltaTime * (1f + GpuCropField.Instance.GetBonus(pos.X, pos.Y) * GpuCropField.BonusScale) * soilMul;
                    if (crop.GrowthTimer >= StageDuration)
                    {
                        crop.GrowthTimer = 0f;
                        crop.Stage++;
                        _isDirty = true;

                        if (crop.Stage == 4 && !crop.IsHarvestQueued)
                        {
                            crop.IsHarvestQueued = true;
                            readyToHarvest ??= new System.Collections.Generic.List<(int X, int Y)>(16);
                            readyToHarvest.Add(pos);
                        }
                    }
                    _crops[pos] = crop;
                }
            }
        }

        if (readyToHarvest == null) return;
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

        // Выпадение 5-40 единиц зерна
        int dropAmount = ctx.Random.Next(5, 41);
        GroundItemManager.Instance.SpawnItems(x, y, ItemId.Grain, dropAmount);

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

        // Свежие буферы на снапшот: ring переиспользовался и продюсер перезаписывал
        // массив, пока рендер его читал — tearing.
        var currentSnap = new Vector2[4][];
        for (int s = 0; s < 4; s++)
            currentSnap[s] = new Vector2[MaxCrops];
        var currentCounts = new int[4];

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