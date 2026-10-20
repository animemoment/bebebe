# Аудит писателей островного состояния (`MapData` + легаси-менеджеры)

Дата: 2026-10-19. Только чтение кода; ссылки вида `файл:строка` — точные на момент аудита.
Область: остров колонии (локальная карта `[0..1024)×[0..1024)`), его рендер, сим, путь агентов,
и существующая система мир-дельт (`WorldDeltaKind`/`IBlockWorld`/`StreamingWorld*`).

---

## 0. Модель в двух словах

Существуют **две независимые вселенные**, связанные только выбором координаты:

| | Остров (колония) | Мир за кромкой |
|---|---|---|
| Состояние | `MapData` (`src/core/world/MapData.cs:15`) + 10 синглтон-менеджеров | чанки `WorldChunk` + разреженные метки `ChunkMutations` |
| Владелец | `SimulationContext` (`src/simulation/SimulationContext.cs:7`), `MapRenderer` (`src/ui/renderers/MapRenderer.cs`) | `StreamingWorldManager` (`.../integration/StreamingWorldManager.cs:16`), `StreamingWorldView` (`src/ui/streaming/StreamingWorldView.cs`) |
| Кто работает | `AgentSimulationThread` (10k агентов, фоновый поток) | `WorldJobTicker` (`src/ui/streaming/WorldJobTicker.cs:15`), «люди мира» на main thread |
| Персистентность | **НЕТ** (см. §5.4) | `world.manifest` + чанк-дельты (`WorldSaveCoordinator.cs:133`, `WorldChunkStore`) |
| Словарь видов | — (каждый менеджер свой) | `WorldDeltaKind` (`.../integration/WorldDeltaKind.cs:4`) |

**Критично:** `SimulationContext` **не копирует** `MapData`, а алиасит его массивы —
`Main.cs:381` передаёт `Ground/TreeOnGrass/StoneOnGrass/Humidity/Fertility` прямо из `MapData`
в `AgentSimulationThread.Start` (`AgentSimulationThread.cs:182`), откуда они уходят в ctor `SimulationContext`
(`AgentSimulationThread.cs:225`, поля `SimulationContext.cs:9-16`). То есть `ctx.TreeOnGrass[x,y]=false`
в джоб-хендлере — это **буквально** запись в `MapData.TreeOnGrass`. Отсюда: «писатель MapData» и
«писатель острова» — часто одно и то же место, но не всегда (`SolidWalls` живёт отдельно, `AgentSimulationThread.cs:200`).

Мост «инструмент → мир» уже существует и **явно исключает остров**:
`StreamingToolPlacement.cs:104-113` (`IsIslandCell`), комментарий `StreamingToolPlacement.cs:10-12`:
«Клетки острова сюда не попадают: их обрабатывает островная логика инструмента».
Ветвление по координате сделано в самих инструментах через `WorldPlacementPass.IsIslandCell`
(`src/ui/tools/WorldPlacementPass.cs:84`).

---

## 1. Прямые писатели `MapData`

Поля: `Ground`, `TreeOnGrass`, `TreeVariant`, `StoneOnGrass`, `Biome`, `Variant`
(`MapData.cs:17-31`, get-only — пишутся только через индексер массива), `Humidity`/`Fertility`
(мутабельные классы), метаданные `Seed/Climate/LakeCount/...` (`MapData.cs:41-48`, сеттеры).

| Файл:строка | Что меняет | Триггер | Класс | Кто читает | Синхронизация с миром | Риск при унификации |
|---|---|---|---|---|---|---|
| `src/core/world/generation/UnifiedWorldGenerator.cs:537-565` | `Ground`, `TreeOnGrass`, `TreeVariant`, `StoneOnGrass`, `Biome`, `Variant` — вся карта | Генерация окна колонии (`GenerateColonyWindow`, объявление `:471`) | **ТЕРРЕЙН** (чистая функция от `seed`+`origin`+`generatorVersion`) | `MapRenderer` (террейн/объекты/горы), все менеджеры-гейты, pathfinder | Нет. Стриминг генерирует те же клетки повторно (`StreamingWorldView.IslandDecorPass`, `:1480`, выровнен с `:545-565` тестом `tests/test_streamingview_decor_seam.cs:151`) | Двойная генерация уже существует: если мир-дельта станет источником истины, островную генерацию придётся оставить как «базу» или выпиливать — иначе два рендера одной клетки (см. §5.5) |
| `UnifiedWorldGenerator.cs:488-491` | `Seed`, `Climate` | Генерация | ТЕРРЕЙН/метаданные | HUD, диагностика, тесты | Нет | Мир хранит `seed` в манифесте отдельно (`WorldManifest`) — метаданные дублируются |
| `UnifiedWorldGenerator.cs:584-586` | `Ground=Grass`, `TreeOnGrass=false`, `StoneOnGrass=false` в стартовой поляне | Генерация (`StartClearRadius`) | **ТЕРРЕЙН**, но **расходится с генератором мира** | `MapRenderer`, гейты, pathfinder | **Нет — и это ломает мост:** `WorldCellQuery.TerrainAt` вернёт для поляны исходные `Water/Mountain` (если такие были), а `WorldGameRules.CanPlaceWallAt`/`CanBuildAt` (`WorldGameRules.cs:56-63`) → `IsReadyBase` (`:180-183`) требует `Terrain==Grass` | Стены/здания в поляне, разрешённые островом, будут отвергнуты мир-дельта-гейтом (`CanPlace` ложно) → зеркало молча теряет постройки |
| `UnifiedWorldGenerator.cs:592-593` | `Humidity.Initialize`, `Fertility.Initialize` (вся почва) | Генерация | ТЕРРЕЙН-производное (шум `moist`/`heightMap`) | оверлеи влажности/плодородия, `FarmYield`, `HumidityMap.GrowthMultiplier` | Нет. Мир умеет **пересчитать** только базовое плодородие (`StreamingWorldView.SoilFertilityAtCell`, `:1518`) — не динамику | Динамический слой нельзя «восстановить» запросом к генератору — только персистить |
| `src/simulation/jobs/handlers/TreeChoppingJobHandler.cs:79` | `ctx.TreeOnGrass[tx,ty]=false` | Тик симуляции: агент досчитал свалить дерево (`Commit`, `:77-80`) | **МУТАЦИЯ** | `MapRenderer` объектный слой, `TileClearanceService`, `JobValidator`, `WanderJobSystem`, `ConstructionJobHandler.CanAgentExecute:39`, `NeedsJobSystem:212`, `FlowField`/pathfinder через декор | **Нет.** В мир пишется только `CutTreePlan` инструментом (`TreeFellingTool.OnRelease:68-88` → `StreamingToolPlacement.TryCutTree:88`), а не факт рубки | При рубке на острове стриминговый слой под островом продолжит рисовать дерево (`StreamingWorldView` читает `CutTree` из меток, `:1717` контекст) → дерево «двоится»; нужен kind `CutTree` из тика симуляции |
| `src/simulation/jobs/handlers/MiningJobHandler.cs:87` | `ctx.StoneOnGrass[tx,ty]=false` | Тик симуляции: агент добыл камень (`Commit`, `:84-88`) | **МУТАЦИЯ** | те же + `StoneJobManager`, `FarmJobManager.MarkPlot:37` | **Нет** (аналогично: только `MinedStonePlan` от инструмента, `StoneMiningTool:82-85`) | То же для камня |
| `src/ui/renderers/MapRenderer.cs:1345` | `_pendingMapData.TreeOnGrass[pos]=false` | Событие `TreeJobManager.OnTreeChopped` (подписка `:377`, вызов `TreeJobManager.CompleteTree:113`) | МУТАЦИЯ (дубль) | — | Нет | **Путь фактически мёртв:** `ApplyMap` (`:1097`) обнуляет `_pendingMapData`, а фильтр `MapData => _currentMapData ?? _pendingMapData` (`:228`) отдаёт `_currentMapData`. Значит запись идёт в `null` → зеркалить тут нельзя, точку надо удалить или направить в фасад |
| `src/ui/renderers/MapRenderer.cs:1359` | `_pendingMapData.StoneOnGrass[pos]=false` | Событие `StoneJobManager.OnStoneMined` (подписка `:381`) | МУТАЦИЯ (дубль) | — | Нет | То же: no-op |
| `src/core/world/HumidityMap.cs:233-339` (`Tick`), вызов `AgentSimulationThread.cs:435-479` | `_cur[]` всей карты (перелив/полив/сезон), swap буферов `:338-339` | Сим-тик каждые `TickIntervalGameSec=250` игровых сек (`HumidityMap.cs:26`) | **МУТАЦИЯ** (динамика от сезона+времени+ферм) | `HumidityOverlayRenderer.Refresh:40`, `FarmYield`, `GrowthMultiplier` | Нет. Аналога в мир-дельтах нет вообще | В `WorldDeltaKind` нет вида «почва»; сериализация 1M `int` на тик — дорого. При унификации почву придётся оставить островным overrid'ом поверх мира |
| `src/core/world/FertilityMap.cs:170-227` (`Tick`), вызов `AgentSimulationThread.cs:485-498` | `_cur[]` всей карты (лес удобряет, огород истощает), swap `:225-226` | Сим-тик каждые `TickIntervalGameSec=500` (`FertilityMap.cs:24`) | **МУТАЦИЯ** | `FertilityOverlayRenderer`, `FarmYield` | Нет | То же |
| `src/core/world/FertilityMap.cs:66-75` (`DrainAfterHarvest`), вызов `src/simulation/farming/CropGrowthManager.cs:174` | `_cur[i] *= 0.8` (клетка) | Тик симуляции: сбор урожая (`CropGrowthManager.HarvestCrop`) | **МУТАЦИЯ** | `FertilityOverlayRenderer`, `FarmYield` | Нет | Точечная мутация почвы — самый «естественный» кандидат на дельту, но вида нет |
| `src/core/world/HumidityMap.cs:64-69` / `FertilityMap.cs:51-56` (`Set`) | одна клетка (дебаг/генерация) | Явный вызов | ТЕРРЕЙН/дебаг | — | Нет | В `src/` не вызываются (только `Initialize`); можно использовать как точку внедрения фасада |
| `scenes/main/Main.cs:281`, `:343` (`ExternalPendingMap = …`) | **замена всей карты** (`MapRenderer.ExternalPendingMap`, `:99-106`) | Регенерация региона/сида (`OnRegionSelected`/`OnSeedRegenerated`) | ТЕРРЕЙН | всё | Нет | Полная замена карты должна инвалидировать зеркало целиком (иначе дельты прошлой карты прилипнут к новой) |

**Итого прямых писателей `MapData`: 12 мест; из них 6 — МУТАЦИИ** (4 бинарных флага + 3 почвенных
пути; `MapRenderer.cs:1345/1359` — нерабочие дубли, реальный эффект дают хендлеры).

Ключевой вывод: **`Ground`, `Biome`, `Variant`, `TreeVariant` после генерации не меняет никто.**
Терраформирования/копания земли в проекте нет — ТЕРРЕЙН действительно восстанавливается из сида
(с оговоркой про стартовую поляну, `UnifiedWorldGenerator.cs:584`).

---

## 2. Легаси-менеджеры, меняющие островное состояние

Все — синглтоны (`static Instance`), состояние вне `MapData`, **не персистится**.

| Файл:строка | Что меняет | Триггер | Класс | Кто читает | Синхронизация с миром | Риск при унификации |
|---|---|---|---|---|---|---|
| `src/simulation/trees/TreeJobManager.cs:20-115` | `_markedTrees` (метки рубки), `JobBroker.RegisterTreeChop` | Инструмент рубки (`TreeFellingTool:87`), ферма (`FarmJobManager:56`), сброс | МУТАЦИЯ-намерение (план) | `MapRenderer.OnTreeMarked`, `JobValidator`, `JobBroker`, `DesignationRenderer` | **Частично:** вне острова инструмент пишет `CutTreePlan` (`StreamingToolPlacement.cs:88-94`) — на острове только метка в памяти | Метка ≠ дельта: план острова не виден `WorldJobBoard` (люди мира не придут); и наоборот `WorldJobTicker` умеет рубить только мир |
| `src/simulation/stones/StoneJobManager.cs:25-139` | `_markedStones` | `StoneMiningTool:91`, ферма (`FarmJobManager:57`) | МУТАЦИЯ-намерение | `JobValidator`, `JobBroker`, рендер | Частично: `MinedStonePlan` вне острова (`StreamingToolPlacement.cs:96-102`) | То же |
| `src/simulation/farming/FarmJobManager.cs:22-125` (маркировка/очистка), `:334-348` (`CompletePlot` → `_completedBeds`), `:130-205` (`NotifyTreeCleared/NotifyStoneCleared`), `:351-369` (`ClearAll`) | `_markedPlots`, `_completedBeds`, `_plotsNeedingClearance` (грядки/грядки в работе) | Инструмент фермы (`FarmingTool:92`), завершение расчистки хендлерами, сброс | **МУТАЦИЯ** (грядка = постройка) | `MapRenderer.RefreshAllBlueprints:1292,1310-1313` (спрайт грядки), `TileClearanceService`, `ZoneManager.AutoPlant`, `BuildTool:269-274` | Частично: вне острова `CropPlan` (`StreamingToolPlacement.cs:69-75`) | Сингл `OnPlotCompleted` (`:20`) есть — но не покрывает фазу расчистки (дерево/камень/предметы), а именно она держит состояние между менеджерами |
| `src/simulation/farming/CropGrowthManager.cs:34-50` (`PlantCrop`), `:148-191` (`HarvestCrop`, вызов `Fertility.DrainAfterHarvest:174`), `:193-202` (`RemoveCrop`), `:257-266` (`ClearAll`) | `_crops` (посевы, стадии 1-4) | Тик симуляции (посадка джобой, рост `:68`, сбор) | **МУТАЦИЯ** | `CropRenderer` (через `SnapshotQueue`), `FarmYield`, `ZoneManager.AutoPlant:114`, `FarmingJobHandler` | **Нет событий вообще** — только `SnapshotQueue` (`:28`) | Нет hook'а для централизованного перехвата: стадии роста и урожай придётся инструментировать вручную либо читать снапшоты (потеря точности/задержка 1 кадр) |
| `src/simulation/structures/ConstructionManager.cs:114-226` (`StartSitesBatch`: читает маски `:124-125`, `:164-166`), `:250-263` (`Notify*Cleared`), `:387-438` (`CompleteConstruction` → `StructureManager.AddStructure:428`), `:440-472` (`CancelSite`), `:526-558` (`UnregisterSiteJobsAndRefund` → `GroundItemManager.SpawnItems:554`), `:586-649` (`ReconcileTick`), `:650` end (`ClearAll`) | `_sites` (площадки, стадии Clearing→Supplying→Assembling), группы, отложенные склады | Инструмент (`BuildTool.cs:179`, `ZoneDraftTool:203-246`), ИИ-джобы, тик | **МУТАЦИЯ** (чертёж и постройка) | `MapRenderer` (чертежи/стены), `BlueprintManager` (фасад), `JobBroker`, `StructureManager` | Частично: `WallPlan/BuildingPlan` вне острова (`StreamingToolPlacement.cs:50-86`) | Есть богатый набор событий (`:24-29`), но у острова план НЕ равен постройке по смыслу мира: у мира «план без готового» персистится как задача, у острова площадка живёт в памяти и исчезает при сбросе |
| `src/simulation/structures/StructureManager.cs:88-131` (`AddStructure`, **`ctx.SolidWalls[x,y]=true:112`**), `:133-160` (`RemoveStructure`, `:151=false`), `:184-211` (`ClearAll`, `:199=false`) | `_structures` (стены/мебель/станки) + `SimulationContext.SolidWalls` + навигация (`FlowFieldManager`, `HierarchicalPathfinder`) | Завершение стройки (`ConstructionJobHandler.cs:106,114`), снос, сброс | **МУТАЦИЯ** (стена/здание) | pathfinder (`HierarchicalPathfinder.cs:413`), `FlowFieldManager.cs:213`, `AgentMovementService.cs:449-458`, `NeedsJobSystem:212`, `WanderJobSystem:34`, `JobBroker` stand-поиск, `MapRenderer._builtWalls` | Частично: `Wall`/`Building` вне острова (`StreamingToolPlacement.cs:50,77`) | **`SolidWalls` — не в `MapData`** (`AgentSimulationThread.cs:200`) и не персистится; при унификации это второе (дублирующее) представление занятости, его надо либо выводить из дельт, либо синхронизировать |
| `src/simulation/building/WallBuildManager.cs:26-34` | прокидывает в `StructureManager.AddStructure(x,y,def)` **без `ctx`** | `MapRenderer.OnBlueprintCompleted:1327` (завершение стены) | МУТАЦИЯ (обходной путь!) | — | Нет | **Дыра:** путь без `ctx` не ставит `SolidWalls` и не инвалидирует pathfinder. Сейчас спасает то, что `StructureManager.AddStructure(ctx,…)` уже вызван из `ConstructionJobHandler:114`/`ConstructionManager:428` и второй вызов вернёт `false` (`StructureManager.cs:95`). При рефакторинге порядок легко потерять |
| `src/simulation/building/BuildingManager.cs:37-46`, `BlueprintManager.cs:54-84` | адаптеры к `StructureManager`/`ConstructionManager` (`AddBuilding`, `AddBlueprint`, `RemoveBlueprint`, …) | Инструменты, UI | МУТАЦИЯ (делегирование) | `MapRenderer`, гейты инструментов | Нет | Тонкие фасады без собственного состояния — удобная точка перехвата, но их много и они дублируют события |
| `src/simulation/items/GroundItemManager.cs:119-222` (`SpawnItems`), `:235-291` (`TrySpillItemsAdjacent`, читает `_mapData.Ground:254`), `:310-344` (`TakeItemsWithType`), `:402-533` (`TryReserveGroundItems`), `:347-358` (`ClearAll`) | `_groundItems` (предметы на земле: Log/Stone/Grain) + чанк-индексы | Рубка (`TreeChoppingJobHandler:127`), добыча (`MiningJobHandler:124`), возврат материалов (`ConstructionManager:554`), вынос фермой (`FarmJobManager:55`), логистика | **МУТАЦИЯ** | `GroundItemRenderer` + `ItemShadowRenderer` (через `SnapshotQueue:75`), `StockpileManager`-логистика, `JobValidator` | **Нет, и вида для предметов в `WorldDeltaKind` нет** | Полный gap: предметные кучи не выражаются мир-дельтами (нет `ItemPile`). Без нового вида зеркалирование принципиально неполно |
| `src/simulation/items/StockpileManager.cs:66-78` (битмаска 512²), `:131-322` (зоны/ожидание/активация/снятие), `:677-798` (deposit/withdraw/резервы), `:864-893` (`ClearAll`) | `_zones`, `_pendingZones`, `_storage`, битмаска | `WarehouseTool:82-84` (остров), `ZoneDraftTool`, логистика | **МУТАЦИЯ** (склад) | `StockpileItemRenderer`, HUD (`GetTotalItemCount`), `JobBroker` | Частично: `Stockpile`/`StockpilePlan` вне острова (`StreamingToolPlacement.cs:61-67`) | **Здесь уже есть жёсткая привязка к 512** (`_zoneBitmask = new long[512*512/64]`, `:66`; проверка `< 512` `:70`), тогда как остров теперь 1024 (`MapRenderer.cs:33-34`) → склад за пределами 512×512 не индексируется. При унификации это надо чинить до зеркалирования |
| `src/simulation/zones/ZoneManager.cs:68-96` (`CreateFarmZones`), `:98-124` (`SetAutoPlant`), `:128-235` (`RemoveTiles`), `:240-306` (work-зоны), `:376-414` (`SetWorkTiles`), `:495-630` (`DispatchZones`), `:662-674` (`ClearAll`) | `_zones`, `_tileToZoneId` (ферма + бригады) | `FarmingTool:93,116`, `ZoneDraftTool`, UI | **МУТАЦИЯ** (зона) | `FarmZoneRenderer`, `WorkZoneController`, `FarmJobManager.AutoPlant`, `BuildTool:272` | Нет (у мира нет понятия «зона»/бригада) | Зоны — чисто островная абстракция: в мир-дельты её не выразить без нового вида (`WorkZone`/`FarmZone`); либо признать их островным слоем |
| `src/simulation/jobs/WorkProgressTracker.cs` | прогресс визуала работ (не `MapData`) | Тик | визуал | `DesignationRenderer`/оверлеи | Нет | Не влияет на персистентность, но при унификации может разъехаться с прогрессом людей мира (`WorldJobTicker.WorkSeconds`) |

---

## 3. Где островное состояние ЧИТАЕТСЯ (рендер / сим / путь)

| Файл:строка | Что читает | Роль |
|---|---|---|
| `src/ui/renderers/MapRenderer.cs:1090-1201` (`ApplyMap`, заливка слоёв) | `Ground`, `GetGroundVariant`, `GetBiome`, `TreeOnGrass`, `GetTreeVariant`, `StoneOnGrass` | основной рендер острова (terrain/object/mountain/stockpile/building/blueprint) |
| `MapRenderer.cs:654-767` (`RequestTerrainWindowUpdate`, `FillIslandWindowFromMapData`) | `Ground`, `Biome`, `Variant` | окно террейна 512² внутри острова (шэйдер) + фоновая генерация за кромкой |
| `MapRenderer.cs:1240-1260` | стены/здания/чертежи из `StructureManager`/`ConstructionManager` | слои `_builtWalls`, `_blueprintLayer` |
| `MapRenderer.cs:1287-1314` (`RefreshAllBlueprints`) | `ConstructionManager.GetAllSites`, `FarmJobManager.GetAllMarkedPlots` | чертежи и грядки |
| `MapRenderer.cs:1341-1364` (`OnTreeChopped`/`OnStoneMined`) | события `TreeJobManager`/`StoneJobManager` | гашение спрайтов + (мёртвые) записи в `_pendingMapData` |
| `src/ui/renderers/ShadowCasterRenderer.cs:67-90` | `Ground`, `TreeOnGrass` | статические тени леса |
| `src/ui/renderers/HumidityOverlayRenderer.cs:40-145` (`Refresh`) | `HumidityMap` | оверлей влажности (троплинг 1с) |
| `src/ui/renderers/FertilityOverlayRenderer.cs` | `FertilityMap` | оверлей плодородия |
| `src/ui/renderers/GroundItemRenderer.cs` | `GroundItemManager.SnapshotQueue` | предметы на земле |
| `src/ui/renderers/CropRenderer.cs` | `CropGrowthManager.SnapshotQueue` | посевы по стадиям |
| `src/ui/renderers/StockpileItemRenderer.cs` | `StockpileManager.SnapshotQueue` | содержимое складов |
| `src/ui/renderers/FarmZoneRenderer.cs` / `DesignationRenderer.cs` | `ZoneManager`, `FarmJobManager`, `WorkProgressTracker` | зоны и метки работ |
| `src/ui/renderers/TerrainTileLayer.cs:17-92` | Godot `TileMapLayer` | автотайлинг (стены/горы) — не читает `MapData` напрямую |
| `src/simulation/AgentSimulationThread.cs:97-121` (`EnsureEnvironmentalMasks`) | `Ground` (маска гор), `TreeOnGrass` (`:462-468`), `FarmJobManager.FillGardenBedFlatMask:469` | маски для тиков почвы; `_environmentalMasksDirty` — ручная инвалидация (`:95`) |
| `AgentSimulationThread.cs:435-498` | `HumidityMap`, `FertilityMap`, `Ground` | тики почвы |
| `src/simulation/pathfinding/HierarchicalPathfinder.cs:413` | `SolidWalls`, `Ground` | иерархический A* |
| `src/simulation/agents/FlowFieldManager.cs:213` | `Ground`, `SolidWalls` | flow-field |
| `src/simulation/agents/AgentMovementService.cs:218,449-458,491-493` | `Ground`, `SolidWalls` | движение/выталкивание |
| `src/simulation/jobs/NeedsJobSystem.cs:212-214`, `WanderJobSystem.cs:34` | `SolidWalls`, `TreeOnGrass`, `Ground` | выбор цели/блуждание |
| `src/simulation/jobs/JobValidator.cs:326`, `JobBroker.cs:89-95` | `Ground`, `TreeOnGrass`, `StoneOnGrass` | валидация задач/поиск места |
| `src/simulation/building/TileClearanceService.cs:42-125` | `TreeOnGrass`, `StoneOnGrass`, `SolidWalls`, `Ground`, `BlueprintManager` | расчистка под грядку/стройку |
| `src/core/world/MapData.cs:67-100` | bounds-safe геттеры (`GetBiome`, `GetGroundVariant`, `GetTreeVariant`, `HasStone`) | API чтения |

Замечание: `SimulationContext` читается **и на 10k агентов, и из параллельных воркеров** —
любая синхронная запись зеркала прямо из джоб-хендлеров идёт из симуляционного/параллельного потока,
что запрещено для мирового слоя (см. §6.7).

---

## 4. Существующая синхронизация с «мировым» состоянием

Что уже есть:

| Механизм | Файл:строка | Замечание |
|---|---|---|
| Словарь видов | `WorldDeltaKind.cs:4-103` | `Wall 0x77`, `Building 0x01`, `MinedStone 0x02`, `CutTree 0x03`, `Stockpile 0x04`, `Crop 0x05`; планы `0x11-0x16`; `DoneFor/PlanFor`, `BlockingKinds`, `IsPlacementKind` |
| Контракт записи | `IBlockWorld.cs:10-23` (+`IWallWorld`), `IWorldAccess.cs:38-44`, `ChunkWorldAccess.cs:39-58` | разреженные метки по загруженным чанкам |
| Хранилище на чанк | `ChunkMutations.cs:11-129` | `Dictionary<(localIndex,kind), byte[]>`; `Encode/Apply` → `WorldChunkDeltaRecord` (персистентность при выгрузке) |
| Менеджер | `StreamingWorldManager.cs:59-193` | `CanPlace/IsBlocked/TryPlaceBlock/TryRemoveBlock/TryReplaceBlock`; `IsBlocked` возвращает `null` для незагруженного чанка (`:63-72`) |
| Вид | `StreamingWorldView.cs:277-335` | **`AddBlock` требует загруженный чанк** (`:282-283`), `RepaintCell`; `:341-378` очередь чертежей; `:452-460` `BlockCount` |
| Адаптер инструментов | `StreamingToolPlacement.cs:44-113` | остров исключён (`:104-106`) |
| Правила | `WorldGameRules.cs:37-184` | гейты только на загруженных чанках (`IsLoadedAt`) |
| Работы мира | `WorldJobBoard.cs:79-86` (`Complete` → `TryReplaceBlock(Plan→Done)`), `WorldJobTicker.cs:15`, `WorldPathFinder.cs` | **отдельный** отряд агентов вне островной симуляции |
| Персистентность | `WorldSaveCoordinator.cs:133` `PersistCheckpoint(gameTime,startTile)`, `WorldManifestStore`, `WorldChunkStore` | только манифест (сид/версия/время/стартовый тайл) + чанк-дельты |
| Рендер мира | `StreamingWorldView.cs:1580-1620` + `IslandDecorPass:1480-1502` | **уже учитывает `CutTree`/`MinedStone`-метки при отрисовке клеток острова** — это готовый шов для зеркалирования |

Чего НЕТ:

* Ни одного вызова `TryPlaceBlock/AddBlock` из островной логики (`TreeChoppingJobHandler`,
  `MiningJobHandler`, `StructureManager`, `ConstructionManager`, `FarmJobManager`, `StockpileManager`,
  `GroundItemManager`, `CropGrowthManager`) — grep по `AddBlock|RemoveBlock|TryPlaceBlock` даёт только
  `StreamingToolPlacement`, `StreamingWorldManager`, `StreamingWorldView`, `WorldJobBoard`, тесты.
* Видов для предметов на земле, почвы (влага/плодородие), стадий культур, зон — в `WorldDeltaKind` нет.
* Обратного чтения: `MapRenderer` не читает `_blocks` вообще → дельты, поставленные людьми мира
  внутри острова, островным рендером/симуляцией **не видны** (кроме подложки `StreamingWorldView`).
* Персистентности острова (`StreamingWorldView.IslandDecorPass` генерирует декор заново, без учёта рубки).

---

## 5. Ответы на отдельные вопросы

### 5.1 Что в `MapData` реально НЕвосстановимо из сида

Восстановимо из сида (`seed`+`generatorVersion`+`origin`): `Ground`, `Biome`, `Variant`, `TreeVariant`,
а также **исходные** значения `TreeOnGrass`/`StoneOnGrass` и **исходные** (сгенерированные) `Humidity`/`Fertility`.

Обязательно персистить (иначе потеряется):

1. **Множество срубленных деревьев** — `TreeOnGrass=false` после `TreeChoppingJobHandler.cs:79`.
   Обратное «дерево выросло» отсутствует, значит каждое `false` относительно генерации — информация.
2. **Множество добытых россыпей** — `StoneOnGrass=false` после `MiningJobHandler.cs:87`.
3. **Динамика влажности** `HumidityMap._cur` (тик `AgentSimulationThread.cs:477` плотно зависит от
   сезона, игрового времени, маски ферм и порядка свопов) — из сида даёт только базовую заливку.
4. **Динамика плодородия** `FertilityMap._cur` + `DrainAfterHarvest` (`CropGrowthManager.cs:174`) —
   зависит от того, где игрок разбил грядки и сколько собрал урожая.
5. **Стартовая поляна** — формально детерминирована, но **расходится** с `TerrainAt` мира
   (`UnifiedWorldGenerator.cs:584` против `WorldCellQuery.TerrainAt`) ⇒ без персиста/явного правила
   мир-дельта-гейты будут считать эти клетки водой/горой.

Вне `MapData`, но той же природы (тоже обязательно персистить при унификации): стены/мебель/станки
(`StructureManager._structures` + `SolidWalls`), площадки и чертежи (`ConstructionManager._sites`),
грядки (`FarmJobManager._markedPlots/_completedBeds`), посевы и стадии (`CropGrowthManager._crops`),
склады и содержимое (`StockpileManager._zones/_storage`), **предметы на земле** (`GroundItemManager._groundItems`),
зоны ферм/бригад (`ZoneManager._zones`), метки рубки/добычи (`TreeJobManager`/`StoneJobManager`).

### 5.2 Минимальный список точек внедрения зеркалирования

Единого фасада **нет**. Его надо ввести: `IIslandMutationSink` (или `IslandMutationJournal`) —
единственный канал записи островных мутаций, и `IslandDeltaMirror`, превращающий вызовы фасада в
`WorldDeltaKind`-записи + островной override-стор (не в чанк-стриминг, см. §6.1).

Минимум точек вызова фасада (11) — ровно там, где мутация реально происходит:

| # | Точка | Что зеркалим |
|---|---|---|
| 1 | `TreeChoppingJobHandler.cs:79` | `CutTree` (факт рубки) |
| 2 | `MiningJobHandler.cs:87` | `MinedStone` |
| 3 | `MapRenderer.cs:1341-1364` | убрать дубли либо перевести на чтение фасада (не писать в `_pendingMapData`) |
| 4 | `StructureManager.cs:112` (`AddStructure`) | `Wall`/`Building` (+payload материала) |
| 5 | `StructureManager.cs:151`/`:199` (`RemoveStructure`/`ClearAll`) | снятие `Wall`/`Building` |
| 6 | `ConstructionManager.cs:114-226` (`StartSitesBatch`) | `WallPlan`/`BuildingPlan`/`StockpilePlan` |
| 7 | `ConstructionManager.cs:250-263` (`NotifyTreeCleared/StoneCleared`) + `:428` (`CompleteConstruction`) | снятие/перевод `Plan→Done` |
| 8 | `FarmJobManager.cs:334-348` (`CompletePlot`) + `:22-125` (маркировка) | `Crop`/`CropPlan` |
| 9 | `StockpileManager.cs:131-322` (add/remove зон) | `Stockpile`/`StockpilePlan` |
| 10 | `GroundItemManager.cs:119-222` + `:310-344` | **нужен новый вид** `ItemPile` (в `WorldDeltaKind` отсутствует) |
| 11 | `CropGrowthManager.cs:34-50,148-202,174` | **нужен новый вид** для стадии культуры и `SoilFertility` |

Если переиспользовать **существующие** виды и не расширять `WorldDeltaKind`, полное зеркалирование
невозможно: пункты 10-11 (и зоны) невыразимы. Для 6 существующих видов достаточно точек 1-9
(≈9 мест) при условии, что фасад введён.

**Альтернатива без правки хендлеров** (дешевле, но неполно): подписаться на уже имеющиеся события
менеджеров (см. §5.3) — тогда 6 из 11 доменов зеркалятся централизованно в 1 файле-подписчике,
а `GroundItemManager`, `CropGrowthManager`, `HumidityMap`, `FertilityMap`, `ZoneManager` всё равно
требуют ручной инструментации (событий нет). Плюс события идут через `Callable.From(...).CallDeferred()`
(`TreeJobManager.cs:27`, `FarmJobManager.cs:59`) — то есть на main thread и **после** фактической
мутации, что для зеркала даже удобнее (нет гонок с сим-потоком).

Оптимальный минимум: **1 новый фасад + 9-11 точек вызова + 2-4 новых `WorldDeltaKind`**
(`ItemPile`, `SoilState`, `CropStage`, `WorkZone`/`FarmZone`) + островной override-стор.

### 5.3 Существующие события/хуки для централизованного перехвата

Есть события (66 объявлений `public event Action`, но пригодны не все):

* Рубка: `TreeJobManager.OnTreeChopped` (`TreeJobManager.cs:18`), `+Marked/Unmarked` (`:14-17`) — **есть**.
* Добыча: `StoneJobManager.OnStoneMined` (`StoneJobManager.cs:23`) — **есть**.
* Структуры: `StructureManager.OnStructureAdded/OnStructureRemoved/OnWallTilesUpdated` (`StructureManager.cs:22-24`) — **есть**.
* Стройка: `ConstructionManager.OnSiteAdded/OnSitesBatchAdded/OnSiteRemoved/OnSitesBatchRemoved/OnSiteCompleted/OnProgressStageChanged` (`ConstructionManager.cs:24-29`) — **есть**.
* Чертежи (фасад): `BlueprintManager.OnBlueprint*` (`BlueprintManager.cs:17-21`) — дублируют предыдущие.
* Ферма: `FarmJobManager.OnPlotMarked/OnPlotsBatchMarked/OnPlotUnmarked/OnPlotsBatchUnmarked/OnPlotCompleted` (`FarmJobManager.cs:16-20`) — **есть**.
* Склады: `StockpileManager.OnZoneTileAdded/OnZoneTileRemoved/OnZoneTilesBatchAdded/OnZoneTilesBatchRemoved/OnItemCountChanged` (`StockpileManager.cs:51-53,200,325`) — **есть**.
* Здания (фасад): `BuildingManager.OnBuildingPlaced/OnBuildingRemoved` (`BuildingManager.cs:15-16`).
* Зоны: `ZoneManager.OnZonesUpdated/OnZoneHovered/OnZoneSelected/OnZoneDeselected/OnAutoPlantChanged` (`ZoneManager.cs:41-45`) — только агрегат, без координат диффа.
* Рендер: `MapRenderer.OnMapApplied` (`MapRenderer.cs:239`), `OnMapModeChanged` (`:232`) — точка «карта сменилась», обязательна для инвалидации зеркала.

Чего нет (нужна ручная инструментация):

* `CropGrowthManager` — **событий нет вообще**, только `SnapshotQueue` (`CropGrowthManager.cs:28`).
* `GroundItemManager` — **событий нет**, только `SnapshotQueue` (`GroundItemManager.cs:75`); `OnItemCountChanged` живёт в `StockpileManager`.
* `HumidityMap`/`FertilityMap` — чистый C# без событий (и без Godot API) — тик `:233`/`:170`.
* `ZoneManager` — только `OnZonesUpdated` без списка изменённых клеток.

`SimEvents` (`src/core/common/SimEvents.cs:14`) — это **телеметрия/профайлер** (`Record/Mark/Count/Series`,
кольцевой буфер + CSV/Chrome-trace), а **не** хук мутаций: подписки/интерсепта нет, `Reset()` (`:232`) только чистит.
Использовать его как перехватчик нельзя; он годится только для наблюдаемости зеркала.

### 5.4 Персистентность острова

`WorldSaveCoordinator` сохраняет только `world.manifest` (сид, версия генератора, `WorldGameTime`,
стартовый тайл) + чанк-дельты (`WorldChunkStore`). **Островное состояние не сохраняется нигде** —
`MapData` пересоздаётся через `GenerateColonyWindow` при `OnMapApplied` (`Main.cs:281,343`),
а все менеджеры очищаются в `ResetColonyState` (`Main.cs:545-570`). То есть на сегодня остров —
«одна сессия»: срубленное дерево, стена, грядка и предмет на земле исчезают при регенерации/сбросе.

---

## 6. Риски при унификации (мир-дельта как источник истины)

1. **Загруженность чанков.** `StreamingWorldView.AddBlock` (`:282-283`) и
   `StreamingWorldManager.TryPlaceBlock` (`:137-141`) работают только с загруженными чанками;
   `IsBlocked` явно возвращает `null` для незагруженного (`:63-72`). Остров — 64×64 = 4096 чанков,
   окно стриминга конечно ⇒ мутации на дальнем краю острова будут **молча отброшены**.
   Нельзя писать остров в чанк-стриминг: нужен островной override-стор (постоянный, как
   `ChunkMutations`, но не привязанный к резидентности).
2. **Гейт «трава из генератора» vs поляна острова.** `WorldGameRules.IsReadyBase` (`:180-183`) и
   `StreamingWorldManager.CanPlace` (`:59-60`) спрашивают `WorldCellQuery.TerrainAt`;
   стартовая поляна (`UnifiedWorldGenerator.cs:584`) может стоять на воде/горах генератора ⇒
   все постройки поляны отбраковываются мир-дельта-гейтом.
3. **Односоставность клетки.** `BlockingKinds` (`WorldDeltaKind.cs:87-91`) запрещает две постоянные
   сущности в клетке, остров же штатно держит чертёж + дерево/камень (расчистка,
   `ConstructionManager.cs:124-125,164-166`) и отдельно — `CutTreePlan`/`MinedStonePlan`, которые
   намеренно НЕ блокируют (`WorldDeltaKind.cs:49-52`, комментарий `WorldJobBoard.cs:19-21`).
   Прямой перенос правил сломает пайплайн расчистки.
4. **Модель данных не покрывает остров.** Нет видов для: предметов на земле (Log/Stone/Grain),
   стадий культур, влаги/плодородия, зон ферм/бригад. `Building` объявлен «opaque» (`WorldDeltaKind.cs:12`) —
   материал стены (дерево/камень, `WallMaterialRegistry`) в дельту не влезает.
5. **Двойной рендер.** `MapRenderer` рисует остров сверху, `StreamingWorldView` — подложку того же
   региона из генератора (`StreamingWorldView.cs:1580-1620`, выравнивание `IslandDecorPass:1480`).
   Зеркало без обратного чтения даст рассинхрон/двоение дерева уже в первом кадре; обратное чтение
   (мир → `MapData`) сейчас отсутствует вовсе.
6. **Идемпотентность/ложные `false`.** `AddBlock` возвращает `false`, если метка уже есть (`:286-287`);
   `TryPlaceBlock` — если занято (`:137-141`); `TryReplaceBlock` откатывает при сбое (`:185-189`).
   Зеркало обязано быть идемпотентным и не входить в цикл ретраев.
7. **Потоки.** Островные мутации идут из `Commit` джоб-хендлеров, который на Phase3b выполняется
   **параллельно** (`TreeChoppingJobHandler.cs:58`, комментарий про `ParallelRng` `MiningJobHandler.cs:95`).
   Мировой слой объявлен main-thread-only (`StreamingWorldView.cs:268-273`, `WorldJobTicker.cs:13`).
   Прямая запись из сима = гонка; нужна очередь, дренируемая на main thread.
8. **Производительность.** Массовые операции инструментов — до 250 000 клеток за драг
   (`WarehouseTool.cs:58`, `FarmingTool.cs:65`, `ZoneDraftTool`), и `MarkPlotsBatch`/`AddZoneTilesBatch`.
   Позаклеточный дельта-API (`Dictionary<(cell,kind),byte[]>` на чанк) на такие объёмы даст
   многомиллионные вставки и репинт-шторм (`RepaintCell` на каждую метку).
9. **Почва.** Динамика влаги/плодородия — 2 × `int[1M]` со свопом буферов (`HumidityMap.cs:338-339`,
   `FertilityMap.cs:225-226`) и `Parallel.For` (`FertilityMap.cs:220`). Сериализовать её по-клеточно
   в дельты нецелесообразно; разумнее оставить островным слоем с чекпоинтом массивов целиком.
10. **Абстракции без аналога.** `ZoneManager` (фермы-компоненты + work-зоны-бригады,
    `ZoneManager.cs:68-306`) и логистика складов (`StockpileManager.cs:677-798`) не выражаются
    клеточными метками — при унификации их придётся либо оставить «островными», либо вводить
    отдельные DTO-виды.
11. **Сброс/смена карты.** `ResetColonyState` (`Main.cs:545-570`) чистит менеджеры, но не зеркало.
    Без подписки на `MapRenderer.OnMapApplied` дельты прошлого сида «прилипнут» к новому
    (в мире манифест при несовпадении сида бэкапится и пересоздаётся — `WorldSaveCoordinator.cs:92-107`;
    зеркалу нужно то же поведение).
12. **Обходной путь без `ctx`.** `WallBuildManager.AddWall` (`:26-29`) → `StructureManager.AddStructure`
    **без** `ctx`, а вызывается из `MapRenderer.OnBlueprintCompleted:1327`. Сейчас спасает
    дублирующий вызов из `ConstructionJobHandler.cs:114`. При унификации точка зеркалирования
    «стена поставлена» может быть пропущена этим путём.

---

## 7. Быстрые факты для планирования

* Прямых писателей `MapData`: **12** (из них **6 — МУТАЦИИ**, 2 из них — нерабочие дубли в `MapRenderer`).
* Держателей островного состояния вне `MapData`: **10** (Tree/Stone/FarmJob, CropGrowth, Construction,
  Structure, GroundItem, Stockpile, Zone) + **3** адаптера (`WallBuild`/`Building`/`Blueprint`).
* Существующих `WorldDeltaKind`-видов: **6 готовых + 6 планов**; не хватает минимум **2-4**
  (`ItemPile`, `SoilState`, `CropStage`, зона).
* Мир-делта-хранилище привязано к резидентности чанка (`StreamingWorldView.cs:282-283`) —
  для острова нужен отдельный постоянный стор.
* Централизованных хуков от менеджеров: **есть у 6 доменов из 11**; у `CropGrowthManager`,
  `GroundItemManager`, `HumidityMap`, `FertilityMap` событий нет.
* `SimEvents` — телеметрия, не хук.
* Персистентности острова нет вообще (`WorldSaveCoordinator.cs:133` — только манифест мира + чанк-дельты).