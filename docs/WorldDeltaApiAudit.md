# Аудит API мирового (стримингового) слоя: дельты, контракты, персистентность

Дата: 2026-10-19. Только чтение кода; ссылки `файл:строка` — точные на момент аудита.
Область: бесконечный стриминговый мир (чанки 64×64, `WorldDeltaKind`, `IBlockWorld`,
`StreamingWorld*`, `UnifiedWorldGenerator`, стор). Островная сторона (`MapData` + легаси-менеджеры)
не дублируется — она разобрана в `docs/MapDataWriterAudit.md`; здесь на неё только ссылки.

---

## 1. `WorldDeltaKind` — виды дельт

Файл: `src/core/world/streaming/integration/WorldDeltaKind.cs` (статический класс, `:4`).
Запись дельты = «присутствие метки kind в клетке (LocalIndex)»; payload-байты `Data`
сейчас всегда пустые (см. §6.8).

| Kind | Значение | Строка | Класс | Что означает | Блокирует клетку? | Placement? |
|---|---|---|---|---|---|---|
| `Wall` | `0x77` | `:10` | готовое | постоянная стена; Data = 1 байт (по доку `:6-9`, фактически пишется пусто) | да | да |
| `Building` | `0x01` | `:13` | готовое | здание/мебель, Data opaque до появления DTO | да | да |
| `MinedStone` | `0x02` | `:16` | готовое | добытая каменная россыпь (снимает камень с рендера/гейтов) | нет | нет |
| `CutTree` | `0x03` | `:19` | готовое | срубленное дерево (снимает дерево) | нет | нет |
| `Stockpile` | `0x04` | `:22` | готовое | складская клетка | да | да |
| `Crop` | `0x05` | `:25` | готовое | посев/грядка | да | да |
| `WallPlan` | `0x11` | `:32` | план | чертёж стены (§29) | да | да |
| `BuildingPlan` | `0x12` | `:35` | план | чертёж стройки | да | да |
| `CropPlan` | `0x13` | `:38` | план | чертёж грядки | да | да |
| `StockpilePlan` | `0x14` | `:41` | план | чертёж склада | да | да |
| `CutTreePlan` | `0x15` | `:44` | план | задача «срубить дерево» | **нет** | да |
| `MinedStonePlan` | `0x16` | `:47` | план | задача «добыть камень» | **нет** | да |

Вспомогательные предикаты/таблицы (тот же файл):

* `IsPlan(kind)` — `:50-52` (шесть планов `0x11..0x16`).
* `DoneFor(planKind)` — `:55-64` (WallPlan→Wall … MinedStonePlan→MinedStone; не-план возвращает себя).
* `PlanFor(doneKind)` — `:67-76` (обратное соответствие).
* `IsPlacementKind(kind)` — `:79-80`: `Wall, Building, Stockpile, Crop` + все планы. «Размещаемые»
  виды требуют пригодной поверхности (трава) в гейтах `StreamingWorldManager.TryPlaceBlock`
  (`StreamingWorldManager.cs:134-136`) и `TryReplaceBlock` (`:169-171`). ВАЖНО: `CutTree`/`MinedStone`
  (готовые) — НЕ placement, а `CutTreePlan`/`MinedStonePlan` (планы) — placement (асимметрия:
  «добычные» готовые метки террейн-гейт не проходят, а их планы проходят — см. §6.9).
* `BlockingKinds` — `:87-91`, единый массив `{Wall, Building, Stockpile, Crop, WallPlan, BuildingPlan,
  StockpilePlan, CropPlan}`. Комментарий `:82-86`: список раньше дублировался в менеджере и правилах,
  и в обоих забыли `Crop/CropPlan`. `CutTree*`/`MinedStone*` намеренно НЕ блокируют — на одной клетке
  могут сосуществовать `CutTreePlan` и `WallPlan` (`WorldJobTicker.cs:59-62`, `WorldJobBoard.cs:19-21`).
* `IsPermanent(kind)` — `:94-102`, обёртка над `BlockingKinds` (готовые объекты и их чертежи).

Персистентность планов «бесплатна» (комментарий `WorldDeltaKind.cs:27-29`): «план без готового» =
незакрытая задача, «готовое» = выполнено; `WorldJobBoard` строит очередь именно из этого
(`StreamingWorldView.cs:362-366`).

---

## 2. Контракты мирового слоя и реализации

### 2.1 `IWallWorld` — `src/core/world/streaming/integration/IWallWorld.cs:10`

| Операция | Строка | Семантика |
|---|---|---|
| `IsLoaded(ChunkKey)` | `:13` | чанк загружен (данные + спрайт готовы) |
| `AddWall(x,y)` | `:16` | false, если чанк не загружен или стена уже стоит |
| `RemoveWall(x,y)` | `:19` | false, если стены нет |
| `HasWall(x,y)` | `:22` | по загруженным данным |
| `WallCount` | `:25` | всего постоянных стен в загруженной области |

### 2.2 `IBlockWorld : IWallWorld` — `src/core/world/streaming/integration/IBlockWorld.cs:10`

| Операция | Строка | Семантика |
|---|---|---|
| `AddBlock(kind,x,y)` | `:13` | добавить метку в загруженную клетку; false — чанк не загружен или метка уже есть |
| `RemoveBlock(kind,x,y)` | `:16` | снять метку; false — метки нет или чанк не загружен |
| `HasBlock(kind,x,y)` | `:19` | только для загруженных чанков |
| `BlockCount(kind)` | `:22` | число загруженных в память меток вида |

### 2.3 `IWorldCellSource : IBlockWorld` — `src/core/world/streaming/integration/IWorldCellSource.cs:8`

`CoversChunk(x,y)` `:11` и `TryCellInfo(x,y,out GeneratedCell)` `:14` — исходные (генераторные) поля
клетки из resident-чанка. Назначение — «единый доступ к миру» (§26.1) без зависимости ядра от узла Godot.

### 2.4 `IWorldAccess` + `ChunkWorldAccess` — `src/core/world/access/IWorldAccess.cs:17`, `src/core/world/access/ChunkWorldAccess.cs:13`

`IWorldAccess` — над-контракт «остров + мир» в глобальных координатах: `Covers` `:23`, `TerrainAt`
`:26`, `IsWaterAt` `:29`, `HasTreeAt` `:32` (с учётом `CutTree`), `HasStoneAt` `:35` (с учётом
`MinedStone`), `HasBlock/AddBlock/RemoveBlock` `:38-44`. Реализация `ChunkWorldAccess` (`:13-66`)
делегирует `IWorldCellSource`: запись в выгруженный чанк → `false` (`:54-58`), а **чтение** поверхности
бросает `InvalidOperationException` («чанк клетки не загружен», `:60-65`), чтобы пустая клетка не
выглядела травой молча. `HasTreeAt`/`HasStoneAt` учитывают вырубку/добычу (`:35-49`), но НЕ
прореживание декора `IslandDecorPass` (см. §7.6).

### 2.5 Реализация: `StreamingWorldView` — `src/ui/streaming/StreamingWorldView.cs:21`

`Node2D, IWallWorld, IBlockWorld, IWorldCellSource` — единственная боевая реализация. Ключевое:

* Хранение меток: `Dictionary<ChunkKey, Dictionary<byte, HashSet<ushort>>> _blocks` (`:77`) —
  чанк → kind → множество локальных индексов; чанки `Dictionary<ChunkKey, WorldChunk> _chunkData` (`:74`).
* `AddBlock` (`:277-292`): чанк обязан быть в `_chunkData` (`:282-283`); дубль отклоняется
  идемпотентно (`:286-287`); инкремент `WallCount` только для `WallKind` (`:288-289`); `RepaintCell`
  (`:290` → `:1735-1755`) рисует/стирает тайлы сразу. Слой чертежей рисуется полупрозрачно
  (`PlanAlpha 0.55`, `:1142`, слои `_planLayer/_wallPlanLayer/_farmPlanLayer`, `:59-71`, `:1244-1247`).
* `RemoveBlock` (`:310-332`): симметрично; `WallCount` зажат снизу нулём (`:324-326`).
* `HasBlock` (`:297-305`) — читает `_blocks` без проверки резидентности чанка (практически безопасно:
  выгрузка удаляет и чанк, и метки вместе, `RemoveChunkTiles :683-686`).
* `IsLoaded` (`:465`) = `_chunkData.ContainsKey`; `CoversChunk` (`:468-469`); `TryCellInfo`
  (`:472-483`) — O(1) из массива чанка.
* `BlockCount(kind)` (`:453-462`) — сумма по загруженным чанкам (только счётчик, не перечисление).
* `CollectPendingPlans(into, focusX, focusY, max)` (`:341-378`) — ЕДИНСТВЕННОЕ перечисление дельт:
  только планы (`PlanKinds :380-388`), только загруженные чанки (`:355-356`), «план без готового»
  (`:362-366`), max-heap ближайших к фокусу, O(N log max) (`:350-352`).
* Диагностика: `IsChunkStored` (`:486`, файл дельты существует на диске), `ChunkHash` (`:489-508`,
  FNV-1a по клеткам чанка), `TerrainSummary` (`:517-537`), `ToString` (`:539`).
* Рендер клеток: `GetCompositeGroundIndex` (`:1580-1647`) — мульти-биомный атлас 30 тайлов
  (`:1403-1464`), декор порогами `WorldDecor` + паритетная формула острова `IslandDecorPass`
  (`:1480-1502` — точная копия прореживания `GenerateColonyWindow`), плодородие кэшем 5×5 на чанк
  (`CellSoilFertilityQ16 :1537-1551`, сетка 16×16 как в генераторе `:1162-1163`).
* Выравнивание с островом: клетки `[0,MapWidth)×[0,MapHeight)` видом НЕ рисуются (`PaintCell
  :1761-1762`), чанки внутри прямоугольника острова окклюзируются (`IsOccludedByColony :869-873`).
* `ResetWorld(newSeed)` (`:149-212`) — полный сброс кэшей/слоев/очередей + пересоздание стор-пути;
  `Shutdown()` (`:542-548`) — остановка стримера (см. §6.4: без flush).

Гарантии по выгруженному чанку (сводно): **любая запись в незагруженный чанк возвращает `false`
без исключений** (`AddBlock :282-283`, `RemoveBlock` через `KindSet`, `StreamingWorldManager.*
:101/:113/:129/:147/:162`, `WorldGameRules.IsLoadedAt :38-39`, `ChunkWorldAccess :54-58`).
`HasBlock` для выгруженного чанка возвращает `false` (пустое множество = «нет данных», что неотличимо
от «метки нет»). `StreamingWorldManager.IsBlocked` — единственное место с явным tri-state `null`
= «чанк не загружен» (`:63-72`).

Потокобезопасность: **весь дельта-API — main-thread-only**. `StreamingWorldView` — Godot-узел
(TileMapLayer в `AddBlock/RepaintCell`), `WorldGameRules`/`StreamingWorldManager`/`WorldJobBoard`/
`WorldJobTicker` — main thread (`WorldJobTicker.cs:13`, комментарии `StreamingWorldView.cs:268-273`).
Внутри стримера всё под `lock(_gate)` (`WorldChunkStreamer.cs:31`). Генераторные запросы
(`WorldCellQuery`, `UnifiedWorldGenerator`, `BulkCellSampler`) — чистые функции, потокобезопасны,
в т.ч. из фонового сим-потока (`AgentMovementService.cs:416` зовёт `WorldCellQuery.TerrainAt`
из `AgentSimulationThread`; `WorldCellQuery.cs:68-71` — заявлена потокобезопасность bulk-сэмплера).

---

## 3. Компоненты: роли, граф вызовов, потоки

### 3.1 Роли

| Компонент | Файл | Роль |
|---|---|---|
| `StreamingWorldView` | `src/ui/streaming/StreamingWorldView.cs:21` | владелец resident-чанков, `_blocks`, рендер (тайлы), кодирование дельт при выгрузке, декодирование при загрузке. Реализует `IWallWorld/IBlockWorld/IWorldCellSource` |
| `WorldChunkStreamer` | `src/core/world/streaming/runtime/WorldChunkStreamer.cs:27` | воркер-пул (2..8, `:137-140`): генерация чанков, чтение дельт с диска, lossless-запись дельт при выгрузке; очереди `desired/pending/in-flight/ready/resident/failed/unloading` под `_gate` |
| `ChunkWindowPlanner` | `src/core/world/streaming/runtime/ChunkWindowPlanner.cs:47` | выровненное окно чанков (сторона 3..16 адаптивно, `StreamingWorldView.ApplyAdaptiveWindow :697-714`; жёсткий предел 256, `:54`), diff `Entered/Exited` |
| `WorldChunkStore` | `src/core/world/streaming/storage/WorldChunkStore.cs:12` | файловое хранилище `chunk_X_Y.bin`, атомарная замена temp→move (`:76-96`), пустой payload = удаление (`:66-70`, tombstone `:216-256`) |
| `WorldChunkCodec` / `WorldChunkDeltaCodec` | `.../storage/WorldChunkCodec.cs:13`, `.../storage/WorldChunkDeltaCodec.cs:34` | бинарный формат файла чанка (Brotli+CRC32) / sparse-payload дельт (varuint, канонический порядок) |
| `WorldManifestStore` / `WorldSaveCoordinator` | `.../storage/WorldManifestStore.cs:20`, `.../coordinator/WorldSaveCoordinator.cs:21` | identity мира `world.manifest` (seed/версии/время/старт-тайл) + чекпоинт; координатор открывает/создаёт манифест, валидирует совместимость |
| `StreamingWorldManager` | `.../integration/StreamingWorldManager.cs:16` | фасад гейтов поверх `IWallWorld/IBlockWorld`: `CanPlace/IsBlocked/TryPlaceWall/TryPlaceBlock/TryRemoveBlock/TryReplaceBlock`; открывает мир через координатора |
| `WorldGameRules` | `.../integration/WorldGameRules.cs:17` | игровые правила (можно ли строить/рубить/сеять) поверх `IBlockWorld` + `WorldCellQuery`; действия Try* (гейт + метка) |
| `StreamingToolPlacement` | `src/ui/streaming/StreamingToolPlacement.cs:14` | адаптер «инструменты UI → мир»: ставит ПЛАНЫ (`WallPlan` и т.д., `:50-102`), снимает план ИЛИ готовое; остров исключён (`IsIslandCell :105-106`) |
| `WorldJobBoard` | `src/ui/streaming/WorldJobBoard.cs:14` | очередь задач: чертежи → `WorldPlan`; claim/release; `Complete` = `TryReplaceBlock(Plan→Done)` (`:79-86`) |
| `WorldJobTicker` | `src/ui/streaming/WorldJobTicker.cs:15` | «люди мира»: спавн/движение/работа; A* по процедурной поверхности (`WorldPathFinder`), пишет «готово» через борд; пинит чанки с задачами (`:121`) |
| `WorldPathFinder` | `src/ui/streaming/WorldPathFinder.cs:15` | A* по `WorldCellQuery.TerrainAt` (работает и в незагруженных чанках, `:9-11`); бюджет 24 000 клеток (`:18`), detour ≤384 (`:21`) |

### 3.2 Кто кого вызывает (боевая сборка — `scenes/main/Main.cs`)

* `Main._Ready` создаёт `StreamingWorldView` (`:128-136`); `InitStreamingWorld(seed)` (`:189-226`):
  `WorldSaveCoordinator(world_<<seed>> , seed, ver, FeatureSchemaVersion)` (`:196-197`) →
  `StreamingWorldManager(view, coordinator)` (`:198`) → `Open(0,0)` (`:199`) → `WorldGameRules`
  (`:200`) → `StreamingToolPlacement` (`:201`) → инъекция в HUD (`:203-206`) → `WorldJobTicker.Setup`
  (`:211-220`, пин чанков `WorldJobTicker.cs:121`).
* Игрок → инструменты (через `HUDController.WorldPlacement` `src/ui/controllers/HUDController.cs:1129`,
  проводка `:994,:1014,:1089,:1107,:1166,:1232`) → `StreamingToolPlacement` → `StreamingWorldManager.
  TryPlaceBlock(Plan)` → `StreamingWorldView.AddBlock`.
* `WorldJobTicker._Process` (`:187-214`) → `WorldJobBoard.Refresh` → `view.CollectPendingPlans`;
  агент доходит → `WorldJobBoard.Complete` → `manager.TryReplaceBlock(Plan→Done)` → `view.Add/RemoveBlock`.
* `Main._Process` (`:458-518`): окно стриминга следует за камерой с троттлингом ≥8 тайлов и ≥120 мс
  (`:495-516`) → `view.Update(visible)` → `planner/streamer.Update` → `Entered/Exited`.
* `Main._ExitTree` (`:572-586`): только `_worldView.Shutdown()` (`:583`) — **без выгрузки/сохранения
  resident-чанков** (см. §6.4).

### 3.3 Схема потоков (текстом)

```
MAIN THREAD (Godot)
  Main._Process ──► StreamingWorldView.Update(visible)
                        └► ChunkWindowPlanner.Update → Desired/Entered/Exited
                        └► WorldChunkStreamer.Update (lock _gate): desired, отмена stale
  Main._Process ──► StreamingWorldView._Process
                        ├► DrainReady (lock) → ApplyReady: декод WorldChunkDeltaCodec → _blocks → _textureDirty
                        ├► DrainFailures (lock) → лог
                        ├► ProcessExits: для Exited∩resident∩!desired: EncodeBlocks(_blocks)
                        │    └► UnloadChunkAsync(payload, metadata) → очередь _unloadQueue
                        └► RepaintDirty: покраска/стирание тайлов по кадровым бюджетам
  Инструменты/WorldJobTicker (main) → IBlockWorld.Add/Remove/Has → _blocks + RepaintCell

WORKER POOL (Task.Run, 2..8 воркеров; не трогают Godot API)
  WorkerLoop (lock _gate на каждом шаге):
    load/gen:  ContainsChunk → TryLoadChunk (Brotli+CRC, метаданные seed/genVer/featureSchema)
               → UnifiedWorldGenerator.GenerateChunk → _ready
    unload:    SaveChunk (WorldChunkCodec.Encode: temp-файл → File.Move(overwrite)) → resident снят
  Связь с main: только очереди _ready/_failures/_unloadQueue под _gate + TCS-пульс воркеров

СИМ-ПОТОК (AgentSimulationThread, 10k агентов) — мировой дельта-слой НЕ трогает;
  читает только потокобезопасные WorldCellQuery.TerrainAt (AgentMovementService.cs:416).
```

### 3.4 Путь дельты: запись → сейв → загрузка

1. **Запись (память):** `StreamingToolPlacement.TryPlaceWall` (`:50-52`) → `StreamingWorldManager.
   TryPlaceBlock(WallPlan)` (`:127-142`: гейты IsLoaded → CanPlace(трава) → дубль → BlockingKinds) →
   `view.AddBlock` (`:277-292`) → `_blocks[key][kind].Add(index)` + `RepaintCell`. **На диск ничего
   не пишется.**
2. **Сейв (при выгрузке):** камера уходит → `Update` → `Exited` → `_exitQueue` → `ProcessExits`
   (`:599-663`) → `EncodeBlocks(key)` (`:755-772`: все метки чанка в `WorldChunkDeltaRecord` с
   пустым Data, сортировка LocalIndex→Kind, канонический payload) + `MakeMetadata()` (`:774-776`:
   FormatVersion=1, GeneratorVersion вида, `UnifiedWorldGenerator.FeatureSchemaVersion`, WorldSeed,
   **GameTimeCheckpoint: 0.0**) → `WorldChunkStreamer.UnloadChunkAsync` (`:273-325`: валидация
   резидентности/не-desired/не-двойная выгрузка/лимит очереди = maxConcurrency) → воркер
   `ProcessUnload` (`:497-514`) → `WorldChunkStore.SaveChunk` (`:57-103`) → `WorldChunkCodec.Encode`
   (Brotli + CRC32, `:37-89`) → атомарная запись `chunk_X_Y.bin` (temp → `File.Move(overwrite)`,
   `:76-96`). Пустой payload → удаление файла (`:66-70`).
3. **Загрузка (возврат игрока):** `Update` → desired → воркер `ProcessChunk` (`:444-495`):
   `ContainsChunk` → `TryLoadChunk` (декод, проверка координат `WorldChunkStore.cs:152-157`) →
   проверка метаданных: seed/genVer/FeatureSchemaVersion должны совпасть со стримером
   (`:463-466`); при несовпадении/битом файле — **graceful degradation**: чанк генерируется «как
   будто дельты нет» (комментарий `:457-462`). Затем `GenerateChunk` → результат в `_ready` →
   main `DrainReady` → `ApplyReady` (`:550-597`): `WorldChunkDeltaCodec.TryDecode(payload)` →
   карта kind→индексы в `_blocks` (замещая прежнюю, защита от двойного счёта стен `:570-573`),
   `_chunkData[key]`, `_textureDirty` → перекраска.

Сейв мирового времени: `StreamingWorldManager.Checkpoint(gameTime)` (`:44-49`) →
`WorldSaveCoordinator.PersistCheckpoint` (`:133-153`) → атомарная перезапись `world.manifest`
(`WorldManifestStore.Save :32-65`, WriteThrough+flush). **В бою никто не вызывает** (см. §6.5).

---

## 4. `UnifiedWorldGenerator` + `WorldCellQuery`: детерминированная база

### 4.1 `UnifiedWorldGenerator` — `src/core/world/generation/UnifiedWorldGenerator.cs`

* Константы: `FeatureSchemaVersion = 1` (`:20`), `DefaultGeneratorVersion = 3` (`:21`),
  `StartClearRadius = 14` (`:22`).
* Глобальный якорь `WorldOriginX/Y` (`:29-30`, mutable static, `SetWorldOrigin :33-37`): локальные
  координаты колонии/стриминга привязаны к региону планеты (2048×1024 регионов × 512 клеток,
  `Main.cs:19-21`); при выборе спавна `SetWorldOrigin(region.X*512, region.Y*512)`
  (`Main.cs:318`, `:256`).
* `FindHabitableSpawn(seed, ver)` (`:44-133`) — спиральный поиск пригодного региона.
* `GenerateChunk(seed, ver, key)` (`:142-205`): сетка макро-климата 5×5 (шаг 16 клеток,
  `:156-169`) из `WorldClimateGenerator.SampleBlendedCell`, билинейный `LerpClimate1D`
  (`:216-237`) на клетку + `SampleCell(override)`; чанк `WorldChunk` 64×64 row-major
  (`WorldChunk.cs:5-38`).
* `SampleCell(seed, ver, wx, wy, overrideClimate?)` (`:244-463`): трёхслойный domain-warp,
  гипсометрический якорь суши, реки/озёра/поймы (`:338-381`), биом по Уиттекеру, пляжи, лес/камень
  Q16. Чистая функция от (seed, ver, wx, wy).
* `GenerateColonyWindow(seed, ver, originX, originY, w, h, explicitClimate?)` (`:471-596`) — то же
  для прямоугольного окна, заполняет `MapData` (Ground/TreeOnGrass/TreeVariant/StoneOnGrass/Biome/
  Variant, `:537-565`); **расчистка стартовой поляны радиуса 14 в центре окна** (`:572-589`) —
  в чанковой генерации её НЕТ (см. §7.3).
* Побитовая эквивалентность: `BulkCellSampler == GenerateColonyWindow` для того же окна; при
  origin, выровненном кратно 64, совпадает и с `GenerateChunk` (документировано
  `WorldCellQuery.cs:62-66`).

### 4.2 `WorldCellQuery` — `src/core/world/streaming/integration/WorldCellQuery.cs`

Одиночные запросы (не требуют resident-чанка, семплируют генератор по абсолютным координатам,
`:8-10`): `CellAt :17`, `TerrainAt :20`, `Elevation(At) :24-28`, `MoistureQ16At :30`,
`ForestQ16At :33`, `StoneQ16At :36`, `IsWaterAt :39`, `IsMountainAt :42`. `Sample` (`:45-46`)
прибавляет текущий `WorldOriginX/Y`.

**Bulk:** `BeginBulkSample(seed, ver, originX, originY, w, h)` (`:72-73`) → `BulkCellSampler`
(`:82-212`): сетка климата (w/16+1)×(h/16+1) узлов (Parallel.For при ≥64 узлов, `:128-140`),
дальше каждая клетка — дешёвый билиней + `SampleCell(override)` (`:188-212`); вне окна —
прозрачный fallback в полный путь (`:175-182`). Снапшот `WorldOrigin` на момент Begin (`:92-94`).
Побитово идентичен `GenerateColonyWindow`/`GenerateChunk` (выровненному).

**Где применяется:** ПОКА только в тестах (`tests/TestGeneratorBench.cs:315,338,370,391` —
верификация эквивалентности и скорость). Боевые потребители одиночного `WorldCellQuery`:
`StreamingWorldManager.CanPlace` (`:60`), `WorldGameRules.GetCell` fallback (`:146`),
`WorldPathFinder.Walkable` (`WorldPathFinder.cs:51,59`), `HUDController` (тултипы
`HUDController.cs:415,461,465`), оверлеи влажности/плодородия за кромкой
(`HumidityOverlayRenderer.cs:98`, `FertilityOverlayRenderer.cs:98`),
`AgentMovementService` (сим-поток, `:416`). `MapRenderer` в окне террейна использует СВОЮ
инлайн-схему «сетка 16×16 + `UnifiedWorldGenerator.SampleCell(overrideClimate)`»
(`MapRenderer.cs:612-626`, `:888`) —Bulk-сэмплер туда ещё не подключён (эквивалентная, но
дублированная реализация).

### 4.3 `GeneratedCell` — `src/core/world/streaming/generation/GeneratedCell.cs:15`

`readonly record struct`: `Terrain` (BaseTerrainKind Grass/Water/Mountain, `:4-9`), `ElevationQ16`,
`MoistureQ16`, `ForestQ16`, `StoneQ16`, `TemperatureQ16`, `Biome` (byte), `Variant` (byte) — все
Q0.16 (ushort). Плодородие `SoilFertilityQ16` в клетке НЕ хранится — пересчитывается отдельно
(сетка 16×16: `StreamingWorldView.SoilFertilityAtCell :1518-1534`, кэш `:1537-1570`).

Координатная алгебра — `WorldCoordinates.cs:32`: `ChunkSize=64` (`:34`), `FloorDiv/FloorMod`
(`:40-59`), `ChunkForTile :61`, `LocalForTile :64`, `TileOrigin/ChunkBounds :70-82`.

---

## 5. Персистентность: что и как лежит на диске

Каталог: `user://infinite_world_lab_cache/world_<seed:x16>/` (вид: `StreamingWorldView._Ready
:117-122`; координатор: `Main.cs:194-197`). Один каталог = один сид; вид и координатор пользуются
им независимо (у каждого свой экземпляр `WorldChunkStore`).

| Артефакт | Имя | Формат | Кто пишет | Кто читает |
|---|---|---|---|---|
| Манифест | `world.manifest` | 50 байт, magic `WMAN`, format u16=1, seed u64, generator u32, feature-schema u32, world-time f64, start-x/y i64, CRC32 (`WorldManifestStore.cs:22-28`, `:142-164`) | `WorldSaveCoordinator.OpenOrCreate` (создание/пересоздание, `:59-112`), `PersistCheckpoint` (`:133-153`) | `OpenOrCreate` → `ResumeState` (`:8-15`) |
| Дельта чанка | `chunk_<cx>_<cy>.bin` (десятичные signed-long, `WorldChunkStore.cs:205-214`) | заголовок 58 байт: magic `WCHK`, format u16=1, generator u32, feature-schema u32, seed u64, chunk-x/y i64, game-time f64, plain-len u32, brotli-len u32, CRC32 (`WorldChunkCodec.cs:26-29`); payload = Brotli от `WDLT`-дельты | стример (воркер) при выгрузке чанка | стример при возврате чанка в окно |

Payload дельты (`WorldChunkDeltaCodec.cs`): magic `WDLT` + schema u16=1 (`:36,:44`) + varuint count
+ записи {varuint delta-LocalIndex, kind u8, varuint len, Data}. Лимиты: LocalIndex ≤ 4095,
records ≤ 65 536, payload ≤ 16 МиБ (`:37-40`, `WorldChunkCodec.cs:18`). Кодирование канонично:
сортировка по (LocalIndex, Kind, Data) (`:51-76`), декодер отвергает нарушение порядка, overlong
varuint, trailing bytes (`:122-199`). Пустая дельта = пустой payload = файла нет.

Атомарность: чанк — temp-файл + `File.Move(overwrite)` в пределах каталога, без fsync
(осознанно; комм. `WorldChunkStore.cs:91-94` — «при сбое питания возможна ситуация "валидное имя,
битые данные"», читатель обязан переживать через graceful degradation). Удаление — переименование
в tombstone `*.deleted` (`:216-256`). Манифест — WriteThrough + `Flush(true)` + move
(`WorldManifestStore.cs:46-59`).

Поведение при несовместимости:
* Манифест: не читается/битый → бэкап `manifest_backup_*` + пересоздание (`:77-89`); seed/genVer/
  featureSchema не совпали → бэкап `manifest_backup_seed_…` + пересоздание (`:92-107`).
  Существующие файлы чанков при этом НЕ трогаются и НЕ переносятся (см. §7.1).
* Чанк: метаданные не совпали / CRC/битый → чанк генерируется без дельты (graceful degradation,
  `WorldChunkStreamer.cs:457-470`); файла это не удаляет — он будет перезаписан при следующей
  выгрузке непустой дельты или удалён при пустой.
* `WorldSaveCoordinator.StoreChunkSnapshot/ReadChunkSnapshot/ChunkPath` (`:156-192`) — альтернативный
  путь чтения/записи чанков с явной проверкой совместимости (`EnsureCompatible :208-221`). **В
  боевом коде не вызываются** (только тесты `tests/Phase5StressTestRunner.cs`, `tests/test_world_interaction.cs`).

E2E-покрытие персистентности: `tests/TestDestroyerStreaming.cs` (a-e: край региона, отрицательные
ChunkKey, roundtrip «поставил → ушёл → вернулся», незагруженный чанк, adversarial борд, смена сида).

---

## 6. Чего НЕ ХВАТАЕТ для роли «единственный источник истины»

### 6.1 Транзакционность

* Единственная «транзакция» — `TryReplaceBlock(old,new)` (`StreamingWorldManager.cs:160-193`):
  Remove→Add с откатом старого блока при сбое нового (F-009, `:185-189`). Остальные операции —
  одиночные check-then-act на main thread (внутри одного кадра атомарны, между кадрами — нет).
* Нет много-клеточных транзакций: драг-разметка инструментов (до 250 000 клеток, см.
  `MapDataWriterAudit.md` §6.8) не имеет all-or-nothing семантики; частичная расстановка при
  прерывании не откатывается.
* Нет транзакции «выгрузка чанка»: между `EncodeBlocks` (снимок `_blocks`, `:755-772`) и записью
  файла запись в чанк уже невозможна (он покинул desired, `UnloadChunkAsync :294-295` бросит),
  но изменения между последним `AddBlock` и кодированием попадают/теряются в зависимости от
  порядка кадров — формальной гарантии «снапшот = состояние на момент X» нет.
* Нет compare-and-set / optimistic concurrency для будущего мультиписателя (островной сим-поток).

### 6.2 Перечисление «все дельты в области»

* `CollectPendingPlans` (`StreamingWorldView.cs:341-378`) — только ПЛАНЫ, только загруженные чанки,
  потолок `max` (бординг вызывает с 256, `WorldJobBoard.cs:41`), сортировка по фокусу. Нет API
  «перечислить все записи kind K в прямоугольнике», «все kinds клетки», «итератор по чанку».
* `BlockCount(kind)` (`:453-462`) — только счётчик.
* `ChunkMutations` (`integration/ChunkMutations.cs:11-130`) — готовый контейнер
  `Dictionary<(localIndex,kind),byte[]>` с `Encode/Apply/FromRecords`… **не используется ни в
  одном боевом вызове** (grep: только сам файл и docs). Вид живёт на собственных `HashSet` без
  payload.
* Нет перечисления ДИСКОВОГО состояния: только `IsChunkStored(key)` по одному ключу (`:486`);
  списка сохранённых чанков/области нет (потребовался бы скан каталога — не реализован).

### 6.3 Обратное чтение дельт в `MapData` при материализации — СЕЙЧАС НЕТ

Проверено grep'ом и чтением: ни одного пути «мир → MapData» не существует. `MapRenderer` не читает
`_blocks`; `GenerateColonyWindow` генерирует окно только из сида (`UnifiedWorldGenerator.cs:535`);
вид не рисует островную зону и её чанки окклюзирует (`PaintCell :1761-1762`,
`IsOccludedByColony :869-873`); `Main` всегда строит остров через `GenerateColonyWindow`
(`Main.cs:260-267`, `:322-329`). Если люди мира поставили дельты в чанках, пересекающих прямоугольник
острова (гейты `StreamingToolPlacement` это допускают: `IsWorldCell` = «чанк загружен», граница
острова отсекается только слоем инструмента `WorldPlacementPass.IsIslandCell`), островная
симуляция/рендер их не увидят. «Переезд окна сима» (пересоздать MapData вокруг новой точки из
мирового слоя) сегодня невозможен в принципе: нужен материализатор «GenerateColonyWindow + наложить
дельты чанков области» — такого API нет.

### 6.4 Flush при выходе / «переезжающее окно»

* `Main._ExitTree` → `view.Shutdown()` (`:583`) → `_streamer.DisposeAsync()` (`StreamingWorldView.cs:542-548`).
  `DisposeAsync` НЕ сохраняет resident-чанки — контракт прямо требует выгрузить их ДО закрытия
  (`WorldChunkStreamer.cs:328-331`). В бою никто не выгружает → **все дельты чанков текущего окна
  теряются при выходе из игры** (сохраняются только чанки, покинувшие окно РАНЬШЕ). Нет API
  «FlushAll(force unload + save)».
* Окно стриминга управляется только камерой (`Main.cs:495-516`); сторона окна адаптивна 3..16 чанков
  (`MinWindowChunks/MaxWindowChunks`, `:43-44`), потолок 16×16=1024² клетки. Нет API «передвинуть
  окно сима»/«закрепить область» (кроме пина задач `IsChunkPinned :49`, `:612-617`), нет
  эвакуации/материализации области при переезде.

### 6.5 Время/чекпоинт

* `StreamingWorldManager.Checkpoint(gameTime)` (`:44-49`) в бою не вызывается никем → `world.manifest`
  после создания больше не обновляется; `ResumeState.GameTime` всегда 0 при возврате.
* `WorldSaveMetadata.GameTimeCheckpoint` пишется всегда `0.0` (`MakeMetadata :776`) — поле мертво
  (заявлено как «время последнего обновления именно этого чанка», `WorldSaveMetadata.cs:5`).
* Нет консолидации с островным `TimeManager`/оффлайн-догоном (`OfflineCatchUpPolicy`) — у мира
  нет собственного хода времени, кроме передаваемого в `Checkpoint`.

### 6.6 Учёт агентов и предметов

* `WorldDeltaKind` не имеет видов для: предметов на земле (ItemPile), стадий культур, почвы
  (влага/плодородие), зон (см. `MapDataWriterAudit.md` §2/§6.4 — там перечень островных
  держателей, которые тогда не выразить).
* Payload каждой записи пуст (`EncodeBlocks :764` — `Array.Empty<byte>()`): материал стены,
  тип здания, количество предметов — неоткуда взять. Кодек payload-байты поддерживает
  (`WorldChunkDeltaRecord.Data`, `ChunkMutations.Set(kind,data)`), но потребитель не появился.
* Мир-агенты (`WorldAgent`, `WorldJobTicker`) — эфемерны по дизайну: не сериализуются, их
  in-flight прогресс (WorkLeft/Path) и claim-ы (`WorldJobBoard._claimed :21`) теряются при
  перезапуске/выгрузке; персистентна только «план без готового» (§29). Это корректно для
  «постов у фокуса», но не даёт «переезжающего» контингента. Островные 10k агентов в мировом
  слое не представлены вовсе (см. `MapDataWriterAudit.md` §0).

### 6.7 События об изменениях для UI/рендера

* В мировом слое НЕТ ни одного события/сигнала (grep по `src/ui/streaming` — только
  `WorldMapOverlay` имеет свои UI-события). `AddBlock/RemoveBlock` сразу дёргают `RepaintCell`
  (жёсткая связка с тайлсетом вида), а не публикуют изменение. HUD/миникарта/звук/островное
  зеркало могут только опрашивать (`BlockCount`, `CollectPendingPlans`, `PendingPlans`).
* Нет диффа «что изменилось в чанке» для реактивного UI (кроме внутреннего `_textureDirty`).
* `SimEvents` — телеметрия, не хук (см. `MapDataWriterAudit.md` §5.3).

### 6.8 Версионирование

* Версии есть у контейнеров: манифест format=1 (`WorldManifestStore.cs:22`), чанк-файл format=1
  (`WorldChunkCodec.cs:15`), payload schema=1 (`WorldChunkDeltaCodec.cs:36`) + у identity:
  seed / GeneratorVersion / FeatureSchemaVersion (манифест и метаданные каждого чанка).
* НЕ версионированы: сам словарь `WorldDeltaKind` (добавление kind обратно-совместимо на чтении —
  неизвестный байт просто осядет в `_blocks` — но семантика BlockingKinds/гейтов меняется кодом,
  не данными), правила гейтов (`WorldGameRules` константы `:20-24`), формат `WorldPlan`.
* Миграций нет вообще: смена версии = тихая потеря дельт (graceful degradation) без уведомления
  игрока и без porting-инструмента (детали §7.1).
* `UnifiedWorldGenerator.WorldOriginX/Y` — mutable static: корректность всех запросов зависит от
  дисциплины «не менять между Begin и End» (Bulk-сэмплер снимает снапшот, `WorldCellQuery.cs:92-94`;
  `WorldPathFinder` чистит кэш между поисками, `WorldPathFinder.cs:30-34`). Формального владения
  (кто и когда имеет право звать `SetWorldOrigin`) нет.

### 6.9 Прочие найденные дыры/асимметрии

* Асимметрия `IsPlacementKind`: `CutTreePlan`/`MinedStonePlan` — placement (гейт травы),
  а готовые `CutTree`/`MinedStone` — нет (`WorldDeltaKind.cs:79-80`). `WorldJobBoard.Complete`
  заменяет `CutTreePlan→CutTree` — замена проходит без террейн-гейта, логично; но инструмент
  `TryCutTree` сначала гейтит по `CanCutTreeAt` (не трава-гейт, а ForestQ16), потом
  `TryPlaceBlock(CutTreePlan)` гейтит по траве (`StreamingWorldManager.cs:134-136`) — двойной
  стандарт для одной сущности.
* `WallCount` — хрупкий ручной счётчик: инкремент/декремент в 4 местах (`AddBlock :288-289`,
  `RemoveBlock :322-327`, `ApplyReady :570-580`, `RemoveChunkTiles :684-686`) с зажимом от ухода
  в минус; при повторной доставке снапшота вычитается только прежний WALL-набор — другие kinds
  молча замещаются (для них счётчика нет, но и инварианта тоже).
* Ошибка чтения валидного файла дельты (транзиент IO) в `ProcessChunk` проглатывается
  (`WorldChunkStreamer.cs:463` — `out _`): чанк генерируется «без дельты», при следующей выгрузке
  пустая память кодируется в пустой payload → `SaveChunk` **удаляет файл с валидными данными**
  (`WorldChunkStore.cs:66-70`). Потеря данных без следа в логе.
* `HasBlock` для выгруженного чанка = `false` без индикации «нет данных» (кроме `IsBlocked`).
  Инструмент вне окна получит «нельзя», а не «не знаю».
* Дублирование пути стор-конфигурации: вид и координатор строят каталог независимо
  (`StreamingWorldView._Ready :117-122` vs `Main.cs:194-197`) и держат по своему `WorldChunkStore`;
  согласование — конвенция, не контракт.
* Пороговое несоответствие декора: `WorldGameRules.IsArableSoil`/`CanCutTreeAt` используют
  только пороги `WorldDecor` (`WorldGameRules.cs:169,174`), без прореживания `IslandDecorPass`
  (группы/полянки, `StreamingWorldView.cs:1480-1502`) → правила считают «дерево есть» там, где
  рендер его не рисует (порог пройден, но сэмпл прореживания выпал). `ChunkWorldAccess.HasTreeAt`
  — та же проблема (`:35-41`).
* `WorldFeatures.cs` (River/Lake полилинии, `:15-77`) — не подключён: реки/озёра считаются
  процедурно внутри `SampleCell` (`:338-381`); файл фактически мёртв для боевого пути.

---

## 7. Риски рассинхрона и как чинить

### 7.1 Генератор vs сохранённые дельты при смене версий

Сценарий: bumped `WorldGeneratorVersion`/`FeatureSchemaVersion`.

1. `OpenOrCreate` видит несовпадение манифеста → бэкап + пересоздаёт манифест
   (`WorldSaveCoordinator.cs:92-107`). Игрок не предупреждён; дельты на диске остаются со СТАРЫМИ
   метаданными.
2. При входе чанка в окно `ProcessChunk` отбракует снапшот по метаданным (`WorldChunkStreamer.cs:463-466`)
   → чанк от новой версии генератора, БЕЗ дельт. Игровая работа (стены/склады/посевы) исчезает тихо.
3. При выгрузке такого чанка новая (возможно пустая) дельта перезапишет старый файл
   (`SaveChunk` пишет по тому же пути) — старые данные уничтожаются физически, откат к старой
   версии больше невозможен.
4. Рассинхрон «террейн по новой версии + дельты, поставленные по старым гейтам»: дельта,
   валидная при v3 (трава), может лечь на воду по v4 — валидации «дельта совместима с новым
   террейном» нет (гейты проверяются только при ЗАПИСИ, не при загрузке).

Что нужно: (а) политика миграции в манифесте (bump с переносом/репроекцией дельт или явным
бэкапом каталога чанков, а не только манифеста); (б) при graceful degradation — хотя бы лог/флаг
«дельта отброшена: N записей» + счётчик для UI; (в) валидация совместимости дельт с террейном при
материализации (пересечения: `Wall` на воде и т.п.).

### 7.2 Расхождение источников seed/версий

`Main.WorldGeneratorVersion` (const, `Main.cs:18`), экспорт вида `GeneratorVersion` (default 3,
`StreamingWorldView.cs:31`), метаданные при выгрузке (`MakeMetadata :774-776`), стример
(валидирует `UnloadChunkAsync` по СВОИМ значениям, `WorldChunkStreamer.cs:283-286`) и координатор
(по СВОИМ, `WorldSaveCoordinator.cs:194-221`) — пять мест, согласующихся конвенцией. Рассинхрон
любой пары = «тихо битые» чанк-файлы (запишутся с одними версиями, стример/координатор отвергнет
по другим). Нужно единое `WorldIdentity`-значение, передаваемое во все точки.

### 7.3 Стартовая поляна: `GenerateColonyWindow` расчищает, `GenerateChunk` — нет

`GenerateColonyWindow` в радиусе 14 (StartClearRadius, `UnifiedWorldGenerator.cs:22`) вокруг центра
окна принудительно ставит `Ground=Grass`, `TreeOnGrass=false`, `StoneOnGrass=false`
(`:572-589`). Чанковая генерация (`GenerateChunk :142-205`) и `SampleCell` про поляну не знают.
Следствия (уже сегодня):
* `WorldCellQuery.TerrainAt` на клетках поляны может вернуть Water/Mountain →
  `StreamingWorldManager.CanPlace :59-60` и `WorldGameRules.IsReadyBase :180-183` отвергнут
  размещение в поляне, `WorldPathFinder.Walkable :50-51` сочтёт клетку непроходимой,
  `AgentMovementService.cs:416` вытолкнет агента.
* Стриминговая подложка под островом рисовала бы «не-траву», но островную зону вид окклюзирует
  (`IsOccludedByColony :869-873`) — визуального шва нет, рассинхрон чисто игровой.
* Дубль генерации: окно 1024² (16×16 чанков) регенерируется и чанками (для `_chunkData`), и
  `GenerateColonyWindow` (для `MapData`) — с разной пост-обработкой.

**Как чинить, не сломав старые сейвы (рекомендация):** сделать поляну ДАННЫМИ, а не частным
случаем генератора:
1. Ввести kind `GroundOverride` (payload = байт целевого `BaseTerrainKind`) — генератор остаётся
   чистой функцией, чанки не меняются, формат payload уже поддерживает Data (`WorldChunkDeltaRecord`).
2. При создании нового мира (`ResumeState.IsNewWorld == true` из `OpenOrCreate`, ровно один раз)
   синтезировать записи дельт для клеток поляны: `CutTree`/`MinedStone` для снятия декора +
   `GroundOverride(Grass)` только там, где генератор != Grass; писать их в delta-файлы чанков
   спавна (через `WorldSaveCoordinator.StoreChunkSnapshot` — путь уже есть, `:156-165`) или в
   отдельный island-override-стор (см. `MapDataWriterAudit.md` §6.1 — стор обязан не зависеть от
   резидентности чанка).
3. Гейты (`CanPlace`, `IsReadyBase`, `IsArableSoil`, `WorldPathFinder.Walkable`,
   `AgentMovementService`) дополнить чтением override-дельты поверх `TerrainAt`.
4. Старые сейвы: манифест существует → `IsNewWorld=false` → синтеза нет, поведение прежнее
   (их рассинхрон остаётся, но ничего не ломается). Опциональная ленивая миграция: добавить в
   манифест флаг «миграция поляны выполнена» — это bump формата манифеста (v2 читает v1 как
   «миграция pending», 50-байтная фиксированная структура позволяет добавить поле только новым
   форматом); выполнить синтез однократно при первом открытии. Старые файлы читаются как раньше.
5. НЕ переносить расчистку в `GenerateChunk`: это меняет выход генератора → обязан bump
   GeneratorVersion → п.7.1 тихо аннулирует ВСЕ старые дельты.

Дополнительно: убрать дубль — материалом для «переезжающего окна сима» должна стать одна
генерация (чанки) + дельты, а `GenerateColonyWindow` оставить тонкой обёрткой bulk-семпла
(`BeginBulkSample` уже побитово эквивалентен) — см. §6.3.

### 7.4 WorldOrigin как mutable static

Все запросы `WorldCellQuery.Sample` прибавляют ТЕКУЩИЙ origin (`:45-46`). Смена региона
(`SetWorldOrigin`, `Main.cs:318/:256`) инвалидирует смысл координат «на лету»; bulk-сэмплер держит
снапшот на Begin (fallback намеренно использует его, `:92-94`), `WorldPathFinder` чистит кэш между
поисками (`:30-34`), но никаких барьеров нет. Если материализация острова (генерация чанков в
воркерах) и `SetWorldOrigin` пересекутся — чанки одного окна посчитаются в разных системах
координат. Нужен immutable-контекст (identity: seed+ver+origin), передаваемый явно.

### 7.5 Пределы представления

* `FitsTileMap` (`:1873-1881`): за пределами int32-диапазона клеток чанк не рисуется (предел
  TileMapLayer) — данные/гейты работают, рендер молча пропадает; на планете 2048×1024 регионов
  (~1M×512K клеток) от int32 далеко, но у кромки надо помнить.
* `MaxBufferedChunks = 256` (`WorldChunkStreamer.cs:29`) — окно 16×16 занимает кэп целиком
  (resident+pending+failed); телепорты создают давление на резервации (чистка есть, `:403-414`,
  но окно 16 — практический максимум одновременно резидентного).
* Транзиентное чтение чанка с ошибкой IO → тихая потеря дельты + перезапись файла (§6.9, п.3).

### 7.6 Согласованность «правила vs рендер» за пределами острова

`WorldGameRules.IsArableSoil` отсекает только воду/песок/лёд/дерево/камень ПО ПОРОГАМ
(`WorldGameRules.cs:156-177`), но не знает прореживания `IslandDecorPass` (группы рощ, пороги
плодородия): правила и рендер отвечают по-разному на «есть ли дерево» в зоне леса. Единый
источник должен暴露 один предикат «декор клетки с учётом дельт» (сейчас он зашит в приватном
`GetCompositeGroundIndex :1580-1647` + `IslandDecorPass :1480-1502` и недоступен правилам).

---

## 8. Резюме

**Готово для роли источника истины (мировой слой):**
детерминированная база (`UnifiedWorldGenerator.SampleCell`/`GenerateChunk`/`GenerateColonyWindow`
+ `WorldCellQuery`, побитово согласованные, потокобезопасные); стабильный словарь видов с
планами/готовыми и единым `BlockingKinds`; чистые контракты (`IWallWorld/IBlockWorld/
IWorldCellSource/IWorldAccess`) с внятными отказами на незагруженном чанке; персистентный
формат (манифест + чанк-дельты, Brotli+CRC, атомарная запись, версионированные метаданные);
graceful degradation при битых/несовместимых дельтах; план→готово как бесплатная модель задач
(§29) с исполнителями (`WorldJobBoard`/`WorldJobTicker`/`WorldPathFinder`).

**Добавить в первую очередь (по убыванию остроты):**
1. **Flush-on-exit** («выгрузить и сохранить все resident-чанки» при Shutdown/выходе) — сейчас
   дельты текущего окна теряются при каждом выходе из игры (`Main.cs:583`,
   `WorldChunkStreamer.cs:328-331`).
2. **Материализатор области**: API «GenerateColonyWindow-окно + наложить дельты чанков этой
   области → MapData/кэш» и обратная эвакуация — без него «переезжающее окно сима» и
   «MapData=кэш» невозможны (§6.3).
3. **Живой чекпоинт**: вызвать `Checkpoint` из `Main` (время сейчас не сохраняется) и писать
   `GameTimeCheckpoint` в метаданные чанков (поле мертво, `MakeMetadata :776`).
4. **События изменений** (`event`/сигнал «клетка kind X изменена», «чанк загружен/выгружен»)
   для UI/рендера/острова-зеркала — сейчас только polling.
5. **Перечисление дельт области** (по kind/прямоугольнику, включая дисковые) + payload DTO
   (материал/тип/кол-во) — контейнер уже готов (`WorldChunkDeltaRecord.Data`,
   `ChunkMutations`), данные пусты.
6. **Единый WorldIdentity** (seed+genVer+featureSchema+origin) во все точки вместо пяти
   независимых источников (§7.2) + политика миграции версий с переносом дельт и логированием
   потерь (§7.1).
7. **Поляна как дельта** (`GroundOverride` + синтез при IsNewWorld, ленивая миграция старых
   сейвов) — не перенося расчистку в генератор чанков (§7.3).
