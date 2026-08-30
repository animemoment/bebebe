using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Core;
using Game.Simulation.Jobs;
using Vector2 = System.Numerics.Vector2;

namespace Game.Simulation;

public sealed class AgentSimulationThread : IDisposable
{
    private AgentDataPool _pool;
    private SimulationContext _ctx;
    private readonly WanderJobSystem _wanderSystem = new();

    public ConcurrentQueue<Vector2[]> PositionQueue { get; } = new();

    private Vector2[][] _snapshotRing;
    private int _snapshotRingIndex;
    private const int SnapshotRingSize = 16;

    private volatile bool _running;
    public volatile bool IsPaused;

    private volatile float _speedMultiplier = 1.0f;
    private volatile bool _speedResetRequested;
    private Task _simulationTask;
    private uint _tickCounter;
    private float _dispatchTimer;
    private float _cropGrowthTimer;

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
    private const float MinSnapInterval = 1.0f / 60.0f;

    // Интервалы диспетчеризации и роста культур измеряются в ИГРОВОМ времени,
    // поэтому частота вызовов в реальном времени = speed / interval и без
    // масштабирования растёт линейно со скоростью (на 100x диспетчер вызывался
    // бы ~333 раза/с, рост культур — ~167 раза/с). Масштаб speed/IntervalScaleSpeed
    // ограничивает частоту сверху (~56 вызовов/с для диспетчера, ~30 для культур).
    private const float DispatchIntervalBase = 0.25f;
    private const float CropGrowthIntervalBase = 0.5f;
    private const float IntervalScaleSpeed = 16f;
    private const int ParallelThreshold = 128;
    private const int ChunkSize = 4096;

    public void Start(int agentCount, TileType[,] ground, bool[,] treeOnGrass, int seed = 0)
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

            _ctx = new SimulationContext(ground, treeOnGrass, solidWalls, walkableTiles, spatialGrid, movement, random);

            _snapshotRing = new Vector2[SnapshotRingSize][];
            for (int r = 0; r < SnapshotRingSize; r++)
            {
                _snapshotRing[r] = new Vector2[agentCount];
            }

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

                if (realDelta > 0.1f) realDelta = 0.1f;
                accumulator += realDelta * _speedMultiplier;

                float currentStepDt = GetSimStepDelta(_speedMultiplier);
                float dispatchInterval = GetScaledInterval(DispatchIntervalBase, _speedMultiplier);
                float cropGrowthInterval = GetScaledInterval(CropGrowthIntervalBase, _speedMultiplier);
                int maxAllowedSteps = _speedMultiplier >= 100f ? 6 : 8;
                int steps = (int)(accumulator / currentStepDt);

                if (steps > maxAllowedSteps)
                {
                    steps = maxAllowedSteps;
                    accumulator = 0f;
                }
                else
                {
                    accumulator -= steps * currentStepDt;
                }

                if (steps > 0)
                {
                    using (GameProfiler.ScopeCustom("Simulation.TotalStepCycle"))
                    {
                        for (int step = 0; step < steps; step++)
                        {
                            _tickCounter++;
                            _dispatchTimer += currentStepDt;
                            _cropGrowthTimer += currentStepDt;

                            if (_cropGrowthTimer >= cropGrowthInterval)
                            {
                                CropGrowthManager.Instance.UpdateGrowth(_cropGrowthTimer, _ctx);
                                _cropGrowthTimer = 0f;
                            }

                            if (_dispatchTimer >= dispatchInterval)
                            {
                                _dispatchTimer = 0f;
                                int dispatchScale = (int)Math.Ceiling(Math.Max(1f, _speedMultiplier / IntervalScaleSpeed));
                                JobDispatcher.Instance.DispatchPendingJobs(_pool, _ctx, dispatchScale);
                            }

                            bool isLastSubStep = (step == steps - 1);
                            Phase2_ParallelUpdate(currentStepDt);
                            Phase3a_ParallelBookkeeping(currentStepDt, isLastSubStep);
                            Phase3b_SequentialCommit(currentStepDt, isLastSubStep);
                        }

                        if (renderTimer.Elapsed.TotalSeconds >= MinSnapInterval)
                        {
                            renderTimer.Restart();
                            GroundItemManager.Instance.GenerateSnapshot();
                            CropGrowthManager.Instance.GenerateSnapshot();
                            PushSnapshot();
                        }
                    }
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
        if (speed >= 100f) return 0.30f;
        if (speed >= 25f)  return 0.15f;
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

            if (count < ParallelThreshold)
            {
                for (int i = 0; i < count; i++)
                {
                    UpdateSingleAgent(i, deltaTime, tickBucket);
                }
            }
            else
            {
                var partitioner = Partitioner.Create(0, count, ChunkSize);
                Parallel.ForEach(partitioner,
                    new ParallelOptions { MaxDegreeOfParallelism = System.Environment.ProcessorCount },
                    range =>
                    {
                        for (int i = range.Item1; i < range.Item2; i++)
                        {
                            UpdateSingleAgent(i, deltaTime, tickBucket);
                        }
                    });
            }
        }
    }

    private void Phase3a_ParallelBookkeeping(float deltaTime, bool rebuildSpatialGrid)
    {
        using (GameProfiler.Scope())
        {
            int count = _pool.Capacity;
            uint tickBucket = _tickCounter & 3;

            if (count < ParallelThreshold)
            {
                for (int i = 0; i < count; i++)
                {
                    BookkeepSingleAgent(i, deltaTime, tickBucket);
                }
            }
            else
            {
                var partitioner = Partitioner.Create(0, count, ChunkSize);
                Parallel.ForEach(partitioner,
                    new ParallelOptions { MaxDegreeOfParallelism = System.Environment.ProcessorCount },
                    range =>
                    {
                        for (int i = range.Item1; i < range.Item2; i++)
                        {
                            BookkeepSingleAgent(i, deltaTime, tickBucket);
                        }
                    });
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BookkeepSingleAgent(int i, float deltaTime, uint tickBucket)
    {
        int cx = (int)_pool.PositionX[i] >> 6;
        int cy = (int)_pool.PositionY[i] >> 6;

        if (_pool.CurrentCellX[i] == cx && _pool.CurrentCellY[i] == cy)
        {
            _pool.CellStayTime[i] += deltaTime;
        }
        else
        {
            _pool.CurrentCellX[i] = cx;
            _pool.CurrentCellY[i] = cy;
            _pool.CellStayTime[i] = 0f;
        }

        var state = _pool.States[i];
        if (state == AgentState.Idle)
        {
            // Тайм-слайсинг таймеров поиска работы (потокобезопасный Random.Shared)
            if ((i & 3) == tickBucket)
            {
                _pool.JobSearchTimer[i] -= deltaTime * 4.0f;
                if (_pool.JobSearchTimer[i] <= 0f)
                {
                    _pool.JobSearchTimer[i] = 6.0f + (float)Random.Shared.NextDouble() * 6.0f;
                    _wanderSystem.TryAssignJob(i, _pool, _ctx);
                }
            }
            return;
        }

        if (state == AgentState.Evacuating)
        {
            _wanderSystem.Commit(i, deltaTime, _pool, _ctx);
            return;
        }
    }

    private void Phase3b_SequentialCommit(float deltaTime, bool rebuildSpatialGrid)
    {
        using (GameProfiler.Scope())
        {
            if (rebuildSpatialGrid)
            {
                _ctx.SpatialGrid.Clear();
            }

            int count = _pool.Capacity;

            for (int i = 0; i < count; i++)
            {
                if (rebuildSpatialGrid)
                {
                    _ctx.SpatialGrid.Insert(i, _pool.CurrentCellX[i], _pool.CurrentCellY[i], _pool);
                }

                var state = _pool.States[i];
                if (state == AgentState.Idle || state == AgentState.Evacuating)
                    continue;

                var jobType = _pool.CurrentJobType[i];
                if (jobType != JobTypeId.None)
                {
                    var handler = JobRegistry.GetHandler(jobType);
                    handler?.Commit(i, deltaTime, _pool, _ctx);
                }
            }
        }
    }

    private void PushSnapshot()
    {
        var snapshot = _snapshotRing[_snapshotRingIndex];
        _pool.CopyPositionsTo(snapshot);
        _snapshotRingIndex = (_snapshotRingIndex + 1) % SnapshotRingSize;

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
    }

    public void Dispose()
    {
        Stop();
    }
}