using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Унифицированный менеджер зон: Farm-грядки и Work-зоны-бригадиры в одном
/// владельце тайлов. Один тайл — максимум одна зона любого вида.
/// Заменяет FarmZoneManager + WorkZoneManager (те оставлены тонкими фасадами).
/// </summary>
public sealed class ZoneManager
{
    public static ZoneManager Instance { get; } = new();

    private const int ChunkDim = 32;
    // Мелочь (комментарий к связи констант): чанки диспатча — 16×16 тайлов, карта
    // 512×512 → сетка 32×32 чанков. Отсюда >> 4 в CalcChunkIndices и ChunkDim = 32.
    // При изменении размера карты/чанка обе величины надо пересчитывать согласованно.
    private const int WorkersPerChunkBudget = 48;
    private const int MaxAssignPerChunk = 8;
    private const int AgentReservedMarker = -2;

    // Границы карты для клиппа дисков Work-зон (B1: volatile — пишет sim, читает UI).
    private static volatile int _mapW = 512;
    private static volatile int _mapH = 512;

    // Round-robin кап зон-бригадиров на тик.
    // TODO (наблюдаемость): при десятках Work-зон MaxZonesPerPass=4 с round-robin
    // по ВСЕМ зонам (включая Farm) даёт ощутимое опоздание назначения для дальних
    // зон — стоит логировать факт «зона не обслужена N проходов».
    private const int MaxZonesPerPass = 4;
    private int _zoneScanIndex;

    // Мелочь: было ThreadLocal<int[]> — DispatchZones зовётся строго из sim-потока,
    // обычное поле проще и честнее отражает инвариант одного потока-читателя.
    private readonly int[] _zoneBuffer = new int[WorkersPerChunkBudget];

    private readonly object _lock = new();
    private readonly Dictionary<int, Zone> _zones = new(256);
    private readonly Dictionary<(int X, int Y), int> _tileToZoneId = new(4096);
    private int _nextZoneId = 1;

    public Zone HoveredZone { get; private set; }
    public Zone SelectedZone { get; private set; }

    public event Action OnZonesUpdated;
    public event Action<Zone> OnZoneHovered;
    public event Action<Zone> OnZoneSelected;
    public event Action OnZoneDeselected;
    public event Action<Zone> OnAutoPlantChanged;

    public static void SetMapBounds(int w, int h)
    {
        if (w > 0) _mapW = w;
        if (h > 0) _mapH = h;
    }

    private static List<int> CalcChunkIndices(HashSet<(int X, int Y)> tiles)
    {
        var set = new HashSet<int>();
        foreach (var (x, y) in tiles)
        {
            int cx = Math.Clamp(x >> 4, 0, ChunkDim - 1);
            int cy = Math.Clamp(y >> 4, 0, ChunkDim - 1);
            set.Add(cy * ChunkDim + cx);
        }
        return new List<int>(set);
    }

    // ---------------- Farm API (mirror FarmZoneManager) ----------------

    /// <summary>Создать Farm-зоны из тайлов (BFS-сплит на связные компоненты).</summary>
    public List<Zone> CreateFarmZones(List<(int X, int Y)> tiles, string namePrefix = "Ферма")
    {
        var result = new List<Zone>();
        if (tiles == null || tiles.Count == 0) return result;
        var components = SplitIntoConnectedComponents(new HashSet<(int X, int Y)>(tiles));
        lock (_lock)
        {
            foreach (var comp in components)
            {
                // Пропускаем компоненты, пересекающие чужие зоны.
                bool clash = false;
                foreach (var pos in comp)
                {
                    if (_tileToZoneId.ContainsKey(pos)) { clash = true; break; }
                }
                if (clash) continue;
                int id = _nextZoneId++;
                var zone = new Zone(id, $"{namePrefix} #{id}", ZoneKind.Farm, comp);
                _zones[id] = zone;
                foreach (var pos in comp)
                    _tileToZoneId[pos] = id;
                result.Add(zone);
                BumpZonesVersion_NoLock();
            }
        }
        if (result.Count > 0)
            Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
        return result;
    }

    public void SetAutoPlant(int zoneId, bool enabled)
    {
        Zone zone = null;
        lock (_lock)
        {
            if (_zones.TryGetValue(zoneId, out zone) && zone.Kind == ZoneKind.Farm)
                zone.AutoPlantEnabled = enabled;
            else
                zone = null;
        }
        if (zone != null)
        {
            if (enabled)
            {
                foreach (var (x, y) in zone.Tiles)
                {
                    if (FarmJobManager.Instance.IsGardenBed(x, y) && !CropGrowthManager.Instance.HasCrop(x, y))
                        JobBroker.Instance.RegisterPlanting(x, y, zone.Id);
                }
            }
            else
            {
                foreach (var (x, y) in zone.Tiles)
                    JobBroker.Instance.UnregisterPlanting(x, y);
            }
            OnAutoPlantChanged?.Invoke(zone);
        }
    }

    /// <summary>Удалить тайлы (фермерский инструмент, правая кнопка).</summary>
    public void RemoveTiles(List<(int X, int Y)> tilesToRemove)
    {
        if (tilesToRemove == null || tilesToRemove.Count == 0) return;
        // Двухфазно: под _lock только структуры зон (тайлы/индексы/BFS-сплит),
        // симуляционные вызовы (RemoveCrop/Unregister/Register — чужие локи)
        // СТРОГО вне _lock. Раньше всё было под одним hold: вложенные локи +
        // долгий BFS = contention sim-потока.
        var affectedZones = new HashSet<int>();
        List<(int ZoneId, List<HashSet<(int X, int Y)>> Comps, bool WasAuto)> splits = null;
        List<int> removedZoneIds = null;
        lock (_lock)
        {
            // FIX #1 (порядок операций): раньше удаление из _tileToZoneId шло ДО
            // проверки Kind — для Work-зоны тайл выпадал из индекса, но оставался
            // в zone.Tiles: рассинхрон tileToZoneId ↔ Tiles (TryGetZoneAt говорил
            // «нет зоны», диспатч продолжал считать тайл частью зоны). Теперь
            // множество удалённых тайлов каждой зоны пересобирается за один проход.
            Dictionary<int, HashSet<(int X, int Y)>> removals = null;
            foreach (var pos in tilesToRemove)
            {
                if (!_tileToZoneId.TryGetValue(pos, out int zoneId)) continue;
                if (!_zones.TryGetValue(zoneId, out var zone)) continue;
                // Work-зоны неделимы: удаление тайлов из них запрещено — ничего не трогаем.
                if (zone.Kind == ZoneKind.Work) continue;
                if (removals == null)
                    removals = new Dictionary<int, HashSet<(int X, int Y)>>(4);
                if (!removals.TryGetValue(zoneId, out var set))
                    removals[zoneId] = set = new HashSet<(int X, int Y)>();
                set.Add(pos);
                _tileToZoneId.Remove(pos);
            }
            if (removals != null)
            {
                foreach (var kv in removals)
                {
                    if (!_zones.TryGetValue(kv.Key, out var zone)) continue;
                    // Immutable-swap (FIX #2): см. ApplyTileRemoval_NoLock.
                    ApplyTileRemoval_NoLock(zone, kv.Value);
                    affectedZones.Add(kv.Key);
                }
            }
            foreach (int zoneId in affectedZones)
            {
                if (!_zones.TryGetValue(zoneId, out var zone)) continue;
                if (zone.Tiles.Count == 0)
                {
                    _zones.Remove(zoneId);
                    BumpZonesVersion_NoLock();
                    if (SelectedZone?.Id == zoneId) DeselectZoneInternal();
                    if (HoveredZone?.Id == zoneId) HoveredZone = null;
                    (removedZoneIds ??= new List<int>()).Add(zoneId);
                    continue;
                }
                var subComponents = SplitIntoConnectedComponents(zone.Tiles);
                if (subComponents.Count > 1)
                {
                    bool wasAuto = zone.AutoPlantEnabled;
                    _zones.Remove(zoneId);
                    BumpZonesVersion_NoLock();
                    if (SelectedZone?.Id == zoneId) DeselectZoneInternal();
                    foreach (var comp in subComponents)
                    {
                        int newId = _nextZoneId++;
                        var newZone = new Zone(newId, $"Ферма #{newId}", ZoneKind.Farm, comp)
                        {
                            AutoPlantEnabled = wasAuto
                        };
                        _zones[newId] = newZone;
                        BumpZonesVersion_NoLock();
                        foreach (var pos in comp)
                            _tileToZoneId[pos] = newId;
                    }
                    (splits ??= new List<(int, List<HashSet<(int, int)>>, bool)>()).Add((zoneId, subComponents, wasAuto));
                }
            }
        }
        // Вне лока: симуляция.
        for (int i = 0; i < tilesToRemove.Count; i++)
        {
            var pos = tilesToRemove[i];
            CropGrowthManager.Instance.RemoveCrop(pos.X, pos.Y);
            JobBroker.Instance.UnregisterPlanting(pos.X, pos.Y);
            JobBroker.Instance.UnregisterHarvest(pos.X, pos.Y);
        }
        if (splits != null)
        {
            for (int s = 0; s < splits.Count; s++)
            {
                var (_, comps, wasAuto) = splits[s];
                if (!wasAuto) continue;
                for (int c = 0; c < comps.Count; c++)
                {
                    foreach (var (x, y) in comps[c])
                    {
                        if (FarmJobManager.Instance.IsGardenBed(x, y) && !CropGrowthManager.Instance.HasCrop(x, y))
                        {
                            // zoneId новой зоны неизвестен здесь без лока — ищем по тайлу.
                            if (TryGetZoneAt(x, y, out var nz))
                                JobBroker.Instance.RegisterPlanting(x, y, nz.Id);
                        }
                    }
                }
            }
        }
        Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
    }

    // ---------------- Work API ----------------

    /// <summary>Создать Work-зону-бригадир кругом вокруг центра. maxWorkers=0 — без лимита.</summary>
    public bool CreateWorkZoneAt(int cx, int cy, ulong jobMask, int tilesTarget, int maxWorkers, out Zone zone, out string failReason)
    {
        zone = null;
        failReason = null;
        if (jobMask == 0 || (jobMask & 1UL) != 0)
        {
            failReason = "маска пуста или содержит бит 0";
            return false;
        }
        int w = _mapW, h = _mapH;
        if (cx < 0 || cy < 0 || cx >= w || cy >= h)
        {
            failReason = "центр вне карты";
            return false;
        }
        maxWorkers = Math.Clamp(maxWorkers, 0, 64);
        int target = Math.Clamp(tilesTarget, 128, 2048);
        int r = Zone.CalcRadius(target);
        var tiles = Zone.BuildDisc(cx, cy, r);
        tiles.RemoveWhere(t => t.Item1 < 0 || t.Item2 < 0 || t.Item1 >= w || t.Item2 >= h);
        if (tiles.Count == 0)
        {
            failReason = "вне карты";
            return false;
        }
        lock (_lock)
        {
            foreach (var t in tiles)
            {
                if (_tileToZoneId.TryGetValue(t, out int otherId))
                {
                    failReason = $"пересекает зону #{otherId}";
                    return false;
                }
            }
            int id = _nextZoneId++;
            zone = new Zone(id, $"Зона #{id}", cx, cy, r, target, maxWorkers, jobMask, tiles, CalcChunkIndices(tiles));
            // FIX #2 (immutable-swap): снапшот геометрии публикуем ОДНОЙ volatile-записью
            // ДО добавления зоны в реестр — читатель либо не видит зону вовсе, либо видит
            // полностью согласованную форму. Дальше ссылки на формы только заменяются.
            Volatile.Write(ref zone.Shape, BuildWorkShape(zone));
            _zones[id] = zone;
            BumpZonesVersion_NoLock();
            foreach (var t in tiles)
                _tileToZoneId[t] = id;
        }
        Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
        return true;
    }

    // ---------------- Общие операции ----------------

    public bool RemoveZone(int id)
    {
        bool wasSelected;
        lock (_lock)
        {
            if (!_zones.TryGetValue(id, out var zone))
                return false;
            foreach (var t in zone.Tiles)
                _tileToZoneId.Remove(t);
            _zones.Remove(id);
            BumpZonesVersion_NoLock();
            wasSelected = SelectedZone?.Id == id;
            if (wasSelected) DeselectZoneInternal();
            if (HoveredZone?.Id == id) HoveredZone = null;
        }
        if (wasSelected) Callable.From(() => OnZoneDeselected?.Invoke()).CallDeferred();
        Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
        return true;
    }

    public bool TryGetZoneAt(int x, int y, out Zone zone)
    {
        lock (_lock)
        {
            if (_tileToZoneId.TryGetValue((x, y), out int zoneId))
                return _zones.TryGetValue(zoneId, out zone);
            zone = null;
            return false;
        }
    }

    public bool TryGetZoneById(int zoneId, out Zone zone)
    {
        lock (_lock)
        {
            return _zones.TryGetValue(zoneId, out zone);
        }
    }

    // P0-3: кэшированный снимок зон для DispatchZones. GetAllZones делал
    // new List + lock + Sort на КАЖДЫЙ диспатч (сотни раз/с). Снимок валиден,
    // пока Version не изменился (инкремент в Create/Delete/SetWork*).
    private List<Zone> _dispatchSnap;
    private int _dispatchSnapVersion = -1;
    private int _zonesVersion;

    public List<Zone> GetAllZones()
    {
        lock (_lock)
        {
            return new List<Zone>(_zones.Values);
        }
    }

    /// <summary>
    /// Снимок зон для диспатч-прохода: без аллокации, если зоны не менялись.
    /// Возвращаемый список НЕ модифицировать (кэш). Вызывается строго из sim-потока
    /// (DispatchZones) — кэш-поля читаются/пишутся только этим потоком; межпотоковую
    /// корректность обеспечивает барьер Volatile.Read(_zonesVersion) и блокировки
    /// писателей на _lock.
    /// </summary>
    public List<Zone> GetDispatchSnapshot()
    {
        int v = Volatile.Read(ref _zonesVersion);
        var snap = _dispatchSnap;
        if (snap != null && v == _dispatchSnapVersion)
            return snap;
        lock (_lock)
        {
            v = _zonesVersion;
            if (_dispatchSnap != null && v == _dispatchSnapVersion)
                return _dispatchSnap;
            var fresh = new List<Zone>(_zones.Values);
            fresh.Sort(static (a, b) => a.Id.CompareTo(b.Id));
            _dispatchSnap = fresh;
            _dispatchSnapVersion = v;
            return fresh;
        }
    }

    // FIX #4: BumpZonesVersion (Interlocked-вариант) удалён — все вызовы шли через
    // _NoLock под _lock; отдельная «потокобезопасная» версия создавала видимость
    // вызова вне лока, которого нет.
    // Мелочь: инкремент под _lock + Volatile.Write публикации — читатели снимка
    // обязаны видеть новый version ДО пересборки _zones (иначе вечный stale-кэш).
    private void BumpZonesVersion_NoLock()
    {
        Volatile.Write(ref _zonesVersion, _zonesVersion + 1);
    }

    // ---------------- Immutable-swap геометрии зон (FIX #2) ----------------

    // Зона формы (ZoneShape) живёт в Zone.cs как публичный неизменяемый тип:
    // собирается ЦЕЛИКОМ и публикуется одной volatile-заменой ссылки Zone.Shape.
    // Читатели (UI-рендер вне _lock, SetAutoPlant, диспатч из sim-потока) берут
    // ссылку один раз и работают с согласованным снапшотом: никаких «Collection
    // was modified» и разорванных состояний при параллельной пересборке тайлов.

    private static ZoneShape BuildWorkShape(Zone z) => new(
        z.RadiusTiles, z.TilesTarget, z.MaxWorkers, z.JobMask, z.PriorityOverride,
        z.Tiles, CalcChunkIndicesArray(z.Tiles));

    private static int[] CalcChunkIndicesArray(HashSet<(int X, int Y)> tiles)
    {
        var list = CalcChunkIndices(tiles);
        var arr = new int[list.Count];
        for (int i = 0; i < list.Count; i++)
            arr[i] = list[i];
        return arr;
    }

    /// <summary>
    /// Удалить тайлы из Farm-зоны (immutable-swap): копия множества без указанных
    /// позиций + замена ссылки. Коллекция Tiles НИКОГДА не мутируется на месте —
    /// читатели (UI-рендер, SetAutoPlant, диспатч) могут итерировать старый
    /// снапшот без lock и без «Collection was modified».
    /// </summary>
    private static void ApplyTileRemoval_NoLock(Zone zone, HashSet<(int X, int Y)> remove)
    {
        var copy = new HashSet<(int X, int Y)>(zone.Tiles.Count);
        foreach (var t in zone.Tiles)
            if (!remove.Contains(t))
                copy.Add(t);
        zone.Tiles = copy;
    }

    public bool SetWorkTiles(int id, int target)
    {
        int clamped = Math.Clamp(target, 128, 2048);
        Zone zone;
        lock (_lock)
        {
            if (!_zones.TryGetValue(id, out zone) || zone.Kind != ZoneKind.Work)
                return false;
        }
        int r = Zone.CalcRadius(clamped);
        var fresh = Zone.BuildDisc(zone.CenterX, zone.CenterY, r);
        int w = _mapW, h = _mapH;
        fresh.RemoveWhere(t => t.Item1 < 0 || t.Item2 < 0 || t.Item1 >= w || t.Item2 >= h);
        if (fresh.Count == 0)
            return false;
        lock (_lock)
        {
            if (!_zones.TryGetValue(id, out zone) || zone.Kind != ZoneKind.Work)
                return false;
            var old = zone.Tiles;
            foreach (var t in fresh)
            {
                if (old.Contains(t)) continue;
                if (_tileToZoneId.TryGetValue(t, out int otherId) && otherId != id)
                    return false;
            }
            foreach (var t in old)
                _tileToZoneId.Remove(t);
            foreach (var t in fresh)
                _tileToZoneId[t] = id;
            zone.Tiles = fresh;
            zone.TilesTarget = clamped;
            zone.RadiusTiles = r;
            // FIX #2: единый согласованный снапшот вместо четырёх отдельных сеттеров
            // (ChunkIndices/TilesTarget/RadiusTiles/Tiles раньше обновлялись раздельно —
            // параллельный читатель мог увидеть новую геометрию со старыми чанками).
            Volatile.Write(ref zone.Shape, BuildWorkShape(zone));
            // FIX AssignedCount-протокола: сброс «назначено за проход» — фаза до
            // параллельного диспатча (SetWork* вызываются из UI/main-потока между
            // проходами DispatchZones; RegisterAssigned из воркеров здесь уже не идёт).
            zone.ResetAssigned();
        }
        Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
        return true;
    }

    public bool SetWorkWorkers(int id, int v)
    {
        lock (_lock)
        {
            if (!_zones.TryGetValue(id, out var zone) || zone.Kind != ZoneKind.Work)
                return false;
            zone.MaxWorkers = Math.Clamp(v, 0, 64);
            Volatile.Write(ref zone.Shape, BuildWorkShape(zone));
            return true;
        }
    }

    public bool SetWorkPriorityOverride(int id, int v)
    {
        int clamped = Math.Clamp(v, 1, 10);
        ulong snap;
        lock (_lock)
        {
            if (!_zones.TryGetValue(id, out var zone) || zone.Kind != ZoneKind.Work)
                return false;
            snap = zone.JobMask;
            zone.PriorityOverride = clamped;
            Volatile.Write(ref zone.Shape, BuildWorkShape(zone));
        }
        // ВНИМАНИЕ (осознанное поведение): оверрайд приоритета зоны пишется в ГЛОБАЛЬНЫЙ
        // JobPriorityManager — затрагивает категории всех зон и свободных работ.
        // Имя обещает зональность; резолвить приоритет в момент клейма — отдельный
        // рефакторинг (см. отчёт ревью, п. «Мелочи»).
        var seen = new HashSet<JobCategory>();
        for (int t = 1; t <= 10; t++)
        {
            if ((snap & (1UL << t)) == 0) continue;
            var typeId = (JobTypeId)t;
            var cat = JobPriorityManager.Instance.GetCategory(typeId);
            if (seen.Add(cat))
                JobPriorityManager.Instance.SetPriority(cat, clamped);
        }
        Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
        return true;
    }

    public void SetHoveredTile(int x, int y)
    {
        Zone newHovered = null;
        lock (_lock)
        {
            if (_tileToZoneId.TryGetValue((x, y), out int zoneId))
                _zones.TryGetValue(zoneId, out newHovered);
        }
        // Мелочь (намеренно): запись/сравнение HoveredZone ВНЕ _lock — reference
        // assignment атомарен, единственный писатель (main-поток), максимум stale
        // на одно событие. Лок здесь лишь создавал бы видимость многопоточных читателей.
        if (HoveredZone != newHovered)
        {
            HoveredZone = newHovered;
            OnZoneHovered?.Invoke(HoveredZone);
            OnZonesUpdated?.Invoke();
        }
    }

    /// <summary>
    /// Выбрать зону по тайлу. Возвращает выбранную (null если промах — выбор снят).
    /// НЕ дёргает чужой Deselect: взаимоисключение окон — на слое контроллеров.
    /// </summary>
    public Zone SelectZoneAt(int x, int y)
    {
        Zone targetZone = null;
        lock (_lock)
        {
            if (_tileToZoneId.TryGetValue((x, y), out int zoneId))
                _zones.TryGetValue(zoneId, out targetZone);
        }
        if (targetZone != null)
        {
            SelectedZone = targetZone;
            OnZoneSelected?.Invoke(SelectedZone);
            OnZonesUpdated?.Invoke();
        }
        else
        {
            DeselectZone();
        }
        return targetZone;
    }

    public void DeselectZone()
    {
        if (SelectedZone != null)
        {
            DeselectZoneInternal();
            OnZoneDeselected?.Invoke();
            OnZonesUpdated?.Invoke();
        }
    }

    private void DeselectZoneInternal() => SelectedZone = null;

    // ---------------- Бригадир-диспатч (только Work-зоны) ----------------

    public int DispatchZones(AgentDataPool pool, SimulationContext ctx)
    {
        if (ctx != null)
            SetMapBounds(ctx.MapWidth, ctx.MapHeight);
        // P0-3: кэшированный снимок вместо new List + Sort на каждый диспатч.
        var zones = GetDispatchSnapshot();
        if (zones.Count == 0) return 0;
        if (JobDispatcher.Instance.JobIndex.UnclaimedCount <= 0) return 0;
        if (JobDispatcher.Instance.IdleWorkers.TotalIdleCount <= 0) return 0;
        // Только Work-зоны участвуют в раздаче.
        int workCount = 0;
        for (int i = 0; i < zones.Count; i++)
            if (zones[i].Kind == ZoneKind.Work) workCount++;
        if (workCount == 0) return 0;
        int totalZones = zones.Count;
        int toProcess = Math.Min(MaxZonesPerPass, totalZones);
        int oldScan = Interlocked.Add(ref _zoneScanIndex, toProcess) - toProcess;
        int baseIdx = ((oldScan % totalZones) + totalZones) % totalZones;
        int total = 0;
        for (int zi = 0; zi < toProcess; zi++)
        {
            var zone = zones[(baseIdx + zi) % totalZones];
            if (zone.Kind != ZoneKind.Work) continue;
            // FIX AssignedCount-протокола: сброс «назначено за проход» строго в фазе
            // ДО параллельной работы воркеров (DispatchZones вызывается из sim-потока
            // между проходами; RegisterAssigned из воркеров к этому моменту завершён).
            // ResetAssigned() также вызывается из SetWork* — это безопасно только потому,
            // что эти методы идут из main/UI-потока в паузе между диспатчами.
            zone.ResetAssigned();
            int zoneAssigned = 0;
            // FIX #2 (immutable-swap): берём ОДИН согласованный снапшот формы зоны.
            // Раньше chunks/tiles/MaxWorkers читались отдельными полями — параллельная
            // пересборка из UI давала разорванное состояние (новая геометрия + старые
            // чанки) и гонку на мутабельном HashSet («Collection was modified»).
            var shape = Volatile.Read(ref zone.Shape);
            if (shape == null)
            {
                // Страховка: зона создана встарую (до публикации Shape) или писатель
                // не прошёл через CreateWorkZoneAt/SetWork*. Собираем и публикуем сами
                // (идемпотентно: пересборка снапшота из текущих полей безвредна).
                lock (_lock)
                {
                    shape = zone.Shape ?? BuildWorkShape(zone);
                    Volatile.Write(ref zone.Shape, shape);
                }
            }
            var chunks = shape.ChunkIndices;
            // О(1) проверка принадлежности тайла зоне + плотный массив чанков —
            // всё из одного согласованного снапшота (immutable-swap).
            var tileSet = shape.TileSet;
            if (chunks == null || tileSet == null) continue;
            foreach (int chunkIdx in chunks)
            {
                if (shape.MaxWorkers > 0 && zoneAssigned >= shape.MaxWorkers)
                    break;
                if (JobDispatcher.Instance.JobIndex.GetChunkJobCount(chunkIdx) == 0)
                    continue;
                // Мелочь: ThreadLocal<int[]> заменён обычным полем — DispatchZones
                // зовётся строго из sim-потока, TLS лишь скрывала это инвариант.
                int[] buf = _zoneBuffer;
                int workerCount = JobDispatcher.Instance.IdleWorkers.CollectIdleWorkersInChunk(chunkIdx, WorkersPerChunkBudget, buf, pool);
                if (workerCount == 0) continue;
                int assigned = 0;
                for (int wi = 0; wi < workerCount; wi++)
                {
                    if (assigned >= MaxAssignPerChunk)
                        break;
                    if (shape.MaxWorkers > 0 && zoneAssigned >= shape.MaxWorkers)
                        break;
                    int agentIndex = buf[wi];
                    if (agentIndex < 0 || agentIndex >= pool.Capacity)
                        continue;
                    if (pool.States[agentIndex] != AgentState.Idle)
                        continue;
                    if (Interlocked.CompareExchange(ref pool.CurrentJobId[agentIndex], AgentReservedMarker, -1) != -1)
                        continue;
                    try
                    {
                        int workerTx = pool.CurrentCellX[agentIndex];
                        int workerTy = pool.CurrentCellY[agentIndex];
                        // FIX #3 (claim → cancel-чернилка): раньше клеймился ЛЮБОЙ лучший
                        // job чанка (чанк 16×16 шире диска зоны), а работы вне зоны/маски
                        // отменялись после OnStart — лишние claim/release циклы. Теперь
                        // предикат «принадлежит зоне» отсеивает кандидатов ДО CAS-захвата.
                        if (JobDispatcher.Instance.JobIndex.TryClaimForWorkerInChunk(
                            chunkIdx, workerTx, workerTy,
                            pool.EquippedTools[agentIndex],
                            pool, agentIndex, ctx,
                            out var activeJob,
                            filter: j => shape.JobMaskBitSet((int)j.TypeId) && tileSet.Contains((j.TargetX, j.TargetY))))
                        {
                            JobDispatcher.Instance.IdleWorkers.RemoveIdleWorker(agentIndex, pool);
                            pool.CurrentJobId[agentIndex] = activeJob.Id;
                            pool.CurrentJobType[agentIndex] = activeJob.TypeId;
                            if (JobRegistry.TryGetHandler(activeJob.TypeId, out var handler))
                            {
                                try
                                {
                                    handler.OnStart(agentIndex, activeJob, pool, ctx);
                                    if (pool.States[agentIndex] == AgentState.Idle)
                                    {
                                        int rollbackId = pool.CurrentJobId[agentIndex];
                                        if (rollbackId != -1)
                                        {
                                            JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(rollbackId);
                                            pool.CurrentJobId[agentIndex] = -1;
                                            pool.CurrentJobType[agentIndex] = JobTypeId.None;
                                        }
                                        JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
                                    }
                                    else
                                    {
                                        // Ревалидация под shape — дешёвая страховка от TOCTOU:
                                        // job мог выпасть из зоны между фильтром клейма и OnStart.
                                        // Основной отсев теперь делает filter в TryClaim (FIX #3).
                                        if (!shape.JobMaskBitSet((int)activeJob.TypeId) || !tileSet.Contains((activeJob.TargetX, activeJob.TargetY)))
                                        {
                                            try { handler.OnCancel(agentIndex, pool, ctx); }
                                            catch (Exception ex)
                                            {
                                                GD.PrintErr($"[ZoneManager] Ошибка OnCancel для агента #{agentIndex}: {ex.Message}");
                                            }
                                            JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(activeJob.Id);
                                            pool.CurrentJobId[agentIndex] = -1;
                                            pool.CurrentJobType[agentIndex] = JobTypeId.None;
                                            pool.States[agentIndex] = AgentState.Idle;
                                            JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
                                            continue;
                                        }
                                        pool.LastJobCategory[agentIndex] = (int)JobPriorityManager.Instance.GetCategory(activeJob.TypeId);
                                        zone.RegisterAssigned();
                                        assigned++;
                                        zoneAssigned++;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    GD.PrintErr($"[ZoneManager] OnStart error {activeJob.TypeId} (agent #{agentIndex}): {ex.Message}\n{ex.StackTrace}");
                                    JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(activeJob.Id);
                                    pool.CurrentJobId[agentIndex] = -1;
                                    pool.CurrentJobType[agentIndex] = JobTypeId.None;
                                    pool.States[agentIndex] = AgentState.Idle;
                                    JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
                                }
                            }
                            else
                            {
                                JobDispatcher.Instance.JobIndex.ReleaseWorkerClaim(activeJob.Id);
                                pool.CurrentJobId[agentIndex] = -1;
                                pool.CurrentJobType[agentIndex] = JobTypeId.None;
                                JobDispatcher.Instance.IdleWorkers.AddIdleWorker(agentIndex, pool);
                            }
                        }
                        else
                        {
                            pool.CurrentJobId[agentIndex] = -1;
                        }
                    }
                    catch
                    {
                        pool.CurrentJobId[agentIndex] = -1;
                        throw;
                    }
                }
                total += assigned;
            }
        }
        return total;
    }

    private static List<HashSet<(int X, int Y)>> SplitIntoConnectedComponents(HashSet<(int X, int Y)> tiles)
    {
        var result = new List<HashSet<(int X, int Y)>>();
        var unvisited = new HashSet<(int X, int Y)>(tiles);
        var queue = new Queue<(int X, int Y)>();
        var dirs = new (int X, int Y)[] { (0, -1), (0, 1), (-1, 0), (1, 0) };
        while (unvisited.Count > 0)
        {
            using var enumerator = unvisited.GetEnumerator();
            enumerator.MoveNext();
            var start = enumerator.Current;
            var comp = new HashSet<(int X, int Y)>();
            queue.Enqueue(start);
            unvisited.Remove(start);
            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                comp.Add(curr);
                foreach (var dir in dirs)
                {
                    var neighbor = (curr.X + dir.X, curr.Y + dir.Y);
                    if (unvisited.Remove(neighbor))
                        queue.Enqueue(neighbor);
                }
            }
            result.Add(comp);
        }
        return result;
    }
}
