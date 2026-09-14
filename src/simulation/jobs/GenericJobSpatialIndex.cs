using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Пространственный индекс задач на SoA + chunk-bucket + lock-free CAS-claim.
/// Регистрация/удаление — под lock (producer, редко). 
/// Claim — lock-free через Interlocked.Increment (consumer, каждый тик).
/// </summary>
public sealed class GenericJobSpatialIndex
{
    private const int ChunkShift = 4;
    private const int ChunkDim = 32;
    private const int ChunkCount = ChunkDim * ChunkDim;
    private const int InitialCapacity = 16384;
    private const int GrowFactor = 2;
    private const int JobsPerChunk = 256;

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
    private int[] _assignedWorkers;       // ← CAS-цель
    private int[] _jobFailCd;             // ms-метка окончания кулдауна «недостижимой» работы (см. MarkJobUnreachable)
    private int[] _targetItemCount;
    private int[] _currentDeliveredCount;
    private ItemId[] _targetItemId;
    private float[] _workDuration;
    private bool[] _active;

    // Free-list для переиспользования jobId.
    // Стартовое состояние: свободны [0, InitialCapacity), голова = 0.
    // _nextJobId продолжает нумерацию ПОСЛЕ free-list, иначе коллизия id.
    private int[] _nextFree;
    private int _freeHead;
    private int _capacity;

    // Chunk bucket: плоский массив jobId с per-chunk start/count
    private int[] _chunkJobs;
    private int[] _chunkStart;
    private int[] _chunkCount;
    private int _chunkCapacity;
    private int _jobsPerChunk = JobsPerChunk;

    // Position-карта для дедупликации при регистрации (только под lock)
    private readonly Dictionary<(int X, int Y, JobTypeId Type), int> _posMap = new(InitialCapacity);

    // Счётчики (volatile для lock-free чтения)
    private int _totalCount;
    private int _unclaimedCount;

    private int _nextJobId = 0;

    public int UnclaimedCount => Volatile.Read(ref _unclaimedCount);
    public int TotalCount => Volatile.Read(ref _totalCount);

    public GenericJobSpatialIndex()
    {
        _capacity = InitialCapacity;
        AllocateArrays(_capacity);
        InitFreeList(_capacity);
        _nextJobId = InitialCapacity;

        _chunkStart = new int[ChunkCount];
        _chunkCount = new int[ChunkCount];
        _chunkJobs = new int[ChunkCount * _jobsPerChunk];
        Array.Fill(_chunkJobs, -1);
        for (int ci = 0; ci < ChunkCount; ci++)
        {
            _chunkStart[ci] = ci * _jobsPerChunk;
        }
        _chunkCapacity = _chunkJobs.Length;
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
    private static int GetChunkIndex(int tileX, int tileY)
    {
        return GetChunkIndexStatic(tileX, tileY);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private JobData GetJobData(int jobId)
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

    private void EnsureCapacity(int neededId)
    {
        if (neededId < _capacity) return;
        int newCap = Math.Max(_capacity * GrowFactor, neededId + 1);
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

        for (int i = _capacity; i < newCap - 1; i++)
            _nextFree[i] = i + 1;
        _nextFree[newCap - 1] = _freeHead;
        _freeHead = _capacity;
        _capacity = newCap;
    }

    /// <summary>
    /// ерестраивает макет chunk-бакетов при переполнении слота чанка.
    /// Rebuilds the chunk-bucket layout when a chunk slot overflows.
    /// Called only under _registerLock (rare case).
    private void RebuildChunkBuckets()
    {
        int maxCount = 0;
        for (int ci = 0; ci < ChunkCount; ci++)
        {
            if (_chunkCount[ci] > maxCount)
                maxCount = _chunkCount[ci];
        }

        int newJobsPerChunk = Math.Max(_jobsPerChunk * 2, maxCount + 1);
        int newCap = ChunkCount * newJobsPerChunk;
        var newJobs = new int[newCap];
        Array.Fill(newJobs, -1);
        var newCounts = new int[ChunkCount];
        var newStarts = new int[ChunkCount];

        foreach (var id in _posMap.Values)
        {
            if (!_active[id]) continue;
            int ci = GetChunkIndex(_targetX[id], _targetY[id]);
            newJobs[ci * newJobsPerChunk + newCounts[ci]] = id;
            newCounts[ci]++;
        }

        // Публикация одним шагом: сначала содержимое, затем барьер,
        // затем управляющие поля. Читатели снимают ссылку _chunkJobs один раз
        // (см. TryClaimForWorkerInChunk/FillPrioritizedUnclaimed) и не видят
        // torn-read наполовину перестроенного макета.
        for (int ci = 0; ci < ChunkCount; ci++)
            newStarts[ci] = ci * newJobsPerChunk;
        Thread.MemoryBarrier();
        _chunkJobs = newJobs;
        _chunkCapacity = newCap;
        _jobsPerChunk = newJobsPerChunk;
        Thread.MemoryBarrier();
        for (int ci = 0; ci < ChunkCount; ci++)
        {
            _chunkStart[ci] = newStarts[ci];
            Volatile.Write(ref _chunkCount[ci], newCounts[ci]);
        }
    }

    private void AddToChunkBucket(int chunkIndex, int jobId)
    {
        if (_chunkCount[chunkIndex] >= _jobsPerChunk)
        {
            // Слот чанка переполнен — расширяем макет. jobId уже в _posMap,
            // поэтому перестройка включит его в новый макет.
            RebuildChunkBuckets();
            return;
        }

        int idx = _chunkStart[chunkIndex] + _chunkCount[chunkIndex];
        _chunkJobs[idx] = jobId;
        _chunkCount[chunkIndex]++;
    }

    private void RemoveFromChunkBucket(int chunkIndex, int jobId)
    {
        int start = _chunkStart[chunkIndex];
        int count = _chunkCount[chunkIndex];
        int end = start + count - 1;
        for (int i = start; i <= end; i++)
        {
            if (_chunkJobs[i] == jobId)
            {
                _chunkJobs[i] = _chunkJobs[end];
                _chunkJobs[end] = -1;
                _chunkCount[chunkIndex]--;
                return;
            }
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
                id = _nextJobId++;
                EnsureCapacity(id);
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
            _maxWorkers[id] = job.MaxWorkers;
            // Публикация полей ДО _active=true: lock-free читатели видят
            // работу только после флага, Thread.MemoryBarrier запрещает
            // перестановку записей (иначе читатель видит _active + мусор).
            Thread.MemoryBarrier();
            _assignedWorkers[id] = 0;
            _jobFailCd[id] = 0; // сброс кулдауна от прошлого владельца jobId из free-list
            _targetItemCount[id] = job.TargetItemCount;
            _currentDeliveredCount[id] = 0;
            _targetItemId[id] = job.TargetItemId;
            _workDuration[id] = job.WorkDuration;
            _active[id] = true;

            _posMap[key] = id;
            _totalCount++;

            int chunkIndex = GetChunkIndex(job.TargetX, job.TargetY);
            AddToChunkBucket(chunkIndex, id);
            Interlocked.Increment(ref _unclaimedCount);

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
                var job = jobs[i];
                var key = (job.TargetX, job.TargetY, job.TypeId);
                if (_posMap.ContainsKey(key))
                    continue;

                int id;
                if (_freeHead != -1)
                {
                    id = _freeHead;
                    _freeHead = _nextFree[id];
                    _nextFree[id] = -1;
                }
                else
                {
                    id = _nextJobId++;
                    EnsureCapacity(id);
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
                _maxWorkers[id] = job.MaxWorkers;
                // Публикация полей ДО _active=true: lock-free читатели видят
                // работу только после флага, Thread.MemoryBarrier запрещает
                // перестановку записей (иначе читатель видит _active + мусор).
                Thread.MemoryBarrier();
                _assignedWorkers[id] = 0;
                _jobFailCd[id] = 0;
                _targetItemCount[id] = job.TargetItemCount;
                _currentDeliveredCount[id] = 0;
                _targetItemId[id] = job.TargetItemId;
                _workDuration[id] = job.WorkDuration;
                _active[id] = true;

                _posMap[key] = id;
                _totalCount++;

                int chunkIndex = GetChunkIndex(job.TargetX, job.TargetY);
                AddToChunkBucket(chunkIndex, id);
                Interlocked.Increment(ref _unclaimedCount);
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

    /// <summary>Быстрая проверка наличия активной задачи на клетке (для JobValidator).</summary>
    public bool HasJobAt(int x, int y, JobTypeId type)
    {
        lock (_registerLock)
        {
            return _posMap.TryGetValue((x, y, type), out int id) && id >= 0 && id < _capacity && _active[id];
        }
    }

    /// <summary>
    /// Слайс активных jobId для аудита (JobValidator): копирует до maxCount id,
    /// начиная с позиции курсора, курсор сдвигает циклически. Под lock — дёшево,
    /// т.к. только чтение _posMap (безопасно при больших объёмах: порциями).
    /// </summary>
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
            // _posMap.Values копировать целиком дорого при 200k — идём enumerator
            // со skip до курсора. O(cursor) на пропуск, но курсор циклический
            // и maxCount=2048: худший случай один проход по словарю за ~100 тиков.
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
                cursor = 0; // прошли до конца — следующий тик с начала
        }
        return destination;
    }

    public bool TryAddJobProgress(int x, int y, JobTypeId type, int countToAdd, out bool isCompleted, out JobData jobSnapshot)
    {
        lock (_registerLock)
        {
            if (_posMap.TryGetValue((x, y, type), out int id) && _active[id])
            {
                _currentDeliveredCount[id] += countToAdd;
                isCompleted = _currentDeliveredCount[id] >= _targetItemCount[id];
                jobSnapshot = GetJobData(id);
                return true;
            }
            isCompleted = false;
            jobSnapshot = default;
            return false;
        }
    }

    /// <summary>
    /// Lock-free: пытается найти и захватить лучшую доступную задачу в указанном чанке для одного рабочего.
    /// Использует Interlocked.CompareExchange на AssignedWorkers — без lock'а.
    /// </summary>
    public bool TryClaimForWorkerInChunk(
        int chunkIndex,
        int workerTileX, int workerTileY,
        ToolRequirement workerTools,
        AgentDataPool pool, int agentIndex,
        SimulationContext ctx,
        out JobData claimedJob)
    {
        claimedJob = default;

        // Снимок макета: RebuildChunkBuckets публикует новый массив одним шагом,
        // читаем ссылку/старт/каунт в локальные переменные, иначе torn-read.
        int[] chunkJobsSnap = Volatile.Read(ref _chunkJobs);
        if (chunkJobsSnap == null) return false;
        int start = Volatile.Read(ref _chunkStart[chunkIndex]);
        int count = Volatile.Read(ref _chunkCount[chunkIndex]);
        if (count == 0) return false;

        // Быстрая проверка: есть ли вообще вакансии в этом чанке?
        bool hasStockpileSpace = StockpileManager.Instance.HasFreeSpace;
        bool hasAvailableLogs = GroundItemManager.Instance.HasAvailableLogs;

        int bestJobId = -1;
        int bestPriority = -1;
        float bestDistSq = float.MaxValue;

        // Проход 1: read-only поиск лучшего кандидата
        int jobsLen = chunkJobsSnap.Length;
        for (int i = 0; i < count; i++)
        {
            int idx = start + i;
            if (idx >= jobsLen)
                break;
            int jobId = chunkJobsSnap[idx];
            if (jobId < 0 || jobId >= _capacity || !_active[jobId])
                continue;

            // Lock-free проверка доступности
            if (Volatile.Read(ref _assignedWorkers[jobId]) >= _maxWorkers[jobId])
                continue;

            if (_requiredTool[jobId] != ToolRequirement.None && (workerTools & _requiredTool[jobId]) == 0)
                continue;

            if (_typeId[jobId] == JobTypeId.StockpileHauling && !hasStockpileSpace)
                continue;

            if (_typeId[jobId] == JobTypeId.BlueprintDelivery && !hasAvailableLogs)
                continue;

            int priority = JobPriorityManager.Instance.GetPriorityForJobType(_typeId[jobId]);
            if (priority <= 0)
                continue;

            // Задача недавно оказалась недостижимой (агент застрял у стены) —
            // даём время, чтобы её мог взять агент с другой стороны.
            if (IsJobOnCooldown(jobId))
                continue;

            if (!JobRegistry.TryGetHandler(_typeId[jobId], out var handler) ||
                !handler.CanAgentExecute(agentIndex, GetJobData(jobId), pool, ctx))
                continue;

            float dx = _standX[jobId] - workerTileX;
            float dy = _standY[jobId] - workerTileY;
            float distSq = dx * dx + dy * dy + GetAffinityPenalty(pool, agentIndex, _typeId[jobId]);

            if (priority > bestPriority || (priority == bestPriority && distSq < bestDistSq))
            {
                bestPriority = priority;
                bestDistSq = distSq;
                bestJobId = jobId;
            }
        }

        if (bestJobId == -1)
            return false;

        // Проход 2: CAS-захват лучшего кандидата
        int current = Volatile.Read(ref _assignedWorkers[bestJobId]);
        while (current < _maxWorkers[bestJobId])
        {
            int prev = Interlocked.CompareExchange(ref _assignedWorkers[bestJobId], current + 1, current);
            if (prev == current)
            {
                // Успешно захвачен слот
                if (current + 1 >= _maxWorkers[bestJobId])
                {
                    // Задача полностью укомплектована — уменьшаем счётчик
                    Interlocked.Decrement(ref _unclaimedCount);
                }
                claimedJob = GetJobData(bestJobId);
                return true;
            }
            // CAS не удался — другой поток опередил, пробуем снова
            current = Volatile.Read(ref _assignedWorkers[bestJobId]);
        }

        // Слоты закончились между проходами
        return false;
    }

    /// <summary>
    /// Lock-free: освобождает слот задачи (Interlocked.Decrement).
    /// Если задача снова стала доступна — увеличивает unclaimedCount.
    /// </summary>
    public void ReleaseWorkerClaim(int jobId)
    {
        if (jobId < 0 || jobId >= _capacity || !_active[jobId])
            return;

        // Decrement возвращает НОВОЕ значение: задача была полностью занята
        // (assigned == max) и стала доступна (new == max - 1).
        // _active читаем ПОСЛЕ Decrement: если работу успели удалить между
        // claim и release, RemoveJob уже поправил _unclaimedCount сам —
        // повторный Increment здесь дал бы двойное восстановление.
        int newAssigned = Interlocked.Decrement(ref _assignedWorkers[jobId]);
        if (!_active[jobId])
            return;
        if (newAssigned == _maxWorkers[jobId] - 1)
        {
            Interlocked.Increment(ref _unclaimedCount);
        }
    }

    /// <summary>
    /// Помечает задачу как «временно недостижимую» (агент застрял на подходе:
    /// загорожена стеной/толпой). На время <paramref name="cooldownMs"/>
    /// задача не назначается никому — это снимает феномен «вечный агент у
    /// стены», когда ближний бездельник раз за разом пробует одну и ту же
    /// работу, а дальний (который мог бы обойти) не получает шанса.
    /// </summary>
    public void MarkJobUnreachable(int jobId, int cooldownMs = 5000)
    {
        if (jobId < 0 || jobId >= _capacity || !_active[jobId])
            return;
        _jobFailCd[jobId] = unchecked((int)System.Environment.TickCount + Math.Max(0, cooldownMs));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsJobOnCooldown(int jobId)
    {
        int expire = _jobFailCd[jobId];
        if (expire == 0)
            return false;
        int remain = unchecked(expire - (int)System.Environment.TickCount);
        // remain>0 корректен при wrap-around int (24.8 дня) для коротких кулдаунов.
        return remain > 0;
    }

    /// <summary>
    /// Штраф за смену профессии (в единицах distSq): задачи той же категории,
    /// которую агент делал последней, предпочитаются в радиусе ~20 тайлов.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float GetAffinityPenalty(AgentDataPool pool, int agentIndex, JobTypeId jobType)
    {
        int last = pool.LastJobCategory[agentIndex];
        if (last < 0)
            return 0f;
        return JobPriorityManager.Instance.GetCategory(jobType) == (JobCategory)last ? 0f : 400f;
    }

    public bool RemoveJob(int id, out JobData removedJob)
    {
        lock (_registerLock)
        {
            if (id < 0 || id >= _capacity || !_active[id])
            {
                removedJob = default;
                return false;
            }

            removedJob = GetJobData(id);
            _active[id] = false;
            _posMap.Remove((_targetX[id], _targetY[id], _typeId[id]));

            _nextFree[id] = _freeHead;
            _freeHead = id;
            _totalCount--;

            int chunkIndex = GetChunkIndex(_targetX[id], _targetY[id]);
            RemoveFromChunkBucket(chunkIndex, id);

            if (Volatile.Read(ref _assignedWorkers[id]) < _maxWorkers[id])
            {
                Interlocked.Decrement(ref _unclaimedCount);
            }

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
                var (x, y) = positions[i];
                if (_posMap.TryGetValue((x, y, type), out int id) && _active[id])
                {
                    _active[id] = false;
                    _posMap.Remove((x, y, type));

                    _nextFree[id] = _freeHead;
                    _freeHead = id;
                    _totalCount--;

                    int chunkIndex = GetChunkIndex(x, y);
                    RemoveFromChunkBucket(chunkIndex, id);

                    if (Volatile.Read(ref _assignedWorkers[id]) < _maxWorkers[id])
                    {
                        Interlocked.Decrement(ref _unclaimedCount);
                    }
                }
            }
        }
    }

    public void FillPrioritizedUnclaimed(List<int> destination)
    {
        destination.Clear();
        int[] chunkJobsSnap = Volatile.Read(ref _chunkJobs);
        if (chunkJobsSnap == null) return;
        int snapLen = chunkJobsSnap.Length;
        for (int ci = 0; ci < ChunkCount; ci++)
        {
            int start = Volatile.Read(ref _chunkStart[ci]);
            int count = Volatile.Read(ref _chunkCount[ci]);
            for (int i = 0; i < count; i++)
            {
                int idx = start + i;
                if (idx < 0 || idx >= snapLen) break;
                int jobId = chunkJobsSnap[idx];
                if (jobId >= 0 && jobId < _capacity && _active[jobId] &&
                    Volatile.Read(ref _assignedWorkers[jobId]) < _maxWorkers[jobId])
                {
                    destination.Add(jobId);
                }
            }
        }
        destination.Sort((a, b) =>
        {
            int pa = JobPriorityManager.Instance.GetPriorityForJobType(_typeId[a]);
            int pb = JobPriorityManager.Instance.GetPriorityForJobType(_typeId[b]);
            int cmp = pb.CompareTo(pa);
            if (cmp != 0) return cmp;
            return _priorityTier[b].CompareTo(_priorityTier[a]);
        });
    }

    /// <summary>
    /// Глобальный захват: ищет в готовом приоритизированном списке кандидатов
    /// (см. FillPrioritizedUnclaimed) ближайшую доступную задачу для рабочего.
    /// Запасной (fallback) распределитель на случай, когда локальный/чанковый
    /// поиск не покрыл всех простаивающих работников.
    /// </summary>
    public bool TryClaimFromCandidateList(
        List<int> candidates,
        int workerTileX, int workerTileY,
        ToolRequirement workerTools,
        AgentDataPool pool, int agentIndex,
        SimulationContext ctx,
        out JobData claimedJob)
    {
        claimedJob = default;
        if (candidates == null || candidates.Count == 0)
            return false;

        bool hasStockpileSpace = StockpileManager.Instance.HasFreeSpace;
        bool hasAvailableLogs = GroundItemManager.Instance.HasAvailableLogs;

        int bestJobId = -1;
        float bestDistSq = float.MaxValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            int jobId = candidates[i];
            if (jobId < 0 || jobId >= _capacity || !_active[jobId])
                continue;

            if (Volatile.Read(ref _assignedWorkers[jobId]) >= _maxWorkers[jobId])
                continue;

            if (_requiredTool[jobId] != ToolRequirement.None && (workerTools & _requiredTool[jobId]) == 0)
                continue;

            if (_typeId[jobId] == JobTypeId.StockpileHauling && !hasStockpileSpace)
                continue;
            if (_typeId[jobId] == JobTypeId.BlueprintDelivery && !hasAvailableLogs)
                continue;

            int priority = JobPriorityManager.Instance.GetPriorityForJobType(_typeId[jobId]);
            if (priority <= 0)
                continue;

            // Задача недавно оказалась недостижимой — пропускаем.
            if (IsJobOnCooldown(jobId))
                continue;

            if (!JobRegistry.TryGetHandler(_typeId[jobId], out var handler) ||
                !handler.CanAgentExecute(agentIndex, GetJobData(jobId), pool, ctx))
                continue;

            float dx = _standX[jobId] - workerTileX;
            float dy = _standY[jobId] - workerTileY;
            float distSq = dx * dx + dy * dy + GetAffinityPenalty(pool, agentIndex, _typeId[jobId]);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestJobId = jobId;
            }
        }

        if (bestJobId == -1)
            return false;

        int current = Volatile.Read(ref _assignedWorkers[bestJobId]);
        while (current < _maxWorkers[bestJobId])
        {
            int prev = Interlocked.CompareExchange(ref _assignedWorkers[bestJobId], current + 1, current);
            if (prev == current)
            {
                if (current + 1 >= _maxWorkers[bestJobId])
                    Interlocked.Decrement(ref _unclaimedCount);
                claimedJob = GetJobData(bestJobId);
                return true;
            }
            current = Volatile.Read(ref _assignedWorkers[bestJobId]);
        }

        return false;
    }

    public int GetChunkJobCount(int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= ChunkCount) return 0;
        return Volatile.Read(ref _chunkCount[chunkIndex]);
    }

    public JobTypeId GetJobType(int jobId)
    {
        if (jobId < 0 || jobId >= _capacity || !_active[jobId])
            return JobTypeId.None;
        return _typeId[jobId];
    }
}
