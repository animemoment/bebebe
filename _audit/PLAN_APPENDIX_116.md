---

## 116. Раунд 2: многопоточные P1 и починка инструмента (R9)

Дата: 2026-10-18. Продолжение §115.

### 116.1. B-02 — три дисциплины синхронизации на одном счётчике (`JobSpatialPyramid`)

`_macroJobCounts` мутировался тремя способами:
- `RegisterJob` (:310) и второй регистратор (:384) — обычный `++` **под** `_registerLock`;
- `ReleaseWorkerClaim` (:539) — `Interlocked.Increment` **вне** лока;
- `TryClaimJobForAgent` (:771) — `Interlocked.Decrement` **вне** лока.

Лок не сериализует против `Interlocked` → **lost update**. Следствие: счётчик уезжал в 0
при живых задачах, строка :438 гасила бит `_macroActiveMask`, и L2-фильтр (:644)
пропускал целый макро-сектор 4×4 чанка — агенты «не видели» работу и простаивали.

**Исправление:** все мутации счётчика переведены на `Interlocked`; декремент — CAS-цикл с
клампом на 0 (семантика `Math.Max(0, x-1)` без гонки); бит маски гасится только после
повторной проверки под локом (как в :771-778).

### 116.2. B-01 — stale-id в `ReleaseWorkerClaim`

`RemoveJob` возвращает `jobId` в LIFO free-list (:445-446), слот затем переиспользуется.
`ReleaseWorkerClaim` делал слепой `Interlocked.Decrement` с проверкой только `_active[jobId]`,
но у переиспользованного слота `_active == true` → «запоздавший» release уводил
`_assignedWorkers` в минус. Далее CAS-захват (`while cur < _maxWorkers`) трактовал минус как
свободный слот и выдавал **второй claim** на задачу с `MaxWorkers = 1`.

**Исправление:** CAS-цикл с клампом (`cur <= 0 → return`). Легитимный release (cur ≥ 1) не изменён.
**Остаточный риск (честно):** если переиспользованный слот уже имеет живой claim, запоздавший
release может украсть его. Полное решение — поколение на слот в дескрипторе работы агента
(в `AgentDataPool` хранится только `int JobId`) — вынесено в R9.

### 116.3. B-03 — две параллельные петли `SimulationLoop`

`Start()` вызывал `Stop(5000)` и **игнорировал** результат (:178), после чего выставлял
`_running = true` (:273) и `Task.Run(SimulationLoop)` (:274). При таймауте старая петля
воскрешалась и тикала параллельно с новой на тех же `_pool`/`_ctx`, а ссылка на неё терялась
при перезаписи `_simulationTask`. Дополнительно `Main.cs` создаёт **новый** экземпляр
(`Main.cs:367`), поэтому инстансная защита не покрыла бы и этот путь.

**Исправление:** статический (процесс-глобальный) счётчик поколений `_loopGeneration`;
`Start()` инкрементирует его **до** `Task.Run`, `SimulationLoop(int generation)` крутится под
`while (_running && generation == _loopGeneration)`; результат `Stop` больше не игнорируется
(логируется). Инвариант «одна живая петля симуляции на процесс» совпадает с требованием общих
синглтонов (`JobDispatcher.Instance.IdleWorkers`, `JobRegistry`, `HierarchicalPathfinder.Instance`).

**Остаточное окно (честно):** старая петля может дописать текущую итерацию, пока новая уже
стартовала. Полное закрытие — барьер (await предыдущей задачи) перед сбросом общих статиков —
вынесено в R9.

### 116.4. Инструмент `DependencyGraphVisualizer`

`--deps` падал на любом файле: `app.py:1988` склеивал `fn['callers']` через `', '.join(...)`,
а элементы — **словари** из chronology-метрик (`app.py:1114-1119`, подмена в `functions_detail`
на `:1716`). Добавлен `fmt_caller()` (строка → как есть, словарь → `file::method()`).
Проверены text-, json-ветка и несуществующий файл. `DependencyGraphVisualizer.exe` пересобран
(`build_exe.py`, PyInstaller 6.22.3 из `D:\dependency_graph_visualizer\.venv`); `app.exe` не
обновлён — файл был занят другим процессом.

### 116.5. Верификация

| Проверка | Результат |
|---|---|
| `dotnet build Game.csproj` | **0 ошибок / 0 предупреждений** |
| `tests/run_test.gd` (14 групп) | ALL TESTS PASSED (Code 0) |
| `tests/run_adversarial_job_distribution_test.gd` (5 групп) | ALL 65K / 1M JOB DISPATCH AUDITS PASSED |
| `tests/run_adversarial_construction_test.gd` (14 групп) | ALL CONSTRUCTION & STRUCTURE AUDITS PASSED |
| `tests/run_adversarial_flow_test.gd` (6 групп) | ALL 6 GPU NAVIGATION & TRANSFORM TESTS PASSED |
| `tests/run_phase3_suite.gd` (**новый драйвер**) | passed, вкл. `TestSimulationThreadStartIdempotency` (двойной `Start`) |
| `tests/run_phase4_suite.gd`, `tests/run_phase5_suite.gd` | passed (Code 0) |
| `DependencyGraphVisualizer.exe --cli --deps <file>` | работает (exit 0) |