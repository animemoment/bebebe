# Реестр применённых фиксов — bebebe-main

Прогон: 2026-10-18, автономный режим. База: рабочее дерево с ~197 изменениями относительно `HEAD`
(собственные правки владельца) + правки этого прогона.
**Все правки — только `src/`** (+ `PLAN.md`, + `_audit/`). Сцены `scenes/` и `tests/` не тронуты.

---

## P0

| ID | Файл | Суть | Статус |
|---|---|---|---|
| **W-01** | `src/core/world/streaming/runtime/WorldChunkStreamer.cs` | В ветке `TryDequeue` воркера ключ не проходил `_pending.Remove` и проверку `_desired`/`_reserved`, `_inFlight` не заполнялся → «залипшие» reservations, незакрытые `CancellationTokenSource` и лишняя генерация при отмене региона (утечка на каждой смене региона) | **ИСПРАВЛЕНО** |

Применённая логика деки:
```csharp
else if (_workQueue.TryDequeue(out key, out _))
{
    if (!_pending.Remove(key))
        continue;
    if (!_desired.Contains(key) || !_reserved.Contains(key))
    {
        _reserved.Remove(key);
        CancelAndDisposeRequest_NoLock(key);
        FillPending_NoLock();
        if (_pending.Count > 0)
            PulseWorkers_NoLock();
        continue;
    }
    _inFlight.Add(key);
```

## P1 / устойчивость

| ID | Файл | Суть | Статус |
|---|---|---|---|
| **W-02** | `WorldChunkStreamer.cs` (`ProcessChunk`) | Бросал `InvalidDataException` устаревший снапшот чанка (несовпадение seed/версий) → краш воркера вместо мягкой деградации. Теперь снапшот берётся только при совпадении `Seed`/`GeneratorVersion`/`FeatureSchemaVersion`, иначе — честная генерация заново | **ИСПРАВЛЕНО** |
| **W-03** | `src/core/world/streaming/storage/WorldChunkStore.cs` | Комментарий в `SaveChunk` утверждал `Flush(true)`/fsync, которых в коде нет. Комментарий приведён к факту (durability не гарантируется, отсылка к мягкой деградации W-02) | **ИСПРАВЛЕНО** |

## UI / рендер

| ID | Файл | Суть | Статус |
|---|---|---|---|
| **C-01** | `src/ui/renderers/MapRenderer.cs` | `StoneJobManager.Instance.OnStoneMined += OnStoneMined` стоял внутри `ApplyMap()` (вызывается на каждую загрузку карты), тогда как парный `OnTreeChopped` — в `_Ready`. Итог: N обработчиков после N загрузок, повторная обработка каждого добытого камня и удержание освобождённого `MapRenderer` в списке вызовов синглтона. Подписка перенесена в `_Ready` (одна на жизненный цикл) | **ИСПРАВЛЕНО** |
| **C-02** | `src/ui/controllers/WorkZoneController.cs` | Кнопка-крестик искалась как `Escape`/`Close`/`Exit`/`Back`, но нода в `scenes/ui/JobDisplay.tscn` называется **`esc`** (как в `Farm.tscn`) → `_escapeButton == null`, окно Work-зоны нельзя было закрыть крестиком | **ИСПРАВЛЕНО** (добавлен `FindChild("esc")`) |
| **C-03** | `src/ui/renderers/AgentRenderer.cs` | Повторный `Initialize()` (смена региона через `Main.OnMapApplied`) пересоздавал буферы нулями, но не сбрасывал `_hasInitialSnapshot`/`_bodiesDirty`/`_shadowsDirty` → первый кадр нового мира интерполировал агентов **из угла (0,0)** вместо мгновенной расстановки по снапшоту | **ИСПРАВЛЕНО** |
| — | `FarmController.cs`, `WorkZoneController.cs` | `_ExitTree()` не обнулял статический `Instance` (в отличие от `HUDController`/`BuildMenuController`) → `X.Instance` отдавал disposed-ноду после выгрузки UI. Добавлено `if (ReferenceEquals(Instance, this)) Instance = null;` | **ИСПРАВЛЕНО** |
| — | 4 файла | Удалены проверенно пустые/неиспользуемые (нет ссылок в `.tscn`, 0 входящих рёбер): `src/ui/StressDebug.cs`, `src/ui/controllers/GardenController.cs`, `src/ui/streaming/InfiniteWorldLab.cs`, `src/ui/streaming/InfiniteWorldGame.cs` + их `.cs.uid` | **СДЕЛАНО** |

## Циклы зависимостей

Итог анализатора (Tarjan SCC): **4 цикла → 1**. Детали и код интерфейсов — `PLAN.md` §115,
проектирование — `_audit/CYCLE_BREAK_DESIGN.md`, замеры — `_audit/cycles_after/`.

| Цикл | Мера | Статус |
|---|---|---|
| #1 «`WorldClimateParams`» (SCC 81 файл) | Коллизия имён файлов, НЕ интерфейс: удалён мёртвый `[Obsolete]`-шим в `climate/WorldClimateParams.cs` + файл переименован в `WorldMacroClimateParams.cs` (по правилу PLAN.md «файл = класс») | **СДЕЛАНО**, оба файла ушли из SCC |
| #2 `WorldAgentRenderer` ↔ `WorldJobTicker` | `IWorldAgentSource` + вынос `WorldAgent`/`WorldAgentState` в отдельный файл | **СДЕЛАНО**, рантайм-проверено |
| #3 `FarmController` ↔ `WorkZoneController` | `IZoneWindow` / `IFarmJobDisplayHost` + листовой `ZoneWindowRegistry` | **СДЕЛАНО** |
| #4a `SelectTool` → `HUDController` → `PIM` → `SelectTool` | `ICellProbeOverlay` + `BindCellProbe`/`SetCellProbe` | **СДЕЛАНО**, рантайм-проверено (тултип «104% норма») |
| #4b `HUDController` ↔ `BuildMenuController` | `IDraftCoordinator` (8 членов) + `BindDraftCoordinator` | **СДЕЛАНО**, рантайм-проверено (`IsDrafting` false→true) |
| Остаток: SCC **79 файлов / 395 рёбер / 28 взаимных пар** | Реальная модульная запутанность `src/simulation/**` + `Game.Core` ← `Game.Simulation`. Одним интерфейсом не лечится | **ЗАДОКУМЕНТИРОВАНО** |

## Сознательно НЕ исправлено (с обоснованием)

Из `GLOBAL_AUDIT_REPORT.md` повторно проверено и **не подтверждено**:

- **B1** «нет проверки устаревших reservations» — НЕВЕРНО: проверка уже есть (`WorldChunkStreamer.cs:169-173`,
  `!_desired.Contains(key)`).
- **B2** «`RingWaterBoost` в `WorldChunkGenerator.cs`» — НЕВЕРНО: такого файла и такого идентификатора
  в проекте не существует.
- **B3** «пустой чанк перезапишет данные» — УЖЕ ИСПРАВЛЕНО в коде: пустой payload → атомарное
  удаление-надгробие (`WorldChunkStore.cs:66-70`).
- **B5** «`LocalIndex` переполняется при 35M клеток» — НЕВЕРНО: при `ChunkSize = 64`
  (`WorldCoordinates.cs:34`) `LocalIndex = local.Y*64 + local.X ∈ 0..4095`, и `MaxLocalIndex = 4095`
  покрывает чанк ровно.
- **B4** — частично: реальный порог переполнения `|chunkX| ≥ long.MaxValue/64 = 144 115 188 075 855 872`,
  а не 35 млн, как в отчёте.

**Вывод по `GLOBAL_AUDIT_REPORT.md`:** отчёт ссылается на несуществующие артефакты
(`WorldChunkGenerator.cs`, `RingWaterBoost`, `tests/adversarial/OceanRingDiscontinuityTests.cs`,
`tests/benchmarks/WorldGenBenchmark.cs`, фиктивные тест-классы и цифры GPU/бенчмарков). Разделы §1, §3, §5
опираются на непроверяемые данные — **использовать нельзя**.

## Находка в самом анализаторе (вне скоупа, НЕ правил)

`DependencyGraphVisualizer.exe --cli --project <bebebe-main> --deps <любой файл>` падает на **любом** файле:

```
[CLI Error]: sequence item 0: expected str instance, dict found
```

Причина — `D:\dependency_graph_visualizer\app.py:1988`:

```python
callers_str = f" | Вызывается в: {', '.join(fn['callers'])}" if fn['callers'] else " | Локальный вызов"
```

`fn['callers']` заполняется в `analyze()` (строка 1716) из `chronology`-метрик, где элементы —
**словари** (`{"file": ..., "method": ...}`), а не строки. `', '.join()` по словарям → `TypeError`.
Путь `--json` (именно `--json`, а не `--format json`) не задет — он сериализует через `json.dumps`.

Готовый патч (1 строка):

```python
callers_str = (" | Вызывается в: " + ", ".join(
    c if isinstance(c, str) else (c.get("file") or c.get("name") or str(c))
    for c in fn["callers"])) if fn["callers"] else " | Локальный вызов"
```

Обходной путь без правки тула: пользоваться `--all-deps` / `--orphans` / `--signals`
и REST-эндпоинтом `POST /api/analyze` (он возвращает `cycles`/`cycle_details` напрямую).

## Верификация

- `dotnet build Game.csproj` → **0 ошибок, 0 предупреждений** (после каждого шага правок).
- `godot --headless --script tests/run_test.gd` (TestWorldGen) → **exit 0**.
- `godot --headless --script tests/run_adversarial_construction_test.gd` → **exit 0**.
- Рантайм `godot_run_project scenes/main/Main.tscn`: мир сгенерирован, колония основана,
  `AgentRenderer` инициализирован (путь C-03 отработал), UI-окна живы; `stderr` — только
  предупреждения GDScript-аддона `godot_ai` (к правкам не относятся).
  - #4b: `BuildMenuController.Open("farming","Ферма")` → `HUDController.IsDrafting` **false → true**.
  - #4a: `MapMode = Humidity` + hover дефолтным `SelectTool` → тултип **«104% норма»**.

---

# Раунд 2 — многопоточные P1 + сам инструмент

## P1 многопоточность (верифицировано по файлу:строке, затем исправлено)

| ID | Файл | Дефект | Статус |
|---|---|---|---|
| **B-02** | `src/simulation/jobs/JobSpatialPyramid.cs` | `_macroJobCounts` мутировался **тремя** дисциплинами: обычный `++` под `_registerLock` (:310, :384), `Interlocked.Increment` вне лока (:539), `Interlocked.Decrement` вне лока (:771). Лок не сериализует против `Interlocked` → lost update → счётчик уезжал в 0 при живых задачах → :438 гасил бит `_macroActiveMask` → L2-фильтр (:644) пропускал макро-сектор 4×4 чанка → **агенты простаивали при наличии работы** | **ИСПРАВЛЕНО** |
| **B-01** | `JobSpatialPyramid.cs` | `RemoveJob` кладёт jobId в LIFO free-list (:445-446), слот переиспользуется. `ReleaseWorkerClaim` делал слепой `Interlocked.Decrement` (проверка только `_active`), а у переиспользованного слота `_active == true` → счётчик уходил в минус → CAS-захват (:760 `while cur < _maxWorkers`) выдавал **второй claim** на задачу с `MaxWorkers=1` | **ИСПРАВЛЕНО** (кламп CAS) |
| **B-03** | `src/simulation/AgentSimulationThread.cs` | `Start()` игнорировал результат `Stop(5000)` (:178), затем `_running = true` (:273) + `Task.Run` (:274) → при таймауте **две петли `SimulationLoop`** параллельно на одном `_pool`/`_ctx`, ссылка на старую терялась. Плюс `Main.cs:367` создаёт новый экземпляр — инстансная защита не покрыла бы и этот путь | **ИСПРАВЛЕНО** (статический токен поколения) |

**Остаточные риски (честно, вынесено в R9):**
- B-01: если переиспользованный слот уже имеет живой claim, запоздавший release может украсть его —
  нужно поколение на слот в дескрипторе работы агента (в `AgentDataPool` только `int JobId`).
- B-03: старая петля может дописать текущую итерацию, пока новая стартовала — нужен барьер
  (await предыдущей задачи) перед сбросом общих статиков.

## Инструмент `DependencyGraphVisualizer` (правка разрешена владельцем)

| Что | Детали |
|---|---|
| Баг | `--deps` падал на **любом** файле: `app.py:1988` делал `', '.join(fn['callers'])`, а элементы — словари из chronology-метрик (`app.py:1114-1119`, подмена в `functions_detail` на `:1716`) |
| Фикс | Добавлен `fmt_caller()`: строка → как есть, словарь → `caller_file::caller_method()` |
| Побочная находка | Ложные callers вида `addons/godot_ai/...::_connect_session_stopped()` — анализатор матчит по простому имени метода (`Bind`); это эвристика, не баг-краш |
| Пересборка | `DependencyGraphVisualizer.exe` пересобран через `build_exe.py` (PyInstaller 6.22.3, venv в `D:\dependency_graph_visualizer\.venv`). `app.exe` обновить не удалось — файл занят другим процессом (exe не тронут, `.bak` рядом) |

## Новый артефакт

`tests/run_phase3_suite.gd` — headless-драйвер для `Phase3StressTestRunner` по образцу
`run_phase4_suite.gd`. Ключевой кейс — `TestSimulationThreadStartIdempotency` (двойной `Start()`
на одном экземпляре должен чисто останавливаться) = прямая проверка B-03.

## Верификация раунда 2

| Проверка | Результат |
|---|---|
| `dotnet build Game.csproj` | **0 ошибок / 0 предупреждений** |
| `tests/run_test.gd` (14 групп, WorldGen) | ALL TESTS PASSED (Code 0) |
| `tests/run_adversarial_job_distribution_test.gd` (5 групп) | ALL 65K / 1M JOB DISPATCH AUDITS PASSED (Code 0) — TEST 3 «no-dead-zones / No blind zones» напрямую покрывает правку B-02 |
| `tests/run_adversarial_construction_test.gd` (14 групп) | ALL CONSTRUCTION & STRUCTURE AUDITS PASSED (Code 0) |
| `tests/run_adversarial_flow_test.gd` (6 групп) | ALL 6 GPU NAVIGATION & TRANSFORM TESTS PASSED (Code 0) |
| `tests/run_phase3_suite.gd` (новый) | passed (Code 0); в логе видна ветка «Предыдущая симуляция активна…», ошибки таймаута нет, `Stop(5000)` = true |
| `tests/run_phase4_suite.gd` / `run_phase5_suite.gd` | passed (Code 0) |
| `DependencyGraphVisualizer.exe --cli --project … --deps …` | exit 0, полный отчёт |

**Инвариант одной петли:** статический `_loopGeneration` безопасен — ни `Main.cs`, ни тесты
(`Phase1/Phase3`, `TestRenderLodBenchmark`, `TestMacroRenderGpuLoad`) не держат две симуляции
одновременно; тесты создают экземпляр без `Start()` и вызывают `Dispose()`.