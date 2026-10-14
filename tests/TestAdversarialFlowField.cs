using System;
using System.Numerics;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.Simulation.Gpu;
using Game.Simulation.Simd;

namespace Game.Tests;

/// <summary>
/// Состязательный тест (@destroyer) для GPU Flow Fields, GPU Agent Transform и навигации Phase 2:
/// - Проверка некорректных и null аргументов (bad data)
/// - Цель внутри стены или за пределами карты
/// - Корректность нормализации градиентов без NaN/бесконечностей
/// - Обход препятствий в лабиринте
/// - Согласованность плоского кэша проходимости (IsTileBlocked)
/// - Пакетная трансформация агентов и битовая маска прибытия StepReachedMask
/// </summary>
public partial class TestAdversarialFlowField : RefCounted
{
    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[ADVERSARIAL FLOW FIELD & TRANSFORM] Starting Adversarial Audit (@destroyer)");
        GD.Print("==================================================");

        try
        {
            TestBadDataAndOutOfBounds();
            TestTargetInsideWallAndZeroGradient();
            TestObstacleDetourAndFiniteVectors();
            TestFlatCostGridConsistency();
            TestFlowFieldManagerCacheLifecycle();
            TestGpuAgentTransformAndArrivalBitmask();

            GD.Print("==================================================");
            GD.Print("[ADVERSARIAL OK] ALL 6 GPU NAVIGATION & TRANSFORM TESTS PASSED! (Code 0)");
            GD.Print("==================================================");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ADVERSARIAL FAILED] {ex}");
            return false;
        }
    }

    private void TestBadDataAndOutOfBounds()
    {
        GD.Print("[TEST 1] Testing bad data: null buffers, negative size, OOB targets...");
        var sim = GpuFlowFieldSimulator.Instance;

        // 1. null буферы
        bool r1 = sim.ComputeFlowField(null, 32, 32, 5, 5, new System.Numerics.Vector2[1024]);
        if (r1) throw new Exception("ComputeFlowField with null costMap must return false!");

        bool r2 = sim.ComputeFlowField(new byte[1024], 32, 32, 5, 5, null);
        if (r2) throw new Exception("ComputeFlowField with null outVectors must return false!");

        // 2. Отрицательные или нулевые размеры
        bool r3 = sim.ComputeFlowField(new byte[100], 0, 10, 0, 0, new System.Numerics.Vector2[100]);
        if (r3) throw new Exception("ComputeFlowField with width=0 must return false!");

        bool r4 = sim.ComputeFlowField(new byte[100], 10, -5, 0, 0, new System.Numerics.Vector2[100]);
        if (r4) throw new Exception("ComputeFlowField with height < 0 must return false!");

        // 3. Цель за пределами карты (OOB)
        bool r5 = sim.ComputeFlowField(new byte[100], 10, 10, 15, 5, new System.Numerics.Vector2[100]);
        if (r5) throw new Exception("ComputeFlowField with targetX >= width must return false!");

        bool r6 = sim.ComputeFlowField(new byte[100], 10, 10, 5, -1, new System.Numerics.Vector2[100]);
        if (r6) throw new Exception("ComputeFlowField with targetY < 0 must return false!");

        GD.Print(" -> Bad data & boundary guards PASSED.");
    }

    private void TestTargetInsideWallAndZeroGradient()
    {
        GD.Print("[TEST 2] Testing target inside wall: must yield zero vectors without throwing...");
        int w = 16;
        int h = 16;
        int total = w * h;
        byte[] cost = new byte[total];
        Array.Fill(cost, (byte)1);

        // Ставим стену в точку (8, 8)
        cost[8 * w + 8] = 0;

        var flow = new System.Numerics.Vector2[total];
        bool ok = GpuFlowFieldSimulator.Instance.ComputeFlowField(cost, w, h, 8, 8, flow);
        if (!ok) throw new Exception("Target inside wall must return true with zero vectors!");

        // Проверяем, что все векторы равны нулю (нет ложных векторов в стену)
        for (int i = 0; i < total; i++)
        {
            if (flow[i] != System.Numerics.Vector2.Zero)
                throw new Exception($"Vector at {i} must be zero when target is blocked, got {flow[i]}");
        }

        GD.Print(" -> Target inside wall handled cleanly with zero vectors PASSED.");
    }

    private void TestObstacleDetourAndFiniteVectors()
    {
        GD.Print("[TEST 3] Testing obstacle detour: U-shaped wall and NaN/Inf validation...");
        int w = 15;
        int h = 15;
        int total = w * h;
        byte[] cost = new byte[total];
        Array.Fill(cost, (byte)1);

        // П-образное препятствие:
        // Стена слева, сверху, справа от точки (7, 7)
        for (int y = 5; y <= 9; y++)
        {
            cost[y * w + 5] = 0; // левая стена
            cost[y * w + 9] = 0; // правая стена
        }
        for (int x = 5; x <= 9; x++)
        {
            cost[5 * w + x] = 0; // верхняя крышка
        }

        int targetX = 7;
        int targetY = 12; // цель внизу под П-образным карманом

        var flow = new System.Numerics.Vector2[total];
        bool ok = GpuFlowFieldSimulator.Instance.ComputeFlowField(cost, w, h, targetX, targetY, flow);
        if (!ok) throw new Exception("ComputeFlowField failed on obstacle detour test!");

        // Проверка: ни один вектор не должен быть NaN или Inf
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var v = flow[y * w + x];
                if (float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsInfinity(v.X) || float.IsInfinity(v.Y))
                    throw new Exception($"Invalid vector at ({x}, {y}): {v}");

                // Внутри кармана (7, 7) вектор должен указывать вниз (+Y), обходя крышку
                if (x == 7 && y == 7)
                {
                    if (v.Y <= 0f)
                        throw new Exception($"Agent inside pocket must steer down (+Y) to exit, got {v}");
                }
            }
        }

        // Вектор в самой цели должен быть точно (0, 0)
        var targetV = flow[targetY * w + targetX];
        if (targetV != System.Numerics.Vector2.Zero)
            throw new Exception($"Target cell vector must be Vector2.Zero, got {targetV}");

        GD.Print(" -> Obstacle detour & NaN-free vectors PASSED.");
    }

    private void TestFlatCostGridConsistency()
    {
        GD.Print("[TEST 4] Testing flat Cost grid consistency with 2D array lookups...");
        int w = 32;
        int h = 32;
        var ground = new TileType[w, h];
        var solidWalls = new bool[w, h];
        var trees = new bool[w, h];

        // Заполняем случайными препятствиями
        var rng = new Random(12345);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                ground[x, y] = (rng.Next(10) == 0) ? TileType.Mountain : TileType.Grass;
                solidWalls[x, y] = (rng.Next(8) == 0);
            }
        }

        var ctx = new SimulationContext(
            ground,
            null,
            trees,
            solidWalls,
            new System.Collections.Generic.List<(int, int)>(),
            new AgentSpatialGrid(w, h),
            new AgentMovementService(),
            new Random(1)
        );

        // Инициализируем HierarchicalPathfinder
        HierarchicalPathfinder.Instance.Initialize(ctx);

        // Сравниваем результат IsTileBlocked для 1000 произвольных координат
        for (int i = 0; i < 1000; i++)
        {
            float wx = (float)rng.NextDouble() * (w * 64);
            float wy = (float)rng.NextDouble() * (h * 64);

            bool blocked = ctx.Movement.IsTileBlocked(wx, wy, ctx);

            int tx = (int)wx >> 6;
            int ty = (int)wy >> 6;

            bool expected = solidWalls[tx, ty] || ground[tx, ty] == TileType.Mountain;
            // Если в центре тайла, результат обязан совпадать с expected
            int subX = (int)wx & 63;
            int subY = (int)wy & 63;
            if (subX >= 10 && subX <= 54 && subY >= 10 && subY <= 54)
            {
                if (blocked != expected)
                    throw new Exception($"Discrepancy at tile ({tx}, {ty}): blocked={blocked}, expected={expected}");
            }
        }

        GD.Print(" -> Flat Cost grid consistency PASSED.");
    }

    private void TestFlowFieldManagerCacheLifecycle()
    {
        GD.Print("[TEST 5] Testing FlowFieldManager cache lifecycle & ClearCache...");
        var ground = new TileType[16, 16];
        var solidWalls = new bool[16, 16];
        var trees = new bool[16, 16];
        var ctx = new SimulationContext(
            ground,
            null,
            trees,
            solidWalls,
            new System.Collections.Generic.List<(int, int)>(),
            new AgentSpatialGrid(16, 16),
            new AgentMovementService(),
            new Random(1)
        );

        HierarchicalPathfinder.Instance.Initialize(ctx);
        var mgr = FlowFieldManager.Instance;
        mgr.ClearCache();

        var f1 = mgr.GetOrCreateTargetFlowField(5, 5, ctx);
        var f2 = mgr.GetOrCreateTargetFlowField(5, 5, ctx);

        if (!ReferenceEquals(f1, f2))
            throw new Exception("FlowFieldManager must return identical cached instance for same target!");

        // Очистка кэша
        mgr.ClearCache();
        var f3 = mgr.GetOrCreateTargetFlowField(5, 5, ctx);
        if (ReferenceEquals(f1, f3))
            throw new Exception("After ClearCache, a fresh flow field array must be created!");

        GD.Print(" -> FlowFieldManager cache lifecycle PASSED.");
    }

    private void TestGpuAgentTransformAndArrivalBitmask()
    {
        GD.Print("[TEST 6] Testing GpuAgentTransformSimulator & SimdMovementBatch (Arrival Bitmask & Bad Inputs)...");
        var sim = GpuAgentTransformSimulator.Instance;

        // 1. Плохие аргументы (null, отрицательные числа, NaN, Infinity)
        if (sim.StepAgents(null, 100, 10f))
            throw new Exception("StepAgents with null pool must return false!");

        var pool = new AgentDataPool(533); // Нечетное число (не кратно 4, 8, 32, 256)
        if (sim.StepAgents(pool, 0, 10f))
            throw new Exception("StepAgents with agentCount=0 must return false!");
        if (sim.StepAgents(pool, -10, 10f))
            throw new Exception("StepAgents with negative agentCount must return false!");
        if (sim.StepAgents(pool, 533, -5f))
            throw new Exception("StepAgents with negative stepLen must return false!");
        if (sim.StepAgents(pool, 533, float.NaN))
            throw new Exception("StepAgents with NaN stepLen must return false!");
        if (sim.StepAgents(pool, 533, 10f, -1f))
            throw new Exception("StepAgents with negative reachSq must return false!");

        // 2. Проверка шага, битовой маски прибытия и переключения WaypointIndex
        for (int i = 0; i < 533; i++)
        {
            pool.PositionX[i] = 100f;
            pool.PositionY[i] = 100f;
            pool.WaypointCount[i] = 2;
            pool.WaypointIndex[i] = 0;

            int wpBase = i * AgentPathConfig.MaxWaypoints;
            // Четные агенты уже рядом с первой точкой (в пределах reachDist=16), нечетные — далеко (в 1000 px)
            if (i % 2 == 0)
            {
                pool.WaypointX[wpBase + 0] = 1; // 1 * 64 + 32 = 96 (дистанция 5.6 px <= 16 px)
                pool.WaypointY[wpBase + 0] = 1;
            }
            else
            {
                pool.WaypointX[wpBase + 0] = 10; // 10 * 64 + 32 = 672 (далеко)
                pool.WaypointY[wpBase + 0] = 10;
            }
            pool.WaypointX[wpBase + 1] = 20; // Вторая точка: 20 * 64 + 32 = 1312
            pool.WaypointY[wpBase + 1] = 20;
        }

        SimdMovementBatch.SyncAllStepTargets(pool, 0, 533, 64f);

        bool ok = sim.StepAgents(pool, 533, 24f, 256f, 64f);
        if (!ok)
            throw new Exception("StepAgents failed on valid 533 agent pool!");

        for (int i = 0; i < 533; i++)
        {
            if (float.IsNaN(pool.PositionX[i]) || float.IsNaN(pool.PositionY[i]))
                throw new Exception($"NaN position at agent {i}");

            if (i % 2 == 0)
            {
                // Четный агент обязан был достигнуть точки 0, переключить WaypointIndex на 1 и обновить StepTarget на 1312
                if (pool.WaypointIndex[i] != 1)
                    throw new Exception($"Even agent {i} should have advanced WaypointIndex to 1, got {pool.WaypointIndex[i]}");
                if (MathF.Abs(pool.StepTargetX[i] - 1312f) > 0.01f)
                    throw new Exception($"Even agent {i} should have updated StepTargetX to 1312, got {pool.StepTargetX[i]}");
            }
            else
            {
                // Нечетный агент обязан был сдвинуться вперед к (672, 672) и остаться на WaypointIndex = 0
                if (pool.WaypointIndex[i] != 0)
                    throw new Exception($"Odd agent {i} should still be on WaypointIndex 0, got {pool.WaypointIndex[i]}");
                if (pool.PositionX[i] <= 100f || pool.PositionY[i] <= 100f)
                    throw new Exception($"Odd agent {i} did not move forward! Pos=({pool.PositionX[i]}, {pool.PositionY[i]})");
            }
        }

        GD.Print(" -> GpuAgentTransformSimulator & Arrival Bitmask PASSED.");
    }
}

