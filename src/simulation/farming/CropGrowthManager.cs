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
    private const int RingSize = 3;

    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), CropData> _crops = new(MaxCrops);

    private readonly Vector2[][][] _snapshotRing = new Vector2[RingSize][][];
    private readonly int[][] _countsRing = new int[RingSize][];
    private int _ringIndex = 0;
    private bool _isDirty = true;

    public ConcurrentQueue<(Vector2[][] PositionsByStage, int[] Counts)> SnapshotQueue { get; } = new();

    public CropGrowthManager()
    {
        for (int r = 0; r < RingSize; r++)
        {
            _snapshotRing[r] = new Vector2[4][];
            _countsRing[r] = new int[4];
            for (int s = 0; s < 4; s++)
            {
                _snapshotRing[r][s] = new Vector2[MaxCrops];
            }
        }
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
        var readyToHarvest = new List<(int X, int Y)>();

        lock (_lock)
        {
            if (_crops.Count == 0) return;

            var keys = new List<(int X, int Y)>(_crops.Keys);
            foreach (var pos in keys)
            {
                var crop = _crops[pos];
                if (crop.Stage >= 1 && crop.Stage < 4)
                {
                    crop.GrowthTimer += deltaTime;
                    if (crop.GrowthTimer >= StageDuration)
                    {
                        crop.GrowthTimer = 0f;
                        crop.Stage++;
                        _isDirty = true;

                        if (crop.Stage == 4 && !crop.IsHarvestQueued)
                        {
                            crop.IsHarvestQueued = true;
                            readyToHarvest.Add(pos);
                        }
                    }
                    _crops[pos] = crop;
                }
            }
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
        lock (_lock)
        {
            if (!_isDirty) return;
            _isDirty = false;

            var currentSnap = _snapshotRing[_ringIndex];
            var currentCounts = _countsRing[_ringIndex];
            Array.Clear(currentCounts, 0, currentCounts.Length);

            foreach (var ((x, y), crop) in _crops)
            {
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

            _ringIndex = (_ringIndex + 1) % RingSize;
            SnapshotQueue.Enqueue((currentSnap, currentCounts));

            while (SnapshotQueue.Count > 2)
                SnapshotQueue.TryDequeue(out _);
        }
    }
}