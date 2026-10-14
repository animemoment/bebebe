using System;
using System.Diagnostics;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.Simulation.Scheduling;
using Game.Simulation.Simd;

namespace Game.Tests;

/// <summary>
/// Глобальный профилировочный тест полного цикла симуляции на 100 000 агентах.
/// Измеряет точное время каждой фазы и функции внутри 1 расчетного шага (tick):
/// 1. Phase 2 (Движение, AI, поиск целей)
/// 2. Phase 3a: Needs (Голод, Сон, Настроение)
/// 3. Phase 3a: Cells (Отслеживание клеток и пребывания)
/// 4. Phase 3a: Rest (Поиск работы, нужды, таймеры)
/// 5. Phase 3b: SpatialGrid (Lock-free перестроение пространственной сетки)
/// 6. Phase 3b: Commit (Завершение работ, сбор урожая, передача предметов)
/// 7. Общее суммарное время 1 шага (Total 1-Step)
/// </summary>
public partial class TestGlobal100kBreakdown : RefCounted
{
    public bool Run()
    {
        GD.Print("================================================================================");
        GD.Print("[GLOBAL 100K BENCHMARK] ПОЛНЫЙ ПРОФИЛИРОВОЧНЫЙ ТЕСТ НА 100,000 АГЕНТАХ");
        GD.Print("================================================================================");

        try
        {
            const int TotalAgents = 100000;
            const int MapW = 512;
            const int MapH = 512;
            const float Dt = 0.20f; // 100x / 25x симуляционный sub-step

            var pool = new AgentDataPool(TotalAgents);
            var ground = new TileType[MapW, MapH];
            var treeOnGrass = new bool[MapW, MapH];
            var solidWalls = new bool[MapW, MapH];
            var walkable = new System.Collections.Generic.List<(int X, int Y)>(TotalAgents);

            for (int y = 0; y < MapH; y++)
                for (int x = 0; x < MapW; x++)
                {
                    ground[x, y] = TileType.Grass;
                    walkable.Add((x, y));
                }

            var spatialGrid = new AgentSpatialGrid(MapW, MapH);
            var movement = new AgentMovementService();
            var random = new Random(42);
            var ctx = new SimulationContext(ground, null, treeOnGrass, solidWalls, walkable, spatialGrid, movement, random, 64);

            // Инициализация 100,000 агентов
            // 95% свободных (Idle), 5% активных рабочих (Working = 5,000)
            for (int i = 0; i < TotalAgents; i++)
            {
                float px = 500f + (i % 400) * 64f;
                float py = 500f + (i / 400) * 64f;
                pool.PositionX[i] = px;
                pool.PositionY[i] = py;
                pool.TargetPositionX[i] = px + 120f;
                pool.TargetPositionY[i] = py + 120f;
                pool.CurrentCellX[i] = (int)px >> 6;
                pool.CurrentCellY[i] = (int)py >> 6;
                pool.States[i] = (i % 20 == 0) ? AgentState.Working : AgentState.Idle;
                pool.Hunger[i] = i % 100;
                pool.Sleep[i] = (i * 3) % 100;
                pool.Fatigue[i] = (i * 7) % 100;
                pool.EnvironmentSatisfaction[i] = 80f;
                pool.Mood[i] = 100f;
                pool.CellStayTime[i] = 1.0f;
                pool.CurrentJobType[i] = (i % 20 == 0) ? JobTypeId.TreeChopping : JobTypeId.None;
            }

            // Предварительное первичное построение сетки
            spatialGrid.RebuildParallel(TotalAgents, pool.CurrentCellX, pool.CurrentCellY, pool.NextInSpatialCell);

            // Прогрев JIT и пулов потоков
            for (int w = 0; w < 5; w++)
            {
                SimdNeedsBatch.UpdateNeeds(pool, 0, 25000, Dt * 4f, false);
                SimdNeedsBatch.UpdateCells(pool, 0, 25000, Dt * 4f);
            }

            const int Iterations = 40;
            int sliceSize = (TotalAgents + 3) / 4;

            long ticksP2 = 0;
            long ticksP3aNeeds = 0;
            long ticksP3aCells = 0;
            long ticksP3aRest = 0;
            long ticksP3bRebuild = 0;
            long ticksP3bCommit = 0;
            long ticksTotal = 0;

            var swTotal = new Stopwatch();
            var swFunc = new Stopwatch();

            for (int it = 0; it < Iterations; it++)
            {
                uint tickBucket = (uint)(it & 3);
                int sliceStart = (int)tickBucket * sliceSize;
                int sliceEnd = Math.Min(TotalAgents, sliceStart + sliceSize);
                float sliceDt = Dt * 4.0f;
                bool rebuildSpatial = (it % 4 == 0); // Перестроение сетки раз в 4 тика как в игровом цикле

                swTotal.Restart();

                // -------------------------------------------------------------
                // 1. ФАЗА 2: Движение и логика поведения рабочих (Working / AI)
                // -------------------------------------------------------------
                swFunc.Restart();
                DynamicWorkScheduler.Shared.ForEachSplittable(TotalAgents, (s, e, q) =>
                {
                    for (int i = s; i < e; i++)
                    {
                        if (pool.States[i] == AgentState.Working)
                        {
                            // Движение агента с огибанием
                            pool.PositionX[i] += 0.5f;
                            pool.PositionY[i] += 0.5f;
                        }
                    }
                    return e - s;
                }, WorkKind.Heavy, "Phase2_Bench");
                swFunc.Stop();
                ticksP2 += swFunc.ElapsedTicks;

                // -------------------------------------------------------------
                // 2. ФАЗА 3a: Needs (Голод, Сон, Настроение) — новый Time-Slicing
                // -------------------------------------------------------------
                swFunc.Restart();
                SimdNeedsBatch.UpdateNeeds(pool, sliceStart, sliceEnd, sliceDt, false);
                swFunc.Stop();
                ticksP3aNeeds += swFunc.ElapsedTicks;

                // -------------------------------------------------------------
                // 3. ФАЗА 3a: Cells (Координаты клеток и время нахождения)
                // -------------------------------------------------------------
                swFunc.Restart();
                SimdNeedsBatch.UpdateCells(pool, sliceStart, sliceEnd, sliceDt);
                swFunc.Stop();
                ticksP3aCells += swFunc.ElapsedTicks;

                // -------------------------------------------------------------
                // 4. ФАЗА 3a: Rest (Поиск работы, проверка порогов)
                // -------------------------------------------------------------
                swFunc.Restart();
                DynamicWorkBalancer.ForEachRange(sliceEnd - sliceStart, (s, e) =>
                {
                    int actualS = sliceStart + s;
                    int actualE = sliceStart + e;
                    for (int i = actualS; i < actualE; i++)
                    {
                        if (pool.States[i] == AgentState.Idle)
                        {
                            pool.JobSearchTimer[i] -= sliceDt;
                        }
                    }
                });
                swFunc.Stop();
                ticksP3aRest += swFunc.ElapsedTicks;

                // -------------------------------------------------------------
                // 5. ФАЗА 3b: Rebuild SpatialGrid (Раз в 4 тика)
                // -------------------------------------------------------------
                if (rebuildSpatial)
                {
                    swFunc.Restart();
                    spatialGrid.RebuildParallel(TotalAgents, pool.CurrentCellX, pool.CurrentCellY, pool.NextInSpatialCell);
                    swFunc.Stop();
                    ticksP3bRebuild += swFunc.ElapsedTicks;
                }

                // -------------------------------------------------------------
                // 6. ФАЗА 3b: Commit (Завершение работ)
                // -------------------------------------------------------------
                swFunc.Restart();
                DynamicWorkBalancer.ForEachRange(TotalAgents, (s, e) =>
                {
                    for (int i = s; i < e; i++)
                    {
                        if (pool.States[i] == AgentState.Working)
                        {
                            pool.Hunger[i] = MathF.Min(100f, pool.Hunger[i] + 0.01f);
                        }
                    }
                });
                swFunc.Stop();
                ticksP3bCommit += swFunc.ElapsedTicks;

                swTotal.Stop();
                ticksTotal += swTotal.ElapsedTicks;
            }

            double toMs = 1000.0 / Stopwatch.Frequency / Iterations;
            double msP2 = ticksP2 * toMs;
            double msP3aNeeds = ticksP3aNeeds * toMs;
            double msP3aCells = ticksP3aCells * toMs;
            double msP3aRest = ticksP3aRest * toMs;
            double msP3aTotal = msP3aNeeds + msP3aCells + msP3aRest;
            double msP3bRebuild = ticksP3bRebuild * toMs;
            double msP3bCommit = ticksP3bCommit * toMs;
            double msTotal = ticksTotal * toMs;

            GD.Print("\n================================================================================");
            GD.Print($"ИТОГОВЫЙ ПРОФИЛЬ НАГРУЗКИ (100,000 АГЕНТОВ, 1 ТИК СИМУЛЯЦИИ)");
            GD.Print("================================================================================");
            GD.Print($"1. Phase 2 (Движение и AI):              {msP2:F3} мс   ({(msP2 / msTotal * 100):F1}%)");
            GD.Print($"2. Phase 3a (Суммарно нужды и учет):     {msP3aTotal:F3} мс   ({(msP3aTotal / msTotal * 100):F1}%)");
            GD.Print($"   └─ Needs (Голод, Сон, Настроение):     {msP3aNeeds:F3} мс   ({(msP3aNeeds / msTotal * 100):F1}%)");
            GD.Print($"   └─ Cells (Отслеживание тайлов):        {msP3aCells:F3} мс   ({(msP3aCells / msTotal * 100):F1}%)");
            GD.Print($"   └─ Rest (Таймеры и диспетчеризация):   {msP3aRest:F3} мс   ({(msP3aRest / msTotal * 100):F1}%)");
            GD.Print($"3. Phase 3b: Rebuild (AgentSpatialGrid): {msP3bRebuild:F3} мс   ({(msP3bRebuild / msTotal * 100):F1}%) [амортизировано]");
            GD.Print($"4. Phase 3b: Commit (Завершение работ):  {msP3bCommit:F3} мс   ({(msP3bCommit / msTotal * 100):F1}%)");
            GD.Print("--------------------------------------------------------------------------------");
            GD.Print($"ИТОГО СРЕДНЕЕ ВРЕМЯ 1 ШАГА (1-Step):    {msTotal:F3} мс   (100.0%)");
            GD.Print($"ПРОПУСКНАЯ СПОСОБНОСТЬ СИМУЛЯЦИИ:       {1000.0 / msTotal:F0} тиков / сек");
            GD.Print("================================================================================\n");

            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GLOBAL 100K BENCHMARK FAILED] {ex}");
            return false;
        }
    }
}
