# PLAN.md — Динамическая балансировка нагрузки между ядрами (straggler / long-tail mitigation)

> Godot 4.7 / C# / .NET 8. Язык плана — русский. Код проекта НЕ менялся, это только проектный документ для `@developer`.
> Источник истины — `src/`. `project_code.txt` устарел, игнорировать (см. `AI_PROJECT_MAP.md`).

---

## 0. Аудит текущего состояния (что найдено в корне `C:\s\bebebe-main`)

### 0.1. Тяжёлые вычисления (кандидаты на балансировку)

| Место | Файл | Характер нагрузки | Равномерность |
|---|---|---|---|
| `Phase2_ParallelUpdate` (движение + `ExecuteParallel` хендлеров) | `src/simulation/AgentSimulationThread.cs:353-375` | `MoveTowards` → `HierarchicalPathfinder.TryFindPath` (A* регионы + A* окно до 2304 кл. + `SmoothPath` с Брезенхемом), `FlowFieldManager.CalculateLocalDetourDirection` (BFS окно 33×33, до 100 шагов) | **Сильно неравномерная**: 95% агентов — дешёвое прямое движение, 5% — дорогая постройка пути. Классический long-tail |
| `Phase3a_ParallelBookkeeping` (needs + cell-tracking + `TryAssignNeedsBehavior`) | `AgentSimulationThread.cs:377-511` | `NeedsJobSystem.TryStartSeekingFood` — скан `GroundItemManager` под `lock` (раньше thundering herd, частично полечено радиусом 3 чанка); `IdleWorkers.UpdateWorkerChunk` — striped locks | Неравномерная при массовом голоде (все 10k в один тик) |
| `Phase3b_SequentialCommit` → теперь параллельный Commit | `AgentSimulationThread.cs:513-559` | `handler.Commit` → `TakeItems/SpawnItems/Release` под `lock` менеджеров; `ReleaseJobWorker` | Неравномерная: агент с контендящимся `lock` сталлит батч |
| `JobDispatcher.DispatchPendingJobs` (chunk-проход) | `src/simulation/jobs/JobDispatcher.cs:63-126` | `DispatchChunk` → `TryClaimForWorkerInChunk` (CAS) + `handler.OnStart` → `TryReserve` под `lock` (Ground/Stockpile) | **Сильно неравномерная**: чанк с «тяжёлыми» резервами держит поток |
| `SpillOverPass` + `GlobalRedistributePass` | `JobDispatcher.cs:244-458` | `FillPrioritizedUnclaimed` O(N) + Sort O(N log N) + O(W×J) claim-сканы, однопоточно | Серийное узкое место, троттлинг 2с уже есть — не параллелить без редизайна, только бюджет |
| `CropGrowthManager.UpdateGrowth` | `src/simulation/farming/CropGrowthManager.cs:61-101` | O(N) итерация словаря под `lock` + `RegisterHarvest` вне lock | Равномерная, лёгкая — балансировщик НЕ нужен, оставить serial |
| `GroundItemManager.GenerateSnapshot` / `CropGrowthManager.GenerateSnapshot` | снапшоты 30 Гц | Копия под коротким lock + построение буфера вне секции | Равномерная — не трогать |
| `AgentSpatialGrid.Clear/Insert` (Rebuild) | `src/simulation/agents/AgentSpatialGrid.cs` | O(N) вставка в `_cellHeads` (общие массивы) | **Непараллелизуемо как есть** (гонка) — оставить sequential, это дёшево |
| `MapGenerator.Generate` + заливка 8 слоёв | `src/ui/renderers/MapRenderer.cs`, `src/core/world/MapGenerator.cs` | fBm 4–5 октав на 512×512 + Task-заливка | Одноразовый старт — вне скоупа per-tick балансировщика |

### 0.2. Что уже сделано (не ломать!)

- `src/simulation/DynamicWorkBalancer.cs` — **уже есть**: центральный атомарный курсор (`Interlocked.Add` на батч), адаптивный размер батча 32..512 через EMA времени батча (`HeavyBatchMs=1.5мс`, `AdaptRate=0.25`), `ThreadLocal<ThreadState>`, публикация `GameProfiler.RecordPhaseBalance(wallMs, cpuMs)` один раз на фазу. Используется в Phase2 / Phase3a / Phase3b-commit / Dispatcher chunk-проходе (`ForEach` + `ForEachRange`). `Reset()` при смене скорости.
- `HierarchicalPathfinder` — кэши уже `ConcurrentDictionary` (бывший `_cacheLock`-straggler устранён), scratch-буферы `ThreadLocal`.
- `IdleWorkerSpatialGrid` — striped locks (32 шарда) вместо глобального.
- `ParallelRng` — `[ThreadStatic]` XorShift, без `Random.Shared` contention.
- `FlowFieldManager` — без `GameProfiler.Scope` в горячем пути, ранний выход ≤5 тайлов без BFS.
- Менеджеры (`Ground/Stockpile/Tree/Farm/Blueprint`) — события в главный поток только через `Callable.From(...).CallDeferred()`, состояние под `lock`/`Interlocked`/`Volatile`.

### 0.3. Single-thread узкие места, оставшиеся после P-оптимизаций

1. **Центральный курсор `DynamicWorkBalancer` — это chunk-partitioning, а не настоящий work-stealing.** Балансирует хорошо, пока cost одного элемента ≤ ~0.1 мс. Если **один агент** внутри батча залипает (BFS 33×33 + `ComputeLocalSegment` с `Array.Fill` 2304 + `new List<int>(64)` + `SmoothPath → result.ToArray()`), весь батч 32–128 встаёт, а курсор не умеет **прервать и разбить** уже взятый батч — хвост фазы = самый медленный батч (Amdahl-хвост).
2. **`Parallel.For(0, maxDegree, ...)` на каждую фазу, 3–8 раз на шаг, до 8 шагов на тик.** Overhead создания/планирования задач ThreadPool на каждый вызов (десятки–сотни мкс) + конкуренция с Godot-пулом. Нет persistent workers.
3. **Нет оценки cost заранее.** Диспетчер знает, что чанки с работами неравноценны, но режет диапазон линейно (`ChunksPerBatch`), а не по весу.
4. **Нет измерения перегруза ядра отдельно от фазы.** `RecordPhaseBalance` даёт wall vs cpu суммарно, но не per-worker EWMA и не «кто straggler».
5. **Аллокации в тяжёлом пути (GC pressure → случайные STW-паузы = псевдо-straggler):** `TryFindPath`: `new int[regionCount-1]×2` (anchorX/Y), `new List<int>(64)` на регион-путь, `result.ToArray()`, `SmoothPath: new List<int>(32) → ToArray()`; `FlowField: Array.Fill(distanceField, MaxValue)` 1089 int на каждый вызов; `DispatchChunk`: `ThreadLocal<int[]>` уже есть — хорошо.
6. **Серийные хвосты:** `SpillOverPass`/`GlobalPass` однопоточны (осознанно, там lock-heavy `CanAgentExecute`), `SpatialGrid.Rebuild` однопоточен (гонка массивов). Их ускорять НЕ параллельностью, а бюджетами/дебаунсом (уже частично есть).

### 0.4. Доступ к Godot API из потоков (запреты, выявленные grep'ом)

- **Запрещено из worker-потоков:** `Node.AddChild/GetNode/FindChild`, `QueueRedraw`, `TileMapLayer.SetCell`, `MultiMesh.SetInstanceTransform`, `EmitSignal` напрямую, `ResourceLoader.Load`, любой `Control.Text = ...`.
- **Разрешено и уже используется:** `GD.Print/ GD.PrintErr` (в Godot 4 потокобезопасны, но шумят и берут внутренний lock — из горячего пути убрать!), `Callable.From(...).CallDeferred()` (менеджеры так шлют события), `ConcurrentQueue<T>` очереди снапшотов → drain в `Main._Process` / рендерах, `volatile`/`Interlocked`/`Volatile.Read`.
- **Нарушение-кандидат:** `AgentSimulationThread.SimulationLoop` (фоновый `Task.Run`) вызывает `GD.Print`/`GD.PrintErr` напрямую из catch и из `JobValidator`-ветки. При шторме ошибок это contention + спам. План: буферизовать (см. §8.4).

---

## 1. Синтез `@researcher` (web-поиск: MS Docs «Custom Partitioners for PLINQ and TPL» + практика .NET 8)

> Примечание: Task tool с `subagent_type=researcher` в этом окружении недоступен, поэтому синтез выполнен архитектором напрямую через `webfetch` первоисточника (MS Learn) и кодовой базы. Выводы зафиксированы здесь, чтобы `@developer` не искал заново.

1. **Range vs chunk:** range (статические диапазоны `Partitioner.Create(from,to)`) быстрее только при равномерной нагрузке и дешёвом теле цикла. При неравномерной (наш случай: pathfinding/BFS/резервы под lock) — **chunk partitioning** (потоки сами забирают чанки по мере готовности) быстрее почти всегда. Текущий курсор `DynamicWorkBalancer` — это уже chunk, правильно.
2. **Кастомный `Partitioner<T>`:** для `Parallel.ForEach` нужен `OrderablePartitioner<(start,end)>` с `SupportsDynamicPartitions=true`, `GetDynamicPartitions` возвращает чанки переменного размера. Контракт: вернуть ровно `partitionCount` партиций в `GetPartitions`, никогда `null`, индексы уникальны без дыр/дублей, `KeysOrderedInEachPartition=true`, `KeysOrderedAcrossPartitions=false` для динамического. Это и есть `AdaptivePartitioner` (§4.4).
3. **`Task` vs `ThreadPool` vs `Parallel.ForEach`:** для per-tick симуляции (миллисекундные фазы, десятки вызовов/с) `Task.Run` на фазу и `Parallel.For` на фазу — overhead на планирование. Правильно: **один persistent набор workers** (долгоживущие `Thread` или `Task` с `ManualResetEvent`/`Barrier`), либо минимум — переиспользуемый `ParallelOptions` + крупный квант. `ThreadPool.SetMinThreads(ProcessorCount, ...)` заранее, чтобы не было «холодного» донабора потоков под нагрузкой.
4. **Настоящий work-stealing (Chase–Lev deque):** у каждого worker свой двухконечный буфер: владелец берёт с «дна» LIFO (кэш-дружественно), воры крадут с «верха» FIFO половинами. В .NET из коробки этого нет (`ConcurrentQueue` — глобальная FIFO, не steal-half). Пишем свой `WorkStealingQueue<T>` (volatile top/bottom + `Interlocked.CAS`), ~100 строк, полностью тестируем без Godot.
5. **Thread affinity в .NET:** публичного API нет. `ProcessThread.ProcessorAffinity` — только для внешних процессов/потоков, для ThreadPool — нельзя и вредно (ломает планировщик). Эмуляция: sticky-назначение (worker обрабатывает соседние индексы → тёплый L1/L2), `ThreadLocal` буферы, DOP = `Environment.ProcessorCount`, `Thread.CurrentThread.Priority` не трогать. P/Invoke `SetThreadAffinityMask` — **запретить** в плане (риск повесить ThreadPool/Godot).
6. **Godot `WorkerThreadPool`:** GDScript-ориентирован (`add_task(Callable)`), маршалинг Callable + возврат в главный поток. Для tight loop 10k×60/с — проигрыш; использовать только для грубых фоновых работ (генерация карты — уже так). Per-tick фазы остаются на .NET-стороне.
7. **GC pressure:** главные враги — замыкания (`Action<int>` с захватом `deltaTime/tickBucket` = alloc на фазу — терпимо, но внутри цикла нельзя), `new List/Dictionary` в горячем пути, `Array.Fill` больших scratch (лучше `Array.Clear` + версионирование или `Span.Fill`), конкатенация строк в логах. Лекарства: `struct WorkItem`, `Span<T>`, `ArrayPool<T>.Shared`, `ThreadLocal` переиспользуемые буферы, `static` лямбды/метод-группы.

---

## 2. Цели, не-цели, ограничения

**Цели:**
- wall-time фаз Phase2/Phase3a/Phase3b-commit/Dispatcher-chunk = `max(идеал cpu/DOP, самый неделимый кусок)`, простой ядер (imbalance%) видим в `PerformanceOverlay`, straggler-хвост короче в ~4–8× против статики 1024.
- Долгая задача (> кванта) **прерывается кооперативно и её остаток перекидывается** на свободные ядра (split/requeue), а не ждёт конца батча.
- Ноль новых Godot-зависимостей в планировщике: чистый C#, юнит-тестируем без движка.

**Не-цели:**
- Не параллелим `CropGrowth.UpdateGrowth`, снапшоты, `SpatialGrid.Rebuild`, `GlobalPass` — там lock/гонки, выигрыш отрицательный.
- Не пишем свой ThreadPool с нуля в v1 — эволюция существующего курсора + deque + partitioner поверх `Parallel.For` с persistent-оптимизацией.
- Не трогаем рендер/UI/шейдеры.

**Жёсткие ограничения:**
- Никаких вызовов Godot Node API из worker-потоков (только очереди + `CallDeferred` из sim-потока, как сейчас).
- Zero-alloc в горячем пути (§7).
- Детерминизм в пределах разумного: порядок обработки агентов не гарантируется (как и сейчас), но покрытие `[0,count)` — строго без дыр/дублей (assert в дебаге).
- .NET 8, C# 12, Godot 4.7, `EnableDynamicLoading=true`.

---

## 3. Архитектура (выжимка)

```
┌──────────────────────────────────────────────────────────────┐
│ AgentSimulationThread (sim-поток, Task.Run)                  │
│  Phase2 / Phase3a / Phase3b-commit / Dispatcher-chunk        │
│        │                                                     │
│        ▼                                                     │
│ IDynamicWorkScheduler.ForEach / ForEachRange  (фасад)        │
│        │                                                     │
│        ├─► AdaptivePartitioner (OrderablePartitioner)        │
│        │     режет [0,count) на батчи 32..512 по весу cost   │
│        ├─► WorkStealingQueue<WorkItem> per-worker (Chase-Lev)│
│        │     владелец LIFO, воры steal-half FIFO             │
│        ├─► LoadMonitor (EWMA per-worker + imbalance%)        │
│        │     решает: shrink/grow батча, кто straggler        │
│        └─► Splittable body (кооперативный квант 1–2 мс):     │
│              превысил квант → остаток батча requeue          │
└──────────────────────────────────────────────────────────────┘
         publish 1 раз на фазу → GameProfiler.RecordPhaseBalance
         drain → PerformanceOverlay (ImbalancePct уже есть)
```

Ключевая идея: **два контура.**
- **Быстрый контур (per-batch, lock-free):** центральный атомарный курсор как сейчас + per-worker deque для steal-half, когда поток простаивает.
- **Медленный контур (per-phase, в sim-потоке):** `LoadMonitor` считает EWMA wall/cpu/imbalance и подстраивает `[MinBatch, MaxBatch]` и квант прерывания на следующую фазу. Никаких решений внутри батча, кроме кооперативного yield по таймеру.

Классификация работ: `WorkKind.Fast` (движение/ Needs-арифметика) vs `WorkKind.Heavy` (pathfinding-запрос, `OnStart`-резерв под lock, BFS-детур). Heavy-батчи режутся мельче сразу (стартовый размер `MinBatchSize`), Fast — крупнее (меньше overhead курсора).

---

## 4. Интерфейсы и сигнатуры (конкретный C# для .NET 8)

> Неймспейс: `Game.Simulation.Scheduling`. Все типы — чистый C#, без `using Godot`.

### 4.1. `WorkItem` — zero-alloc дескриптор (struct, 16 байт)

```csharp
// src/simulation/scheduling/WorkItem.cs
namespace Game.Simulation.Scheduling;

/// <summary>Дескриптор неделимой единицы кражи. struct — ноль аллокаций в очередях.</summary>
public readonly struct WorkItem
{
    public readonly int Start;        // inclusive
    public readonly int Count;        // > 0
    public readonly float CostHint;   // оценка: 1.0 = средний агент, 4.0 = чанк с работами
    public readonly WorkKind Kind;    // Fast / Heavy
    public int EndExclusive => Start + Count;
}

public enum WorkKind : byte { Fast = 0, Heavy = 1 }
```

### 4.2. `IDynamicWorkScheduler` — фасад (то, что зовёт sim-поток)

```csharp
// src/simulation/scheduling/IDynamicWorkScheduler.cs
namespace Game.Simulation.Scheduling;

public interface IDynamicWorkScheduler
{
    /// <summary>Поэлементный проход [0,count). body обязана быть thread-safe для непересекающихся i.</summary>
    void ForEach(int count, Action<int> body, WorkKind kind = WorkKind.Fast, string profilerScope = null);

    /// <summary>Диапазонный проход: body(start,endExclusive). Возвращает число обработанных элементов.</summary>
    int ForEachRange(int count, Action<int, int> body, WorkKind kind = WorkKind.Fast, string profilerScope = null);

    /// <summary>Прерываемая версия: body проверяет token.ShouldYield каждые YieldEveryN и бросает остаток.</summary>
    int ForEachSplittable(int count, Action<int, int, WorkQuantum> body, WorkKind kind = WorkKind.Fast, string profilerScope = null);

    SchedulerStats GetStats();   // для LoadMonitor/overlay, struct-копия
    void Reset();                // сброс EMA (смена скорости симуляции)
}
```

### 4.3. `WorkQuantum` — кооперативное прерывание долгой задачи

```csharp
// src/simulation/scheduling/WorkQuantum.cs
namespace Game.Simulation.Scheduling;

/// <summary>Квант времени батча. Передаётся в splittable-body по значению (struct).</summary>
public readonly struct WorkQuantum
{
    public readonly long DeadlineTicks;  // Stopwatch.GetTimestamp() + Quantum
    public readonly int YieldEveryN;     // проверять каждые N элементов (напр. 8)

    public bool ShouldYield(int processedSinceCheck)
        => processedSinceCheck >= YieldEveryN && Stopwatch.GetTimestamp() >= DeadlineTicks;
}
```

Политика: квант по умолчанию `2.0 мс` для Phase2/Commit, `1.5 мс` для Dispatcher (там lock'и). Проверка — раз в 8 элементов (один `Stopwatch.GetTimestamp` ~20–30 нс, не чаще). Превысил → body возвращает `остаток (restStart, restCount)`, планировщик `Requeue`-ит его как новый `WorkItem` с тем же `CostHint × 1.5` (эскалация — в следующий раз нарежется мельче).

### 4.4. `AdaptivePartitioner` — кастомный partitioner

```csharp
// src/simulation/scheduling/AdaptivePartitioner.cs
namespace Game.Simulation.Scheduling;

public sealed class AdaptivePartitioner : OrderablePartitioner<Tuple<int, int>>
{
    public AdaptivePartitioner(int minBatch, int maxBatch, Func<int, float> costHintProvider = null);
    public override bool SupportsDynamicPartitions => true;
    public override IList<IEnumerator<KeyValuePair<long, Tuple<int, int>>>> GetPartitions(int partitionCount); // ровно partitionCount, пустые — пустые энумераторы
    public override IEnumerable<KeyValuePair<long, Tuple<int, int>>> GetDynamicPartitions();                   // ленивый chunk-stream поверх курсора
    public override bool KeysOrderedInEachPartition => true;
    public override bool KeysOrderedAcrossPartitions => false;
    public override bool KeysNormalized => false; // ключи — стартовые индексы, не плотные
}
```

Поведение: стартовый размер = `Heavy ? minBatch : InitialBatchSize`; рост `+32` при `ms < HeavyMs×0.25`, уполовинивание при `ms > HeavyMs` (та же EMA-логика, что сейчас в `DynamicWorkBalancer`, но с учётом `CostHint`: эффективный размер = `baseSize / max(1, CostHint)`).

### 4.5. `WorkStealingQueue<T>` — Chase–Lev deque (один на worker)

```csharp
// src/simulation/scheduling/WorkStealingQueue.cs
namespace Game.Simulation.Scheduling;

public sealed class WorkStealingQueue<T> where T : struct
{
    public WorkStealingQueue(int capacity = 1024); // степень двойки, ring-buffer
    public bool TryPushLocal(T item);              // только владелец (очередь — ThreadLocal)
    public bool TryPopLocal(out T item);           // владелец, LIFO (дно)
    public bool TryStealHalf(WorkStealingQueue<T> victim, List<T> buffer); // вор: забирает до половины FIFO (верх)
    public int Count { get; }                      // Volatile-read, приближённый
}
```

Реализация: массив `T[]`, `volatile int _top/_bottom`, маскирование индексом, `Interlocked.CompareExchange` на steal, `Volatile`/`Interlocked.MemoryBarrier` на границах. Никаких `lock`. Переполнение → fallback в глобальный курсор (не расти бесконечно).

### 4.6. `LoadMonitor` — измерение перегруза ядра

```csharp
// src/simulation/scheduling/LoadMonitor.cs
namespace Game.Simulation.Scheduling;

public sealed class LoadMonitor
{
    public void RecordWorkerBatch(int workerId, double batchMs, int batchSize);
    public void EndPhase(double wallMs, double cpuMs, int degreeOfParallelism);
    public SchedulerStats GetStats();   // ImbalancePct, MaxWorkerMs, AvgWorkerMs, StragglerWorkerId
    public void Reset();
    public int SuggestBatchSize(WorkKind kind); // min..max с учётом EWMA
}

public readonly struct SchedulerStats
{
    public readonly double WallMs;
    public readonly double CpuMs;
    public readonly int ImbalancePct;   // (wall - cpu/DOP)/wall*100, 0 = идеал
    public readonly double MaxBatchMs;  // самый медленный батч фазы = хвост
    public readonly double EmaBatchMs;  // сглаженная медиана
    public readonly int StragglerCount; // батчей дольше HeavyMs
}
```

Формулы: `Ema = Ema + (sample - Ema) × 0.25`; `ideal = cpu / DOP`; `imbalance = max(0,(wall-ideal)/wall)×100`. Публикация в `GameProfiler.RecordPhaseBalance(scope, wallMs, cpuMs)` — **один вызов на фазу**, как сейчас (contention-запрет на per-batch запись сохраняется).

### 4.7. `DynamicWorkScheduler` — реализация по умолчанию

```csharp
// src/simulation/scheduling/DynamicWorkScheduler.cs
namespace Game.Simulation.Scheduling;

public sealed class DynamicWorkScheduler : IDynamicWorkScheduler
{
    public static DynamicWorkScheduler Shared { get; } = new();
    public const int InitialBatchSize = 128;
    public const int MinBatchSize = 32;
    public const int MaxBatchSize = 512;
    public static double HeavyBatchMs = 1.5;   // поле, а не const — для тюнинга из overlay/тестов
    public static double QuantumMs = 2.0;

    public void ForEach(int count, Action<int> body, WorkKind kind = WorkKind.Fast, string profilerScope = null);
    public int ForEachRange(int count, Action<int, int> body, WorkKind kind = WorkKind.Fast, string profilerScope = null);
    public int ForEachSplittable(int count, Action<int, int, WorkQuantum> body, WorkKind kind = WorkKind.Fast, string profilerScope = null);
    public SchedulerStats GetStats();
    public void Reset();
}
```

Внутри: `Parallel.For(0, DOP, ...)` + центральный курсор (`Interlocked.Add`) + `ThreadLocal<WorkStealingQueue<WorkItem>>` для steal-half при простое + `LoadMonitor`. Совместимость: оставить `static class DynamicWorkBalancer` как **тонкий фасад-делегат** к `DynamicWorkScheduler.Shared` (чтобы не переписывать 4 точки вызова в один день), пометить `[Obsolete("Use DynamicWorkScheduler.Shared")]` на втором этапе.

---

## 5. Политика миграции (straggler → свободные ядра)

1. **Измерение:** каждый батч меряется `Stopwatch.GetTimestamp` вокруг тела; в thread-local аккумулятор (`batches/items/totalMs/maxMs`), публикация раз на фазу. Порог `HeavyBatchMs=1.5мс` (≈10× медианы лёгкого батча 128 агентов).
2. **Адаптация размера:** `ms > Heavy OR Ema > Heavy → size = max(Min, size/2)`; `ms < Heavy×0.25 → size = min(Max, size+32)`. Heavy-работы стартуют с `Min` сразу.
3. **Steal-half при простое:** поток, увидевший `start >= count` на центральном курсоре, не выходит сразу, а делает до 2 попыток `TryStealHalf` у случайной жертвы (`ParallelRng.Next`) — забирает до половины остатка victim-очереди. Это покрывает случай «курсор пуст, но чужой батч ещё выполняется» только для splittable-остатков (см. п.4): уже выполняющийся не-Splittable батч прервать нельзя, его остаток — только в следующей фазе мельче.
4. **Timeout-прерывание с разбиением (только `ForEachSplittable`):** body каждые 8 элементов проверяет `quantum.ShouldYield`. При истечении — возвращает остаток, планировщик кладёт его обратно (`Requeue` с `CostHint×1.5`). Хвост фазы ограничен `QuantumMs + один YieldEveryN`, а не «самый медленный агент».
5. **Что НЕ прерываем:** `OnStart`-резервы под lock (атомарны по смыслу), `SpatialGrid`-rebuild, `GlobalPass`. Их — только мельче нарезать и бюджетировать.
6. **Голод (starvation) исключён:** центральный курсор монотонен, deque — bounded, requeue-остатки имеют приоритет (кладётся в голову локальной очереди владельца).

---

## 6. Потокобезопасность

- SoA-записи (`AgentDataPool` массивы) — по непересекающимся индексам, как сейчас. Никаких новых разделяемых mutable без синхронизации.
- Курсор выдачи — единственный `Interlocked.Add` на батч (не на элемент). Санити-assert: `batchSeq <= count + MaxBatch×DOP`.
- Claim работ (`GenericJobSpatialIndex._assignedWorkers`) — уже CAS, не трогать.
- `IdleWorkerSpatialGrid` striped locks — не трогать, только не добавлять новых lock'ов в горячий путь.
- `WorkStealingQueue` — lock-free (CAS + volatile). `LoadMonitor.RecordWorkerBatch` — пишет в `ThreadLocal`, агрегация в `EndPhase` однопоточно (sim-поток ждёт `Parallel.For`).
- `GameProfiler.RecordPhaseBalance` — один вызов на фазу (уже lock-free через `GetOrAdd` + прямые записи).
- **Запрет:** `lock` внутри `body(i)` запрещён, кроме существующих менеджер-lock'ов (Ground/Stockpile/JobIndex-register). Новых `lock(_lock)` в фазах не вводить; нужное — через `Interlocked`/`ConcurrentQueue`.
- Двойной mod для round-robin (`((x % n) + n) % n`), никакого `Math.Abs` на счётчике (Overflow на `int.MinValue` — уже полечено в Dispatcher, не регрессировать).

---

## 7. Zero-alloc и GC pressure

- `WorkItem`/`WorkQuantum`/`SchedulerStats` — `readonly struct`, передача по значению/`in`.
- Body-делегаты: в точках вызова использовать **статические лямбды без захвата** либо приватные методы (`UpdateSingleAgent`, `BookkeepSingleAgent`, `CommitSingleAgent`, `DispatchChunkRange`). Захват `deltaTime/tickBucket` — один alloc делегата на фазу допустим, внутрь цикла — ничего.
- `ArrayPool<int>.Shared` для временных `anchorX/anchorY` в `HierarchicalPathfinder.TryFindPath` (сейчас `new int[]`), либо stackalloc при `regionCount ≤ 16` (типично). `SmoothPath`: заменить `List<int> → ToArray()` на проход с подсчётом + `ArrayPool`. `ComputeLocalSegment`: `new List<int>(16)` → pooled буфер.
- `FlowField.CalculateLocalDetourDirection`: `Array.Fill(1089)` каждый вызов — заменить на версионированный `int[] _stamp` (без очистки) либо `Span.Fill` только затронутых клеток (очередь `queue[0..tail]`).
- `ThreadLocal` буферы планировщика — создавать лениво, `trackAllValues:true` только для `Reset()` (как сейчас).
- Логи из worker-потоков — запретить `GD.Print` в горячем пути; ошибки body — считать в `Interlocked`-счётчик, печатать из sim-потока после фазы (образец: `catch → _phaseErrors++`, после `Parallel.For` — один `GD.PrintErr` с числом).

---

## 8. Точки интеграции (какие файлы в `src/` создать/изменить)

### 8.1. Создать (новое, чистое, тестируемое)

| Файл | Содержимое |
|---|---|
| `src/simulation/scheduling/WorkItem.cs` | `WorkItem` + `WorkKind` (§4.1) |
| `src/simulation/scheduling/WorkQuantum.cs` | `WorkQuantum` (§4.3) |
| `src/simulation/scheduling/IDynamicWorkScheduler.cs` | интерфейс (§4.2) + `SchedulerStats` |
| `src/simulation/scheduling/WorkStealingQueue.cs` | Chase–Lev deque (§4.5) |
| `src/simulation/scheduling/LoadMonitor.cs` | EWMA + imbalance (§4.6) |
| `src/simulation/scheduling/AdaptivePartitioner.cs` | `OrderablePartitioner` (§4.4) |
| `src/simulation/scheduling/DynamicWorkScheduler.cs` | реализация (§4.7), внутри — курсор + deque + monitor |

### 8.2. Изменить (минимально, по одному за шаг)

| Файл | Изменение |
|---|---|
| `src/simulation/DynamicWorkBalancer.cs` | Стать фасадом: `ForEach/ForEachRange → DynamicWorkScheduler.Shared.*`, `Reset → Shared.Reset()`. Поведение не меняется. `[Obsolete]` — только после перевода всех вызовов |
| `src/simulation/AgentSimulationThread.cs` | Phase2 → `ForEachSplittable(count, (s,e,q) => loop s..e UpdateSingleAgent + yield-check, Heavy, scope)`; Phase3a/Phase3b аналогично; `GD.Print` из JobAudit-ветки → буфер + печать после фазы; `ThreadPool.SetMinThreads(DOP, DOP)` в `Start()` |
| `src/simulation/jobs/JobDispatcher.cs` | Chunk-проход: `ForEachRange(..., kind: Heavy)` + `CostHint` = `GetChunkJobCount(chunk)` (чанк с работами — тяжелее); `Spill/Global` — не трогать (бюджеты уже есть) |
| `src/simulation/pathfinding/HierarchicalPathfinder.cs` | `TryFindPath`: `anchorX/anchorY` → `ArrayPool`/`stackalloc`; `SmoothPath`/`ComputeLocalSegment` — pooled буферы вместо `new List`; без смены алгоритма |
| `src/simulation/agents/FlowFieldManager.cs` | `distanceField` — версионирование вместо `Array.Fill`; без смены BFS-логики |
| `src/core/common/GameProfiler.cs` | Добавить `RecordSchedulerStats(SchedulerStats)` (опционально) или переиспользовать `RecordPhaseBalance`; добавить счётчик `StragglerCount` в `BalanceSnapshot` (1 поле, без contention) |
| `src/ui/PerformanceOverlay.cs` | Показать `ImbalancePct + MaxBatchMs` из `SnapshotBalance` (уже есть таблица балансов — расширить колонку, не новый UI) |

### 8.3. НЕ трогать

`AgentSpatialGrid` (rebuild sequential), `CropGrowthManager.UpdateGrowth`, снапшоты, `MapGenerator`, рендеры, `AgentGpuComputeService`, сцены `scenes/`.

### 8.4. Правило Godot API (повторить в коде комментарием)

```
// SCHEDULING RULE: из body(i) запрещены ЛЮБЫЕ Godot Node API
// (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
// Разрешены: чистые вычисления, SoA-массивы, Interlocked/Volatile,
// ConcurrentQueue.Enqueue (снапшоты), ThreadLocal, ParallelRng.
// Всё, что должно попасть в главный поток, — через существующие очереди
// (PositionQueue/SnapshotQueue) или CallDeferred из sim-потока (не из workers).
```

---

## 9. Юнит-тестируемость

- `WorkStealingQueue/LoadMonitor/AdaptivePartitioner/DynamicWorkScheduler` — ноль зависимости от Godot: тесты — обычный NUnit/xUnit вне движка (или `dotnet test` по `Game.csproj`, если в репо заведётся тестовый проект; иначе — отдельная `tests/` сборка без Godot.NET.Sdk).
- Обязательные тесты: покрытие `[0,count)` без дыр/дублей при `count ∈ {0,1,31,32,33,10000}` и `DOP ∈ {1,2,8}`; steal-half возвращает половину; `LoadMonitor` imbalance 0 на равномерной и >50% на одной тяжёлой; splittable requeue не теряет остаток; `Reset()` возвращает `InitialBatchSize`.
- Детерминированный режим: `DOP=1` → строго последовательный проход (для golden-тестов).

---

## 10. Порядок внедрения для `@developer` (пошагово, каждый шаг — компилируется и играется)

1. **Шаг 1 — скелет без поведения:** создать 7 файлов из §8.1; `DynamicWorkScheduler` v1 = копия текущей логики `DynamicWorkBalancer` (курсор + EMA), но с сигнатурами §4.7 (`kind` пока игнорируется). `DynamicWorkBalancer` → фасад. Тесты покрытия. Критерий: игра запускается, баланс-метрики те же.
2. **Шаг 2 — LoadMonitor:** вынести EMA/imbalance из планировщика в `LoadMonitor`, добавить `MaxBatchMs/StragglerCount` в снапшот баланса, показать в overlay. Тесты EWMA.
3. **Шаг 3 — WorkStealingQueue + steal-half при простое:** `ThreadLocal` deque, центральный курсор остаётся первичным; steal — только когда курсор пуст (2 попытки). Тесты deque.
4. **Шаг 4 — Splittable:** `ForEachSplittable` + `WorkQuantum` (квант 2мс, check каждые 8); перевести Phase2 на него (самый тяжёлый). Остальные фазы — в следующем шаге. Тест requeue.
5. **Шаг 5 — CostHint:** Dispatcher chunk-проход с `kind: Heavy` + hint по `GetChunkJobCount`; Phase3b-commit — `Fast`; Phase2 — эвристика `Heavy` если у агента `WaypointCount>0 || StuckTimer>0.5` (дешёвая проверка без локов). Замер imbalance до/после.
6. **Шаг 6 — Zero-alloc в pathfinding/flowfield** (§7, pooled буферы). Отдельно от планировщика, легко откатить. Замер GC (счётчик Gen0/с в overlay или `dotnet-counters`).
7. **Шаг 7 — `AdaptivePartitioner` как `OrderablePartitioner`:** только если шаги 1–5 дали <10% выигрыша хвоста — иначе пропустить (курсор уже покрывает 90% кейсов, partitioner нужен в основном для `Parallel.ForEach`-стиля вызовов извне).

Каждый шаг: `dotnet build`, запуск 10k агентов на 1x/25x, скрин overlay (Wall/Cpu/Imbalance по трём фазам), без регрессии `JobValidator fixed=0`.

---

## 11. Приёмка (числа)

- Imbalance фаз Phase2/Phase3a при 10k агентов, 8+ ядер: **< 25%** в установившемся режиме (сейчас при straggler — до 60–80%).
- MaxBatchMs фазы: **< 3 мс** при `MinBatch=32` (хвост ограничен квантом, а не BFS).
- Overhead планировщика на фазу (курсор + замеры): **< 2%** wall-time фазы.
- Ноль новых аллокаций на элемент (замер через `GC.GetAllocatedBytesForCurrentThread` в тесте `ForEach(100k)` — только константа на фазу).
- `JobValidator` молчит, агенты не теряются (покрытие без дыр — assert в Debug).

---

## 12. Риски и «не делать»

- **Не делать dedicated Thread'ы с affinity (P/Invoke)** — риск повесить ThreadPool и Godot main loop; выигрыш на uniform-нагрузке нулевой.
- **Не прерывать lock-секции** — `OnStart`-резерв либо целиком, либо никак; прерывание только между элементами батча.
- **Не плодить `Parallel.For` вложенно** (Dispatcher внутри фазы): уже есть один уровень, второй уровень — только `ForEachRange` того же планировщика, без вложенного `Parallel`.
- **Не звать Godot API из body** — ревью-чеклист: grep `GD\.|GetNode|AddChild|SetCell|QueueRedraw|Load\(` внутри `src/simulation/scheduling/` должен быть пуст.
- Откат: каждый шаг §10 откатывается одной заменой вызова на `DynamicWorkBalancer.ForEach` (фасад живёт до конца).

---

## 14. Горы (mountains.png `uid://de7mit41ukuoq`, атлас 4×4) — проект `@architect`

> Источник: описание пользователя (ряды Y=0..3). Раскладка НЕ совпадает с `WallTileHelper`
> (у стен mask→atlas рассыпан, у гор — своя систематика). Поэтому отдельный хелпер,
> отдельный слой рендера, отдельный порог генерации. Код — только `src/` (правило `@developer`).

### 14.1. Раскладка атласа → маска (Up=1, Right=2, Down=4, Left=8)

| Атлас (x,y) | Описание пользователя | Маска |
|---|---|---|
| (0,0) | Одиночный, связей нет | 0 |
| (1,0) | Стык только сверху | 1 (Up) |
| (2,0) | Вертикаль верх+низ | 5 (Up+Down) |
| (3,0) | Стык только снизу | 4 (Down) |
| (0,1) | Стык только слева | 8 (Left) |
| (1,1) | Угол верх+лево | 9 (Up+Left) |
| (2,1) | T без низа (верх+лево+право) | 11 |
| (3,1) | Угол верх+право | 3 (Up+Right) |
| (0,2) | Горизонталь лево+право | 10 |
| (1,2) | T без права (верх+низ+лево) | 13 |
| (2,2) | Сплошной центр, все стороны+углы | 15 |
| (3,2) | T без лева (верх+низ+право) | 7 |
| (0,3) | Стык только справа | 2 (Right) |
| (1,3) | Угол низ+лево | 12 (Down+Left) |
| (2,3) | T без верха (низ+лево+право) | 14 |
| (3,3) | Угол низ+право | 6 (Down+Right) |

Инверсия (mask→atlas) для `MountainTileHelper.TileMap[16]`:
`0→(0,0), 1→(1,0), 2→(0,3), 3→(3,1), 4→(3,0), 5→(2,0), 6→(3,3), 7→(3,2), 8→(0,1), 9→(1,1), 10→(0,2), 11→(2,1), 12→(1,3), 13→(1,2), 14→(2,3), 15→(2,2)`.

### 14.2. Интерфейсы

```csharp
// src/core/world/MountainTileHelper.cs
namespace Game.Core;
public static class MountainTileHelper
{
    public static Vector2I GetTile((int X, int Y) pos, HashSet<(int X, int Y)> mountains);
    public static Vector2I GetTile(int x, int y, TileType[,] ground); // перегрузка для MapRenderer без HashSet
    public static bool IsMountain(int x, int y, TileType[,] ground);  // bounds-safe
}
```

```csharp
// TileType.cs
public enum TileType { Grass, Water, Mountain }
```

### 14.3. Генерация (`MapGenerator`)

- Новый порог `mountainThreshold = 0.70 + rand*0.06` (по той же `heightMap`, что вода;
  вода режется снизу `< waterThreshold`, горы — сверху `> mountainThreshold`, конфликтов нет).
- Порядок в `GenerateOnce`: вода → `EnforceLakeLimit` → река (затирает горы/траву в воду,
  `FillDisc` без изменений) → `CarveMountains` → `EnsureLandConnectivity` → `ClearStartArea`
  → `PlantForests`. `CarveMountains`: кандидаты `heightMap > mountainThreshold` → чистка
  одиночек (убрать компоненты < 6 кл. FloodFill-ом, вернуть в Grass) → иначе оставить.
- `EnsureLandConnectivity`: связность считается по `Grass` (горы уже исключены, т.к. не Grass);
  мосты-«броды» чинят и воду, и горы (`Water|Mountain → Grass`, до 4 кл.).
- `ClearStartArea`: `Mountain → Grass` + снять деревья (как вода).
- `PlantForests`: деревья только на `Grass`, горы пропускаются.
- `Score/MeetsPlayability`: добавить штраф за `mountainRatio > 0.15` и требование `< 0.18`,
  чтобы не замуровать карту; `MinGrassRatio 0.55` не менять (горы отъедают ~5–12%).

### 14.4. Семантика проходимости: гора = стена (блокирует), а не вода (замедляет)

- `AgentMovementService.IsTileBlocked`: `Ground==Mountain → true` (+ радиус-край 8px по 4 соседям,
  как стены). `MoveToPoint` water-slow только для `Water`.
- `HierarchicalPathfinder.Build`: `SolidWalls || Mountain → CostBlocked`, `Water → CostWater`.
- `FlowFieldManager`, `JobBroker.FindStandPosition`, `NeedsJobSystem` — уже `==Grass`, горы
  исключены без правок (проверить grep'ом).
- `GridHelper.TryFindAdjacentWalkable/TryFindNearestFreeTile`: добавить `ground==Grass` проверку
  (сейчас проверяют только стены/деревья — агент высаживался бы в гору).
- `AgentGpuComputeService`: `blocked = Water || Mountain || SolidWalls`.
- `AgentSimulationThread.Start` walkable: уже `==Grass`, без правок.
- Инструменты: `BuildTool.CanPlaceBlueprintAt`, `FarmingTool.CanDesignateFarmPlot`:
  `==Water → return false` заменить на `!=Grass → return false` (запрет гор как воды).
  `PlayerInteractionManager`, `StressDebug`: `==Water → continue` заменить на `!=Grass`.

### 14.5. Рендер (`MapRenderer`)

- `SourceMountain = 9`, `TextureMountain = "uid://de7mit41ukuoq"`, новый `_mountainLayer`
  (между `_groundLayer` и `_objectLayer`, чтобы горы перекрывали траву, но не агентов).
- `CreateMountainTileSet()`: atlas 4×4, `TileSize 64`, `CreateTile` для всех 16 клеток.
- `ApplyMap`: земля под горой — `Grass`; поверх в `_mountainLayer` — `SetCell(pos, SourceMountain,
  MountainTileHelper.GetTile(x, y, mapData.Ground))`. Подсчёт `mountains++` в лог
  рядом с `суша/вода/деревья`.
- Тени: горы в `ShadowCasterRenderer.RebuildStatic` НЕ добавляем (v1, только деревья/стены).

### 14.6. Приёмка `@developer`

1. `dotnet build` чистый; карта генерится, в логе `горы=N (5–12% от 512×512)`.
2. Автотайлинг визуально: одиночки (0,0), прямые, T, углы, центр (2,2) без серой каймы.
3. Агенты не заходят в горы, путь строится в обход (стена-семантика), `JobValidator fixed=0`.
4. Старт-поляна R12 без гор/воды/деревьев; связность суши ≥92% с учётом гор.
5. `@destroyer`-кейсы: `Ground=null`, гора на краю карты (bounds-safe `IsMountain`),
   вся карта в горах (walkable пуст → `InvalidOperationException`, а не зависание).

---

## 13. Готовые инструкции для `@developer`

1. Работай **строго в `src/`** по §8.1–8.2 и порядку §10. `scenes/`, `project.godot`, `Game.csproj` — не трогать.
2. Начни с Шага 1: создай `src/simulation/scheduling/*.cs` (7 файлов), неймспейс `Game.Simulation.Scheduling`, без `using Godot`. `DynamicWorkBalancer` преврати в фасад-делегат (старые сигнатуры сохрани 1-в-1, чтобы `AgentSimulationThread` и `JobDispatcher` компилировались без правок).
3. Сохраняй стиль репо: `using` сверху, `readonly struct` где zero-alloc, `Interlocked/Volatile` вместо `lock` в новом коде, комментарии на русском, XML-doc на публичных методах.
4. Каждый body-вызов снабди комментарием `// SCHEDULING RULE ...` (§8.4) в месте определения body (не внутри планировщика).
5. Если `@destroyer` пришлёт `bad_data.json`/кейс (null `SimulationContext`, `count=0/1/отрицательный`, `DOP=1`, исключение в body посреди батча) — чини так: исключение одного `i` логируется в счётчик и не роняет фазу (остальные `i` добиваются, покрытие сохраняется); `null body/ctx` → `ArgumentNullException` сразу, а не `NullReferenceException` в пуле.
6. По завершении каждого шага отвечай: какие файлы созданы/изменены + значения Wall/Cpu/Imbalance трёх фаз + `dotnet build` чистый.

---

## 15. GPU-вычисления (compute / vertex-shader / CPU-SIMD) — проект `@architect` (ноябрь 2026)

> Синтез `@researcher` (webfetch 13.09.2026, stable-доки 4.7 + MS Learn SIMD от 18.07.2026).
> Код НЕ менялся, это проектный документ для `@developer`.

### 15.0. Ключевой вердикт (читать первым)

- Проект работает на **`renderer/rendering_method="gl_compatibility"`** (`project.godot:44`).
  По докам Godot 4.7: `RenderingDevice is not available ... when using the Compatibility rendering method`
  (`class_renderingdevice.html`), `Compute shaders can only be used from RenderingDevice-based
  renderers (Forward+ or Mobile)` (`compute_shaders.html`), таблица `renderers.html`:
  `Compute shaders | Compatibility: ❌`, `RenderingDevice access | Compatibility: ❌`.
- **Следствие: существующий `src/simulation/gpu/AgentGpuComputeService.cs`
  (`CreateLocalRenderingDevice` + `agent_movement.glsl`, local_size 64) в прод-цикл при
  `gl_compatibility` включать НЕЛЬЗЯ** — `CreateLocalRenderingDevice()` вернёт null/ошибку.
  Релиз 4.7 (`godotengine.org/releases/4.7/`) ничего не меняет: хайлайты — AreaLight3D, HDR,
  DrawableTexture2D, без compute в Compatibility. `rendering_device/driver.windows="d3d12"`
  при Compatibility игнорируется (ветка OpenGL), повлияет только после перехода на Mobile/Forward+.
- Оговорка: страница `tutorials/rendering/rendering_device.html` — 404 (корректный путь —
  `tutorials/shaders/compute_shaders.html`); надежда «local device создаёт свой Vulkan-контекст
  независимо от рендера» доками 4.7 опровергнута.
- **Поэтому «GPU» в этом плане = три трека:** A) CPU-SIMD поверх SoA (основной, работает везде),
  B) vertex-shader визуал через MultiMesh (работает в Compatibility), C) настоящий compute —
  только как опция под Mobile/Forward+ (не в проде сейчас).

### 15.1. Аудит кандидатов: лёгкие × массовые операции

| # | Операция | Место | N / частота | Пригодность для GPU | Решение |
|---|---|---|---|---|---|
| 1 | Needs-арифметика (Hunger/Sleep/Fatigue/Mood, ~10 FLOP) | `AgentSimulationThread.UpdateNeedsSingleAgent:470-507` | 10k × каждый шаг (до 8 шагов/тик) | ✅ Идеальна, но compute недоступен → **SIMD** | Трек A, шаг 1 |
| 2 | Старт интерполяции рендера (lerp prev→target + заливка буфера 8 float) | `AgentRenderer._Process:181-217` | 10k × 60fps, ~1мс | ✅ Векторизуется + частично в vertex-shader | Трек A шаг 2 + Трек B шаг 4 |
| 3 | Тени агентов (растяжение силуэта R(dir)*S(len,width), якорь у ног) | `AgentRenderer._Process:197-212` | 10k × 60fps, ~1мс | ✅ **Полностью в vertex-shader** (матрица из солнца, CPU вообще не нужен) | Трек B, шаг 4 |
| 4 | Прямое движение (normalize + шаг + скольжение, `MoveToPoint`) | `AgentMovementService:137-245`, GLSL-копия `agent_movement.glsl:59-102` | 10k × шаг, ветвления + `IsTileBlocked` | ⚠️ Условно: compute-копия уже есть, но требует readback каждый тик + недоступен в Compatibility | Трек C (опция), НЕ в прод |
| 5 | Cell-tracking (`pos>>6`, CellStayTime) | `BookkeepSingleAgent:514-528` | 10k × шаг, ~5 операций | ✅ SIMD-тривиально | Трек A, шаг 1 |
| 6 | Рост культур (`timer += dt`, Stage++) | `CropGrowthManager.UpdateGrowth:61-101` | ≤4096, редко (0.5с игр. вр.) | ❌ Слишком мало/редко, lock-словарь — overhead GPU больше выигрыша | НЕ трогать |
| 7 | fBm-генерация карты (4–5 октав, 512×512) | `NoiseGenerator.GenerateFbmMap`, `MapGenerator` | 262k × одноразово при старте (~40–110мс в фоне) | ⚠️ Классический GPGPU-кейс, но одноразовый и уже быстрый — миграция не окупается | НЕ трогать (v2-опция Трека C) |
| 8 | Pathfinding A* / BFS FlowField 33×33 | `HierarchicalPathfinder`, `FlowFieldManager:62-...` | 5% агентов, ветвящийся, `Array.Fill` 1089, ранний выход ≤5 тайлов | ❌ Ветвления + thread-local scratch + неравномерность — GPU-враг, остаётся на CPU + work-stealing (§1–12) | НЕ трогать |
| 9 | Снапшоты (копия под lock + построение буфера) | `GenerateSnapshot` ×3, 30 Гц | 10k × 30/с | ❌ Копия — bandwidth-bound, не compute-bound | НЕ трогать |

Порог окупаемости compute с readback каждый тик (из доки + MS Learn SIMD): 10k × ~32B ≈ 320KB —
целиком в L2/L3, AVX2/AVX-512 даёт 8–16 float/инструкцию без PCIe round-trip и `Submit/Sync`-stall
(дока требует держать 2–3 кадра в полёте, иначе CPU стоит). GPU выигрывает при десятках–сотнях
тысяч тел **без** нужды читать результат каждый кадр (чистый визуал).

### 15.2. Трек A — CPU-SIMD поверх SoA (основной, работает в Compatibility и headless)

Идея: `AgentDataPool` уже SoA (`float[]`), идеально ложится на `Span<float>` + векторизацию
без GC pressure. Слои .NET 8 (MS Learn `standard/simd`, 18.07.2026): сначала `TensorPrimitives`
(`System.Numerics.Tensors` NuGet — готовые `Add/Multiply/...` над `Span<T>`), затем `Vector<T>`
(`Vector.IsHardwareAccelerated`), затем фиксированные `Vector256/512<T>` (`Intrinsics`), точечные
`Avx2/Fma` — только для измеренных хот-патов. На x86/x64 `Vector256` = 2×lane по 128 —
lane-crossing shuffle дороже, мерить.

```csharp
// src/simulation/simd/SimdNeedsBatch.cs — чистый C#, без using Godot
namespace Game.Simulation.Simd;
public static class SimdNeedsBatch
{
    // Векторизованный UpdateNeedsSingleAgent для среза [start, end):
    // Hunger/Sleep += rate*dt (clamp 100), Fatigue по маске Working/Idle,
    // Mood = 100 - (H*Wh + S*Ws + F*Wf + (100-E)*We). Маски — через Vector<T>.
    // Scalar-fallback при !Vector.IsHardwareAccelerated. Zero-alloc: только Span over pool arrays.
    public static void UpdateNeeds(AgentDataPool pool, int start, int end, float deltaTime, bool updateEnv);
    public static void UpdateCells(AgentDataPool pool, int start, int end, float deltaTime); // pos>>6, CellStayTime
}
```

Файлы: создать `src/simulation/simd/SimdNeedsBatch.cs` (+ позже `SimdMoveBatch.cs` для прямого
движения без препятствий — только normalize+шаг, `IsTileBlocked`-ветка остаётся scalar).
Интеграция: `Phase3a_ParallelBookkeeping` режет диапазон через `DynamicWorkScheduler`
(§4–5), внутри батча зовёт `SimdNeedsBatch` вместо скалярного цикла; `AgentRenderer._Process`
lerp-заливку — аналогично (или Трек B). Тесты без Godot: побайтовое равенство SIMD vs scalar
на граничных (0/100/clamp, Working/Idle, updateEnv true/false), `DOP ∈ {1,8}`.

### 15.3. Трек B — вершинный шейдер + MultiMesh (единственный настоящий GPU в Compatibility)

Симуляция остаётся на CPU (§15.2), в GPU уходит визуал (доки: `using_multimesh.html`,
`animating_thousands_of_fish.html`, `using_servers.html`, `gpu_optimization.html`):

1. **Тени агентов → vertex shader.** Сейчас CPU считает матрицу растяжения на каждого агента
   (`AgentRenderer:203-211`). Перенести: солнце (`Dir/LengthPx/WidthScale/Alpha`) — в глобальные
   shader-params материала тени, растяжение от ног — в вершинный шейдер квада. CPU-заливка
   `_shadowBuffer` исчезает полностью (−1мс на 10k, −80KB/кадр bandwidth). `Modulate`/Visible —
   как сейчас из `ApplyShadow`.
2. **Покачивание/наклон агентов → vertex shader через `INSTANCE_CUSTOM`/`COLOR`.**
   `AgentRenderer`: `UseCustomData = true`, скорость/состояние пишутся в custom-раз в ~0.25с
   (не каждый кадр), анимация — в шейдере. Логика не трогается.
3. **Bulk-заливка через один линейный массив.** Готовить `_renderBuffer` можно в `Parallel.For`
   (SoA → buffer), заливать одним `_multiMesh.Buffer = ...`; плюс `VisibleInstanceCount` для пула.
   Предупреждение `using_servers.html`: не читать из RenderingServer каждый кадр (stall) — только пишем.
4. Ограничения: нет per-instance frustum culling (all-or-none) — лечится разбивкой на несколько
   MultiMesh по зонам (v2); `GPUParticles` для агентов НЕ использовать (нужны логика/память/readback,
   в Compatibility урезаны trails/SDF-collision); `DrawableTexture2D` (новое 4.7) — для
   minimap/феромонов, не для физики.

### 15.4. Трек C — настоящий compute (опция, НЕ в проде при gl_compatibility)

`AgentGpuComputeService.cs` + `agent_movement.glsl` оставить как есть, но:
- обернуть явным рантайм-гейтом: `RenderingMethod != gl_compatibility` + `IsInitialized`, иначе —
  CPU-fallback с одним предупреждением (не спам в лог каждый тик);
- при будущем переходе на Mobile renderer (дешевле Forward+ для сима): readback переписать на
  `BufferGetDataAsync` + 2–3 кадра задержки (best practice доки), `FreeRid()` вручную
  (GC RIDs не чистит), длинные диспатчи резать на чанки (Windows TDR 5–10с), `local_size 64/128/256`
  под задачу, `restrict` в GLSL; актуальный C# API 4.7 — `StorageBufferCreate/BufferUpdate/
  BufferClear/BufferCopy`, `ComputeListDispatchIndirect/SetPushConstant/AddBarrier`,
  `ComputePipelineIsValid`, `CaptureTimestamp`, `RDShaderFile.GetSpirV(version)/GetVersionList()`.
- Миграция рендера — отдельное решение (риск регрессии всего рендера), НЕ часть этого плана.

### 15.5. Порядок внедрения для `@developer` (каждый шаг — компилируется и играется)

1. **Шаг G1 — гейт GPU-сервиса (без поведения):** в `AgentGpuComputeService` добавить
   `IsComputeAvailable()` (проверка рендера != Compatibility + `CreateLocalRenderingDevice != null`),
   CPU-fallback по умолчанию, один лог вместо спама. Критерий: игра запускается как раньше,
   сервис НЕ в прод-цикле (как сейчас). Файлы: только `gpu/` + флаг, `scenes/` не трогать.
2. **Шаг G2 — `SimdNeedsBatch.UpdateNeeds` + тест равенства scalar:** интегрировать в
   `Phase3a` внутри существующего `DynamicWorkScheduler`-батча. Замер: Wall/Cpu/Imbalance Phase3a
   до/после на 10k, `dotnet build` чистый.
3. **Шаг G3 — `SimdNeedsBatch.UpdateCells` + lerp-заливка `AgentRenderer`:** тот же паттерн,
   замер `Render: Agent MultiMesh` до/после.
4. **Шаг G4 — тени в vertex shader:** материал тени + shader-params солнца, удаление CPU-заливки
   `_shadowBuffer`. Замер: −~1мс/кадр на 10k, визуально тени те же (скрин утро/полдень/вечер).
5. **Шаг G5 (опция) — `INSTANCE_CUSTOM`-анимация + `SimdMoveBatch` для прямого движения.**
   Только если G2–G4 дали <10% суммарно — иначе пропустить.
6. Трек C в этом плане НЕ реализуется (только гейт из G1); при смене рендера — отдельный план.

### 15.6. Приёмка (числа)

- G2: Phase3a Wall −15–30% на 10k (Needs+Cells — чистая арифметика, главный выигрыш SIMD);
  побайтовое равенство SIMD vs scalar в тестах (clamp 0/100, Working/Idle, Env-флаг).
- G3: `Render: Agent MultiMesh` −10–20%, ноль новых аллокаций в тике
  (`GC.GetAllocatedBytesForCurrentThread` константа на фазу).
- G4: тени визуально идентичны (3 скрина), CPU-заливка теней удалена полностью.
- `JobValidator fixed=0`, агенты не теряются, `dotnet build` чистый после каждого шага.
- Compute в проде при gl_compatibility — отсутствует (гейт G1 это гарантирует).

## 16. Режим карты «Влажность» + HumidityMap — проект `@architect`

> Медленная почвенная влага. Тик раз в 30 сек игрового времени, цифры почти
> не прыгают (испарение — долгий процесс). Огород слегка ускоряет высыхание.
> Культуры обычно до грунтовых вод не дотягиваются — влияние мягкое.

### 16.1. Хранение (`src/core/world/HumidityMap.cs`, новый файл)

- `byte[] Moisture 0..200` (вода = 200, >100 = переувлажнение), плоский `y*W+x`.
- Double-buffer `cur/nxt` + предрасчёт `waterNear` (вода в 4-соседях).
- Всё int-only, ноль аллокаций в тике. ~0.5МБ на 512².

### 16.2. Генерация (в `MapGenerator.GenerateOnce`, после гор)

- База: `moist fBm` (уже считается для рощ) + бонус у воды + штраф на высоте.
- Вода = 200, рядом с водой = 120–150, иначе 40–100 по шуму.
- Лес сажается где влажно (уже есть) + удерживает влагу (тик ниже).

### 16.3. Тик (раз в 30с игрового, в `AgentSimulationThread` рядом с ростом)

- Формула на клетку: `v = me + (avg4-me)/16 + (waterNear ? +3 : 0) - evap`,
  `evap = 1 + (высоко ? 1 : 0) - (лес ? 1 : 0) - (огород ? 1 раз в неск. тиков : 0)`.
- Делитель 16 (а не 8): процесс в 2 раза медленнее диффузии из ресёрча.
- Кламп 0..200. Вода всегда 200 (Dirichlet). `Parallel.For` по строкам.
- Бюджет: 262k × ~10 оп раз в 30с = незаметно вообще.

### 16.4. Фермы (`CropGrowthManager.UpdateGrowth`)

- Трапеция: `<15 → 0.3`, `15..40 → 0.3..0.8`, `40..130 → 0.8..1.0`,
  `130..170 → 1.0..0.6`, `>170 → 0.4` (болото гниёт, но не в ноль).
- Мягкая: минимум 0.3, чтобы засуха не убивала всё (культуры не дотягиваются
  до грунтовых вод — влияние приглушено).
- Читает готовое значение, диффузию не трогает.

### 16.5. Рендер (`MapRenderer`: новый `_humidityLayer`)

- `TileMapLayer` между farm/stockpile и object + 1 синий тайл 64×64.
- Цвет: `0 → EraseCell` (не рисуем, сухо), иначе синий, alpha 0.15..0.55
  по влажности. Каждый процент влияет (alpha = 0.15 + m/200*0.4,
  темнота через затемнение base-цвета).
- Обновление через `PendingCell`-батч (бюджет 2048/кадр), при включении —
  полный RebuildAll, при выключении — `Clear()`.
- `MapMode { Normal, Humidity }`, тоггл кнопкой `humidity`, `other` тогглит
  `>`/`<` + видимость `HBoxContainer` (additionally). `Button2` не трогаем.
- Тултип: % под курсором (SelectTool hover + строка в Garden-панели/легенда).

### 16.6. Приёмка `@developer`

1. `dotnet build` чистый; синева включается/выключается, `other` меняет `>`/`<`.
2. Цифры меняются медленно: за 5 мин игры дрейф ±5-10, у воды держится 150+.
3. Фермы на сухом растут заметно медленнее, на болоте — хуже оптимума.
4. `JobValidator fixed=0`, фризов нет (тик <1мс раз в 30с).

## 18. Независимые слои шума + FertilityMap (плодородие 0..200)

> Проблема: один и тот же fBm-шум переиспользуется в нескольких местах —
> `moisture` в `PlantForests` и `moistNoise` в `GenerateOnce` оба считаются как
> `GenerateFbmMap(w,h,forestSeed+777u,groveScale*0.7f,3)` (тот же сид, тот же scale).
> Рощи, стартовая влажность и (теперь) плодородие оказываются скоррелированы:
> где роща — там и мокро, и плодородно. Задача: разнести генерацию на разные слои
> (разные сиды/масштабы), ввести `FertilityMap` 0..200 с зелёным оверлеем.

### 18.1. Слои шума (каждый — свой сид и свой масштаб)

| Слой | Назначение | Сид | Scale | Октавы |
|---|---|---|---|---|
| height | рельеф (вода/горы) | `seedH = seed` | `heightScale` (90..140) | `octaves` (4..5) |
| grove | кластеры рощ | `seedF = forestSeed` | `groveScale` (60..100) | `octaves` |
| moist | стартовая влажность | `seedF + 777u` | `groveScale*0.7f` | 3 |
| fertile | стартовое плодородие | `seedF + 4242u` | `groveScale*0.5f` | 3 |

Правила: НИКАКОЙ слой не читает чужой шум. `PlantForests` — только `groveMap`
(+ свой локальный `moistAccept`, пересчитанный из `moist`-слоя, а не общий);
`Humidity.Initialize` — только `moist`; `Fertility.Initialize` — только `fertile`.
Дешёвые `new Random(seed)` в `AccumulateOctave` остаются (генерация одноразовая).

### 18.2. Хранение (`src/core/world/FertilityMap.cs`, новый файл, близнец HumidityMap)

- `int[] cur/nxt` плоские `y*W+x`, double-buffer, int-only, ноль аллокаций в тике.
- Диапазон 0..200 (`MaxFertility=200`, `MinFertility=0` пола снизу НЕТ — бывает 0).
- Вода/горы = 0 (там не растёт). Лес слегка удобряет (+), огород истощает (−).
- `Initialize(ground, fertileNoise)`: `0 + n*200` по шуму, вода/горы → 0.
- `Tick(ground, forestMask, farmMask)`: раз в 60 игровых минут (в 2 раза реже
  влаги — плодородие почти геология), дрейф ±1 за тик, кламп 0..200.
  `Parallel.For` по строкам, swap буферов. Чистый C# без Godot API.
- `GrowthMultiplier(fertility)`: мягкая трапеция как у влаги, минимум 0.3:
  `<20 → 0.3`, `20..60 → 0.3..0.8`, `60..140 → 0.8..1.0 плато`, `140..180 → 1.0..0.6`,
  `>180 → 0.4` (переудобрено/засолено — хуже оптимума, но не в ноль).
- Итоговый множитель роста = `humidityMul * fertilityMul` (оба мягкие, мин 0.09).

### 18.3. Генерация (`MapGenerator.GenerateOnce`)

- После `Humidity.Initialize`: `fertileNoise = GenerateFbmMap(w,h,seedF+4242u,groveScale*0.5f,3)`,
  `data.Fertility.Initialize(data.Ground, fertileNoise)`.
- Лес в `PlantForests`: влага для accept-решётки берётся из ОТДЕЛЬНОГО
  `moisture`-шума (свой вызов, не разделяемый с влажностью карты).

### 18.4. Симуляция

- `MapData.Fertility` (new в ctor, как `Humidity`); `SimulationContext.Fertility`
  (nullable, старые вызовы — без множителя); `AgentSimulationThread.Start` —
  новый опциональный параметр `fertility=null` (в конце, старые вызовы живы).
- Тик fertility в том же месте, где humidity-тик: свой таймер
  `_fertilityTimer`, интервал `FertilityMap.TickIntervalGameSec` (500 геймсек).
- `CropGrowthManager.UpdateGrowth`: `soilMul = humMul * fertMul`.
- `Main.cs`: пробросить `MapData.Fertility` в `Start(...)`.

### 18.5. Рендер

- `MapMode { Normal, Humidity, Fertility }` — взаимоисключающие (как сейчас
  humidity-тоггл: включение одного гасит другой).
- `FertilityOverlayRenderer` — близнец `HumidityOverlayRenderer` (256², 1с
  троттлинг), цвет зелёный: `0 → почти белый (1,1,1)`, `200 → тёмно-зелёный
  (0,0.45,0.1)`, alpha 0.8. Вода/горы (fert=0) — бледно, и так видно что не растёт.
- `MapRenderer`: поле `_fertilityOverlay`, `Fertility` геттер, `SetMapMode`
  и `RefreshFertilityOverlay` зеркально humidity-ветке.
- `HUDController`: кнопка `fertility` (уже есть в `hud.tscn`) — тоггл режима
  Fertility + легенда «Плодородие: бледное — бедно, тёмное — богато» +
  тултип `ShowFertilityAt` (`{v} {бедно|норма|богато}`). Подсветка кнопок:
  активная — modulate, вторая гаснет (режимы взаимоисключающие).
- `SelectTool.OnHover`: дёргать `ShowFertilityAt` рядом с `ShowHumidityAt`
  (каждый сам проверяет свой режим и прячется в чужом).

### 18.6. Приёмка `@developer`

1. `dotnet build` чистый; зелень включается/выключается, humidity/fertility
   взаимоисключают друг друга.
2. Рощи ≠ плодородные пятна ≠ влажные пятна визуально (три разных шума).
3. Фермы на бедной почве растут медленнее (мин множитель 0.09 при двойной засухе).
4. `JobValidator fixed=0`, тик fertility <1мс раз в 60 геймминут.

## 19. Реалистичная генерация: разбор критики и что берём

> Источник — ревью пользователя (4 блока: минусы генерации, 10 советов,
> 5 правил влаги, 5 правил плодородия). Ниже — вердикт архитектора по каждому
> пункту: берём / откладываем / отклоняем + куда легло в коде.

### 19.1. Критические замечания → решения

1. **Нет эрозии.** БЕРЁМ частично: полный droplet-симулятор (5000–10000 частиц
   с переносом осадка) — это 262k×шаги на генерацию, дорого для 4 попыток
   `Generate` (~40–110мс бюджет). Вместо этого: (а) `Ridged`-слой для гор
   (`1-|n|`, острые хребты вместо пузырей, совет №2); (б) упрощённая
   термическая эрозия — талус: проход по горе, крутой склон сыпет материал
   вниз до стабилизации (совет №7, дешёвый, O(N)); (в) река слегка врезается
   в heightMap вдоль русла (каньон, −0.05 в радиусе, совет №3-лайт).
   Полную гидравлику откладываем (дорого, риск регресса связности).
2. **Река прорезает будущие горы.** БЕРЁМ: меняем порядок —
   `CarveMountains` ДО реки. Река идёт по heightMap, где горы уже помечены:
   `WalkDownhill` получает штраф за вход в гору, `FillDisc` горы не затирает
   (вода огибает скалу, а не течёт сквозь). `ClearStartArea`/`EnsureLand` —
   после, как раньше.
3. **Влага без rain shadow.** БЕРЁМ: в `Humidity.Initialize` добавляем
   орографический проход — ветер с запада (фиксированный `WindDir = (-1,0)`,
   см. правило влаги №1): наветренный склон (рост высоты против ветра) +
   бонус, подветренный (спад за горой) − штраф. Считается по уже готовой
   heightMap за один линейный проход с запада на восток (дешевле трассировки
   лучей, эффект тот же: мокрый запад склона, сухая тень на востоке).
4. **EnforceLakeLimit оставляет озеро на краю.** БЕРЁМ: компонента, касающаяся
   края, считается морем только если её размер > `SeaMinSize` (напр. 2000 кл.,
   ~0.8% карты). Маленькое «озеро», случайно задевшее границу, — тоже озеро:
   участвует в топ-2/засыпке на общих основаниях, а не авто-море.

### 19.2. Средние замечания → решения

5. **Лес на крутом склоне.** БЕРЁМ: в `PlantForests` запрет деревьев там, где
   градиент heightMap > `SteepSlope` (~0.004/кл — скалы/кустарник, а не лес).
   Slope считается центральными разностями по heightMap (уже есть в руке).
6. **Камни uniform 1.2%.** БЕРЁМ: плотность зависит от биома — у подножия гор
   ×4 (элювий/осыпь), у берега реки/озера ×2 (русловые валуны), в чистом поле
   ×0.3. Нужны маски «рядом с горой/водой» (дешёвый проход 3×3 окном).
7. **Симметричная диффузия влаги.** БЕРЁМ частично: анизотропия по склону —
   вниз по градиенту высоты коэффициент больше (влага стекает в долины),
   вверх — меньше. Полный градиент в тике дорог (heightMap читать на клетку
   ×262k раз в 30 геймминут — на самом деле дёшево, тик редкий). Реализация:
  `Tick` принимает `downhillMask` (пока = highMask гор: heightMap в симуляции
  не хранится, настоящий склон равнины недоступен; на равнине диффузия
  симметричная, анизотропия работает на горных склонах — влага стекает с гор
  в долины, что и даёт главный эффект): `downhill`-клетке нижний сосед весит
  ×2, верхний ×0.5. Хребты сохнут, долины мокнут.
8. **Радиальный falloff = круглый остров.** БЕРЁМ: домножаем радиус на
   низкочастотный шум (warp): `d' = d × (0.75 + 0.5×warpNoise)`, где warp —
   2-октавный fBm scale ~200. Появляются полуострова/бухты, цена — один
   маленький шум. Фьорды/мысы-лайт без полной замены острова.

### 19.3. Мелкие замечания → решения

9. **`Random(seed)` не стабилен между .NET.** БЕРЁМ: свой XorShift32 в
   `NoiseGenerator` (20 строк, детерминирован навсегда, быстрее `Random`).
   Карты со старым `Random` не обязаны совпадать пиксель-в-пиксель —
   сиды те же, распределение то же, попиксельно может съехать (приемлемо).
10. **`HashSet<int>` visited в реке.** БЕРЁМ: `bool[]`/`int[]`-stamp visited
    (O(1), ноль хеширования, ноль аллокаций в цикле). На 512² — один массив
    262k int на вызов реки (1МБ, одноразово, ок).

### 19.4. Советы №1,4,6,8,9,10 — откладываем/отклоняем

- **№1 (биомы Voronoi+Lloyd до террейна).** ОТКЛАДЫВАЕМ: большой рефактор
  (все пороги/скоринг завязаны на единый fBm), выигрыш — разнообразие, риск —
  регресс playability-метрик (связность ≥92%, трава ≥55%). Отдельный эпик.
- **№4 (река как следствие эрозии).** ОТКЛАДЫВАЕМ вместе с полной гидравликой
  (п.1): сначала талус+каньон-лайт, смотрим скриншоты.
- **№6 (типы берегов: скала/песок/болото).** ОТКЛАДЫВАЕМ: требует новых
  TileType/тайлов/рендера (не только `src/`-генерация, но сцены и атласы).
  Задел уже есть: `WaterFeed`/склон/влага позволяют вывести тип берега позже.
- **№8 (опушки/поляны rule-based).** ЧАСТИЧНО УЖЕ ЕСТЬ (clearing 75% +
  edgeNeed-рамп). Добавляем только запрет на склоне (п.5). Полный rule-scatter —
  откладываем.
- **№9 (сезонные реки/вади).** ОТКЛОНЯЕМ для генерации (визуал, не симуляция);
  но сезонность ВЛАГИ берём (правило влаги №5 — синус от времени года).
- **№10 (типы озёр).** ОТКЛАДЫВАЕМ: все озёра пока FloodFill-компоненты;
  типизация (тектоническое/старица/кратер) — отдельный эпик с формой котлована.

### 19.5. Правила влажности → решения

- **П1 (влага = ветер+высота+дистанция).** БЕРЁМ: `Initialize` =
  `base(шум) + waterGrad(sqrt) + orographic(навет/подвет) − altitudeDry`.
  Ветер фиксированный с запада (константа `WindDirX=-1`, без розы ветров).
- **П2 (вода не единственный источник + sqrt-градиент).** БЕРЁМ: бонус у воды
  `feed²×1.4` заменяем на `sqrt(feed/R)×k` — плавный, без ступеньки у берега.
  База `MinMoisture=10` остаётся (грунтовые воды).
- **П3 (анизотропная диффузия).** БЕРЁМ: см. п.7 выше (slope-mask в тик).
- **П4 (лес транспирирует соседям).** БЕРЁМ: в тике клетка рядом с лесом
  (forestMask соседа) получает +1 раз в N тиков (хеш-гейт 1/8, как удобрение).
  Дешевле отдельного «воздушного» слоя, эффект — влажный ореол вокруг рощ.
- **П5 (сезонность).** БЕРЁМ: в тик добавляем `seasonalDelta` от мирового
  времени: лето −1, зима +1 (синус по дню года, амплитуда 1 — карта «дышит»,
  механика почти не дёргается). `Tick` принимает `season` float (−1..+1).

### 19.6. Правила плодородия → решения

- **П1 (плодородие ≠ влажность).** УЖЕ ЕСТЬ (§18: свой слой +4242). Дополняем:
  болото (мокро+низко) − штраф, пойма (у воды+низко) + бонус.
- **П2 (возраст ландшафта).** БЕРЁМ частично: горы − бедно (уже 0),
  пойма реки/озера + бонус (свежий аллювий, `feed>0` + низко → +40),
  под лесом старт +20 (органика). Тектонику/миллионы лет не моделируем.
- **П3 (накопление, а не константа).** ЧАСТИЧНО УЖЕ ЕСТЬ (тик: лес +1,
  огород −1, диффузия /64). Добавляем восстановление под паром: клетка без
  огорода медленно ползёт к базовому значению (+1 раз в 8 тиков до base).
  Базу храним отдельным `byte[]` (снимок Initialize, 256КБ).
- **П4 (наклон).** БЕРЁМ: в `Initialize` штраф за склон:
  `fert *= 1 − slopePenalty` (круто >15° → до −70%, полого <3° → −10%
  заболачивание низин компенсируется пойменным бонусом П2).
- **П5 (видимость).** ЧАСТИЧНО УЖЕ ЕСТЬ (зелёный оверлей §18.5). Дополнительно:
  бедная почва (<20) визуально не отличается на траве — оставляем на оверлей
  (перекраска травы — рендер-эпик, не генерация).

### 19.7. Порядок генерации после изменений

```
heightMap(fBm) → +ridged×0.3 → islandFalloff(warp) → вода → EnforceLakeLimit(sea-порог)
→ CarveMountains(талус) → река(штраф гор, каньон-врез, без затирания гор)
→ EnsureLand → ClearStart → moist-слой → forests(без склона) → stones(по биому)
→ Humidity.Init(ground+moist+height: base+sqrt-water+rains shadow+altitude)
→ Fertility.Init(ground+fertile+height+waterFeed: base+пойма+лес+склон-штраф)
```

Приёмка: `dotnet build` чистый; реки не текут сквозь горы (grep-скрин:
0 водных клеток с height>mountainThreshold на тестовом сиде); лес не стоит
на склоне; у гор — осыпь камней; запад склонов мокрее востока; острова не
идеально круглые; `JobValidator fixed=0`.

### 15.7. Риски и «не делать»

- **Не включать compute в прод при `gl_compatibility`** — упадёт/вернёт null (доки 4.7, §15.0).
- **Не переносить на GPU:** A*/BFS-FlowField (ветвления), lock-секции менеджеров, снапшоты
  (bandwidth, не compute), рост культур (мало/редко), fBm-генерацию (одноразовая, уже быстрая).
- **Не читать из RenderingServer/GPU каждый кадр** (stall) — только one-way CPU→GPU для визуала.
- **Не использовать GPUParticles для агентов** (нужны память/логика/readback).
- Не менять `project.godot` рендер / `scenes/` / `Game.csproj` в этом плане (смена рендера — отдельно).

## 20. Фермерство: окно Farm.tscn + урожай от почвы + истощение −20%

> Ответы пользователя (собраны через вопросы, сентябрь 2026):
> урожай — чем лучше земля, тем больше зерна (5 минимум, 40 максимум);
> культура одна — пшеница; истощение — каждый урожай умножает плодородие
> клетки на 0.8 (100 → 80 → 64), пустая земля сама медленно растёт обратно;
> кнопки Farm ↔ JobDisplay переключают окна, кнопка назад видна только при
> переходе из фермы; подсказка про клетку — всплывашка рядом с мышкой внутри
> окна фермы. Код НЕ менялся — это проектный документ для `@developer`.

### 20.1. Что меняется в двух словах (для не-эксперта)

1. Нажали на ферму — открывается окно **Farm.tscn** (раньше было Garden.tscn).
2. В окне видно: название («Ферма #5»), сколько клеток, что растёт (пшеница).
3. Земля стала важнее: на хорошей земле (влажно + плодородно) с 1 клетки
   падает много зерна (до 40), на плохой — мало (от 5).
4. Скорость роста везде одинаковая — земля влияет только на урожай.
5. Каждый собранный урожай отнимает у клетки пятую часть плодородия
   (было 100 → стало 80 → потом 64). Без посадок земля сама отрастает.
6. При наведении на клетку внутри окна фермы — всплывашка рядом с мышкой:
   влажность, плодородие, что растёт.
7. Кнопка в ферме ведёт в окно работ (JobDisplay), кнопка назад видна только
   если пришли из фермы. В окне работ для фермы — те же ползунки, что у
   обычных зон (тайлы, рабочие, приоритет).
8. Чинится баг: в режиме карты «Влажность» при наведении на клетку не видно
   цифру влажности.

### 20.2. Окна: Farm.tscn вместо Garden.tscn

**Файлы:** `src/ui/controllers/GardenController.cs` (переделать в `FarmController`),
`scenes/ui/Farm.tscn` (уже есть — использовать её, `Garden.tscn` не удалять,
мало ли что ссылается).

**Что внутри Farm.tscn (имена нод — как в сцене):**

| Нода в сцене | Что показывает |
|---|---|
| `Label` (верхняя, размер 40) | Название: «Ферма #5» |
| `Label` «N клеток» (внутри ScrollContainer) | Сколько клеток: «12 кл.» |
| `Description` (RichTextLabel) | Про пшеницу (текст уже в сцене, не трогать) |
| `AmountOfResources` (Label) | «Нужно зерна: 12» (по 1 на клетку) |
| `CheckMark` (кнопка с галочкой) | Вкл/выкл автопосадка (зелёная = вкл, бледная = выкл) |
| `esc` (кнопка с крестиком) | Закрыть окно |
| `Infoormation` (кнопка с «i») | Показать/спрятать подсказку про клетку |
| `ButtonForJobDisplay` | Открыть окно работ (JobDisplay) для этой фермы |
| `InfoWindow` (Label) | ВНИМАНИЕ: в сцене лежит за краем окна (offset_left = −236, не видна). НЕ чинить позицией в сцене — сцены не трогаем. Вместо этого кодом создать свою всплывашку рядом с мышкой (см. §20.5) |

**Как переделать GardenController (по шагам):**

1. Переименовать класс `GardenController` → `FarmController`
   (файл `GardenController.cs` можно переименовать в `FarmController.cs`,
   старое имя оставить как `// TODO: удалить после проверки` — чтобы ничего
   не сломать за один шаг).
2. В `LoadScene()` искать сначала `"res://scenes/ui/Farm.tscn"`,
   потом старые пути Garden (запасной вариант, если Farm не найдётся).
3. Привязки нод по именам из Farm.tscn:
   - заголовок = `FindChild("Label")` — ПЕРВЫЙ найденный Label может быть
     «N клеток», а не заголовок! Поэтому брать так: корневой `Control` →
     прямой ребёнок с именем `Label` (тот, у которого `offset_right = 502`,
     размер шрифта 40). Если сомневаешься — искать Label с текстом «Ферма»
     или самый верхний (offset_top = 0). Запасной вариант: искать по
     `unique_id=63811646` нельзя из кода — по имени и позиции.
   - счётчик клеток = Label «N клеток» (внутри ScrollContainer, размер 24).
   - ресурсы = `FindChild("AmountOfResources")`.
   - галочка автопосадки = `FindChild("CheckMark")` (их ДВЕ в сцене с одним
     именем! Одна внутри ScrollContainer, вторая — корневая рядом с esc.
     Брать КОРНЕВУЮ: `root.GetNode("CheckMark")` напрямую, не рекурсивный
     FindChild. Вторую (внутреннюю) — игнорить или спрятать кодом).
   - esc = `FindChild("esc")` (маленькая, имя строчными).
   - кнопка работ = `FindChild("ButtonForJobDisplay")`.
   - кнопка информации = `FindChild("Infoormation")` (внимание — опечатка
     с двумя «o» в сцене, искать именно так!).
   - `InfoWindow` из сцены НЕ использовать (лежит за краем, невидимая) —
     оставить как есть, кодом создать свою всплывашку (см. §20.5).
4. Все подписки `FarmZoneManager.Instance.OnZoneSelected/OnZoneDeselected`
   остаются как были — меняется только внешний вид окна.
5. `HUDController.SetupGardenController()` — создавать `FarmController`
   вместо `GardenController`. Имя ноды `"GardenController"` можно оставить
   (чтобы не сломать `FindChild`), либо поменять везде сразу — решить
   на месте, главное чтобы `Instance` находился.
6. `WorkZoneController.OnZoneSelected` закрывает окно фермы при выборе
   рабочей зоны — поменять тип `GardenController.Instance` на новый.
   То же в `HUDController._UnhandledInput` (Escape) и `OnPrioritetButtonPressed`.

**Названия ферм:** `ZoneManager.CreateFarmZones` уже даёт `"Грядка #id"`.
Поменять префикс по умолчанию с `"Грядка"` на `"Ферма"` (параметр
`namePrefix = "Грядка"` → `"Ферма"`), а при сплите в `RemoveTiles`
`"Грядка #{newId}"` → `"Ферма #{newId}"`. Старые зоны «Грядка #» останутся
со старыми именами — это нормально, новые будут «Ферма #».

### 20.3. Урожай зависит от земли, скорость роста — нет

**Правило (простыми словами):** растёт всё одинаково быстро (60 секунд
на фазу, 4 фазы = 4 минуты до сбора). А сколько зерна упадёт — решает земля:
влажно + плодородно = до 40, сухо + бедно = от 5.

**Текущее состояние:** `CropGrowthManager.UpdateGrowth` умножает
`GrowthTimer` на `soilMul = humMul × fertMul` (строки 100–105) — это ВЛИЯНИЕ
НА СКОРОСТЬ. Его убрать. `HarvestCrop` кидает `ParallelRng.Next(5, 41)` —
случайно, без земли. Его поменять.

**Интерфейсы (конкретный C#):**

```csharp
// src/core/world/FarmYield.cs (новый файл, чистый C#, без Godot)
// Один метод на всех: и симуляция, и окно фермы показывают одно число.
namespace Game.Core;

public static class FarmYield
{
    public const int MinDrop = 5;
    public const int MaxDrop = 40;

    /// <summary>
    /// Урожай с 1 клетки: среднее между Min и Max по качеству земли.
    /// Качество = (humMul + fertMul) / 2, каждый множитель 0.3..1.0.
    /// Плохая земля (0.3+0.3)/2=0.3 → ~5-10; хорошая 1.0 → ~30-40.
    /// Внутри — случайный разброс ±20% через rng-параметр (долю).
    /// </summary>
    public static int RollDrop(float humMul, float fertMul, double rand01);
}
```

Формула внутри `RollDrop` (простая, без сюрпризов):
`quality = (humMul + fertMul) * 0.5f` (0.3..1.0);
`mean = MinDrop + (MaxDrop - MinDrop) * ((quality - 0.3f) / 0.7f)`;
`result = mean * (0.8 + rand01 * 0.4)` → кламп 5..40 → `(int)`.
Пример: оба множителя 1.0 → mean=40 → 32..40. Оба 0.3 → mean=5 → 5..6.

**Изменения в `CropGrowthManager`:**

1. `UpdateGrowth`: убрать чтение `ctx.Humidity`/`ctx.Fertility` и умножение
   на `soilMul` — таймер растёт как `crop.GrowthTimer += deltaTime`
   (скорость везде одинаковая). Параметр `ctx` оставить (сигнатуру не ломать —
   зовётся из `AgentSimulationThread`), внутри станет не нужен для роста.
   Комментарий про «мягкие множители» на строках 98–99 удалить/переписать.
2. `HarvestCrop(x, y, ctx)`: перед `SpawnItems` прочитать землю клетки:
   `hum = ctx?.Humidity?.Get(x, y) ?? 100`, `fert = ctx?.Fertility?.Get(x, y) ?? 100`;
   `humMul = HumidityMap.GrowthMultiplier(hum)`, `fertMul = FertilityMap.GrowthMultiplier(fert)`;
   `drop = FarmYield.RollDrop(humMul, fertMul, ParallelRng.NextDouble())`.
   Дальше как было: `SpawnItems(x, y, Grain, drop)`, автопосадка без изменений.
3. Сразу после сбора — истощение клетки (см. §20.4).
4. `GrowthMultiplier` в обоих Map-классах НЕ менять (они теперь означают
   «качество земли для урожая», а не «скорость роста» — переименовывать
   не надо, только комменты подправить).

### 20.4. Истощение −20% за урожай + самовосстановление

**Правило (простыми словами):** собрали урожай с клетки — её плодородие
умножается на 0.8 (было 100 → стало 80 → потом 64 → потом 51...).
Земля без посадок сама потихоньку растёт обратно к начальному значению
(это уже умеет `FertilityMap` через `_base` — см. ниже).

**Текущее состояние:** `FertilityMap.Tick` уже истощает грядки на −1 за тик
(строка 191–192) и уже восстанавливает пустые к `_base` +1 раз в 8 тиков
(строки 195–197). Но тик редкий (раз в час игрового), а требование —
«минус пятая часть за каждый урожай». Поэтому добавляем точечное истощение
при сборе + оставляем фоновый тик как есть (он даёт медленный дрейф и
восстановление — именно то, что просили: «сама растёт»).

**Интерфейс:**

```csharp
// src/core/world/FertilityMap.cs — добавить метод:
/// <summary>
/// Истощение после сбора урожая: значение × 0.8 (100 → 80 → 64...).
/// Кламп 0..200. _base НЕ трогаем — восстановление идёт к начальному.
/// Потокобезопасность: тот же lock-подход, что у Get/Set
/// (сейчас Get/Set без lock — добавить внутренний lock-объект,
/// т.к. HarvestCrop зовётся из параллельного Commit, а Tick — из sim-потока).
/// </summary>
public void DrainAfterHarvest(int x, int y);
```

Реализация `DrainAfterHarvest`: `int i = y*Width+x` (с OOB-гардом);
`_cur[i] = (int)(_cur[i] * 0.8f)` (дробь отбрасывается: 100→80, 80→64,
64→51, 51→40...). Swap-буфер `_nxt[i]` подтянется следующим тиком сам
(тик пересчитывает всё из `_cur`), специально трогать не надо.

**Потокобезопасность (важно!):** сейчас `Get/Set` в `FertilityMap` без lock,
а `Tick` идёт `Parallel.For` + swap массивов. `DrainAfterHarvest` будет
зваться из `HarvestJobHandler.Commit` (параллельные воркеры) одновременно
с чтением `Get` из тех же воркеров. Гонка на одном `int` безвредна
(запись атомарна, stale на тик незаметен), НО swap `_cur/_nxt` в `Tick`
может съесть истощение если оно попало в старый `_cur` после копирования.
Решение простое: вызывать `DrainAfterHarvest` из `HarvestCrop` (он уже идёт
под `lock (_lock)` менеджеров? — нет, `HarvestCrop` снимает lock до дропа).
Поэтому: истощение делать ВНУТРИ `CropGrowthManager.HarvestCrop`, где уже
есть `ctx.Fertility`, сразу после `SpawnItems`, без доп.локов — потеря
одного истощения раз в час при совпадении с тиком приемлема (следующий
урожай доберёт). В план фиксируем: без нового lock-объекта, документировать
комментом `// RACE: тик раз в час может съесть одно истощение — ок`.

**Кто зовёт:** `CropGrowthManager.HarvestCrop` в конце:
```csharp
ctx?.Fertility?.DrainAfterHarvest(x, y);
```
`ctx` уже есть параметром — ничего пробрасывать не надо.

**Самовосстановление:** уже работает (пар к `_base` в тике). Ничего не делать,
только проверить что `_base` заполняется в `Initialize` (да, строки 135–137).
В приёмке: убрать все культуры с клетки → за несколько часов игрового
плодородие ползёт вверх к стартовому.

### 20.5. Всплывашка про клетку внутри окна фермы (InfoWindow)

**Проблема:** `InfoWindow` в Farm.tscn лежит за краем окна
(`offset_left = −236`, отрицательный — не видна) и это Label, а не окно.
Сцены не трогаем — чиним кодом.

**Решение:** `FarmController` создаёт кодом свой Label-тултип (близнец
`HumidityTooltip` из `HUDController`):
- создаётся в `LoadScene()` после привязок: `new Label { TopLevel = true,
  ZIndex = 200, MouseFilter = Ignore, font_size = 20 }`, `AddChild`.
- метод `ShowCellAt(mouseGlobalPos)`: мышка → клетка:
  `tilePos = worldPos / 64` (TileSize 64; мировые координаты из
  `GetGlobalMousePosition()` — контроллер FullRect, совпадают).
- текст: `Влага: {m}% ({сухо|норма|мокро})`, `Плодородие: {v} ({бедно|норма|богато})`,
  третья строка — что на клетке: `Пшеница (фаза {stage}/4)` / `Пусто` /
  `Не ферма` (если клетка не в выбранной зоне — тултип прячем).
- слова-категории — те же пороги, что в `HUDController`:
  влага `<40 сухо, ≤130 норма, иначе мокро`; плодородие `<60 бедно,
  ≤140 норма, иначе богато`.
- виден только если: окно фермы открыто + кнопка `Infoormation` включена
  (тумблер: первое нажатие — показать режим подсказок, второе — скрыть)
  + мышка над клеткой своей фермы. Иначе `Visible = false`.
- в `_Process(delta)` — двигать за мышкой (`Position = mouse + (16,16)`),
  текст обновлять только при смене клетки (кэш `_lastX/_lastY`, как в
  `SelectTool` — не жечь `Text=` каждый кадр).
- данные: `MapRenderer.Instance.Humidity/Fertility`, культура —
  `CropGrowthManager.Instance.TryGetCrop(x, y, ...)`, принадлежность зоне —
  `FarmZoneManager.Instance.TryGetZoneAt` + сверка `zone.Id == SelectedZone.Id`.

**Кнопка `Infoormation`:** тумблер `_infoMode` + подсветка Modulate
(как humidity-кнопка в HUD: вкл — яркая, выкл — обычная). По умолчанию ВЫКЛ
(чтобы не мешала), включается по клику.

**Кнопка `ButtonForJobDisplay`:** по нажатию — открыть JobDisplay для этой
фермы (см. §20.6), окно фермы закрыть (или оставить под ним — решить как у
WorkZone: там окно одно, взаимоисключение; здесь тоже закрываем ферму,
открываем работы, назад — возвращаемся).

### 20.6. Переход Farm ↔ JobDisplay

**Требование:** в ферме кнопка ведёт на JobDisplay; в JobDisplay кнопка
`ButtonForFarm` видна только когда перешли с фермы, иначе скрыта
(в сцене `visible = false` по умолчанию — так и оставить).

**Текущее состояние:** `WorkZoneController` грузит `JobDisplay.tscn`,
находит `NameWork/Tiles/Worker/Prioritet` + слайдеры, `ButtonForFarm`
НЕ привязан вообще (в `LoadScene` его нет — найти и привязать).
`FarmZoneManager`/`ZoneManager` НЕ знают про Work-зоны ферм: ферма — это
`ZoneKind.Farm`, а слайдеры тикают `WorkZoneManager` (`ZoneKind.Work`).
Диспатч `DispatchZones` раздаёт только Work-зоны (§ZoneManager.cs:500).

**Решение (минимальное, без переделки диспатча):** окно работ для фермы
показывает ТЕ ЖЕ ползунки, но в режиме «только чтение + приоритет»:
- `Tiles` — показывает число клеток фермы (слайдер заблокирован:
  `Editable = false`, ферма не меняет размер через работы).
- `Worker` — показывает/меняет лимит? У Farm-зон лимита нет. Минимально:
  слайдер заблокирован, показывает «∞» (как лимит 0 в Work-зонах).
- `Prioritet` — РАБОТАЕТ: дёргает `JobPriorityManager` для категории
  Farming (через существующий `SetWorkPriorityOverride`? — нет, он для Work.
  Нужен прямой вызов: по маске типов Planting/Harvesting/Farming найти
  категории и выставить приоритет — вынести общий хелпер из
  `ZoneManager.SetWorkPriorityOverride` в статик, переиспользовать).
- Кнопка `ButtonForFarm` — «назад в ферму»: закрыть JobDisplay, открыть
  FarmController на той же зоне.

**Откуда JobDisplay знает что его открыли с фермы:** статическое поле-флаг
в `WorkZoneController`: `public static int CameFromFarmZoneId = -1`
(−1 = пришли не с фермы). `FarmController` перед открытием ставит
`CameFromFarmZoneId = zone.Id` + вызывает
`WorkZoneManager.ShowForFarmZone(zoneId)` (новый метод, см. ниже);
`WorkZoneController` при открытии проверяет флаг: если `!= -1` —
показать `ButtonForFarm`, иначе спрятать. При закрытии JobDisplay через esc
или выбор другой зоны — сбросить в −1. Нажатие `ButtonForFarm` — закрыть
работы, `FarmZoneManager.SelectZoneAt` на клетку той фермы (переоткрыть
окно фермы), сбросить флаг.

**Новый метод для открытия работ по ферме** (в `WorkZoneController`):
```csharp
/// <summary>
/// Открыть JobDisplay для Farm-зоны (только чтение тайлов/лимита +
/// рабочий приоритет Farming). ButtonForFarm видна, назад — в ферму.
/// </summary>
public void ShowForFarmZone(int farmZoneId);
```
Внутри: взять зону через `FarmZoneManager.Instance.TryGetZoneById`,
заполнить `_titleLabel` («Ферма #5»), Tiles.Number = число клеток,
Worker.Number = «∞», приоритет = текущий глобальный Farming
(`JobPriorityManager.Instance.GetPriority(JobCategory.Farming)` —
проверить имя enum на месте), заблокировать Tiles/Worker слайдеры
(`Editable = false`), приоритетный — рабочий. `_rootPanel.Visible = true`.

**Альтернатива (если слайдеры Farm/Work сильно конфликтуют):** тот же
`ShowForFarmZone`, но Tiles/Worker строки целиком прячем (`Visible = false`),
оставляем только название + приоритет + назад. Решение — на месте по факту
в сцене (что проще, то и берём; зафиксировать в отчёте шага).

**Что НЕ делаем:** не заводим Work-зону-дубль для фермы, не трогаем
`DispatchZones` (фермерские работы и так разбираются общим диспатчем через
`JobIndex`), не меняем `JobPriorityManager` (только читаем/пишем через
существующие методы).

### 20.7. Баг: в режиме влажности не видно цифру при наведении

**Симптом:** включили карту влажности, водим мышкой — синева есть, а цифры
влажности у курсора нет.

**Причина (гипотеза №1, проверить на месте):** `SelectTool.OnHover` зовёт
`ShowHumidityAt` при КАЖДОМ движении, но внутри `ShowHumidityAt`:
`ground[x, y] != TileType.Grass → Visible = false`. На воде/горе/краю карты
тултип прячется — это правильно. А на траве должен показываться. Если не
показывается и на траве — смотрим дальше.

**Причина (гипотеза №2, вероятная):** `HUDController.SetMouseFilterRecursive`
ставит `Ignore` всем не-кнопкам, а `_humidityTooltip` создаётся в
`SetupHumidityTooltip()` ДО рекурсии? Нет — `SetupMapModeControls` (легенда)
вызывается до `SetupHumidityTooltip`, а `SetMouseFilterRecursive` — после
обоих (строка 139). Рекурсия идёт по детям HUD (`this`), тултип — ребёнок
HUD → ему тоже прилетит `Ignore`. Но тултипу `Ignore` и нужен... `Ignore`
не прячет, только клики пропускает. Значит дело не в фильтре.

**Причина (гипотеза №3, самая вероятная):** тултип создаётся с
`TopLevel = true` + `Position = GetGlobalMousePosition() + (16,16)`.
`GetGlobalMousePosition()` у Control в CanvasLayer возвращает координаты
канваса, а `TopLevel` контрол позиционируется в вьюпорте — при ненулевом
оффсете CanvasLayer тултип улетает за экран. Плюс `FollowHumidityTooltip`
каждый кадр переставляет позицию — если `Visible=false` в `ShowHumidityAt`
(режим не Humidity / не трава), а потом `ShowHumidityAt` снова зовётся
только при смене клетки (кэш `_lastHumidityX/Y` в SelectTool!) — тултип
может остаться спрятанным пока мышь стоит. Но баг про «не видно вообще» —
скорее позиция за экраном.

**Что сделать `@developer` (по шагам):**

1. Проверить в игре: включить влажность, навести на траву в центре карты.
   Если цифры нет — поставить брейк/лог в `ShowHumidityAt`: доходит ли до
   `Visible = true`, какие `x, y, ground[x,y], CurrentMapMode`.
2. Если доходит, но не видно — дело в позиции: заменить
   `_humidityTooltip.Position = GetGlobalMousePosition() + ...` на
   `_humidityTooltip.GlobalPosition = ...` (для TopLevel в CanvasLayer
   правильная именно GlobalPosition). То же в `FollowHumidityTooltip`
   и в `ShowFertilityAt` (близнец — тот же баг).
3. Если не доходит — проверить `map.Humidity == null` (карта готова?),
   `map.MapData.Ground == null`, `CurrentMapMode` (кнопка точно включила?).
4. Дополнительно: `SelectTool` кэширует клетку и не дёргает HUD пока стоим —
   а `FollowHumidityTooltip` двигает тултип каждый кадр. Если тултип был
   спрятан (вода), а мы ушли на траву БЕЗ смены кадра hover (мышь стоит,
   камера едет WASD) — цифра не появится. Фикс: в `HUDController._Process`
   раз в 0.25с перезапрашивать `ShowHumidityAt` для клетки под курсором
   (пересчёт tile из мыши через `MapRenderer`: `mouseWorld / 64`).
   Дешёво (4 раза в секунду), баг «камера уехала — цифра не обновилась» чинит.
5. Проверка приёмки: влажность вкл → на траве «87% норма» у курсора;
   плодородие вкл → «120 норма»; вода → тултип спрятан; камера едет —
   цифра обновляется.

### 20.8. Параметры фермы: клетки, название, культуры

- **Сколько клеток:** `zone.TotalTiles` (`Tiles.Count`) — уже есть.
  Показать в «N клеток» + в `AmountOfResources` («Нужно зерна: N»).
- **Название:** `zone.Name` («Ферма #id») — в верхний Label.
- **Виды культур:** только пшеница (ответ пользователя). В `Description`
  текст уже в сцене — не трогать. Отдельного выбора культуры НЕТ,
  `RequiredSeedItem = Grain` уже стоит по умолчанию. Никаких новых enum/ItemId.
- **Дополнительно показать (мелочь, но просят «параметры»):**
  посажено/всего: `PlantedCount/TotalTiles`? `PlantedCount` в `Zone` есть,
  но кто его обновляет — проверить grep'ом; если всегда 0 — посчитать на
  месте: пройтись по `zone.Tiles`, `CropGrowthManager.HasCrop` → «растёт: K/N».
  Дешёво (фермы маленькие), обновлять раз в секунду в `_Process` пока окно
  открыто (кэш строки, `Text=` только при изменении).

### 20.9. Порядок внедрения для `@developer` (каждый шаг — компилируется)

1. **Шаг 1 — окно Farm.tscn:** переименовать/переделать `GardenController`
   в `FarmController` (§20.2), префикс «Ферма #». `dotnet build` + клик по
   ферме открывает Farm.tscn с названием и числом клеток. Garden.tscn не удалять.
2. **Шаг 2 — урожай от земли:** `FarmYield.cs` + `UpdateGrowth` без soilMul
   + `HarvestCrop` с `RollDrop` (§20.3). Проверка: сухая/бедная — 5–10,
   мокрая/жирная — 30–40. `JobValidator fixed=0`.
3. **Шаг 3 — истощение:** `FertilityMap.DrainAfterHarvest` + вызов из
   `HarvestCrop` (§20.4). Проверка: было 100 → после сбора 80 → потом 64;
   пар восстанавливает.
4. **Шаг 4 — всплывашка в ферме:** тултип + `Infoormation`-тумблер (§20.5).
   Проверка: навёл на клетку фермы — влага/плодородие/фаза рядом с мышкой.
5. **Шаг 5 — Farm ↔ JobDisplay:** `ButtonForJobDisplay` + `ButtonForFarm`
   + `ShowForFarmZone` (§20.6). Проверка: туда-обратно, флаг сбрасывается,
   с обычных зон кнопка назад скрыта.
6. **Шаг 6 — баг влажности:** диагностика + фикс позиции/обновления (§20.7).
   Проверка: цифры у курсора в обоих режимах, камера едет — обновляется.
7. **Шаг 7 — параметры:** «растёт K/N», «нужно зерна N» (§20.8).
   Финальный прогон: 10k агентов, ферма полный цикл, overlay без регресса.

### 20.10. Приёмка (числа и картинки)

1. Клик по ферме → Farm.tscn: «Ферма #5», «12 кл.», пшеница, галочка работает.
2. Рост: все фазы по 60с вне зависимости от земли (замер таймером стадии).
3. Урожай: плохая земля — 5–10, хорошая — 30–40 (10 сборов на каждой, скрины).
4. Истощение: 100 → 80 → 64 (лог `Fertility.Get` до/после сбора).
5. Пар: без посадок плодородие растёт обратно к базовому за часы игрового.
6. Всплывашка в ферме: влага + плодородие + фаза у курсора, за краем окна
   ничего невидимого не болтается (старый InfoWindow не используем).
7. Farm → JobDisplay → Farm: кнопки видно/скрыто по правилу, слайдеры для
   фермы в read-only (или спрятаны — зафиксировать что выбрано).
8. Влажность/плодородие на карте: цифры у курсора есть, на воде — нет.
9. `dotnet build` чистый после каждого шага, `JobValidator fixed=0`.

### 20.11. Риски и «не делать»

- НЕ трогать `scenes/` (позицию InfoWindow в сцене не чинить — кодом мимо).
- НЕ добавлять новые культуры/ItemId (только пшеница/зерно).
- НЕ менять скорость роста обратно (60с/фаза — константа, земля влияет
  только на урожай).
- НЕ заводить Work-зону-дубль для фермы (диспатч не трогать).
- НЕ ставить lock в `DrainAfterHarvest` (гонка раз в час приемлема, см. §20.4).
- НЕ переименовывать `GrowthMultiplier` (смысл «качество земли» — только комменты).
- Откат каждого шага — одной заменой вызова (фасады `Farm/WorkZoneManager` живы).

> Приёмы Syx из `code.txt`: рисовать только экран, LOD по зуму, таблицы заранее,
> ранний выход, ноль `new` в кадре, батчи одним flush. `dotnet build` чистый, 0 варнингов.

- `AgentRenderer` (спрайт 32x32, НЕ 64): `AgentHalf=16` (ноги), `AgentQuarter=8` (якорь)
  вместо размазанных `*0.5f/*0.25f` — математика та же, читается явно. Формулы:
  `sLen=Length+16`, `anchor=dir*(sLen/2-8)`, тень от `py+16`. Шейдер `AgentShadow.gdshader`
  не меняли (там уже ±16). LOD: зум камеры `<0.35` — тень через кадр (−50% CPU прохода 10k).
- `ShadowCasterRenderer`: frustum culling по камере (`UpdateView` O(1) в Tick, запас
  `MaxShadowLengthPx` от обрезки у края). `PushBuffer` пишет только видимых + cap 65536.
  Камера уехала за пол-экрана — пересбор (`viewMoved`), солнце то же. Без камеры
  (юнит) — всех, как раньше.
- `ItemShadowRenderer` (новый, `src/ui/renderers/ItemShadowRenderer.cs`): один MultiMesh
  cap 4096 на мелочь, Z3. Длина ×0.35, ширина ×0.5, альфа ×0.5 (низкие — тень короткая).
  `SetSun` из Main, `PushSpots(позиции, размер)` из рендереров. Ночь/полдень — early-out.
- Проводка: `Main._Ready` создаёт `ItemShadowRenderer`, отдаёт в `GroundItemRenderer` (44px),
  `CropRenderer` (64px, только стадии 3-4 — мелочь 1-2 пропускаем), склад — через
  `MapRenderer.StockpileItems` (рендерер склада живёт там же ребёнком).
- Свет: без изменений (один `CanvasModulate`, Light2D нет — см. DayNightCycle шапку).
# 21. Ghost склада/зоны, жёсткий пайплайн стройки, визуал прогресса (сентябрь 2026)

> Реализовано. `dotnet build` чистый, 0 варнингов. Правило src-only: `scenes/` не тронуты.

## 21.1. Ghost-призрак склада (баг «вместо призрака — стены»)

- Причина: `_ghostLayer.TileSet` — сценовый wall-набор, где под номерами
  `SourceGrass=0` / `SourceGardenBed=6` лежат СТЕНЫ (terrain-источники).
  `ZoneDraftTool` рисовал зону этими номерами — склад заливался стенами.
  Мебель рисовала `_sourceId` из building-набора (5/14/15/16) — в ghost-наборе
  это тоже стены/пустота.
- Фикс (`MapRenderer`): `_ghostLayer.TileSet` = ДУБЛЬ сценового TileSet
  (`Duplicate()`) + одиночные Source травы/грядки/мебели под новыми номерами
  `GhostGrassSource=100, GhostGardenBedSource=101, GhostWorkTableSource=105,
  GhostBedSource=114, GhostBenchSource=115, GhostNightstandSource=116`
  (`CloneTileSetWithGhostSources`, `AddGhostSingleTile`). Дубль никому
  не мешает: wall/blueprint-слои сидят на оригинале.
- `GhostSourceForZone(zoneKind)` (warehouse — трава, остальное — грядка),
  `GhostSourceForBuilding(type)` (мебель; стены идут terrain-ghost, -1 = не рисовать).
- `ZoneDraftTool.RepaintGhost` и `BuildTool.OnHover/OnDrag` переведены на ghost-Source.
- Проверка: черновик склада = фиолетовая... нет — трава-призрак + кольцо стен;
  ферма = грядка-призрак; мебель = свой спрайт; стен-призрак — terrain как раньше.

## 21.2. Жёсткий пайплайн: расчистка → поднос → стройка (per-cell, без счётчиков)

- Причина зависаний: счётчики `PendingClear/PendingSupply` умирали от рассинхрона
  (работа отменена/удалена вне пайплайна — Notify не пришёл — стройка висела вечно).
  Плюс `BeginSupply` выкидывал клетки с предметами (дропы рубки!), `HasAvailableLogs`
  гейтил доставку при пустом складе, `CanAgentExecute` haul не знал про камень.
- `ConstructionPipeline` переписан на СОСТОЯНИЕ ПО КЛЕТКАМ:
  `CellStage { Clearing=1, Supply=2, Building=3, Done=4 }` + флаги
  `NeedTree/NeedStone/NeedHaul`. Все переходы идемпотентны (повтор = no-op),
  счётчиков нет. `_cellToSite` — обратный индекс (O(1) вместо O(сайты×клетки)).
- Жёсткий гейт `IsStageAllowed(x, y, type)`: расчистка — только Clearing,
  доставка — только Supply, стройка — только Building. Вшит в `CanAgentExecute`
  `TreeChopping/Mining/BlueprintDelivery/Construction/StockpileHauling`
  (вне стройки гейт = true, обычные работы не страдают).
- Notify-точки по клеткам: `NotifyTreeCleared/NotifyStoneCleared/NotifyHaulCleared`
  (хендлеры рубки/добычи/haul), `NotifySupplyDone(x, y)` (`DeliverLogsToBlueprint`),
  `NotifyWallBuilt(x, y)` (`ConstructionJobHandler`). Старые счётчиковые
  `NotifyClearDone(siteId)/NotifySupplyDone(siteId)` — `[Obsolete]` no-op.
- `ReconcileTick(ctx)` (sim-поток, wall-clock гейт 5с): сверка флагов с фактом
  мира (`ctx.TreeOnGrass/StoneOnGrass`, `HasItemsAt`, стены/здания) + индексом
  (`HasJobAt`) и метками менеджеров. Потерянные работы пересоздаются, фантомные
  флаги снимаются, срубленное вручную — подхватывается. Готовые клетки —
  `PromoteReadyCells` в Supply (с перепроверкой терраина).
- `JobBroker.RegisterConstructionAt(x, y)` (дедуп по индексу) — создание
  Construction-работы для этапа 3 и reconcile.
- ВСЕ постройки через пайплайн: `BuildTool.OnRelease` зовёт `StartSite` и для
  мебели (прямой `AddBlueprintsBatch` удалён). Одна клетка — один сайт
  (дедуп пересекающихся строек в `StartSite`).
- `JobValidator`: чинит только разрешённое гейтом (дерево/камень — Clearing,
  доставка — Supply, стройка — Building), иначе тащил бы этапы назад.
- `StockpileHauling.CanAgentExecute`: гейт по `job.SourceX/SourceY` (источник
  расчистки) + `HasAvailableItemsOfType(Stone)` (камень тоже везётся).
- Приёмка: стройка идёт строго 1→2→3 по клеткам; отмена/удаление работ вручную
  не вешает стройку (reconcile за ≤5с чинит); `JobValidator fixed=0`.

## 21.3. Визуал прогресса ломки/стройки: 6 стадий ProcessOfWork

- Атлас `uid://qqhtvxqngv4a` (ProcessOfWork.png 192×128, сетка 3×2, тайл 64):
  стадии 0..5 = `(0,0) (1,0) (2,0) (0,1) (1,1) (2,1)` — начало (0,0), конец (2,1).
- `WorkProgressTracker` (`src/simulation/building/WorkProgressTracker.cs`, чистый C#):
  `ReportFraction(x, y, fraction)` / `Report(x, y, stage)` (только смена стадии
  уходит в очередь — троттлинг), `Clear(x, y)` (финал/отмена → стирание, stage -1),
  `DrainDirty(dst, maxCount)` (бюджет 256/кадр), `SweepStale(dst, 10с)` (протухшие).
  Lock-free (`ConcurrentDictionary` + `ConcurrentQueue`), менеджеров не зовёт.
- `MapRenderer`: новый `_workProgressLayer` (поверх всего, alpha 0.85) + кодовый
  TileSet из `uid://qqhtvxqngv4a` (`CreateWorkProgressTileSet`, `SourceWorkProgress=200`,
  сцену не трогали). `FlushWorkProgress` в `_Process`: Drain → SetCell/EraseCell,
  SweepStale раз в 2с.
- Хендлеры пишут прогресс в `ExecuteParallel` (доля `WorkProgress/Duration`) и
  `Clear` в `Commit`/`OnCancel`/`RemoveBlueprint`: рубка, камень, стройка,
  вскопка, посадка, сбор. Видно и ломку, и стройку любого объекта, как просили.
- Приёмка: бьёт/строит — поверх клетки листаются 6 картинок 0,0→2,1; закончил/
  отменил — спрайт исчезает (Clear + SweepStale-страховка 10с).
