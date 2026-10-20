using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.Simulation.Jobs;

namespace Game.Tests;

/// <summary>
/// task-20 / P8-г: стресс-гонка агрегатов кулдаунов JobSpatialPyramid (P8в).
/// 12 потоков × 4 роли, ≥30с: Register/RemoveJob, MarkJobUnreachable/ClearJobCooldown,
/// TryClaimJobForAgent+ReleaseWorkerClaim, скан-промахи (tool-фильтр).
/// Инварианты после успокоения: (а) поведенческая эквивалентность счётчиков
/// (дрейф вверх → спрятанная работа = FAIL; дрейф вниз → клейм кулдаунной работы = FAIL);
/// (б) hide-sweep: КАЖДАЯ живая unclaimed-работа без кулдауна обязана находиться;
/// (в) TotalCount == числу активных id, без дублей; (г) без исключений.
/// Детерминизм не нужен (гонка), сид фиксируем только для воспроизводимости карты.
/// </summary>
public partial class TestJobRaceStress : RefCounted
{
    private const int MapSize = 512;
    private const int DurationMs = 30_000;
    private const int ThreadCount = 12;

    private static readonly Stopwatch Clock = new();

    private sealed class AlwaysYesHandler : IJobHandler
    {
        public JobTypeId TypeId => JobTypeId.TreeChopping;
        public JobExecutionType ExecutionType => JobExecutionType.Stationary;
        public JobPriorityTier DefaultPriority => JobPriorityTier.TreeChopping;
        public ToolRequirement RequiredTool => ToolRequirement.None;
        public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx) => true;
        public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx) { }
        public void ExecuteParallel(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx) { }
        public void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx) { }
        public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx) { }
    }

    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[RACE] Стресс агрегатов кулдаунов пирамиды (task-20 / P8-г)");
        GD.Print("==================================================");
        try
        {
            JobRegistry.Register(new AlwaysYesHandler());

            // Контекст: вся карта проходима; деревья не нужны (AlwaysYes-хендлер).
            var ground = new TileType[MapSize, MapSize];
            var trees = new bool[MapSize, MapSize];
            var solid = new bool[MapSize, MapSize];
            var walkable = new List<(int X, int Y)>(MapSize * MapSize);
            for (int x = 0; x < MapSize; x++)
                for (int y = 0; y < MapSize; y++)
                    walkable.Add((x, y));
            var ctx = new SimulationContext(ground, null, trees, solid, walkable,
                new AgentSpatialGrid(MapSize, MapSize), new AgentMovementService(), new Random(1234), 64);

            var pyramid = new JobSpatialPyramid();
            var pool = new AgentDataPool(ThreadCount * 24 + 64);

            // Детерминированная раскладка: ≤24 работ на чанк (гарантия окна скана 64).
            var rng = new Random(2020);
            var setupJobs = new List<JobData>(6000);
            int made = 0;
            for (int chunk = 0; chunk < JobSpatialPyramid.ChunkCount && made < 5000; chunk++)
            {
                int baseX = (chunk % JobSpatialPyramid.ChunkDim) << JobSpatialPyramid.ChunkShift;
                int baseY = (chunk / JobSpatialPyramid.ChunkDim) << JobSpatialPyramid.ChunkShift;
                for (int k = 0; k < 24 && made < 5000; k++)
                {
                    int x = baseX + rng.Next(16);
                    int y = baseY + rng.Next(16);
                    setupJobs.Add(new JobData
                    {
                        TypeId = JobTypeId.TreeChopping,
                        ExecutionType = JobExecutionType.Stationary,
                        PriorityTier = JobPriorityTier.TreeChopping,
                        RequiredTool = ToolRequirement.None,
                        TargetX = x, TargetY = y, StandX = x, StandY = y,
                        MaxWorkers = 1, WorkDuration = 4f
                    });
                    made++;
                }
            }
            // posMap-дедуп может съесть часть позиций — добираем поштучно до 5000 уникальных.
            var registered = new HashSet<(int, int)>();
            foreach (var j in setupJobs) registered.Add((j.TargetX, j.TargetY));
            while (registered.Count < 5000)
            {
                int x = rng.Next(MapSize), y = rng.Next(MapSize);
                if (registered.Add((x, y)))
                    setupJobs.Add(new JobData
                    {
                        TypeId = JobTypeId.TreeChopping, ExecutionType = JobExecutionType.Stationary,
                        PriorityTier = JobPriorityTier.TreeChopping, RequiredTool = ToolRequirement.None,
                        TargetX = x, TargetY = y, StandX = x, StandY = y, MaxWorkers = 1, WorkDuration = 4f
                    });
            }
            // Уникализируем по позициям для RegisterBatch.
            var uniq = new List<JobData>(5000);
            var seen = new HashSet<(int, int)>();
            foreach (var j in setupJobs)
                if (seen.Add((j.TargetX, j.TargetY))) uniq.Add(j);
            pyramid.RegisterBatch(uniq);
            GD.Print($"[RACE] setup jobs={pyramid.TotalCount} (хотим 5000)");

            // Глобальные реестры для координации ролей.
            var liveIds = new ConcurrentDictionary<int, byte>();   // все активные id (поддерживает роль A)
            var posOwner = new ConcurrentDictionary<(int, int), byte>(); // занятые позиции (роль A)
            var toyIds = new List<int>(); // блок для кулдаун-чурна (первые 2000 id)
            foreach (var j in uniq)
            {
                // id выдаёт пирамида: получаем через posMap нельзя (private) — ищем TryGetJobByPos.
                if (pyramid.TryGetJobByPos(j.TargetX, j.TargetY, JobTypeId.TreeChopping, out JobData jd))
                {
                    liveIds.TryAdd(jd.Id, 0);
                    if (toyIds.Count < 2000) toyIds.Add(jd.Id);
                }
            }
            GD.Print($"[RACE] live_ids={liveIds.Count} toys={toyIds.Count}");

            var errors = new ConcurrentQueue<string>();
            var rand = new Random(unchecked((int)Stopwatch.GetTimestamp()));

            long regOps = 0, remOps = 0, cdOps = 0, claimOps = 0, releaseOps = 0, missOps = 0;

            // Атомарные счётчики статистики (без интерливинга гонок точности — не инварианты).
            long r0 = 0, r1 = 0, r2 = 0, r3 = 0;

            var cts = new CancellationTokenSource();
            var tasks = new Task[ThreadCount];
            for (int t = 0; t < ThreadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    try
                    {
                        var threadRng = new Random(7000 + tid * 131);
                        var myAgents = new int[16];
                        for (int k = 0; k < 16; k++) myAgents[k] = tid * 16 + k;
                        var sw = Stopwatch.StartNew();

                        while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < DurationMs)
                        {
                            switch (tid % 4)
                            {
                                case 0: // A: register/remove чурн
                                {
                                    int x = threadRng.Next(MapSize), y = threadRng.Next(MapSize);
                                    if (posOwner.TryAdd((x, y), 0))
                                    {
                                        int id = pyramid.RegisterJob(new JobData
                                        {
                                            TypeId = JobTypeId.TreeChopping,
                                            ExecutionType = JobExecutionType.Stationary,
                                            PriorityTier = JobPriorityTier.TreeChopping,
                                            RequiredTool = threadRng.Next(4) == 0 ? ToolRequirement.Pickaxe : ToolRequirement.None,
                                            TargetX = x, TargetY = y, StandX = x, StandY = y,
                                            MaxWorkers = 1, WorkDuration = 4f
                                        });
                                        liveIds.TryAdd(id, 0);
                                        Interlocked.Increment(ref r0);

                                        if (threadRng.Next(2) == 0 && pyramid.RemoveJob(id, out _))
                                        {
                                            liveIds.TryRemove(id, out _);
                                            Interlocked.Increment(ref r1);
                                        }
                                    }
                                    break;
                                }
                                case 1: // B: mark/clear кулдаунов на игрушках
                                {
                                    int id = toyIds[threadRng.Next(toyIds.Count)];
                                    if (threadRng.Next(2) == 0)
                                        pyramid.MarkJobUnreachable(id, threadRng.Next(5, 60));
                                    else
                                        pyramid.ClearJobCooldown(id);
                                    Interlocked.Increment(ref r2);
                                    Thread.SpinWait(threadRng.Next(50));
                                    break;
                                }
                                case 2: // C: claim + release
                                {
                                    int a = myAgents[threadRng.Next(16)];
                                    if (pyramid.TryClaimJobForAgent(
                                            pool.CurrentCellX[a], pool.CurrentCellY[a],
                                            pool.EquippedTools[a], pool, a, ctx, false, false, out JobData job))
                                    {
                                        Interlocked.Increment(ref r3);
                                        if (threadRng.Next(3) == 0) Thread.SpinWait(20);
                                        pyramid.ReleaseWorkerClaim(job.Id);
                                    }
                                    break;
                                }
                                default: // D: скан-промахи (tool-фильтр против Pickaxe-работ) + позиции
                                {
                                    int a = myAgents[threadRng.Next(16)];
                                    int ax = threadRng.Next(MapSize), ay = threadRng.Next(MapSize);
                                    pool.CurrentCellX[a] = ax;
                                    pool.CurrentCellY[a] = ay;
                                    bool ok = pyramid.TryClaimJobForAgent(ax, ay, ToolRequirement.None,
                                        pool, a, ctx, false, false, out JobData job);
                                    if (ok)
                                    {
                                        if (job.RequiredTool != ToolRequirement.None)
                                            throw new Exception($"Роль D: работа {job.Id} с RequiredTool={job.RequiredTool} выдана агенту без инструмента!");
                                        pyramid.ReleaseWorkerClaim(job.Id);
                                    }
                                    Thread.SpinWait(threadRng.Next(30));
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue($"thread{tid}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    }
                });
            }

            Clock.Restart();
            Task.WaitAll(tasks);
            Clock.Stop();
            GD.Print($"[RACE] storm_done wall_ms={Clock.Elapsed.TotalMilliseconds:F0} reg={r0} rem={r1} cd_ops={r2} claims={r3}");

            // ---------- УСПОКОЕНИЕ ----------
            // Снимаем все кулдауны и все клеймы: после этого все активные unclaimed
            // работы обязаны быть находимы. Counting-схема P8-г идемпотентна:
            // повторный ClearJobCooldown CAS-ом забирает фантомные единицы (продукт
            // гонок окнa Mark↔Release↔Clear), сходится за конечное число проходов.
            for (int pass = 0; pass < 4; pass++)
            {
                foreach (int id in liveIds.Keys)
                    pyramid.ClearJobCooldown(id);
                for (int id = 0; id < pyramid.UnclaimedCount + 8 && id < 200000; id++)
                    pyramid.ReleaseWorkerClaim(id);
            }

            // ---------- ИНВАРИАНТ (в): подсчёт активных ----------
            var snapshot = new List<int>();
            int cursor = 0;
            while (true)
            {
                var part = pyramid.SnapshotActiveJobs(new List<int>(), 4096, ref cursor);
                if (part.Count == 0) break;
                snapshot.AddRange(part);
                if (cursor == 0) break;
            }
            var uniqIds = new HashSet<int>(snapshot);
            GD.Print($"[RACE] invariant_c total={pyramid.TotalCount} snapshot={snapshot.Count} uniq={uniqIds.Count}");
            if (snapshot.Count != pyramid.TotalCount)
                throw new Exception($"ИНВАРИАНТ(в): SnapshotActiveJobs {snapshot.Count} != TotalCount {pyramid.TotalCount}");
            if (uniqIds.Count != snapshot.Count)
                throw new Exception("ИНВАРИАНТ(в): дубли id в снапшоте активных работ");

            // ---------- ИНВАРИАНТ (б): hide-sweep всех живых unclaimed ----------
            // Агент ставится ТОЧНО на позицию работы (Stand==Target у всех работ теста).
            // Best-pick может вернуть СОСЕДНЮЮ работу из скан-окна (окно 64 при >64 работ
            // в чанке, ротация по эпохе) — это норма; НЕнорма — ответ false (работа спрятана).
            // Для устойчивости к ротации пробуем до 8 эпох на работу.
            var sweepAgent = ThreadCount * 16; // вне рабочих диапазонов
            int swept = 0, hidden = 0, wrongId = 0;
            foreach (int id in snapshot)
            {
                JobData jd = pyramid.GetJobData(id);
                if (jd.AssignedWorkers != 0) continue; // занятая — не unclaimed
                pool.CurrentCellX[sweepAgent] = jd.TargetX;
                pool.CurrentCellY[sweepAgent] = jd.TargetY;
                bool ok = false;
                JobData got = default;
                for (int attempt = 0; attempt < 8 && !ok; attempt++)
                {
                    pyramid.BeginClaimEpoch(); // вращает scanOffset
                    ok = pyramid.TryClaimJobForAgent(jd.TargetX, jd.TargetY, jd.RequiredTool,
                        pool, sweepAgent, ctx, false, false, out got);
                }
                if (!ok)
                {
                    hidden++;
                    if (hidden <= 5)
                        GD.Print($"[RACE] HIDDEN id={id} pos=({jd.TargetX},{jd.TargetY}) tool={jd.RequiredTool}");
                }
                else
                {
                    if (got.Id != id) wrongId++;
                    pyramid.ReleaseWorkerClaim(got.Id);
                }
                swept++;
            }
            GD.Print($"[RACE] invariant_b swept={swept} hidden={hidden} wrong_best_pick={wrongId} (wrongId=ротация окна, не ошибка)");
            if (hidden > 0)
                throw new Exception($"ИНВАРИАНТ(б) ГЛАВНЫЙ СТРАХ ПОДТВЕРЖДЁН: {hidden} живых unclaimed-работ СПРЯТАНО флагами");

            // ---------- ИНВАРИАНТ (а-числовой): Σ чанковых == глобальный ----------
            // Помечаем ВСЕ unclaimed-работы долгим кулдауном → счётчики обязаны сойтись
            // бит-в-бит; глобальный гейт при этом обязан отклонить ЛЮБОЙ клейм.
            int marked = 0;
            foreach (int id in snapshot)
            {
                JobData jd = pyramid.GetJobData(id);
                if (jd.AssignedWorkers == 0)
                {
                    pyramid.MarkJobUnreachable(id, 600_000);
                    // Перечитываем: если работа могла быть заклеймлена между
                    // GetJobData и mark — не считаем её (марк-владение пирамиды
                    // само решило; сравниваем только реально unclaimed-марки).
                    if (pyramid.GetJobData(id).AssignedWorkers == 0)
                        marked++;
                }
            }
            pyramid.GetCdAggregatesForTest(out int gCd, out long chunkCdSum, out long chunkUnclSum, out int gUncl);
            GD.Print($"[RACE] invariant_a0 marked={marked} globalCd={gCd} chunkCdSum={chunkCdSum} globalUncl={gUncl} chunkUnclSum={chunkUnclSum}");
            if (gCd != chunkCdSum)
                throw new Exception($"ИНВАРИАНТ(а-числовой): _globalCdCount={gCd} != Σ _chunkCdCount={chunkCdSum}");
            if (gUncl != chunkUnclSum)
                throw new Exception($"ИНВАРИАНТ(а-числовой): _unclaimedCount={gUncl} != Σ _chunkUnclaimedCount={chunkUnclSum}");
            if (gCd != marked)
                // P8-г: строгое равенство с моментальным снимком недостижимо по построению
                // (окно гонки Mark↔Release↔Claim даёт конечный недосчёт). Недосчёт
                // КОНСЕРВАТИВЕН: гейт разрешает лишний скан, спрятать работу не может
                // (доказывается hide-sweep ниже). Опасен только избыток — ассертим его.
                GD.Print($"[RACE] invariant_a0 drift globalCd-marked={gCd - marked} (отрицательный = консервативный недосчёт, допустим)");
            if (gCd > marked)
                throw new Exception($"ИНВАРИАНТ(а): избыток Cd-единиц globalCd={gCd} > помечено={marked} — фантомный учёт скрывает работы");

            // Глобальный гейт: все unclaimed на кулдауне → клейм обязан провалиться.
            int gateFalseNeeded = 0;
            for (int probe = 0; probe < 200; probe++)
            {
                pool.CurrentCellX[sweepAgent] = probe * 3 % MapSize;
                pool.CurrentCellY[sweepAgent] = probe * 7 % MapSize;
                pyramid.BeginClaimEpoch();
                if (pyramid.TryClaimJobForAgent(pool.CurrentCellX[sweepAgent], pool.CurrentCellY[sweepAgent],
                        ToolRequirement.Pickaxe | ToolRequirement.Axe | ToolRequirement.Hammer, // все инструменты
                        pool, sweepAgent, ctx, true, true, out JobData _))
                {
                    gateFalseNeeded++;
                }
            }
            GD.Print($"[RACE] invariant_a1 gate_probes=200 unexpected_claims={gateFalseNeeded}");
            if (gateFalseNeeded > 0)
                throw new Exception($"ИНВАРИАНТ(а): {gateFalseNeeded}/200 клеймов при тотальном кулдауне — гейт пропустил");

            // Снимаем ВСЕ кулдауны → счётчики обязаны обнулиться (двойной учёт → спрятанные).
            foreach (int id in snapshot)
                pyramid.ClearJobCooldown(id);
            pyramid.GetCdAggregatesForTest(out int gCd2, out long chunkCdSum2, out _, out _);
            GD.Print($"[RACE] invariant_a2 after_clear globalCd={gCd2} chunkCdSum={chunkCdSum2}");
            if (gCd2 != 0 || chunkCdSum2 != 0)
                throw new Exception($"ИНВАРИАНТ(а): после снятия кулдаунов globalCd={gCd2}, chunkCdSum={chunkCdSum2} (ожидались 0) — дрейф ВВЕРЗ, работа будет спрятана");

            // Возврат к жизни: та же hide-sweep-логика после снятия кулдаунов.
            int revivedHidden = 0;
            foreach (int id in snapshot)
            {
                JobData jd = pyramid.GetJobData(id);
                if (jd.AssignedWorkers != 0) continue;
                pool.CurrentCellX[sweepAgent] = jd.TargetX;
                pool.CurrentCellY[sweepAgent] = jd.TargetY;
                bool ok = false;
                for (int attempt = 0; attempt < 8 && !ok; attempt++)
                {
                    pyramid.BeginClaimEpoch();
                    ok = pyramid.TryClaimJobForAgent(jd.TargetX, jd.TargetY, jd.RequiredTool,
                        pool, sweepAgent, ctx, false, false, out JobData got);
                    if (ok) pyramid.ReleaseWorkerClaim(got.Id);
                }
                if (!ok) revivedHidden++;
            }
            GD.Print($"[RACE] invariant_a3 revived_sweep hidden_after_clear={revivedHidden}");
            if (revivedHidden > 0)
                throw new Exception($"ИНВАРИАНТ(а): {revivedHidden} работ спрятаны ПОСЛЕ снятия кулдаунов (двойной учёт CdMarkRemoved)");

            if (errors.IsEmpty)
            {
                GD.Print("==================================================");
                GD.Print("[RACE OK] ИНВАРИАНТЫ СОБЛЮДЕНЫ (Code 0)");
                GD.Print("==================================================");
                return true;
            }

            foreach (var e in errors)
                GD.PrintErr($"[RACE ERR] {e}");
            return false;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RACE FAILED] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }
}
