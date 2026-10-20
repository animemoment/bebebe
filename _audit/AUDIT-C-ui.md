# AUDIT-C — UI / Render / Integration / Hot-path (adversarial audit)

- **Auditor:** `@destroyer` / teammate `audit-ui`
- **Task:** `task-3` (AUDIT-C)
- **Project:** `D:\bebebe-main (1)\bebebe-main` (Godot 4.7 mono / .NET 8 / C#)
- **Read-only mode:** no file under `src/`, `scenes/`, `tests/` was modified. The only file created by this audit is this report.
- **Scope of this pass:** `src/ui/**`, `scenes/main/Main.cs`, `src/core/common/**` (GameProfiler, ProfilerLogService, SimEvents), `src/core/world/access/**`, `WorldMapTileMapper.cs`, plus `scenes/ui/*.tscn` node names used as `GetNode`/`FindChild` targets. `src/simulation/**` in general is **out of scope** (one out-of-scope observation is flagged separately at the end).
- **Reproduce:**
  - `D:\dependency_graph_visualizer\DependencyGraphVisualizer.exe --cli --project "D:\bebebe-main (1)\bebebe-main" --hotpaths` (31 hot methods)
  - `... --signals` (subscription map), `... --scenes` (15 scenes → node graph)
  - scene cross-check: `Select-String -Path scenes\ui\*.tscn -Pattern '^\[node name="([^"]+)"'`
- **Method:** for every candidate the *code path* was read end-to-end and the failure scenario written down **before** it was accepted as a defect. Candidates that did not survive verification are listed in §3 ("проверено — не дефект") together with the reason, so the lead does not re-litigate them.

## 1. Summary table

| ID | Finding | Severity | Confidence | Category | Anchor |
|----|---------|----------|-----------|----------|--------|
| C-01 | `OnStoneMined` re-subscribed on **every** map generation; unsubscribed once | P2 | high | 2 subscriptions | `MapRenderer.cs:851` vs `:1486` |
| C-02 | Work-zone (JobDisplay) close button `esc` is never bound → dead × button | P2 | high | 3 wrong node paths | `WorkZoneController.cs:141` |
| C-03 | `AgentRenderer.Initialize` does not reset `_hasInitialSnapshot` (+ mesh has no `VisibleInstanceCount=0`) → agents pile up / fly in from (0,0) after a region switch | P2 | high | 3 stale state + 4 render/sim | `AgentRenderer.cs:244`, `:167-175`, `:402-412` |
| C-04 | One `_isMouseDown` flag for LMB+RMB; tools act on the **released** button → drag can invert (unmark/remove instead of mark) | P2 | high | 5 tools | `PlayerInteractionManager.cs:95-107` |
| C-05 | Draft (door mode) RMB leaves `IsSelecting=true` and `_isRightDragging=true` → stuck red selection rect | P2 | high | 5 tools | `ZoneDraftTool.cs:78-91,136-139,229-240` |
| C-06 | `Stopwatch.StartNew()` heap allocation per frame in renderers | P2 | high | 1 hot allocs | `GroundItemRenderer.cs:91`, `AgentRenderer.cs:480,498`, `HUDController.cs:760` |
| C-07 | Per-mouse-motion `Godot.Collections.Array` + `new[]{}` allocations in ghost preview | P2 | high | 1 hot allocs | `BuildTool.cs:47,64`, `ZoneDraftTool.cs:367` |
| C-08 | `_pendingCells`/`_choppedTreesQueue` (and the render event handlers) have no generation guard → previous world's cells land in the freshly generated map | P2 | medium | 4 render/sim consistency | `MapRenderer.cs:476-492,794-821,356-408` |
| C-09 | `TerrainTileLayer.Flush` is unbudgeted and calls `exists()` **twice** per dirty cell → one-frame spike on mass marking | P2 | medium | 1 hot allocs / 4 | `TerrainTileLayer.cs:49-68` |
| C-10 | `SimEvents.Record` writes 7 fields of a shared ring slot unsynchronized from sim thread + main thread | P2 | medium | 6 profiler correctness | `SimEvents.cs:88-91` |
| C-11 | RMB drag paints the **positive** build ghost while release removes; `_isLeftClick` is a dead field in 5 tools | P3 | medium | 5 tools | `BuildTool.cs:78-84,131-155` |
| C-12 | `OnTreeChopped`/`OnStoneMined` write `_pendingMapData` that `ApplyMap` has just nulled → dead branch | P3 | medium | 3 latent | `MapRenderer.cs:819,1050,1064` |

Open branch: `Main.cs:263-264` explicitly tolerates "the old sim thread did not stop within 3 s" — that path is the trigger amplifier of C-08 (see the scenario there).

## 2. Findings

### [C-01] `StoneJobManager.OnStoneMined` is re-subscribed on every map application — Severity P2 — Confidence high

- **Файл:** `src/ui/renderers/MapRenderer.cs:851` (подписка), `:1486` (единственная отписка), `:354` (правильный близнец)
- **Код:**
  ```csharp
  // MapRenderer.cs:846-851, ВНУТРИ private void ApplyMap()
  TileSet objectTileSet = CreateObjectTileSet();
  _objectLayer.TileSet = objectTileSet;

  // Россыпи камня стираем из object-слоя по событию добычи
  // (близнец OnTreeChopped) — клетки копим в тот же батч-очередь.
  StoneJobManager.Instance.OnStoneMined += OnStoneMined;
  ```
  ```csharp
  // MapRenderer.cs:353-354, ВНУТРИ _Ready() — подписка ровно один раз
  StockpileItems = stockpileItemRenderer;
  TreeJobManager.Instance.OnTreeChopped += OnTreeChopped;
  ```
  ```csharp
  // MapRenderer.cs:1483-1486
  public override void _ExitTree()
  {
      TreeJobManager.Instance.OnTreeChopped -= OnTreeChopped;
      StoneJobManager.Instance.OnStoneMined -= OnStoneMined;
  ```
- **Дефект:** подписка на `OnStoneMined` сделана не в `_Ready`, а в `ApplyMap`, который вызывается на **каждую** генерацию карты (`OnRegionSelected` и `OnSeedRegenerated` → `mapRenderer.StartAsync()` → `Callable.From(ApplyMap).CallDeferred()`, `Main.cs:271,332,470`). Перед повторной подпиской нет парной отписки (`-=` в `ApplyMap` отсутствует). `_ExitTree` снимает обработчик ровно один раз.
- **Сценарий отказа:** игрок меняет регион на мировой карте (штатный сценарий, 2-й запуск карты) → `OnStoneMined` имеет 2 обработчика. Каждая добытая клетка камня обрабатывается дважды и дважды кладёт клетку в `_choppedTreesQueue` (`:1067`). После N перезаходов/регенераций — N вызовов `MapRenderer.OnStoneMined` на 1 камень и N ссылок в invocation-list `StoneJobManager.Instance` (singleton живёт всю сессию): освобождённый `MapRenderer` (вместе с `TileMapLayer`-нодами и Mono-обёртками) перестаёт быть достижимым для GC → утечка на каждом перезаходе. Прочие слушатели этого события (`DesignationRenderer.cs:73`, отписка корректная `:197`) остаются с одним вызовом — множится ровно обработчик `MapRenderer`.
- **Минимальный фикс:** перенести подписку в `_Ready` рядом со строкой 354 (симметрично `OnTreeChopped`); либо в начале `ApplyMap` сделать `StoneJobManager.Instance.OnStoneMined -= OnStoneMined;` перед `+=`.
- **Как проверить:** временный счётчик в `OnStoneMined` (`_minedCount++` + `GD.Print`) — после двух смен региона на одну добычу камня должно приходить 1 срабатывание, а не 2. Проверить, что счётчик срабатываний `ApplyMap` > 1 (лог `MapRenderer: карта сгенерирована`).

### [C-02] Окно рабочей зоны (JobDisplay) не имеет рабочей кнопки закрытия — Severity P2 — Confidence high

- **Файл:** `src/ui/controllers/WorkZoneController.cs:141-149`, сцена `scenes/ui/JobDisplay.tscn:139`
- **Код:**
  ```csharp
  // WorkZoneController.cs:141-149
  _escapeButton = _rootPanel.FindChild("Escape", true, false) as BaseButton
                  ?? _rootPanel.FindChild("Close", true, false) as BaseButton
                  ?? _rootPanel.FindChild("Exit", true, false) as BaseButton
                  ?? _rootPanel.FindChild("Back", true, false) as BaseButton;

  if (_escapeButton != null)
  {
      _escapeButton.Pressed += () => WorkZoneManager.Instance.DeselectZone();
  }
  ```
  ```
  # scenes/ui/JobDisplay.tscn:139  (полный список нод сцены: Control, VScrollBar, MainWindow,
  # NameWork, Tiles, HSlider, Number, Worker, HSlider, Number, Prioritet, Number,
  # esc, Information, ButtonForFarm, CheckMark, Infoormation)
  [node name="esc" type="Button" parent="VScrollBar/MainWindow" unique_id=1175255156]
  ```
- **Дефект:** в сцене кнопка закрытия называется `esc`; код ищет только `Escape`/`Close`/`Exit`/`Back` (ни одной из этих нод в сцене нет). `FindChild` регистрозависим, поэтому `_escapeButton == null` и обработчик не подключается. Скан прямых детей по имени тоже не делается (в `FarmController` он есть — см. §3). В `WorkZoneController` нет ни `_UnhandledInput`, ни `_Input` с `Key.Escape` (`Select-String` по файлу — 0 совпадений), т.е. клавиатурного фолбэка тоже нет.
- **Сценарий отказа:** игрок кликает рабочую зону → открывается `JobDisplay` → клик по кнопке-крестику `esc` не делает ничего (кнопка живая, `MouseFilter` не тронут, но `Pressed` не подключён). Закрыть окно можно только снятием выделения зоны (клик по другой зоне/пустому месту). Игрок воспринимает это как «окно зависло».
- **Минимальный фикс:** добавить `?? _rootPanel.FindChild("esc", true, false) as BaseButton` в цепочку (как в `BuildMenuController.cs:123` и `FarmController.cs:117-119`).
- **Как проверить:** запустить игру, выделить ворк-зону, кликнуть по `esc` — окно должно закрыться (`WorkZoneManager.Instance.DeselectZone()`); в отладчике `_escapeButton` не должен быть `null`.

### [C-03] `AgentRenderer.Initialize` не сбрасывает `_hasInitialSnapshot` → агенты «вылетают» из угла (0,0) после смены региона — Severity P2 — Confidence high

- **Файл:** `src/ui/renderers/AgentRenderer.cs:167-175` (создание меша), `:244` (конец `Initialize`), `:400-412` (защита, которая обходится)
- **Код:**
  ```csharp
  // AgentRenderer.cs:167-175 — у свежего меша НЕ выставлен VisibleInstanceCount
  _multiMesh = new MultiMesh
  {
      Mesh = quadMesh,
      TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
      UseColors = _gpuInterpolationEnabled,
      UseCustomData = _gpuInterpolationEnabled,
      InstanceCount = agentCount,
      CustomAabb = mapAabb
  };
  ```
  ```csharp
  // AgentRenderer.cs:244 — единственное, что выставляется в конце Initialize
          _initialized = true;
      }
  ```
  ```csharp
  // AgentRenderer.cs:400-413
  if (latestSnapshot != null)
  {
      if (!_hasInitialSnapshot)
      {
          // Первый снапшот: заполняем и prev, и target стартовыми позициями,
          // чтобы агенты не «выстреливали» из угла (0,0) в начале работы.
          Array.Copy(latestSnapshot, _targetPositions, _agentCount);
          for (int i = 0; i < _agentCount; i++)
          {
              _prevPositions[i] = _targetPositions[i];
              _facingAngles[i] = 0f;
          }
          _hasInitialSnapshot = true;
      }
  ```
- **Дефект:** `Initialize` пересоздаёт массивы `_prevPositions/_targetPositions` (нули) и меш, но **не сбрасывает** `_hasInitialSnapshot`, `_lerpFactor`, `_bodiesDirty`, `_shadowsDirty`. После смены региона (`Main.OnMapApplied` → `_agentRenderer.Initialize(_agentThread, AgentCount)`, `Main.cs:386`) флаг остаётся `true`, поэтому ветка «первый снапшот» (которая и существует, чтобы агенты не появлялись из (0,0)) больше не срабатывает: `_prevPositions` остаются нулями, а `_targetPositions` получают реальные координаты → все агенты интерполируются от (0,0) к своим позициям. Плюс свежий `MultiMesh` создан без `VisibleInstanceCount = 0` (ср. `GroundItemRenderer.cs:60`, где оно есть), т.е. дефолт движка (-1 = все инстансы видимы) рисует `InstanceCount` трансформов до первой заливки буфера, а заливка пропускается early-out'ом `:390`, если `_bodiesDirty == false`.
- **Сценарий отказа:** игрок открывает мировую карту (M), выбирает новый регион → после применения карты в течение нескольких кадров (пока новый sim-поток не опубликует первый снапшот) все 10 000 спрайтов колонистов стоят кучей в мировом (0,0), затем «размазываются» к своим настоящим клеткам за ~2-3 кадра (`_lerpFactor` 0→1 при `lerpSpeed = 25`). Пользователь видит вспышку агентов в углу карты на каждой смене региона / регенерации сида.
- **Минимальный фикс:** в конце `Initialize` (перед `_initialized = true`) добавить `_hasInitialSnapshot = false; _lerpFactor = 0f; _bodiesDirty = true; _shadowsDirty = true;` и `VisibleInstanceCount = 0` в инициализатор `_multiMesh` (и `_shadowMultiMesh`).
- **Как проверить:** сменить регион и записать кадры (`godot_command screenshot` сразу после `OnMapApplied`) — агентов в (0,0) быть не должно; либо временный `GD.Print(_hasInitialSnapshot, _prevPositions[0])` в начале `_Process`.

### [C-04] Один флаг `_isMouseDown` на ЛКМ+ПКМ, инструменты действуют по «отпущенной» кнопке — Severity P2 — Confidence high

- **Файл:** `src/ui/PlayerInteractionManager.cs:95-107`, потребители `src/ui/tools/{TreeFellingTool,StoneMiningTool,FarmingTool,WarehouseTool,BuildTool}.cs`
- **Код:**
  ```csharp
  // PlayerInteractionManager.cs:95-107
  if (mouseBtn.Pressed && (isLeft || isRight))
  {
      _isMouseDown = true;
      _startTile = tilePos;
      _currentTool.OnClick(tilePos, worldPos, isLeft);
  }
  else if (!mouseBtn.Pressed && _isMouseDown && (isLeft || isRight))
  {
      _isMouseDown = false;
      _currentTool.OnRelease(_startTile, tilePos, worldPos, isLeft);
  }
  ```
  ```csharp
  // TreeFellingTool.cs:50-52 — «какая кнопка отпущена», а не «какой начат драг»
  public void OnRelease(Vector2I startTile, Vector2I endTile, Vector2 worldPos, bool isLeftClick)
  {
      if (!_isDragging) return;
  ```
- **Дефект:** состояние нажатия — единый `_isMouseDown` без хранения того, **какая** кнопка начала драг. `OnRelease` получает `isLeft` от **отпущенной** кнопки. Пока ЛКМ удерживается (идёт драг разметки), нажатие и отпускание ПКМ доставляет в инструмент `OnClick(..., isLeft=false)` и затем `OnRelease(..., isLeftClick=false)` → инструмент выполняет обратную операцию (снятие меток / удаление зоны/чертежа) вместо простановки. Симметрично: отпускание ЛКМ после начатого ПКМ-драга удаления.
- **Сценарий отказа:** игрок зажимает ЛКМ и растягивает прямоугольник для вырубки леса, по привычке (или случайно) щёлкает ПКМ, отпуская её — весь растянутый прямоугольник **снимает** метки вместо того, чтобы их поставить (`UnmarkTreesBatch(_cellBuffer)`), а затем отпускание ЛКМ уже ничего не делает (`_isMouseDown` уже `false`). Второй путь: альт-таб/потеря фокуса во время драга — событие release не приходит, `_isMouseDown` остаётся `true`, и первый же клик после возврата в игру воспринимается как завершение «зависшего» драга от старой точки (`_startTile`).
- **Минимальный фикс:** хранить кнопку-инициатор (`_dragButton`) и передавать в `OnRelease` именно её: `OnRelease(_startTile, tilePos, worldPos, _dragButtonIsLeft)`. Сброс `_isMouseDown`/`_dragButton` в `SetTool` (`PlayerInteractionManager.cs:43-52`) и в `OnToolReset`/Esc-ветке.
- **Как проверить:** юнит-тест на `PlayerInteractionManager`: LMB press → RMB press → RMB release → ожидать `OnClick(isLeft:true)`-семантику для release; в игре — ЛКМ-драг разметки с коротким ПКМ-кликом должен по-прежнему **ставить** метки.

### [C-05] Дверной режим черновика: ПКМ оставляет «залипание» — красная рамка выбора и `_isRightDragging` не сбрасываются — Severity P2 — Confidence high

- **Файл:** `src/ui/tools/ZoneDraftTool.cs:78-91` (начало ПКМ-драга), `:136-139` (ранний `return`), `:229-240` (`Cancel`)
- **Код:**
  ```csharp
  // ZoneDraftTool.cs:78-91 (внутри OnClick)
  if (!isLeftClick)
  {
      // ПКМ — драг удаления/вырезания проёмов
      _isRightDragging = true;
      _rightDragRemoval = _removalPass;
      _selectionBox?.StartSelection(_dragStart);
      return;
  }
  ```
  ```csharp
  // ZoneDraftTool.cs:136-139 (внутри OnRelease)
  if (!isLeftClick)
  {
      if (_doorMode) return;      // ← выход ДО EndSelection()
      EndSelection(false);
  ```
  ```csharp
  // ZoneDraftTool.cs:229-240 (Cancel)
  public void Cancel()
  {
      _dragging = false;
      ClearGhost();
      _zoneCells.Clear();
      _wallCells.Clear();
      _removedWalls.Clear();
      _worldZoneCells.Clear();
      _worldWallCells.Clear();
      _selectionBox?.CancelSelection();
      _selectionBox?.ResetDefaultStyle();
      // ← сброса _isRightDragging здесь НЕТ (grep: присваивания только в :83, :108, :162)
  }
  ```
- **Дефект:** порядок проверок такой: ветка ПКМ (`:80-92`) **до** проверки `_doorMode` — она успевает выставить `_isRightDragging = true`, `_dragStart`, и запустить красное выделение `CancelFill` (`StartSelection`). После этого и `OnDrag:124-125` (`if (_doorMode) return;`), и `OnRelease:138-139` (`if (_doorMode) return;`) выходят **до** `EndSelection()` и до сброса `_isRightDragging`. Итог в дверном режиме: `SelectionBox.IsSelecting` остаётся `true` (красная рамка `CancelFill` заморожена на экране — `UpdateSelection` тоже не вызывается), `_isRightDragging` остаётся `true`, а ветка удаления по ПКМ (`:158-162`) недостижима (в дверном режиме ПКМ-драг вообще ничего не удаляет). Сбрасывается `_isRightDragging` только в двух местах — `:108` (обычное нажатие ЛКМ) и `:162` (обычное отпускание ПКМ), — оба недостижимы при `_doorMode`.
- **Сценарий отказа (два шага, оба видны игроку):**
  1. Игрок ставит склад со стенами, включает режим дверей и делает ПКМ-вырез в стене → после отпускания ПКМ на экране остаётся красный прямоугольник; он не исчезает ни по отпусканию мыши, ни движением (рамка «залипла»), снять его можно только `CancelDraft`/`ConfirmDraft` (там вызывается `CancelSelection`).
  2. Игрок выключает режим дверей и ведёт мышью: `OnDrag:122` пропускает управление дальше (т.к. `_isRightDragging == true`), `:129` вызывает `_selectionBox.UpdateSelection(...)` → «залипшая» красная рамка снова начинает следовать за курсором, а следующее отпускание ПКМ отрабатывает как удаление по прямоугольнику `_dragStart`→`endTile` — т.е. отложенное удаление клеток зоны/чертежей, которое игрок инициировал ещё в дверном режиме и уже забыл.
- **Минимальный фикс:** перенести проверку `if (_doorMode)` в начало `OnClick` (до ветки ПКМ) — как минимум для ПКМ; в ветке `if (_doorMode)` в `OnRelease` перед `return` вызывать `_selectionBox?.EndSelection(); _selectionBox?.ResetDefaultStyle(); _isRightDragging = false;`; в `Cancel()` (`:229-240`) добавить `_isRightDragging = false;` (и `_rightDragRemoval = false;`).
- **Как проверить:** включить режим дверей, зажать ПКМ и отпустить — `SelectionBox.IsSelecting` должен стать `false` (`godot_command eval` на ноде `SelectionBox`), красная рамка исчезнуть; после Esc-отмены черновика повторный ПКМ-драг не должен удалять стены.

### [C-06] `Stopwatch.StartNew()` — аллокация объекта в каждом кадре рендера — Severity P2 — Confidence high

- **Файл:** `src/ui/renderers/GroundItemRenderer.cs:91`, `src/ui/renderers/AgentRenderer.cs:480` и `:498`, `src/ui/controllers/HUDController.cs:760`
- **Код:**
  ```csharp
  // GroundItemRenderer.cs:88-94 — аллокация ДО проверки null
  // P0-1: рендер — главный поток, но Series/Count на каждый _Process
  // (60/с × типы) всё равно жмут тот же _cLock, что и sim-поток.
  // Оставляем только суммарный замер прохода (один Record).
  var swR = System.Diagnostics.Stopwatch.StartNew();

  if (latestSnapshot.PositionsByItem == null || latestSnapshot.Counts == null)
      return;
  ```
  ```csharp
  // AgentRenderer.cs:480 и :498 (внутри _Process, обе ветки GPU/CPU)
  var swB = System.Diagnostics.Stopwatch.StartNew();
  ```
  ```csharp
  // HUDController.cs:757-760 (RefreshStats, вызывается троттлингом 1/с)
  var swH = System.Diagnostics.Stopwatch.StartNew();
  ```
- **Дефект:** `System.Diagnostics.Stopwatch` — ссылочный тип; `StartNew()` = `new Stopwatch()` → аллокация в куче на каждом вызове. Замер попадает в `_Process` главного потока (а у `AgentRenderer` — в обе ветки пути буфера), поэтому аллокации идут в горячем пути кадра. Проект объявляет минимизацию GC-pressure приоритетом (`AGENTS.md` → `@researcher`: «оптимизация C# (минимизация GC pressure…)»); при 10k агентах и 60 FPS это ≥120 лишних объектов/с только от двух файлов, каждый со счётчиком времени внутри, и они попадают в Gen0 при каждом кадре движения.
- **Сценарий отказа:** массовая симуляция (10k агентов, 100× скорость) — каждый кадр в Gen0 попадают `Stopwatch`-объекты; GC-пауза срабатывает чаще и заметнее именно тогда, когда кадр и без того перегружен (рассинхрон анимации интерполяции агентов рывками). Профильно это видно как `GC0`-серии (`sys.gc0`, `AgentSimulationThread.cs:357`), растущие вместе с FPS рендера.
- **Минимальный фикс:** использовать поля-поля структуры `long _swStart = Stopwatch.GetTimestamp();` + `Stopwatch.GetElapsedTime(_swStart).TotalMilliseconds` (как уже сделано в `GameProfiler.ProfileScope:216,223`) либо `static readonly Stopwatch _sw = new()` + `Restart()` (без аллокации). В `GroundItemRenderer` перенести замер за проверку `null`.
- **Как проверить:** с включённым `GameProfiler.TrackAllocations` сравнить `alloc_b` для `render`-скоупов до/после; либо `dotnet-counters` (Gen0 rate) в сцене с 10k агентов.

### [C-07] Аллокации на каждое движение мыши в ghost-превью — Severity P2 — Confidence high

- **Файл:** `src/ui/tools/BuildTool.cs:44-54,64`, `src/ui/tools/ZoneDraftTool.cs:364-374`
- **Код:**
  ```csharp
  // BuildTool.cs:44-54
  private void PaintGhostPreview(IEnumerable<Vector2I> cells)
  {
      if (_ghostLayer == null || _ghostLayer.TileSet == null) return;
      var arr = new Godot.Collections.Array<Vector2I>();
      foreach (var c in cells)
          arr.Add(c);
      if (arr.Count == 0) return;
      _ghostLayer.SetCellsTerrainConnect(arr, GhostTerrainSetId, GhostWallTerrainId, true);
      ...
  ```
  ```csharp
  // BuildTool.cs:62-65 (OnHover — на каждое событие движения мыши)
  if (_buildingType == BuildingType.WoodWall)
  {
      PaintGhostPreview(new[] { tilePos });
  ```
  ```csharp
  // ZoneDraftTool.cs:364-374 — та же схема на каждый шаг драга
  private void PaintWallGhost(IEnumerable<Vector2I> cells)
  {
      if (_ghostLayer == null || _ghostLayer.TileSet == null) return;
      var arr = new Godot.Collections.Array<Vector2I>();
      foreach (var c in cells)
          arr.Add(c);
  ```
- **Дефект:** два уровня аллокаций на каждое движение мыши: (1) C#-массив `new[] { tilePos }` (плюс boxing при передаче в `IEnumerable<Vector2I>`), (2) `Godot.Collections.Array<Vector2I>` — managed-обёртка + маршалинг, которая живёт только до вызова. Для стены (`WoodWall`) это происходит по каждой клетке под курсором при каждом `_Input`-событии мыши (десятки-сотни раз в секунду при быстром движении), т.к. в `BuildTool`/`ZoneDraftTool` нет переиспользуемого буфера (в `RepaintGhost` рядом уже есть `_ringBuffer`, т.е. паттерн в проекте известен).
- **Сценарий отказа:** игрок быстро ведёт мышью, рисуя стену → десятки `Godot.Collections.Array` + boxing-массивов в секунду, каждая маршалится в движок; на слабой машине это добавляет GC-дрожь и «залипание» ghost за курсором (превью отстаёт от мыши). Особенно заметно на ПКМ/ЛКМ одновременном драге, где `PaintGhostPreview` вызывается и на hover, и на drag.
- **Минимальный фикс:** поле `private readonly Godot.Collections.Array<Vector2I> _ghostArr = new();` с `Clear()` + `AddRange` (или один `SetCell` на одиночную клетку вместо `SetCellsTerrainConnect`), передавать `ReadOnlySpan<Vector2I>` вместо `IEnumerable<Vector2I>`, убрав boxing.
- **Как проверить:** замер `GC.GetAllocatedBytesForCurrentThread()` до/после 100 событий `OnHover`; в профайлере — исчезновение `render`-аллокаций при простоявшемся ghost.

### [C-08] У батч-очередей рендера нет guard'а поколения — клетки прошлого мира попадают на новую карту — Severity P2 — Confidence medium

- **Файл:** `src/ui/renderers/MapRenderer.cs:476-492` (дренаж), `:794-821` (`ClearAllLayers` + `ApplyMap`), `:356-408` (продюсеры), `scenes/main/Main.cs:261-271,322-332`
- **Код:**
  ```csharp
  // MapRenderer.cs:476-492
  if (!_choppedTreesQueue.IsEmpty && _objectLayer != null)
  {
      int budget = MaxCellsPerFrame;
      while (budget-- > 0 && _choppedTreesQueue.TryDequeue(out var cell))
      {
          _objectLayer.EraseCell(cell);
      }
  }
  int cellsBudget = _pendingCells.Count > 4096 ? 16384 : MaxCellsPerFrame;
  while (cellsBudget-- > 0 && _pendingCells.TryDequeue(out var pc))
  {
      if (pc.Layer == null) continue;
      if (pc.Erase) pc.Layer.EraseCell(pc.Pos);
      else pc.Layer.SetCell(pc.Pos, pc.Source, pc.Atlas);
  }
  ```
  ```csharp
  // MapRenderer.cs:794-810 — чистим ТОЛЬКО ноды, очереди не трогаем
  public void ClearAllLayers()
  {
      _groundLayer?.Clear();
      _objectLayer?.Clear();
      ...
      _ghostLayer?.Clear();
      _workProgressLayer?.Clear();
  }

  // MapRenderer.cs:812-821
  private void ApplyMap()
  {
      ...
      _currentMapData = mapData;
      _pendingMapData = null;
      ClearAllLayers();
  ```
  ```csharp
  // MapRenderer.cs:358 — продюсер (StockpileManager, батч-добавление клеток зоны)
  _onZoneTilesBatchAdded = arr => { foreach (var pos in arr) _pendingCells.Enqueue(new PendingCell(_stockpileLayer, new Vector2I(pos.X, pos.Y), SourceGrass, AtlasOrigin, false)); };
  ```
- **Дефект:** `PendingCell` держит ссылку на **ноду** слоя (`_stockpileLayer`, `_blueprintLayer`, `_buildingLayer`) и координаты в системе координат текущей карты. Ноды после `ClearAllLayers()` те же самые, а `_pendingCells`/`_choppedTreesQueue` не опустошаются и не инвалидируются — значит всё, что было поставлено в очередь до смены мира, будет применено уже к **новой** карте. Guard'а «поколение» (`generationId`/`mapEpoch`) нет ни у очередей, ни у lambda-обработчиков `:372-408`, которые в `_ExitTree` вообще не отписываются (там снимаются только именованные `_on*` и `FarmJobManager`).
- **Сценарий отказа (штатный, не экзотика):** `Main.cs:261-264` сам допускает, что старый sim-поток не остановится за 3 с: `if (!_agentThread.Stop(3000)) GD.PrintErr("[MAIN] Предыдущий AgentSimulationThread не остановился в пределах таймаута!");` — после этого код всё равно идёт дальше, старый поток продолжает работать в фоне и публиковать события менеджеров (`OnBuildingPlaced` → `_pendingCells.Enqueue(_buildingLayer, …)`). Игрок в этот момент уже выбрал новый регион: `ClearAllLayers()` стёр слои, затем пришли клетки прошлого мира с координатами, валидными и в новой сетке (размеры карты совпадают) → на новой карте появляются фантомные стены/чертежи/постройки прошлого региона, которые ни один менеджер не знает и ничто не уберёт. Тот же эффект даёт любая задержка дренажа (бюджет 2048/16384 клеток за кадр) при массовой разметке перед сменой региона.
- **Минимальный фикс:** добавить `int _mapEpoch` и хранить его в `PendingCell`/проверять при дренаже, либо в `ApplyMap`/`ClearAllLayers` опустошать очереди: `while (_pendingCells.TryDequeue(out _)) { }` + `_choppedTreesQueue.Clear()`; плюс отписывать lambda-обработчики `:372-408` в `_ExitTree` (для этого их нужно превратить в методы или хранить делегаты в полях).
- **Как проверить:** временно затормозить дренаж (`cellsBudget = 1`), пометить 100k клеток склада, сразу переключить регион → на новом регионе не должно остаться фиолетовых клеток склада/чертежей; в логе — `[SPAWN] Выбран регион` после `MapRenderer: карта сгенерирована`.

### [C-09] `TerrainTileLayer.Flush` без бюджета и с двойным вызовом `exists()` — Severity P2 — Confidence medium

- **Файл:** `src/ui/renderers/TerrainTileLayer.cs:49-68`, вызовы `src/ui/renderers/MapRenderer.cs:516-519`
- **Код:**
  ```csharp
  // TerrainTileLayer.cs:49-68
  public void Flush(Func<Vector2I, bool> exists)
  {
      if (_dirty.Count == 0) return;
      if (_layer?.TileSet == null) { _dirty.Clear(); return; }

      foreach (var c in _dirty)
      {
          if (!exists(c))
              _layer.EraseCell(c);
      }
      var cells = new Godot.Collections.Array<Vector2I>();
      foreach (var c in _dirty)
      {
          if (exists(c))          // ← второй вызов для каждой клетки
              cells.Add(c);
      }
      _dirty.Clear();
      if (cells.Count > 0)
          _layer.SetCellsTerrainConnect(cells, _terrainSetId, _terrainId, true);
  }
  ```
  ```csharp
  // MapRenderer.cs:516-519 — колбэки, каждый = обращение к менеджеру-синглтону
  _builtWalls?.Flush(c => StructureManager.Instance.IsWallAt(c.X, c.Y));
  _wallBlueprints?.Flush(c => ConstructionManager.Instance.IsWallSiteAt(c.X, c.Y));
  _farmTerrain?.Flush(c => FarmJobManager.Instance.IsGardenBed(c.X, c.Y));
  _farmBlueprintTerrain?.Flush(c => FarmJobManager.Instance.IsPlotMarked(c.X, c.Y));
  ```
- **Дефект:** `_dirty` копится без ограничения (в `MapRenderer` нет бюджета для `TerrainTileLayer`, в отличие от `_pendingCells`), а `Flush` обрабатывает **все** клетки в одном кадре, вызывая `exists()` дважды на клетку (сначала чтобы стереть, потом чтобы добавить) — т.е. 2×N обращений к менеджерам через лямбду: 2 делегированных вызова + `HashSet`/массивные обращения на каждую клетку. Плюс выделяется `Godot.Collections.Array<Vector2I>` на весь объём (маршалинг десятков-сотен тысяч элементов в одном кадре).
- **Сценарий отказа:** игрок размечает ферму 500×500 (250 000 клеток — это ровно потолок инструмента, `FarmingTool.cs:66` `if (cellCount > 250_000) return;`) — `FarmJobManager.OnPlotsBatchMarked` → `_onPlotsBatchMarked` (`MapRenderer.cs:415-419`) вызывает `_farmBlueprintTerrain.MarkDirty(...)` на каждую клетку, затем `MapRenderer._Process` за один кадр делает ~500 000 вызовов `IsPlotMarked` + маршалит 250k-элементный массив в `SetCellsTerrainConnect` → фриз кадра на секунды/десятки мс, ровно тот случай, от которого комментарий `MapRenderer.cs:484-485` защищает соседний путь (`_pendingCells`), но не этот.
- **Минимальный фикс:** один проход с локальным `List<Vector2I>`/переиспользуемым полем-массивом: `bool e = exists(c); if (e) cells.Add(c); else _layer.EraseCell(c);` (сокращает 2N → N), добавить лимит клеток на кадр (например, 4096) с переносом остатка в `_dirty` следующего кадра, и переиспользовать `Godot.Collections.Array` поле.
- **Как проверить:** замер длительности кадра (`GameProfiler`/`_Process` MS) при разметке фермы 500×500 — не более ~16 мс на кадр; профайлер покажет отсутствие пика в `Render`-скоупе.

### [C-10] `SimEvents.Record` пишет 7 полей общего ring-слота без синхронизации (sim-поток + главный поток) — Severity P2 — Confidence medium

- **Файл:** `src/core/common/SimEvents.cs:85-92` (запись), `:174-188` (чтение), `src/simulation/AgentSimulationThread.cs:362`, `src/ui/renderers/GroundItemRenderer.cs:131`
- **Код:**
  ```csharp
  // SimEvents.cs:85-92
  public static void Record(string kind, string name, float v1 = 0, long v2 = 0, int v3 = 0, string meta = null)
  {
      if (!Capture.Enabled) return; // A13: триггер записи
      int i = (int)((uint)Interlocked.Increment(ref _ringHead) % RingCap);
      var e = _ring[i] ??= new Ev();
      e.TsReal = NowSec(); e.TsGame = _gameTime; e.Speed = _speed;
      e.Kind = kind; e.Name = name; e.V1 = v1; e.V2 = v2; e.V3 = v3; e.Meta = meta;
  }
  ```
  ```csharp
  // SimEvents.cs:174-180 (читатель — главный поток)
  int head = Volatile.Read(ref _ringHead);
  int n = Math.Min(maxRows, Math.Min(head, RingCap));
  for (int k = 0; k < n; k++)
  {
      int i = (head - n + k + RingCap * 2) % RingCap;
      var e = _ring[i];
      if (e == null || e.Kind == null) continue;
  ```
- **Дефект:** кольцо `_ring` — общий статический массив, защищённый только атомарным инкрементом индекса. Запись слота — это check-then-act (`??=`) + 7 несвязанных присваиваний. Писатели: sim-поток (`SimEvents.Mark("SIM_LAG", …)`, `AgentSimulationThread.cs:362`, и `ProfileScope` из симуляционных систем) и главный поток (`GroundItemRenderer.cs:131`, `AgentRenderer.cs:489,508`, `HUDController.cs:774`, `TimeManager.cs:37`). Читатель (`BuildEventsCsv`/`BuildChromeTrace`, UI-экспорт) работает на главном потоке.
- **Сценарий отказа:** два потока получают один и тот же `i` → оба создают свой `Ev` и второй перезаписывает ссылку первого, а уже записанные поля «победителя» исчезают; читатель в это время видит смешанную запись (`Kind` от одного события, `V1`/`Name` от другого) и экспортирует в CSV/Chrome-trace строку с чужими числами. Диагностика по трейсу («какой scope дал 120 мс») становится неверной — именно тот класс ошибок, который профайлер должен ловить. Отдельно `Reset()` (`:232-239`) делает `Array.Clear(_ring, …)` под `_cLock`, который писатели **не берут** → экспорт/сброс во время активной симуляции даёт строки без имени (`Name == null` → пустая колонка в CSV).
- **Минимальный фикс:** писать слот «двухфазно»: заполнять локальный `Ev` и публиковать ссылку через `Volatile.Write(ref _ring[i], local)` (читатель уже терпит `null`), либо завести отдельный `Ev[]` на поток-писатель и сливать в UI по запросу; либо в `Reset()` сначала останавливать/гейтить `Capture.Enabled` и брать `_cLock` у писателей.
- **Как проверить:** стресс-тест: включить `Capture`, включить скорость 100×, экспортировать CSV — не должно быть строк с пустым `scope`/`dur` или с `ts_real`, выходящим за монотонность; повторить 20 раз.

### [C-11] ПКМ рисует «положительный» ghost, а по отпусканию удаляет; `_isLeftClick` — мёртвое поле — Severity P3 — Confidence medium

- **Файл:** `src/ui/tools/BuildTool.cs:78-84,131-155`; аналогично `TreeFellingTool`, `StoneMiningTool`, `WarehouseTool`, `FarmingTool`
- **Код:**
  ```csharp
  // BuildTool.cs:78-84 (OnClick)
  _isDragging = true;
  _dragStart = tilePos;
  _isLeftClick = isLeftClick;   // ← присваивается
  ...
  ```
  ```csharp
  // BuildTool.cs:131-155 (OnRelease) — используется НЕ сохранённый флаг, а кнопка релиза
  public void OnRelease(Vector2I startTile, Vector2I endTile, Vector2 worldPos, bool isLeftClick)
  {
      _isDragging = false;
      _lastDragTile = new(-1, -1);
      ClearGhost();
      ...
      if (isLeftClick) { ... StartSitesBatch(validIslandCells, def, _mapData); }
      else            { ... CancelSitesBatch(islandCells); }
  ```
- **Дефект:** `_isLeftClick` сохраняется в `OnClick`, но в `OnRelease` не читается — `OnRelease` принимает собственный `isLeftClick` (см. C-04), т.е. поле мёртвое, а поведение определяется кнопкой релиза. Пока ЛКМ-драг активен, каждый ПКМ-hover продолжает рисовать **положительное** превью (`OnDrag` → `PaintGhostPreview`, `BuildTool.cs:100-128`), а по отпусканию ПКМ чертежи сносятся — превью и результат противоречат друг другу. Проверено grep'ом по `src/ui/tools`: поле объявлено и присваивается, но **не читается нигде** — `BuildTool.cs:17,82`, `TreeFellingTool.cs:13,31`, `WarehouseTool.cs:11,26`, `FarmingTool.cs:14,33` (8 совпадений = 4 объявления + 4 присваивания, 0 чтений; в `StoneMiningTool` поля нет вовсе).
- **Сценарий отказа:** игрок тянет стену ЛКМ, случайно задевает ПКМ (она рисует стену в превью), отпускает ПКМ → стены не строятся, а уже стоящие чертежи под курсором сносятся. Визуально: «нарисовал — исчезло».
- **Минимальный фикс:** в `OnRelease` использовать сохранённый `_isLeftClick` (и обозначить его как единственный источник истины), либо удалить поле и явно блокировать смену кнопки во время драга в `PlayerInteractionManager` (см. C-04).
- **Как проверить:** ЛКМ-драг + короткий ПКМ-клик → стена должна строиться; `grep -n "_isLeftClick" src/ui/tools/**` должен показать хотя бы одно **чтение** поля (сейчас 0 — 8 совпадений только объявления/присваивания).

### [C-12] Мёртвая ветка: `OnTreeChopped`/`OnStoneMined` пишут в `_pendingMapData`, который `ApplyMap` уже обнулил — Severity P3 — Confidence medium

- **Файл:** `src/ui/renderers/MapRenderer.cs:819` (`_pendingMapData = null;`), `:1045-1053`, `:1056-1068`
- **Код:**
  ```csharp
  // MapRenderer.cs:818-819 (ApplyMap)
  _currentMapData = mapData;
  _pendingMapData = null;
  ```
  ```csharp
  // MapRenderer.cs:1050-1053 (OnTreeChopped)
  _pendingMapData?.TreeOnGrass[pos.X, pos.Y] = false;   // ← всегда null после ApplyMap
  _objectLayer?.EraseCell(pos);
  _choppedTreesQueue.Enqueue(new Vector2I(pos.X, pos.Y));
  ```
- **Дефект:** после `ApplyMap` (первая же генерация завершает окно `_pendingMapData`) обе строки превращаются в no-op: флаг `TreeOnGrass/StoneOnGrass` у **актуальной** карты рендером не снимается. Ветка выглядит как рабочая защита (её наличие читается как «флаг синхронизирован»), но не выполняется.
- **Честная оценка последствий:** сегодня дефект **не виден игроку**, потому что симуляция снимает флаг в тех же самых массивах: `SimulationContext.StoneOnGrass = stoneOnGrass` — прямая ссылка на `MapData.StoneOnGrass` (`src/simulation/SimulationContext.cs:42`), и `MiningJobHandler.cs:87` / `TreeChoppingJobHandler.cs:79` пишут `false` в тот же массив. Т.е. это латентный дефект: он сработает, как только флаг начнёт снимать кто-то, кроме симуляции (или для ветки мирового стриминга, где островной массив не задействован). Отдельно: пока генерация **в полёте** (окно между `StartAsync` и `ApplyMap`), запись идёт в будущую карту — если дерево срублено в этом окне, флаг на новой карте снимется до её применения (сценарий гонки, требует одновременности).
- **Минимальный фикс:** `(_currentMapData)?.TreeOnGrass[…] = false;` либо явный комментарий-инвариант «флаг снимает только симуляция», чтобы будущий рефакторинг не полагался на неработающую строку.
- **Как проверить:** временный лог в `OnTreeChopped` — после смены региона `_pendingMapData == null` (проверяется `godot_command eval` на `MapRenderer`).

## 3. Проверено — не дефект (чтобы не переоткрывать)

| Кандидат | Почему не дефект |
|---|---|
| `MapRenderer.cs:530-533` — `ShadowRenderer.Visible = false` при `zoom < 0.22` | Нода не «залипает»: `Main.cs:434` каждый кадр зовёт `ShadowRenderer.Tick(sun, delta)`, который сам пересчитывает видимость. |
| `WorldMapOverlay.cs:125,221` — `GetNodeOrNull<Button>("Control/BtnClose")` | Ноды `BtnClose` в `scenes/ui/WorldMap.tscn` нет (проверено по `[node name=…]`), но код null-safe (`?.`), закрытие идёт кнопкой в сцене + `Main.ToggleWorldMap` (клавиша M). По истории проекта (`tools/history_analysis.json`, `tools/full_chronicle_data.json`) удаление ноды намеренное. |
| `MapRenderer.cs:1045-1068` — запись в `_pendingMapData` | См. C-12: симуляция пишет в те же массивы по прямой ссылке, поэтому игрок не видит расхождения. Оставлено как латентный P3, не как баг. |
| `HUDController.cs:144` — `GetTree().Root.SizeChanged += ApplyResponsiveLayout` | Парная отписка есть: `HUDController._ExitTree:1480`. Там же снимаются `StockpileManager.OnItemCountChanged` (`:1482`) и оба `OnToolReset` (`:1485-1492`). |
| `GameProfiler` — деление на ноль | Все деления защищены: `SnapshotMetrics:288-291` (`windowSec`, `frames` ≥ 1), `:317` `calls > 0`, `RefreshBalance:136-138` `perFrameDivisor`, `:158` пропуск пустых фаз. `RefreshBalance` не течёт: `idx` + `Array.Resize` вместо списка. |
| `ProfilerLogService` — рост памяти лога | Ограничен: `_seconds` по `KeepSeconds`/`MaxRecordedSeconds` (`:225-228`), `_inputs` по `MaxInputs` (`:288-289`), `_spikes` до 2000 (`_spikes.RemoveAt(0)`). Неограниченны только пользовательские `_markers`/`_notes` — это осознанный журнал. |
| `DayNightCycle.Tick` — запись `CanvasModulate.Color` каждый кадр | Самоограничение: тик раз в 0.1 с + epsilon-проверка дистанции цвета (`DayNightCycle.cs:76`), O(1) на кадр. |
| `TerrainTileLayer.Flush` — аллокация на кадр | Не в простое: ранний выход `if (_dirty.Count == 0) return;` (`:51`) до любых аллокаций. Аллокация возможна только при реальной пачке — это C-09. |
| `AgentRenderer._Process` — заливка буфера каждый кадр | Есть dirty-флаги + early-out (`:389-391`): при осевшем lerp и отсутствии снапшотов кадр пропускается целиком (`Buffer=` не вызывается). |
| `GroundItemRenderer._Process` — работа при отсутствии изменений | Ранний выход `if (!GroundItemManager.Instance.SnapshotQueue.TryDequeue(...)) return;` (`:78-79`) — при простое симуляции кадр не трогает GPU. |
| `FarmController.cs:117-119` — поиск кнопки `esc` | Здесь `esc` обрабатывается корректно (скан прямых детей + `case "esc"`), т.е. C-02 — дефект **только** `WorkZoneController`. Симптом-близнец в `Farm` сознательно исключён как ложное срабатывание. |
| `MapRenderer.cs:1419,1434,1448` — `FindChild("wood_wall_tile"/"mountains_tile"/"GardenBeds")` | Все три ноды реально есть в `scenes/main/Main.tscn` (проверено по `[node name=…]`). |
| Опечатки в именах нод сцен (`TreeFelingButton`, `OrderVarinat`, `Infoormation`) | Код использует `??`-фолбэки (`HUDController.cs:125-129` — скорость/`stop`, `HUDController.cs:597-598` — `PriorityBoard`, `WorkZoneController.cs:95-98`, `FarmController.cs:120-122`) — покрыто. |
| `WorldMapOverlay._Process:402-414` + `HideGameLayer:839-849` — `GetChildren()` | Гейт по изменению числа детей (`GetChildCount() != _lastParentChildCount`), в устоявшемся кадре — O(1), без аллокаций. |

## 4. Покрытие категорий задания

1. **Аллокации в `_Process`/`_Input`/`_Draw`** — C-06 (Stopwatch в `_Process`), C-07 (аллокации в `_Input`/hover-обработчиках инструментов), C-09 (массовая сборка `Godot.Collections.Array` + двойной `exists`). Дополнительно: `SelectionBox._Draw` гейтится `IsSelecting`, `WorldMapOverlay._Draw` — O(1). Ненулевой, но не аллокационный путь: заливка полного `MultiMesh.Buffer` (16384 инстанса) в `GroundItemRenderer:125` при любом изменении — отмечено как известное ограничение API, не дефект.
2. **Подписки без отписки / утечки ресурсов** — C-01 (главный дефект), плюс C-08 (lambda-обработчики `MapRenderer.cs:372-408` не отписываются в `_ExitTree` — часть того же фикса). Остальное проверено: `DesignationRenderer`, `FarmZoneRenderer`, `HUDController`, `WorldJobTicker`, `PerformanceOverlay` снимают обработчики.
3. **Null-ref / порядок `_Ready` / протухший кэш** — C-02 (неверный путь к ноде сцены), C-03 (протухший флаг `_hasInitialSnapshot` после повторной инициализации), C-12 (мёртвая ветка `_pendingMapData`).
4. **Согласованность рендера и симуляции (фантомные тайлы, несовпадение координат)** — C-08 (очереди без поколения → клетки прошлого мира), C-03 (позиции агентов от (0,0) при смене мира). Проверено и отклонено: `ShadowRenderer.Visible`, запись `_pendingMapData`, `BtnClose`.
5. **Залипание инструментов в `src/ui/tools/**` (ghost, ПКМ/Esc)** — C-04 (`_isMouseDown` на две кнопки), C-05 (дверной режим + `_isRightDragging`), C-11 (ПКМ-превью vs ПКМ-удаление). Все три — конкретные сценарии отказа, не стилистика.
6. **Корректность профайлера (деление на ноль, рост лога, гонки)** — C-10 (несинхронизированная запись ring-слота из двух потоков). Деления и рост памяти проверены и чисты (§3).

## 5. Вне заявленного scope (передать владельцу `src/simulation`)

- `src/simulation/items/GroundItemManager.cs:659` — `SimEvents.Series("res.available." + ((ItemId)t).ToString(), …)` внутри цикла: конкатенация строки на каждый тип ресурса на каждый вызов (плюс `enum.ToString()`), а `SimEvents.Series` использует словарь со строковым ключом. Если этот метод вызывается чаще, чем раз в секунду, это постоянный GC-мусор в sim-потоке. Файл вне моего разрешённого scope (`src/ui`, `src/core/common`, `scenes/main/Main.cs`), поэтому не оформляю как C-finding — фиксирую как передачу.