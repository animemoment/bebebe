using System;
using System.Diagnostics;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.Simulation.Scheduling;
using Game.Simulation.Simd;

namespace Game.Tests;

public partial class Test10kBenchmark : RefCounted
{
    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[BENCHMARK 10K] Starting 10,000 Agents CPU Cache & Sim Benchmark");
        GD.Print("==================================================");

        try
        {

        const int AgentCount = 10000;
        var pool = new AgentDataPool(AgentCount);

        // Инициализируем данные 10 000 агентов
        for (int i = 0; i < AgentCount; i++)
        {
            pool.PositionX[i] = 1000f + (i % 500) * 64f;
            pool.PositionY[i] = 1000f + (i / 500) * 64f;
            pool.CurrentCellX[i] = (int)pool.PositionX[i] >> 6;
            pool.CurrentCellY[i] = (int)pool.PositionY[i] >> 6;
            pool.States[i] = (AgentState)(i % 5);
            pool.Hunger[i] = (i % 100);
            pool.Sleep[i] = ((i * 3) % 100);
            pool.Fatigue[i] = ((i * 7) % 100);
            pool.EnvironmentSatisfaction[i] = 80f;
            pool.Mood[i] = 100f;
            pool.CellStayTime[i] = (i % 10 == 0) ? 0f : 1.5f;
        }

        // 1. Warm-up
        for (int w = 0; w < 5; w++)
        {
            SimdNeedsBatch.UpdateNeeds(pool, 0, AgentCount, 0.05f, false);
            SimdNeedsBatch.UpdateCells(pool, 0, AgentCount, 0.05f);
        }

        // 2. Бенчмарк SimdNeedsBatch (UpdateNeeds + UpdateCells на 10k агентов)
        const int SimdIterations = 200;
        long memBefore = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        for (int it = 0; it < SimdIterations; it++)
        {
            SimdNeedsBatch.UpdateNeeds(pool, 0, AgentCount, 0.05f, false);
            SimdNeedsBatch.UpdateCells(pool, 0, AgentCount, 0.05f);
        }
        sw.Stop();
        long memAfter = GC.GetTotalAllocatedBytes(true);
        double simdTotalMs = sw.Elapsed.TotalMilliseconds;
        double simdPerCallMs = simdTotalMs / SimdIterations;
        long simdAllocBytes = memAfter - memBefore;

        GD.Print($"[SIMD 10K] {SimdIterations} итераций: Total={simdTotalMs:F2} ms, PerCall={simdPerCallMs:F4} ms, Alloc={simdAllocBytes} bytes ({simdAllocBytes / SimdIterations} B/call)");

        // 3. Бенчмарк планировщика DynamicWorkScheduler на 10k элементов (ForEachRange)
        const int SchedulerIterations = 100;
        memBefore = GC.GetTotalAllocatedBytes(true);
        sw.Restart();
        long sum = 0;
        for (int it = 0; it < SchedulerIterations; it++)
        {
            DynamicWorkScheduler.Shared.ForEachRange(AgentCount, (s, e) =>
            {
                long localSum = 0;
                for (int i = s; i < e; i++)
                {
                    localSum += i;
                }
                System.Threading.Interlocked.Add(ref sum, localSum);
            }, WorkKind.Fast);
        }
        sw.Stop();
        memAfter = GC.GetTotalAllocatedBytes(true);
        double schedTotalMs = sw.Elapsed.TotalMilliseconds;
        double schedPerCallMs = schedTotalMs / SchedulerIterations;
        long schedAllocBytes = memAfter - memBefore;

        GD.Print($"[SCHEDULER 10K] {SchedulerIterations} итераций: Total={schedTotalMs:F2} ms, PerCall={schedPerCallMs:F4} ms, Alloc={schedAllocBytes} bytes ({schedAllocBytes / SchedulerIterations} B/call)");

        // 4. Бенчмарк WorkStealingQueue при конкуренции воров
        const int StealIterations = 50000;
        var queue = new WorkStealingQueue<WorkItem>(2048);
        for (int i = 0; i < 1000; i++)
            queue.TryPushLocal(new WorkItem(i, 32, 1f, WorkKind.Fast));

        sw.Restart();
        int ownerPops = 0;
        int thiefSteals = 0;
        var thiefBuf = new System.Collections.Generic.List<WorkItem>(64);
        var t1 = System.Threading.Tasks.Task.Run(() =>
        {
            for (int k = 0; k < StealIterations; k++)
            {
                if (queue.TryPushLocal(new WorkItem(k, 32, 1f, WorkKind.Fast)))
                {
                    if (queue.TryPopLocal(out _))
                        ownerPops++;
                }
            }
        });
        var t2 = System.Threading.Tasks.Task.Run(() =>
        {
            for (int k = 0; k < StealIterations; k++)
            {
                thiefBuf.Clear();
                int st = queue.TryStealHalfInto(thiefBuf);
                thiefSteals += st;
            }
        });
        System.Threading.Tasks.Task.WaitAll(t1, t2);
        sw.Stop();
        double wsqMs = sw.Elapsed.TotalMilliseconds;
        GD.Print($"[WSQ CONCURRENCY] {StealIterations} ops: {wsqMs:F2} ms (Pops={ownerPops}, Steals={thiefSteals})");

        // 5. Бенчмарк Phase3a полного цикла (двухпроходный с False Sharing счетчиками)
        const int Phase3aIterations = 100;
        long[] dbgCount = new long[8];
        long[] dbgTicks = new long[5];
        memBefore = GC.GetTotalAllocatedBytes(true);
        sw.Restart();
        for (int it = 0; it < Phase3aIterations; it++)
        {
            // Проход 1: Fused Needs + Cells
            DynamicWorkScheduler.Shared.ForEachRange(AgentCount, (s, e) =>
            {
                long tn0 = Stopwatch.GetTimestamp();
                SimdNeedsBatch.UpdateNeeds(pool, s, e, 0.05f, false);
                long tn1 = Stopwatch.GetTimestamp();
                SimdNeedsBatch.UpdateCells(pool, s, e, 0.05f);
                long tn2 = Stopwatch.GetTimestamp();
                System.Threading.Interlocked.Add(ref dbgTicks[0], tn1 - tn0);
                System.Threading.Interlocked.Add(ref dbgTicks[1], tn2 - tn1);
            }, WorkKind.Fast);

            // Проход 2: Rest + False Sharing счетчики в 64-байтной строке
            DynamicWorkScheduler.Shared.ForEachRange(AgentCount, (s, e) =>
            {
                long t0 = Stopwatch.GetTimestamp();
                int idle = 0, slice = 0, hungry = 0, sleepy = 0, env = 0, changed = 0;
                for (int i = s; i < e; i++)
                {
                    AgentState state = pool.States[i];
                    bool cellChanged = pool.CellStayTime[i] == 0f;
                    if (state == AgentState.Idle)
                    {
                        idle++;
                        if ((i & 3) == 0)
                        {
                            slice++;
                            if (pool.Hunger[i] > 70f) hungry++;
                            if (pool.Sleep[i] > 80f || pool.Fatigue[i] > 80f) sleepy++;
                            if (pool.EnvironmentSatisfaction[i] < 40f) env++;
                        }
                    }
                    if (cellChanged) changed++;
                }
                long t1 = Stopwatch.GetTimestamp();
                System.Threading.Interlocked.Add(ref dbgTicks[2], t1 - t0);
                System.Threading.Interlocked.Add(ref dbgCount[0], e - s);
                System.Threading.Interlocked.Add(ref dbgCount[1], idle);
                System.Threading.Interlocked.Add(ref dbgCount[2], slice);
                System.Threading.Interlocked.Add(ref dbgCount[3], hungry);
                System.Threading.Interlocked.Add(ref dbgCount[4], sleepy);
                System.Threading.Interlocked.Add(ref dbgCount[5], env);
                System.Threading.Interlocked.Add(ref dbgCount[6], changed);
            }, WorkKind.Fast);
        }
        sw.Stop();
        memAfter = GC.GetTotalAllocatedBytes(true);
        double p3aTotalMs = sw.Elapsed.TotalMilliseconds;
        double p3aPerCallMs = p3aTotalMs / Phase3aIterations;
        long p3aAllocBytes = memAfter - memBefore;

        GD.Print($"[PHASE3A TWO-PASS 10K] {Phase3aIterations} итераций: Total={p3aTotalMs:F2} ms, PerCall={p3aPerCallMs:F4} ms, Alloc={p3aAllocBytes} bytes ({p3aAllocBytes / Phase3aIterations} B/call)");

        // 6. Бенчмарк Phase3a оптимизированный (однопроходный, L1 cache-blocked + padded slots без False Sharing)
        var slots = new PaddedSlot[32];
        memBefore = GC.GetTotalAllocatedBytes(true);
        sw.Restart();
        for (int it = 0; it < Phase3aIterations; it++)
        {
            DynamicWorkScheduler.Shared.ForEachRange(AgentCount, (slot, s, e) =>
            {
                long tn0 = Stopwatch.GetTimestamp();
                SimdNeedsBatch.UpdateNeeds(pool, s, e, 0.05f, false);
                long tn1 = Stopwatch.GetTimestamp();
                SimdNeedsBatch.UpdateCells(pool, s, e, 0.05f);
                long tn2 = Stopwatch.GetTimestamp();

                int idle = 0, slice = 0, hungry = 0, sleepy = 0, env = 0, changed = 0;
                for (int i = s; i < e; i++)
                {
                    AgentState state = pool.States[i];
                    bool cellChanged = pool.CellStayTime[i] == 0f;
                    if (state == AgentState.Idle)
                    {
                        idle++;
                        if ((i & 3) == 0)
                        {
                            slice++;
                            if (pool.Hunger[i] > 70f) hungry++;
                            if (pool.Sleep[i] > 80f || pool.Fatigue[i] > 80f) sleepy++;
                            if (pool.EnvironmentSatisfaction[i] < 40f) env++;
                        }
                    }
                    if (cellChanged) changed++;
                }
                long tn3 = Stopwatch.GetTimestamp();

                if ((uint)slot < (uint)slots.Length)
                {
                    ref var st = ref slots[slot];
                    st.TicksNeeds += tn1 - tn0;
                    st.TicksCells += tn2 - tn1;
                    st.TicksRest += tn3 - tn2;
                    st.CountAgents += e - s;
                    st.CountIdle += idle;
                    st.CountSlice += slice;
                    st.CountHungry += hungry;
                    st.CountSleepy += sleepy;
                    st.CountEnv += env;
                    st.CountChanged += changed;
                }
            }, WorkKind.Fast);
        }
        sw.Stop();
        memAfter = GC.GetTotalAllocatedBytes(true);
        double p3aOptTotalMs = sw.Elapsed.TotalMilliseconds;
        double p3aOptPerCallMs = p3aOptTotalMs / Phase3aIterations;
        long p3aOptAllocBytes = memAfter - memBefore;

        double p3aSpeedup = p3aPerCallMs / p3aOptPerCallMs;
        GD.Print($"[PHASE3A OPTIMIZED 10K] {Phase3aIterations} итераций: Total={p3aOptTotalMs:F2} ms, PerCall={p3aOptPerCallMs:F4} ms, Alloc={p3aOptAllocBytes} bytes ({p3aOptAllocBytes / Phase3aIterations} B/call)");
        GD.Print($"[PHASE3A SPEEDUP] Ускорение: {p3aSpeedup:F2}x (Было {p3aPerCallMs:F4} ms -> Стало {p3aOptPerCallMs:F4} ms)");

        // 7. Итоговый отчет
        GD.Print("==================================================");
        GD.Print($"[BENCHMARK SUMMARY] Simd={simdPerCallMs:F4}ms ({simdAllocBytes / SimdIterations} B/call), Sched={schedPerCallMs:F4}ms ({schedAllocBytes / SchedulerIterations} B/call), Phase3a_Old={p3aPerCallMs:F4}ms, Phase3a_Opt={p3aOptPerCallMs:F4}ms (Speedup {p3aSpeedup:F2}x, {p3aOptAllocBytes / Phase3aIterations} B/call), WSQ={wsqMs:F2}ms");
        GD.Print("==================================================");
        return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[BENCHMARK 10K FAILED] {ex}");
            return false;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 128)]
    private struct PaddedSlot
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public long TicksNeeds;
        [System.Runtime.InteropServices.FieldOffset(8)] public long TicksCells;
        [System.Runtime.InteropServices.FieldOffset(16)] public long TicksRest;
        [System.Runtime.InteropServices.FieldOffset(24)] public long CountAgents;
        [System.Runtime.InteropServices.FieldOffset(32)] public long CountIdle;
        [System.Runtime.InteropServices.FieldOffset(40)] public long CountSlice;
        [System.Runtime.InteropServices.FieldOffset(48)] public long CountHungry;
        [System.Runtime.InteropServices.FieldOffset(56)] public long CountSleepy;
        [System.Runtime.InteropServices.FieldOffset(64)] public long CountEnv;
        [System.Runtime.InteropServices.FieldOffset(72)] public long CountChanged;
    }
}
