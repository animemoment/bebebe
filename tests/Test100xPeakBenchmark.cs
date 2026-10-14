using System;
using System.Diagnostics;
using System.Numerics;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.Simulation.Scheduling;
using Game.Simulation.Simd;
using Vector2 = System.Numerics.Vector2;

namespace Game.Tests;

public partial class Test100xPeakBenchmark : RefCounted
{
    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[BENCHMARK 100X] Starting 100x Speed Peak CPU Benchmark (10,000 Agents)");
        GD.Print("==================================================");

        try
        {
            const int AgentCount = 10000;
            const float Dt100x = 0.50f; // 100x speed sub-step delta time
            var pool = new AgentDataPool(AgentCount);

            const int MapW = 256;
            const int MapH = 256;
            var ground = new TileType[MapW, MapH];
            var treeOnGrass = new bool[MapW, MapH];
            var solidWalls = new bool[MapW, MapH];
            var walkableTiles = new System.Collections.Generic.List<(int X, int Y)>(AgentCount);

            for (int x = 0; x < MapW; x++)
            {
                for (int y = 0; y < MapH; y++)
                {
                    ground[x, y] = TileType.Grass;
                    walkableTiles.Add((x, y));
                }
            }

            var spatialGrid = new AgentSpatialGrid(MapW, MapH);
            var movement = new AgentMovementService();
            var random = new Random(12345);
            var ctx = new SimulationContext(ground, null, treeOnGrass, solidWalls, walkableTiles, spatialGrid, movement, random, 64);

            for (int i = 0; i < AgentCount; i++)
            {
                float posX = 1000f + (i % 200) * 64f;
                float posY = 1000f + (i / 200) * 64f;
                pool.PositionX[i] = posX;
                pool.PositionY[i] = posY;
                pool.TargetPositionX[i] = posX + 500f;
                pool.TargetPositionY[i] = posY + 500f;
                pool.CurrentCellX[i] = (int)posX >> 6;
                pool.CurrentCellY[i] = (int)posY >> 6;
                // 95% idle, 5% working (500 активных рабочих агентов)
                pool.States[i] = (i % 20 == 0) ? AgentState.Working : AgentState.Idle;
                pool.Hunger[i] = i % 100;
                pool.Sleep[i] = (i * 3) % 100;
                pool.Fatigue[i] = (i * 7) % 100;
                pool.EnvironmentSatisfaction[i] = 80f;
                pool.Mood[i] = 100f;
                pool.CellStayTime[i] = 1.0f;
                pool.CurrentJobId[i] = (i % 20 == 0) ? i : -1;
                pool.CurrentJobType[i] = (i % 20 == 0) ? JobTypeId.TreeChopping : JobTypeId.None;
            }

            // Вспомогательный метод для активного движения агентов с непрерывным пинг-понгом целей
            void StepAgentMovement(int i)
            {
                if (movement.MoveTowards(i, new Vector2(pool.TargetPositionX[i], pool.TargetPositionY[i]), 16f, Dt100x, pool, ctx))
                {
                    // Агент достиг цели — переключаем цель обратно на исходную позицию (гарантия 100% реального движения в бенчмарке)
                    float originX = 1000f + (i % 200) * 64f;
                    float originY = 1000f + (i / 200) * 64f;
                    pool.TargetPositionX[i] = (pool.TargetPositionX[i] > originX + 250f) ? originX : originX + 500f;
                    pool.TargetPositionY[i] = (pool.TargetPositionY[i] > originY + 250f) ? originY : originY + 500f;
                }
            }

            // 1. Warm-up (JIT compilation & ThreadPool thread warmup)
            for (int w = 0; w < 10; w++)
            {
                SimdNeedsBatch.UpdateNeeds(pool, 0, AgentCount, Dt100x, false);
                SimdNeedsBatch.UpdateCells(pool, 0, AgentCount, Dt100x);
                DynamicWorkScheduler.Shared.ForEachSplittable(AgentCount, (s, e, q) =>
                {
                    for (int i = s; i < e; i++)
                    {
                        if (pool.States[i] == AgentState.Working)
                            StepAgentMovement(i);
                    }
                    return e - s;
                }, WorkKind.Heavy);
                DynamicWorkBalancer.ForEachRange(AgentCount, (s, e) =>
                {
                    for (int i = s; i < e; i++)
                    {
                        if (pool.States[i] == AgentState.Idle) continue;
                        pool.Hunger[i] = MathF.Min(100f, pool.Hunger[i] + 0.01f);
                    }
                });
            }

            // 2. Бенчмарк движения AgentMovementService при dt=0.50 (100x) с РЕАЛЬНЫМ перемещением
            const int MoveIterations = 50;
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < MoveIterations; it++)
            {
                for (int i = 0; i < 500; i++) // 500 движущихся агентов
                {
                    StepAgentMovement(i);
                }
            }
            sw.Stop();
            double moveTotalMs = sw.Elapsed.TotalMilliseconds;
            double movePerCallMs = moveTotalMs / MoveIterations;
            GD.Print($"[MOVE 100X dt=0.50] {MoveIterations} итераций (500 агентов): Total={moveTotalMs:F2} ms, PerCall={movePerCallMs:F4} ms");

            // 3. Замер пика Phase2 на 10k агентах при dt=0.50
            const int Phase2Iterations = 50;
            double maxPhase2Ms = 0;
            double totalPhase2Ms = 0;
            for (int it = 0; it < Phase2Iterations; it++)
            {
                sw.Restart();
                DynamicWorkScheduler.Shared.ForEachSplittable(AgentCount, (s, e, q) =>
                {
                    for (int i = s; i < e; i++)
                    {
                        if (pool.States[i] == AgentState.Working)
                        {
                            StepAgentMovement(i);
                        }
                    }
                    return e - s;
                }, WorkKind.Heavy);
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                totalPhase2Ms += ms;
                if (ms > maxPhase2Ms) maxPhase2Ms = ms;
            }
            double avgPhase2Ms = totalPhase2Ms / Phase2Iterations;
            GD.Print($"[PHASE2 100X 10k] {Phase2Iterations} итераций: Avg={avgPhase2Ms:F4} ms, Peak Max={maxPhase2Ms:F4} ms");

            // 4. Замер пика Phase3b Commit на 10k агентах при 500 работающих
            const int Phase3bIterations = 50;
            double maxPhase3bMs = 0;
            double totalPhase3bMs = 0;
            for (int it = 0; it < Phase3bIterations; it++)
            {
                sw.Restart();
                DynamicWorkBalancer.ForEachRange(AgentCount, (s, e) =>
                {
                    for (int i = s; i < e; i++)
                    {
                        var state = pool.States[i];
                        if (state == AgentState.Idle || state == AgentState.Evacuating)
                            continue;
                        pool.Hunger[i] = MathF.Min(100f, pool.Hunger[i] + 0.01f);
                    }
                }, "Phase3b_Bench");
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                totalPhase3bMs += ms;
                if (ms > maxPhase3bMs) maxPhase3bMs = ms;
            }
            double avgPhase3bMs = totalPhase3bMs / Phase3bIterations;
            GD.Print($"[PHASE3B 10k] {Phase3bIterations} итераций: Avg={avgPhase3bMs:F4} ms, Peak Max={maxPhase3bMs:F4} ms");

            // 5. Замер пика полного TotalStepCycle при пакетном всплеске шагов (Burst 10 шагов)
            const int CycleIterations = 30;
            double maxBurstCycleMs = 0;
            double totalBurstCycleMs = 0;
            for (int it = 0; it < CycleIterations; it++)
            {
                sw.Restart();
                int burstSteps = 10;
                for (int s = 0; s < burstSteps; s++)
                {
                    // Phase 2
                    DynamicWorkScheduler.Shared.ForEachSplittable(AgentCount, (start, end, q) =>
                    {
                        for (int i = start; i < end; i++)
                        {
                            if (pool.States[i] == AgentState.Working)
                                StepAgentMovement(i);
                        }
                        return end - start;
                    }, WorkKind.Heavy);

                    // Phase 3a
                    SimdNeedsBatch.UpdateNeeds(pool, 0, AgentCount, Dt100x, false);
                    SimdNeedsBatch.UpdateCells(pool, 0, AgentCount, Dt100x);

                    // Phase 3b
                    DynamicWorkBalancer.ForEachRange(AgentCount, (start, end) =>
                    {
                        for (int i = start; i < end; i++)
                        {
                            if (pool.States[i] == AgentState.Idle) continue;
                            pool.Hunger[i] = MathF.Min(100f, pool.Hunger[i] + 0.01f);
                        }
                    });
                }
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                totalBurstCycleMs += ms;
                if (ms > maxBurstCycleMs) maxBurstCycleMs = ms;
            }
            double avgBurstCycleMs = totalBurstCycleMs / CycleIterations;
            GD.Print($"[BURST CYCLE (10 steps) 100X] {CycleIterations} итераций: Avg={avgBurstCycleMs:F2} ms, PEAK MAX={maxBurstCycleMs:F2} ms");

            // 6. Замер одиночного шага (1 sub-step) на 100x
            double maxSingleStepMs = 0;
            double totalSingleStepMs = 0;
            for (int it = 0; it < CycleIterations; it++)
            {
                sw.Restart();
                DynamicWorkScheduler.Shared.ForEachSplittable(AgentCount, (start, end, q) =>
                {
                    for (int i = start; i < end; i++)
                    {
                        if (pool.States[i] == AgentState.Working)
                            StepAgentMovement(i);
                    }
                    return end - start;
                }, WorkKind.Heavy);

                SimdNeedsBatch.UpdateNeeds(pool, 0, AgentCount, Dt100x, false);
                SimdNeedsBatch.UpdateCells(pool, 0, AgentCount, Dt100x);

                DynamicWorkBalancer.ForEachRange(AgentCount, (start, end) =>
                {
                    for (int i = start; i < end; i++)
                    {
                        if (pool.States[i] == AgentState.Idle) continue;
                        pool.Hunger[i] = MathF.Min(100f, pool.Hunger[i] + 0.01f);
                    }
                });
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                totalSingleStepMs += ms;
                if (ms > maxSingleStepMs) maxSingleStepMs = ms;
            }
            double avgSingleStepMs = totalSingleStepMs / CycleIterations;
            GD.Print($"[SINGLE STEP (1 step) 100X] {CycleIterations} итераций: Avg={avgSingleStepMs:F3} ms, PEAK MAX={maxSingleStepMs:F3} ms");

            // 7. Замер амортизированного цикла (2 шага за проход)
            double maxAmortizedCycleMs = 0;
            double totalAmortizedCycleMs = 0;
            for (int it = 0; it < CycleIterations; it++)
            {
                sw.Restart();
                int amortizedSteps = 2;
                for (int s = 0; s < amortizedSteps; s++)
                {
                    DynamicWorkScheduler.Shared.ForEachSplittable(AgentCount, (start, end, q) =>
                    {
                        for (int i = start; i < end; i++)
                        {
                            if (pool.States[i] == AgentState.Working)
                                StepAgentMovement(i);
                        }
                        return end - start;
                    }, WorkKind.Heavy);

                    SimdNeedsBatch.UpdateNeeds(pool, 0, AgentCount, Dt100x, false);
                    SimdNeedsBatch.UpdateCells(pool, 0, AgentCount, Dt100x);

                    DynamicWorkBalancer.ForEachRange(AgentCount, (start, end) =>
                    {
                        for (int i = start; i < end; i++)
                        {
                            if (pool.States[i] == AgentState.Idle) continue;
                            pool.Hunger[i] = MathF.Min(100f, pool.Hunger[i] + 0.01f);
                        }
                    });
                }
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                totalAmortizedCycleMs += ms;
                if (ms > maxAmortizedCycleMs) maxAmortizedCycleMs = ms;
            }
            double avgAmortizedCycleMs = totalAmortizedCycleMs / CycleIterations;
            GD.Print($"[AMORTIZED CYCLE (2 steps) 100X] {CycleIterations} итераций: Avg={avgAmortizedCycleMs:F2} ms, PEAK MAX={maxAmortizedCycleMs:F2} ms");

            GD.Print("==================================================");
            GD.Print($"[SUMMARY 100X] 1 Step Avg={avgSingleStepMs:F2}ms, Peak={maxSingleStepMs:F2}ms | Amortized Avg={avgAmortizedCycleMs:F2}ms, Peak={maxAmortizedCycleMs:F2}ms");
            GD.Print("==================================================");

            // Автоматические ассерты соблюдения лимитов (с запасом на фоновую загрузку CPU)
            if (avgSingleStepMs > 3.0)
            {
                throw new Exception($"[REGRESSION DETECTED] Single step cycle average time {avgSingleStepMs:F2} ms exceeded ceiling 3.0 ms!");
            }

            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[BENCHMARK 100X FAILED] {ex}");
            return false;
        }
    }
}
