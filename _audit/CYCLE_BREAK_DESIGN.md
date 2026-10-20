# Разрыв циклических зависимостей — верификация и точный код интерфейсов

> Проект: `D:\bebebe-main (1)\bebebe-main` (Godot 4.7, C# / .NET 8)
> Источник списка циклов: вывод `DependencyGraphVisualizer.exe --cli`
> Статус документа: все 4 «цикла» проверены по фактическому коду (не по отчёту анализатора).

---

## 0. TL;DR — вердикты по 4 циклам

| # | Вердикт | Реальное ребро (доказательство) | Рекомендация анализатора |
| :- | :--- | :--- | :--- |
| **1** | ❌ **ЛОЖНЫЙ ЦИКЛ (артефакт)** — 80 файлов в одном SCC невозможно объяснить реальным циклом | `UnifiedWorldGenerator.cs` → `WorldClimateParams.cs` — это **DTO-структура с нулём исходящих рёбер** (`src/core/world/streaming/generation/WorldClimateParams.cs` не имеет ни одного `using` и состоит только из примитивов) | ❌ **Неверна.** Интерфейс тут не нужен и вреден |
| **2** | ✅ **РЕАЛЬНЫЙ** (2 файла) | `WorldAgentRenderer.cs:27,29` → `WorldJobTicker.cs` **и** `WorldJobTicker.cs:115` → `WorldAgentRenderer.cs` | ✅ Верна |
| **3** | ✅ **РЕАЛЬНЫЙ** (2 файла) | `FarmController.cs:222,233` → `WorkZoneController.cs` **и** `WorkZoneController.cs:237,313` → `FarmController.cs` | ✅ Верна по сути |
| **4** | ✅ **РЕАЛЬНЫЙ, но это ДВА независимых цикла** | (a) `SelectTool.cs:31` → `HUDController` → `PlayerInteractionManager.cs:28` → `SelectTool`; (b) `HUDController.cs:535` → `BuildMenuController` → `BuildMenuController.cs:177,250,275,288,306,312,320,407` → `HUDController` | ❌ **Ребро указано неверно** — это фантом из комментария (`HUDController.cs:705`) |

### Отдельная находка — реальный баг (не архитектура)

`FarmController._ExitTree()` (`FarmController.cs:411-416`) и `WorkZoneController._ExitTree()`
(`WorkZoneController.cs:455-461`) **не обнуляют** статический `Instance`, тогда как
`BuildMenuController.cs:472` и `HUDController.cs:1477` — обнуляют. После выгрузки сцены
статика продолжает ссылаться на освобождённый Godot-объект. Подробности — §5.

---

## 1. Цикл #1 — ЛОЖНЫЙ. Причина и настоящий фикс

### Доказательство, что цикл фальшивый

`src/core/world/streaming/generation/WorldClimateParams.cs` — целиком:

```csharp
namespace Game.Core.WorldStreaming;

public readonly record struct WorldClimateParams(
    ushort ElevationQ16, ushort ContinentalnessQ16, /* ... ещё 16 полей ... */ ushort RiverDensityQ16)
{
    public const ushort Q16Max = ushort.MaxValue;
    public static WorldClimateParams DefaultTemperate => new(...);
}
```

В файле **нет ни одного `using`**, ни одного обращения к другому типу проекта. Это чистый
value-DTO. Узел с нулём исходящих рёбер **по определению не может входить в цикл** —
он не может «дойти обратно». Значит, «цикл #1» — ошибка построения графа, а не архитектуры.

### Настоящая причина — дубль простого имени типа

В проекте **два разных типа с одинаковым именем `WorldClimateParams`**:

| Файл | Тип | Namespace |
| :--- | :--- | :--- |
| `src/core/world/streaming/generation/WorldClimateParams.cs` | `readonly record struct WorldClimateParams` | `Game.Core.WorldStreaming` |
| `src/core/world/climate/WorldClimateParams.cs:197-202` | `[Obsolete] public static class WorldClimateParams` | `Game.Core.WorldClimate` |

Анализатор адресует узлы по простому имени → два несвязанных типа **склеились в один узел**.
Склеенный узел унаследовал рёбра устаревшей оболочки (она тянет `WorldMacroClimateParams`,
`ContinentShaper`, климат и т.д.), из-за чего SCC раздулся до 80 файлов.

### Фикс — НЕ интерфейс, а устранение коллизии имён

Устаревшая оболочка не используется нигде (проверено: поиск `WorldClimate.WorldClimateParams`
по всему репозиторию — 0 совпадений; код проекта использует алиасы `MacroClimate` /
`LocalClimate`). Удалить строки 194-202 файла `src/core/world/climate/WorldClimateParams.cs`:

```csharp
// УДАЛИТЬ целиком (строки 194-202):
/// <summary>
/// Обратная совместимость для методов-архетипов и FromSeed.
/// </summary>
[Obsolete("Используйте WorldMacroClimateParams во избежание коллизии имен с WorldStreaming.WorldClimateParams.")]
public static class WorldClimateParams
{
    public static WorldMacroClimateParams FromSeed(ulong worldSeed) => WorldMacroClimateParams.FromSeed(worldSeed);
    public static WorldMacroClimateParams.WorldArchetype GetArchetype(ulong worldSeed) => WorldMacroClimateParams.GetArchetype(worldSeed);
}
```

> ⚠️ Сам файл удалять нельзя: в нём выше (строки 1-192) живёт рабочий `WorldMacroClimateParams`.
> Удаляется только хвостовая `[Obsolete]`-оболочка.

После этого «цикл #1» исчезает из графа, потому что исчезает склеенный узел.

> **Почему интерфейс здесь вреден:** `WorldClimateParams` — `readonly record struct`,
> используемая в горячих циклах генерации (`UnifiedWorldGenerator.cs:159` —
> `stackalloc WorldClimateParams[GridSide * GridSide]`, далее bilinear-интерполяция).
> Замена на `interface` означает боксинг на каждой итерации и потерю `stackalloc`/`in`-семантики.

---

## 2. Цикл #2 — РЕАЛЬНЫЙ. Точный код

### Реальные рёбра

```csharp
// src/ui/streaming/WorldAgentRenderer.cs:27,29
private WorldJobTicker _ticker;
public void Bind(WorldJobTicker ticker) { _ticker = ticker; QueueRedraw(); }

// src/ui/streaming/WorldJobTicker.cs:115
public WorldAgentRenderer RenderTarget { get; set; }
```

Рендереру от тикера нужен **ровно один член** — `Agents` (`WorldAgentRenderer.cs:38`).

### ⚠️ Критичная тонкость

Одного интерфейса **недостаточно**: типы `WorldAgentState` (строки 9-23) и `WorldAgent`
(строки 25-46) объявлены **внутри** `WorldJobTicker.cs`. Граф анализатора — файловый,
поэтому рендерер продолжит ссылаться на файл `WorldJobTicker.cs` и цикл сохранится.
**Обязательно вынести DTO в отдельный файл.**

### Файл 1 (новый): `src/ui/streaming/IWorldAgentSource.cs`

```csharp
using System.Collections.Generic;

namespace Game.UI.Streaming;

/// <summary>
/// Источник людей мира для рендера. Узкий контракт: рендереру нужен только список агентов,
/// поэтому он не зависит от конкретного тикера. Реализуется <see cref="WorldJobTicker"/>.
/// Разрывает цикл WorldAgentRenderer ↔ WorldJobTicker.
/// </summary>
public interface IWorldAgentSource
{
	/// <summary>Люди мира (для рендера). Коллекция read-only для потребителя.</summary>
	IReadOnlyList<WorldAgent> Agents { get; }
}
```

### Файл 2 (новый): `src/ui/streaming/WorldAgent.cs` — сюда ПЕРЕЕЗЖАЮТ DTO из `WorldJobTicker.cs`

```csharp
using System.Collections.Generic;
using Game.Core.WorldStreaming.Integration;

namespace Game.UI.Streaming;

/// <summary>Состояние человека мира (§29).</summary>
public enum WorldAgentState
{
	/// <summary>Свободен: ищет задачу или уходит домой.</summary>
	Idle,

	/// <summary>Идёт к чертежу по найденному пути.</summary>
	Walking,

	/// <summary>Работает на клетке.</summary>
	Working,

	/// <summary>Переброска по морю: цель на суше, но отрезана водой (лодок пока нет).</summary>
	SeaCrossing
}

/// <summary>Человек мира: позиция в клетках (центр клетки = +0.5), задача и путь к ней.</summary>
public sealed class WorldAgent
{
	public float X;
	public float Y;
	public WorldAgentState State;
	public WorldPlan Job;
	public float WorkLeft;
	public float IdleTime;

	/// <summary>Найденный путь (клетки), обход воды и гор.</summary>
	public readonly List<(long X, long Y)> Path = new(256);

	/// <summary>Текущая точка пути.</summary>
	public int PathIndex;

	/// <summary>Путь ещё не считан (считается по бюджету — не каждый кадр).</summary>
	public bool NeedsPath;

	/// <summary>Остаток переброски по морю, сек.</summary>
	public float TravelLeft;
}
```

### Правки в существующих файлах

`src/ui/streaming/WorldJobTicker.cs`:
```csharp
// 1) удалить enum WorldAgentState (строки 9-23) и class WorldAgent (строки 25-46) — они переехали;
// 2) объявить реализацию интерфейса:
public sealed partial class WorldJobTicker : Node, IWorldAgentSource
{
	// строка 112 остаётся как есть — она уже удовлетворяет контракту:
	public IReadOnlyList<WorldAgent> Agents => _agents;

	// строка 115 остаётся как есть (обратное ребро ticker → renderer цикл не создаёт):
	public WorldAgentRenderer RenderTarget { get; set; }
```

`src/ui/streaming/WorldAgentRenderer.cs`:
```csharp
	// было: private WorldJobTicker _ticker;
	private IWorldAgentSource _ticker;

	// было: public void Bind(WorldJobTicker ticker)
	public void Bind(IWorldAgentSource source)
	{
		_ticker = source;
		QueueRedraw();
	}
```

**Результат по рёбрам:** `WorldJobTicker → WorldAgentRenderer`, `WorldAgentRenderer → IWorldAgentSource.cs + WorldAgent.cs`.
Обратного пути в `WorldJobTicker.cs` нет — цикл разорван.

---

## 3. Цикл #3 — РЕАЛЬНЫЙ. Точный код

### Реальные рёбра

```csharp
// FarmController.cs:220-225  (Farm → Work)
var zone = FarmZoneManager.Instance.SelectedZone;
var work = WorkZoneController.Instance;
if (work == null) return;
Close();
work.ShowForFarmZone(zone.Id);

// FarmController.cs:233-235  (Farm → Work)
var work = WorkZoneController.Instance;
if (work != null && work.IsOpen) work.Close();

// WorkZoneController.cs:237-239 и 313-315  (Work → Farm)
var farmCtrl = FarmController.Instance;
if (farmCtrl != null && farmCtrl.IsOpen) farmCtrl.Close();
```

### Что реально нужно каждой стороне

| Потребитель | Нужно | Тип |
| :--- | :--- | :--- |
| `FarmController` | `IsOpen`, `Close()`, `ShowForFarmZone(int)` | Work-окно |
| `WorkZoneController` | `IsOpen`, `Close()` | Farm-окно |

### Файл 1 (новый): `src/ui/controllers/IZoneWindow.cs`

```csharp
namespace Game.UI;

/// <summary>
/// Узкий контракт окна зоны (ферма / склад): взаимное закрытие окон без прямой зависимости
/// FarmController ↔ WorkZoneController. Реализуется обоими контроллерами.
/// Разрывает цикл #3.
/// </summary>
public interface IZoneWindow
{
	/// <summary>Окно сейчас видимо игроку.</summary>
	bool IsOpen { get; }

	/// <summary>Закрыть окно (идемпотентно).</summary>
	void Close();
}

/// <summary>
/// Хост окна JobDisplay для Farm-зоны (§20.6). Реализуется WorkZoneController,
/// потребляется FarmController. Отдельный интерфейс — потому что это не «окно», а действие.
/// </summary>
public interface IFarmJobDisplayHost
{
	/// <summary>Открыть JobDisplay для Farm-зоны по её id.</summary>
	void ShowForFarmZone(int farmZoneId);
}
```

### Файл 2 (новый): `src/ui/controllers/ZoneWindowRegistry.cs`

Листовой реестр: не ссылается ни на один контроллер, поэтому разрывает зависимость.

```csharp
namespace Game.UI;

/// <summary>
/// Реестр окон зон. Листовой файл без зависимостей: контроллеры видят только интерфейсы
/// и не ссылаются друг на друга напрямую. Регистрация — в _Ready, снятие — в _ExitTree
/// (иначе статика удержит освобождённый Godot-объект, см. §5).
/// </summary>
public static class ZoneWindowRegistry
{
	/// <summary>Окно Farm-зоны (регистрирует FarmController).</summary>
	public static IZoneWindow FarmWindow { get; set; }

	/// <summary>Окно Work-зоны (регистрирует WorkZoneController).</summary>
	public static IZoneWindow WorkWindow { get; set; }

	/// <summary>Хост JobDisplay для Farm-зоны (регистрирует WorkZoneController).</summary>
	public static IFarmJobDisplayHost FarmJobDisplay { get; set; }
}
```

### Правки в существующих файлах

`src/ui/controllers/FarmController.cs`:
```csharp
// объявление класса
public partial class FarmController : Control, IZoneWindow

// _Ready (строка ~44): после Instance = this;
ZoneWindowRegistry.FarmWindow = this;

// _ExitTree (строка ~411): ДО base._ExitTree()
if (ReferenceEquals(ZoneWindowRegistry.FarmWindow, this))
    ZoneWindowRegistry.FarmWindow = null;
Instance = null;                     // ← попутный фикс реального бага, см. §5

// OnJobDisplayPressed (строки 220-225) — было обращение к WorkZoneController.Instance:
var zone = FarmZoneManager.Instance.SelectedZone;
if (zone == null) return;
var host = ZoneWindowRegistry.FarmJobDisplay;
if (host == null) return;
Close();
host.ShowForFarmZone(zone.Id);

// OnZoneSelected (строки 233-235):
var work = ZoneWindowRegistry.WorkWindow;
if (work != null && work.IsOpen)
    work.Close();
```

`src/ui/controllers/WorkZoneController.cs`:
```csharp
// объявление класса
public partial class WorkZoneController : Control, IZoneWindow, IFarmJobDisplayHost

// _Ready (строка ~51): после Instance = this;
ZoneWindowRegistry.WorkWindow = this;
ZoneWindowRegistry.FarmJobDisplay = this;

// _ExitTree (строка ~455): ДО base._ExitTree()
if (ReferenceEquals(ZoneWindowRegistry.WorkWindow, this))
    ZoneWindowRegistry.WorkWindow = null;
if (ReferenceEquals(ZoneWindowRegistry.FarmJobDisplay, this))
    ZoneWindowRegistry.FarmJobDisplay = null;
Instance = null;                     // ← попутный фикс реального бага, см. §5

// строки 237-239 и 313-315 — было FarmController.Instance:
var farm = ZoneWindowRegistry.FarmWindow;
if (farm != null && farm.IsOpen)
    farm.Close();
```

> **Почему реестр, а не события.** Оба класса уже объявляют и эмитят `OnOpened` / `OnClosed`
> (см. §7), но у них **ноль подписчиков**: взаимное закрытие — это *действие*, а не
> уведомление, поэтому событийной шины без координатора недостаточно. Реестр — минимальная
> честная развязка. Если хочется чистых событий, нужен третий участник-координатор,
> который подписан на `OnOpened` обоих окон и закрывает «соседа»; тогда `IZoneWindow`
> остаётся, а `ZoneWindowRegistry` заменяется на координатор.

---

## 4. Цикл #4 — РЕАЛЬНЫЙ, но состоит из ДВУХ циклов

### Почему рекомендованное ребро — фантом

Анализатор советовал резать `HUDController.cs → SelectTool.cs (namespace-using)`.
`SelectTool` встречается в `HUDController.cs` **единственный раз — в комментарии**:

```csharp
// src/ui/controllers/HUDController.cs:705
// SelectTool дёргает Show* только при смене клетки — перезапрашиваем сами
```

Резать это ребро бессмысленно. **Настоящий** цикл (a) замыкается через другую пару рёбер.

### Цикл (a): `SelectTool → HUDController → PlayerInteractionManager → SelectTool`

```csharp
// src/ui/tools/SelectTool.cs:31-33   SelectTool → HUDController
var hud = HUDController.Instance;
hud?.ShowHumidityAt(tilePos.X, tilePos.Y);
hud?.ShowFertilityAt(tilePos.X, tilePos.Y);

// src/ui/controllers/HUDController.cs:229-231   HUDController → PlayerInteractionManager
if (PlayerInteractionManager.Instance != null && _interactionManager == null)
    _interactionManager = PlayerInteractionManager.Instance;

// src/ui/PlayerInteractionManager.cs:28   PlayerInteractionManager → SelectTool
_defaultTool = new SelectTool(Selection);
```

SelectTool нужны от HUD **ровно два метода**: `ShowHumidityAt(int,int)` (`HUDController.cs:435`)
и `ShowFertilityAt(int,int)` (`HUDController.cs:389`).

#### Файл (новый): `src/ui/ICellProbeOverlay.cs`

```csharp
namespace Game.UI;

/// <summary>
/// Показ значений клетки под курсором (влажность / плодородие).
/// Узкий контракт для SelectTool, чтобы инструмент не зависел от HUDController.
/// Реализуется HUDController. Разрывает цикл
/// SelectTool → HUDController → PlayerInteractionManager → SelectTool.
/// </summary>
public interface ICellProbeOverlay
{
	/// <summary>Показать % влажности клетки.</summary>
	void ShowHumidityAt(int x, int y);

	/// <summary>Показать плодородие клетки.</summary>
	void ShowFertilityAt(int x, int y);
}
```

#### Правки

`src/ui/tools/SelectTool.cs`:
```csharp
	private ICellProbeOverlay _probe;

	/// <summary>Подключить источник подсказок клетки (HUDController).</summary>
	public void BindCellProbe(ICellProbeOverlay probe) => _probe = probe;

	// OnHover (было HUDController.Instance):
	if (tilePos.X == _lastHumidityX && tilePos.Y == _lastHumidityY)
		return;
	_lastHumidityX = tilePos.X;
	_lastHumidityY = tilePos.Y;
	_probe?.ShowHumidityAt(tilePos.X, tilePos.Y);
	_probe?.ShowFertilityAt(tilePos.X, tilePos.Y);
```

`src/ui/PlayerInteractionManager.cs` (только интерфейс, без HUDController — иначе создадим новый цикл):
```csharp
	/// <summary>Прокинуть подсказки клетки в дефолтный инструмент.</summary>
	public void SetCellProbe(ICellProbeOverlay probe) => _defaultTool?.BindCellProbe(probe);
```

`src/ui/controllers/HUDController.cs` — там, где HUD уже общается с `PlayerInteractionManager`
(строки 229-231, сразу после получения `_interactionManager`):
```csharp
	_interactionManager = PlayerInteractionManager.Instance;
	_interactionManager?.SetCellProbe(this);   // HUDController : ICellProbeOverlay
```
и объявление класса: `public partial class HUDController : Control, ICellProbeOverlay`.

**Рёбра после фикса:** `SelectTool → ICellProbeOverlay.cs`; `PlayerInteractionManager → SelectTool + ICellProbeOverlay.cs`;
`HUDController → PlayerInteractionManager + ICellProbeOverlay.cs`. Возврата в `SelectTool.cs` нет.

### Цикл (b): `HUDController ↔ BuildMenuController`

```csharp
// src/ui/controllers/HUDController.cs:535        HUDController → BuildMenuController
_buildMenuController = new BuildMenuController { Name = "BuildMenuController" };
// src/ui/controllers/HUDController.cs:1439       (type-check)
control is BuildMenuController ||

// BuildMenuController.cs:177,178,250-257,263,264,275,279,281,288,289,306,307,312,313,320,321,407,408
HUDController.Instance?.SetDraftDoorMode(false);
HUDController.Instance?.StartZoneDraft(_currentZoneId);
bool nowDrafting = hud != null && hud.IsDrafting;
hud.CancelDraft();  hud.SetDraftDoorMode(false);
hud?.StopDraftDrawing();  hud?.SetDraftDoorMode(_doorMode);
hud?.ConfirmDraft();  hud?.DiscardDraft();  hud?.SetDraftMaterial(_selectedMaterialId);
```

Полный список того, что `BuildMenuController` требует от HUD (проверено по строкам):

| Метод / свойство HUD | Объявление в HUDController.cs |
| :--- | :--- |
| `bool IsDrafting { get; }` | 1128 |
| `void StartZoneDraft(string zoneId)` | 1136 |
| `void SetDraftDoorMode(bool enabled)` | 1166 |
| `void ConfirmDraft()` | 1178 |
| `void StopDraftDrawing()` | 1189 |
| `void CancelDraft()` | 1195 |
| `void DiscardDraft()` | 1206 |
| `void SetDraftMaterial(string id)` | (используется на 408) |

#### Файл (новый): `src/ui/controllers/IDraftCoordinator.cs`

```csharp
namespace Game.UI;

/// <summary>
/// Координатор черновика постройки: то, что окно стройки (BuildMenuController) вправе
/// требовать от HUD. Реализуется HUDController, потребляется BuildMenuController.
/// Разрывает цикл HUDController ↔ BuildMenuController (ветка (b) цикла #4).
/// </summary>
public interface IDraftCoordinator
{
	/// <summary>Активен ли черновик зоны/стен.</summary>
	bool IsDrafting { get; }

	/// <summary>Начать черчение зоны по её id.</summary>
	void StartZoneDraft(string zoneId);

	/// <summary>Включить/выключить режим дверного проёма.</summary>
	void SetDraftDoorMode(bool enabled);

	/// <summary>Остановить рисование, сохранив черновик.</summary>
	void StopDraftDrawing();

	/// <summary>Зафиксировать черновик (коммит).</summary>
	void ConfirmDraft();

	/// <summary>Отменить черновик без удаления поставленного.</summary>
	void CancelDraft();

	/// <summary>Отменить черновик и снести поставленные чертежи/зону.</summary>
	void DiscardDraft();

	/// <summary>Выбрать материал стен для черновика.</summary>
	void SetDraftMaterial(string materialId);
}
```

#### Правки

`src/ui/controllers/HUDController.cs`:
```csharp
	public partial class HUDController : Control, ICellProbeOverlay, IDraftCoordinator

	// строка 535 — сразу после создания окна стройки:
	_buildMenuController = new BuildMenuController { Name = "BuildMenuController" };
	_buildMenuController.BindDraftCoordinator(this);
```

`src/ui/controllers/BuildMenuController.cs`:
```csharp
	private IDraftCoordinator _draft;

	/// <summary>Подключить координатор черновика (HUDController).</summary>
	public void BindDraftCoordinator(IDraftCoordinator draft) => _draft = draft;

	// все 8 обращений HUDController.Instance → _draft:
	_draft?.SetDraftDoorMode(false);
	_draft?.StartZoneDraft(_currentZoneId);
	bool nowDrafting = _draft != null && _draft.IsDrafting;
	_draft?.CancelDraft();
	_draft?.StopDraftDrawing();
	_draft?.ConfirmDraft();
	_draft?.DiscardDraft();
	_draft?.SetDraftMaterial(_selectedMaterialId);
```

> `HUDController.cs:1439` (`control is BuildMenuController`) трогать не нужно: ребро
> `HUDController → BuildMenuController` остаётся односторонним, цикл рвётся обратным ребром.

---

## 5. Попутно найденный РЕАЛЬНЫЙ баг: висячие статические `Instance`

### Факт

| Контроллер | `_Ready`: ставит `Instance = this` | `_ExitTree`: обнуляет `Instance` |
| :--- | :--- | :--- |
| `BuildMenuController` | 66-68 ✅ | 469-472 ✅ `Instance = null;` |
| `HUDController` | 75-77 ✅ | 1474-1477 ✅ `Instance = null;` |
| **`FarmController`** | 42-44 ✅ | **411-416 ❌ НЕ обнуляет** |
| **`WorkZoneController`** | 49-51 ✅ | **455-461 ❌ НЕ обнуляет** |

```csharp
// src/ui/controllers/FarmController.cs:411-416 — Instance остаётся висеть
public override void _ExitTree()
{
    FarmZoneManager.Instance.OnZoneSelected -= OnZoneSelected;
    FarmZoneManager.Instance.OnZoneDeselected -= Close;
    base._ExitTree();
}
```

### Почему это баг, а не стиль

1. `FarmController.Instance` / `WorkZoneController.Instance` после освобождения ноды указывают
   на **disposed Godot-объект**. Любое обращение к нему даёт `ObjectDisposedException`
   (или необработанный access к freed native handle).
2. Условия вида `WorkZoneController.Instance == null` (`HUDController.cs:640,663,812,824`)
   **никогда не станут истинными** после выгрузки UI → логика «окно мертво, открой заново»
   не срабатывает, игрок видит залипшее состояние.
3. Сценарий: `menu.tscn` (главная сцена) → «Играть» → `Main.tscn` → возврат в меню → снова
   «Играть». Второй `Main` создаёт новые контроллеры, но старая статика уже отравлена;
   при этом `FarmController._ExitTree` ещё и отписывается от `FarmZoneManager`, т.е.
   частичная уборка есть — пропущена именно строка обнуления.

### Фикс

Добавить `Instance = null;` в `_ExitTree()` обоих контроллеров (симметрично
`BuildMenuController.cs:472` и `HUDController.cs:1477`), плюс снятие регистрации в
`ZoneWindowRegistry` (§3), если он внедряется.

---

## 6. Корректировка списка «мёртвого кода»

Список из отчёта содержит ложные срабатывания. Проверено:

### ❌ НЕ удалять (ложные срабатывания)

| Файл | Почему это НЕ мёртвый код |
| :--- | :--- |
| `autoload/mcp_interaction_server.gd` | Зарегистрирован как autoload `McpInteractionServer` в `project.godot:21`. Удаление отключает in-game MCP-сервер |
| `src/GlobalUsings.cs` | Содержит `global using Game.Core.WorldStreaming;` — компиляционный файл на весь проект; удаление ломает сборку |
| `src/core/world/MountainTileHelper.cs` | Используется: `tests/TestWorldGen.cs:390` — `MountainTileHelper.GetTile(x, y, map.Ground)` |
| `scenes/ui/Garden.tscn` | Пустая заглушка сцены (34 байта, `load_steps=1`, нод нет) — не «точка входа» |
| `scenes/infinite_world/infinite_world_game_demo.tscn` | То же: пустая заглушка |

### ✅ Действительно пустые — можно удалять (проверено: нет ссылок ни в одном `.tscn`)

| Файл | Размер |
| :--- | :--- |
| `src/ui/StressDebug.cs` (+`.uid`) | 1 байт |
| `src/ui/controllers/GardenController.cs` (+`.uid`) | 1 байт |
| `src/ui/streaming/InfiniteWorldLab.cs` (+`.uid`) | 1 байт |
| `src/ui/streaming/InfiniteWorldGame.cs` (+`.uid`) | 0 байт |

Удалять вместе с парными `.cs.uid` (Godot-метаданные), иначе останутся висячие uid-ссылки.

### ⚠️ Требует отдельной проверки, а не слепого удаления

Маленькие enum-файлы (`TileType.cs`, `AgentState.cs`, `JobTypeId.cs`, `NeedBehavior.cs`,
`ResourceTypes.cs`, `StructureCategory.cs`, `BuildingType.cs`, `JobCategory.cs`,
`JobExecutionType.cs`, `JobPriorityTier.cs`) анализатор объявил изолированными, но
enum'ы часто используются внутри `switch` и в атрибутах — по одному имени файла судить нельзя.
Удалять только после `grep` по каждому имени типа. `src/core/world/streaming/generation/WorldFeatures.cs`
(112 строк) — проверять отдельно.

---

## 7. Оговорки по «Глобальному реестру сигналов» (142 записи)

Этот отчёт шумный, использовать его как список багов нельзя.

1. **Встроенные сигналы Godot не являются сигналами проекта.** Записи `Pressed`, `TextChanged`,
   `SizeChanged`, `ValueChanged`, `Position`, `X`, `Y` — это `BaseButton.Pressed`,
   `Control.SizeChanged`, `Range.ValueChanged`, `Node2D.Position` и т.д. Эмитит их движок,
   поэтому пометка «⚠️ Никогда не вызывается (нет emit)» бессмысленна.
2. **Локальные переменные приняты за сигналы.** Записи `c`, `i`, `k`, `sum`, `total`, `offset`,
   `totalRead`, `acc`, `b`, `g`, `r`, `v`, `err`, `rawElevation`, `weightSum`, `encodedLength`,
   `restStart`, `off`, `stragglers`, `batches`, `cursor`, `signature`, `fixed_` — это переменные.
   Подтверждение: `src/core/world/streaming/storage/WorldChunkStore.cs:128` — `int totalRead = 0;`
   (объявлено «сигналом» `totalRead`).
3. **Реально полезное наблюдение.** `OnOpened` / `OnClosed` объявлены и эмитятся в
   `BuildMenuController`, `FarmController`, `WorkZoneController`, но **подписчиков нет ни одного** —
   готовый механизм развязки для цикла #3, который сейчас не используется (см. §3).
4. **Кандидаты в реальные баги (требуют проверки, не подтверждено).** `MapRenderer` объявляет
   и подписывается, но никто не эмитит: `OnBuildingPlaced`, `OnBuildingRemoved`, `OnTilesUpdated`,
   `OnPlotsBatchMarked`, `OnStonesBatchMarked`, `OnTreesBatchMarked`, `OnZoneTilesBatchAdded`,
   `OnSitesBatchAdded`; плюс `OnMapApplied` (подписан `Main.cs`). Либо мёртвые подписки, либо
   батч-операции не обновляют карту. Направлено аудитору UI-слоя.

---

## 8. Порядок применения и верификация

Порядок — от самого безопасного к самому рискованному:

| Шаг | Действие | Риск | Проверка |
| :--- | :--- | :--- | :--- |
| 1 | §1: удалить `[Obsolete]`-оболочку `WorldClimateParams` | низкий | `dotnet build` 0/0 |
| 2 | §5: `Instance = null` в двух `_ExitTree` | низкий | `dotnet build` 0/0 + headless-тесты |
| 3 | §6: удалить 4 пустых `.cs` + `.uid` | низкий | `dotnet build` 0/0 + отсутствие ссылок в `.tscn` |
| 4 | §2: цикл #2 (`IWorldAgentSource` + вынос DTO) | низкий | `dotnet build` 0/0, `Main.tscn` без ошибок |
| 5 | §4a: цикл #4a (`ICellProbeOverlay`) | средний | build + smoke-прогон UI: подсказка влажности/плодородия |
| 6 | §4b: цикл #4b (`IDraftCoordinator`) | средний | build + smoke: окно стройки, зоны, дверь, коммит |
| 7 | §3: цикл #3 (`IZoneWindow` + реестр) | средний | build + smoke: взаимное закрытие окон фермы/склада, JobDisplay |
| 8 | Перезапуск анализатора | — | «цикл #1» исчез; циклы #2/#3/#4 исчезли |

Обязательный минимум для каждого шага: `dotnet build Game.csproj` → 0 ошибок, 0 предупреждений,
и headless-прогон `tests/run_test.gd`. Шаги 5-7 дополнительно требуют ручного smoke в Godot,
т.к. headless-тесты UI не покрывают.