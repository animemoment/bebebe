using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Унифицированный менеджер зон: Farm-грядки (тайлы) и Work-зоны-бригадиры
/// (§119 P0: центр + РАДИУС 64..512, клетки НЕ материализуются) в одном
/// владельце. Farm-тайлы по-прежнему «один тайл — одна Farm-зона»; Work-зоны
/// НЕ пишут тайлы в _tileToZoneId — попадание проверяется формулой радиуса,
/// пересечения Work-зон разрешает партиция P1 (ScheduleRepartition).
/// Заменяет FarmZoneManager + WorkZoneManager (те оставлены тонкими фасадами).
/// </summary>
public sealed class ZoneManager
{
	public static ZoneManager Instance { get; } = new();

	private const int ChunkDim = 32;
	private const int WorkersPerChunkBudget = 48;
	private const int MaxAssignPerChunk = 8;
	private const int AgentReservedMarker = -2;

	private readonly object _lock = new();
	private readonly Dictionary<int, Zone> _zones = new(256);
	private readonly Dictionary<(int X, int Y), int> _tileToZoneId = new(4096);
	private int _nextZoneId = 1;

	// Границы карты для клиппа дисков Work-зон (B1: volatile — пишет sim, читает UI).
	private static volatile int _mapW = 512;
	private static volatile int _mapH = 512;

	/// <summary>§119 P1: ширина карты для покрытий (ZoneCoverageMap).</summary>
	public static int MapWidth => _mapW;

	/// <summary>§119 P1: высота карты для покрытий (ZoneCoverageMap).</summary>
	public static int MapHeight => _mapH;

	// Round-robin кап зон-бригадиров на тик.
	private const int MaxZonesPerPass = 4;
	private int _zoneScanIndex;

	private readonly ThreadLocal<int[]> _zoneBuffer = new(() => new int[WorkersPerChunkBudget]);

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

	/// <summary>
	/// §119 P0: индексы чанков (16×16, сетка ChunkDim×ChunkDim), пересекающих
	/// ДИСК зоны, — аналитически, БЕЗ материализации клеток. Чанк пересекает
	/// диск, если ближайшая к центру точка прямоугольника чанка в радиусе r
	/// (clamp центра в прямоугольник чанка). Тот же индекс-пространство, что
	/// у CalcChunkIndices, — диспатч (JobIndex.GetChunkJobCount) не меняется.
	/// </summary>
	private static List<int> CalcChunkIndicesForDisc(int cx, int cy, int r)
	{
		var set = new HashSet<int>();
		int minCx = Math.Clamp((cx - r) >> 4, 0, ChunkDim - 1);
		int maxCx = Math.Clamp((cx + r) >> 4, 0, ChunkDim - 1);
		int minCy = Math.Clamp((cy - r) >> 4, 0, ChunkDim - 1);
		int maxCy = Math.Clamp((cy + r) >> 4, 0, ChunkDim - 1);
		long rr = (long)r * r;
		for (int chy = minCy; chy <= maxCy; chy++)
		{
			int y0 = chy << 4;
			// Ближайшая к центру Y-координата прямоугольника чанка.
			int ny = Math.Clamp(cy, y0, y0 + 15);
			long dy = ny - cy;
			for (int chx = minCx; chx <= maxCx; chx++)
			{
				int x0 = chx << 4;
				int nx = Math.Clamp(cx, x0, x0 + 15);
				long dx = nx - cx;
				if (dx * dx + dy * dy <= rr)
					set.Add(chy * ChunkDim + chx);
			}
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
			var zoneTilesToRemove = new Dictionary<int, List<(int X, int Y)>>();
			foreach (var pos in tilesToRemove)
			{
				if (_tileToZoneId.TryGetValue(pos, out int zoneId))
				{
					if (_zones.TryGetValue(zoneId, out var zone))
					{
						// Work-зоны неделимы: удаление тайлов из них запрещено.
						if (zone.Kind == ZoneKind.Work) continue;
						if (!zoneTilesToRemove.TryGetValue(zoneId, out var list))
						{
							list = new List<(int X, int Y)>();
							zoneTilesToRemove[zoneId] = list;
						}
						list.Add(pos);
						affectedZones.Add(zoneId);
					}
					_tileToZoneId.Remove(pos);
				}
			}
			foreach (var (zid, toRemoveList) in zoneTilesToRemove)
			{
				if (_zones.TryGetValue(zid, out var z))
				{
					var newTiles = new HashSet<(int X, int Y)>(z.Tiles);
					foreach (var p in toRemoveList)
						newTiles.Remove(p);
					z.Tiles = newTiles;
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
		FarmJobManager.Instance.UnmarkPlotsBatch(tilesToRemove);
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

	/// <summary>
	/// §119 P0: создать Work-зону-бригадир = ЦЕНТР + РАДИУС (64..512, кламп),
	/// ровно ОДИН бит JobMask (одна зона = один тип работ), DesiredWorkers —
	/// заявка без потолка (0..999). Клетки НЕ материализуются; пересечения
	/// зон не режутся (партиция — P1). Параметр радиусный: UI-слайдер до P2
	/// шлёт легаси-значение, оно клампится в 64..512.
	/// </summary>
	public bool CreateWorkZoneAt(int cx, int cy, ulong jobMask, int radiusTiles, int desiredWorkers, out Zone zone, out string failReason)
	{
		zone = null;
		failReason = null;
		// §119: ровно один тип работ — ровно один бит; бит 0 (JobTypeId.None) запрещён.
		if (jobMask == 0 || (jobMask & (jobMask - 1)) != 0 || (jobMask & 1UL) != 0)
		{
			failReason = "маска должна содержать ровно один бит типа работ (не 0)";
			return false;
		}
		int w = _mapW, h = _mapH;
		if (cx < 0 || cy < 0 || cx >= w || cy >= h)
		{
			failReason = "центр вне карты";
			return false;
		}
		int r = Math.Clamp(radiusTiles, WorkZone.MinRadiusTiles, WorkZone.MaxRadiusTiles);
		desiredWorkers = Math.Clamp(desiredWorkers, 0, WorkZone.MaxDesiredWorkers);
		lock (_lock)
		{
			int id = _nextZoneId++;
			zone = new Zone(id, $"Зона #{id}", cx, cy, r, desiredWorkers, jobMask, CalcChunkIndicesForDisc(cx, cy, r));
			_zones[id] = zone;
			BumpZonesVersion_NoLock();
		}
		ScheduleRepartition();
		Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
		return true;
	}

	// ---------------- Общие операции ----------------

	/// <summary>
	/// §119 P7б (task-17): восстановить Work-зону с СОХРАНЁННЫМ id и полями
	/// (обратная сторона снапшота GetAllZones для ZonePersistence). Вызывается
	/// ТОЛЬКО из пути загрузки колонии (до старта сима/диспатча). Соседние id
	/// не переиспользуются: _nextZoneId всегда растёт (max(restored)+1).
	/// </summary>
	public bool RestoreWorkZoneForPersistence(
		int id, string name, int cx, int cy, int radiusTiles, int desiredWorkers,
		ulong jobMask, int? priorityOverride, bool enabled, int anchorBlockId)
	{
		if (id < 1 || jobMask == 0 || (jobMask & (jobMask - 1)) != 0 || (jobMask & 1UL) != 0)
			return false;
		int w = _mapW, h = _mapH;
		if (cx < 0 || cy < 0 || cx >= w || cy >= h)
			return false;
		int r = Math.Clamp(radiusTiles, WorkZone.MinRadiusTiles, WorkZone.MaxRadiusTiles);
		lock (_lock)
		{
			if (_zones.ContainsKey(id))
				return false; // дубликат в файле — вторую молча не ставим
			if (id >= _nextZoneId)
				_nextZoneId = id + 1;
			var zone = new Zone(id, string.IsNullOrEmpty(name) ? $"Зона #{id}" : name,
				cx, cy, r, Math.Clamp(desiredWorkers, 0, WorkZone.MaxDesiredWorkers),
				jobMask, CalcChunkIndicesForDisc(cx, cy, r))
			{
				PriorityOverride = priorityOverride is >= 1 and <= 10 ? priorityOverride : null,
				Enabled = enabled,
				AnchorBlockId = anchorBlockId
			};
			_zones[id] = zone;
			BumpZonesVersion_NoLock();
		}
		ScheduleRepartition();
		Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
		return true;
	}

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
		// §119 P1: очередь удалённой зоны падает в непокрытые (спека: снос зоны —
		// работы падают в непокрытые; репуш подберёт ≤30с).
		ZoneJobRouter.Instance.OnZoneRemoved(id);
		ScheduleRepartition();
		Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
		return true;
	}

	/// <summary>
	/// §119 P0/P5: удалить Work-зону с ЦЕНТРОМ в указанной клетке (путь для
	/// сноса центрального блока-якоря). Farm-зоны не имеют центра — не матчатся.
	/// Работы обрезанной зоны по контракту §119 ОСТАЮТСЯ у зоны до её смерти:
	/// здесь зона исчезает целиком, а непокрытые работы подберёт репуш (P1).
	/// </summary>
	public bool RemoveZoneAtCenter(int cx, int cy)
	{
		int targetId = -1;
		lock (_lock)
		{
			foreach (var kvp in _zones)
			{
				var z = kvp.Value;
				if (z.Kind == ZoneKind.Work && z.CenterX == cx && z.CenterY == cy)
				{
					targetId = kvp.Key;
					break;
				}
			}
		}
		return targetId != -1 && RemoveZone(targetId);
	}

	// ---------------- §119 P1: партиция покрытий (заглушка P0 → событие P1) ----------------

	private volatile bool _repartitionDirty;

	/// <summary>
	/// §119: заявить пересчёт покрытий зон. P0 — пометка dirty; P1 — дополнительно
	/// будит ZoneJobRouter (битмапы покрытия пересчитаются не чаще 1р/2с —
	/// throttle внутри ZoneCoverageMap).
	/// </summary>
	public void ScheduleRepartition()
	{
		_repartitionDirty = true;
		ZoneJobRouter.Instance.Coverage.RequestRebuild();
	}

	/// <summary>Для P1/тестов: есть ли незавершённая заявка на пересчёт.</summary>
	public bool IsRepartitionPending => _repartitionDirty;

	/// <summary>§119 P1: снять заявку после фактического пересчёта (зовёт ZoneJobRouter.Sweep).</summary>
	public void ClearRepartitionPending() => _repartitionDirty = false;

	/// <summary>§119 P1: версия состава зон (для покрытий/диагностики).</summary>
	public int ZonesVersion => Volatile.Read(ref _zonesVersion);

	/// <summary>
	/// §119 P0: поиск зоны по клетке. Farm — точная битмапа _tileToZoneId;
	/// Work — формула радиуса по списку зон (зон немного; битмапа покрытия
	/// клетка→зона на каждый тип работ — P1). При перекрытии Work-зон —
	/// ближайший центр (детерминизм; партиция P1 заменит на AssignedCount).
	/// </summary>
	public bool TryGetZoneAt(int x, int y, out Zone zone)
	{
		zone = FindZoneAt(x, y);
		return zone != null;
	}

	private Zone FindZoneAt(int x, int y)
	{
		lock (_lock)
		{
			if (_tileToZoneId.TryGetValue((x, y), out int zoneId) && _zones.TryGetValue(zoneId, out var farm))
				return farm;
			Zone best = null;
			long bestDist = long.MaxValue;
			foreach (var z in _zones.Values)
			{
				if (z.Kind != ZoneKind.Work || !z.ContainsTile(x, y))
					continue;
				long dx = x - z.CenterX;
				long dy = y - z.CenterY;
				long d = dx * dx + dy * dy;
				if (d < bestDist || (d == bestDist && best != null && z.Id > best.Id))
				{
					best = z;
					bestDist = d;
				}
			}
			return best;
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
	/// Возвращаемый список НЕ модифицировать (кэш). Только для sim-потока.
	/// </summary>
	public List<Zone> GetDispatchSnapshot()
	{
		int v = Volatile.Read(ref _zonesVersion);
		var snap = Volatile.Read(ref _dispatchSnap);
		if (snap != null && v == Volatile.Read(ref _dispatchSnapVersion))
			return snap;
		lock (_lock)
		{
			v = _zonesVersion;
			if (_dispatchSnap != null && v == _dispatchSnapVersion)
				return _dispatchSnap;
			var fresh = new List<Zone>(_zones.Values);
			fresh.Sort(static (a, b) => a.Id.CompareTo(b.Id));
			_dispatchSnap = fresh;
			Volatile.Write(ref _dispatchSnapVersion, v);
			return fresh;
		}
	}

	private void BumpZonesVersion()
	{
		Interlocked.Increment(ref _zonesVersion);
	}

	/// <summary>То же, но под уже взятым _lock (инкремент int атомарен и без Interlocked).</summary>
	private void BumpZonesVersion_NoLock()
	{
		_zonesVersion++;
	}

	/// <summary>
	/// §119 P0: задать РАДИУС зоны (64..512, кламп). Тайлы не перестраиваются —
	/// пересчитываются только аналитические ChunkIndices. Пересечения не режем
	/// (партиция спорных клеток — P1, ScheduleRepartition).
	/// </summary>
	public bool SetWorkRadius(int id, int radius)
	{
		int clamped = Math.Clamp(radius, WorkZone.MinRadiusTiles, WorkZone.MaxRadiusTiles);
		lock (_lock)
		{
			if (!_zones.TryGetValue(id, out var zone) || zone.Kind != ZoneKind.Work)
				return false;
			zone.RadiusTiles = clamped;
			zone.TilesTarget = clamped; // легаси-поле: до P2 хранит радиус
			zone.ChunkIndices = CalcChunkIndicesForDisc(zone.CenterX, zone.CenterY, clamped);
			zone.ResetAssigned();
			BumpZonesVersion_NoLock();
		}
		ScheduleRepartition();
		Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
		return true;
	}

	/// <summary>§119: задать ЗАЯВКУ на число рабочих (0..999, без потолка).</summary>
	public bool SetWorkDesiredWorkers(int id, int v)
	{
		bool ok;
		lock (_lock)
		{
			ok = _zones.TryGetValue(id, out var zone) && zone.Kind == ZoneKind.Work;
			if (ok)
			{
				zone.DesiredWorkers = Math.Clamp(v, 0, WorkZone.MaxDesiredWorkers);
				BumpZonesVersion_NoLock();
			}
		}
		// §119 P1: DesiredWorkers == 0 снимает владение клетками — покрытию нужен пересчёт.
		if (ok)
			ScheduleRepartition();
		return ok;
	}

	/// <summary>§119: вкл/выкл зоны (выключенная не владеет клетками — покрытию нужен пересчёт).</summary>
	public bool SetWorkEnabled(int id, bool enabled)
	{
		bool ok;
		lock (_lock)
		{
			ok = _zones.TryGetValue(id, out var zone) && zone.Kind == ZoneKind.Work;
			if (ok)
			{
				zone.Enabled = enabled;
				BumpZonesVersion_NoLock();
			}
		}
		if (ok)
		{
			ScheduleRepartition();
			Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
		}
		return ok;
	}

	public bool SetWorkPriorityOverride(int id, int v)
	{
		int clamped = Math.Clamp(v, 1, 10);
		lock (_lock)
		{
			if (!_zones.TryGetValue(id, out var zone) || zone.Kind != ZoneKind.Work)
				return false;
			zone.PriorityOverride = clamped;
		}
		Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
		return true;
	}

	public void SetHoveredTile(int x, int y)
	{
		Zone newHovered = FindZoneAt(x, y);
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
		Zone targetZone = FindZoneAt(x, y);
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
			zone.ResetAssigned();
			int zoneAssigned = 0;
			var chunks = zone.ChunkIndices;
			var tiles = zone.Tiles;
			if (chunks == null || tiles == null) continue;
			foreach (int chunkIdx in chunks)
			{
				if (zone.DesiredWorkers > 0 && zoneAssigned >= zone.DesiredWorkers)
					break;
				if (JobDispatcher.Instance.JobIndex.GetChunkJobCount(chunkIdx) == 0)
					continue;
				int[] buf = _zoneBuffer.Value;
				int workerCount = JobDispatcher.Instance.IdleWorkers.CollectIdleWorkersInChunk(chunkIdx, WorkersPerChunkBudget, buf, pool);
				if (workerCount == 0) continue;
				int assigned = 0;
				for (int wi = 0; wi < workerCount; wi++)
				{
					if (assigned >= MaxAssignPerChunk)
						break;
					if (zone.DesiredWorkers > 0 && zoneAssigned >= zone.DesiredWorkers)
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
						if (JobDispatcher.Instance.JobIndex.TryClaimForWorkerInChunk(
							chunkIdx, workerTx, workerTy,
							pool.EquippedTools[agentIndex],
							pool, agentIndex, ctx,
							out var activeJob))
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
										if (!zone.Matches(activeJob.TypeId) || !zone.ContainsTile(activeJob.TargetX, activeJob.TargetY))
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

	public void ClearAll()
	{
		lock (_lock)
		{
			_zones.Clear();
			_tileToZoneId.Clear();
			_nextZoneId = 1;
			SelectedZone = null;
			HoveredZone = null;
			_repartitionDirty = false;
			BumpZonesVersion_NoLock();
		}
		// §119 P1: смена мира — роутер тоже под ноль (очереди/покрытие старого мира невалидны).
		ZoneJobRouter.Instance.Reset();
		Callable.From(() => OnZonesUpdated?.Invoke()).CallDeferred();
	}
}
