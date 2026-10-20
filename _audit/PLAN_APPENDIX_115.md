---

## 115. Разрыв dependency-циклов: применённые интерфейсы (R7)

Дата: 2026-10-18. Автономный прогон (роли `@architect` + `@developer`).
Основание: Tarjan SCC анализатора (`DependencyGraphVisualizer --cli`), вывод владельца: **4 цикла**.
Результат: **1 цикл**. Полный реестр правок — `_audit/FIXES_APPLIED.md`, замеры — `_audit/cycles_after/`.

### 115.1. Цикл #1 — коллизия имён ФАЙЛОВ, интерфейс не нужен

`src/core/world/climate/WorldClimateParams.cs` и `src/core/world/streaming/generation/WorldClimateParams.cs`
имели одинаковый стем; анализатор склеивал узлы по имени → фантомные рёбра внутрь SCC.

Меры:
1. Удалён мёртвый `[Obsolete] public static class WorldClimateParams` (шим-обёртка).
2. Файл переименован в `WorldMacroClimateParams.cs` (+ `.cs.uid`) — по собственному правилу
   PLAN.md «файл должен называться как класс» и в духе `R2b`.

> **Интерфейс здесь был бы ВРЕДЕН.** Тип — `readonly record struct WorldMacroClimateParams`,
> он используется со `stackalloc WorldClimateParams[...]` в `UnifiedWorldGenerator.cs`
> (нулевые аллокации). Любой интерфейс превратил бы его в ссылочный тип.

### 115.2. Цикл #2 — `WorldAgentRenderer` ↔ `WorldJobTicker`

```csharp
// NEW: src/ui/streaming/IWorldAgentSource.cs
public interface IWorldAgentSource { IReadOnlyList<WorldAgent> Agents { get; } }
```
- `WorldJobTicker : Node, IWorldAgentSource`
- `WorldAgentRenderer._ticker` и параметр `Bind(...)` → `IWorldAgentSource`
- **Обязательно:** `WorldAgentState` + `WorldAgent` вынесены в новый `src/ui/streaming/WorldAgent.cs`.
  Без выноса DTO файловое ребро `WorldAgentRenderer.cs → WorldJobTicker.cs` сохранялось и цикл не рвался.

### 115.3. Цикл #3 — `FarmController` ↔ `WorkZoneController`

```csharp
// NEW: src/ui/controllers/IZoneWindow.cs   (namespace Game.UI)
public interface IZoneWindow { bool IsOpen { get; } void Close(); }
public interface IFarmJobDisplayHost { void ShowForFarmZone(int farmZoneId); }

// NEW: src/ui/controllers/ZoneWindowRegistry.cs — листовой файл, без зависимостей
public static class ZoneWindowRegistry
{
    public static IZoneWindow FarmWindow { get; set; }
    public static IZoneWindow WorkWindow { get; set; }
    public static IFarmJobDisplayHost FarmJobDisplay { get; set; }
}
```
- `FarmController : Control, IZoneWindow`; `WorkZoneController : Control, IZoneWindow, IFarmJobDisplayHost`.
- Регистрация в `_Ready`, снятие в `_ExitTree` строго через `ReferenceEquals` (иначе статика удержит
  освобождённую Godot-ноду).
- Заменены 4 перекрёстных обращения `X.Instance` (`FarmController.OnZoneSelected/OnJobDisplayPressed`,
  `WorkZoneController` ×2).

### 115.4. Цикл #4 — две независимые ветви

**(a)** `SelectTool` → `HUDController` → `PlayerInteractionManager` → `SelectTool`:

```csharp
// NEW: src/ui/ICellProbeOverlay.cs
public interface ICellProbeOverlay { void ShowHumidityAt(int x, int y); void ShowFertilityAt(int x, int y); }
```
- `SelectTool.BindCellProbe(ICellProbeOverlay)`; `OnHover` больше не знает про `HUDController`.
- `PlayerInteractionManager.SetCellProbe(ICellProbeOverlay)` **кэширует** пробу и переустанавливает её
  в `_Ready`/`Initialize` — вызов до создания `SelectTool` безопасен (порядок `_Ready` не гарантирован).
- `HUDController : Control, ICellProbeOverlay, IDraftCoordinator`; вызов `_interactionManager.SetCellProbe(this)`.

**(b)** `HUDController` ↔ `BuildMenuController`:

```csharp
// NEW: src/ui/controllers/IDraftCoordinator.cs   (namespace Game.UI)
public interface IDraftCoordinator
{
    bool IsDrafting { get; }
    void StartZoneDraft(string zoneId);
    void SetDraftDoorMode(bool enabled);
    void StopDraftDrawing();
    void ConfirmDraft();
    void CancelDraft();
    void DiscardDraft();
    void SetDraftMaterial(string materialId);
}
```
- `BuildMenuController.BindDraftCoordinator(IDraftCoordinator)`; все 8 обращений `HUDController.Instance`
  заменены на `_draft` (тип локальной переменной в обработчиках — интерфейсный).
- Проверка `control is BuildMenuController` в HUD остаётся: она однонаправленная и цикла не создаёт.

### 115.5. Проверка

- `dotnet build Game.csproj` — **0 ошибок / 0 предупреждений** после каждого шага.
- `tests/run_test.gd` → exit 0; `tests/run_adversarial_construction_test.gd` → exit 0.
- Рантайм (`scenes/main/Main.tscn`): мир сгенерирован, агенты инициализированы, ошибок C# нет.
  - #4b: `BuildMenuController.Open("farming","Ферма")` → `HUDController.IsDrafting` **false → true**.
  - #4a: `MapMode = Humidity` + hover `SelectTool` → тултип **«104% норма»**.
- Анализатор: **4 → 1** цикл; из SCC ушли оба `WorldClimateParams.cs` (81 → 79 узлов).

### 115.6. Остаток (R8, следующий шаг)

Оставшийся SCC — **79 файлов, 395 внутренних рёбер, 28 взаимных пар** (`_audit/cycles_after/mutual_pairs.txt`).
Это не фантом: `src/simulation/**` (items ↔ jobs ↔ structures ↔ farming ↔ scheduling) взаимно ссылается,
плюс `UnifiedWorldGenerator` ↔ climate-файлы через `using` собственного namespace.
Направление работ: (1) `Game.Core` не должен знать `Game.Simulation` — инвертировать через события/интерфейсы
`IJobBoard`, `IStockpileReader`; (2) разорвать `GroundItemManager` ↔ `StockpileManager` ↔ `JobBroker`
владельцем-координатором; (3) `WorldRegions` вынести в лист (R2b).
Одним интерфейсом этот узел не развязывается — нужна поэтапная декомпозиция слоёв.

### 115.7. Прочие применённые фиксы (не архитектура)

W-01 (P0, утечка reservation/CTS в `WorldChunkStreamer`), W-02 (мягкая деградация снапшота вместо
`InvalidDataException`), W-03 (лживый комментарий про fsync), C-01 (`OnStoneMined` подписка перенесена
из `ApplyMap` в `_Ready`), C-02 (крестик Work-зоны: нода называется `esc`), C-03 (`AgentRenderer.Initialize`
не сбрасывал снапшот-флаги → агенты летели из (0,0)), `Instance = null` в `_ExitTree` для
`FarmController`/`WorkZoneController`, удаление 4 пустых файлов. Реестр — `_audit/FIXES_APPLIED.md`.