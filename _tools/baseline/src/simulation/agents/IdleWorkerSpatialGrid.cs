using System;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Безопасный и сверхбыстрый O(1) пространственный индекс свободных агентов (Idle).
/// </summary>
public sealed class IdleWorkerSpatialGrid
{
    private const int ChunkShift = 4; // 16x16 тайлов на чанк
    private const int ChunkDim = 32;   // 32x32 чанка
    private const int MaxPerChunkInspect = 32;

    private readonly int[] _chunkHeads = new int[ChunkDim * ChunkDim];
    private bool[] _inGrid;
    private int[] _agentChunk;
    // P-баланс: striped locks вместо одного глобального _lock. Bookkeep-фаза
    // дёргает UpdateWorkerChunk на каждую смену клетки, Dispatcher — Add/Remove.
    // Шард — ПО ЧАНКУ (StripeOf), а не по агенту: Add/Remove/Move мутируют общий
    // _chunkHeads[chunkIndex] + Next/Prev соседей — два агента в одном чанке под
    // разными шардами давали lost-update головы списка. Шард по чанку сериализует
    // всех писателей одного чанка между собой; агенты разных чанков идут параллельно.
    // Remove под шардом чанка безопасен: MoveLocked перепроверяет _agentChunk под
    // lock и выходит, если чанк уже сменился (см. MoveLocked).
    private const int StripeCount = 32;
    private readonly object[] _stripes = CreateStripes();
    private static object[] CreateStripes()
    {
        var s = new object[StripeCount];
        for (int i = 0; i < s.Length; i++) s[i] = new object();
        return s;
    }
    private static int StripeOf(int chunkIndex) => (chunkIndex & (StripeCount - 1));
    // Ресайз массивов — под отдельным lock (редко, только рост пула).
    private readonly object _resizeLock = new();
    private int _totalIdleCount = 0;
    private int _lastChunkScanIndex = 0;

    public int TotalIdleCount => Volatile.Read(ref _totalIdleCount);

    public IdleWorkerSpatialGrid()
    {
        Array.Fill(_chunkHeads, -1);
    }

    private void EnsureCapacity(int capacity)
    {
        if (_inGrid == null || _inGrid.Length < capacity)
        {
            Array.Resize(ref _inGrid, capacity);
            int oldLen = _agentChunk != null ? _agentChunk.Length : 0;
            Array.Resize(ref _agentChunk, capacity);
            for (int i = oldLen; i < capacity; i++)
            {
                _agentChunk[i] = -1;
            }
        }
    }

    private static int ComputeChunkIndex(int cellX, int cellY)
    {
        int cx = Math.Clamp(cellX >> ChunkShift, 0, ChunkDim - 1);
        int cy = Math.Clamp(cellY >> ChunkShift, 0, ChunkDim - 1);
        return cy * ChunkDim + cx;
    }

    public void AddIdleWorker(int agentIndex, AgentDataPool pool)
    {
        EnsureCapacityLocked(pool.Capacity);
        if (Volatile.Read(ref _inGrid[agentIndex]))
            return;

        int cx = Math.Clamp(pool.CurrentCellX[agentIndex] >> ChunkShift, 0, ChunkDim - 1);
        int cy = Math.Clamp(pool.CurrentCellY[agentIndex] >> ChunkShift, 0, ChunkDim - 1);
        int chunkIndex = cy * ChunkDim + cx;

        // Шард по чанку: все писатели одной головы списка сериализованы.
        lock (_stripes[StripeOf(chunkIndex)])
        {
            if (_inGrid[agentIndex])
                return;
            _inGrid[agentIndex] = true;
            _agentChunk[agentIndex] = chunkIndex;

            int oldHead = _chunkHeads[chunkIndex];
            pool.NextInIdleCell[agentIndex] = oldHead;
            pool.PrevInIdleCell[agentIndex] = -1;

            if (oldHead != -1)
            {
                pool.PrevInIdleCell[oldHead] = agentIndex;
            }

            _chunkHeads[chunkIndex] = agentIndex;
            Interlocked.Increment(ref _totalIdleCount);
        }
    }

    public void RemoveIdleWorker(int agentIndex, AgentDataPool pool)
    {
        EnsureCapacityLocked(pool.Capacity);
        if (agentIndex < 0 || agentIndex >= _inGrid.Length)
            return;
        // Шард по чанку снятия (hint может устареть — Move мог пересадить).
        // Если под lock чанк сменился — отпускаем stripe и повторяем по
        // актуальному чанку (максимум несколько итераций при активной
        // миграции агента). Раньше был early-return «до следующей попытки»
        // диспетчера — он терял один проход и рассинхронизировал
        // TotalIdleCount (collect видит агента, claim пропускает по State).
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int chunkHint = _agentChunk != null && agentIndex < _agentChunk.Length
                ? _agentChunk[agentIndex] : -1;
            if (chunkHint < 0)
            {
                // Агент уже снят (Move не ставит -1, только RemoveLocked) —
                // либо никогда не был в сетке.
                lock (_stripes[(agentIndex & (StripeCount - 1))])
                {
                    if (!_inGrid[agentIndex])
                        return;
                    chunkHint = _agentChunk[agentIndex];
                    if (chunkHint < 0)
                    {
                        // In-grid без чанка: битое состояние, чиним счётчик.
                        _inGrid[agentIndex] = false;
                        pool.NextInIdleCell[agentIndex] = -1;
                        pool.PrevInIdleCell[agentIndex] = -1;
                        Interlocked.Decrement(ref _totalIdleCount);
                        return;
                    }
                    // Чанк появился — повторяем по нормальному пути.
                    continue;
                }
            }
            int stripeHint = StripeOf(chunkHint);
            lock (_stripes[stripeHint])
            {
                if (!_inGrid[agentIndex])
                    return;

                int chunkIndex = _agentChunk[agentIndex];
                if (chunkIndex != chunkHint)
                    continue; // Move пересадил под другим шардом — retry по новому чанку.
                RemoveLocked(agentIndex, pool, chunkIndex);
                return;
            }
        }
        // #4: за 4 попытки не сошлось (агент мигрировал между чанками быстрее,
        // чем мы брали шард). Раньше здесь был тихий no-op: агент уже в Working,
        // но _inGrid=true и _totalIdleCount завышен навсегда (дрейф счётчика),
        // а повторный AddIdleWorker дедуплицировался по _inGrid и терял агента.
        // Fallback: берём ВСЕ шарды по порядку (младший→старший, как в
        // UpdateWorkerChunk — дедлока нет) и вырезаем агента из актуального
        // чанка принудительно. Редкий путь (только после 4 гонок подряд).
        ForceRemoveLocked(agentIndex, pool);
    }

    /// <summary>
    /// #4: принудительное снятие после 4 неудачных попыток RemoveIdleWorker.
    /// Берёт все шарды по порядку (упорядоченно — дедлока нет, тот же порядок,
    /// что в UpdateWorkerChunk), блокируя конкурентные Move/Add на время
    /// операции. Ищет агента в списке его актуального чанка по обходу
    /// (защита от рассинхрона головы) и вырезает. _totalIdleCount правится
    /// ровно один раз (только если _inGrid был true).
    /// </summary>
    private void ForceRemoveLocked(int agentIndex, AgentDataPool pool)
    {
        // Вложенные lock по порядку шардов — тот же паттерн, что в
        // UpdateWorkerChunk (два шарда ordered). Здесь — все 32: рекурсивный
        // захват через цикл невозможен для lock(), поэтому берём их
        // последовательно через Monitor с гарантированным освобождением.
        for (int s = 0; s < StripeCount; s++)
            System.Threading.Monitor.Enter(_stripes[s]);
        try
        {
            if (!_inGrid[agentIndex])
                return;
            int chunkIndex = _agentChunk[agentIndex];
            if (chunkIndex >= 0 && chunkIndex < _chunkHeads.Length)
            {
                // Ищем агента в списке чанка обходом (голова могла уплыть
                // под гонкой Move — unlink по next/prev всё равно корректен,
                // если агент действительно в этом списке).
                int curr = _chunkHeads[chunkIndex];
                bool found = false;
                while (curr != -1)
                {
                    if (curr == agentIndex) { found = true; break; }
                    curr = (curr >= 0 && curr < pool.Capacity) ? pool.NextInIdleCell[curr] : -1;
                }
                if (found)
                {
                    RemoveLocked(agentIndex, pool, chunkIndex);
                    return;
                }
                // Агент не в списке своего чанка (голова рассинхронизирована
                // гонкой): чистим флаги и счётчик, чтобы не дрейфовал.
                _inGrid[agentIndex] = false;
                _agentChunk[agentIndex] = -1;
                pool.NextInIdleCell[agentIndex] = -1;
                pool.PrevInIdleCell[agentIndex] = -1;
                Interlocked.Decrement(ref _totalIdleCount);
                return;
            }
            // Чанк вне диапазона — битое состояние, чиним счётчик.
            _inGrid[agentIndex] = false;
            _agentChunk[agentIndex] = -1;
            pool.NextInIdleCell[agentIndex] = -1;
            pool.PrevInIdleCell[agentIndex] = -1;
            Interlocked.Decrement(ref _totalIdleCount);
        }
        finally
        {
            for (int s = StripeCount - 1; s >= 0; s--)
                System.Threading.Monitor.Exit(_stripes[s]);
        }
    }

    private void RemoveLocked(int agentIndex, AgentDataPool pool, int chunkIndex)
    {
            int next = pool.NextInIdleCell[agentIndex];
            int prev = pool.PrevInIdleCell[agentIndex];

            if (prev != -1)
            {
                pool.NextInIdleCell[prev] = next;
            }
            else
            {
                if (chunkIndex >= 0 && chunkIndex < _chunkHeads.Length && _chunkHeads[chunkIndex] == agentIndex)
                {
                    _chunkHeads[chunkIndex] = next;
                }
            }

            if (next != -1)
            {
                pool.PrevInIdleCell[next] = prev;
            }

            _inGrid[agentIndex] = false;
            _agentChunk[agentIndex] = -1;
            pool.NextInIdleCell[agentIndex] = -1;
            pool.PrevInIdleCell[agentIndex] = -1;
            Interlocked.Decrement(ref _totalIdleCount);
    }

    private void EnsureCapacityLocked(int capacity)
    {
        if (_inGrid != null && _inGrid.Length >= capacity)
            return;
        lock (_resizeLock)
        {
            EnsureCapacity(capacity);
        }
    }

    /// <summary>
    /// Пересаживает уже зарегистрированного в сетке бездельника в новый чанк,
    /// если он переместился между клетками с момента регистрации.
    /// Вызывается из фазы bookkeeping в фоновом потоке симуляции.
    /// </summary>
    public void UpdateWorkerChunk(int agentIndex, AgentDataPool pool)
    {
        if (agentIndex < 0) return;
        if (_inGrid == null || agentIndex >= _inGrid.Length || !_inGrid[agentIndex])
            return;

        int newChunk = ComputeChunkIndex(pool.CurrentCellX[agentIndex], pool.CurrentCellY[agentIndex]);
        int curChunk = _agentChunk[agentIndex];
        if (newChunk == curChunk)
            return;

        // Переезд трогает ДВЕ головы (_chunkHeads[curChunk] и [newChunk]) —
        // берём оба шарда ordered (младший→старший, дедлока нет). MoveLocked
        // перепроверяет _agentChunk под lock.
        int s1 = StripeOf(curChunk);
        int s2 = StripeOf(newChunk);
        if (s1 == s2)
        {
            lock (_stripes[s1])
            {
                MoveLocked(agentIndex, pool, curChunk, newChunk);
            }
        }
        else if (s1 < s2)
        {
            lock (_stripes[s1])
            {
                lock (_stripes[s2])
                {
                    MoveLocked(agentIndex, pool, curChunk, newChunk);
                }
            }
        }
        else
        {
            lock (_stripes[s2])
            {
                lock (_stripes[s1])
                {
                    MoveLocked(agentIndex, pool, curChunk, newChunk);
                }
            }
        }
    }

    private void MoveLocked(int agentIndex, AgentDataPool pool, int curChunk, int newChunk)
    {
        // Перепроверка под lock: агент могли снять/переместить конкурентом.
        if (!_inGrid[agentIndex] || _agentChunk[agentIndex] != curChunk)
            return;
        // Снять со старого чанка
        int next = pool.NextInIdleCell[agentIndex];
        int prev = pool.PrevInIdleCell[agentIndex];
        if (prev != -1)
        {
            pool.NextInIdleCell[prev] = next;
        }
        else if (_chunkHeads[curChunk] == agentIndex)
        {
            _chunkHeads[curChunk] = next;
        }
        if (next != -1)
        {
            pool.PrevInIdleCell[next] = prev;
        }
        pool.NextInIdleCell[agentIndex] = -1;
        pool.PrevInIdleCell[agentIndex] = -1;

        // Вставить в новый чанк
        _agentChunk[agentIndex] = newChunk;
        int oldHead = _chunkHeads[newChunk];
        pool.NextInIdleCell[agentIndex] = oldHead;
        pool.PrevInIdleCell[agentIndex] = -1;
        if (oldHead != -1)
        {
            pool.PrevInIdleCell[oldHead] = agentIndex;
        }
        _chunkHeads[newChunk] = agentIndex;
    }

    /// <summary>
    /// Lock-free сбор свободных рабочих из указанного чанка.
    /// Читает односвязный список без lock'а — консистентность для симуляции достаточна.
    /// </summary>
    public int CollectIdleWorkersInChunk(int chunkIndex, int maxCount, int[] destination, AgentDataPool pool)
    {
        if (chunkIndex < 0 || chunkIndex >= _chunkHeads.Length || maxCount <= 0)
            return 0;

        int collected = 0;
        int curr = Volatile.Read(ref _chunkHeads[chunkIndex]);

        while (curr != -1 && collected < maxCount)
        {
            if (curr < pool.Capacity && pool.States[curr] == AgentState.Idle)
            {
                destination[collected++] = curr;
            }
            curr = Volatile.Read(ref pool.NextInIdleCell[curr]);
        }

        return collected;
    }

    /// <summary>
    /// Lock-free сбор свободных рабочих round-robin по чанкам (без глобального lock'а).
    /// </summary>
    public int CollectIdleWorkers(int maxCount, int[] destination, AgentDataPool pool)
    {
        if (destination == null || destination.Length < maxCount)
            return 0;

        if (Volatile.Read(ref _totalIdleCount) <= 0)
            return 0;

        int collected = 0;
        int totalChunks = _chunkHeads.Length;

        for (int offset = 0; offset < totalChunks && collected < maxCount; offset++)
        {
            int chunkIdx = (_lastChunkScanIndex + offset) % totalChunks;
            int curr = Volatile.Read(ref _chunkHeads[chunkIdx]);

            while (curr != -1 && collected < maxCount)
            {
                if (curr < pool.Capacity && pool.States[curr] == AgentState.Idle)
                {
                    destination[collected++] = curr;
                }
                curr = Volatile.Read(ref pool.NextInIdleCell[curr]);
            }
        }

        _lastChunkScanIndex = (_lastChunkScanIndex + 17) % totalChunks;
        return collected;
    }
}