using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Иерархическая пирамида плотности задач (Hierarchical Spatial Work Matcher).
/// Трехуровневая структура:
/// - L0: Битовая маска и SoA хранилище задач.
/// - L1: Секторы 16x16 тайлов (1024 сектора на 512x512) с O(1) swap-and-pop списками.
/// - L2: 64-битная макро-маска (8x8 макро-секторов 64x64 тайла) с tzcnt поиском за 1 такт CPU.
/// Обеспечивает мгновенный подбор работы на любом расстоянии без радиусных ограничений и очередей.
/// </summary>
public sealed class JobSpatialPyramid
{
    public const int ChunkShift = 4; // 16x16 тайлов на чанк/сектор
    public const int ChunkDim = 32;   // 32x32 чанка = 512x512 тайлов
    public const int ChunkCount = ChunkDim * ChunkDim;
    public const int ChunkGridDim = ChunkDim;

    public const int MacroShift = 6;  // 64x64 тайла на макро-сектор (4x4 чанка)
    public const int MacroDim = 8;    // 8x8 макро-секторов
    public const int MacroCount = MacroDim * MacroDim; // 64 макро-сектора = ровно 1 ulong

    private const int InitialCapacity = 32768;
    private const int GrowFactor = 2;

    private readonly object _registerLock = new();

    // SoA — основные поля задачи (индекс = jobId)
    private int[] _targetX;
    private int[] _targetY;
    private int[] _standX;
    private int[] _standY;
    private int[] _sourceX;
    private int[] _sourceY;
    private JobTypeId[] _typeId;
    private JobExecutionType[] _executionType;
    private JobPriorityTier[] _priorityTier;
    private ToolRequirement[] _requiredTool;
    private int[] _maxWorkers;
    private int[] _assignedWorkers;       // CAS-цель
    private int[] _jobFailCd;             // кулдаун недостижимости
    private int[] _targetItemCount;
    private int[] _currentDeliveredCount;
    private ItemId[] _targetItemId;
    private float[] _workDuration;
    private bool[] _active;

    // Индекс позиции внутри чанка (для O(1) удаления из чанка)
    private int[] _chunkJobIndex;

    // Free-list для переиспользования jobId
    private int[] _nextFree;
    private int _freeHead;
    private int _capacity;

    // L1: Per-chunk плотные массивы jobId (swap-and-pop)
    private int[][] _chunkJobs;
    private int[] _chunkCount;
    private int[] _chunkUnclaimedCount;

    // L2: 64-битная битовая маска активных макро-секторов
    private ulong _macroActiveMask;
    private int[] _macroJobCounts;

    // Position-карта для дедупликации
    private readonly Dictionary<(int X, int Y, JobTypeId Type), int> _posMap = new(InitialCapacity);

    // Счётчики
    private int _totalCount;
    private int _unclaimedCount;

    // Memo эпоха и per-dispatch memoization (S-033)
    private int _claimEpoch;
    private int[] _canExecEpoch;
    private bool[] _canExecResult;

    public int UnclaimedCount => Volatile.Read(ref _unclaimedCount);
    public int TotalCount => Volatile.Read(ref _totalCount);

    public JobSpatialPyramid()
    {
        _capacity = InitialCapacity;
        AllocateArrays(_capacity);
        InitFreeList(_capacity);

        _chunkJobs = new int[ChunkCount][];
        _chunkCount = new int[ChunkCount];
        _chunkUnclaimedCount = new int[ChunkCount];
        for (int i = 0; i < ChunkCount; i++)
        {
            _chunkJobs[i] = new int[64];
        }

        _macroJobCounts = new int[MacroCount];
        _macroActiveMask = 0UL;
    }

    private void AllocateArrays(int capacity)
    {
        _targetX = new int[capacity];
        _targetY = new int[capacity];
        _standX = new int[capacity];
        _standY = new int[capacity];
        _sourceX = new int[capacity];
        _sourceY = new int[capacity];
        _typeId = new JobTypeId[capacity];
        _executionType = new JobExecutionType[capacity];
        _priorityTier = new JobPriorityTier[capacity];
        _requiredTool = new ToolRequirement[capacity];
        _maxWorkers = new int[capacity];
        _assignedWorkers = new int[capacity];
        _jobFailCd = new int[capacity];
        _targetItemCount = new int[capacity];
        _currentDeliveredCount = new int[capacity];
        _targetItemId = new ItemId[capacity];
        _workDuration = new float[capacity];
        _active = new bool[capacity];
        _nextFree = new int[capacity];
        _chunkJobIndex = new int[capacity];
        _canExecEpoch = new int[capacity];
        _canExecResult = new bool[capacity];
    }

    private void InitFreeList(int capacity)
    {
        for (int i = 0; i < capacity - 1; i++)
            _nextFree[i] = i + 1;
        _nextFree[capacity - 1] = -1;
        _freeHead = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetChunkIndexStatic(int tileX, int tileY)
    {
        int cx = Math.Clamp(tileX >> ChunkShift, 0, ChunkDim - 1);
        int cy = Math.Clamp(tileY >> ChunkShift, 0, ChunkDim - 1);
        return cy * ChunkDim + cx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetMacroIndex(int tileX, int tileY)
    {
        int mx = Math.Clamp(tileX >> MacroShift, 0, MacroDim - 1);
        int my = Math.Clamp(tileY >> MacroShift, 0, MacroDim - 1);
        return my * MacroDim + mx;
    }

    private void EnsureCapacity(int neededId)
    {
        if (neededId < _capacity) return;
        int newCap = Math.Max(_capacity * GrowFactor, neededId + 1024);
        Array.Resize(ref _targetX, newCap);
        Array.Resize(ref _targetY, newCap);
        Array.Resize(ref _standX, newCap);
        Array.Resize(ref _standY, newCap);
        Array.Resize(ref _sourceX, newCap);
        Array.Resize(ref _sourceY, newCap);
        Array.Resize(ref _typeId, newCap);
        Array.Resize(ref _executionType, newCap);
        Array.Resize(ref _priorityTier, newCap);
        Array.Resize(ref _requiredTool, newCap);
        Array.Resize(ref _maxWorkers, newCap);
        Array.Resize(ref _assignedWorkers, newCap);
        Array.Resize(ref _jobFailCd, newCap);
        Array.Resize(ref _targetItemCount, newCap);
        Array.Resize(ref _currentDeliveredCount, newCap);
        Array.Resize(ref _targetItemId, newCap);
        Array.Resize(ref _workDuration, newCap);
        Array.Resize(ref _active, newCap);
        Array.Resize(ref _nextFree, newCap);
        Array.Resize(ref _chunkJobIndex, newCap);
        Array.Resize(ref _canExecEpoch, newCap);
        Array.Resize(ref _canExecResult, newCap);

        for (int i = _capacity; i < newCap - 1; i++)
            _nextFree[i] = i + 1;
        _nextFree[newCap - 1] = _freeHead;
        _freeHead = _capacity;
        _capacity = newCap;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JobData GetJobData(int jobId)
    {
        return new JobData
        {
            Id = jobId,
            TypeId = _typeId[jobId],
            ExecutionType = _executionType[jobId],
            PriorityTier = _priorityTier[jobId],
            RequiredTool = _requiredTool[jobId],
            SourceX = _sourceX[jobId],
            SourceY = _sourceY[jobId],
            TargetX = _targetX[jobId],
            TargetY = _targetY[jobId],
            StandX = _standX[jobId],
            StandY = _standY[jobId],
            TargetItemId = _targetItemId[jobId],
            TargetItemCount = _targetItemCount[jobId],
            CurrentDeliveredCount = _currentDeliveredCount[jobId],
            MaxWorkers = _maxWorkers[jobId],
            AssignedWorkers = Volatile.Read(ref _assignedWorkers[jobId]),
            WorkDuration = _workDuration[jobId],
            IsActive = _active[jobId]
        };
    }

    public void BeginClaimEpoch()
    {
        int next = unchecked(_claimEpoch + 1);
        if (next == int.MaxValue)
        {
            if (_canExecEpoch != null)
                Array.Clear(_canExecEpoch, 0, _canExecEpoch.Length);
            next = 1;
        }
        Volatile.Write(ref _claimEpoch, next);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMemoCanExecute(int jobId, out bool result)
    {
        int epoch = Volatile.Read(ref _claimEpoch);
        if (epoch != 0 && jobId >= 0 && jobId < _capacity &&
            Volatile.Read(ref _canExecEpoch[jobId]) == epoch)
        {
            result = _canExecResult[jobId];
            return true;
        }
        result = false;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMemoCanExecute(int jobId, bool result)
    {
        int epoch = Volatile.Read(ref _claimEpoch);
        if (epoch != 0 && jobId >= 0 && jobId < _capacity)
        {
            _canExecResult[jobId] = result;
            Volatile.Write(ref _canExecEpoch[jobId], epoch);
        }
    }

    public int RegisterJob(JobData job)
    {
        lock (_registerLock)
        {
            var key = (job.TargetX, job.TargetY, job.TypeId);
            if (_posMap.TryGetValue(key, out int existingId))
                return existingId;

            int id;
            if (_freeHead != -1)
            {
                id = _freeHead;
                _freeHead = _nextFree[id];
                _nextFree[id] = -1;
            }
            else
            {
                id = _capacity;
                EnsureCapacity(id + 1);
            }

            _targetX[id] = job.TargetX;
            _targetY[id] = job.TargetY;
            _standX[id] = job.StandX;
            _standY[id] = job.StandY;
            _sourceX[id] = job.SourceX;
            _sourceY[id] = job.SourceY;
            _typeId[id] = job.TypeId;
            _executionType[id] = job.ExecutionType;
            _priorityTier[id] = job.PriorityTier;
            _requiredTool[id] = job.RequiredTool;
            _maxWorkers[id] = Math.Max(1, job.MaxWorkers);
            _assignedWorkers[id] = 0;
            _jobFailCd[id] = 0;
            _targetItemCount[id] = job.TargetItemCount;
            _currentDeliveredCount[id] = 0;
            _targetItemId[id] = job.TargetItemId;
            _workDuration[id] = job.WorkDuration;
            _active[id] = true;

            _posMap[key] = id;
            _totalCount++;
            Interlocked.Increment(ref _unclaimedCount);

            // Вставка в чанк L1
            int chunkIndex = GetChunkIndexStatic(job.TargetX, job.TargetY);
            ref var cJobs = ref _chunkJobs[chunkIndex];
            int count = _chunkCount[chunkIndex];
            if (count >= cJobs.Length)
            {
                Array.Resize(ref cJobs, cJobs.Length * 2);
            }
            cJobs[count] = id;
            _chunkJobIndex[id] = count;
            _chunkCount[chunkIndex] = count + 1;
            Interlocked.Increment(ref _chunkUnclaimedCount[chunkIndex]);

            // Обновление L2 макро-маски.
            // Счётчик мутируется и ВНЕ _registerLock (Interlocked в ReleaseWorkerClaim /
            // TryClaimJobForAgent), поэтому обычный ++ терял обновления, счётчик уезжал
            // в 0 при живых задачах и бит L2-маски гас — агенты переставали видеть работу
            // в целом макро-секторе. Маску пишем под локом (все её записи — под ним).
            int macroIndex = GetMacroIndex(job.TargetX, job.TargetY);
            Interlocked.Increment(ref _macroJobCounts[macroIndex]);
            _macroActiveMask |= (1UL << macroIndex);

            return id;
        }
    }

    public void RegisterBatch(List<JobData> jobs)
    {
        if (jobs == null || jobs.Count == 0) return;
        lock (_registerLock)
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                RegisterJobLocked(jobs[i]);
            }
        }
    }

    private void RegisterJobLocked(JobData job)
    {
        var key = (job.TargetX, job.TargetY, job.TypeId);
        if (_posMap.ContainsKey(key))
            return;

        int id;
        if (_freeHead != -1)
        {
            id = _freeHead;
            _freeHead = _nextFree[id];
            _nextFree[id] = -1;
        }
        else
        {
            id = _capacity;
            EnsureCapacity(id + 1);
        }

        _targetX[id] = job.TargetX;
        _targetY[id] = job.TargetY;
        _standX[id] = job.StandX;
        _standY[id] = job.StandY;
        _sourceX[id] = job.SourceX;
        _sourceY[id] = job.SourceY;
        _typeId[id] = job.TypeId;
        _executionType[id] = job.ExecutionType;
        _priorityTier[id] = job.PriorityTier;
        _requiredTool[id] = job.RequiredTool;
        _maxWorkers[id] = Math.Max(1, job.MaxWorkers);
        _assignedWorkers[id] = 0;
        _jobFailCd[id] = 0;
        _targetItemCount[id] = job.TargetItemCount;
        _currentDeliveredCount[id] = 0;
        _targetItemId[id] = job.TargetItemId;
        _workDuration[id] = job.WorkDuration;
        _active[id] = true;

        _posMap[key] = id;
        _totalCount++;
        Interlocked.Increment(ref _unclaimedCount);

        int chunkIndex = GetChunkIndexStatic(job.TargetX, job.TargetY);
        ref var cJobs = ref _chunkJobs[chunkIndex];
        int count = _chunkCount[chunkIndex];
        if (count >= cJobs.Length)
        {
            Array.Resize(ref cJobs, Math.Max(cJobs.Length * 2, count + 64));
        }
        cJobs[count] = id;
        _chunkJobIndex[id] = count;
        _chunkCount[chunkIndex] = count + 1;
        Interlocked.Increment(ref _chunkUnclaimedCount[chunkIndex]);

        int macroIndex = GetMacroIndex(job.TargetX, job.TargetY);
        Interlocked.Increment(ref _macroJobCounts[macroIndex]);
        _macroActiveMask |= (1UL << macroIndex);
    }

    public bool RemoveJob(int jobId, out JobData removedJob)
    {
        lock (_registerLock)
        {
            if (jobId < 0 || jobId >= _capacity || !_active[jobId])
            {
                removedJob = default;
                return false;
            }

            removedJob = GetJobData(jobId);
            _active[jobId] = false;

            var key = (_targetX[jobId], _targetY[jobId], _typeId[jobId]);
            _posMap.Remove(key);
            _totalCount--;

            bool wasUnclaimed = Volatile.Read(ref _assignedWorkers[jobId]) < _maxWorkers[jobId];
            if (wasUnclaimed)
            {
                Interlocked.Decrement(ref _unclaimedCount);
            }

            // Удаление из L1 чанка за O(1) swap-and-pop
            int chunkIndex = GetChunkIndexStatic(removedJob.TargetX, removedJob.TargetY);
            var cJobs = _chunkJobs[chunkIndex];
            int count = _chunkCount[chunkIndex];
            int idx = _chunkJobIndex[jobId];

            if (idx >= 0 && idx < count)
            {
                int lastIdx = count - 1;
                int lastJobId = cJobs[lastIdx];

                cJobs[idx] = lastJobId;
                _chunkJobIndex[lastJobId] = idx;
                cJobs[lastIdx] = -1;

                _chunkCount[chunkIndex] = lastIdx;
                if (wasUnclaimed)
                {
                    Interlocked.Decrement(ref _chunkUnclaimedCount[chunkIndex]);
                }
            }

            // Обновление L2 макро-маски: декрементируем только если задача была unclaimed.
            // Атомарный декремент с клампом на 0 (семантика Math.Max(0, x-1) без гонки):
            // обычный RMW под _registerLock НЕ сериализуется против Interlocked-мутаций
            // из ReleaseWorkerClaim (539) и TryClaimJobForAgent (771) → lost update.
            if (wasUnclaimed)
            {
                int macroIndex = GetMacroIndex(removedJob.TargetX, removedJob.TargetY);

                int macroCount;
                do
                {
                    macroCount = Volatile.Read(ref _macroJobCounts[macroIndex]);
                    if (macroCount <= 0)
                        break;
                }
                while (Interlocked.CompareExchange(
                           ref _macroJobCounts[macroIndex], macroCount - 1, macroCount) != macroCount);

                // Бит гасим только если счётчик реально дошёл до нуля, и перепроверяем под
                // локом (как в TryClaimJobForAgent), иначе погасим сектор с живыми задачами.
                if (macroCount <= 1)
                {
                    lock (_registerLock)
                    {
                        if (Volatile.Read(ref _macroJobCounts[macroIndex]) <= 0)
                            _macroActiveMask &= ~(1UL << macroIndex);
                    }
                }
            }

            // Возврат в free-list
            _nextFree[jobId] = _freeHead;
            _freeHead = jobId;

            return true;
        }
    }

    public bool RemoveJobByPos(int x, int y, JobTypeId type, out JobData removedJob)
    {
        lock (_registerLock)
        {
            if (_posMap.TryGetValue((x, y, type), out int id))
            {
                return RemoveJob(id, out removedJob);
            }
            removedJob = default;
            return false;
        }
    }

    public void RemoveBatchByPositions(List<(int X, int Y)> positions, JobTypeId type)
    {
        if (positions == null || positions.Count == 0) return;
        lock (_registerLock)
        {
            for (int i = 0; i < positions.Count; i++)
            {
                var pos = positions[i];
                if (_posMap.TryGetValue((pos.X, pos.Y, type), out int id))
                {
                    RemoveJob(id, out _);
                }
            }
        }
    }

    public bool TryGetJob(int id, out JobData job)
    {
        if (id < 0 || id >= _capacity || !_active[id])
        {
            job = default;
            return false;
        }
        job = GetJobData(id);
        return true;
    }

    public bool TryGetJobByPos(int x, int y, JobTypeId type, out JobData job)
    {
        lock (_registerLock)
        {
            if (_posMap.TryGetValue((x, y, type), out int id))
            {
                job = GetJobData(id);
                return true;
            }
            job = default;
            return false;
        }
    }

    public bool HasJobAt(int x, int y, JobTypeId type)
    {
        lock (_registerLock)
        {
            return _posMap.TryGetValue((x, y, type), out int id) && id >= 0 && id < _capacity && _active[id];
        }
    }

    public int GetChunkJobCount(int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= ChunkCount) return 0;
        return Volatile.Read(ref _chunkUnclaimedCount[chunkIndex]);
    }

    /// <summary>
    /// Освобождение слота задачи (агент освободился или был отменен).
    /// </summary>
    public void ReleaseWorkerClaim(int jobId)
    {
        if (jobId < 0 || jobId >= _capacity || !_active[jobId])
            return;

        // Кламп на 0 через CAS: ReleaseWorkerClaim может прийти «по stale-id» — слот уже
        // освобождён в RemoveJob и переиспользован под ДРУГУЮ задачу (free-list LIFO).
        // Слепой Decrement уводил счётчик в минус, и следующая CAS-атака
        // (while cur < _maxWorkers) выдавала ВТОРОЙ claim на задачу с MaxWorkers=1.
        int cur;
        do
        {
            cur = Volatile.Read(ref _assignedWorkers[jobId]);
            if (cur <= 0)
                return;
        }
        while (Interlocked.CompareExchange(ref _assignedWorkers[jobId], cur - 1, cur) != cur);

        int newAssigned = cur - 1;
        if (!_active[jobId])
            return;

        if (newAssigned == _maxWorkers[jobId] - 1)
        {
            Interlocked.Increment(ref _unclaimedCount);
            int ci = GetChunkIndexStatic(_targetX[jobId], _targetY[jobId]);
            Interlocked.Increment(ref _chunkUnclaimedCount[ci]);

            int mi = GetMacroIndex(_targetX[jobId], _targetY[jobId]);
            Interlocked.Increment(ref _macroJobCounts[mi]);
            lock (_registerLock)
            {
                _macroActiveMask |= (1UL << mi);
            }
        }
    }

    public void MarkJobUnreachable(int jobId, int cooldownMs = 5000)
    {
        if (jobId < 0 || jobId >= _capacity || !_active[jobId])
            return;
        _jobFailCd[jobId] = unchecked((int)System.Environment.TickCount + Math.Max(0, cooldownMs));
    }

    public void ClearJobCooldown(int jobId)
    {
        if (jobId >= 0 && jobId < _capacity)
            _jobFailCd[jobId] = 0;
    }

    public void ClearJobCooldownAt(int x, int y, JobTypeId type)
    {
        lock (_registerLock)
        {
            if (_posMap.TryGetValue((x, y, type), out int id) && id >= 0 && id < _capacity)
            {
                _jobFailCd[id] = 0;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsJobOnCooldown(int jobId)
    {
        int expire = _jobFailCd[jobId];
        if (expire == 0) return false;
        return unchecked(expire - (int)System.Environment.TickCount) > 0;
    }

    /// <summary>
    /// Иерархический поиск задачи для агента.
    /// Сначала проверяет свой чанк -> макро-сектор (4x4 чанка) -> глобальную пирамиду через ulong маску.
    /// </summary>
    public bool TryClaimJobForAgent(
        int agentX, int agentY,
        ToolRequirement tools,
        AgentDataPool pool,
        int agentIndex,
        SimulationContext ctx,
        bool hasStockpileSpace,
        bool hasAvailableLogs,
        out JobData claimedJob)
    {
        claimedJob = default;
        if (Volatile.Read(ref _unclaimedCount) <= 0)
            return false;

        int agentChunk = GetChunkIndexStatic(agentX, agentY);

        // 1. Быстрый проход по своему чанку
        if (Volatile.Read(ref _chunkUnclaimedCount[agentChunk]) > 0)
        {
            if (TryClaimInChunk(agentChunk, agentX, agentY, tools, pool, agentIndex, ctx, hasStockpileSpace, hasAvailableLogs, out claimedJob))
                return true;
        }

        // 2. Локальный макро-поиск в радиусе 1..3 чанков
        int centerCx = agentChunk % ChunkDim;
        int centerCy = agentChunk / ChunkDim;

        for (int r = 1; r <= 3; r++)
        {
            int minX = Math.Max(0, centerCx - r);
            int maxX = Math.Min(ChunkDim - 1, centerCx + r);
            int minY = Math.Max(0, centerCy - r);
            int maxY = Math.Min(ChunkDim - 1, centerCy + r);

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    if (cx > minX && cx < maxX && cy > minY && cy < maxY)
                        continue; // кольцо

                    int ci = cy * ChunkDim + cx;
                    if (Volatile.Read(ref _chunkUnclaimedCount[ci]) > 0)
                    {
                        if (TryClaimInChunk(ci, agentX, agentY, tools, pool, agentIndex, ctx, hasStockpileSpace, hasAvailableLogs, out claimedJob))
                            return true;
                    }
                }
            }
        }

        // 3. Глобальный макро-поиск через 64-битную маску L2
        ulong mask = Volatile.Read(ref _macroActiveMask);
        if (mask == 0UL)
            return false;

        int agentMacro = GetMacroIndex(agentX, agentY);
        // Сначала макро-секторы с тем же битовым сдвигом
        for (int mi = 0; mi < MacroCount; mi++)
        {
            int macroIdx = (agentMacro + mi) & 63;
            if ((mask & (1UL << macroIdx)) != 0 && Volatile.Read(ref _macroJobCounts[macroIdx]) > 0)
            {
                int baseChunkX = (macroIdx % MacroDim) * 4;
                int baseChunkY = (macroIdx / MacroDim) * 4;

                for (int cy = 0; cy < 4; cy++)
                {
                    for (int cx = 0; cx < 4; cx++)
                    {
                        int ci = (baseChunkY + cy) * ChunkDim + (baseChunkX + cx);
                        if (ci >= 0 && ci < ChunkCount && Volatile.Read(ref _chunkUnclaimedCount[ci]) > 0)
                        {
                            if (TryClaimInChunk(ci, agentX, agentY, tools, pool, agentIndex, ctx, hasStockpileSpace, hasAvailableLogs, out claimedJob))
                                return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float GetAffinityPenalty(AgentDataPool pool, int agentIndex, JobTypeId jobType)
    {
        if (pool == null || agentIndex < 0 || agentIndex >= pool.Capacity) return 0f;
        int last = pool.LastJobCategory[agentIndex];
        return JobPriorityManager.Instance.GetCategory(jobType) == (JobCategory)last ? 0f : 400f;
    }

    private bool TryClaimInChunk(
        int chunkIndex,
        int agentX, int agentY,
        ToolRequirement tools,
        AgentDataPool pool,
        int agentIndex,
        SimulationContext ctx,
        bool hasStockpileSpace,
        bool hasAvailableLogs,
        out JobData claimedJob)
    {
        claimedJob = default;
        int count = Volatile.Read(ref _chunkCount[chunkIndex]);
        if (count == 0) return false;

        var cJobs = _chunkJobs[chunkIndex];
        int scanLimit = Math.Min(count, cJobs.Length);

        int bestJobId = -1;
        int bestPriority = -1;
        float bestDistSq = float.MaxValue;

        for (int i = 0; i < scanLimit; i++)
        {
            if (i >= cJobs.Length) break;
            int jobId = cJobs[i];
            if (jobId < 0 || jobId >= _capacity || !_active[jobId])
                continue;

            if (Volatile.Read(ref _assignedWorkers[jobId]) >= _maxWorkers[jobId])
                continue;

            if (_requiredTool[jobId] != ToolRequirement.None && (tools & _requiredTool[jobId]) == 0)
                continue;

            if (_typeId[jobId] == JobTypeId.StockpileHauling && !hasStockpileSpace)
                continue;

            if (_typeId[jobId] == JobTypeId.BlueprintDelivery && !hasAvailableLogs)
                continue;

            if (IsJobOnCooldown(jobId))
                continue;

            // Быстрая проверка хэндлера CanAgentExecute с memoization на эпоху диспатча (S-033)
            if (JobRegistry.TryGetHandler(_typeId[jobId], out var handler))
            {
                bool canExec;
                if (TryGetMemoCanExecute(jobId, out bool memo))
                {
                    canExec = memo;
                }
                else
                {
                    canExec = handler.CanAgentExecute(agentIndex, GetJobData(jobId), pool, ctx);
                    SetMemoCanExecute(jobId, canExec);
                }

                if (!canExec)
                    continue;
            }

            int userPrio = JobPriorityManager.Instance.GetPriority(
                JobPriorityManager.Instance.GetCategory(_typeId[jobId]));
            if (userPrio <= 0)
                continue;

            int score = (userPrio << 8) | (255 - (byte)_priorityTier[jobId]);
            float dx = _standX[jobId] - agentX;
            float dy = _standY[jobId] - agentY;
            float distSq = dx * dx + dy * dy + GetAffinityPenalty(pool, agentIndex, _typeId[jobId]);

            if (score > bestPriority || (score == bestPriority && distSq < bestDistSq))
            {
                bestPriority = score;
                bestDistSq = distSq;
                bestJobId = jobId;
            }
        }

        if (bestJobId == -1)
            return false;

        // CAS захват
        int cur = Volatile.Read(ref _assignedWorkers[bestJobId]);
        while (cur < _maxWorkers[bestJobId])
        {
            int prev = Interlocked.CompareExchange(ref _assignedWorkers[bestJobId], cur + 1, cur);
            if (prev == cur)
            {
                if (cur + 1 >= _maxWorkers[bestJobId])
                {
                    Interlocked.Decrement(ref _unclaimedCount);
                    Interlocked.Decrement(ref _chunkUnclaimedCount[chunkIndex]);

                    int mi = GetMacroIndex(_targetX[bestJobId], _targetY[bestJobId]);
                    if (Interlocked.Decrement(ref _macroJobCounts[mi]) <= 0)
                    {
                        lock (_registerLock)
                        {
                            if (_macroJobCounts[mi] <= 0)
                                _macroActiveMask &= ~(1UL << mi);
                        }
                    }
                }
                claimedJob = GetJobData(bestJobId);
                return true;
            }
            cur = Volatile.Read(ref _assignedWorkers[bestJobId]);
        }

        return false;
    }

    public List<int> SnapshotActiveJobs(List<int> destination, int maxCount, ref int cursor)
    {
        destination.Clear();
        lock (_registerLock)
        {
            if (_posMap.Count == 0)
            {
                cursor = 0;
                return destination;
            }
            int skipped = 0;
            int target = cursor % Math.Max(1, _posMap.Count);
            foreach (int id in _posMap.Values)
            {
                if (skipped < target)
                {
                    skipped++;
                    continue;
                }
                if (destination.Count >= maxCount)
                    break;
                if (id >= 0 && id < _capacity && _active[id])
                    destination.Add(id);
            }
            cursor += destination.Count;
            if (destination.Count < maxCount)
                cursor = 0;
        }
        return destination;
    }

    public bool TryAddJobProgress(int x, int y, JobTypeId type, int countToAdd, out bool isCompleted, out JobData jobSnapshot)
    {
        lock (_registerLock)
        {
            if (_posMap.TryGetValue((x, y, type), out int id) && _active[id])
            {
                int target = _targetItemCount[id];
                int remaining = target - _currentDeliveredCount[id];
                int accepted = Math.Min(countToAdd, Math.Max(0, remaining));
                _currentDeliveredCount[id] += accepted;
                isCompleted = _currentDeliveredCount[id] >= target;
                jobSnapshot = GetJobData(id);
                return true;
            }
            isCompleted = false;
            jobSnapshot = default;
            return false;
        }
    }

    public bool TryAddJobProgressClamped(int x, int y, JobTypeId type, int countToAdd, out int acceptedCount, out bool isCompleted, out JobData jobSnapshot)
    {
        acceptedCount = 0;
        lock (_registerLock)
        {
            if (_posMap.TryGetValue((x, y, type), out int id) && _active[id])
            {
                int target = _targetItemCount[id];
                int remaining = target - _currentDeliveredCount[id];
                acceptedCount = Math.Min(countToAdd, Math.Max(0, remaining));
                _currentDeliveredCount[id] += acceptedCount;
                isCompleted = _currentDeliveredCount[id] >= target;
                jobSnapshot = GetJobData(id);
                return true;
            }
            isCompleted = false;
            jobSnapshot = default;
            return false;
        }
    }

    public bool TryClaimForWorkerInChunk(
        int chunkIndex,
        int workerTileX, int workerTileY,
        ToolRequirement workerTools,
        AgentDataPool pool,
        int agentIndex,
        SimulationContext ctx,
        out JobData claimedJob)
    {
        bool hasStockpileSpace = StockpileManager.Instance.HasFreeSpace;
        bool hasAvailableLogs = GroundItemManager.Instance.HasAvailableLogs;
        return TryClaimInChunk(
            chunkIndex, workerTileX, workerTileY,
            workerTools, pool, agentIndex, ctx,
            hasStockpileSpace, hasAvailableLogs,
            out claimedJob);
    }

    public void Clear()
    {
        lock (_registerLock)
        {
            Array.Clear(_active, 0, _capacity);
            Array.Clear(_assignedWorkers, 0, _capacity);
            Array.Clear(_jobFailCd, 0, _capacity);
            Array.Clear(_chunkCount, 0, ChunkCount);
            Array.Clear(_chunkUnclaimedCount, 0, ChunkCount);
            Array.Clear(_macroJobCounts, 0, MacroCount);
            _macroActiveMask = 0UL;
            _posMap.Clear();
            _totalCount = 0;
            _unclaimedCount = 0;
            InitFreeList(_capacity);
        }
    }
}
