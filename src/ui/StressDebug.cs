using System;
using System.Collections.Generic;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.UI.Tools;

namespace Game.UI;

/// <summary>Временный стресс-хук 12к грядок: F8 — создать, F9 — метрики, F10 — 100x. Удалить после теста.</summary>
public static class StressDebug
{
    private const int Size = 110; // 110x110 = 12100 тайлов

    public static void SpawnFarmStress()
    {
        try
        {
            var map = MapRenderer.Instance?.MapData;
            if (map == null) { GD.PrintErr("[StressDebug] нет MapData"); return; }
            GD.Print("[StressDebug] старт сбора тайлов");
            int cx = map.Width / 2, cy = map.Height / 2, half = Size / 2;
            var plots = new List<(int X, int Y)>(Size * Size);
            for (int x = cx - half; x < cx + half; x++)
                for (int y = cy - half; y < cy + half; y++)
                {
                    if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) continue;
                    if (map.Ground[x, y] != TileType.Grass) continue;
                    if (map.TreeOnGrass[x, y]) continue;
                    plots.Add((x, y));
                }
            GD.Print($"[StressDebug] грядок к созданию: {plots.Count}");
            try { JobBroker.Instance.RegisterFarmPlotBatch(plots); }
            catch (Exception ex) { GD.PrintErr($"[StressDebug] RegisterBatch: {ex.Message}\n{ex.StackTrace}"); return; }
            GD.Print("[StressDebug] RegisterFarmPlotBatch OK");
            GD.Print($"[StressDebug] total={JobDispatcher.Instance.JobIndex.TotalCount} unclaimed={JobDispatcher.Instance.JobIndex.UnclaimedCount}");
        }
        catch (Exception ex) { GD.PrintErr($"[StressDebug] Spawn: {ex.Message}\n{ex.StackTrace}"); }
    }

    public static void LogMetrics()
    {
        try
        {
            var idx = JobDispatcher.Instance.JobIndex;
            GD.Print($"[StressDebug] METRICS beds={FarmJobManager.Instance.GetAllCompletedBeds().Count} unclaimed={idx.UnclaimedCount} total={idx.TotalCount}");
        }
        catch (Exception ex) { GD.PrintErr($"[StressDebug] Metrics: {ex.Message}"); }
    }
}
