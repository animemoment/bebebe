using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.Simulation.Jobs;

namespace Game.Tests;

/// <summary>
/// task-14 / P8а: бенчмарк распределителя работ «ДО» (точка сравнения с §119).
///
/// Замеряет фактический продакшн-путь:
///  1) Регистрация 1/1k/10k/100k: поштучно (RegisterJob) vs батчем (RegisterBatch)
///     vs игровой вход JobBroker.RegisterTreeChopBatch. Мс и мкс/работу.
///  2) Стоимость выдачи (TryClaimJobForAgent — тот же API, что в JobDispatcher)
///     при 1k/10k/100k работ: попадание (claim+release) и худший промах
///     (все работы на cooldown — полный обход чанков/маски).
///  3) ЗАЛП: 10k выдач через JobDispatcher.DispatchPendingJobs — сумма мс и худший вызов.
///  4) Мемоизация CanExecute: счётчик реальных вызовов handler'а за эпоху
///     (memo ON — одна BeginClaimEpoch на 100 агентов vs OFF — эпоха на каждого).
///  5) DispatchZones: стоимость вызова при 1/10/100 Work-зонах (кап MaxZonesPerPass=4).
///  6) Память: GC.GetAllocatedBytesForCurrentThread до/после 100k регистрации.
///
/// Детерминизм: собственный LCG с фиксированным сидом. src/** не правится.
/// </summary>
public partial class BenchJobAssignment : RefCounted
{
    private const int MapSize = 512; // 512×512 = 1024 чанка пирамиды, 64 макро-сектора

    private static readonly Stopwatch Clock = new();
    private static long _rng;
    private static int _canExecCalls;
    private static int _canExecInnerFails;

    /// <summary>Обёртка TreeChoppingJobHandler со счётчиком реальных CanAgentExecute.</summary>
    private sealed class CountingTreeHandler : IJobHandler
    {
        private readonly TreeChoppingJobHandler _inner = new();

        public JobTypeId TypeId => _inner.TypeId;
        public JobExecutionType ExecutionType => _inner.ExecutionType;
        public JobPriorityTier DefaultPriority => _inner.DefaultPriority;
        public ToolRequirement RequiredTool => _inner.RequiredTool;

        public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
        {
            Interlocked.Increment(ref _canExecCalls);
            bool ok = _inner.CanAgentExecute(agentIndex, in job, pool, ctx);
            if (!ok) Interlocked.Increment(ref _canExecInnerFails);
            return ok;
        }

        public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
            => _inner.OnStart(agentIndex, in job, pool, ctx);

        public void ExecuteParallel(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
            => _inner.ExecuteParallel(agentIndex, deltaTime, pool, ctx);

        public void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx)
            => _inner.Commit(agentIndex, deltaTime, pool, ctx);

        public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx)
            => _inner.OnCancel(agentIndex, pool, ctx);
    }

    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[JOB-BENCH] Распределитель работ «ДО» (task-14 / P8а)");
        GD.Print("==================================================");
        try
        {
            JobRegistry.Register(new CountingTreeHandler());

            (SimulationContext ctx, bool[,] trees) = MakeCtx();

            Scenario1Registration();
            Scenario2ClaimCost(ctx, trees);
            Scenario3Salvo10k(ctx, trees);
            Scenario4Memoization(ctx, trees);
            Scenario5DispatchZones(ctx, trees);
            Scenario6MissStorm(ctx, trees);

            GD.Print("==================================================");
            GD.Print("[JOB-BENCH OK] ALL SCENARIOS PASSED (Code 0)");
            GD.Print("==================================================");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[JOB-BENCH FAILED] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
        finally
        {
            CleanupAll();
        }
    }

    // ------------------------------------------------------------------ //
    //  Инфраструктура                                                     //
    // ------------------------------------------------------------------ //

    private static void SeedRng(ulong s) => _rng = (long)s;

    private static int NextInt(int maxInclusive)
    {
        _rng = _rng * 6364136223846793005L + 1442695040888963407L;
        uint v = (uint)((ulong)_rng >> 33);
        return (int)(v % (uint)maxInclusive);
    }

    private static (SimulationContext Ctx, bool[,] Trees) MakeCtx()
    {
        var ground = new TileType[MapSize, MapSize];
        var trees = new bool[MapSize, MapSize];
        var solid = new bool[MapSize, MapSize];
        var walkable = new List<(int X, int Y)>(MapSize * MapSize);
        for (int x = 0; x < MapSize; x++)
            for (int y = 0; y < MapSize; y++)
            {
                ground[x, y] = TileType.Grass;
                walkable.Add((x, y));
            }

        var spatialGrid = new AgentSpatialGrid(MapSize, MapSize);
        var movement = new AgentMovementService();
        var ctx = new SimulationContext(ground, null, trees, solid, walkable, spatialGrid, movement, new Random(42), 64);
        return (ctx, trees);
    }

    /// <summary>Детерминированные уникальные позиции работ; trees==null — без разметки деревьев.</summary>
    private static List<JobData> MakeJobs(int count, bool[,] trees, ulong seed, int startId = 0)
    {
        SeedRng(seed);
        var jobs = new List<JobData>(count);
        var used = new HashSet<(int X, int Y)>(count * 2);
        while (jobs.Count < count)
        {
            int x = NextInt(MapSize);
            int y = NextInt(MapSize);
            if (!used.Add((x, y))) continue;

            jobs.Add(new JobData
            {
                Id = startId + jobs.Count,
                TypeId = JobTypeId.TreeChopping,
                ExecutionType = JobExecutionType.Stationary,
                PriorityTier = JobPriorityTier.TreeChopping,
                RequiredTool = ToolRequirement.None,
                TargetX = x,
                TargetY = y,
                StandX = x,
                StandY = y,
                MaxWorkers = 1,
                WorkDuration = 8.0f
            });
            if (trees != null) trees[x, y] = true;
        }
        return jobs;
    }

    private static AgentDataPool MakePool(int count, ulong seed)
    {
        var pool = new AgentDataPool(count);
        SeedRng(seed);
        for (int i = 0; i < count; i++)
        {
            int cx = NextInt(MapSize);
            int cy = NextInt(MapSize);
            pool.PositionX[i] = cx * 64f + 32f;
            pool.PositionY[i] = cy * 64f + 32f;
            pool.CurrentCellX[i] = cx;
            pool.CurrentCellY[i] = cy;
            pool.States[i] = AgentState.Idle;
            pool.CurrentJobId[i] = -1;
            pool.CurrentJobType[i] = JobTypeId.None;
            pool.EquippedTools[i] = ToolRequirement.None;
        }
        return pool;
    }

    private static void CleanupAll()
    {
        try
        {
            JobDispatcher.Instance.JobIndex.Clear();
            JobDispatcher.Instance.IdleWorkers.Clear();
            ZoneManager.Instance.ClearAll();
            ConstructionManager.Instance.ClearAll();
            GroundItemManager.Instance.ClearAll();
            StockpileManager.Instance.ClearAll();
        }
        catch { }
    }

    // ------------------------------------------------------------------ //
    //  1. Регистрация: поштучно vs батч vs брокер                         //
    // ------------------------------------------------------------------ //

    private void Scenario1Registration()
    {
        GD.Print("--- [JOB-BENCH] 1. РЕГИСТРАЦИЯ (мс всего | мкс/работу) ---");
        var sizes = new[] { 1, 1000, 10000, 100000 };

        foreach (int n in sizes)
        {
            var jobs = MakeJobs(n, null, 1000 + (ulong)n); // деревья не нужны для регистрации

            // Поштучно
            var p1 = new JobSpatialPyramid();
            Clock.Restart();
            for (int i = 0; i < n; i++) p1.RegisterJob(jobs[i]);
            Clock.Stop();
            double perJobUs1 = n > 0 ? Clock.Elapsed.TotalMilliseconds * 1000.0 / n : 0;
            GD.Print($"[JOB-BENCH] reg_individual n={n} total_ms={Clock.Elapsed.TotalMilliseconds:F2} us_per_job={perJobUs1:F3} total={p1.TotalCount}");

            // Батчем (один lock)
            var p2 = new JobSpatialPyramid();
            Clock.Restart();
            p2.RegisterBatch(jobs);
            Clock.Stop();
            double perJobUs2 = n > 0 ? Clock.Elapsed.TotalMilliseconds * 1000.0 / n : 0;
            GD.Print($"[JOB-BENCH] reg_batch n={n} total_ms={Clock.Elapsed.TotalMilliseconds:F2} us_per_job={perJobUs2:F3} total={p2.TotalCount}");

            // Игровой вход: JobBroker.RegisterTreeChopBatch (ctx=null — без pathfinding stand-поиска)
            JobDispatcher.Instance.JobIndex.Clear();
            var cells = new List<(int X, int Y)>(n);
            for (int i = 0; i < n; i++) cells.Add((jobs[i].TargetX, jobs[i].TargetY));
            Clock.Restart();
            JobBroker.Instance.RegisterTreeChopBatch(cells, null);
            Clock.Stop();
            double perJobUs3 = n > 0 ? Clock.Elapsed.TotalMilliseconds * 1000.0 / n : 0;
            GD.Print($"[JOB-BENCH] reg_broker_batch n={n} total_ms={Clock.Elapsed.TotalMilliseconds:F2} us_per_job={perJobUs3:F3} unclaimed={JobDispatcher.Instance.JobIndex.UnclaimedCount}");
            JobDispatcher.Instance.JobIndex.Clear();
        }

        // 6. Память: свежая пирамида + батч 100k (список построен ДО замера).
        var jobs100k = MakeJobs(100000, null, 777);
        var pMem = new JobSpatialPyramid();
        long before = GC.GetAllocatedBytesForCurrentThread();
        pMem.RegisterBatch(jobs100k);
        long after = GC.GetAllocatedBytesForCurrentThread();
        double bytesPerJob = (after - before) / 100000.0;
        GD.Print($"[JOB-BENCH] memory_100k allocated_bytes={(after - before)} bytes_per_job={bytesPerJob:F1} (SoA-рост + chunk-массивы + posMap; сам List<JobData> построен до замера)");
    }

    // ------------------------------------------------------------------ //
    //  2. Стоимость выдачи vs число работ (главный тест на сложность)     //
    // ------------------------------------------------------------------ //

    private void Scenario2ClaimCost(SimulationContext ctx, bool[,] trees)
    {
        GD.Print("--- [JOB-BENCH] 2. ВЫДАЧА (TryClaimJobForAgent) vs число работ ---");
        var sizes = new[] { 1000, 10000, 100000 };
        const int HitClaims = 1000;
        const int MissClaims = 200;
        double worstAvgUs = 0;

        foreach (int jn in sizes)
        {
            var pyramid = new JobSpatialPyramid();
            var jobs = MakeJobs(jn, trees, 2000 + (ulong)jn);
            pyramid.RegisterBatch(jobs);

            var pool = MakePool(64, 3000 + (ulong)jn);

            // Попадание: claim + release (работа возвращается в пул — каждая проба «свежая»).
            int hits = 0;
            Clock.Restart();
            for (int i = 0; i < HitClaims; i++)
            {
                int a = i & 63;
                if (pyramid.TryClaimJobForAgent(pool.CurrentCellX[a], pool.CurrentCellY[a],
                        ToolRequirement.None, pool, a, ctx, false, false, out JobData job))
                {
                    hits++;
                    pyramid.ReleaseWorkerClaim(job.Id);
                }
            }
            Clock.Stop();
            double hitUs = Clock.Elapsed.TotalMilliseconds * 1000.0 / HitClaims;

            // Худший промах: все работы на cooldown → полный обход (чанки + маска L2).
            for (int id = 0; id < jn; id++)
                pyramid.MarkJobUnreachable(id, 3_600_000);

            int misses = 0;
            Clock.Restart();
            for (int i = 0; i < MissClaims; i++)
            {
                int a = i & 63;
                if (!pyramid.TryClaimJobForAgent(pool.CurrentCellX[a], pool.CurrentCellY[a],
                        ToolRequirement.None, pool, a, ctx, false, false, out _))
                    misses++;
            }
            Clock.Stop();
            double missUs = Clock.Elapsed.TotalMilliseconds * 1000.0 / MissClaims;

            GD.Print($"[JOB-BENCH] claim jobs={jn} hits={hits}/{HitClaims} hit_us_per_call={hitUs:F2} miss_us_per_call={missUs:F2} (miss=полный обход с cooldown)");
            if (hitUs > worstAvgUs) worstAvgUs = hitUs;
        }

        if (worstAvgUs > 2000.0)
            throw new Exception($"Средняя выдача {worstAvgUs:F1} мкс > 2000 мс-порога (2мс) — тест красный.");
    }

    // ------------------------------------------------------------------ //
    //  3. ЗАЛП: 10 000 выдач в одном «тике» (через JobDispatcher)         //
    // ------------------------------------------------------------------ //

    private void Scenario3Salvo10k(SimulationContext ctx, bool[,] trees)
    {
        GD.Print("--- [JOB-BENCH] 3. ЗАЛП: 10k выдач через DispatchPendingJobs ---");
        CleanupAll();

        const int JobCount = 12000;
        const int Agents = 10240;
        var jobs = MakeJobs(JobCount, trees, 4242);
        JobDispatcher.Instance.JobIndex.RegisterBatch(jobs);

        var pool = MakePool(Agents, 4343);
        for (int i = 0; i < Agents; i++)
            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(i, pool);

        int totalAssigned = 0;
        int calls = 0;
        double totalMs = 0;
        double maxCallMs = 0;

        // Диспетчер имеет бюджет 0.8мс/вызов и кап 4096 назначений (MaxAssignmentsPerTick):
        // 10k выдач НЕ помещаются в один тик по дизайну. Меряем честно: повторные вызовы
        // (как реальная симуляция тик за тиком) до 10k назначений — суммарные мс,
        // худший одиночный вызов и среднее на назначение.
        while (totalAssigned < 10000 && calls < 200)
        {
            int unclaimedBefore = JobDispatcher.Instance.JobIndex.UnclaimedCount;
            Clock.Restart();
            JobDispatcher.Instance.DispatchPendingJobs(pool, ctx);
            Clock.Stop();
            double ms = Clock.Elapsed.TotalMilliseconds;
            int delta = unclaimedBefore - JobDispatcher.Instance.JobIndex.UnclaimedCount;
            totalAssigned += delta;
            totalMs += ms;
            if (ms > maxCallMs) maxCallMs = ms;
            calls++;
        }

        double usPerAssign = totalAssigned > 0 ? totalMs * 1000.0 / totalAssigned : 0;
        GD.Print($"[JOB-BENCH] salvo_10k dispatch_calls={calls} assigned={totalAssigned} total_ms={totalMs:F2} max_call_ms={maxCallMs:F2} us_per_assignment={usPerAssign:F2}");
        if (totalAssigned < 10000)
            throw new Exception($"Залп выдал только {totalAssigned}/10000 за {calls} вызовов.");
    }

    // ------------------------------------------------------------------ //
    //  4. Мемоизация CanExecute: пересчёты за эпоху                       //
    // ------------------------------------------------------------------ //

    private void Scenario4Memoization(SimulationContext ctx, bool[,] trees)
    {
        GD.Print("--- [JOB-BENCH] 4. МЕМОИЗАЦИЯ CanExecute (S-033) ---");
        CleanupAll();

        const int JobCount = 5000;
        const int Agents = 100;
        var pyramid = new JobSpatialPyramid();
        var jobs = MakeJobs(JobCount, trees, 5151);
        pyramid.RegisterBatch(jobs);
        var pool = MakePool(Agents, 5252);

        // A) memo ON: одна эпоха на всех агентов (как в DispatchPendingJobs).
        _canExecCalls = 0;
        _canExecInnerFails = 0;
        pyramid.BeginClaimEpoch();
        Clock.Restart();
        int assignedA = 0;
        for (int a = 0; a < Agents; a++)
        {
            if (pyramid.TryClaimJobForAgent(pool.CurrentCellX[a], pool.CurrentCellY[a],
                    ToolRequirement.None, pool, a, ctx, false, false, out JobData job))
            {
                assignedA++;
                pyramid.ReleaseWorkerClaim(job.Id);
            }
        }
        Clock.Stop();
        double msA = Clock.Elapsed.TotalMilliseconds;
        int callsWithMemo = _canExecCalls;
        GD.Print($"[JOB-BENCH] memo_on agents={Agents} canexec_calls={callsWithMemo} assigned={assignedA} total_ms={msA:F2}");

        // B) memo OFF: свежая эпоха перед КАЖДЫМ агентом (мемо не работает).
        _canExecCalls = 0;
        _canExecInnerFails = 0;
        Clock.Restart();
        int assignedB = 0;
        for (int a = 0; a < Agents; a++)
        {
            pyramid.BeginClaimEpoch();
            if (pyramid.TryClaimJobForAgent(pool.CurrentCellX[a], pool.CurrentCellY[a],
                    ToolRequirement.None, pool, a, ctx, false, false, out JobData job))
            {
                assignedB++;
                pyramid.ReleaseWorkerClaim(job.Id);
            }
        }
        Clock.Stop();
        double msB = Clock.Elapsed.TotalMilliseconds;
        int callsNoMemo = _canExecCalls;
        double effect = callsNoMemo > 0 ? 1.0 - (double)callsWithMemo / callsNoMemo : 0;
        GD.Print($"[JOB-BENCH] memo_off agents={Agents} canexec_calls={callsNoMemo} assigned={assignedB} total_ms={msB:F2}");
        GD.Print($"[JOB-BENCH] memo_effect пересчётов_с={callsWithMemo} пересчётов_без={callsNoMemo} экономия={effect:P1} (inner_fails_on={_canExecInnerFails})");

        if (assignedA != assignedB)
            throw new Exception($"Мемоизация меняет результат: assigned ON={assignedA} vs OFF={assignedB}.");
    }

    // ------------------------------------------------------------------ //
    //  5. DispatchZones: 1 / 10 / 100 Work-зон                            //
    // ------------------------------------------------------------------ //

    private void Scenario5DispatchZones(SimulationContext ctx, bool[,] trees)
    {
        GD.Print("--- [JOB-BENCH] 5. DispatchZones (Work-зоны, кап MaxZonesPerPass=4) ---");
        foreach (int zoneCount in new[] { 1, 10, 100 })
        {
            CleanupAll();
            SeedRng(6000 + (ulong)zoneCount);
            ulong treeMask = 1UL << (byte)JobTypeId.TreeChopping; // бит 1 (bit0=запрещён)

            int zonesCreated = 0;
            int jobsPerZone = 24;
            for (int k = 0; k < zoneCount; k++)
            {
                int cx = 40 + (k % 10) * 47;
                int cy = 40 + (k / 10) * 47;
                if (!ZoneManager.Instance.CreateWorkZoneAt(cx, cy, treeMask, 128, 8, out Zone zone, out string err))
                {
                    GD.Print($"[JOB-BENCH] zone_create_fail cx={cx} cy={cy} err={err}");
                    continue;
                }
                zonesCreated++;

                // Работы внутри зоны (радиус диска ~6 тайлов при target=128).
                var cells = new List<(int X, int Y)>(jobsPerZone);
                for (int i = 0; i < jobsPerZone; i++)
                {
                    int dx = NextInt(13) - 6;
                    int dy = NextInt(13) - 6;
                    int tx = cx + dx, ty = cy + dy;
                    if ((uint)tx >= (uint)MapSize || (uint)ty >= (uint)MapSize) { i--; continue; }
                    cells.Add((tx, ty));
                }
                JobBroker.Instance.RegisterTreeChopBatch(cells, null);
                foreach (var (tx, ty) in cells) trees[tx, ty] = true;

                // 4 свободных агента в центре зоны.
                for (int a = 0; a < 4; a++)
                {
                    int ai = zonesCreated * 4 + a;
                    if (ai >= _zonePool.Capacity) continue;
                    _zonePool.PositionX[ai] = cx * 64f + 32f;
                    _zonePool.PositionY[ai] = cy * 64f + 32f;
                    _zonePool.CurrentCellX[ai] = cx;
                    _zonePool.CurrentCellY[ai] = cy;
                    _zonePool.States[ai] = AgentState.Idle;
                    _zonePool.CurrentJobId[ai] = -1;
                    JobDispatcher.Instance.IdleWorkers.AddIdleWorker(ai, _zonePool);
                }
            }

            double sum = 0, maxMs = 0;
            int totalAssigned = 0;
            const int Calls = 3;
            for (int c = 0; c < Calls; c++)
            {
                Clock.Restart();
                int assigned = ZoneManager.Instance.DispatchZones(_zonePool, ctx);
                Clock.Stop();
                double ms = Clock.Elapsed.TotalMilliseconds;
                sum += ms;
                if (ms > maxMs) maxMs = ms;
                totalAssigned += assigned;
            }
            GD.Print($"[JOB-BENCH] dispatch_zones zones={zonesCreated} calls={Calls} avg_ms_per_call={sum / Calls:F2} max_call_ms={maxMs:F2} assigned_total={totalAssigned} (кап 4 зоны/вызов)");
        }
    }

    // ------------------------------------------------------------------ //
    //  6. MISS-ШТОРМ (task-18 P8в) + детерминизм назначений               //
    // ------------------------------------------------------------------ //

    private void Scenario6MissStorm(SimulationContext ctx, bool[,] trees)
    {
        GD.Print("--- [JOB-BENCH] 6. MISS-ШТОРМ + детерминизм (task-18 P8в) ---");

        // A) Детерминизм: фиксированные сиды, 25 раундов × 200 агентов claim+release.
        //    Последовательность назначенных jobId ДОЛЖНА быть бит-в-бит той же до/после P8в.
        {
            var pyramid = new JobSpatialPyramid();
            var jobs = MakeJobs(2000, trees, 9100);
            pyramid.RegisterBatch(jobs);
            var pool = MakePool(200, 9200);

            var seq = new System.Text.StringBuilder(4096);
            ulong hash = 1469598103934665603UL; // FNV-1a
            int totalClaims = 0;
            const int Rounds = 25;
            for (int r = 0; r < Rounds; r++)
            {
                pyramid.BeginClaimEpoch();
                for (int a = 0; a < 200; a++)
                {
                    if (pyramid.TryClaimJobForAgent(pool.CurrentCellX[a], pool.CurrentCellY[a],
                            ToolRequirement.None, pool, a, ctx, false, false, out JobData job))
                    {
                        totalClaims++;
                        hash = (hash ^ (ulong)job.Id) * 1099511628211UL;
                        if (seq.Length < 4000) seq.Append(job.Id).Append(',');
                        pyramid.ReleaseWorkerClaim(job.Id);
                    }
                    else
                    {
                        hash = (hash ^ 0xDEADBEEFUL) * 1099511628211UL;
                        seq.Append(".,");
                    }
                }
            }
            string seqStr = seq.ToString();
            if (seqStr.Length > 200) seqStr = seqStr.Substring(0, 200) + "...";
            GD.Print($"[JOB-BENCH] determinism claims={totalClaims} seq_hash_fnv1a={hash} seq_head={seqStr}");
        }

        // B) MISS-ШТОРМ: 100k работ, ВСЕ на cooldown (3600с), 64 агента — только промахи.
        {
            var pyramid = new JobSpatialPyramid();
            var jobs = MakeJobs(100000, trees, 9300);
            pyramid.RegisterBatch(jobs);
            for (int id = 0; id < 100000; id++)
                pyramid.MarkJobUnreachable(id, 3_600_000);
            var pool = MakePool(64, 9400);

            const int MissCalls = 400;
            // Прогрев (JIT) — не замеряется.
            for (int i = 0; i < 8; i++)
                pyramid.TryClaimJobForAgent(pool.CurrentCellX[0], pool.CurrentCellY[0],
                    ToolRequirement.None, pool, 0, ctx, false, false, out _);

            double sum = 0, maxUs = 0;
            Clock.Restart();
            for (int i = 0; i < MissCalls; i++)
            {
                int a = i & 63;
                bool claimed = pyramid.TryClaimJobForAgent(pool.CurrentCellX[a], pool.CurrentCellY[a],
                    ToolRequirement.None, pool, a, ctx, false, false, out _);
                if (claimed) throw new Exception("MISS-шторм: работа ЗАЗВАНА при тотальном кулдауне — флаги сломаны.");
            }
            Clock.Stop();
            sum = Clock.Elapsed.TotalMilliseconds;
            maxUs = 0;
            // Отдельный замер худшего одиночного вызова.
            for (int i = 0; i < MissCalls; i++)
            {
                int a = (i + 17) & 63;
                Clock.Restart();
                pyramid.TryClaimJobForAgent(pool.CurrentCellX[a], pool.CurrentCellY[a],
                    ToolRequirement.None, pool, a, ctx, false, false, out _);
                Clock.Stop();
                double us = Clock.Elapsed.TotalMilliseconds * 1000.0;
                if (us > maxUs) maxUs = us;
            }
            double avgUs = sum * 1000.0 / MissCalls;
            GD.Print($"[JOB-BENCH] miss_storm jobs=100000 all_cooldown miss_calls={MissCalls} avg_us_per_miss={avgUs:F2} max_us_per_miss={maxUs:F2}");
            if (avgUs > 100.0)
                throw new Exception($"Промах {avgUs:F2}мкс > цели 100мкс (было 5725мкс до P8в).");
        }

        // C) Частичный кулдаун: половина работ недостижима — выдача должна продолжаться.
        {
            var pyramid = new JobSpatialPyramid();
            var jobs = MakeJobs(20000, trees, 9500);
            pyramid.RegisterBatch(jobs);
            for (int id = 0; id < 20000; id += 2) // чётные id — на кулдаун
                pyramid.MarkJobUnreachable(id, 3_600_000);
            var pool = MakePool(64, 9600);

            int claimed = 0;
            Clock.Restart();
            for (int i = 0; i < 2000; i++)
            {
                int a = i & 63;
                if (pyramid.TryClaimJobForAgent(pool.CurrentCellX[a], pool.CurrentCellY[a],
                        ToolRequirement.None, pool, a, ctx, false, false, out JobData job))
                {
                    claimed++;
                    pyramid.ReleaseWorkerClaim(job.Id);
                    if ((job.Id & 1) == 0)
                        throw new Exception($"ЗАЗВАНА работа {job.Id} на кулдауне — флаги пропускают НЕдостижимые чанки неправильно.");
                }
            }
            Clock.Stop();
            double usPerCall = Clock.Elapsed.TotalMilliseconds * 1000.0 / 2000;
            GD.Print($"[JOB-BENCH] partial_cooldown jobs=20000 half_on_cd claimed={claimed}/2000 us_per_call={usPerCall:F2} (половина работ доступна — выдача жива)");
            if (claimed == 0)
                throw new Exception("Частичный кулдаун: ни одной выдачи — оптимизация съела живые работы.");
        }
    }

    private static readonly AgentDataPool _zonePool = new(1024);
}
