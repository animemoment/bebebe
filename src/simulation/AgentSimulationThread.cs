using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const int SnapshotRingSize = 6;

    private volatile bool _running;
    public volatile bool IsPaused;

    private volatile float _speedMultiplier = 1.0f;
    private volatile bool _speedResetRequested;
    private Task _simulationTask;
    private uint _tickCounter;
    private float _dispatchTimer;

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
    private const float DispatchInterval = 0.25f;

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
                float posX = tx * 64f + 32f;
                float posY = ty * 64f + 32f;

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
                int maxAllowedSteps = _speedMultiplier >= 25f ? 25 : 8;
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

                            CropGrowthManager.Instance.UpdateGrowth(currentStepDt, _ctx);

                            if (_dispatchTimer >= DispatchInterval)
                            {
                                _dispatchTimer = 0f;
                                JobDispatcher.Instance.DispatchPendingJobs(_pool, _ctx);
                            }

                            Phase2_ParallelUpdate(currentStepDt);
                            Phase3_Commit(currentStepDt);
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

                Thread.Sleep(_speedMultiplier <= 1.0f ? 6 : 1);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[AgentSimulationThread] Ошибка тика: {ex.Message}\n{ex.StackTrace}");
                Thread.Sleep(20);
            }
        }
    }

    private static float GetSimStepDelta(float speed)
    {
        if (speed >= 100f) return 0.15f;
        if (speed >= 25f)  return 0.08f;
        return BaseFixedDeltaTime;
    }

    private void Phase2_ParallelUpdate(float deltaTime)
    {
        using (GameProfiler.Scope())
        {
            int count = _pool.Capacity;

            Parallel.For(0, count, i =>
            {
                var state = _pool.States[i];
                if (state == AgentState.Idle || state == AgentState.Evacuating)
                {
                    _wanderSystem.ExecuteParallel(i, deltaTime, _pool, _ctx);
                    return;
                }

                var jobType = _pool.CurrentJobType[i];
                if (jobType != JobTypeId.None && JobRegistry.TryGetHandler(jobType, out var handler))
                {
                    handler.ExecuteParallel(i, deltaTime, _pool, _ctx);
                }
            });
        }
    }

    private void Phase3_Commit(float deltaTime)
    {
        using (GameProfiler.Scope())
        {
            _ctx.SpatialGrid.Clear();
            int count = _pool.Capacity;

            for (int i = 0; i < count; i++)
            {
                int cx = (int)(_pool.PositionX[i] / _ctx.TileSize);
                int cy = (int)(_pool.PositionY[i] / _ctx.TileSize);

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

                _ctx.SpatialGrid.Insert(i, cx, cy, _pool);

                var state = _pool.States[i];
                if (state == AgentState.Idle)
                {
                    _pool.JobSearchTimer[i] -= deltaTime;
                    if (_pool.JobSearchTimer[i] <= 0f)
                    {
                        _pool.JobSearchTimer[i] = 6.0f + (float)_ctx.Random.NextDouble() * 6.0f;
                        _wanderSystem.TryAssignJob(i, _pool, _ctx);
                    }
                    _wanderSystem.Commit(i, deltaTime, _pool, _ctx);
                    continue;
                }
                else if (state == AgentState.Evacuating)
                {
                    _wanderSystem.Commit(i, deltaTime, _pool, _ctx);
                    continue;
                }

                var jobType = _pool.CurrentJobType[i];
                if (jobType != JobTypeId.None && JobRegistry.TryGetHandler(jobType, out var handler))
                {
                    handler.Commit(i, deltaTime, _pool, _ctx);
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