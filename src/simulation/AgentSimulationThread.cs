using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Core;
using Game.Simulation.Gpu;
using Game.Simulation.Jobs;
using Game.Simulation.Scheduling;
using Game.Simulation.Simd;
using Vector2 = System.Numerics.Vector2;

namespace Game.Simulation;

public sealed class AgentSimulationThread : IDisposable
{
    private AgentDataPool _pool;
    private SimulationContext _ctx;
    private readonly WanderJobSystem _wanderSystem = new();
    private readonly NeedsJobSystem _needsSystem = new();

    public ConcurrentQueue<Vector2[]> PositionQueue { get; } = new();

    private volatile bool _running;
    public volatile bool IsPaused;

    private volatile float _speedMultiplier = 1.0f;
    private volatile bool _speedResetRequested;
    private Task _simulationTask;
    private uint _tickCounter;
    // FIX круг-2 №9: старые значения MinThreads для restore в Stop().
    // Без сохранения глобальный SetMinThreads тёк наружу (меняли пул навсегда).
    private int _prevMinWorkerThreads;
    private int _prevMinCompletionThreads;
    private bool _minThreadsAdjusted;    private float _dispatchTimer;
    private float _cropGrowthTimer;
    // Медленная влага почвы: тик раз в 30с игрового времени.
    private float _humidityTimer;
    private float _stockpileSweepTimer;
    // Аудит работ (JobValidator): тикает по РЕАЛЬНОМУ времени, не игровому —
    // иначе на паузе/1x проверка стояла бы, а на 100x долбила бы каждый тик.
    private float _jobAuditRealTimer;
    // Таймер редкого обновления окружения (потребности): растёт в игровом времени,
    // сбрасывается раз в AgentNeedsConfig.EnvironmentUpdatePeriodGameSec (= 1 игровой час).
    private float _needsEnvTimer;
    // Таймер обновления целей GPU-flowfield: растёт в игровом времени.
    // SetTargets строит битмаску W*H (262k uint = 1MB при 512x512) и всегда
    // инвалидирует поле → пересчёт 128 диспатчей. Поэтому зовём SetTargets
    // не чаще 1 раза в 10с игрового времени (троттлинг 2с внутри TryCompute
    // сглаживает сам пересчёт). Переиспользуемые буферы без аллокаций в тике.
    private float _flowTargetTimer;
    private const float FlowTargetIntervalGameSec = 10.0f;
    // Нижняя граница по wall-clock для flowfield-блока (SetTargets + TryCompute):
    // на 100x один проход = 8 игросек (16 шагов × dt 0.5), поэтому игровой троттлинг
    // (10с целей / 2с пересчёта) проходил бы КАЖДЫЙ кадр → полный пересчёт
    // (128 диспатчей + Sync-readback 2МБ) + SetTargets-инвалидация (маска 1МБ).
    // Паттерн как GlobalPassMinInterval в JobDispatcher: оба условия должны пройти
    // (gameOk && wallOk). На паузе wall идёт, игра стоит → gameOk false, лишних
    // пересчётов нет.
    private long _flowWallTicks;
    private const int FlowTargetMaxCells = 512;
    private readonly List<int> _flowJobIds = new(FlowTargetMaxCells);
    private readonly List<(int X, int Y)> _flowCells = new(FlowTargetMaxCells);

    /// <summary>
    /// Накопленное мировое игровое время (секунды) — авторитет «времени в мире».
    /// Растёт только при реальном исполнении шагов симуляции (пауза останавливает его).
    /// </summary>
    public volatile float GameTimeSeconds;

    public float SpeedMultiplier
    {
        get => _speedMultiplier;
        set
        {
            _speedMultiplier = value;
            _speedResetRequested = true;
        }
    }

    private const float BaseFixedDeltaTime = 0.05f;
    private const float MinSnapInterval = 1.0f / 30.0f;

    // Интервалы диспетчеризации и роста культур измеряются в ИГРОВОМ времени,
    // поэтому частота вызовов в реальном времени = speed / interval и без
    // масштабирования растёт линейно со скоростью (на 100x диспетчер вызывался
    // бы ~333 раза/с, рост культур — ~167 раза/с). Масштаб speed/IntervalScaleSpeed
    // ограничивает частоту сверху (~56 вызовов/с для диспетчера, ~30 для культур).
    private const float DispatchIntervalBase = 0.25f;
    private const float CropGrowthIntervalBase = 0.5f;
    private const float IntervalScaleSpeed = 16f;
    private const int ParallelThreshold = 128;
    // P-динамика: статический ChunkSize=1024 убран — фазы идут через
    // DynamicWorkBalancer (батчи 32..512 + work-stealing через атомарный курсор).
    // Тяжёлый агент задерживает батч 128, а не чанк 1024: straggler-хвост короче в ~8x.

    public void Start(int agentCount, TileType[,] ground, bool[,] treeOnGrass, int seed = 0, HumidityMap humidity = null)
    {
        try
        {
            int width = ground.GetLength(0);
            int height = ground.GetLength(1);
            bool[,] solidWalls = new bool[width, height];

            var walkableTiles = new List<(int X, int Y)>(agentCount);
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (ground[x, y] == TileType.Grass && !treeOnGrass[x, y])
                    {
                        walkableTiles.Add((x, y));
                    }
                }
            }

            if (walkableTiles.Count == 0)
                throw new InvalidOperationException("Нет проходимых тайлов травы.");

            var random = new Random(seed == 0 ? System.Environment.TickCount : seed);
            _pool = new AgentDataPool(agentCount);

            var spatialGrid = new AgentSpatialGrid(width, height);
            var movement = new AgentMovementService();

            _ctx = new SimulationContext(ground, humidity, treeOnGrass, solidWalls, walkableTiles, spatialGrid, movement, random);
            HierarchicalPathfinder.Instance.Initialize(_ctx);

            for (int i = 0; i < agentCount; i++)
            {
                int tileIndex = random.Next(walkableTiles.Count);
                var (tx, ty) = walkableTiles[tileIndex];
                float posX = (tx << 6) + 32f;
                float posY = (ty << 6) + 32f;

                _pool.PositionX[i] = posX;
                _pool.PositionY[i] = posY;
                _pool.LastPositionX[i] = posX;
                _pool.LastPositionY[i] = posY;
                _pool.TargetPositionX[i] = posX;
                _pool.TargetPositionY[i] = posY;
                _pool.CurrentCellX[i] = tx;
                _pool.CurrentCellY[i] = ty;
                _pool.States[i] = AgentState.Idle;
                _pool.JobSearchTimer[i] = 4.0f + (float)random.NextDouble() * 6.0f;
                // P1: джиттер голода/сна при спавне (±20): иначе все 10k пересекают порог
                // Hunger>70 в один тик 16:48 и устраивают thundering herd сканов еды.
                _pool.Hunger[i] = (float)random.NextDouble() * 20.0f;
                _pool.Sleep[i] = (float)random.NextDouble() * 20.0f;

                JobDispatcher.Instance.IdleWorkers.AddIdleWorker(i, _pool);
            }

            JobRegistry.Register(new TreeChoppingJobHandler());
            JobRegistry.Register(new ConstructionJobHandler());
            JobRegistry.Register(new FarmingJobHandler());
            JobRegistry.Register(new BlueprintDeliveryJobHandler());
            JobRegistry.Register(new StockpileHaulingJobHandler());
            JobRegistry.Register(new PlantingJobHandler());
            JobRegistry.Register(new HarvestJobHandler());

            PushSnapshot();

            // FIX круг-2 №9: сохранить старые значения, проверить bool SetMinThreads,
            // восстанавливать в Stop(). Без restore меняли глобальный пул навсегда.
            int dop = System.Environment.ProcessorCount;
            if (dop < 1) dop = 1;
            ThreadPool.GetMinThreads(out _prevMinWorkerThreads, out _prevMinCompletionThreads);
            if (ThreadPool.SetMinThreads(dop, dop))
                _minThreadsAdjusted = true;

            _running = true;
            _simulationTask = Task.Run(SimulationLoop);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AgentSimulationThread] Ошибка старта симуляции: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void SimulationLoop()
    {
        var sw = new Stopwatch();
        var renderTimer = new Stopwatch();
        float accumulator = 0f;
        sw.Start();
        renderTimer.Start();

        while (_running)
        {
            try
            {
                if (_speedResetRequested)
                {
                    accumulator = 0f;
                    _speedResetRequested = false;
                    // Профиль нагрузки сменился (другой шаг/частота фаз) —
                    // сбрасываем EMA размеров батчей балансировщика.
                    DynamicWorkBalancer.Reset();
                    sw.Restart();
                }

                if (IsPaused)
                {
                    Thread.Sleep(20);
                    sw.Restart();
                    continue;
                }

                float realDelta = (float)sw.Elapsed.TotalSeconds;
                sw.Restart();

                if (realDelta > 0.25f) realDelta = 0.25f; // Clamp 0.25с: при долгих итерациях Phase2 учитываем реальное время честнее; больше — риск спирали смерти (нагрузка растёт лавинообразно).
                accumulator += realDelta * _speedMultiplier;

                float currentStepDt = GetSimStepDelta(_speedMultiplier);
                float dispatchInterval = GetScaledInterval(DispatchIntervalBase, _speedMultiplier);
                float cropGrowthInterval = GetScaledInterval(CropGrowthIntervalBase, _speedMultiplier);
                int maxAllowedSteps = _speedMultiplier >= 100f ? 16 : 10; // Лимит шагов за проход: 16 на 100x (пропускная способность), иначе 10; больше — дольше кадр и риск спирали смерти.
                int steps = (int)(accumulator / currentStepDt);

                if (steps > maxAllowedSteps)
                {
                    steps = maxAllowedSteps;
                    // Переносим ограниченный остаток (не более одного полного прохода backlog), чтобы лишнее время не сгорало, но спираль смерти не разгонялась.
                    accumulator = Math.Min(accumulator - steps * currentStepDt, currentStepDt * maxAllowedSteps);
                }
                else
                {
                    accumulator -= steps * currentStepDt;
                }

                if (steps > 0)
                {
                    // Буфер JobAudit-печати: GD.Print из горячего пути убран —
                    // копим за цикл шагов, печатаем один раз из sim-потока после фаз.
                    int jobAuditFixedTotal = 0;
                    // FIX круг-2 №6: сброс тикового аккумулятора баланса в начале тика —
                    // Publish планировщика аккумулирует sub-steps, overlay видит сумму.
                    DynamicWorkScheduler.ResetTickBalance("Simulation.Phase2_Balance");
                    DynamicWorkScheduler.ResetTickBalance("Simulation.Phase3a_Balance");
                    DynamicWorkScheduler.ResetTickBalance("Simulation.Phase3b_Balance");
                    DynamicWorkScheduler.ResetTickBalance("Dispatcher.Balance");
                    using (GameProfiler.ScopeCustom("Simulation.TotalStepCycle"))
                    {
                        for (int step = 0; step < steps; step++)
                        {
                            _tickCounter++;
                            _dispatchTimer += currentStepDt;
                            _cropGrowthTimer += currentStepDt;
                            _stockpileSweepTimer += currentStepDt;
                            _needsEnvTimer += currentStepDt;
                            // Мировое игровое время: каждый шаг = квант игрового времени.
                            GameTimeSeconds += currentStepDt;

                            // Редкое обновление окружения (потребности): раз в игровой час.
                            bool updateNeedsEnvThisStep = false;
                            if (_needsEnvTimer >= AgentNeedsConfig.EnvironmentUpdatePeriodGameSec)
                            {
                                _needsEnvTimer = 0f;
                                updateNeedsEnvThisStep = true;
                            }

                            if (_cropGrowthTimer >= cropGrowthInterval)
                            {
                                // GPU-влажность почвы: троттлинг 5с игрового времени внутри Tick,
                                // CPU-источник истины (стадии/таймеры) не трогается — только бонус роста.
                                GpuCropField.Instance.Tick(GameTimeSeconds, _ctx);
                                CropGrowthManager.Instance.UpdateGrowth(_cropGrowthTimer, _ctx);
                                _cropGrowthTimer = 0f;
                            }

                            // Почвенная влага: медленный тик раз в 30с игрового.
                            // Цифры дрейфуют на единицы, у воды держится 150+.
                            // Размеры кэшируем до лямбд (GetLength в Parallel.For
                            // 262k раз — лишний вызов; GetLength(0/1) не free).
                            // Маску грядок снимаем ОДИН раз до тика: IsGardenBed
                            // берёт lock на КАЖДУЮ клетку — 262k lock'ов в тике.
                            _humidityTimer += currentStepDt;
                            if (_humidityTimer >= HumidityMap.TickIntervalGameSec && _ctx?.Humidity != null)
                            {
                                _humidityTimer = 0f;
                                var humidityMap = _ctx.Humidity;
                                var ground = _ctx.Ground;
                                var trees = _ctx.TreeOnGrass;
                                int gw = ground.GetLength(0);
                                int gh = ground.GetLength(1);
                                int tw = trees.GetLength(0);
                                int th = trees.GetLength(1);
                                bool[,] farmMask = FarmJobManager.Instance.BuildGardenBedMask(gw, gh);
                                int mw = humidityMap.Width;
                                int mh = humidityMap.Height;
                                humidityMap.Tick(
                                    ground,
                                    (x, y) => (uint)x < (uint)gw && (uint)y < (uint)gh && ground[x, y] == TileType.Mountain,
                                    (x, y) => (uint)x < (uint)tw && (uint)y < (uint)th && trees[x, y],
                                    (x, y) => (uint)x < (uint)mw && (uint)y < (uint)mh && farmMask[x, y]);
                            }

                            if (_dispatchTimer >= dispatchInterval)
                            {
                                _dispatchTimer = 0f;
                                int dispatchScale = (int)Math.Ceiling(Math.Max(1f, _speedMultiplier / IntervalScaleSpeed));
                                JobDispatcher.Instance.DispatchPendingJobs(_pool, _ctx, dispatchScale);
                                // Цели GPU-flowfield: не чаще 1 раза в 10с игрового времени
                                // (SetTargets строит битмаску 1MB + всегда инвалидирует поле;
                                // чаще — 4MB/s мусора + пересчёт 128 диспатчей каждые 0.25с).
                                // Плюс нижняя граница 2 РЕАЛЬНЫЕ секунды (см. поле _flowWallTicks):
                                // на 100x один проход = 8 игросек, игровой таймер проходил бы
                                // каждый кадр → полный пересчёт + Sync-readback 2МБ каждый кадр.
                                // Оба условия должны пройти: gameOk && wallOk. Единый wall-гейт
                                // на весь блок: SetTargets инвалидирует снапшот, TryCompute тут же
                                // в этом же проходе считает заново (блокировать его вторым
                                // гейтом нельзя — иначе поле останется протухшим).
                                _flowTargetTimer += dispatchInterval;
                                {
                                    long flowNow = DateTime.UtcNow.Ticks;
                                    // 2с в тиках = 20_000_000 (TimeSpan.TicksPerSecond * 2).
                                    bool wallOk = (flowNow - _flowWallTicks) >= 20000000L;
                                    if (wallOk)
                                    {
                                        if (_flowTargetTimer >= FlowTargetIntervalGameSec)
                                        {
                                            _flowTargetTimer = 0f;
                                            RefreshFlowFieldTargets();
                                        }
                                        // Пересчёт поля: троттлинг 2с игрового времени внутри,
                                        // ранний выход если свежо. Sim-поток, local RD — ок.
                                        GpuFlowField.Instance.TryCompute(_ctx, GameTimeSeconds);
                                        _flowWallTicks = flowNow;
                                    }
                                }
                            }

                            // Плановый «подметальный» проход: гарантирует, что
                            // для каждого лежащего на земле предмета есть haul-работа.
                            // (Склад может быть нарисован ПОСЛЕ выпадения предметов.)
                            if (_stockpileSweepTimer >= 1.0f)
                            {
                                _stockpileSweepTimer = 0f;
                                JobBroker.Instance.SweepStockpileHaulJobs();
                            }

                            // Аудит работ по РЕАЛЬНОМУ времени: раз в 30с порциями
                            // (только marked-клетки, не вся карта). На паузе не тикает.
                            _jobAuditRealTimer += realDelta;
                            if (_jobAuditRealTimer >= JobValidator.AuditIntervalRealSec)
                            {
                                _jobAuditRealTimer = 0f;
                                using (GameProfiler.ScopeCustom("Simulation.JobAudit"))
                                {
                                    jobAuditFixedTotal += JobValidator.Instance.Tick(_pool, _ctx);
                                }
                            }

                            bool isLastSubStep = (step == steps - 1);
                            Phase2_ParallelUpdate(currentStepDt);
                            Phase3a_ParallelBookkeeping(currentStepDt, isLastSubStep, updateNeedsEnvThisStep);
                            Phase3b_SequentialCommit(currentStepDt, isLastSubStep);
                        }

                        if (renderTimer.Elapsed.TotalSeconds >= MinSnapInterval)
                        {
                            renderTimer.Restart();
                            GroundItemManager.Instance.GenerateSnapshot();
                            StockpileManager.Instance.GenerateSnapshot();
                            CropGrowthManager.Instance.GenerateSnapshot();
                            PushSnapshot();
                            // GPU-редукция статистики: троттлинг 2с wall-clock внутри Tick,
                            // sim-поток, _pool доступен. HUD только читает Last.
                            GpuStatsReduce.Instance.Tick(_pool);
                        }

                        // П.3: троттлинг событий склада — сливаем накопленные тоталы не чаще 200мс
                        // (иначе 1000 Deposit/Withdraw за тик = 1000 CallDeferred в главный поток).
                        StockpileManager.Instance.TickEventThrottle(currentStepDt);
                    }

                    if (jobAuditFixedTotal > 0)
                        GD.Print($"[JobValidator] исправлено работ: {jobAuditFixedTotal}");
                }

                // Sleep(0) при speed >= 25 уступает квант только готовым потокам и
                // при простое превращается в busy-loop (100% одного ядра). На
                // «холодных» итерациях (steps == 0) спим 1 мс — темп симуляции при
                // этом самоподдерживается аккумулятором (realDelta * speed).
                if (_speedMultiplier >= 25f)
                {
                    Thread.Sleep(steps > 0 ? 0 : 1);
                }
                else
                {
                    Thread.Sleep(6);
                }
            }
            catch (AggregateException aggEx)
            {
                foreach (var inner in aggEx.Flatten().InnerExceptions)
                {
                    GD.PrintErr($"[AgentSimulationThread] Параллельная ошибка: {inner.Message}\n{inner.StackTrace}");
                }
                Thread.Sleep(20);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[AgentSimulationThread] Ошибка тика: {ex.Message}\n{ex.StackTrace}");
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>
    /// Масштабирует интервал (в игровом времени) под скорость, чтобы частота
    /// вызовов в реальном времени (speed / interval) не росла линейно со скоростью.
    /// При speed &lt;= IntervalScaleSpeed возвращает базовый интервал без изменений.
    /// </summary>
    private static float GetScaledInterval(float baseInterval, float speed)
        => baseInterval * Math.Max(1f, speed / IntervalScaleSpeed);

    private static float GetSimStepDelta(float speed)
    {
        // Крупный шаг на высокой скорости: грубее движение и риск туннелирования при большом dt (коллизии/путь могут проскакивать), зато пропускная способность выше.
        if (speed >= 100f) return 0.50f; // 100x: dt=0.5 — движение грубое, возможны рывки и туннелирование, но 16*0.5=8.0 игросек за проход.
        if (speed >= 25f)  return 0.20f; // 25x+: dt=0.2 — умеренное огрубление движения ради скорости.
        return BaseFixedDeltaTime;
    }

    private void UpdateSingleAgent(int i, float deltaTime, uint tickBucket)
    {
        var state = _pool.States[i];
        if (state == AgentState.Idle)
        {
            // Тайм-слайсинг: безработные обновляются батчами по 25% через битовую маску
            if ((i & 3) == tickBucket)
            {
                _wanderSystem.ExecuteParallel(i, deltaTime * 4.0f, _pool, _ctx);
            }
            return;
        }

        if (state == AgentState.Evacuating)
        {
            // Поведенческие реакции на потребности имеют приоритет над обычной эвакуацией.
            if (_pool.NeedsBehavior[i] != NeedBehavior.None)
                _needsSystem.ExecuteParallel(i, deltaTime, _pool, _ctx);
            else
                _wanderSystem.ExecuteParallel(i, deltaTime, _pool, _ctx);
            return;
        }

        var jobType = _pool.CurrentJobType[i];
        if (jobType != JobTypeId.None)
        {
            var handler = JobRegistry.GetHandler(jobType);
            handler?.ExecuteParallel(i, deltaTime, _pool, _ctx);
        }
    }

    private void Phase2_ParallelUpdate(float deltaTime)
    {
        using (GameProfiler.Scope())
        {
            int count = _pool.Capacity;
            uint tickBucket = _tickCounter & 3;

            // SCHEDULING RULE (PLAN.md §8.4): из body(i) запрещены ЛЮБЫЕ Godot Node API
            // (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
            // Разрешены: чистые вычисления, SoA-массивы, Interlocked/Volatile,
            // ConcurrentQueue.Enqueue (снапшоты), ThreadLocal, ParallelRng.
            if (count < ParallelThreshold)
            {
                int errors = 0;
                for (int i = 0; i < count; i++)
                {
                    try { UpdateSingleAgent(i, deltaTime, tickBucket); }
                    catch { errors++; }
                }
                if (errors > 0)
                    GD.PrintErr($"[Phase2] ошибок UpdateSingleAgent: {errors}");
            }
            else
            {
                int errors = 0;
                DynamicWorkScheduler.Shared.ForEachSplittable(
                    count,
                    (s, e, q) =>
                    {
                        // SCHEDULING RULE (PLAN.md §8.4): тело — только чистые
                        // вычисления + SoA-массивы; cached yield-check ПЕРЕД КАЖДЫМ
                        // агентом (FIX круг-2 №4): timestamp кэшируется каждые 8,
                        // но дедлайн проверяется каждый i — после 1 тяжёлого агента
                        // следующий i сразу yield'ит. Хвост ~1 тяжёлый агент + квант.
                        // FIX круг-2 №7: локальный счётчик — без CAS на исключение.
                        int localErrors = 0;
                        int p = 0;
                        long cached = 0;
                        int sinceRefresh = 0;
                        for (int i = s; i < e; i++)
                        {
                            if (q.ShouldYieldCached(ref cached, ref sinceRefresh))
                                break;
                            try { UpdateSingleAgent(i, deltaTime, tickBucket); }
                            catch { localErrors++; }
                            p++;
                            sinceRefresh++;
                        }
                        if (localErrors > 0)
                            Interlocked.Add(ref errors, localErrors);
                        return p;
                    },
                    WorkKind.Heavy,
                    "Simulation.Phase2_Balance");
                errors += DynamicWorkScheduler.Shared.LastPhaseErrorCount;
                if (errors > 0)
                    GD.PrintErr($"[Phase2] ошибок UpdateSingleAgent: {errors}");
            }
        }
    }

    private void Phase3a_ParallelBookkeeping(float deltaTime, bool rebuildSpatialGrid, bool updateNeedsEnv = false)
    {
        using (GameProfiler.Scope())
        {
            int count = _pool.Capacity;
            uint tickBucket = _tickCounter & 3;

            if (count < ParallelThreshold)
            {
                for (int i = 0; i < count; i++)
                {
                    BookkeepSingleAgent(i, deltaTime, tickBucket, updateNeedsEnv);
                }
            }
            else
            {
                // G3: два диапазонных батч-прохода через ForEachRange, затем
                // поэлементный остаток (Idle/Evac-ветки, НЕ батчится: lock/striped
                // UpdateWorkerChunk и тайм-слайсинг со сканами под lock).
                // (а) потребности (SIMD: Hunger/Sleep/Mood; Fatigue скаляр внутри),
                // (б) cell-tracking (скаляр, memory-bound — см. SimdNeedsBatch.UpdateCells).
                DynamicWorkBalancer.ForEachRange(count,
                    (s, e) => SimdNeedsBatch.UpdateNeeds(_pool, s, e, deltaTime, updateNeedsEnv),
                    "Simulation.Phase3a_Needs");
                int eNeeds = DynamicWorkScheduler.Shared.LastPhaseErrorCount;
                DynamicWorkBalancer.ForEachRange(count,
                    (s, e) => SimdNeedsBatch.UpdateCells(_pool, s, e, deltaTime),
                    "Simulation.Phase3a_Cells");
                int eCells = DynamicWorkScheduler.Shared.LastPhaseErrorCount;
                // G3: остаток БЕЗ Needs и БЕЗ записи cell-tracking (уже сделаны
                // батчами выше). cellChanged для Idle-ветки перевычисляется чтением
                // CellStayTime[i] == 0f в месте вызова (без записи — запись уже
                // сделана батчем (б)): CurrentCellX/Y уже равны новым cx/cy ИЛИ
                // остались старыми, поэтому прямое сравнение после батча всегда
                // даёт «совпало». Вместо него используется эвристика Stay == 0f
                // (батч (б) сбрасывает Stay в 0 строго при смене клетки; при deltaTime
                // > 0 ветка «совпало» даёт Stay > 0). При deltaTime == 0 эвристика
                // может дать ложное срабатывание — UpdateWorkerChunk идемпотентен
                // (no-op при том же чанке), регрессии поведения нет.
                DynamicWorkBalancer.ForEach(count,
                    i => BookkeepSingleAgentRest(i, deltaTime, tickBucket, _pool.CellStayTime[i] == 0f),
                    "Simulation.Phase3a_Balance");
                int eRest = DynamicWorkScheduler.Shared.LastPhaseErrorCount;
                int eTotal = eNeeds + eCells + eRest;
                if (eTotal > 0)
                    GD.PrintErr($"[Phase3a] ошибок Bookkeeping: {eTotal} (needs={eNeeds} cells={eCells} rest={eRest})");
            }
        }
    }

    /// <summary>
    /// Фаза обновления базовых потребностей (SoA-расширение).
    /// deltaTime — игровые секунды (скорость уже учтена через accumulator/шаги),
    /// отдельно на _speedMultiplier домножать НЕ нужно.
    /// Вызывается из BookkeepSingleAgent первой строкой, поэтому покрывает все состояния.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateNeedsSingleAgent(int i, float deltaTime, bool updateEnv)
    {
        // G2: поэлементный путь делегирован SimdNeedsBatch.UpdateNeedsSingle
        // (та же семантика, AggressiveInlining сохранён с обеих сторон).
        // G3: диапазонный SimdNeedsBatch.UpdateNeeds интегрирован в Phase3a
        // батчинг (ForEachRange-путь); здесь — поэлементный путь для
        // BookkeepSingleAgent (ветка count &lt; ParallelThreshold).
        SimdNeedsBatch.UpdateNeedsSingle(_pool, i, deltaTime, updateEnv);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BookkeepSingleAgent(int i, float deltaTime, uint tickBucket, bool updateNeedsEnv = false)
    {
        UpdateNeedsSingleAgent(i, deltaTime, updateNeedsEnv);

        // G3: cell-tracking делегирован SimdNeedsBatch.UpdateCellSingle
        // (та же семантика, возвращает cellChanged для Idle-ветки ниже).
        bool cellChanged = SimdNeedsBatch.UpdateCellSingle(_pool, i, deltaTime);

        BookkeepSingleAgentRest(i, deltaTime, tickBucket, cellChanged);
    }

    /// <summary>
    /// G3: остаток bookkeeping БЕЗ потребностей и БЕЗ записи cell-tracking —
    /// только Idle/Evacuating-ветки. В параллельном пути Phase3a вызывается после
    /// батчей UpdateNeeds/UpdateCells; Needs уже обновлены, CurrentCellX/Y и
    /// CellStayTime уже записаны батчем. cellChanged передаётся параметром:
    /// полный путь даёт его из UpdateCellSingle, параллельный — эвристикой
    /// CellStayTime[i] == 0f (см. комментарий в Phase3a_ParallelBookkeeping).
    /// Тайм-слайсинг `(i&amp;3)==tickBucket`, TryAssignNeedsBehavior (сканы под lock),
    /// TryAssignJob (ParallelRng) и UpdateWorkerChunk (lock/striped) — НЕ батчатся.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BookkeepSingleAgentRest(int i, float deltaTime, uint tickBucket, bool cellChanged)
    {
        var state = _pool.States[i];
        if (state == AgentState.Idle)
        {
            // Свободный агент переместился — обновляем его чанк в сетке бездельников,
            // чтобы локальный поиск работы видел актуальное положение.
            if (cellChanged)
            {
                JobDispatcher.Instance.IdleWorkers.UpdateWorkerChunk(i, _pool);
            }

            // Тайм-слайсинг таймеров поиска работы (потокобезопасный Random.Shared)
            if ((i & 3) == tickBucket)
            {
                // P0: backoff неудачного поиска еды (NeedsRetryTimer 30-60с) тикает первым:
                // 10k голодных без еды не должны вызывать TryAssign (и скан) каждые 4 тика.
                // Отдельный таймер — JobSearchTimer блуждания (6с) его не затирает.
                if (_pool.NeedsRetryTimer[i] > 0f)
                {
                    _pool.NeedsRetryTimer[i] -= deltaTime * 4.0f;
                }
                else
                {
                    // Сначала потребности: голодный/уставший свободный агент уходит
                    // удовлетворять нужду и НЕ берёт обычную работу в этом тике.
                    if (_needsSystem.TryAssignNeedsBehavior(i, _pool, _ctx))
                        return;
                }
                _pool.JobSearchTimer[i] -= deltaTime * 4.0f;
                if (_pool.JobSearchTimer[i] > 0f)
                    return;
                _pool.JobSearchTimer[i] = 6.0f + (float)ParallelRng.NextDouble() * 6.0f;
                _wanderSystem.TryAssignJob(i, _pool, _ctx);
            }
            return;
        }

        if (state == AgentState.Evacuating)
        {
            if (_pool.NeedsBehavior[i] != NeedBehavior.None)
                _needsSystem.Commit(i, _pool);
            else
                _wanderSystem.Commit(i, deltaTime, _pool, _ctx);
            return;
        }
    }

    private void Phase3b_SequentialCommit(float deltaTime, bool rebuildSpatialGrid)
    {
        using (GameProfiler.Scope())
        {
            int count = _pool.Capacity;

            // Rebuild сетки — строго последовательно (общие _cellHeads/_activeCells,
            // Parallel здесь дал бы гонку), но это дешёвый O(N) проход без логики.
            if (rebuildSpatialGrid)
            {
                _ctx.SpatialGrid.Clear();
                for (int i = 0; i < count; i++)
                    _ctx.SpatialGrid.Insert(i, _pool.CurrentCellX[i], _pool.CurrentCellY[i], _pool);
            }

            // Commit — дорого (TakeItems/SpawnItems/Release под lock'ами, один
            // залипший агент сталлил все 10k в одном потоке — Amdahl-стопор A).
            // Handler.Commit потокобезопасны (менеджеры под lock/CAS), записи SoA —
            // по непересекающимся индексам: гоним через балансировщик.
            if (count < ParallelThreshold)
            {
                for (int i = 0; i < count; i++)
                    CommitSingleAgent(i, deltaTime);
            }
            else
            {
                DynamicWorkBalancer.ForEach(count,
                    i => CommitSingleAgent(i, deltaTime),
                    "Simulation.Phase3b_Balance");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitSingleAgent(int i, float deltaTime)
    {
        var state = _pool.States[i];
        if (state == AgentState.Idle || state == AgentState.Evacuating)
            return;

        var jobType = _pool.CurrentJobType[i];
        if (jobType != JobTypeId.None)
        {
            var handler = JobRegistry.GetHandler(jobType);
            handler?.Commit(i, deltaTime, _pool, _ctx);
        }
    }

    // Сбор целей GPU-flowfield из JobIndex (существующий публичный API:
    // FillPrioritizedUnclaimed собирает unclaimed-id, TryGetJob отдаёт
    // JobData с координатами TargetX/TargetY). Кап 512 клеток, дедуп не нужен —
    // SetTargets сам дедуплицирует битмаской. Вызывается из sim-потока, буферы
    // переиспользуемые (без аллокаций в тике), TryGetJob lock-free на чтение.
    //
    // БАГ A (#3-пусто): если целей нет (UnclaimedCount<=0, список пуст или все
    // отфильтровались) — обязательно зовём SetTargets с пустым набором.
    // Пустой набор корректен по коду GpuFlowField.SetTargets: valid=0 даёт
    // _hasTargets=false + _snapshot=null → TryCompute вернёт false → агенты
    // идут старым путём (локальный BFS). Без этого старый снапшот вёл бы
    // к мёртвым (уже разобранным) целям.
    //
    // БАГ C (#3-resize): GenericJobSpatialIndex.TryGetJob читает _capacity/_active
    // и SoA-массивы без синхронизации, а Register делает Array.Resize под
    // _registerLock. Гонка «рост capacity во время чтения» даёт
    // IndexOutOfRangeException. Ловим его здесь и используем частичные цели
    // (или протухаем через пустой SetTargets) — безопасно. Lock в индекс НЕ
    // добавляем (риск контеншна/дедлока в горячем пути). Полное решение
    // (снапшот-изоляция индекса) — вне скоупа GPU-трека.
    private void RefreshFlowFieldTargets()
    {
        var index = JobDispatcher.Instance.JobIndex;
        int mapW = _ctx.MapWidth;
        int mapH = _ctx.MapHeight;
        _flowCells.Clear();
        try
        {
            if (index.UnclaimedCount <= 0)
            {
                // Чисто пусто: гасим старый снапшот, чтобы не вести к мёртвым целям.
                GpuFlowField.Instance.SetTargets(_flowCells, mapW, mapH);
                return;
            }
            _flowJobIds.Clear();
            index.FillPrioritizedUnclaimed(_flowJobIds);
            if (_flowJobIds.Count == 0)
            {
                GpuFlowField.Instance.SetTargets(_flowCells, mapW, mapH);
                return;
            }
            int n = Math.Min(_flowJobIds.Count, FlowTargetMaxCells);
            for (int i = 0; i < n; i++)
            {
                // Defensive-чтение: при гонке с Array.Resize бросает
                // IndexOutOfRangeException — выходим, используем частичный набор.
                try
                {
                    if (!index.TryGetJob(_flowJobIds[i], out var job))
                        continue;
                    int tx = job.TargetX;
                    int ty = job.TargetY;
                    if ((uint)tx < (uint)mapW && (uint)ty < (uint)mapH)
                        _flowCells.Add((tx, ty));
                }
                catch (IndexOutOfRangeException)
                {
                    break;
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            // Гонка с ростом capacity внутри FillPrioritizedUnclaimed/сортировки:
            // остаток пропускаем, ниже — частичные цели или протухание. Безопасно.
        }
        // Единый выход: пустой _flowCells корректен для SetTargets
        // (valid=0 → _hasTargets=false → TryCompute=false → локальный BFS),
        // Array.Empty в конце — чтобы явно не держать ссылку на переиспользуемый буфер.
        GpuFlowField.Instance.SetTargets(
            _flowCells.Count > 0 ? _flowCells : Array.Empty<(int, int)>(), mapW, mapH);
    }

    private void PushSnapshot()
    {
        // Снапшот — свежий массив: ring переиспользовался и продюсер перезаписывал
        // буфер, пока консьюмер (рендер) его читал — tearing позиций.
        // Аллокация 10k Vector2 ~80КБ на 30Гц — приемлемо ради корректности.
        var snapshot = new Vector2[_pool.Capacity];
        _pool.CopyPositionsTo(snapshot);

        PositionQueue.Enqueue(snapshot);
        while (PositionQueue.Count > 2)
            PositionQueue.TryDequeue(out _);
    }

    public void Stop()
    {
        _running = false;
        try
        {
            _simulationTask?.Wait(500);
        }
        catch { }
        // FIX круг-2 №9: восстановить MinThreads, изменённые в Start().
        if (_minThreadsAdjusted)
        {
            try { ThreadPool.SetMinThreads(_prevMinWorkerThreads, _prevMinCompletionThreads); }
            catch { }
            _minThreadsAdjusted = false;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}