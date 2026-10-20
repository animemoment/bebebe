# AUDIT-B: глубокий аудит симуляции (потоки, планировщик, джобы, пулы, RNG, численные, offline)

- **Задача:** task-2 (AUDIT-B), исполнитель: `@destroyer` / teammate `audit-sim`
- **Проект:** `D:\bebebe-main (1)\bebebe-main` (Godot 4.7 mono, .NET 8, C#)
- **Режим:** READ-ONLY по `src/`, `scenes/`, `tests/`. Изменён/создан только этот файл.
- **Метод:** ручное чтение кода (read/grep) по всему scope `src/simulation/**` + `src/ui/renderers/AgentRenderer.cs` (потребитель снапшотов), `scenes/main/Main.cs` (жизненный цикл). Инструмент CLI (`DependencyGraphVisualizer.exe`) не использовался: все выводы получены прямым чтением кода и проверкой арифметики переходов счётчиков. Каждое утверждение — с `путь:строка` и цитатой.
- **Всего находок:** 10 (B-01…B-10). Из них P1 — 3, P2 — 7, P0 — 0.
- **Итог по категориям ТЗ:** 1 (многопоточность) — B-01/B-03/B-05/B-09; 2 (планировщик) — B-04; 3 (пулы) — B-08 (+ доказательства отсутствия дефекта); 4 (джобы) — B-01/B-02; 5 (RNG) — B-09; 6 (численные) — B-05 (+ доказательства отсутствия); 7 (offline catch-up) — B-10.

---

## B-01 — Переиспользование jobId (free-list ABA): одна работа назначается двум агентам, чужой job удаляется, счётчики уезжают — P1 — confidence: high (механизм), medium (частота срабатывания)

**Файлы/строки:**
- `src/simulation/jobs/JobSpatialPyramid.cs:260-265` — выдача id из free-list (LIFO)
- `src/simulation/jobs/JobSpatialPyramid.cs:444-446` — возврат id в free-list
- `src/simulation/jobs/JobSpatialPyramid.cs:523-532` — `ReleaseWorkerClaim` без проверки «поколения»
- `src/simulation/jobs/JobSpatialPyramid.cs:758-768` — CAS-захват и переход «unclaimed → fully assigned»
- `src/simulation/jobs/JobDispatcher.cs:165,187` — stale-id берётся из `pool.CurrentJobId[]`
- `src/simulation/structures/ConstructionManager.cs:526-534,604-611` — удаление работ «в полёте» и повторная регистрация в том же вызове

**Код (1) — выдача/возврат id, никакого поколения/версии нет:**
```csharp
// RegisterJob (JobSpatialPyramid.cs:260-265)
int id;
if (_freeHead != -1)
{
    id = _freeHead;
    _freeHead = _nextFree[id];
    _nextFree[id] = -1;
}
...
// RemoveJob (JobSpatialPyramid.cs:444-446)
// Возврат в free-list
_nextFree[jobId] = _freeHead;
_freeHead = jobId;
```

**Код (2) — освобождение слота проверяет только `_active`, т.е. новую инкарнацию того же id:**
```csharp
// JobSpatialPyramid.cs:523-532
public void ReleaseWorkerClaim(int jobId)
{
    if (jobId < 0 || jobId >= _capacity || !_active[jobId])
        return;

    int newAssigned = Interlocked.Decrement(ref _assignedWorkers[jobId]);
    if (!_active[jobId])
        return;

    if (newAssigned == _maxWorkers[jobId] - 1)
    {
        Interlocked.Increment(ref _unclaimedCount);
```

**Код (3) — предикат «работа стала полностью занята» работает по значению счётчика, а не по инварианту:**
```csharp
// JobSpatialPyramid.cs:758-779 (TryClaimInChunk)
int cur = Volatile.Read(ref _assignedWorkers[bestJobId]);
while (cur < _maxWorkers[bestJobId])
{
    int prev = Interlocked.CompareExchange(ref _assignedWorkers[bestJobId], cur + 1, cur);
    if (prev == cur)
    {
        if (cur + 1 >= _maxWorkers[bestJobId])   // ← при cur == -1 условие ЛОЖНО
        {
            Interlocked.Decrement(ref _unclaimedCount);
            Interlocked.Decrement(ref _chunkUnclaimedCount[chunkIndex]);
```

**Дефект.**
`jobId` — это переиспользуемый индекс слота, а агент хранит его в `pool.CurrentJobId[i]` на протяжении всей работы (секунды–минуты). Любой путь удаления работы «в полёте» (напр. `ConstructionManager.UnregisterSiteJobsAndRefund` → `JobDispatcher.UnregisterJobByPos` → `JobSpatialPyramid.RemoveJob`, `JobSpatialPyramid.cs:452-463`/`388-450`) кладёт id в **голову** free-list, поэтому уже **следующая** регистрация получает ровно этот id (LIFO). `RemoveJob` при этом не проверяет `_assignedWorkers > 0` и не сбрасывает `pool.CurrentJobId` агентов, а `ReleaseWorkerClaim` не отличает «старую» инкарнацию id от «новой»: единственная защита — `!_active[jobId]`, но у новой работы `_active == true`.

Арифметика последствий (проверена по коду):
1. Пока агент A ещё держит id `J`, работу `J` удаляют. Затем на `J` регистрируется **другая** работа `K` (`_assignedWorkers[J] = 0`).
2. A завершает свою работу → `JobDispatcher.ReleaseJobWorker` (`JobDispatcher.cs:187`) → `ReleaseWorkerClaim(J)` проходит проверку `_active[J]` (это уже `K`) → `_assignedWorkers[K] = -1`.
3. Агент B легально захватывает `K`: `cur = -1`, CAS `-1 → 0` успешен, но `cur+1 (0) >= max (1)` **ложно** → `_unclaimedCount`/`_chunkUnclaimedCount` **не** декрементируются, работа остаётся помеченной «unclaimed» и, главное, остаётся выбираемой: проверка `Volatile.Read(ref _assignedWorkers[jobId]) >= _maxWorkers[jobId]` (`JobSpatialPyramid.cs:704`) даёт `0 >= 1` = false.
4. Агент C захватывает ту же `K` (CAS `0 → 1`, теперь `1 >= 1` → декремент). **Двое агентов работают одну однократную работу.**
5. Симметричный вариант: если к моменту stale-release у `K` `_assignedWorkers == 1`, декремент даёт `0 == max-1` → счётчики **инкрементируются**, работа снова публикуется как свободная, хотя агент B её ещё выполняет → третий захват.

Кроме дублирования работ тот же stale-id даёт удаление **чужой** свежей работы: хендлеры вызывают `JobDispatcher.TryUnregisterJob(pool.CurrentJobId[i])` (напр. `StockpileHaulingJobHandler`/`ConstructionJobHandler`), и по переиспользованному id удаляется только что созданная (никак не связанная) работа.

**Сценарий отказа (детерминированный, в одном потоке).**
`ConstructionManager.ReconcileTick` (каждые 5 с, уже на sim-потоке) в одном вызове: для «уже построенной» площадки вызывает `CompleteConstruction` → `UnregisterSiteJobsAndRefund` (`ConstructionManager.cs:526-534`) — снимает `StockpileHauling`/`Construction`/… даже если агент ещё назначен (проверки `AssignedWorkers` нет), а ниже в том же цикле для других площадок снова регистрирует работы (`ConstructionManager.cs:631,638,645` → `JobBroker.RegisterBlueprint`/`RegisterConstructionAt`). Новые работы гарантированно получают только что освобождённые id (LIFO). Далее агент, назначенный на снятую работу, при завершении (или при `ReleaseJobWorkerForStuck`/`OnStart`-rollback, `JobDispatcher.cs:112,126,135`) бьёт по чужой работе: удаляет её и/или ломает счётчики (см. п.2–5). Второй, более широкий источник окна — снятие работы из UI-потока (`CancelSites`/`CancelSite`, `ConstructionManager.cs:507-523`) при живой симуляции.

**Минимальный фикс.**
Генерации на слот: `int[] _jobVersion` + передавать `(id, version)` в `pool.CurrentJobId` (или отдельный `pool.CurrentJobVersion[]`), и в `ReleaseWorkerClaim`/`TryUnregisterJob`/`MarkJobUnreachable` сверять версию; при `RegisterJob` — `version++`. Альтернатива-minimum: не возвращать id в free-list, пока `_assignedWorkers[id] != 0` (тогда двойное назначение исчезает, но остаётся «удалить нельзя при живом агенте»).

**Как проверить фикс.**
Юнит-тест: `RegisterJob(J)`, `TryClaimJobForAgent` (assign=1), `RemoveJob(J)`, `RegisterJob(K)` (обязан получить иной id/версию), затем `ReleaseWorkerClaim(J)` — `_assignedWorkers[K]` обязан остаться `0`, `UnclaimedCount` не измениться; и три последовательных `TryClaimJobForAgent` на K должны дать ровно 1 успех.

---

## B-02 — Смешанная синхронизация `_macroJobCounts`: потерянные апдейты → макро-маска L2 гаснет при живых работах → «полосы» незабираемых работ — P1 — confidence: high (структура гонки), medium (наблюдаемость на конкретном прогоне)

**Файлы/строки:**
- `src/simulation/jobs/JobSpatialPyramid.cs:308-311` — инкремент при регистрации (под `_registerLock`, обычный `++`)
- `src/simulation/jobs/JobSpatialPyramid.cs:433-441` — декремент в `RemoveJob` (под `_registerLock`, **неатомарный RMW**)
- `src/simulation/jobs/JobSpatialPyramid.cs:532-543` — инкремент в `ReleaseWorkerClaim` (**вне** `_registerLock`, `Interlocked`)
- `src/simulation/jobs/JobSpatialPyramid.cs:770-778` — декремент в claim-проходе (**вне** `_registerLock`, `Interlocked`)
- `src/simulation/jobs/JobSpatialPyramid.cs:636-661` — потребление: маска + `_macroJobCounts > 0` как гейт глобального скана

**Код:**
```csharp
// JobSpatialPyramid.cs:433-441 (RemoveJob, под _registerLock)
if (wasUnclaimed)
{
    int macroIndex = GetMacroIndex(removedJob.TargetX, removedJob.TargetY);
    _macroJobCounts[macroIndex] = Math.Max(0, _macroJobCounts[macroIndex] - 1); // ← read-modify-write
    if (_macroJobCounts[macroIndex] == 0)
    {
        _macroActiveMask &= ~(1UL << macroIndex);
    }
}
```
```csharp
// JobSpatialPyramid.cs:538-543 (ReleaseWorkerClaim, БЕЗ _registerLock)
int mi = GetMacroIndex(_targetX[jobId], _targetY[jobId]);
Interlocked.Increment(ref _macroJobCounts[mi]);   // ← может быть затёрт RMW выше
lock (_registerLock)
{
    _macroActiveMask |= (1UL << mi);
}
```
```csharp
// JobSpatialPyramid.cs:770-777 (TryClaimInChunk, БЕЗ _registerLock)
int mi = GetMacroIndex(_targetX[bestJobId], _targetY[bestJobId]);
if (Interlocked.Decrement(ref _macroJobCounts[mi]) <= 0)
{
    lock (_registerLock)
    {
        if (_macroJobCounts[mi] <= 0)
            _macroActiveMask &= ~(1UL << mi);
    }
}
```

**Дефект.**
Одно и то же поле `_macroJobCounts[i]` мутируется тремя разными дисциплинами: (а) неатомарный RMW под `_registerLock` (`RemoveJob:437`, `RegisterJob:310`), (б) `Interlocked` **вне** лока (`ReleaseWorkerClaim:539`, `TryClaimInChunk:771`). Лок в (а) не защищает от (б), а `Interlocked` в (б) не защищает от RMW в (а): типичный lost update — поток-регистратор/ремовер читает `_macroJobCounts[i] = N`, параллельно `Interlocked.Increment` меняет на `N+1`, затем RMW пишет `N-1` → инкремент потерян и счётчик оказывается **ниже** реального. Когда счётчик доходит до 0, бит маски снимается (`:439-440`, `:775-776`), и тогда глобальный скан `TryClaimJobForAgent` **не заходит** в этот макро-сектор вообще:
```csharp
// JobSpatialPyramid.cs:643-644
int macroIdx = (agentMacro + mi) & 63;
if ((mask & (1UL << macroIdx)) != 0 && Volatile.Read(ref _macroJobCounts[macroIdx]) > 0)
```
Незабранные работы в этом секторе остаются достижимы только локальным скан-ом «свой чанк / r ≤ 3» — ровно тот симптом, который описан в самом проекте в `AgentSimulationThread.ReportDispatchDistribution` («где-то пашут, где-то не тронуто вообще», `AgentSimulationThread.cs:808-819`).

**Сценарий отказа.**
`RemoveJob` (UI-поток: отмена площадки/склада; или sim-поток: `ReconcileTick`) удаляет работу сектора `M`, одновременно на sim-потоке `DispatchPendingJobs` → `TryClaimInChunk` делает `Interlocked.Decrement` по `M`, а параллельный `ReleaseWorkerClaim` (Phase3b-воркер, хендлер завершил работу) — `Interlocked.Increment` по `M`. Любая из этих операций пары может быть потеряна RMW из `RemoveJob`. Если итог по `M` становится ≤ 0, бит маски снимается, и работы, физически лежащие в `M`, перестают получать агентов до следующего успешного инкремента.

**Минимальный фикс.**
Убрать неатомарные RMW: и в `RemoveJob`, и в `RegisterJob` использовать `Interlocked.Increment/Decrement(ref _macroJobCounts[...])`, а снятие бита маски делать строго под `_registerLock` после `Interlocked.Decrement` (как уже сделано в `TryClaimInChunk`: `if (Interlocked.Decrement(...) <= 0) lock(...) { if (count <= 0) mask &= ~bit; }`). Дополнительно: маску не снимать, если `_chunkUnclaimedCount` соответствующего сектора > 0.

**Как проверить фикс.**
Стресс-тест с двумя потоками: N потоков делают `RegisterJob/RemoveJob` по сектору `M`, M потоков — `TryClaimJobForAgent`/`ReleaseWorkerClaim` по тому же сектору; инвариант-ассерт: `_macroJobCounts[M] == число активных unclaimed работ в M` и `((_macroActiveMask >> M) & 1) == (count > 0)` после join.

---

## B-03 — `Start()` после таймаута `Stop()`: два цикла симуляции на одном пуле + `Reset()` пути под живым воркером — P1 — confidence: medium

**Файлы/строки:**
- `src/simulation/AgentSimulationThread.cs:175-179` — единственная проверка «предыдущая симуляция активна»
- `src/simulation/AgentSimulationThread.cs:1225-1253` — `Stop()` не бросает/не блокирует навсегда, только печатает ошибку
- `src/simulation/AgentSimulationThread.cs:203-209,273-274` — замена `_pool`/`_ctx`, `Initialize()` и повторный `_running = true`
- `src/simulation/pathfinding/HierarchicalPathfinder.cs:126-142` — `Reset()` обнуляет `_state/_cost/_edgeStart/_portals` под `_buildLock`
- `scenes/main/Main.cs:261-268` — вызывающий код печатает ошибку таймаута, но продолжает `ResetColonyState()`

**Код:**
```csharp
// AgentSimulationThread.cs:175-179
if (_running || (_simulationTask != null && !_simulationTask.IsCompleted))
{
    GD.Print("[AgentSimulationThread] Предыдущая симуляция активна, останавливаем перед новым стартом...");
    Stop(5000);          // ← результат не проверяется
}
...
// AgentSimulationThread.cs:203-209
_pool = new AgentDataPool(agentCount);
...
_ctx = new SimulationContext(...);
HierarchicalPathfinder.Instance.Initialize(_ctx);   // → Reset() под _buildLock
...
// AgentSimulationThread.cs:273-274
_running = true;                                    // ← старая петля снова видит true
_simulationTask = Task.Run(SimulationLoop);
```
```csharp
// AgentSimulationThread.cs:1231-1236
if (_simulationTask != null)
{
    cleanStop = _simulationTask.Wait(timeoutMs);
    if (!cleanStop)
        GD.PrintErr($"[AgentSimulationThread] Таймаут остановки симуляции ({timeoutMs} мс). Поток всё ещё активен!");
}
```
```csharp
// HierarchicalPathfinder.cs:126-135
public void Reset()
{
    lock (_buildLock)
    {
        _state = null;
        _cost = null;
        _edgeStart = null;
        _edgeCount = null;
        _portals = null;
```

**Дефект.**
`Stop()` при таймауте возвращает `false` и **ничего не делает** дальше; `Start()` не смотрит на возвращённое значение и продолжает. Так как `_running` — общий `volatile bool`, повторный `_running = true` (строка 273) **реанимирует старую петлю**: теперь две петли одновременно вызывают `Phase2/3a/3b` по одному и тому же `_pool`/`_ctx` (поля читаются заново на каждой фазе), т.е. каждая фаза выполняется дважды параллельно: двойная обработка коммитов (двойной `TakeItems`/`SpawnItems`, двойной `TryUnregisterJob`), двойной `ReleaseJobWorker`, `IdleWorkers` и `JobSpatialPyramid`-счётчики получают по два апдейта на одно событие. Дополнительно `HierarchicalPathfinder.Initialize()` → `Reset()` обнуляет `_edgeStart/_portals/_cost` **под живыми Phase2-воркерами**, которые прошли проверку `if (_state == null || _dirty)` до `Reset` и читают поля без лока:
```csharp
// HierarchicalPathfinder.cs:602-604 (PickBestPortal)
int idx = ra * 4 + (byte)dir;
int start = _edgeStart[idx];   // ← после Reset() это NullReferenceException
int cnt = _edgeCount[idx];
```
Исключение в воркере ловит планировщик (`DynamicWorkScheduler.cs:492` — `catch { localErrors++; }`) → батч агентов молча пропускается (см. B-04).

Вероятность таймаута не гипотетическая: в комментариях самого проекта зафиксированы итерации по 7.3 с («видно, где сидят 7.3с: Rebuild или Commit», `AgentSimulationThread.cs:1135-1136`) при 100k агентов, то есть > 5 с на итерацию — реальный режим.

**Сценарий отказа.**
Меню «новая игра/сменить регион» → `Main.OnRegionSelected` → `_agentThread.Stop(3000)` (или `Start`→`Stop(5000)`) не укладывается в таймаут на большой карте → `Main.cs:268` `ResetColonyState()` (в т.ч. `HierarchicalPathfinder.Reset()`) и затем `Start(...)`: старая петля ещё жива, `_running` снова `true` → два цикла на одном `_pool`; параллельно воркеры старого цикла падают в `PickBestPortal` с NRE.

**Минимальный фикс.**
Сделать остановку обязательной: `Stop()` возвращает `bool`, и `Start()` при `false` (а) не продолжать (`throw`/`return`), либо (б) после `Wait` делать `Thread.Join` без таймаута / `Interlocked`-«поколение» (`_runGeneration`), которое старая петля проверяет вместе с `_running`:
```csharp
private volatile int _generation;
// Stop(): _running=false; если !Wait(ms) { ... } ; Start(): int gen = ++_generation; _running = true;
// SimulationLoop: while (_running && Volatile.Read(ref _generation) == gen)
```
Плюс `Reset` пути и `Initialize` вызывать только после подтверждённой остановки.

**Как проверить фикс.**
Тест: `Start(1000, ...)`; затем искусственно замедлить фазу (Sleep в теле `UpdateSingleAgent` через тестовый хук) и вызвать `Start(500, ...)`: после вызова `_generation`-инвариант — старая петля должна завершиться в пределах одного шага, `AgentDataPool` не должен наблюдаться двумя потоками (счётчик входа/выхода из `Phase2_ParallelUpdate` == 1).

---

## B-04 — Планировщик молча «съедает» необработанные батчи (исключение / нулевой прогресс) и считает их выполненными; ошибки Phase3b не выводятся — P2 — confidence: high

**Файлы/строки:**
- `src/simulation/scheduling/DynamicWorkScheduler.cs:486-497` — range-батч: исключение = «сделано»
- `src/simulation/scheduling/DynamicWorkScheduler.cs:518-524` — то же на курсорном батче
- `src/simulation/scheduling/DynamicWorkScheduler.cs:738-746` — `done <= 0` = «сделано целиком»
- `src/simulation/AgentSimulationThread.cs:1158-1182` — Phase3b не читает `LastPhaseErrorCount`
- `src/simulation/AgentSimulationThread.cs:742-751` — Phase2-обёртка наоборот считает ложные ошибки (см. ниже)

**Код:**
```csharp
// DynamicWorkScheduler.cs:515-524 (ForEachRangeCore, cursor-батч)
long t0 = Stopwatch.GetTimestamp();
try { body.Invoke(slot, (int)start, end); }
catch { localErrors++; }                                   // ← исключение проглочено
long t1 = Stopwatch.GetTimestamp();
localCpuTicks += t1 - t0;
_monitor.RecordWorkerBatch(slot, (t1 - t0) * 1000.0 / Stopwatch.Frequency, end - (int)start);
localProcessed += end - (int)start;                        // ← батч засчитан обработанным
continue;
```
```csharp
// DynamicWorkScheduler.cs:738-746 (ForEachSplittable)
if (done <= 0)
{
    localErrors++;
    localProcessed += item.Count;     // ← элементы НЕ выполнены, но засчитаны
    continue;
}
```

**Дефект.**
Для `ForEachRange` (а это весь Phase3b-commit: `AgentSimulationThread.cs:1165-1180` → `handler.Commit(...)`) исключение в теле означает, что **до 512 агентов** пропустили коммит (не отнесли предмет, не сняли резерв, не завершили работу), при этом счётчик `processed`/метрики утверждают, что диапазон обработан. Повтора нет by design (комментарий `DynamicWorkScheduler.cs:451-453`). Такие ошибки никто не печатает: Phase3a и Phase2 читают `LastPhaseErrorCount` после **своих** фаз, а Phase3b — нет (`AgentSimulationThread.cs:1158-1183`). Итог: разовые «пропажи» шагов коммита (визуально — агент не отдал предмет/остался на месте) без единой строчки в логе.
Отдельно: секвенциальная ветка `ForEachSplittable` при `done <= 0` (`DynamicWorkScheduler.cs:633-640`) просто `break` — остаток диапазона (при `count < 64` или `dop==1`, т.е. на малой популяции это **вся** колония) не обрабатывается в этом суб-степе.

**Сценарий отказа.**
`count < ParallelThreshold` или малая карта → Phase3b идёт `ForEachRange`; в `ConstructionJobHandler.Commit` (или `StockpileHaulingJobHandler.Commit`) возникает исключение (напр. из-за гонки в пункте B-01 c чужим jobId или `ctx` несоответствия) → батч 32-512 агентов пропускает коммит; в консоли — ничего, в метриках — «всё обработано».

**Минимальный фикс.**
(1) Логировать/агрегировать ошибки всех фаз: читать `LastPhaseErrorCount` и после Phase3b (и печатать из sim-потока, как уже делается для Phase2/3a). (2) Для range-тел, где важна потеря, добавить per-element fallback: при исключении повторить диапазон поэлементно с `try/catch` на каждый i (как в `ForEach`), а не засчитывать диапазон целиком.

**Как проверить фикс.**
Тест: тело `ForEachRange`, бросающее на i==середине диапазона; ожидание — остальные i всё равно выполнены (счётчик вызовов == count), ошибка видна в логе/метрике.

---

## B-05 — `HierarchicalPathfinder`: чтение мутируемых полей вне опубликованного `_state` → NRE/мусорные якоря; в `IsTileBlocked` сверяется только `MapWidth` — P2 — confidence: medium

**Файлы/строки:**
- `src/simulation/pathfinding/HierarchicalPathfinder.cs:592-616` — `PickBestPortal` читает `_edgeStart/_edgeCount/_portals/_mapWidth`
- `src/simulation/pathfinding/HierarchicalPathfinder.cs:693-712` — `ComputeRegionPath` читает `_regionDimX/_regionDimY`, пишет в `g[sr]` без проверки `sr < cap`
- `src/simulation/pathfinding/HierarchicalPathfinder.cs:421-436` — `EnsureOverlay` перезаписывает эти поля
- `src/simulation/pathfinding/HierarchicalPathfinder.cs:126-142` — `Reset()` обнуляет их
- `src/simulation/agents/AgentMovementService.cs:405-419` — `Cost` и `MapWidth` — два независимых чтения; проверяется только ширина

**Код:**
```csharp
// HierarchicalPathfinder.cs:602-616
int idx = ra * 4 + (byte)dir;
int start = _edgeStart[idx];
int cnt = _edgeCount[idx];
if (start < 0 || cnt <= 0)
    return false;
...
    int portalPacked = _portals[start + i];
    int px = portalPacked % _mapWidth;
```
```csharp
// AgentMovementService.cs:405-419
byte[] cost = HierarchicalPathfinder.Instance.Cost;                      // State?.Cost ?? _cost
if (cost != null && HierarchicalPathfinder.Instance.MapWidth == ctx.MapWidth)  // ← второй State-read
{
    int w = ctx.MapWidth;
    int idx = ty * w + tx;
    if (cost[idx] == HierarchicalPathfinder.CostBlocked)
        return true;
    ...
    if (subY > 64 - AgentRadius && ty < ctx.MapHeight - 1 && cost[idx + w] == HierarchicalPathfinder.CostBlocked) return true;
```

**Дефект.**
Заявленный контракт — «потокобезопасен, состояние публикуется атомарно через immutable `NavGridState`» (`NavGridState.cs:6-9`), но горячий путь читает **не** снимок, а живые поля: `PickBestPortal` использует `_edgeStart/_edgeCount/_portals/_mapWidth`, `ComputeRegionPath` — `_regionDimX/_regionDimY`, `Cost`/`MapWidth` — два отдельных `Volatile.Read(ref _state)` (`HierarchicalPathfinder.cs:60-64`). `Invalidate()` срабатывает от завершения стройки (`StructureManager.cs:115,154,209`) и от Phase3b-воркеров, `Reset()` — от `Main.ResetColonyState`/меню; читатели же берут `_buildLock` только внутри `EnsureOverlay` (то есть лишь при `_dirty`/`_state == null`). Значит воркер, уже прошедший проверку и находящийся в `PickBestPortal`, читает поля **без лока** в момент их замены/обнуления:
- `Reset()` (обнуление) → `NullReferenceException` на `_edgeStart[idx]` (ловится планировщиком → тихий пропуск батча, см. B-03/B-04);
- `EnsureOverlay` (перезапись) → «сшитое» чтение (`start`/`cnt` из старого массива, `_portals` — из нового, ещё не заполненного нулями) → мусорный якорь `packed == 0` → путь через клетку (0,0); `_mapWidth` из нового состояния при `portalPacked` из старого — тоже мусорный якорь;
- `IsTileBlocked`: `cost` из старого `NavGridState`, а `w = ctx.MapWidth` — при смене карты той же ширины, но другой высоты `idx = ty*w+tx` выходит за длину `cost` → `IndexOutOfRangeException` в Phase2-воркере (ловится → пропуск батча). Проверяется только `MapWidth`, `MapHeight` не сверяется.

**Сценарий отказа.**
Игрок достраивает стену (Phase3b → `StructureManager.AddStructure` → `Invalidate`) — в этот же момент другой воркер Phase2 выполняет запрос пути: `_dirty` уже `true`, но `EnsureOverlay` ещё не построил новые массивы → чтение частично обновлённого набора полей → агент получает путь через клетку (0,0) или «нет пути». При отмене стройки/новой карте с одновременным `Reset()` — NRE в воркере.

**Минимальный фикс.**
Читать всё только из снимка: в начале `TryFindPathScratch/TryFindPath` один раз взять `var st = Volatile.Read(ref _state)` и передать `st` (Cost/MapWidth/MapHeight/EdgeStart/EdgeCount/Portals/RegionDim*) параметрами в `PickBestPortal`/`ComputeRegionPath`/`ComputeLocalSegment`. `IsTileBlocked` — брать один снимок: `var pf = HierarchicalPathfinder.Instance; var st = pf.State;` и проверять `st.MapWidth == ctx.MapWidth && st.MapHeight == ctx.MapHeight`.

**Как проверить фикс.**
Тест с двумя потоками: поток-читатель непрерывно вызывает `TryFindPathScratch`/`IsTileBlocked`, поток-писатель вызывает `Invalidate()`+`Initialize(newCtx)` каждые 1 мс в течение 5 с — при фиксе не должно быть ни одного исключения и ни одного якоря вне `[0, MapWidth×MapHeight)`.

---

## B-06 — Таймер аудита работ тикает по суб-степам: «раз в 30 с реального времени» превращается в ~30 с / steps — P2 — confidence: high

**Файл/строки:** `src/simulation/AgentSimulationThread.cs:518-528` (внутри `for (int step = 0; step < steps; step++)`, начало цикла — строка 375)

**Код:**
```csharp
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
```
`realDelta` — это время **одной итерации петли** (строка 316), а инкремент стоит внутри цикла по `steps` суб-степам, поэтому за одну итерацию таймер растёт на `steps × realDelta`, а не на `realDelta`.

**Дефект.**
Заявленный интервал «30 с wall-clock» (`JobValidator.cs:19-20`) в реальности делится на число суб-степов: на 1x `steps` обычно 1 (`Thread.Sleep(6)`), но на высокой скорости/при отставании `steps` доходит до 10 (`AgentSimulationThread.cs:328-330`) — аудит запускается до ~10× чаще, чем задумано. `JobValidator.Tick` — не бесплатный: `SnapshotActiveJobs` (под `_registerLock`, аллокация `_jobSlice`), до 2048 `CanAgentExecute` с локами менеджеров (`GroundItemManager`/`StockpileManager`/`ConstructionManager`) и `AuditMarksSlice` (`JobValidator.cs:67-100,120-170`). То есть на 100x воркеры диспетчера/коммита получают дополнительный lock-контеншн по чанкам, которого не ожидали по комментарию.

**Сценарий отказа.**
100x, `steps == 2…10` в каждой итерации, карта с тысячами работ: аудит вместо «раз в 30 с» идёт каждые 3-15 с реального времени, каждый раз беря `_registerLock` и локи менеджеров; на 100k агентов это добавляет периодические просадки Phase3b (в метриках — всплески `Simulation.JobAudit`).

**Минимальный фикс.**
Вынести инкремент из цикла суб-степов:
```csharp
// один раз на итерацию петли, вне for (step...)
if (steps > 0) {
    _jobAuditRealTimer += realDelta;
    if (_jobAuditRealTimer >= JobValidator.AuditIntervalRealSec) { ... }
}
```

**Как проверить фикс.**
Лог-счётчик: число вызовов `JobValidator.Tick` за 60 с реального времени на 100x должен быть ≈ 2, а не 20.

---

## B-07 — Кольцо снапшотов `PositionQueue`: буфер переиспользуется без передачи владения; «гарантия отсутствия tearing» не держится при вытеснении потребителя — P2 — confidence: medium

**Файлы/строки:**
- `src/simulation/AgentSimulationThread.cs:1205-1223` — производитель (кольцо 4 + cap очереди 2)
- `src/ui/renderers/AgentRenderer.cs:393-421` — потребитель: держит ссылку на последний снапшот и копирует из него

**Код:**
```csharp
// AgentSimulationThread.cs:1209-1222
int slot = _posSnapCursor % 4;
_posSnapCursor++;
var snapshot = _positionSnapRing[slot];
if (snapshot == null || snapshot.Length != _pool.Capacity)
{
    snapshot = new Vector2[_pool.Capacity];
    _positionSnapRing[slot] = snapshot;
}

_pool.CopyPositionsTo(snapshot);

PositionQueue.Enqueue(snapshot);
while (PositionQueue.Count > 2)
    PositionQueue.TryDequeue(out _);
```
```csharp
// AgentRenderer.cs:395-406 (главный поток)
while (_simulationThread.PositionQueue.TryDequeue(out var snapshot))
{
    latestSnapshot = snapshot;
}
if (latestSnapshot != null)
{
    if (!_hasInitialSnapshot)
    {
        Array.Copy(latestSnapshot, _targetPositions, _agentCount);
```

**Дефект.**
Буфер выдаётся «по ссылке» и не помечается занятым: производитель не знает, читает ли его кто-то. Гарантия «cap=2, ротация=4 ⇒ перезаписи читаемого буфера нет» (комментарий на строках 1207-1208) опирается на то, что потребитель укладывается в < 2 публикации. Но потребитель — `_Process` главного потока: он может быть вытеснен ОС (или задержан другим `_Process`/GC) на время, за которое sim-поток сделает 3-4 публикации (на 60-80 снапшотах/с это ~50 мс). Тогда тот же массив будет перезаписан **во время** `Array.Copy(latestSnapshot, ...)`/`FillGpuInstanceBuffer` → «рваная» позиция у части агентов (прыжок визуально, не краш). Второй источник неконсистентности: `_pool.CopyPositionsTo(snapshot)` читает `PositionX/Y`, которые параллельно пишут Phase2-воркеры (Phase3a/3b идут после, но снапшот пушится в конце итерации, а следующая итерация уже может стартовать Phase2, пока главный поток ещё читает `latestSnapshot`) — «одно мгновение» не гарантируется.

**Сценарий отказа.**
Окно без фокуса/тяжёлый кадр рендера (или отладчик/GC-пауза) → главный поток просыпается с `latestSnapshot`, который уже перезаписан 3-й публикацией → группа агентов на один кадр прыгает в случайную сторону.

**Минимальный фикс.**
Передавать владение явно: кольцо из N=4 буферов + `ConcurrentQueue<(Vector2[] buf, int idx)>` и возврат буфера потребителем (`ReturnBuffer(idx)`) либо `Interlocked`-флаг «busy» на слоте; публиковать `ArrayPool`-буфер с явным возвратом. Либо увеличить ротацию до 8 и добавить счётчик «потребитель отстал» с пропуском снимка.

**Как проверить фикс.**
Тест: искусственная задержка потребителя (Thread.Sleep(100) внутри обработки) при 60 публикациях/с — при фиксе данные внутри одного снапшота обязаны быть консистентны (проверять счётчик «busy» до/после копирования).

---

## B-08 — `busyWorkers = count - totalIdle`: при `TotalIdleCount > Capacity` Phase3b-коммит отключается целиком — P2 — confidence: low (нужен stale-idle после рестарта меньшим пулом)

**Файлы/строки:**
- `src/simulation/AgentSimulationThread.cs:1148-1154`
- `src/simulation/agents/FlatIdleWorkerPool.cs:47-62,137-144` — idempotent add, `Clear()` есть, но `Start()` его не вызывает
- `src/simulation/jobs/JobDispatcher.cs:76` — фильтр «вне пула» только на диспатче

**Код:**
```csharp
// AgentSimulationThread.cs:1150-1154
int totalJobs = JobDispatcher.Instance.JobIndex.TotalCount;
int totalIdle = JobDispatcher.Instance.IdleWorkers.TotalIdleCount;
int busyWorkers = count - totalIdle;

if (totalJobs > 0 && busyWorkers > 0)
{
    ... CommitSingleAgent / ForEachRange(handler.Commit) ...
}
```

**Дефект.**
`busyWorkers` считается как разность **независимых** счётчиков без клампа. Если по любой причине `TotalIdleCount > _pool.Capacity` (например, `Start()` вызван с меньшим `agentCount`, а stale-записи в `FlatIdleWorkerPool` остались: `Start` не вызывает `IdleWorkers.Clear()`, `AgentSimulationThread.cs:203-240`), то `busyWorkers < 0` и коммит **не выполняется вообще ни разу**, пока счётчик не выправится: агенты ходят к цели и никогда не отдают/не завершают работу, работы копятся. То же переполнение может дать любой будущий баг аддитивности (например, двойной `AddIdleWorker` из пути, где idempotency-guard снят). Сейчас это смягчено тем, что `Main.ResetColonyState` вызывает `IdleWorkers.Clear()` (`Main.cs:502`) до старта, и тем, что диспатч отсекает индексы ≥ `pool.Capacity` (`JobDispatcher.cs:76`) — но защита от арифметики отрицательного `busyWorkers` отсутствует, а `Start` сам по себе не изолирует пул от предыдущего запуска.

**Сценарий отказа.**
Новая игра с меньшей популяцией (10000 → 500) в сценарии, где `ResetColonyState` не вызван (или вызван до `Stop`-таймаута, см. B-03): в пуле остаются idle-записи с индексами 500…9999 → `totalIdle > count` → Phase3b-коммит молча пропускает все тики; в логе `[JobValidator]` будут копиться «мёртвые» работы, агенты не завершают ничего.

**Минимальный фикс.**
Клампить: `int busyWorkers = Math.Max(0, count - Math.Min(totalIdle, count));` и/или вызывать `JobDispatcher.Instance.IdleWorkers.Clear()` (и `JobIndex.Clear()`) в `Start()` до регистрации агентов, а также инвариант-проверку `TotalIdleCount <= _pool.Capacity` в отладочной сборке.

**Как проверить фикс.**
Тест: `Start(100)`, затем `Start(50)` (без Clear-пула вручную) — коммит обязан продолжать работать: счётчик завершённых работ за 60 с > 0.

---

## B-09 — `ParallelRng`: детерминизм невозможен by construction (seed от `TickCount` + `ManagedThreadId`, `[ThreadStatic]` состояние живёт между фазами и прогонами) — P2 — confidence: high

**Файл/строки:** `src/simulation/ParallelRng.cs:15-26,28-45,51-63`

**Код:**
```csharp
[ThreadStatic]
private static ulong _state;                      // :15-16
private static ulong Seed()
{
    ulong s = (ulong)(Environment.TickCount ^ (Thread.CurrentThread.ManagedThreadId * 7919));  // :20
    if (s == 0) s = 0x9E3779B97F4A7C15UL;
    s ^= s >> 29; s *= 0xBF58476D1CE4E5B9UL;
    s ^= s >> 32;
    return s;
}
```
```csharp
// :45 — используется в горячих ветках решений, в т.ч. параллельных:
public static double NextDouble() => (Next() >> 11) * (1.0 / 9007199254740992.0);
```

**Дефект.**
1. Поток значения привязан к `(TickCount, ManagedThreadId)`, т.е. к моменту первого обращения потока и к тому, **какой** ThreadPool-поток получил батч. Один и тот же батч агентов в двух прогонах обслуживается разными потоками → последовательность `ParallelRng` другая → решения `WanderJobSystem.TryAssignJob` (`ParallelRng.NextDouble()`), `pool.JobSearchTimer[i] = 6f + NextDouble()*6f` (`AgentSimulationThread.cs:1113`), `NeedsJobSystem.ThreadRandom` (`NeedsJobSystem.cs:19-20`) и выбор жертвы для steal (`DynamicWorkScheduler.cs:529,765`) не воспроизводимы. Даже при фиксированном seed карты и `new Random(seed)` для спавна (`AgentSimulationThread.cs:202`) детерминизма нет — golden/save-replay тесты невозможны.
2. `[ThreadStatic]` состояние не сбрасывается между фазами/запусками новой игры: последовательности продолжаются, поэтому «тот же вход» после перезапуска симуляции даёт другой выход (в отличие от `ctx.Random`).
3. Сам `Next(min, max)` реализован корректно: rejection-sampling с `limit = (2^64-1)/range*range` (`ParallelRng.cs:56-62`) — смещения нет, деления на 0 нет (`range != 0` обеспечен проверкой `minInclusive >= maxExclusive`); это проверено и дефектом не является.

**Сценарий отказа.**
Пользователь дважды загружает один и тот же сейв — поведение колонии (кто какую работу взял, кто пошёл есть) заметно расходится; баг-репорт «после загрузки агенты повели себя иначе» невозможно воспроизвести.

**Минимальный фикс.**
Детерминированный per-stream seed: `_state = Hash(globalSeed, phaseId, batchStart, workerIndex)` — тогда поток значений зависит только от того, **что** обрабатывается, а не от того, какой поток это делает; при этом сохраняется lock-free `[ThreadStatic]`. Плюс явный `Reset(seed)` в `Start()`.

**Как проверить фикс.**
Golden-тест: два прогона с одинаковым seed (в т.ч. с `GlobalForcedDop = 1` и `= 4`) должны давать одинаковую траекторию ≥ N тиков (хеш позиций/назначений).

---

## B-10 — Offline catch-up: NaN в сохранённом `CatchUpElapsedGameSeconds` обнуляет учёт лимита (повторный полный догон 7 суток) — P2 — confidence: medium

**Файлы/строки:**
- `src/simulation/offline/OfflineWorldTicker.cs:54-59,109-122`
- `src/simulation/offline/OfflineCatchUpPolicy.cs:16-28`

**Код:**
```csharp
// OfflineWorldTicker.cs:116-122
private static double NormalizeApplied(double value)
{
    double maximum = OfflineCatchUpPolicy.DefaultMaximumElapsedGameSeconds;
    if (!double.IsFinite(value) || value <= 0d)
        return value > 0d ? maximum : 0d;      // ← NaN: NaN > 0 == false → 0
    return Math.Min(value, maximum);
}
```
```csharp
// OfflineWorldTicker.cs:56-58
double previousApplied = NormalizeApplied(from.CatchUpElapsedGameSeconds);
double elapsed = GetAvailableElapsed(elapsedGameSeconds, previousApplied);  // remaining = max - 0
```
```csharp
// OfflineCatchUpPolicy.cs:23-27
if (double.IsNaN(elapsedGameSeconds) || elapsedGameSeconds <= 0d)
    return 0d;
```

**Дефект.**
Все «внешние» double (запрошенный интервал) санитизируются, но **уже применённый** счётчик из снимка/сейва — нет: `NaN` (порча сейва, деление 0/0 при расчёте офлайн-интервала в вышестоящем коде) обрабатывается как «ничего не применено» (`previousApplied = 0`), после чего `GetAvailableElapsed` выдаёт полный остаток лимита. Каждая следующая загрузка такого сейва снова применяет полный 7-суточный догон (культуры скачут в зрелость, потребности/голод — на максимум). `+∞` обрабатывается верно (→ `maximum`, остаток 0 — идемпотентно), т.е. дырка именно в `NaN`/отрицательных.

**Сценарий отказа.**
Сейв с `CatchUpElapsedGameSeconds = NaN` (или формула офлайн-времени дала NaN при отсутствии времени выхода) → при каждой загрузке агенты получают «+7 суток» потребностей, культуры — мгновенно созревают; игрок видит «голодную смерть» после каждой загрузки.

**Минимальный фикс.**
```csharp
if (double.IsNaN(value)) return maximum;   // порча = «лимит исчерпан», а не «не применялось»
if (!double.IsFinite(value) || value <= 0d) return value > 0d ? maximum : 0d;
```
(плюс нормализовать сохраняемое значение при записи сейва).

**Как проверить фикс.**
Юнит-тест: `AdvanceAgent(s with CatchUpElapsedGameSeconds = NaN, 1e6)` → `previousApplied` обязан стать `maximum`, `elapsed == 0`, поля потребностей не изменены.

---

# Проверено — дефект не подтверждён (доказательство отсутствия)

Чтобы аудит не выглядел «только негативным», ниже перечислены места, где гипотеза дефекта проверялась по коду и **опровергнута**:

1. **`AgentDataPool` — реаллокации/двойного освобождения слотов нет.** Пул фиксированного `Capacity` (`agents/AgentDataPool.cs:11`), `WaypointX/Y` аллоцируются как `capacity * AgentPathConfig.MaxWaypoints` (`:154-155`, `MaxWaypoints = 96`, `:201`). Все писатели `WaypointCount` либо `0`, либо `(byte)cap` с `cap = Math.Min(count, MaxWaypoints)` (`AgentMovementService.cs:169-177`; grep по `WaypointCount[...] =` — 19 совпадений, все `0` или `cap`), поэтому переполнения среза `[i*96, i*96+96)` нет.
2. **`AgentSpatialGrid.RebuildParallel` — Treiber-push корректен при текущем вызове.** Параллельная ветка (`agents/AgentSpatialGrid.cs:103-133`) делает `Interlocked.Exchange` головы, а затем `nextInCell[i] = oldHead`; «окно» неконсистентности безопасно, т.к. rebuild выполняется **эксклюзивно** в Phase3b на sim-потоке с join внутри `Parallel.For` (`AgentSimulationThread.cs:1143-1145`), читателей spatial-grid в этот момент нет. `_activeCells` не переполняется: клетка добавляется ровно один раз при `oldHead == -1`.
3. **`FlatIdleWorkerPool` — дублей/потерь слотов нет.** `AddIdleWorker` идемпотентен (`_agentToIdleIndex[agentIndex] != -1 → return`, `:54-55`), `RemoveIdleWorker` — корректный swap-and-pop с обработкой `idx == lastIdx` (`:78-87`). Это закрывает гипотезу «OnStart сам зовёт `AddIdleWorker`, а диспетчер затем зовёт ещё раз» (`JobDispatcher.cs:116` + `StockpileHaulingJobHandler.cs:47`): повторный Add безвреден.
4. **`WorkStealingQueue` — Chase-Lev реализован аккуратно.** При пустой очереди `bottom` выравнивается к `top` (`:110-113`), кража половины делает копию до CAS и откатывает буфер при проигрыше (`:157-167`), `MemoryBarrier` стоит там, где нужно (`:90,145`). Бесконечного steal-цикла нет: выход из `while(true)` возможен только когда своя очередь пуста, overflow пуст и курсор исчерпан (`DynamicWorkScheduler.cs:802-814`), а владелец очереди из неё не выходит, пока там есть элементы.
5. **`LoadMonitor` не падает при `dop > MaxSupportedDop`.** Все обращения к `_slots[slot]` защищены: `(uint)slot < (uint)MaxSupportedDop ? _slots[slot] : _local.Value` (`:85, 173, 224`), агрегация клампится `Math.Clamp(degreeOfParallelism, 1, MaxSupportedDop)` (`:117`). На 192-поточных машинах поведение — деградация на ThreadLocal, не исключение.
6. **Численные: делений на ноль в движении нет.** `SimdMovementBatch` защищает `vDist` через `Vector.Max(vDsq, vEps)` (`simd/SimdMovementBatch.cs:114`); `AgentMovementService.MoveTowards` выполняет деление только после проверок `distSq <= reachDistance²` (`:210-239`, `:266-270`); вес предмета в `GroundItemManager` защищён `w > 0f ? w : 1f` (`items/GroundItemManager.cs:108-109`), поэтому `(int)(maxWeightCapacity / weight)` (`:486`) не может дать `(int)Infinity`.
7. **`CropGrowthManager` — гонки на данных культур нет.** `CropData` — `struct` (`farming/CropData.cs:3`), поэтому снимок под локом (`:79-86`) содержит копии; мутация идёт по копиям, а write-back под локом перепроверяет `_crops.TryGetValue` и пишет только «новее» (`:117-135`). Параллельный `HarvestCrop` (`:148-160`) удаляет под тем же локом.
8. **Offline-композиция догона сама по себе аддитивна.** `Advance(Advance(s,t1),t2) == Advance(s,t1+t2)` при корректных (`finite`) значениях `CatchUpElapsedGameSeconds`: `previousApplied` клампится к `maximum`, `remaining = max(0, max - applied)` (`OfflineWorldTicker.cs:109-122`); `OfflineCropGrowth.StageDurationGameSeconds = 60` совпадает с `CropGrowthManager.StageDuration = 60.0f` (`farming/CropGrowthManager.cs:13`) — рассинхрона скорости роста нет.

# Сводная таблица

| ID | Кратко | Severity | Confidence | Файл:строка |
|----|--------|----------|-----------|-------------|
| B-01 | Free-list ABA jobId: двойное назначение работы, удаление чужой работы, порча счётчиков | P1 | high/medium | `src/simulation/jobs/JobSpatialPyramid.cs:260,445,523,758` |
| B-02 | `_macroJobCounts`: неатомарный RMW под локом + Interlocked вне лока → гаснет маска L2, работы не забираются | P1 | high/medium | `src/simulation/jobs/JobSpatialPyramid.cs:437,539,771` |
| B-03 | `Start()` после таймаута `Stop()`: две петли на одном пуле + `Reset()` пути под живым воркером | P1 | medium | `src/simulation/AgentSimulationThread.cs:175,273,1231` |
| B-04 | Планировщик молча пропускает батч (исключение/нулевой прогресс), считая его сделанным; ошибки Phase3b не печатаются | P2 | high | `src/simulation/scheduling/DynamicWorkScheduler.cs:492,519,741` |
| B-05 | `HierarchicalPathfinder`: чтение мутируемых полей вне `_state`; в `IsTileBlocked` сверяется только `MapWidth` | P2 | medium | `src/simulation/pathfinding/HierarchicalPathfinder.cs:603`; `src/simulation/agents/AgentMovementService.cs:405` |
| B-06 | Таймер аудита работ тикает внутри цикла суб-степов → аудит до ×10 чаще заявленного | P2 | high | `src/simulation/AgentSimulationThread.cs:520` |
| B-07 | Снапшоты позиций: кольцо 4 без передачи владения → возможен tearing у потребителя | P2 | medium | `src/simulation/AgentSimulationThread.cs:1209`; `src/ui/renderers/AgentRenderer.cs:395` |
| B-08 | `busyWorkers = count - totalIdle` без клампа: при idle > capacity коммит отключается целиком | P2 | low | `src/simulation/AgentSimulationThread.cs:1152` |
| B-09 | `ParallelRng`: seed от `TickCount`/`ThreadId` + `[ThreadStatic]` → детерминизма нет | P2 | high | `src/simulation/ParallelRng.cs:16,20` |
| B-10 | Offline catch-up: `NaN` в applied-счётчике = «лимит не исчерпан» → повторный полный догон | P2 | medium | `src/simulation/offline/OfflineWorldTicker.cs:119` |

**Рекомендованный порядок исправлений:** B-01 → B-02 → B-03 (P1: порча состояния/потеря работ/двойная симуляция), затем B-04 и B-06 (тихие потери + лишняя нагрузка), затем B-05/B-07/B-09/B-10/B-08.