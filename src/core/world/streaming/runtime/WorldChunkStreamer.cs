#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Неизменяемый envelope результата для передачи из worker-пула вызывающему потоку.
/// Snapshot.Payload — byte[] в существующем storage API; получателю следует обращаться
/// с ним как с read-only данными.
/// </summary>
public readonly record struct ChunkStreamResult(
    ChunkKey Key,
    WorldChunk Chunk,
    ChunkSnapshot? Snapshot);

/// <summary>Ошибка загрузки/генерации чанка, передаваемая только через DrainFailures.</summary>
public readonly record struct ChunkStreamFailure(ChunkKey Key, string Message);

/// <summary>
/// Асинхронно загружает delta и генерирует базовые чанки для desired window.
/// Не зависит от Godot API; все результаты извлекаются вызывающим потоком через drain.
/// </summary>
public sealed class WorldChunkStreamer : IAsyncDisposable
{
    public const int MaxBufferedChunks = 256;
    public const int MaxWorkerCount = 16;
    private readonly object _gate = new();
    private readonly ChunkWindowPlanner _planner;
    private readonly WorldChunkStore _store;
    private readonly ulong _worldSeed;
    private readonly uint _generatorVersion;
    private readonly int _maxConcurrency;
    private readonly Task[] _workers;

    private readonly HashSet<ChunkKey> _desired = new();
    // Один reservation покрывает ожидающую работу, активный worker, очередь результатов,
    // ошибки и уже переданный caller-owned resident. Освобождается только при drop/error drain/unload.
    private readonly HashSet<ChunkKey> _reserved = new();
    private readonly HashSet<ChunkKey> _pending = new();
    private readonly HashSet<ChunkKey> _inFlight = new();
    private readonly HashSet<ChunkKey> _readyKeys = new();
    private readonly HashSet<ChunkKey> _resident = new();
    private readonly HashSet<ChunkKey> _failureKeys = new();
    private readonly HashSet<ChunkKey> _failed = new();
    private readonly HashSet<ChunkKey> _unloading = new();
    private readonly Dictionary<ChunkKey, CancellationTokenSource> _requestCancellation = new();

    private PriorityQueue<ChunkKey, WorkPriority> _workQueue = new();
    private readonly Queue<ChunkStreamResult> _ready = new();
    private readonly Queue<ChunkStreamFailure> _failures = new();
    private readonly Queue<UnloadWork> _unloadQueue = new();

    // Pulse-all condition variable. Every waiter observes the same current TCS; a producer
    // swaps it under _gate before completing the old one, so work cannot be lost.
    private TaskCompletionSource<bool> _workSignal = NewSignal();
    private int _activeUnloadCount;
    private bool _hasVisibleBounds;
    private bool _disposeRequested;
    private Task? _disposeTask;

    public WorldChunkStreamer(
        ChunkWindowPlanner planner,
        ulong worldSeed,
        uint generatorVersion,
        WorldChunkStore store,
        int maxConcurrency = 4)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (maxConcurrency < 1 || maxConcurrency > MaxWorkerCount)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), maxConcurrency,
                $"Число workers должно быть в диапазоне 1..{MaxWorkerCount}.");

        _worldSeed = worldSeed;
        _generatorVersion = generatorVersion;
        _maxConcurrency = maxConcurrency;
        _workers = new Task[maxConcurrency];
        for (int i = 0; i < _workers.Length; i++)
            _workers[i] = Task.Run(WorkerLoopAsync);
    }

    public int MaxConcurrency => _maxConcurrency;

    /// <summary>Сколько ключей занимает общий hard-cap (включая отданные caller resident chunks).</summary>
    public int ReservedCount
    {
        get { lock (_gate) return _reserved.Count; }
    }

    public int ReadyCount
    {
        get { lock (_gate) return _ready.Count; }
    }

    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    public bool IsResident(ChunkKey key)
    {
        lock (_gate) return _resident.Contains(key);
    }

    public bool IsDesired(ChunkKey key)
    {
        lock (_gate) return _desired.Contains(key);
    }

    /// <summary>
    /// Применить новое видимое окно и пересобрать приоритет ожидающих работ.
    /// Exited resident chunks остаются зарезервированными до явного UnloadChunkAsync.
    /// </summary>
    public ChunkWindowPlan Update(WorldRect visibleTileBounds)
    {
        lock (_gate)
        {
            ThrowIfDisposing_NoLock();

            bool visibleBoundsChanged = !_hasVisibleBounds || _lastVisibleBounds != visibleTileBounds;
            ChunkWindowPlan plan = _planner.Update(visibleTileBounds);
            _desired.Clear();
            foreach (ChunkKey key in plan.Desired)
                _desired.Add(key);

            CancelStaleInFlight_NoLock();
            DropStaleReady_NoLock();
            DropStaleFailures_NoLock();
            _failed.RemoveWhere(key => !_desired.Contains(key));
            if (visibleBoundsChanged)
                RebuildPending_NoLock(visibleTileBounds);
            else
                FillPending_NoLock();
            if (_pending.Count > 0)
                PulseWorkers_NoLock();
            return plan;
        }
    }

    /// <summary>
    /// Передать до maxCount готовых результатов в caller-owned буфер. Вызывать из main/caller thread;
    /// извлечение переводит ключ в resident state и сохраняет его reservation до явной выгрузки.
    /// </summary>
    public int DrainReady(Span<ChunkStreamResult> destination, int maxCount)
    {
        if (maxCount < 0)
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        int limit = Math.Min(maxCount, destination.Length);
        if (limit == 0)
            return 0;

        lock (_gate)
        {
            int count = 0;
            while (count < limit && _ready.TryDequeue(out ChunkStreamResult result))
            {
                _readyKeys.Remove(result.Key);
                // Update удаляет stale результаты; повторная проверка защищает от interleaving.
                if (!_desired.Contains(result.Key))
                {
                    _reserved.Remove(result.Key);
                    continue;
                }

                _resident.Add(result.Key);
                destination[count++] = result;
            }
            return count;
        }
    }

    /// <summary>
    /// Передать ошибки caller thread. После drain ошибка больше не занимает result slot;
    /// такой ключ автоматически не ретраится до RetryFailed или выхода/повторного входа в окно.
    /// </summary>
    public int DrainFailures(Span<ChunkStreamFailure> destination, int maxCount)
    {
        if (maxCount < 0)
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        int limit = Math.Min(maxCount, destination.Length);
        if (limit == 0)
            return 0;

        lock (_gate)
        {
            int count = 0;
            while (count < limit && _failures.TryDequeue(out ChunkStreamFailure failure))
            {
                _failureKeys.Remove(failure.Key);
                _reserved.Remove(failure.Key);
                if (_desired.Contains(failure.Key))
                    _failed.Add(failure.Key);
                destination[count++] = failure;
            }
            FillPending_NoLock();
            if (_pending.Count > 0)
                PulseWorkers_NoLock();
            return count;
        }
    }

    /// <summary>Явно повторить последний завершившийся с ошибкой ключ, если он всё ещё desired.</summary>
    public bool RetryFailed(ChunkKey key)
    {
        lock (_gate)
        {
            ThrowIfDisposing_NoLock();
            if (!_desired.Contains(key) || !_failed.Remove(key))
                return false;
            FillPending_NoLock();
            if (_pending.Count > 0)
                PulseWorkers_NoLock();
            return true;
        }
    }

    /// <summary>
    /// Отменить ожидающие и активные generation/load запросы. Переданные caller resident chunks
    /// не удаляются и не сохраняются автоматически. Следующий Update снова наполнит актуальное окно.
    /// </summary>
    public void CancelPending()
    {
        lock (_gate)
        {
            ThrowIfDisposing_NoLock();
            _desired.Clear();

            foreach (ChunkKey key in _pending)
            {
                _reserved.Remove(key);
                CancelAndDisposeRequest_NoLock(key);
            }
            _pending.Clear();
            _workQueue.Clear();

            foreach (ChunkKey key in _inFlight)
            {
                if (_requestCancellation.TryGetValue(key, out CancellationTokenSource? cancellation))
                    cancellation.Cancel();
            }

            DropStaleReady_NoLock();
            DropStaleFailures_NoLock();
            _failed.Clear();
            PulseWorkers_NoLock();
        }
    }

    /// <summary>
    /// Lossless выгрузка ранее переданного resident чанка, который больше не входит в desired window.
    /// Запись/удаление delta выполняет ограниченный worker pool; reservation снимается только после
    /// успешного завершения store операции. Пустой payload передаётся SaveChunk как удаление старого delta.
    /// При насыщении лимита сохранений метод немедленно бросает InvalidOperationException — вызывающий
    /// должен дождаться предыдущих задач и повторить вызов (внутренняя очередь payload не растёт).
    /// </summary>
    public Task UnloadChunkAsync(
        ChunkKey key,
        ReadOnlyMemory<byte> deltaPayload,
        WorldSaveMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deltaPayload.Length > WorldChunkCodec.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(deltaPayload),
                $"Payload превышает лимит {WorldChunkCodec.MaxPayloadBytes} байт.");
        if (metadata.Seed != _worldSeed
            || metadata.GeneratorVersion != _generatorVersion
            || metadata.FeatureSchemaVersion != WorldFeatureGenerator.FeatureSchemaVersion)
            throw new ArgumentException("Metadata должна соответствовать seed, generator version и feature schema этого streamer.", nameof(metadata));

        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ThrowIfDisposing_NoLock();
            if (!_resident.Contains(key))
                throw new InvalidOperationException($"Чанк {key} не был передан вызывающему потоку как resident.");
            if (_desired.Contains(key))
                throw new InvalidOperationException($"Чанк {key} всё ещё входит в desired window.");
            if (!_unloading.Add(key))
                throw new InvalidOperationException($"Выгрузка чанка {key} уже выполняется.");
            if (_activeUnloadCount >= _maxConcurrency)
            {
                _unloading.Remove(key);
                throw new InvalidOperationException("Очередь lossless-выгрузки заполнена; дождитесь предыдущих UnloadChunkAsync.");
            }
            _activeUnloadCount++;
        }

        byte[] stablePayload;
        try
        {
            stablePayload = deltaPayload.IsEmpty ? Array.Empty<byte>() : deltaPayload.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            RollbackUnloadAdmission(key);
            throw;
        }

        lock (_gate)
        {
            // Запрос принят до dispose: сохраняем его, DisposeAsync дождётся store операции.
            _unloadQueue.Enqueue(new UnloadWork(key, stablePayload, metadata, cancellationToken, completion));
            PulseWorkers_NoLock();
        }
        return completion.Task;
    }

    /// <summary>
    /// Остановить приём работ, удалить ожидающие/непереданные результаты и дождаться активных workers.
    /// Уже принятые UnloadChunkAsync завершаются до выхода. Перед DisposeAsync вызывающая сторона обязана
    /// явно выгрузить resident chunks, чьи изменения должны пережить закрытие этого coordinator.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask != null)
                return new ValueTask(_disposeTask);

            _disposeRequested = true;
            foreach (ChunkKey key in _pending)
            {
                _reserved.Remove(key);
                CancelAndDisposeRequest_NoLock(key);
            }
            _pending.Clear();
            _workQueue.Clear();
            foreach (ChunkKey key in _inFlight)
            {
                if (_requestCancellation.TryGetValue(key, out CancellationTokenSource? cancellation))
                    cancellation.Cancel();
            }

            while (_ready.TryDequeue(out ChunkStreamResult result))
            {
                _readyKeys.Remove(result.Key);
                _reserved.Remove(result.Key);
            }
            while (_failures.TryDequeue(out ChunkStreamFailure failure))
            {
                _failureKeys.Remove(failure.Key);
                _reserved.Remove(failure.Key);
            }
            _failed.Clear();
            _desired.Clear();
            // Resident results принадлежат caller; этот coordinator не удаляет их state/store.
            _resident.Clear();
            _reserved.Clear();

            PulseWorkers_NoLock();
            _disposeTask = Task.WhenAll(_workers);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            ChunkKey key = default;
            CancellationToken requestToken = default;
            UnloadWork? unload = null;
            Task? waitTask = null;

            lock (_gate)
            {
                if (_unloadQueue.TryDequeue(out UnloadWork? unloadWork))
                {
                    unload = unloadWork;
                }
                else if (_disposeRequested)
                {
                    if (_activeUnloadCount == 0)
                        return;
                    waitTask = _workSignal.Task;
                }
                else if (_workQueue.TryDequeue(out key, out _))
                {
                    if (!_pending.Remove(key) || !_desired.Contains(key) || !_reserved.Contains(key))
                        continue;
                    _inFlight.Add(key);
                    if (_requestCancellation.TryGetValue(key, out CancellationTokenSource? cancellation))
                        requestToken = cancellation.Token;
                }
                else
                {
                    waitTask = _workSignal.Task;
                }
            }

            if (waitTask != null)
            {
                await waitTask.ConfigureAwait(false);
                continue;
            }

            if (unload != null)
            {
                ProcessUnload(unload);
                continue;
            }

            ProcessChunk(key, requestToken);
        }
    }

    private void ProcessChunk(ChunkKey key, CancellationToken requestToken)
    {
        try
        {
            requestToken.ThrowIfCancellationRequested();
            if (!IsWanted(key))
            {
                CompleteDiscardedChunk(key);
                return;
            }

            ChunkSnapshot? snapshot = null;
            if (_store.ContainsChunk(key.X, key.Y))
            {
                if (!_store.TryLoadChunk(key.X, key.Y, out ChunkSnapshot loadedSnapshot, out string? error))
                    throw new InvalidDataException(error ?? $"Не удалось загрузить delta чанка {key}.");

                if (loadedSnapshot.Metadata.Seed != _worldSeed
                    || loadedSnapshot.Metadata.GeneratorVersion != _generatorVersion
                    || loadedSnapshot.Metadata.FeatureSchemaVersion != WorldFeatureGenerator.FeatureSchemaVersion)
                    throw new InvalidDataException($"Delta чанка {key} относится к несовместимому seed/generator/feature schema.");
                snapshot = loadedSnapshot;
            }

            requestToken.ThrowIfCancellationRequested();
            if (!IsWanted(key))
            {
                CompleteDiscardedChunk(key);
                return;
            }

            // Генератор синхронный: используем MapGenerator.RegionAPI для единого террейна
            // вместо старого Value-noise WorldChunkGenerator (§24-§33).
            var chunk = LoadChunkViaRegionApi(key);
            requestToken.ThrowIfCancellationRequested();
            CompleteChunk(key, chunk, snapshot);
        }
        catch (OperationCanceledException)
        {
            CompleteCanceledChunk(key);
        }
        catch (Exception ex)
        {
            if (requestToken.IsCancellationRequested)
                CompleteCanceledChunk(key);
            else
                CompleteChunkFailure(key, ex);
        }
    }

    /// <summary>
    /// Загружает базовый чанк через единый конвейер WorldLayerStack (план §3.1):
    /// каждый слой — чистая функция от (seed, generatorVersion, АБСОЛЮТНЫЕ x, y).
    /// Никакого ±1 bbox-хака и adjustedSeed от HashCoords: соседние чанки стыкуются
    /// байт-в-байт, детерминизм не зависит от порядка загрузки.
    /// Гидрология (L3) выполняется на регион-окне с фиксированным маргином и кэшируется
    /// по RegionKey ⇒ сток/реки/озёра непрерывны через границы чанков.
    /// </summary>
    private WorldChunk LoadChunkViaRegionApi(ChunkKey key)
    {
        var cells = new GeneratedCell[WorldChunk.CellCount];
        long baseX = key.X * WorldChunk.Side;
        long baseY = key.Y * WorldChunk.Side;

        for (int localY = 0; localY < WorldChunk.Side; localY++)
        {
            long wy = baseY + localY;
            for (int localX = 0; localX < WorldChunk.Side; localX++)
            {
                long wx = baseX + localX;
                var c = Layers.WorldLayerStack.SampleChunkCell(_worldSeed, _generatorVersion, wx, wy);
                cells[localY * WorldChunk.Side + localX] = new GeneratedCell(
                    c.Terrain, c.ElevationQ16, c.MoistureQ16, c.ForestQ16, c.StoneQ16, c.TemperatureQ16);
            }
        }

        return new WorldChunk(key, cells);
    }

    private void ProcessUnload(UnloadWork work)
    {
        try
        {
            work.CancellationToken.ThrowIfCancellationRequested();
            // SaveChunk(empty) атомарно удаляет прежнюю delta запись и не создаёт файл.
            _store.SaveChunk(work.Metadata, work.Key.X, work.Key.Y, work.Payload);
            CompleteUnload(work, error: null, canceled: false);
        }
        catch (OperationCanceledException)
        {
            CompleteUnload(work, error: null, canceled: true);
        }
        catch (Exception ex)
        {
            CompleteUnload(work, error: ex, canceled: false);
        }
    }

    private bool IsWanted(ChunkKey key)
    {
        lock (_gate)
            return !_disposeRequested && _desired.Contains(key);
    }

    private void CompleteChunk(ChunkKey key, WorldChunk chunk, ChunkSnapshot? snapshot)
    {
        lock (_gate)
        {
            _inFlight.Remove(key);
            DisposeRequest_NoLock(key);
            if (_disposeRequested || !_desired.Contains(key))
            {
                _reserved.Remove(key);
                FillPending_NoLock();
                if (_pending.Count > 0)
                    PulseWorkers_NoLock();
                return;
            }

            _ready.Enqueue(new ChunkStreamResult(key, chunk, snapshot));
            _readyKeys.Add(key);
        }
    }

    private void CompleteDiscardedChunk(ChunkKey key)
    {
        lock (_gate)
        {
            _inFlight.Remove(key);
            DisposeRequest_NoLock(key);
            _reserved.Remove(key);
            FillPending_NoLock();
            if (_pending.Count > 0)
                PulseWorkers_NoLock();
        }
    }

    private void CompleteCanceledChunk(ChunkKey key)
    {
        lock (_gate)
        {
            _inFlight.Remove(key);
            DisposeRequest_NoLock(key);
            _reserved.Remove(key);
            FillPending_NoLock();
            if (_pending.Count > 0)
                PulseWorkers_NoLock();
        }
    }

    private void CompleteChunkFailure(ChunkKey key, Exception exception)
    {
        lock (_gate)
        {
            _inFlight.Remove(key);
            DisposeRequest_NoLock(key);
            if (_disposeRequested || !_desired.Contains(key))
            {
                _reserved.Remove(key);
            }
            else
            {
                _failureKeys.Add(key);
                _failures.Enqueue(new ChunkStreamFailure(key, exception.ToString()));
            }
            FillPending_NoLock();
            if (_pending.Count > 0)
                PulseWorkers_NoLock();
        }
    }

    private void CompleteUnload(UnloadWork work, Exception? error, bool canceled)
    {
        lock (_gate)
        {
            _activeUnloadCount--;
            _unloading.Remove(work.Key);
            if (error == null && !canceled)
            {
                _resident.Remove(work.Key);
                _reserved.Remove(work.Key);
            }
            FillPending_NoLock();
            PulseWorkers_NoLock();
        }

        if (canceled)
            work.Completion.TrySetCanceled(work.CancellationToken);
        else if (error != null)
            work.Completion.TrySetException(error);
        else
            work.Completion.TrySetResult(true);
    }

    private void CancelStaleInFlight_NoLock()
    {
        foreach (ChunkKey key in _inFlight)
        {
            if (!_desired.Contains(key)
                && _requestCancellation.TryGetValue(key, out CancellationTokenSource? cancellation))
                cancellation.Cancel();
        }
    }

    private void CancelAndDisposeRequest_NoLock(ChunkKey key)
    {
        if (!_requestCancellation.Remove(key, out CancellationTokenSource? cancellation))
            return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void DisposeRequest_NoLock(ChunkKey key)
    {
        if (_requestCancellation.Remove(key, out CancellationTokenSource? cancellation))
            cancellation.Dispose();
    }

    private void RollbackUnloadAdmission(ChunkKey key)
    {
        lock (_gate)
        {
            _unloading.Remove(key);
            _activeUnloadCount--;
            if (_disposeRequested)
                PulseWorkers_NoLock();
        }
    }

    private void DropStaleReady_NoLock()
    {
        if (_ready.Count == 0)
            return;
        var retained = new Queue<ChunkStreamResult>(_ready.Count);
        while (_ready.TryDequeue(out ChunkStreamResult result))
        {
            if (_desired.Contains(result.Key))
            {
                retained.Enqueue(result);
            }
            else
            {
                _readyKeys.Remove(result.Key);
                _reserved.Remove(result.Key);
            }
        }
        while (retained.TryDequeue(out ChunkStreamResult result))
            _ready.Enqueue(result);
    }

    private void DropStaleFailures_NoLock()
    {
        if (_failures.Count == 0)
            return;
        var retained = new Queue<ChunkStreamFailure>(_failures.Count);
        while (_failures.TryDequeue(out ChunkStreamFailure failure))
        {
            if (_desired.Contains(failure.Key))
            {
                retained.Enqueue(failure);
            }
            else
            {
                _failureKeys.Remove(failure.Key);
                _reserved.Remove(failure.Key);
            }
        }
        while (retained.TryDequeue(out ChunkStreamFailure failure))
            _failures.Enqueue(failure);
    }

    private void RebuildPending_NoLock(WorldRect visibleBounds)
    {
        _lastVisibleBounds = visibleBounds;
        _hasVisibleBounds = true;
        ChunkKey[] oldPending = new ChunkKey[_pending.Count];
        _pending.CopyTo(oldPending);
        _pending.Clear();
        _workQueue.Clear();

        foreach (ChunkKey key in oldPending)
        {
            if (_desired.Contains(key) && _reserved.Contains(key))
            {
                _pending.Add(key);
                _workQueue.Enqueue(key, PriorityFor(key, visibleBounds));
            }
            else
            {
                _reserved.Remove(key);
                CancelAndDisposeRequest_NoLock(key);
            }
        }

        // Fill any slots freed by exited work, retaining tokens for pending keys that
        // stayed desired while merely refreshing their camera-distance priority.
        FillPending_NoLock();
    }

    private void FillPending_NoLock()
    {
        // Called after drain/failure/unload. The most recent Update already established priorities.
        if (_desired.Count == 0 || _disposeRequested)
            return;
        foreach (ChunkKey key in OrderedDesiredByDistance_NoLock(_lastVisibleBounds))
        {
            if (_reserved.Count >= MaxBufferedChunks)
                break;
            if (IsAlreadyTracked_NoLock(key))
                continue;
            _reserved.Add(key);
            _pending.Add(key);
            _requestCancellation[key] = new CancellationTokenSource();
            _workQueue.Enqueue(key, PriorityFor(key, _lastVisibleBounds));
        }
    }

    private WorldRect _lastVisibleBounds;

    private bool IsAlreadyTracked_NoLock(ChunkKey key)
        => _reserved.Contains(key)
            || _inFlight.Contains(key)
            || _readyKeys.Contains(key)
            || _resident.Contains(key)
            || _failureKeys.Contains(key)
            || _failed.Contains(key)
            || _unloading.Contains(key);

    private IEnumerable<ChunkKey> OrderedDesiredByDistance_NoLock(WorldRect visibleBounds)
    {
        var ordered = new List<(ChunkKey Key, WorkPriority Priority)>(_desired.Count);
        foreach (ChunkKey key in _desired)
            ordered.Add((key, PriorityFor(key, visibleBounds)));
        ordered.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));
        foreach (var item in ordered)
            yield return item.Key;
    }

    private static WorkPriority PriorityFor(ChunkKey key, WorldRect visibleBounds)
    {
        long minX = WorldCoordinates.FloorDiv(visibleBounds.MinX, WorldCoordinates.ChunkSize);
        long minY = WorldCoordinates.FloorDiv(visibleBounds.MinY, WorldCoordinates.ChunkSize);
        long maxX = WorldCoordinates.FloorDiv(checked(visibleBounds.MaxX - 1), WorldCoordinates.ChunkSize);
        long maxY = WorldCoordinates.FloorDiv(checked(visibleBounds.MaxY - 1), WorldCoordinates.ChunkSize);
        long dx = key.X < minX ? checked(minX - key.X) : key.X > maxX ? checked(key.X - maxX) : 0;
        long dy = key.Y < minY ? checked(minY - key.Y) : key.Y > maxY ? checked(key.Y - maxY) : 0;
        return new WorkPriority(checked(dx + dy), key.Y, key.X);
    }

    private void ThrowIfDisposing_NoLock()
    {
        if (_disposeRequested)
            throw new ObjectDisposedException(nameof(WorldChunkStreamer));
    }

    private void PulseWorkers_NoLock()
    {
        TaskCompletionSource<bool> previous = _workSignal;
        _workSignal = NewSignal();
        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class UnloadWork
    {
        public ChunkKey Key { get; }
        public byte[] Payload { get; }
        public WorldSaveMetadata Metadata { get; }
        public CancellationToken CancellationToken { get; }
        public TaskCompletionSource<bool> Completion { get; }

        public UnloadWork(ChunkKey key, byte[] payload, WorldSaveMetadata metadata,
            CancellationToken cancellationToken, TaskCompletionSource<bool> completion)
        {
            Key = key;
            Payload = payload;
            Metadata = metadata;
            CancellationToken = cancellationToken;
            Completion = completion;
        }
    }

    private readonly record struct WorkPriority(long Distance, long Y, long X) : IComparable<WorkPriority>
    {
        public int CompareTo(WorkPriority other)
        {
            int distance = Distance.CompareTo(other.Distance);
            if (distance != 0)
                return distance;
            int y = Y.CompareTo(other.Y);
            return y != 0 ? y : X.CompareTo(other.X);
        }
    }
}
