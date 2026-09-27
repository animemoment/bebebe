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

    // Per-dispatch memo CanAgentExecute: состояние менеджеров (Ground/Crop)
    // заморожено на время DispatchPendingJobs (диспетчер и коммиты идут
    // последовательно в одном sim-потоке), поэтому повторы CanAgentExecute
    // для одного jobId между воркерами — чистый дубль под lock. Кэшируем
    // результат на время одного DispatchPendingJobs-вызова: один lock на
    // jobId вместо N (по числу воркеров). Эпоха инкрементится диспетчером
    // в начале каждого вызова (BeginClaimEpoch); записи — только воркеры
    // claim-проходов, гонка записей одного jobId невозможна без потери
    // корректности (идемпотентный bool), чтение — без lock.
    // Размер — по capacity, растёт в EnsureCapacity.
    private int _claimEpoch;
    private int[] _canExecEpoch;
    private bool[] _canExecResult;

    private int _nextJobId = 0;

    public int UnclaimedCount => Volatile.Read(ref _unclaimedCount);
    public int TotalCount => Volatile.Read(ref _totalCount);

    /// <summary>
    /// Начать новую эпоху memo CanAgentExecute. Зовёт диспетчер один раз
    /// в начале DispatchPendingJobs (sim-поток). Переполнение int — сброс
    /// массива эпох (раз в ~2 млрд диспатчей).
    /// </summary>
    public void BeginClaimEpoch()
    {
        int next = unchecked(_claimEpoch + 1);
        if (next == int.MaxValue)
        {
            Array.Clear(_canExecEpoch, 0, _canExecEpoch.Length);
            next = 1;
        }
        Volatile.Write(ref _claimEpoch, next);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetMemoCanExecute(int jobId, out bool result)
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
    private void StoreMemoCanExecute(int jobId, bool result)
    {
        if (jobId < 0 || jobId >= _capacity)
            return;
        _canExecResult[jobId] = result;
        Volatile.Write(ref _canExecEpoch[jobId], Volatile.Read(ref _claimEpoch));
    }

    public GenericJobSpatialIndex()
    {
        _capacity = InitialCapacity;
        AllocateArrays(_capacity);
        _canExecEpoch = new int[_capacity];
        _canExecResult = new bool[_capacity];
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
        Array.Resize(ref _canExecEpoch, newCap);
        Array.Resize(ref _canExecResult, newCap);

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
    ///
    /// БАГ «полосы грядок»: старая публикация (сначала _chunkJobs, потом
    /// _chunkStart/_chunkCount) давала torn-read — читатель видел новый массив
    /// со старыми start/count и считал чанк пустым/чужой. Теперь порядок
    /// обратный: сначала counts в 0 (чанк «пуст», но консистентен), барьер,
    /// затем новый массив + starts, барьер, затем реальные counts. Читатель
    /// в худшем случае видит пустой чанк один диспатч-вызов, но никогда —
    /// чужое содержимое.
    /// </summary>
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

        for (int ci = 0; ci < ChunkCount; ci++)
            newStarts[ci] = ci * newJobsPerChunk;

        // Шаг 1: counts в 0 — чанки «пусты», но консистентны со старым массивом
        // (count=0 → читатель выходит раньше чтения содержимого).
        for (int ci = 0; ci < ChunkCount; ci++)
            Volatile.Write(ref _chunkCount[ci], 0);
        Thread.MemoryBarrier();

        // Шаг 2: новый массив + starts (counts ещё 0 — читатель видит пустоту).
        _chunkJobs = newJobs;
        _chunkCapacity = newCap;
        _jobsPerChunk = newJobsPerChunk;
        for (int ci = 0; ci < ChunkCount; ci++)
            _chunkStart[ci] = newStarts[ci];
        Thread.MemoryBarrier();

        // Шаг 3: реальные counts — чанки «появляются» целиком.
        for (int ci = 0; ci < ChunkCount; ci++)
            Volatile.Write(ref _chunkCount[ci], newCounts[ci]);
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

            // P0-1: без трейса регистрации (ToString+Mark+интерполяция на каждую
            // работу; sweep регистрирует сотни).

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
                // #2: кламп перепоставки — носильщик может принести до 29 брёвен
                // (25кг/0.85) при target 10–25. Без клампа излишек испарялся:
                // индекс и AddDeliveredLogs слепо прибавляли count.
                int target = _targetItemCount[id];
                int remaining = target - _currentDeliveredCount[id];
                int accepted = Math.Min(countToAdd, Math.Max(0, remaining));
                _currentDeliveredCount[id] += accepted;
                isCompleted = _currentDeliveredCount[id] >= target;
                countToAdd = accepted;
                jobSnapshot = GetJobData(id);
                return true;
            }
            isCompleted = false;
            jobSnapshot = default;
            return false;
        }
    }

    /// <summary>
    /// #2: остаток доставки сверх target (то, что кламп отсёк в TryAddJobProgress).
    /// Вызывается через out-параметр — сколько из carried реально принято.
    /// </summary>
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

        // Снимок макета (порядок важен — см. RebuildChunkBuckets): сначала
        // count, барьер, затем ссылка+start. Если между чтениями прошла
        // перестройка (counts сброшены в 0 → новый макет), count=0 и выходим
        // раньше чтения чужого содержимого. idx>=len — тоже выход, не пропуск.
        int count = Volatile.Read(ref _chunkCount[chunkIndex]);
        if (count == 0) return false;
        Thread.MemoryBarrier();
        int[] chunkJobsSnap = Volatile.Read(ref _chunkJobs);
        if (chunkJobsSnap == null) return false;
        int start = Volatile.Read(ref _chunkStart[chunkIndex]);

        // Быстрая проверка: есть ли вообще вакансии в этом чанке?
        bool hasStockpileSpace = StockpileManager.Instance.HasFreeSpace;
        bool hasAvailableLogs = GroundItemManager.Instance.HasAvailableLogs;

        // P0-2: кап сканирования чанка (было 64 с головы списка — хвост чанка
        // голодал вечно: старые работы лежали дальше 64-й позиции и их никто
        // не брал). Окно сканирования ротируется по эпохе диспатча: каждый
        // вызов покрывает следующие 64, за несколько проходов — весь чанк.
        const int MaxClaimScanPerWorker = 64;
        int scanCount = Math.Min(count, MaxClaimScanPerWorker);
        int epoch = Volatile.Read(ref _claimEpoch);
        int scanStart = count > scanCount ? epoch % (count - scanCount + 1) : 0;

        int bestJobId = -1;
        int bestPriority = -1;
        float bestDistSq = float.MaxValue;

        // Проход 1: read-only поиск лучшего кандидата
        // (окно [scanStart, scanStart+scanCount) — ротация эпохой выше).
        int jobsLen = chunkJobsSnap.Length;
        for (int i = 0; i < scanCount; i++)
        {
            int idx = start + scanStart + i;
            if (idx >= start + count)
                break;
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

            // Finding 7: приоритет через категорию без GetPriorityForJobType
            // (switch + Volatile-read вместо двух вызовов на кандидата).
            int priority = JobPriorityManager.Instance.GetPriority(
                JobPriorityManager.Instance.GetCategory(_typeId[jobId]));
            if (priority <= 0)
                continue;

            // Задача недавно оказалась недостижимой (агент застрял у стены) —
            // даём время, чтобы её мог взять агент с другой стороны.
            if (IsJobOnCooldown(jobId))
                continue;

            // Per-dispatch memo: все CanAgentExecute агент-независимы
            // (читают только ctx/мокси менеджеров, agentIndex игнорируется),
            // состояние заморожено на время диспатча — один lock на jobId.
            bool canExec;
            if (TryGetMemoCanExecute(jobId, out bool memo))
            {
                canExec = memo;
            }
            else
            {
                if (!JobRegistry.TryGetHandler(_typeId[jobId], out var handler2) ||
                    !handler2.CanAgentExecute(agentIndex, GetJobData(jobId), pool, ctx))
                {
                    StoreMemoCanExecute(jobId, false);
                    continue;
                }
                StoreMemoCanExecute(jobId, true);
                canExec = true;
            }
            if (!canExec)
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
        {
            return false;
        }

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
        // P0-1: без SimEvents — кулдаун случается из горячего пути, интерполяция
        // строки + два lock на каждый stuck-релиз.
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

    // P0-1: no-op — оставлена для совместимости вызовов. Счётчики canexec.*
    // (ToString + lock на каждого кандидата) убраны из claim-цикла.
    public void NoteCanExec(JobTypeId type, bool memoHit, bool result)
    {
    }

    /// <summary>
    /// C31: снимок состава задач (job_id, type, pos, assigned/max, priority, fail_cd)
    /// для ответа «какие работы в диспетчере сейчас». Вызывать раз в секунду из sim-потока,
    /// не из горячего пути (O(N) проход). Формат строк: id;type;x;y;assigned/max;prio;cooldown.
    /// </summary>
    public string BuildJobSnapshot(int maxRows = 200)
    {
        var sb = new System.Text.StringBuilder(2048);
        sb.AppendLine("id;type;x;y;assigned/max;prio;cooldown_ms");
        int rows = 0;
        for (int id = 0; id < _capacity && rows < maxRows; id++)
        {
            if (!_active[id]) continue;
            int cd = 0;
            int exp = _jobFailCd[id];
            if (exp != 0)
            {
                int remain = unchecked(exp - (int)System.Environment.TickCount);
                if (remain > 0) cd = remain;
            }
            sb.Append(id).Append(';').Append(_typeId[id].ToString()).Append(';')
              .Append(_targetX[id]).Append(';').Append(_targetY[id]).Append(';')
              .Append(Volatile.Read(ref _assignedWorkers[id])).Append('/').Append(_maxWorkers[id]).Append(';')
              .Append(JobPriorityManager.Instance.GetPriorityForJobType(_typeId[id])).Append(';')
              .Append(cd).AppendLine();
            rows++;
        }
        return sb.ToString();
    }
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

            // P0-1: без трейса удаления (ToString+Count+Mark на каждое завершение
            // haul-работы; при шаттлах это тысячи/с).

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

    /// <summary>
    /// Собирает незахваченные задачи в приоритизированный список для global-прохода.
    /// P1 (Dispatch-спайк): старый Fill шёл по ВСЕМ 1024 чанкам даже когда
    /// незахваченных мало (пустые чанки — volatile-read, но при тысячах
    /// unclaimed в 2–3 чанках хвост всё равно сканировался зря) + ToArray()×2
    /// на сортировку (два heap-alloc раз в 2с + двойное копирование).
    /// Теперь: ранний выход по счётчику собранных unclaimed (O(unclaimed),
    /// не O(N)) + сортировка in-place по scratch-массивам без ToArray.
    /// destination переупорядочивается in-place, аллокаций нет.
    /// </summary>
    public void FillPrioritizedUnclaimed(List<int> destination)
    {
        const int MaxGlobalCandidates = 512;
        destination.Clear();
        int wantTotal = Volatile.Read(ref _unclaimedCount);
        if (wantTotal <= 0) return;
        int want = Math.Min(wantTotal, MaxGlobalCandidates);
        int[] chunkJobsSnap = Volatile.Read(ref _chunkJobs);
        if (chunkJobsSnap == null) return;
        int snapLen = chunkJobsSnap.Length;
        int collectedUnclaimed = 0;
        // Стартовый чанк ротируется эпохой: иначе при want < unclaimed
        // (ранний стоп выше) хвост чанков не собирался НИКОГДА — старые
        // работы в дальних чанках висели вечно ("работа давняя — не берут").
        int fillEpoch = Volatile.Read(ref _claimEpoch);
        for (int pass = 0; pass < ChunkCount; pass++)
        {
            int ci = (fillEpoch + pass) % ChunkCount;
            {
            // Тот же порядок, что в TryClaimForWorkerInChunk: count → барьер →
            // ссылка/starts. Рваный макет даёт пропуск чанка на один проход,
            // а не чужое содержимое.
            int count = Volatile.Read(ref _chunkCount[ci]);
            if (count == 0) continue;
            Thread.MemoryBarrier();
            int[] snap = Volatile.Read(ref _chunkJobs);
            if (!ReferenceEquals(snap, chunkJobsSnap))
            {
                chunkJobsSnap = snap;
                if (chunkJobsSnap == null) return;
                snapLen = chunkJobsSnap.Length;
            }
            int start = Volatile.Read(ref _chunkStart[ci]);
            for (int i = 0; i < count; i++)
            {
                int idx = start + i;
                if (idx < 0 || idx >= snapLen) break;
                int jobId = chunkJobsSnap[idx];
                if (jobId >= 0 && jobId < _capacity && _active[jobId] &&
                    Volatile.Read(ref _assignedWorkers[jobId]) < _maxWorkers[jobId])
                {
                    destination.Add(jobId);
                    collectedUnclaimed++;
                    // Ранний стоп двойной: кап 512 (global берёт ≤32 рабочих —
                    // запас покрывает выбор) И все unclaimed уже собраны
                    // (хвост чанков — пустые/занятые, сканировать нечего).
                    // Полный скан 262k не нужен: O(unclaimed), не O(N).
                    if (destination.Count >= MaxGlobalCandidates || collectedUnclaimed >= want)
                        goto Sort;
                }
            }
            }
        }
    Sort:
        // Ключ сортировки предвычисляем один раз на задачу: приоритет типа
        // (через категорию — дешёвый switch) в старших битах, тир — в младших.
        // Внимание: Emergency=0 — самый ВАЖНЫЙ тир, Low=6 — фон. Прямой (p<<8)|tier
        // инвертирует порядок тиров (Low всплывал бы вверх). Инвертируем тир:
        // key = (p << 8) | (255 - tier): сортировка по убыванию = старый компаратор
        // (pb.CompareTo(pa), затем tier b.CompareTo(a)) 1-в-1.
        // @destroyer: приоритет типа p — тот же GetPriorityForJobType, только без
        // лишнего indirection-вызова; инверсия тира покрыта инвариантом ниже.
        int n = destination.Count;
        if (n == 0) return;
        _sortKeys.EnsureCapacity(n);
        _sortIds.EnsureCapacity(n);
        _sortKeys.Clear();
        _sortIds.Clear();
        // P1: in-place сортировка без ToArray()×2 — scratch-массивы переиспользуются
        // между проходами (Fill идёт раз в 2с, но два heap-alloc 512 int + двойное
        // копирование на каждый global-проход давили Gen0 зря).
        if (_sortKeysScratch == null || _sortKeysScratch.Length < n)
        {
            _sortKeysScratch = new int[Math.Max(n, 512)];
            _sortIdsScratch = new int[Math.Max(n, 512)];
        }
        for (int i = 0; i < n; i++)
        {
            int jobId = destination[i];
            int p = JobPriorityManager.Instance.GetPriorityForJobType(_typeId[jobId]);
            _sortKeysScratch[i] = (p << 8) | (255 - (byte)_priorityTier[jobId]);
            _sortIdsScratch[i] = jobId;
        }
        // Сортировка пар (key, jobId) без аллокаций на проход: Comparer без
        // замыкания (static lambda в Comparer.Create — один alloc раз в 2с).
        Array.Sort(_sortKeysScratch, _sortIdsScratch, 0, n, Comparer<int>.Create(static (a, b) => b.CompareTo(a)));
        destination.Clear();
        for (int i = 0; i < n; i++)
            destination.Add(_sortIdsScratch[i]);
    }

    // Scratch для предвычисленных ключей сортировки FillPrioritizedUnclaimed.
    // Fill зовётся из одного sim-потока (GlobalRedistributePass) — гонки нет.
    // P1: _sortKeys/_sortIds оставлены для совместимости (EnsureCapacity/Clear
    // дёшевы), реальная сортировка идёт по scratch-массивам без ToArray.
    private readonly List<int> _sortKeys = new(512);
    private readonly List<int> _sortIds = new(512);
    private int[] _sortKeysScratch;
    private int[] _sortIdsScratch;

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

            // Finding 7: приоритет через категорию без GetPriorityForJobType
            // (switch + Volatile-read вместо двух вызовов на кандидата).
            int priority = JobPriorityManager.Instance.GetPriority(
                JobPriorityManager.Instance.GetCategory(_typeId[jobId]));
            if (priority <= 0)
                continue;

            // Задача недавно оказалась недостижимой — пропускаем.
            if (IsJobOnCooldown(jobId))
                continue;

            // То же per-dispatch memo, что в TryClaimForWorkerInChunk.
            bool canExec2;
            if (TryGetMemoCanExecute(jobId, out bool memo2))
            {
                canExec2 = memo2;
            }
            else
            {
                if (!JobRegistry.TryGetHandler(_typeId[jobId], out var handler2) ||
                    !handler2.CanAgentExecute(agentIndex, GetJobData(jobId), pool, ctx))
                {
                    StoreMemoCanExecute(jobId, false);
                    continue;
                }
                StoreMemoCanExecute(jobId, true);
                canExec2 = true;
            }
            if (!canExec2)
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
