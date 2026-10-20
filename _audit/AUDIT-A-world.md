# AUDIT-A — world streaming / storage / generation / climate

**Задача**: shared task `task-1` (AUDIT-A)
**Аудитор**: @destroyer (`audit-world`), режим READ-ONLY
**Проект**: `D:\bebebe-main (1)\bebebe-main` (Godot 4.7, .NET 8, C#)
**Дата**: 2026-10-18
**Охват (только чтение)**: `src/core/world/streaming/**`, `src/core/world/climate/**`,
`src/core/world/generation/UnifiedWorldGenerator.cs`,
`src/core/world/{HumidityMap,FertilityMap,BiomeMapper,MapData,GridHelper,WallTileHelper,MountainTileHelper}.cs`.
Файлы вне охвата (`src/ui/streaming/StreamingWorldView.cs`, `scenes/main/Main.cs`, `src/ui/tools/*`)
читались **только как доказательство достижимости** дефектов; в них ничего не менялось.

**Методика**
* `read` — построчное чтение всех файлов охвата; `grep`/`glob` — поиск символов и файлов;
* `DependencyGraphVisualizer.exe --cli --project ... --orphans` — ревизия мёртвого кода;
* `dotnet build Game.csproj` — проверка, что цитируемый код собирается: **0 ошибок, 0 предупреждений**
  (`Game -> .godot/mono/temp/bin/Debug/Game.dll`);
* сквозная проверка имён (461 файл, рекурсивный `Select-String` по `.cs/.gd/.tscn/.godot/.shader/.md/.csproj`)
  для всех утверждений чужого отчёта;
* ничего под `src/`, `scenes/`, `tests/` не создано и не изменено.

**Итог**: 9 находок (1×P0, 4×P1, 4×P2). Баг-реестр B1..B5 из `GLOBAL_AUDIT_REPORT.md`:
B1 — **НЕВЕРНО**, B2 — **НЕВЕРНО** (ссылка на несуществующий код), B3 — **УЖЕ ИСПРАВЛЕНО/НЕВЕРНО**,
B4 — **ЧАСТИЧНО ПОДТВЕРЖДЕНО** (механизм есть, порог и «проверка» вымышлены), B5 — **НЕВЕРНО**.

| ID | Severity | Confidence | Файл:строка | Суть |
|----|----------|------------|-------------|------|
| W-01 | **P0** | high (логика) / medium (срабатывание в view) | `WorldChunkStreamer.cs:389-392` | Утечка reservation + навсегда «залипший» чанк при сжатии окна без смены прямоугольника; лечится только `DisposeAsync`. При накоплении 256 утечек стриминг умирает полностью |
| W-02 | P1 | high | `WorldChunkStreamer.cs:430-441`, `539-558` | Любой нечитаемый/чужой delta-файл навсегда блокирует генерацию чанка (нет fallback на базовую генерацию) → постоянная дыра в мире |
| W-03 | P1 | high | `WorldChunkStore.cs:79-92` vs `WorldManifestStore.cs:46-56` | Запись delta-чанков не durable (нет `Flush(true)`/`WriteThrough`) при том, что манифест пишется durable → после сбоя питания «битый» файл → см. W-02 |
| W-04 | P1 | high (структурно) / medium (масштаб) | `UnifiedWorldGenerator.cs:167,197` vs `244-260` | Два независимых источника террейна для одной клетки: гейты/пасфайндинг/оверлеи семплируют климат в **искажённой** точке, рендер и чанк — интерполяцию по **неискажённой** сетке |
| W-05 | P1 | high | `UnifiedWorldGenerator.cs:29-31,148-149` + `Main.cs:245,287` | Изменяемое глобальное состояние `WorldOriginX/Y` читается воркерами генерации; streamer «замораживает» seed/version, но не origin → чанки одного окна из разных миров |
| W-06 | P2 | high | `UnifiedWorldGenerator.cs:145-146` | `key.X * ChunkSize` без `checked` при том, что `WorldCoordinates.TileOrigin` — `checked` → тихая циклическая подмена координат вместо `OverflowException` |
| W-07 | P2 | high | `WorldFeatures.cs:15-111`, `ChunkMutations.cs:11-130` | 241 строка мёртвого кода: геометрия River/Lake и chunk-mutation API не используются нигде (0 ссылок) |
| W-08 | P2 | **low** (достижимость не доказана) | `StreamingWorldView.cs:705-724` (`ClampVisibleToWindow`) | Страховочный цикл клампа при срабатывании даёт вырожденный прямоугольник → `ChunkWindowPlanner.Update` бросает `ArgumentException` в главном потоке |
| W-09 | P2 | medium | `FertilityMap.cs:66-75,224-226`, `HumidityMap.cs:63-69` | Гонка «тик со свапом буферов» ↔ точечные записи (`DrainAfterHarvest`, `Set`) → потеря обновления (сам автор признаёт в комментарии) |

---

## W-01 — Утечка reservation/pending-инварианта и полная остановка стриминга

**Severity**: P0 · **Confidence**: high (дефект логики доказан по коду), medium (частота срабатывания в эталонном `StreamingWorldView`; через публичный API воспроизводится детерминированно)

* `src/core/world/streaming/runtime/WorldChunkStreamer.cs:389-392`

```csharp
else if (_workQueue.TryDequeue(out key, out _))
{
    if (!_pending.Remove(key) || !_desired.Contains(key) || !_reserved.Contains(key))
        continue;
```

* `src/core/world/streaming/runtime/WorldChunkStreamer.cs:140-143`

```csharp
if (visibleBoundsChanged)
    RebuildPending_NoLock(visibleTileBounds);
else
    FillPending_NoLock();
```

* `src/core/world/streaming/runtime/WorldChunkStreamer.cs:689-711` (гейт по лимиту)

```csharp
for (int i = 0; i < _orderedDesiredBuffer.Count; i++)
{
    if (_reserved.Count >= MaxBufferedChunks)
        break;
```

* `src/core/world/streaming/runtime/WorldChunkStreamer.cs:715-722` — ключ с живым `_reserved` навсегда исключён из повторной постановки:

```csharp
private bool IsAlreadyTracked_NoLock(ChunkKey key)
    => _reserved.Contains(key)
        || _inFlight.Contains(key)
        || _readyKeys.Contains(key)
```

**Дефект.** Инвариант «ключ в `_workQueue` ⇒ ключ в `_pending`» поддерживается, но обратный переход не полный:
если ключ покинул `_desired`, а не покинул `_pending` (ветка `else` на строке 143 — пересборка `_pending`
**не выполняется**, потому что прямоугольник не изменился, а `_desired` изменился из-за смены стороны окна),
воркер, декодируя его из очереди, делает `continue`: `_pending.Remove` уже выполнен, `_workQueue` уже пуст,
но **`_reserved` не очищается, `_requestCancellation[key]` не освобождается**. После этого:
`IsAlreadyTracked_NoLock` → `true` (по `_reserved`) → ключ **никогда** не будет поставлен в работу снова.

Хуже: `CancelPending()` (строки 238-242) снимает `_reserved` только у ключей из `_pending` и `_inFlight` —
уже «выброшенный» воркером ключ там отсутствует, поэтому **`CancelPending` утечку не лечит**; очищает её
только `DisposeAsync` (строка 360, `_reserved.Clear()`).

Так как `SetWindowSide` документирован как «набор пересчитается на следующем Update»
(`ChunkWindowPlanner.cs:84-87`), а `StreamingWorldView.Update` вызывает
`ApplyAdaptiveWindow(...)` → `SetWindowSide(...)` непосредственно перед `_streamer.Update(ClampVisibleToWindow(...))`
(`StreamingWorldView.cs:237-238`), режим «сменилась сторона окна, прямоугольник не изменился» — документированный
способ использования `WorldChunkStreamer`: через публичный API он воспроизводится детерминированно (см. ниже),
в самом view требует совпадения клампнутого прямоугольника (оценка ниже).

**Сценарий отказа (детерминированный, через публичный API).**

```csharp
var planner = new ChunkWindowPlanner();                       // margin = 50, side = 16
var streamer = new WorldChunkStreamer(planner, seed, ver, store, maxConcurrency: 4);
var rect = new WorldRect(0, 0, 128, 128);                     // 128 клеток + 2*50 запас = 228

streamer.Update(rect);                                        // 16x16 = 256 желаемых ключей → _reserved = 256
planner.SetWindowSide(4);                                     // окно 4x4 (228 <= 4*64 = 256 — валидно)
streamer.Update(rect);                                        // ПРЯМОУГОЛЬНИК ТОТ ЖЕ → visibleBoundsChanged == false
```

1. `plan.Desired` сузился с 256 до 16 ключей, но `~240` ключей остались в `_pending`/`_workQueue`/`_reserved`.
2. Воркеры разбирают очередь и на каждый устаревший ключ выполняют `continue` → `_reserved` остаётся занят.
3. `_reserved.Count ≈ 240…256 ≥ MaxBufferedChunks (256, строка 29)` → `FillPending_NoLock()` на первом же шаге
   делает `break` → **новые чанки больше не ставятся в работу вообще** (сдвиг камеры в новую область ничего не грузит).
4. `CancelPending()` (вызывается при `ResetWorld`) утечку не снимает. Стриминг оживает только после
   `DisposeAsync()` — то есть, по сути, только при смене сида/перезапуске.

Дополнительное следствие: утёкшие ключи, если они снова войдут в окно, **никогда не сгенерируются** —
`IsAlreadyTracked_NoLock` вернёт `true`, чанк останется дырой. Плюс утекает по одному `CancellationTokenSource`
на ключ (категория «утечки»).

В эталонном `StreamingWorldView` для срабатывания нужно, чтобы `visibleBoundsChanged == false` (клампнутый
прямоугольник тот же), но `_desired` сузился, то есть сторона окна уменьшилась. Сторона выбирается по **входному**
прямоугольнику — `ApplyAdaptiveWindow` (`StreamingWorldView.cs:676-690`):
`side = clamp(ChunkSpan(MinX-50, MaxX+50), MinWindowChunks, maxAllowed)`, а кламп (`:705-724`) даёт
`[c - min(width, available(side))/2, c + min(width, available(side))/2]`, где `available = max(64, (side-1)*64-100)`.
Конструктивное совпадение: при `MinX ≡ 28 (mod 64)` ширина видимой области 64 клетки даёт `ChunkSpan = 3` → `side = 3`,
`available = max(64, 2*64-100) = 64`, `halfW = min(64,64)/2 = 32`; ширина 65 клеток даёт `ChunkSpan = 4` → `side = 4`,
`available = max(64, 3*64-100) = 92`, `halfW = min(65,92)/2 = 32`. Клампнутый прямоугольник в обоих случаях один и
тот же — `[c-32, c+32]`, поэтому при неизменном центре (симметричный зум) переход «65 → 64 клетки» даёт `Update`
с тем же прямоугольником и `side` 4 → 3: `_desired` сужается с 16 до 9 ключей, 7 из них «утекают» навсегда.
Это ~7 слотов за событие, поэтому полная остановка стриминга в view требует накопления ~37 таких совпадений
(в отличие от ~240 слотов за один вызов через публичный API выше); каждый утёкший ключ при этом становится
постоянной дырой. Численную частоту события в рантайме я **не измерял** (нужен прогон view) — это оценка, а не замер.

**Минимальный фикс (любой из двух, лучше оба).**
1. В `Update` убирать устаревшие pending-ключи независимо от смены прямоугольника: заменить `else FillPending_NoLock();`
   на `else RebuildPending_NoLock(_lastVisibleBounds);` (или добавить проход «`_pending` ∖ `_desired`» →
   `_reserved.Remove` + `CancelAndDisposeRequest_NoLock`).
2. В воркере не молча выбрасывать работу:
```csharp
if (!_pending.Remove(key)) continue;
if (!_desired.Contains(key) || !_reserved.Contains(key))
{
    _reserved.Remove(key);
    CancelAndDisposeRequest_NoLock(key);
    continue;
}
```

**Как проверить фикс.** Юнит-тест (в проекте запрещён — писать вне `tests/`): после двух `Update` из сценария выше
дождаться `PendingCount == 0`; затем `Update` с далёким прямоугольником и убедиться, что
`IsResident` для новых ключей становится `true` в пределах таймаута. До фикса — не становится никогда.

---

## W-02 — Нечитаемый delta-файл навсегда блокирует генерацию чанка

**Severity**: P1 · **Confidence**: high

* `src/core/world/streaming/runtime/WorldChunkStreamer.cs:430-441`

```csharp
ChunkSnapshot? snapshot = null;
if (_store.ContainsChunk(key.X, key.Y))
{
    if (!_store.TryLoadChunk(key.X, key.Y, out ChunkSnapshot loadedSnapshot, out string? error))
        throw new InvalidDataException(error ?? $"Не удалось загрузить delta чанка {key}.");

    if (loadedSnapshot.Metadata.Seed != _worldSeed
        || loadedSnapshot.Metadata.GeneratorVersion != _generatorVersion
        || loadedSnapshot.Metadata.FeatureSchemaVersion != UnifiedWorldGenerator.FeatureSchemaVersion)
        throw new InvalidDataException($"Delta чанка {key} относится к несовместимому seed/generator/feature schema.");
    snapshot = loadedSnapshot;
}
```

* `src/core/world/streaming/runtime/WorldChunkStreamer.cs:539-558` (фрагмент :549-553)

```csharp
else
{
    _failureKeys.Add(key);
    _failures.Enqueue(new ChunkStreamFailure(key, exception.ToString()));
}
```

* `WorldChunkStreamer.cs:182-184` (документация поведения) — «такой ключ автоматически не ретраится до RetryFailed
  или выхода/повторного входа в окно».

**Дефект.** Ошибка чтения/декодирования существующего delta-файла трактуется как **фатальная ошибка всего чанка**,
а не как «локальные метки испорчены — сгенерировать базу и отложить/переименовать битый файл». Базовая генерация
(которая вообще не зависит от файла) не выполняется, `_resident` не достигается, чанк не рисуется.
Второй путь того же класса — несовместимые метаданные (`:436-439`): после смены `generatorVersion` или
`FeatureSchemaVersion` **каждый** существующий delta-файл становится блокирующим, поэтому весь ранее застроенный
регион перестаёт грузиться, хотя базовая генерация нового мира полностью корректна.

**Сценарий отказа.** Игрок построил стены → файл `chunk_X_Y.bin` записан. Питание пропало между записью данных и
фиксацией метаданных (см. W-03) → файл существует, CRC/схема не сходятся. Клиент входит в окно:
`ContainsChunk == true` → `TryLoadChunk == false` (`WorldChunkStore.cs:147-154`, `WorldChunkCodec.cs:134-174`) →
`InvalidDataException` → `CompleteChunkFailure` → ключ в `_failed`. Выход из окна и повторный вход (`_failed.RemoveWhere`
в `Update`, строка 139) даёт ту же ошибку — файл-то остался. Итог: **постоянная пустая (непрокрашенная) область мира
и потерянные метки**, причём внешне это выглядит как «стриминг не грузит чанк», без краша.

**Минимальный фикс.** В `ProcessChunk` не бросать на непроходимом delta: сгенерировать базовый чанк,
вернуть `snapshot = null` и передать ошибку как предупреждение (например, отдельным полем результата);
битый файл переименовать в `*.corrupt` (однократно, с сохранением), чтобы он не блокировал загрузку повторно.
Для случая несовместимых метаданных — то же самое плюс архивация/удаление устаревшего файла (иначе смена
`generatorVersion`/schema «замораживает» все застроенные чанки разом).

**Как проверить.** Положить в каталог сохранения `chunk_0_0.bin` с валидной `WCHK`-магией и испорченным CRC,
вызвать `Update` на окно, содержащее `(0,0)`: до фикса `DrainReady` пуст, `DrainFailures` содержит ключ и
`IsResident((0,0)) == false` навсегда; после фикса чанк приходит в `DrainReady`, а файл переименован.

---

## W-03 — Delta-чанки пишутся не durable (в отличие от манифеста)

**Severity**: P1 · **Confidence**: high

* `src/core/world/streaming/storage/WorldChunkStore.cs:79-92`

```csharp
using (var stream = new FileStream(
    tempPath,
    FileMode.CreateNew,
    FileAccess.Write,
    FileShare.None,
    bufferSize: 64 * 1024,
    options: FileOptions.None))
{
    stream.Write(encoded, 0, encoded.Length);
}

// Temp находится рядом с target: Move с overwrite выполняет замену в рамках
// одного тома только после полной записи и flush; до этого старый файл цел.
File.Move(tempPath, targetPath, overwrite: true);
```

* `src/core/world/streaming/storage/WorldManifestStore.cs:46-56` — «правильный» сосед в том же модуле:

```csharp
using (var stream = new FileStream(
    tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
    bufferSize: 4096, options: FileOptions.WriteThrough))
{
    stream.Write(bytes, 0, bytes.Length);
    stream.Flush(flushToDisk: true);
}
File.Move(tempPath, fullPath, overwrite: true);
```

**Дефект.** Комментарий обещает «полную запись и flush» перед `Move`, но фактически:
`FileOptions.None` + отсутствие `Flush(flushToDisk: true)` означают, что данные остаются в кэше ОС;
`File.Move(overwrite)` (в .NET — `MoveFileEx` без `MOVEFILE_WRITE_THROUGH`) переименовывает файл,
не гарантируя порядок «данные → метаданные». После сбоя питания на месте чанка может оказаться валидное *имя*
с неполными данными. `WorldChunkStore` и `WorldManifestStore` — один модуль, одна задача, разная гарантия.

**Сценарий отказа.** Крах ОС/питания сразу после `UnloadChunkAsync` → файл существует, CRC не сходится →
при следующем входе в окно срабатывает W-02 → чанк не грузится, метки игрока потеряны.

**Минимальный фикс.** `options: FileOptions.WriteThrough` + `stream.Flush(flushToDisk: true)` перед `Move`
(либо, как минимум, документировать ослабленную гарантию и добавить «repair»-стратегию).

**Как проверить.** Сравнить поведение обоих store на тесте «жёсткое завершение процесса через
внешний kill между `Write` и `Move`» — сейчас достаточно `fsync`-инструмента (procmon/ETW), чтобы увидеть
отсутствие записи на диск у чанка и её наличие у манифеста.

---

## W-04 — Два источника истины для террейна одной клетки (гейты vs рендер/чанк)

**Severity**: P1 · **Confidence**: high (структурно: два разных семпла доказаны кодом); medium (числовой масштаб расхождения не измерялся)

* Чанк (то, что видит игрок и что читают jobs через `IWorldCellSource`) — климат интерполируется по **неискажённой** сетке узлов:
  `src/core/world/generation/UnifiedWorldGenerator.cs:166-167,197`

```csharp
long gwx = minWx + gx * SubSize;                                  // узел без domain warp
grid[row + gx] = WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, gwx, gwy);
...
cells[rowOffset + x] = SampleCell(worldSeed, generatorVersion, wx, wy, cellClimate);
```

* Точечный семпл (гейты размещения/пасфайндинг/оверлеи) — климат берётся в **искажённой** точке
  (`src/core/world/generation/UnifiedWorldGenerator.cs:246-260`, meso ±36 клеток + micro ±6 клеток = до ±42):

```csharp
// 1. Двухслойный высокоэффективный Domain Warping (Meso: 64 кл., Micro: 16 кл.)   // :246
ushort wMesoX = CoordinateHash.ValueNoiseQ16(worldSeed + 2001ul, generatorVersion, GenerationDomain.CoastWarpMeso, wx, wy, 64);      // :249
ushort wMesoY = CoordinateHash.ValueNoiseQ16(worldSeed + 2002ul, generatorVersion, GenerationDomain.CoastWarpMeso, wx + 31, wy, 64); // :250
long wx1 = wx + ((int)wMesoX - 32768) * 36 / 32768;                              // :251
...
long warpedWx = wx1 + ((int)wMicroX - 32768) * 6 / 32768;                        // :256
// Физический макро-климат планеты: семплируется со сглаживанием по искривленным координатам // :259
WorldClimateParams climate = overrideClimate ?? WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, warpedWx, warpedWy); // :260
```

Существенно, что смещение **не** постоянно: это шум с длиной волны 64 кл. (meso) и 16 кл. (micro),
поэтому точечная и интерполяционная поверхности расходятся «пятнами» вдоль берегов, а не равномерным сдвигом.

* Гейты идут именно точечным путём: `src/core/world/streaming/integration/WorldCellQuery.cs:44-45`

```csharp
private static GeneratedCell Sample(ulong seed, uint ver, long x, long y)
    => UnifiedWorldGenerator.SampleCell(seed, ver, UnifiedWorldGenerator.WorldOriginX + x, UnifiedWorldGenerator.WorldOriginY + y);
```

* `src/core/world/streaming/integration/StreamingWorldManager.cs:59-60` и `:127-135`:

```csharp
public bool CanPlace(long cellX, long cellY)
    => WorldCellQuery.TerrainAt(_seed, _generatorVersion, cellX, cellY) == BaseTerrainKind.Grass;
...
if (placementKind && !CanPlace(cellX, cellY))
    return false;
```

Террейн решается по `climate.ElevationQ16` (`UnifiedWorldGenerator.cs:388-420`), то есть расхождение семпла
климата напрямую переключает Water/Grass/Mountain.

**Дефект.** Для одной и той же клетки существуют две разные функции террейна: `f_chunk(x,y)` (интерполяция по
неискажённой сетке 16 клеток) и `f_point(x,y)` (значение в точке, смещённой до ~42 клеток domain-warp'ом).
Рендер, `ChunkWorldAccess` и `WorldGameRules.GetCell` (fast-path `TryCellInfo`, `WorldGameRules.cs:135-141`)
используют `f_chunk`; `StreamingWorldManager.CanPlace/IsBlocked/TryPlaceWall/TryPlaceBlock`,
`WorldPathFinder.cs:43`, `AgentMovementService.cs:396`, `HUDController.cs:413,459` и оверлеи влажности/плодородия —
`f_point`. Это ровно тот класс ошибки, от которого модуль защищается для порогов декора —
`src/core/world/streaming/integration/WorldDecor.cs:3-7`:
«Пороги декора мира (лес/камни), Q0.16. Один источник истины: по ним рисует стриминговый вид и по ним же
отвечает доступ к миру (§26.1) — иначе «вижу дерево, но игра говорит, что его нет»». Но защита есть только для
порогов декора, а не для самого террейна.

**Сценарий отказа.** Игрок ставит стену (`BuildTool.cs:169` / `ZoneDraftTool.cs:657` → `StreamingToolPlacement.TryPlaceWall`
→ `StreamingWorldManager.TryPlaceBlock` → `CanPlace` → `f_point`). На клетке у берега `f_point = Grass`, а
`f_chunk = Water` (рендер показывает воду) → **стена строится «в воде»**. Симметрично: на видимой траве
размещение запрещается («нельзя строить»), потому что искажённый семпл попал в океан. Патфайндинг
(`WorldPathFinder`/`AgentMovementService` по `f_point`) при этом прокладывает путь по воде, которую видит игрок.

**Минимальный фикс.** Один вход: либо гейты берут `IWorldCellSource.TryCellInfo` (resident-чанк) с фолбэком на
*ту же* интерполяционную сетку (например, `UnifiedWorldGenerator.SampleClimateInterpolatedCell(seed, ver, wx, wy)`
с узлами, выровненными по глобальной сетке 16), либо чанк заполняется точечным семплом. Заодно убрать `warpedWx/warpedWy`
из выбора климата (`:260`), оставив их только для локального рельефа.

**Как проверить.** В одном 64×64 чанке на побережье сравнить `cell.Terrain` из `GenerateChunk` и
`WorldCellQuery.TerrainAt` для всех 4096 клеток и посчитать число расхождений: ожидается >0 (до фикса) и 0 (после).

---

## W-05 — Изменяемый статический WorldOrigin, читаемый воркерами генерации

**Severity**: P1 · **Confidence**: high

* `src/core/world/generation/UnifiedWorldGenerator.cs:29-37`

```csharp
public static long WorldOriginX { get; set; } = 0;
public static long WorldOriginY { get; set; } = 0;
public static ulong ActiveWorldSeed { get; set; } = 0;

public static void SetWorldOrigin(long originX, long originY)
{
    WorldOriginX = originX;
    WorldOriginY = originY;
}
```

* Чтение в потоке-воркере (`WorldChunkStreamer.cs:451` → `GenerateChunk`) — `UnifiedWorldGenerator.cs:148-149`:

```csharp
long minWx = WorldOriginX + chunkOriginX;
long minWy = WorldOriginY + chunkOriginY;
```

* Достижимость: `scenes/main/Main.cs:245` меняет origin, а старый стример живёт до `Main.cs:287` — между ними
`await Task.Run(...)` генерации колонии (строки 249-256):

```csharp
UnifiedWorldGenerator.SetWorldOrigin(spawnRegion.X * WorldRegionCells, spawnRegion.Y * WorldRegionCells); // :245
var mapData = await Task.Run(() => UnifiedWorldGenerator.GenerateColonyWindow(...));                       // :249-256
...
_worldView?.ResetWorld(newSeed);                                                                          // :287
```

Автор уже фиксировал ровно эту гонку ранее: `PLAN.md:5531` — «Гонка `UnifiedWorldGenerator.GenerateColonyWindow`:
параллельные потоки читали статические `WorldOriginX/Y`. Старая асинхронная задача могла перетереть новый мир»
(исправление — захват в локальные переменные внутри `GenerateColonyWindow`).

**Дефект.** `WorldChunkStreamer` считает свою идентичность неизменяемой (`_worldSeed`, `_generatorVersion` —
`readonly`, проверяются в `UnloadChunkAsync:276-279`), но фактический результат `GenerateChunk` зависит ещё и от
глобального мутабельного `WorldOriginX/Y`, который streamer не контролирует. `GenerateChunk` задокументирован как
«потокобезопасен» (`:138`) — это неверно, пока origin мутабелен.

**Сценарий отказа.** Смена сида/региона во время активного стриминга: воркеры старого окна генерируют часть чанков
со старым origin, часть — с новым; на стыке получаются два разных мира в одном окне (скачок рельефа, «остров»
внутри нового мира), а сохранённые меткb привязываются к клеткам другого региона.

**Минимальный фикс.** Передавать origin (и seed/version) параметрами: `GenerateChunk(seed, ver, origin, key)` /
`SampleCell(seed, ver, origin, x, y)`, а `WorldChunkStreamer` пусть держит `readonly WorldCell origin` и проверяет
его в `UnloadChunkAsync` наравне с seed/version. Как минимум — захват origin в локальную переменную в `GenerateChunk`
и запрет `SetWorldOrigin` при живом стримере (порядок `ResetWorld` → `SetWorldOrigin` → `InitStreamingWorld`).

**Как проверить.** Тест: запустить `Update`+воркеры, вызвать `SetWorldOrigin` в середине, собрать `ChunkHash` чанков
одного окна — до фикса хеши не совпадут с эталоном для одного origin.

---

## W-06 — Непроверяемое умножение координат в GenerateChunk (расхождение с контрактом TileOrigin)

**Severity**: P2 · **Confidence**: high

* `src/core/world/generation/UnifiedWorldGenerator.cs:144-149`

```csharp
var cells = new GeneratedCell[WorldChunk.CellCount];
long chunkOriginX = key.X * (long)WorldCoordinates.ChunkSize;
long chunkOriginY = key.Y * (long)WorldCoordinates.ChunkSize;

long minWx = WorldOriginX + chunkOriginX;
```

* Контракт соседнего API — `src/core/world/streaming/generation/WorldCoordinates.cs:67-71`:

```csharp
/// Глобальный origin чанка. Переполнение за диапазон long явно бросает OverflowException.
public static WorldCell TileOrigin(ChunkKey key)
    => new(checked(key.X * ChunkSize), checked(key.Y * ChunkSize));
```

**Дефект.** `TileOrigin` честно бросает `OverflowException` на `|chunkX| ≥ 144115188075855872` (`long.MaxValue/64`),
а `GenerateChunk` на тех же ключах **молча** переполняется и генерирует чанк для произвольной другой точки мира
(плюс `WorldOriginX + chunkOriginX` — тоже без `checked`). `WorldCellQuery.Sample` (`WorldCellQuery.cs:45`)
добавляет ту же незащищённую арифметику. Планер пока держит ключи в безопасном диапазоне
(`ChunkWindowPlanner.cs:131-142`, `checked`), поэтому это латентный дефект публичного API, а не сегодняшний краш.

**Сценарий отказа.** Любой вызов `GenerateChunk` вне планера (тесты, инструменты, будущие менеджеры) с ключом
`long.MinValue`/`MaxValue` даёт чанк «из ниоткуда»: `TileOrigin` для того же ключа бросил бы исключение, а
`GenerateChunk` вернёт валидный объект с неправильными координатами и (при записи меток) испортит чужие клетки.

**Минимальный фикс.** `checked(key.X * (long)WorldCoordinates.ChunkSize)` (и `checked` на сложении с origin),
либо `WorldCoordinates.TileOrigin(key)` внутри `GenerateChunk`.

**Как проверить.** Вызвать `GenerateChunk(seed, ver, new ChunkKey(long.MaxValue, 0))`: до фикса — чанк без исключения,
`WorldCoordinates.TileOrigin(...)` для того же ключа — `OverflowException`.

---

## W-07 — Мёртвый код: «геометрия рек/озёр» и chunk-mutation API не участвуют в работе

**Severity**: P2 · **Confidence**: high

* `src/core/world/streaming/generation/WorldFeatures.cs:15,65,85` — 111 строк: `RiverFeature`, `LakeFeature`, `WorldChunkFeatures`
* `src/core/world/streaming/integration/ChunkMutations.cs:11` — 130 строк: `ChunkMutations` (в т.ч. `Encode/Apply/GetLocalIndex`)
* Фактические ссылки: `grep` по всему проекту даёт совпадения **только внутри самих файлов** + `PLAN.md`/`docs/`.
  Независимая проверка `DependencyGraphVisualizer.exe --cli --project ... --orphans` относит
  `src/core/world/streaming/generation/WorldFeatures.cs` к файлам без связей (0 входящих ссылок в коде и в сценах).
* Живая гидрология сделана иначе — шумом прямо в клетке: `UnifiedWorldGenerator.cs:345-364`
  (`GenerationDomain.LakeGeometry`/`RiverGeometry`), а живое кодирование delta — в `StreamingWorldView.cs:731-747`.

**Дефект.** Модуль содержит два неиспользуемых слоя. Практические последствия: (1) они создают ложную картину
архитектуры — `GLOBAL_AUDIT_REPORT.md` описывает «River/Lake geometry» и «River/Lake features» как работающий слой
с измеримой стоимостью (`:112`, `:140`) и перечисляет `tests/adversarial/…` (`:193`); (2) `ChunkMutations` —
единственная реализация инварианта «LocalIndex ∈ 0..4095 с
проверкой принадлежности чанку» (`ChunkMutations.cs:120-129`), но в бою работает не она, а инлайн-формула
`local.Y * 64 + local.X` в `StreamingWorldView.cs:269,287,302` (без проверки принадлежности чанку).

**Минимальный фикс.** Либо удалить оба файла (и тогда удалить упоминания из `PLAN.md`), либо подключить
`ChunkMutations` как реальный контейнер меток чанка и вынести туда проверку принадлежности клетки чанку.

**Как проверить.** `DependencyGraphVisualizer.exe --cli --project ... --orphans` и `grep -r "ChunkMutations" src`.

---

## W-08 — Кламп видимого окна может вернуть вырожденный прямоугольник → исключение в главном потоке

**Severity**: P2 · **Confidence**: **low** (дефект по построению; достижимого входа я не построил — помечаю честно)

* `src/ui/streaming/StreamingWorldView.cs:705-724` (вне охвата, приведено как место дефекта; сам дефект — следствие
  контракта `ChunkWindowPlanner`). Комментарий метода прямо формулирует намерение, которое цикл нарушает (`:700-704`):

```csharp
/// Планировщик требует, чтобы видимая область с запасом помещалась в целое число выровненных
/// чанков. Если она больше максимального окна — сужаем прямоугольник вокруг центра (дальние
/// края дозаполнятся при движении камеры), а не падаем исключением из планировщика.
private WorldRect ClampVisibleToWindow(WorldRect visible)
{
	long halfW = Math.Min(width, available) / 2;                                                    // :714
	long halfH = Math.Min(height, available) / 2;                                                   // :715
	// Страховка от выравнивания: прямоугольник с запасом должен попадать в side чанков.         // :716
	while (halfW > 1                                                                                // :717
		&& ChunkSpan(centerX - halfW - _planner.MarginTiles, centerX + halfW + _planner.MarginTiles) > side)
		halfW -= WorldCoordinates.ChunkSize / 2;                                                    // :719
	return new WorldRect(centerX - halfW, centerY - halfH, centerX + halfW, centerY + halfH);       // :723
}
```

* Реакция планера — `src/core/world/streaming/runtime/ChunkWindowPlanner.cs:196-200`:

```csharp
private static void ValidateVisibleBounds(WorldRect bounds)
{
    if (bounds.MinX >= bounds.MaxX || bounds.MinY >= bounds.MaxY)
        throw new ArgumentException("Видимая область должна быть непустым прямоугольником с Max exclusive.", nameof(bounds));
}
```

**Дефект.** Условие цикла `halfW > 1` с шагом `-32` означает, что из любого `halfW ∈ (1, 33]` цикл выходит
на `halfW = 0` (или отрицательном, если старт < 32: например 30 → −2). Итоговый прямоугольник
`new WorldRect(centerX - halfW, ..., centerX + halfW, ...)` при `halfW ≤ 0` вырожден/инвертирован, и
`WorldChunkStreamer.Update` → `ChunkWindowPlanner.Update` бросает `ArgumentException`, которое в
`StreamingWorldView.Update` не перехвачено (вызов из игрового цикла). То есть «страховка» при срабатывании
превращает кламп в исключение вместо коррекции.

Я **не смог** построить достижимый вход для эталонной цепочки вызовов (при `side`, вычисленном из того же
прямоугольника, интервал `[center ± halfW]` является подынтервалом исходного, поэтому `ChunkSpan ≤ side`),
поэтому это находка «по построению», а не воспроизведённый баг.

**Минимальный фикс.** Делать `halfW = Math.Max(1, halfW - 32)` либо пересчитывать `halfW` от `available`,
и защитить `Update` в view (`try/catch` → лог + пропуск кадра).

**Как проверить.** Юнит-тест `ClampVisibleToWindow` (сделать метод internal/тестируемым) с прямоугольником,
для которого `ChunkSpan > side` при `halfW ∈ (1,33]`; до фикса — `ArgumentException` из планера.

---

## W-09 — Гонка точечных записей с тиком буферов влажности/плодородия

**Severity**: P2 · **Confidence**: medium (гонка реальна, потеря — редкая; сам автор её признаёт)

* `src/core/world/FertilityMap.cs:62-75`

```csharp
/// RACE: тик раз в час игрового может съесть одно истощение
/// (swap буферов) — приемлемо, следующий урожай доберёт. Без lock.
/// Зовётся из CropGrowthManager.HarvestCrop (параллельный Commit).
public void DrainAfterHarvest(int x, int y)
{
    ...
    int v = (int)(_cur[i] * 0.8f);
    _cur[i] = v;
}
```

* тот же файл, `:224-226`:

```csharp
// Swap буферов.
_cur = nxt;
_nxt = cur;
```

* `src/core/world/HumidityMap.cs:63-69` — `Set` пишет в `_cur` без синхронизации с `Tick`.

**Дефект.** `DrainAfterHarvest`/`Set` берут ссылку на текущее поле `_cur` и пишут в массив, который в этот момент
может быть целевым буфером `nxt` у параллельного `Tick` (после свапа) — запись либо теряется (немедленно
перезаписывается тиком), либо портит состояние незавершённого тика. Инвариант «`_base` — якорь, `_cur` —
рабочий буфер» не защищён.

**Сценарий отказа.** Сбор урожая в момент тика: одно истощение плодородия молча пропадает (до следующего сбора).
Накопительно — «плодородие не падает»/«влажность не меняется» в редких кадрах, что трудно объяснить игроку.

**Минимальный фикс.** Либо `lock`/`Interlocked` вокруг точечных операций и свапа, либо очередь отложенных
точечных правок, применяемых в начале следующего тика (в main thread).

**Как проверить.** Стресс-тест: N потоков вызывают `DrainAfterHarvest` на разных клетках, параллельно гонять `Tick`
и сверять сумму ожидаемых вычетов с фактическим изменением.

---

## Проверено — дефектов не найдено (и почему)

Ниже — то, что я целенаправленно искал и **не** подтвердил. Это важно, потому что часть этих пунктов
сформулирована как баги в `GLOBAL_AUDIT_REPORT.md`.

1. **DrainReady не возвращает устаревшие результаты (операции «stale» закрыты).**
   `WorldChunkStreamer.cs:165-178`: результат проверяется по актуальному `_desired` **на каждом извлечении**,
   а `DropStaleReady_NoLock` дополнительно вызывается из `Update` (:137), `CancelPending` (:252) и при `DisposeAsync`
   (:346-355). Устаревший результат не может попасть в caller (см. вердикт по B1).
2. **Reservation освобождается в остальных (не-W-01) путях.** `CompleteChunk` (:493-511), `CompleteDiscardedChunk`
   (:513-524), `CompleteCanceledChunk` (:526-537), `CompleteChunkFailure` (:539-558), `CompleteUnload` (:560-581),
   `DisposeAsync` (:325-366) — все снимают `_reserved`; `CancellationTokenSource` освобождается через
   `CancelAndDisposeRequest_NoLock`/`DisposeRequest_NoLock` (:593-605). Реальной утечки CTS я нашёл только одну —
   в W-01.
3. **PulseWorkers без потери пробуждений.** `_workSignal` подменяется под `_gate`, старый TCS завершается (:741-746);
   воркер сначала проверяет очереди и лишь затем ждёт (:377-407). Потери wakeup не видно; `CompleteUnload` всегда
   пульсирует (:572), поэтому сценарий «dispose ждёт `_activeUnloadCount == 0`, а пульса не будет» не подтверждается.
4. **Dispose-путь без взаимной блокировки.** `DisposeAsync` не берёт внешних блокировок, ждёт `Task.WhenAll(_workers)`
   после снятия `_gate` (`:362-364`), и не ждёт незавершённых `UnloadChunkAsync` бесконечно (они дорабатываются
   воркерами). Deadlock не найден.
5. **Стор — аккуратный.** `WorldChunkStore.TryLoadChunk:149-154` сверяет координаты из header с запросом (это то,
   что закрывает подмену/перемещение файла), `WorldChunkCodec.TryDecode:103-174` проверяет размер, магию, версию
   формата, конечность чекпоинта, длину распакованного/сжатого payload, совпадение длины файла, полное завершение
   Brotli-потока, отсутствие лишних байтов и CRC32. Пустой payload не кодируется (`:44-45`) и корректно трактуется
   как удаление (`WorldChunkStore.cs:66-70`) — см. B3. `WorldChunkDeltaCodec.TryDecode:122-198` проверяет версию
   схемы, count, диапазон LocalIndex (с защитой от переполнения, `:166-168`), длины, порядок записей и trailing bytes.
6. **Швов генерации между чанками нет (доказано логикой).** Узлы климата в `GenerateChunk` стоят на глобальных
   координатах `minWx + gx*16`, где `minWx = origin + key.X*64` — кратна 16 (`UnifiedWorldGenerator.cs:145-169`),
   а веса билинейной интерполяции зависят только от глобального `wx ∈ [minWx, minWx+64)` (`:178-197`).
   Значит, соседние чанки интерполируют **одну и ту же** глобальную функцию → разрыва на границе чанка нет.
   `GenerateColonyWindow` использует ту же сетку (`:510`) и совпадает с ней, поскольку все вызовы передают
   `origin = 0`, а `WorldOrigin` кратен 512 (`Main.cs:245,306`, `MapRenderer.cs:456`). **Замечание**: совпадение
   держится только на кратности origin 16 — это не проверяется кодом; вызов `GenerateColonyWindow` с
   `origin % 16 != 0` даст шов (не оформляю находкой: текущие вызовы корректны).
7. **Детерминизм «seed + version ⇒ тот же чанк» не нарушен.** Генерация не имеет RNG и состояния;
   `WorldClimateGenerator` кэширует регион в `[ThreadStatic]` direct-mapped кэше, но проверяет
   `Seed/Version/Rx/Ry` (`:89-95`), поэтому кэш не влияет на результат (и не шарится между потоками).
   `RegionTraitProvider.FromCoords` (`RegionTraits.cs:60-64`) детерминирован.
8. **Лимиты delta/чанка не переполняются в текущем пайплайне.** `LocalIndex = local.Y*64 + local.X ∈ [0,4095]`
   (`StreamingWorldView.cs:269,287,302`; `ChunkMutations.cs:127-128`) при `MaxLocalIndex = 4095`
   (`WorldChunkDeltaCodec.cs:37`) — полное покрытие чанка 64×64; `MaxRecordCount = 65536` допускает 16 записей
   на клетку (`:38`). То есть лимит `LocalIndex` недостижим для клеточных меток (см. B5).
9. **Верхний предел `MaxPayloadBytes` соблюдён на всех входах.** `UnloadChunkAsync:273-275` отвергает payload
   больше лимита, `WorldChunkCodec.Encode:46-47` — тоже, `MaxStoredFileBytes` в сторе (`:14`) согласован с кодеком,
   `FillPending_NoLock` ограничивает число reservation (`MaxBufferedChunks = 256`, `:29`) — поэтому OOM по размеру
   payload в рамках API не видно (проблема ровно в W-01, где этот же гейт блокирует работу).

## Что проверить не удалось (явно)

* **Численный масштаб расхождения W-04** — нужен запуск генерации (правка/создание тестовых `.cs` запрещены
  условием задачи, а `%TEMP%`-харнесс условие тоже нарушает: разрешён ровно один создаваемый файл).
* **Реальные тайминги генерации (заявленные в чужом отчёте 28.8 мс/чанк)** — артефактов для замера нет (см. ниже),
  `WorldChunkGeneratorTiming.Record` вызывается (`UnifiedWorldGenerator.cs:202`), но ни один потребитель статистики
  в проекте не обнаружен (`TryGetLast`/`Snapshot` используются только внутри файла).
* **W-08** — воспроизводимый вход не построен (помечено low).
* **Поведение после реального сбоя питания (W-03/W-02)** — проверяется только внешним `fsync`/procmon-тестом.
* **Клиентские UI-вызовы `StreamingWorldView.Update`** (`WorldJobTicker`/`InfiniteWorldGame`) читались частично:
  дефект W-01 доказан на уровне стримера, а частота срабатывания в эталонном view оценена аналитически, не измерением.

---

## Разбор баг-реестра B1..B5 из `GLOBAL_AUDIT_REPORT.md`

### B1 — «Обновление/дренаж race condition» — **НЕВЕРНО**

Заявление отчёта: «`DropStaleReady_NoLock()` вызывается только внутри `Update()`. Если клиент вызывает
`DrainReady()` напрямую без промежуточного `Update()`, старые результаты … остаются в очереди»,
и предлагаемый фикс — проверка `if (!_desired.Contains(result.Key)) { _reserved.Remove(...); continue; }`.

Текущий код (`src/core/world/streaming/runtime/WorldChunkStreamer.cs:165-178`) **уже содержит ровно эту проверку**:

```csharp
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
```

Кроме того, посылка «только внутри `Update()`» неверна: `DropStaleReady_NoLock()` вызывается ещё из
`CancelPending` (`:252`) и из `DisposeAsync` (`:346-350`). Описанная в B1 последовательность
(`DrainReady` дважды без `Update`) **не может** вернуть чанк из неактивного окна: он будет отброшен на строке 169.
**Вердикт: НЕВЕРНО** (заявленного дефекта в текущем коде нет; предлагаемый «фикс» уже реализован).
Реальный дефект в этом же месте другой — инвариант pending/reservation на сжатии окна (W-01).

### B2 — «Ocean ring discontinuity [512→513]», `RingWaterBoost`, `WorldChunkGenerator.cs:155-162` — **НЕВЕРНО (ссылки на несуществующий код)**

* `RingWaterBoost` — **0 вхождений** во всём проекте (рекурсивный поиск по 461 файлу, включая `.cs/.gd/.tscn/.godot/.md`).
* Файла `src/core/world/streaming/generation/WorldChunkGenerator.cs` **не существует** (glob по всему дереву; имя
  встречается только как историческое упоминание — `PLAN.md:1532,1643,2105` и `UnifiedWorldGenerator.cs:11`
  «Заменяет собой устаревшие разрозненные генераторы (MapGenerator, WorldChunkGenerator, GpuChunkGenerator…)»).
* В текущем генераторе нет понятия «кольцо океана вокруг 512»: мир — планетарная модель 2048×1024 региона
  (`WorldRegions.cs:9-10`), широтные пояса (`LatitudeBands.cs`), континенты `ContinentShaper`, а граница 512 —
  это кромка региона/колонии, а не порог `RingWaterBoost`. Числа «~62000» и «0» в отчёте не соответствуют ни
  одной константе кода (SeaLevelQ16 = 22000, `WorldClimateGenerator.cs:19`).

**Вердикт: НЕВЕРНО** — утверждение построено на вымышленном символе и вымышленном файле. Настоящие «швы» в коде
я проверял отдельно: между чанками их нет (см. пункт 6 раздела «Проверено»); у кромки колонии совпадение
обеспечивается выравниванием origin (там же).

### B3 — «Empty-delta unload ambiguity» — **УЖЕ ИСПРАВЛЕНО / НЕВЕРНО**

Заявление: «Пустой payload передаётся в `SaveChunk` как “удалить”. Но `WorldChunkCodec.Encode` бросает
`ArgumentException` для пустого payload».

Формально `Encode` действительно бросает (`WorldChunkCodec.cs:44-45`):

```csharp
if (payload.IsEmpty)
    throw new ArgumentException("Пустой delta не кодируется в файл.", nameof(payload));
```

Но до `Encode` пустой payload не доходит: `WorldChunkStore.SaveChunk` обрабатывает его первым и явно
**не пишет файл**, а атомарно удаляет прежний (`WorldChunkStore.cs:66-70`), что задокументировано в контракте
(`:52-56`): «Пустой payload удаляет устаревший delta атомарным переименованием в tombstone и возвращает false
(файл чанка отсутствует)». Streamer делает то же самое в `ProcessUnload` (`WorldChunkStreamer.cs:473-474`:
«SaveChunk(empty) атомарно удаляет прежнюю delta запись и не создаёт файл»). Со стороны кодека пустой delta тоже
легален: `WorldChunkDeltaCodec.Encode` для нуля записей возвращает `Array.Empty<byte>()` (`:56-57`).

**Вердикт: НЕВЕРНО для текущего кода** (механизм «пропускать пустые дельты без записи файла» уже реализован и
задокументирован). Замечание на будущее: семантика «пустой payload ⇒ удалить файл» опасна там, где `_blocks`
мог оказаться пустым при непустом файле (например, после неудачного `TryDecode` в
`StreamingWorldView.ApplyReady:565-568`); это единственный смежный риск, но он требует уже испорченного payload.

### B4 — «TileOrigin overflow при |X| > 35M» — **ЧАСТИЧНО ПОДТВЕРЖДЕНО** (механизм есть; порог, тест и «митигация» — вымышлены)

Что верно: `TileOrigin` действительно защищён и бросает исключение (`WorldCoordinates.cs:67-71`):

```csharp
/// Глобальный origin чанка. Переполнение за диапазон long явно бросает OverflowException.
public static WorldCell TileOrigin(ChunkKey key)
    => new(checked(key.X * ChunkSize), checked(key.Y * ChunkSize));
```

Что неверно:
1. **Порог.** «|X| > 35M» неверен на ~12 порядков: переполнение наступает при `|chunkX| > long.MaxValue / 64`
   = `144115188075855871` (то есть при координате **клетки** `|x| > 9.22e18`). `35_000_000 * 64 = 2.24e9` —
   до предела `long` не доходит.
2. **«Проверено в `ExtremeCoordinateTests`»** — такого класса/файла в проекте нет: 0 вхождений имени
   (рекурсивный поиск по 461 файлу), в `tests/` нет ни одного файла `*Tests*.cs`, кроме `TestShadowAndTerrainAudit.cs`.
3. **«Уже реализовано в `InfiniteWorldGame.ClampCameraToWorld`»** — метода `ClampCameraToWorld` в проекте нет
   (0 вхождений). Реальный кламп — `CameraController.ClampPosition` (`src/ui/CameraController.cs:142-180`),
   включаемый через `SetWorldBoundsTiles` (`Main.cs:226`); `InfiniteWorldGame.cs` (файл есть) такого метода не содержит.
4. **Реальный дефект рядом** (отчёт его пропустил): та же координатная арифметика в генераторе
   **не** проверяется — `UnifiedWorldGenerator.cs:145-146` (см. W-06).

**Вердикт: ЧАСТИЧНО ПОДТВЕРЖДЕНО** — само ограничение существует и осознанно, но все конкретные атрибуты
утверждения (порог, тест, митигация) не соответствуют коду.

### B5 — «LocalIndex > 4095 может ударить при плотной застройке» — **НЕВЕРНО**

Лимит существует (`WorldChunkDeltaCodec.cs:37,67-68`):

```csharp
public const int MaxLocalIndex = 4095;
...
if (record.LocalIndex > MaxLocalIndex)
    throw new ArgumentOutOfRangeException(nameof(delta), $"LocalIndex {record.LocalIndex} вне диапазона 0..{MaxLocalIndex}.");
```

Но `LocalIndex` — это **индекс клетки внутри чанка 64×64**, а не счётчик объектов: живая формула
`(ushort)(local.Y * WorldCoordinates.ChunkSize + local.X)` (`StreamingWorldView.cs:269,287,302`;
то же в `ChunkMutations.cs:127-128`) даёт диапазон `0..4095` = ровно 4096 клеток чанка. «Заполнить всё поле
стенами» = индексы `0..4095` включительно, то есть **не** превышает лимит. Для нескольких видов на одной клетке
служит `Kind` (ключ `(LocalIndex, Kind)`), а `MaxRecordCount = 65536` (`:38`) допускает до 16 записей на клетку.
Дополнительно: `ChunkMutations.Apply` (`:101-103`) валидирует индекс до записи, а `Encode` — до кодирования.

**Вердикт: НЕВЕРНО** — заявленный сценарий («плотная застройка ⇒ encode throws») недостижим через интеграцию;
лимит 4095 является полным покрытием чанка, а не узким местом.

---

## Отдельно: недостоверные утверждения `GLOBAL_AUDIT_REPORT.md`, не входящие в B1..B5

Проверено рекурсивным поиском по 461 файлу проекта — **0 вхождений** каждого имени:

| Утверждение отчёта | Факт |
|---|---|
| §1 таблица тестов: `WorldGenerationTests`, `ExtremeCoordinateTests`, `StreamerChurnTests`, `WorldChunkCodecTests`, `CorruptChunkTests`, `DeltaCodecStressTests` — «✅ PASS» | Ни одного из этих классов/файлов в проекте нет. В `tests/` нет файлов `*Tests*.cs` (кроме `TestShadowAndTerrainAudit.cs`), нет каталогов `adversarial/`, `benchmarks/`, нет `OfflineCatchUpTests.cs` |
| §5 `tests/adversarial/OceanRingDiscontinuityTests.cs` | не существует (нет каталога `tests/adversarial`) |
| §5 `tests/benchmarks/WorldGenBenchmark.cs` | не существует (нет каталога `tests/benchmarks`) |
| §5 `shaders/chunk_value_noise_compute.compute` | не существует; в проекте вообще **нет ни одного `.compute`** файла |
| §5 `src/core/world/streaming/generation/WorldChunkGeneratorTiming.cs` | **существует** (подтверждено) |
| §5 `PLAN_GLOBAL_AUDIT_GPU.md` | **существует** в `D:\bebebe-main (1)\` |
| B2 `WorldChunkGenerator.cs`, `RingWaterBoost`; B4 `InfiniteWorldGame.ClampCameraToWorld` | не существуют (0 вхождений) |

Следствие: числа отчёта («~28.8 мс/чанк», «256 чанков = 7.4 сек», «4–7× GPU») **не имеют проверяемого источника** —
ни одного артефакта бенчмарка/тайминга с потребителем в проекте нет. Рекомендую не использовать §1, §3, §5 и
баг-реестр этого отчёта как основание для решений без повторного замера.

---

## Приоритеты для @developer (минимальные правки, максимальный эффект)

1. **W-01** — снять инвариантную утечку (`RebuildPending` в ветке без смены прямоугольника **и** освобождение
   `_reserved`/CTS в воркере). Через публичный API (SetWindowSide + тот же прямоугольник) это даёт детерминированную
   и **необратимую** остановку стриминга (~240 утёкших слотов за один вызов, оживает только после `DisposeAsync`);
   в эталонном view каждый редкий случай-совпадение даёт ~7 постоянных утечек, то есть накопление (оценка).
2. **W-02 + W-03** — durable-запись delta (паритет с `WorldManifestStore`) и fallback «битый delta → базовая
   генерация + переименование файла». Оба вместе закрывают потерю меток игрока и «дыры» мира.
3. **W-04** — свести террейн к одному входу (иначе стены в воде / запрет стройки на видимой земле).
4. **W-05** — перестать читать мутабельный `WorldOriginX/Y` из воркеров.
5. W-06..W-09 — гигиена: `checked`-арифметика, удаление мёртвого кода, защита клампа, синхронизация тиков.