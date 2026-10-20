using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Иерархический поиск пути (упрощённый HPA*): карта делится на регионы
/// (по умолчанию 16x16 тайлов), путь строится сначала ГРУБО через граф
/// регионов (A* по регионам), а затем ДЕТАЛИЗИРУЕТСЯ полноценным A* по
/// клеткам в узком «окне» из 1-3 соседних регионов — от входа условной
/// зоны к её выходу для входа в следующую зону.
///
/// Особенности:
///  - Граничные «порталы» — непрерывные проходимые отрезки общей границы
///    двух регионов; якорь портала — центральная клетка отрезка.
///  - A* на детальном уровне работает по локальному окну (клетки лишь
///    нескольких регионов), что даёт копеечную стоимость даже на 512x512.
///  - Кэш: грубые пути (регион->регион) и детальные сегменты
///    (клетка->клетка) кэшируются; инвалидируется при постройке стен.
///  - Потокобезопасен: построение оверлея под lock, scratch-буферы A*
///    ThreadLocal, кэш-словари под отдельным lock'ом.
///  - Не зависит от Godot API (чистый C#).
/// </summary>
public sealed class HierarchicalPathfinder
{
	public static HierarchicalPathfinder Instance { get; } = new();

	/// <summary>Размер региона в тайлах (степень двойки: 4 -> 16x16 тайлов).</summary>
	private const int RegionShift = 4;
	private const int RegionSize = 1 << RegionShift;

	// Стоимость прохождения клетки: 0 — блокирована (стена), 1 — трава.
	// #13: CostWater=6 — это ФЛАГ типа клетки («вода»), а НЕ множитель пути.
	// Реальный штраф воды — ×3 к шагу (step *= 3 ниже: 10→30 / 14→42).
	// _cost[] как вес напрямую использовать нельзя (дало бы ×6 вместо ×3).
	public const byte CostBlocked = 0;
	public const byte CostGrass = 1;
	public const byte CostWater = 6;
	// Сколько водных тайлов подряд LOS прощает напрямик (мелкая лужа/брод).
	// Длиннее — строим иерархический путь с водным штрафом (обход).
	private const int MaxDirectWaterTiles = 3;

	// Лимиты кэша (простая политика: при переполнении — полная очистка).
	private const int MaxRegionPathCache = 32768;
	private const int MaxSegmentCache = 65536;

	// Детальное окно: до 3x3 регионов = (3*16)^2 = 2304 клетки.
	private const int LocalCapacity = (3 * RegionSize) * (3 * RegionSize);
	// §118 фаза 5: предел роста окна локального A*. Якоря порталов лежат на границе
	// ДВУХ регионов, поэтому сегмент «якорь→якорь» требует окна 4x4 региона
	// (4096 клеток). Раньше окно жёстко ограничивалось 2304 и всё, что больше,
	// отбраковывалось — ИЕРАРХИЧЕСКАЯ СБОРКА ПУТИ ЛОМАЛАСЬ НА КАЖДОМ СЕГМЕНТЕ
	// МЕЖДУ РЕГИОНАМИ (обход воды, стен, гор). Буферы теперь растут по требованию.
	private const int LocalCapacityMax = (5 * RegionSize) * (5 * RegionSize);

	private int _mapWidth;
	private int _mapHeight;
	private int _regionDimX;
	private int _regionDimY;

	private byte[] _cost;              // [mapWidth*mapHeight]
	// §118 фаза 5 (восстановлено после потери рабочей копии): публикуемый СНИМОК
	// графа. Оверлей перестраивается в локальных массивах и публикуется одной
	// записью Volatile — читатели (16 потоков симуляции + FlowField) никогда не видят
	// полуобновлённый граф (порталы, обрезанные на середине построения).
	private NavGridState _state;
	public NavGridState State => Volatile.Read(ref _state);
	public byte[] Cost => State?.Cost ?? _cost;
	public int MapWidth => State?.MapWidth ?? _mapWidth;
	public int MapHeight => State?.MapHeight ?? _mapHeight;

	private int[] _edgeStart;          // [regions*4] стартовый индекс в _portals
	private int[] _edgeCount;          // [regions*4] число порталов у (регион, направление)
	private int[] _portals;            // плоский массив якорей порталов (packed cell)
	private int _portalCount;
	private int _buildVersion;
	private volatile bool _dirty = true;

	private SimulationContext _ctx;

	// §118 фаза 5: оверлей можно строить не только из SimulationContext, но и из
	// ВНЕШНЕЙ сетки стоимостей (остров 1024² из ActiveArea, бенчмарки, будущий
	// единый мир §118.1 «одинаковые функции везде»). null → строим из _ctx.
	private byte[] _externalCost;
	private int _externalWidth;
	private int _externalHeight;

	/// <summary>
	/// §118.5: допустимое отношение «длина найденного пути / чебышёвское расстояние».
	/// Чебышёв — математическая нижняя граница числа шагов (диагональ = 1 шаг),
	/// поэтому путь длиннее k·L означает «цель фактически недостижима» (обход реки
	/// вокруг света), а НЕ «далеко идти». Дальность не режем — решаем по математике
	/// и отдаём задачу кулдауну вызывающего (JobValidator/AgentMovementService).
	/// </summary>
	public const int MaxDetourRatio = 6;

	/// <summary>Короче этого чебышёва пути не судим: у малых L отношение шумное (обход стены 3x3).</summary>
	private const int MinDetourJudgeCells = 48;

	private long _detourRejects;
	/// <summary>Сколько путей отвергнуто как абсурдный обход (наблюдаемость, §118.5).</summary>
	public long DetourRejects => Interlocked.Read(ref _detourRejects);

	// §118 фаза 5: счётчики причин отказа — чтобы «путь не найден» не был чёрным ящиком.
	private long _failNoRegionPath;
	private long _failNoSegment;
	private long _failDetour;
	private long _failPortal;
	private long _failOverflow;

	/// <summary>Диагностика отказов: regionPath=N segment=N detour=N portal=N overflow=N.</summary>
	public string FailureStats =>
		$"regionPath={Interlocked.Read(ref _failNoRegionPath)} segment={Interlocked.Read(ref _failNoSegment)} detour={Interlocked.Read(ref _failDetour)} portal={Interlocked.Read(ref _failPortal)} overflow={Interlocked.Read(ref _failOverflow)}";

	// §118.5: профилировка «куда уходит время» — тики Stopwatch по фазам.
	private long _profileRegionPathTicks;
	private long _profileSegmentTicks;
	private long _profileLocalSearches;
	private long _profileSegmentCacheHits;

	/// <summary>Профиль: время в регион-A* и в локальных сегментах (мс) + число поисков.</summary>
	public string Profile
	{
		get
		{
			double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			return $"regionPath={Interlocked.Read(ref _profileRegionPathTicks) * f:F0}мс " +
				$"segments={Interlocked.Read(ref _profileSegmentTicks) * f:F0}мс " +
				$"localSearches={Interlocked.Read(ref _profileLocalSearches)} " +
				$"segCacheHits={Interlocked.Read(ref _profileSegmentCacheHits)}";
		}
	}

	public void ResetProfile()
	{
		Interlocked.Exchange(ref _profileRegionPathTicks, 0);
		Interlocked.Exchange(ref _profileSegmentTicks, 0);
		Interlocked.Exchange(ref _profileLocalSearches, 0);
		Interlocked.Exchange(ref _profileSegmentCacheHits, 0);
	}

	// ------------------------------------------------------------------
	// Бюджетный гейт (§118.5): амортизация вместо обрезания функциональности
	// ------------------------------------------------------------------

	/// <summary>
	/// Сколько поисков пути разрешено запускать за окно кадра. Юзер: минимум 50
	/// на кадр, затем «в 3 раза меньше» → 17 за кадр при 60 FPS ≈ 1000 поисков/с.
	/// </summary>
	public int SearchesPerFrame { get; set; } = 17;

	/// <summary>Длина окна бюджета в тиках Stopwatch (по умолчанию 1 кадр при 60 FPS).</summary>
	private long _budgetWindowTicks = System.Diagnostics.Stopwatch.Frequency / 60;

	private long _budgetWindowStart;
	private int _budgetUsed;
	private long _budgetDeferred;

	/// <summary>Сколько запросов отложено бюджетом (наблюдаемость).</summary>
	public long BudgetDeferred => Interlocked.Read(ref _budgetDeferred);

	public void ConfigureBudget(int searchesPerFrame, double frameSeconds = 1.0 / 60.0)
	{
		SearchesPerFrame = Math.Max(1, searchesPerFrame);
		_budgetWindowTicks = Math.Max(1, (long)(System.Diagnostics.Stopwatch.Frequency * frameSeconds));
		Interlocked.Exchange(ref _budgetWindowStart, System.Diagnostics.Stopwatch.GetTimestamp());
		Interlocked.Exchange(ref _budgetUsed, 0);
	}

	/// <summary>
	/// Пытается зарезервировать слот поиска в текущем кадровом окне.
	/// false = бюджет исчерпан: вызывающий ОБЯЗАН перенести запрос на следующий
	/// кадр (сохранив прогресс), а не считать цель недостижимой.
	/// </summary>
	public bool TryReserveSearch()
	{
		long now = System.Diagnostics.Stopwatch.GetTimestamp();
		long start = Volatile.Read(ref _budgetWindowStart);
		if (now - start >= _budgetWindowTicks)
		{
			// Новое окно: CAS, чтобы сбрасывал ровно один поток.
			if (Interlocked.CompareExchange(ref _budgetWindowStart, now, start) == start)
				Interlocked.Exchange(ref _budgetUsed, 0);
		}

		int used = Interlocked.Increment(ref _budgetUsed);
		if (used <= SearchesPerFrame)
			return true;

		Interlocked.Increment(ref _budgetDeferred);
		return false;
	}

	private readonly object _buildLock = new();
	// P-баланс: кэши — ConcurrentDictionary вместо Dictionary+_cacheLock.
	// _cacheLock сериализовал все 16 потоков Parallel-фаз на КАЖДОМ запросе пути:
	// один «тяжёлый» ComputeLocalSegment держал lock, остальные 15 ждали —
	// классический straggler при низкой средней загрузке CPU.
	private readonly ConcurrentDictionary<int, int[]> _regionPathCache = new(4, 1024);
	private readonly ConcurrentDictionary<ulong, int[]> _segmentCache = new(4, 4096);

	private sealed class SearchBuffers
	{
		// Детальный A* (локальное окно). §118 фаза 5: массивы НЕ readonly — окно
		// растёт по требованию (LocalCapacityMax), иначе сегмент между якорями
		// соседних регионов (окно 4x4 = 4096 клеток) не помещался и путь рвался.
		public int[] G = new int[LocalCapacity + 1];
		public int[] Parent = new int[LocalCapacity + 1];
		public int[] Open = new int[LocalCapacity + 1];
		public bool[] Closed = new bool[LocalCapacity + 1];
		public int[] Value = new int[LocalCapacity + 1];

		/// <summary>Гарантирует место под окно из cap клеток (растёт, не сжимается).</summary>
		public void EnsureLocalCapacity(int cap)
		{
			int need = cap + 1;
			if (need <= G.Length)
				return;
			G = new int[need];
			Parent = new int[need];
			Open = new int[need];
			Closed = new bool[need];
			Value = new int[need];
		}

		// Грубый A* (граф регионов).
		public readonly int[] RG = new int[4096];
		public readonly int[] RParent = new int[4096];
		public readonly int[] ROpen = new int[4096];
		public readonly bool[] RClosed = new bool[4096];
		public readonly int[] RValue = new int[4096];

		// PERF F4: scratch для TryFindPath/SmoothPath. Переиспользуемые буферы
		// вместо new int[]/List<int>/ToArray в горячем пути. Капасити: якоря ≤ 256
		// переходов регионов; сборка пути и сглаживание ≤ 4096 точек.
		public readonly int[] Anchor = new int[512];
		public readonly int[] PathScratch = new int[4096];
		public readonly int[] SmoothScratch = new int[4096];
		public readonly int[] RevScratch = new int[4096];
	}

	private readonly ThreadLocal<SearchBuffers> _buffers = new(() => new SearchBuffers());

	private enum Dir : byte
	{
		North = 0,
		East = 1,
		South = 2,
		West = 3,
		None = 4
	}

	// ------------------------------------------------------------------
	// Публичный API
	// ------------------------------------------------------------------

	/// <summary>
	/// Безусловно сбрасывает все кэши, граф навигации и отметки грязных данных
	/// (при смене сида мира или карты).
	/// </summary>
	public void Reset()
	{
		lock (_buildLock)
		{
			_state = null;
			_cost = null;
			_externalCost = null;
			_edgeStart = null;
			_edgeCount = null;
			_portals = null;
			_portalCount = 0;
			_dirty = true;
			_lastInvalidateMs = 0;
			_regionPathCache.Clear();
			_segmentCache.Clear();
			_buildVersion++;
		}
	}

	/// <summary>
	/// Устанавливает контекст симуляции (источник данных карты). Вызывается
	/// один раз при старте симуляции. Сброс оверлея — через Invalidate() или Reset().
	/// </summary>
	public void Initialize(SimulationContext ctx)
	{
		lock (_buildLock)
		{
			Reset();
			_ctx = ctx;
		}
	}

	/// <summary>
	/// §118 фаза 5: инициализация оверлея из ГОТОВОЙ сетки стоимостей
	/// (0 = блок, 1 = проходимо; вода помечается CostWater). Не требует
	/// SimulationContext — это путь для острова (ActiveArea) и бенчмарков.
	/// Массив НЕ копируется: владение переходит пасфайндеру до следующего вызова.
	/// </summary>
	public void InitializeCost(byte[] cost, int width, int height)
	{
		if (cost == null)
			throw new ArgumentNullException(nameof(cost));
		if (width <= 0 || height <= 0)
			throw new ArgumentOutOfRangeException(nameof(width), "Размер сетки должен быть положительным.");
		if ((long)width * height > cost.Length)
			throw new ArgumentException(
				$"Сетка {width}x{height} больше массива стоимостей ({cost.Length}).", nameof(cost));

		lock (_buildLock)
		{
			Reset();
			_ctx = null;
			_externalCost = cost;
			_externalWidth = width;
			_externalHeight = height;
		}
	}

	/// <summary>
	/// Сбрасывает кэши и помечает оверлей на перестройку (при постройке стен).
	/// Дебаунс по wall-clock: 100 завершений стройки в один тик раньше давали
	/// 100 последовательных Clear (каждый — снос 65k entries под _buildLock).
	/// Версия в ключе кэша не нужна: Clear под гейтом достаточно редок.
	/// </summary>
	private long _lastInvalidateMs;

	public void Invalidate()
	{
		long now = System.Environment.TickCount64;
		if (now - Volatile.Read(ref _lastInvalidateMs) < 200)
			return;
		lock (_buildLock)
		{
			now = System.Environment.TickCount64;
			if (now - _lastInvalidateMs < 200)
				return;
			_lastInvalidateMs = now;
			_buildVersion++;
			_dirty = true;
			_regionPathCache.Clear();
			_segmentCache.Clear();
		}
	}

	/// <summary>
	/// Строит иерархический путь от тайла (sx, sy) к (tx, ty).
	/// При успехе возвращает true; если прямая видимость есть — true с
	/// count == 0 (двигаться напрямую). Точки (без стартовой клетки)
	/// записываются в <paramref name="outPath"/> как int-коды y*mapWidth+x,
	/// количество — в <paramref name="count"/>.
	/// </summary>
	public bool TryFindPath(int sx, int sy, int tx, int ty, out int[] outPath, out int count)
	{
		outPath = null;
		count = 0;

		if (sx == tx && sy == ty)
			return true;

		// Деградация при «пустом» оверлее (симуляция не инициализирована) или
		// когда граф помечен грязным (стена/карта сменились) — перестраиваем.
		if (_state == null || _dirty)
		{
			EnsureOverlay();
			if (_state == null)
				return false;
		}

		// Прямая видимость — дешёвый путь без построения.
		// НО: вода в LOS раньше игнорировалась — агент шёл напрямик через
		// озеро/реку (только ×0.45 к скорости) вместо обхода. Если водный
		// отрезок длиннее порога — идём в иерархический A* (у него вода
		// стоит ×3 и маршрут обогнёт водоём, где это выгодно).
		// #14: один проход Брезенхема вместо двух (HasLineOfSight +
		// WaterCrossingLength дублировали обход) — LineOfSightWithWater
		// возвращает и флаг стен, и длину водного отрезка сразу.
		if (LineOfSightWithWater(sx, sy, tx, ty, out int directWater)
			&& directWater <= MaxDirectWaterTiles)
			return true;

		int sr = RegionOfCell(sx, sy);
		int tr = RegionOfCell(tx, ty);

		int[] regionPath = GetRegionPathCached(sr, tr);
		if (regionPath == null || regionPath.Length < 2)
		{
			Interlocked.Increment(ref _failNoRegionPath);
			return false;
		}

		int regionCount = regionPath.Length;

		// PERF F4: якоря и сборка пути — в thread-local scratch (без new int[]/List
		// на запрос: ~500 запросов × 6 аллокаций за кадр на 100x давили Gen0 и
		// останавливали все потоки, включая рендер). Кэш regionPath — shared
		// (ConcurrentDictionary), но scratch — per-thread, гонки нет.
		var scratch = _buffers.Value;
		// anchorPairs хранится парами (x,y) в одном массиве: [x0..xn, y0..yn].
		int anchorPairs = regionCount - 1;
		// @destroyer: anchorPairs<=0 невозможен (regionPath.Length>=2 проверен
		// выше), но оставляем guard — дешевле, чем доказывать инвариант.
		// Переполнение int при anchorPairs*2: regionCount ограничен числом
		// регионов карты (≤1024), переполнения нет.
		if (anchorPairs <= 0 || anchorPairs * 2 > scratch.Anchor.Length)
			return false; // регион-путь патологичен — retry на следующем кадре
		Span<int> anchors = scratch.Anchor.AsSpan(0, anchorPairs * 2);
		Span<int> anchorX = anchors.Slice(0, anchorPairs);
		Span<int> anchorY = anchors.Slice(anchorPairs, anchorPairs);

		for (int t = 0; t < regionCount - 1; t++)
		{
					if (!PickBestPortal(regionPath[t], regionPath[t + 1], sx, sy, tx, ty,
						out anchorX[t], out anchorY[t]))
					{
						Interlocked.Increment(ref _failPortal);
						return false; // порталы между соседями исчезли — пересчёт на след. кадре
					}
		}

		Span<int> path = scratch.PathScratch;
		int pathLen = 0;

		for (int i = 0; i < regionCount; i++)
		{
			int fromX = i == 0 ? sx : anchorX[i - 1];
			int fromY = i == 0 ? sy : anchorY[i - 1];
			int toX = i == regionCount - 1 ? tx : anchorX[i];
			int toY = i == regionCount - 1 ? ty : anchorY[i];

			if (fromX == toX && fromY == toY)
				continue;

			int[] seg = GetSegmentCached(fromX, fromY, toX, toY);
			if (seg == null || seg.Length < 2)
				return false;

			// Копируем сегмент, исключая его первый элемент (либо это старт,
			// либо якорь, уже добавленный предыдущим сегментом).
			if (pathLen + seg.Length - 1 > path.Length)
			{
				Interlocked.Increment(ref _failOverflow);
				return false; // путь длиннее scratch — retry (кэш сегментов цел)
			}
			for (int k = 1; k < seg.Length; k++)
			{
				path[pathLen++] = seg[k];
			}
		}

		if (pathLen == 0)
			return true;

		// §118.5: математическая отсечка абсурдного обхода (см. MaxDetourRatio).
		// Цель достижима только «через полмира» → считаем недостижимой; вызывающий
		// получит false и посадит задачу на свой кулдаун (дальность НЕ урезаем).
		if (IsDetourAbsurd(PathCellLength(path[..pathLen], sx, sy), sx, sy, tx, ty))
		{
			Interlocked.Increment(ref _detourRejects);
			Interlocked.Increment(ref _failDetour);
			return false;
		}

		int smoothed = SmoothPath(sx, sy, tx, ty, path.Slice(0, pathLen), scratch.SmoothScratch);
		if (smoothed <= 0)
			return false;

		// Единственная аллокация на УСПЕШНЫЙ немгновенный путь: точный sized-массив
		// результата. Для горячего пути симуляции используйте TryFindPathScratch.
		var result = new int[smoothed];
		scratch.SmoothScratch.AsSpan(0, smoothed).CopyTo(result);

		outPath = result;
		count = result.Length;
		return true;
	}

	/// <summary>
	/// ZERO-ALLOC версия для горячего пути симуляции: возвращает ссылку на per-thread
	/// scratch.SmoothScratch без создания нового массива. Вызывающий поток обязан
	/// прочитать первые <paramref name="count"/> элементов немедленно, не сохраняя ссылку.
	/// </summary>
	public bool TryFindPathScratch(int sx, int sy, int tx, int ty, out int[] scratchBuf, out int count)
	{
		scratchBuf = null;
		count = 0;

		if (sx == tx && sy == ty)
			return true;

		// §118 ф.5 (найдено destroyer'ом): ГОРЯЧИЙ путь обязан перестраивать оверлей
		// и по флагу _dirty, а не только при пустом _cost. Иначе после постройки
		// стены Invalidate() ставит _dirty, но симуляция (она ходит именно через
		// TryFindPathScratch) продолжает пользоваться устаревшим графом и агенты
		// ходят сквозь новые стены, пока кто-то не дёрнет TryFindPath.
		if (_state == null || _dirty)
		{
			EnsureOverlay();
			if (_state == null)
				return false;
		}

		if (LineOfSightWithWater(sx, sy, tx, ty, out int directWater)
			&& directWater <= MaxDirectWaterTiles)
			return true;

		int sr = RegionOfCell(sx, sy);
		int tr = RegionOfCell(tx, ty);

		int[] regionPath = GetRegionPathCached(sr, tr);
		if (regionPath == null || regionPath.Length < 2)
		{
			Interlocked.Increment(ref _failNoRegionPath);
			return false;
		}

		int regionCount = regionPath.Length;
		var scratch = _buffers.Value;
		int anchorPairs = regionCount - 1;
		if (anchorPairs <= 0 || anchorPairs * 2 > scratch.Anchor.Length)
			return false;

		Span<int> anchors = scratch.Anchor.AsSpan(0, anchorPairs * 2);
		Span<int> anchorX = anchors.Slice(0, anchorPairs);
		Span<int> anchorY = anchors.Slice(anchorPairs, anchorPairs);

		for (int t = 0; t < regionCount - 1; t++)
		{
			if (!PickBestPortal(regionPath[t], regionPath[t + 1], sx, sy, tx, ty,
				out anchorX[t], out anchorY[t]))
			{
				return false;
			}
		}

		Span<int> path = scratch.PathScratch;
		int pathLen = 0;

		for (int i = 0; i < regionCount; i++)
		{
			int fromX = i == 0 ? sx : anchorX[i - 1];
			int fromY = i == 0 ? sy : anchorY[i - 1];
			int toX = i == regionCount - 1 ? tx : anchorX[i];
			int toY = i == regionCount - 1 ? ty : anchorY[i];

			if (fromX == toX && fromY == toY)
				continue;

			int[] seg = GetSegmentCached(fromX, fromY, toX, toY);
			if (seg == null || seg.Length < 2)
				return false;

			if (pathLen + seg.Length - 1 > path.Length)
				return false;
			for (int k = 1; k < seg.Length; k++)
			{
				path[pathLen++] = seg[k];
			}
		}

		if (pathLen == 0)
			return true;

		// §118.5: математическая отсечка абсурдного обхода (см. MaxDetourRatio).
		// Цель достижима только «через полмира» → считаем недостижимой; вызывающий
		// получит false и посадит задачу на свой кулдаун (дальность НЕ урезаем).
		if (IsDetourAbsurd(PathCellLength(path[..pathLen], sx, sy), sx, sy, tx, ty))
		{
			Interlocked.Increment(ref _detourRejects);
			Interlocked.Increment(ref _failDetour);
			return false;
		}

		int smoothed = SmoothPath(sx, sy, tx, ty, path.Slice(0, pathLen), scratch.SmoothScratch);
		if (smoothed <= 0)
			return false;

		// Ноль аллокаций: отдаём ThreadLocal буфер напрямую
		scratchBuf = scratch.SmoothScratch;
		count = smoothed;
		return true;
	}

	// ------------------------------------------------------------------
	// Построение оверлея (стоимости + порталы регионов)
	// ------------------------------------------------------------------

	private void EnsureOverlay()
	{
		lock (_buildLock)
		{
			if (_state != null && !_dirty)
				return;

			if (_ctx == null && _externalCost == null)
			{
				// Симуляция ещё не стартовала — оверлей недоступен.
				return;
			}

			int w;
			int h;
			byte[] cost;

			if (_externalCost != null)
			{
				// §118 фаза 5: внешняя сетка стоимостей (остров/бенчмарк) — берём как есть.
				w = _externalWidth;
				h = _externalHeight;
				cost = _externalCost;
			}
			else
			{
				w = _ctx.MapWidth;
				h = _ctx.MapHeight;
				cost = new byte[w * h];

				for (int y = 0; y < h; y++)
				{
					int rowBase = y * w;
					for (int x = 0; x < w; x++)
					{
						byte c;
						// Камень проходим (как трава): россыпь не блокирует путь. Вода и горы блокируют путь.
						if (_ctx.SolidWalls[x, y] || _ctx.Ground[x, y] == TileType.Mountain || _ctx.Ground[x, y] == TileType.Water)
							c = CostBlocked;
						else
							c = CostGrass;
						cost[rowBase + x] = c;
					}
				}
			}

			_mapWidth = w;
			_mapHeight = h;
			_regionDimX = (w + RegionSize - 1) >> RegionShift;
			_regionDimY = (h + RegionSize - 1) >> RegionShift;
			_cost = cost;

			// ---- Портал: проходимый отрезок общей границы двух регионов.
			// Каждую общую границу регистрируем от «юго-восточной» стороны,
			// чтобы обратный переход пользовался тем же набором якорей.
			int regionCount = _regionDimX * _regionDimY;
			_edgeStart = new int[regionCount * 4];
			_edgeCount = new int[regionCount * 4];
			Array.Fill(_edgeStart, -1);
			Array.Clear(_edgeCount, 0, _edgeCount.Length);
			_portals = new int[regionCount * 4 * 16];
			_portalCount = 0;

			for (int ry = 0; ry < _regionDimY; ry++)
			{
				for (int rx = 0; rx < _regionDimX; rx++)
				{
					int rid = ry * _regionDimX + rx;

					// Восточная граница (сосед справа).
					if (rx + 1 < _regionDimX)
					{
						_edgeStart[rid * 4 + (byte)Dir.East] = _portalCount;
						_edgeCount[rid * 4 + (byte)Dir.East] =
							BuildEdgePortals(rx, ry, Dir.East, cost, w, h);
					}

					// Южная граница.
					if (ry + 1 < _regionDimY)
					{
						_edgeStart[rid * 4 + (byte)Dir.South] = _portalCount;
						_edgeCount[rid * 4 + (byte)Dir.South] =
							BuildEdgePortals(rx, ry, Dir.South, cost, w, h);
					}

					// Северная/западная — те же якоря, что у верхнего/левого
					// соседа (их ребра South/East).
					if (ry > 0)
					{
						int northNeighborBase = (ry - 1) * _regionDimX * 4 + rx * 4 + (byte)Dir.South;
						_edgeStart[rid * 4 + (byte)Dir.North] = _edgeStart[northNeighborBase];
						_edgeCount[rid * 4 + (byte)Dir.North] = _edgeCount[northNeighborBase];
					}
					if (rx > 0)
					{
						_edgeStart[rid * 4 + (byte)Dir.West] = _edgeStart[rid * 4 - 4 + (byte)Dir.East];
						_edgeCount[rid * 4 + (byte)Dir.West] = _edgeCount[rid * 4 - 4 + (byte)Dir.East];
					}
				}
			}

			// Публикуем снимок ОДНОЙ записью: читатели видят либо старый граф
			// целиком, либо новый целиком (порталы не бывают «полупостроенными»).
			var snapshot = new NavGridState(w, h, _regionDimX, _regionDimY,
				cost, _edgeStart, _edgeCount, _portals, _portalCount);
			Volatile.Write(ref _state, snapshot);
			_dirty = false;
		}
	}

	/// <summary>
	/// Находит непрерывные проходимые отрезки общей границы региона (rx, ry)
	/// с соседом в направлении <paramref name="dir"/> (East или South) и
	/// записывает якоря (центральную клетку отрезка, packed) в _portals.
	/// Возвращает число порталов.
	/// </summary>
	private int BuildEdgePortals(int rx, int ry, Dir dir, byte[] cost, int w, int h)
	{
		int x0 = rx << RegionShift;
		int y0 = ry << RegionShift;
		int x1 = Math.Min(x0 + RegionSize - 1, w - 1);
		int y1 = Math.Min(y0 + RegionSize - 1, h - 1);

		int count = 0;

		if (dir == Dir.East)
		{
			int nx = Math.Min(x1 + 1, w - 1);
			int runStartY = -1;
			for (int y = y0; y <= y1; y++)
			{
				bool a = cost[y * w + x1] != CostBlocked;
				bool b = cost[y * w + nx] != CostBlocked;
				if (a && b)
				{
					if (runStartY < 0) runStartY = y;
				}
				else if (runStartY >= 0)
				{
					_portals[_portalCount++] = ((runStartY + y - 1) >> 1) * w + x1;
					count++;
					runStartY = -1;
				}
			}
			if (runStartY >= 0)
			{
				_portals[_portalCount++] = ((runStartY + y1) >> 1) * w + x1;
				count++;
			}
		}
		else // Dir.South
		{
			int ny = Math.Min(y1 + 1, h - 1);
			int runStartX = -1;
			for (int x = x0; x <= x1; x++)
			{
				bool a = cost[y1 * w + x] != CostBlocked;
				bool b = cost[ny * w + x] != CostBlocked;
				if (a && b)
				{
					if (runStartX < 0) runStartX = x;
				}
				else if (runStartX >= 0)
				{
					_portals[_portalCount++] = y1 * w + ((runStartX + x - 1) >> 1);
					count++;
					runStartX = -1;
				}
			}
			if (runStartX >= 0)
			{
				_portals[_portalCount++] = y1 * w + ((runStartX + x1) >> 1);
				count++;
			}
		}

		return count;
	}

	// ------------------------------------------------------------------
	// Хелперы координат и порталов
	// ------------------------------------------------------------------

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int RegionOfCell(int tx, int ty)
	{
		return (ty >> RegionShift) * _regionDimX + (tx >> RegionShift);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int RegionCoordX(int regionId) => regionId % _regionDimX;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int RegionCoordY(int regionId) => regionId / _regionDimX;

	/// <summary>Направление от региона ra к соседнему региону rb (или None).</summary>
	private Dir DirectionBetween(int ra, int rb)
	{
		int ax = RegionCoordX(ra), ay = RegionCoordY(ra);
		int bx = RegionCoordX(rb), by = RegionCoordY(rb);
		if (by == ay)
		{
			if (bx == ax + 1) return Dir.East;
			if (bx == ax - 1) return Dir.West;
		}
		if (bx == ax)
		{
			if (by == ay + 1) return Dir.South;
			if (by == ay - 1) return Dir.North;
		}
		return Dir.None;
	}

	/// <summary>
	/// Выбирает лучший портал на общей границе регионов ra и rb:
	/// якорь, ближайший к прямой (sx,sy)-(tx,ty). Якорь возвращается в
	/// координатах клетки региона ra.
	/// </summary>
	private bool PickBestPortal(int ra, int rb, int sx, int sy, int tx, int ty,
		out int anchorX, out int anchorY)
	{
		anchorX = -1;
		anchorY = -1;

		Dir dir = DirectionBetween(ra, rb);
		if (dir == Dir.None)
			return false;

		int idx = ra * 4 + (byte)dir;
		int start = _edgeStart[idx];
		int cnt = _edgeCount[idx];
		if (start < 0 || cnt <= 0)
			return false;

		int best = -1;
		int bestScore = int.MaxValue;
		int bestPacked = 0;

		for (int i = 0; i < cnt; i++)
		{
			int portalPacked = _portals[start + i];
			int px = portalPacked % _mapWidth;
			int py = portalPacked / _mapWidth;

			// Расстояние от якоря до прямой S-T (упрощённо: до отрезка через
			// проекцию). Достаточно точности для выбора портала.
			int score = DistSqToSegment(px, py, sx, sy, tx, ty);
			if (score < bestScore)
			{
				bestScore = score;
				best = i;
				bestPacked = portalPacked;
			}
		}

		if (best < 0)
			return false;

		anchorX = bestPacked % _mapWidth;
		anchorY = bestPacked / _mapWidth;
		return true;
	}

	private static int DistSqToSegment(int px, int py, int ax, int ay, int bx, int by)
	{
		int vx = bx - ax, vy = by - ay;
		int wx = px - ax, wy = py - ay;
		int lenSq = vx * vx + vy * vy;
		if (lenSq == 0)
		{
			int offX = px - ax, offY = py - ay;
			return offX * offX + offY * offY;
		}
		float t = (float)(wx * vx + wy * vy) / (float)lenSq;
		t = Math.Max(0f, Math.Min(1f, t));
		float cx = ax + t * vx;
		float cy = ay + t * vy;
		float projDx = px - cx, projDy = py - cy;
		return (int)(projDx * projDx + projDy * projDy);
	}

	// ------------------------------------------------------------------
	// Грубый уровень: A* по графу регионов
	// ------------------------------------------------------------------

	/// <summary>
	/// §118.5: абсурден ли найденный обход. L — чебышёвское расстояние (нижняя
	/// граница числа шагов при диагональном движении). Путь длиннее MaxDetourRatio·L
	/// означает, что цель достижима только «через полмира» — считаем её фактически
	/// недостижимой и отдаём вызывающему его кулдаун. Малые расстояния не судим.
	/// </summary>
	/// <summary>
	/// Истинная длина пути в КЛЕТКАХ. После сглаживания массив содержит опорные
	/// точки, а не каждую клетку, поэтому длину считаем как сумму чебышёвых
	/// расстояний между соседними опорами (они соединены прямой видимостью,
	/// значит путь между ними ≈ этой длине). Без этого k·L «не видел» обходов.
	/// </summary>
	private int PathCellLength(ReadOnlySpan<int> path, int sx, int sy)
	{
		int width = _mapWidth;
		int total = 0;
		int px = sx;
		int py = sy;
		for (int i = 0; i < path.Length; i++)
		{
			int packed = path[i];
			int cx = packed % width;
			int cy = packed / width;
			int dx = cx > px ? cx - px : px - cx;
			int dy = cy > py ? cy - py : py - cy;
			total += dx > dy ? dx : dy;
			px = cx;
			py = cy;
		}
		return total;
	}

	private static bool IsDetourAbsurd(int pathLen, int sx, int sy, int tx, int ty)	{
		int dx = tx > sx ? tx - sx : sx - tx;
		int dy = ty > sy ? ty - sy : sy - ty;
		int chebyshev = dx > dy ? dx : dy;
		if (chebyshev < MinDetourJudgeCells)
			return false;
		return (long)pathLen > (long)MaxDetourRatio * chebyshev;
	}

	private int[] GetRegionPathCached(int sr, int tr)
	{
		// P0-1: без счётчиков кэша — вызываются из тысяч A* в параллельных фазах.
		int key = sr * 4096 + tr;
		if (_regionPathCache.TryGetValue(key, out int[] cached))
		{
			return cached;
		}

		long rpStart = System.Diagnostics.Stopwatch.GetTimestamp();
		int[] path = ComputeRegionPath(sr, tr);
		Interlocked.Add(ref _profileRegionPathTicks, System.Diagnostics.Stopwatch.GetTimestamp() - rpStart);
		if (path == null)
			return null;
		// Переполнение: удаляем четверть старых записей вместо Clear всего кэша
		// (Clear под нагрузкой = thundering herd: все потоки одновременно идут
		// в ComputeRegionPath/ComputeLocalSegment).
		if (_regionPathCache.Count >= MaxRegionPathCache)
		{
			int toRemove = MaxRegionPathCache / 4;
			foreach (var k in _regionPathCache.Keys)
			{
				if (toRemove-- <= 0) break;
				_regionPathCache.TryRemove(k, out _);
			}
		}
		_regionPathCache[key] = path;
		return path;
	}

	/// <summary>
	/// A* по графу регионов: узлы — регионы, ребро существует, если между
	/// регионами есть хотя бы один портал. Гедонистика — клеточное расстояние
	/// между центрами регионов.
	/// </summary>
	private int[] ComputeRegionPath(int sr, int tr)
	{
		var b = _buffers.Value;
		int[] g = b.RG;
		int[] parent = b.RParent;
		int[] open = b.ROpen;
		bool[] closed = b.RClosed;

		int cap = _regionDimX * _regionDimY;
		if (cap > g.Length)
			return null;

		Array.Fill(g, int.MaxValue, 0, cap);
		Array.Fill(parent, -1, 0, cap);
		Array.Fill(closed, false, 0, cap);

		int openCount = 0;
		open[0] = sr;
		openCount = 1;
		g[sr] = 0;

		int trx = RegionCoordX(tr);
		int tryRegionY = RegionCoordY(tr);

		const int MaxExpand = 4096;
		int expanded = 0;

		while (openCount > 0 && expanded < MaxExpand)
		{
			int cur = RegionPop(open, ref openCount);
			if (cur == tr)
				break;
			if (closed[cur])
				continue;
			closed[cur] = true;
			expanded++;

			int cx = RegionCoordX(cur);
			int cy = RegionCoordY(cur);

			// 4 соседа по региональной сетке.
			TryRelaxRegion(cur, cy * _regionDimX + (cx + 1), cx + 1 < _regionDimX,
				trx, tryRegionY, b, open, ref openCount);
			TryRelaxRegion(cur, cy * _regionDimX + (cx - 1), cx > 0,
				trx, tryRegionY, b, open, ref openCount);
			TryRelaxRegion(cur, (cy + 1) * _regionDimX + cx, cy + 1 < _regionDimY,
				trx, tryRegionY, b, open, ref openCount);
			TryRelaxRegion(cur, (cy - 1) * _regionDimX + cx, cy > 0,
				trx, tryRegionY, b, open, ref openCount);
		}

		if (parent[tr] < 0 && sr != tr)
			return null;

		// Восстановление в RevScratch (без List<int>(16)): цепочка parent
		// длиной ≤ числа регионов. Кэшируем shared — возвращаем sized-копию.
		// @destroyer: parent-цикл теоретически возможен при data race, поэтому
		// счётчик итераций ограничен RevScratch.Length — зацикливания нет.
		var rb = _buffers.Value;
		int rn = 0;
		int curNode = tr;
		while (curNode >= 0 && rn < rb.RevScratch.Length)
		{
			rb.RevScratch[rn++] = curNode;
			curNode = parent[curNode];
		}
		if (curNode >= 0)
			return null; // цепочка длиннее scratch — патологично, без пути
		var result = new int[rn];
		for (int i = 0; i < rn; i++)
		{
			result[i] = rb.RevScratch[rn - 1 - i];
		}
		return result;
	}

	private void TryRelaxRegion(int from, int to, bool valid,
		int trx, int tryRegionY, SearchBuffers b, int[] open, ref int openCount)
	{
		if (!valid)
			return;

		// Ребро существует только если между регионами есть портал.
		Dir dir = DirectionBetween(from, to);
		if (dir == Dir.None)
			return;

		int idx = from * 4 + (byte)dir;
		if (_edgeCount[idx] <= 0)
			return;

		if (b.RClosed[to])
			return;

		int tentative = b.RG[from] + 1;
		if (tentative >= b.RG[to])
			return;

		b.RG[to] = tentative;
		b.RParent[to] = from;

		// Индекс по f = g + h в куче.
		long f = (long)tentative + RegionHeuristic(to, trx, tryRegionY);
		RegionPush(open, ref openCount, to, (int)Math.Min(f, int.MaxValue));
	}

	/// <summary>Клеточная (чебышёва) оценка между регионами, в узлах.</summary>
	private int RegionHeuristic(int regionId, int trx, int tryRegionY)
	{
		int dx = Math.Abs(RegionCoordX(regionId) - trx);
		int dy = Math.Abs(RegionCoordY(regionId) - tryRegionY);
		return Math.Max(dx, dy);
	}

	// ------------------------------------------------------------------
	// Детальный уровень: A* по клеткам локального окна
	// ------------------------------------------------------------------

	private int[] GetSegmentCached(int fx, int fy, int tx, int ty)
	{
		int packedFrom = fy * _mapWidth + fx;
		int packedTo = ty * _mapWidth + tx;
		ulong key = ((ulong)packedFrom << 32) | (uint)packedTo;

		// P0-1: без счётчиков кэша (горячий параллельный путь).
		if (_segmentCache.TryGetValue(key, out int[] cached))
		{
			Interlocked.Increment(ref _profileSegmentCacheHits);
			return cached;
		}

		// §118.5: профиль — сколько времени уходит на локальные A* (сегменты).
		long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
		int[] raw = ComputeLocalSegment(fx, fy, tx, ty);
		Interlocked.Add(ref _profileSegmentTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
		Interlocked.Increment(ref _profileLocalSearches);
		if (raw == null)
			return null;

		// §118 ф.5: СГЛАЖИВАЕМ СЕГМЕНТ ЗДЕСЬ, при кэшировании, а не весь путь на
		// каждом запросе. Сегмент ≤ ~48 клеток, поэтому сглаживание дешёвое, а сборка
		// пути из 3000 клеток превращается в сборку из ~50 опорных точек — цена
		// запроса падает в разы при той же траектории.
		var seg = raw;
		var buffers = _buffers.Value;
		int smoothed = SmoothPath(fx, fy, tx, ty, raw.AsSpan(), buffers.SmoothScratch);
		if (smoothed > 0 && smoothed <= buffers.SmoothScratch.Length)
		{
			int lastPacked = buffers.SmoothScratch[smoothed - 1];
			// Кэшируем сглаженный вариант только если он доводит до цели сегмента:
			// иначе (излом коридора) оставляем сырой путь — он гарантированно валиден.
			if (lastPacked == ty * _mapWidth + tx)
			{
				// КОНТРАКТ сборки пути: seg[0] — это НАЧАЛО сегмента (сборка его
				// отбрасывает как уже добавленный якорь/старт). Поэтому сглаженный
				// вариант обязан начинаться с (fx,fy), иначе путь «перепрыгивает»
				// первую опорную точку каждого сегмента.
				seg = new int[smoothed + 1];
				seg[0] = fy * _mapWidth + fx;
				Array.Copy(buffers.SmoothScratch, 0, seg, 1, smoothed);
			}
		}

		// Переполнение: четверть вместо Clear (thundering herd).
		if (_segmentCache.Count >= MaxSegmentCache)
		{
			int toRemove = MaxSegmentCache / 4;
			foreach (var k in _segmentCache.Keys)
			{
				if (toRemove-- <= 0) break;
				_segmentCache.TryRemove(k, out _);
			}
		}
		_segmentCache[key] = seg;
		return seg;
	}

	// ------------------------------------------------------------------
	// Бинарные кучи (lazy-deletion A*)
	// ------------------------------------------------------------------

	private static void PushMin(int[] open, SearchBuffers b, int[] value, ref int openCount, int node, int key)
	{
		int i = openCount;
		open[openCount++] = node;
		value[node] = key;
		while (i > 0)
		{
			int p = (i - 1) >> 1;
			if (value[open[p]] <= key)
				break;
			open[i] = open[p];
			i = p;
		}
		open[i] = node;
	}

	private static int PopMin(int[] open, SearchBuffers b, int[] value, ref int openCount)
	{
		int root = open[0];
		openCount--;
		if (openCount > 0)
		{
			int moved = open[openCount];
			int key = value[moved];
			int i = 0;
			while (true)
			{
				int left = i * 2 + 1;
				if (left >= openCount)
					break;
				int right = left + 1;
				int child = left;
				if (right < openCount && value[open[right]] < value[open[left]])
					child = right;
				if (value[open[child]] >= key)
					break;
				open[i] = open[child];
				i = child;
			}
			open[i] = moved;
		}
		return root;
	}

	private void RegionPush(int[] open, ref int openCount, int node, int key)
	{
		var b = _buffers.Value;
		PushMin(open, b, b.RValue, ref openCount, node, key);
	}

	private int RegionPop(int[] open, ref int openCount)
	{
		var b = _buffers.Value;
		return PopMin(open, b, b.RValue, ref openCount);
	}

	private void LocalPush(int[] open, ref int openCount, int node, int key)
	{
		var b = _buffers.Value;
		PushMin(open, b, b.Value, ref openCount, node, key);
	}

	private int LocalPop(int[] open, ref int openCount)
	{
		var b = _buffers.Value;
		return PopMin(open, b, b.Value, ref openCount);
	}

	/// <summary>
	/// Полноценный A* по клеткам в окне, охватывающем регионы обеих точек
	/// плюс одно кольцо запаса (до 3x3 регионов = 2304 клетки). Это «от входа
	/// зоны к её выходу»: точка входа и точка выхода лежат на границах,
	/// окно включает оба региона, поэтому путь может свободно пересекать
	/// границу в любом легальном месте.
	/// </summary>
	private int[] ComputeLocalSegment(int fx, int fy, int tx, int ty)
	{
		if (tx < 0 || ty < 0 || tx >= _mapWidth || ty >= _mapHeight)
			return null;
		if (IsCellBlocked(tx, ty))
			return null;

		int rx0 = fx >> RegionShift, ry0 = fy >> RegionShift;
		int rx1 = tx >> RegionShift, ry1 = ty >> RegionShift;

		int minRX = Math.Max(0, Math.Min(rx0, rx1) - 1);
		int maxRX = Math.Min(_regionDimX - 1, Math.Max(rx0, rx1) + 1);
		int minRY = Math.Max(0, Math.Min(ry0, ry1) - 1);
		int maxRY = Math.Min(_regionDimY - 1, Math.Max(ry0, ry1) + 1);

		int minX = minRX << RegionShift;
		int minY = minRY << RegionShift;
		int maxX = Math.Min(((maxRX + 1) << RegionShift) - 1, _mapWidth - 1);
		int maxY = Math.Min(((maxRY + 1) << RegionShift) - 1, _mapHeight - 1);

		int ww = maxX - minX + 1;
		int hh = maxY - minY + 1;
		int cap = ww * hh;
		// §118 фаза 5: раньше здесь стояло cap > LocalCapacity → null, и сегмент
		// «якорь→якорь» (якоря лежат на границе двух регионов → окно 4x4 региона)
		// НИКОГДА не вычислялся: вода/стены/горы делали путь невосстановимым.
		if (cap > LocalCapacityMax)
		{
			Interlocked.Increment(ref _failNoSegment);
			return null;
		}

		var b = _buffers.Value;
		b.EnsureLocalCapacity(cap);
		int[] g = b.G;
		int[] parent = b.Parent;
		int[] open = b.Open;
		bool[] closed = b.Closed;

		Array.Fill(g, int.MaxValue, 0, cap);
		Array.Fill(parent, -1, 0, cap);
		Array.Fill(closed, false, 0, cap);

		int sidx = (fy - minY) * ww + (fx - minX);
		int tidx = (ty - minY) * ww + (tx - minX);
		if (sidx < 0 || tidx < 0 || sidx >= cap || tidx >= cap)
			return null;

		g[sidx] = 0;
		int openCount = 0;
		LocalPush(open, ref openCount, sidx, OctileDist(fx - tx, fy - ty));

		const int MaxExpand = 4096;
		int expanded = 0;

		while (openCount > 0 && expanded < MaxExpand)
		{
			int u = LocalPop(open, ref openCount);
			if (closed[u])
				continue;
			closed[u] = true;
			expanded++;

			if (u == tidx)
				break;

			int ux = u % ww + minX;
			int uy = u / ww + minY;
			int uCost = g[u];

			for (int dy = -1; dy <= 1; dy++)
			{
				int ny = uy + dy;
				if (ny < minY || ny > maxY)
					continue;
				int nyw = ny * _mapWidth;
				for (int dx = -1; dx <= 1; dx++)
				{
					if (dx == 0 && dy == 0)
						continue;
					int nx = ux + dx;
					if (nx < minX || nx > maxX)
						continue;

					int cellCost = _cost[nyw + nx];
					if (cellCost == CostBlocked)
						continue;

					int step = (dx != 0 && dy != 0) ? 14 : 10;
					if (cellCost == CostWater)
						step *= 3;

					int v = (ny - minY) * ww + (nx - minX);
					if (closed[v])
						continue;

					int tentative = uCost + step;
					if (tentative < g[v])
					{
						g[v] = tentative;
						parent[v] = u;
						int f = tentative + OctileDist(nx - tx, ny - ty);
						LocalPush(open, ref openCount, v, f);
					}
				}
			}
		}

		if (g[tidx] == int.MaxValue)
			return null;

		// Восстановление пути (включая старт и цель) в RevScratch без List.
		// Длина ограничена cap окна (≤ LocalCapacity); результат — sized-копия
		// для shared кэша сегментов.
		int n = 0;
		int cur = tidx;
		while (cur != -1 && n < b.RevScratch.Length)
		{
			b.RevScratch[n++] = cur;
			if (cur == sidx)
				break;
			cur = parent[cur];
		}
		if (n == 0 || (n >= b.RevScratch.Length && cur != -1 && cur != sidx))
			return null;
		var result = new int[n];
		for (int i = 0; i < n; i++)
		{
			int loc = b.RevScratch[n - 1 - i];
			int lx = loc % ww + minX;
			int ly = loc / ww + minY;
			result[i] = ly * _mapWidth + lx;
		}
		return result;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int OctileDist(int dx, int dy)
	{
		dx = Math.Abs(dx);
		dy = Math.Abs(dy);
		return 10 * Math.Max(dx, dy) - 4 * Math.Min(dx, dy);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool IsCellBlocked(int tx, int ty)
	{
		if ((uint)tx >= (uint)_mapWidth || (uint)ty >= (uint)_mapHeight)
			return true;
		return _cost[ty * _mapWidth + tx] == CostBlocked;
	}

	// ------------------------------------------------------------------
	// Прямая видимость и сглаживание
	// ------------------------------------------------------------------

	/// <summary>
	/// Клеточная прямая видимость по Брезенхэму: сегмент свободен, если ни
	/// одна клетка по пути не блокирована стеной (вода допустима — движение
	/// напрямую сквозь воду разрешено, лишь медленнее).
	/// </summary>
	/// <summary>
	/// Целочисленный Брезенхем: ~5ns на клетку вместо ~30 (float-деление +
	/// Math.Round ~15ns на шаг). SmoothPath делает LOS на каждый узел пути —
	/// при 200 узлах × 30 шагов это был главный жор сглаживания.
	/// </summary>
	private bool HasLineOfSight(int x0, int y0, int x1, int y1)
	{
		int dx = Math.Abs(x1 - x0);
		int dy = Math.Abs(y1 - y0);
		int sx = x0 < x1 ? 1 : -1;
		int sy = y0 < y1 ? 1 : -1;
		int err = dx - dy;
		while (true)
		{
			if (IsCellBlocked(x0, y0))
				return false;
			if (x0 == x1 && y0 == y1)
				return true;
			int e2 = err << 1;
			if (e2 > -dy) { err -= dy; x0 += sx; }
			if (e2 < dx) { err += dx; y0 += sy; }
		}
	}

	/// <summary>
	/// Длина водного отрезка вдоль LOS (тайлы воды по Брезенхему).
	/// Стены здесь не проверяем — их уже отсеял HasLineOfSight.
	/// #14: оставлен для совместимости; горячий путь TryFindPath использует
	/// LineOfSightWithWater (один проход вместо двух).
	/// </summary>
	private int WaterCrossingLength(int x0, int y0, int x1, int y1)
	{
		int dx = Math.Abs(x1 - x0);
		int dy = Math.Abs(y1 - y0);
		int sx = x0 < x1 ? 1 : -1;
		int sy = y0 < y1 ? 1 : -1;
		int err = dx - dy;
		int water = 0;
		while (true)
		{
			if ((uint)x0 < (uint)_mapWidth && (uint)y0 < (uint)_mapHeight
				&& _cost[y0 * _mapWidth + x0] == CostWater)
				water++;
			if (x0 == x1 && y0 == y1)
				return water;
			int e2 = err << 1;
			if (e2 > -dy) { err -= dy; x0 += sx; }
			if (e2 < dx) { err += dx; y0 += sy; }
		}
	}

	/// <summary>
	/// #14: совмещённый LOS + подсчёт воды за ОДИН проход Брезенхема.
	/// Возвращает false при первой же стене; иначе true + число водных
	/// тайлов отрезка в <paramref name="waterLength"/>. Экономит ~50% работы
	/// прямого коридора против пары HasLineOfSight + WaterCrossingLength.
	/// </summary>
	private bool LineOfSightWithWater(int x0, int y0, int x1, int y1, out int waterLength)
	{
		waterLength = 0;
		int dx = Math.Abs(x1 - x0);
		int dy = Math.Abs(y1 - y0);
		int sx = x0 < x1 ? 1 : -1;
		int sy = y0 < y1 ? 1 : -1;
		int err = dx - dy;
		while (true)
		{
			if (IsCellBlocked(x0, y0))
				return false;
			if ((uint)x0 < (uint)_mapWidth && (uint)y0 < (uint)_mapHeight
				&& _cost[y0 * _mapWidth + x0] == CostWater)
				waterLength++;
			if (x0 == x1 && y0 == y1)
				return true;
			int e2 = err << 1;
			if (e2 > -dy) { err -= dy; x0 += sx; }
			if (e2 < dx) { err += dx; y0 += sy; }
		}
	}

	/// <summary>
	/// Прореживает детальный путь: оставляет только точки поворота, где
	/// прямая видимость между последовательными опорными точками сохраняется.
	/// PERF F4: zero-alloc — пишет в <paramref name="destination"/> (thread-local
	/// scratch вызывателя), возвращает число точек. Семантика 1-в-1 со старым
	/// List-вариантом (включая финал и fallback на последнюю точку).
	/// </summary>
	// §118.5: максимальный шаг сглаживания. Ограничивает работу линейной величиной.
	private const int MaxSmoothSpan = 48;

	private int SmoothPath(int sx, int sy, int tx, int ty, Span<int> path, Span<int> destination)
	{
		// §118.5: РАНЬШЕ здесь на КАЖДУЮ клетку пути выполнялся Брезенхем от текущей
		// опоры (O(N²)): обход реки длиной 3000 клеток = ~4,5 млн шагов Брезенхема,
		// ~5 мс на ОДИН путь. Теперь окно просмотра ограничено MaxSmoothSpan, работа
		// линейная, а траектория та же — опорные точки просто плотнее (все они лежат
		// на одной прямой, агент идёт по той же линии).
		int outLen = 0;
		int curX = sx;
		int curY = sy;
		int idx = 0;

		while (idx < path.Length)
		{
			int best = -1;
			int bestX = curX;
			int bestY = curY;

			for (int j = idx; j < path.Length; j++)
			{
				int packed = path[j];
				int px = packed % _mapWidth;
				int py = packed / _mapWidth;
				int dx = px > curX ? px - curX : curX - px;
				int dy = py > curY ? py - curY : curY - py;
				if ((dx > dy ? dx : dy) > MaxSmoothSpan)
					break; // дальше окна не смотрим — фиксируем достигнутое
				if (!HasLineOfSight(curX, curY, px, py))
					break;
				best = j;
				bestX = px;
				bestY = py;
			}

			if (best < 0)
			{
				// Из опоры не видно ни одной следующей клетки (излом коридора) —
				// берём соседнюю как есть: путь собран по проходимым клеткам.
				int packed = path[idx];
				bestX = packed % _mapWidth;
				bestY = packed / _mapWidth;
				best = idx;
			}

			if ((uint)outLen >= (uint)destination.Length)
				return 0; // scratch переполнен — вызывающй retry'ит
			destination[outLen++] = bestY * _mapWidth + bestX;
			curX = bestX;
			curY = bestY;
			idx = best + 1;
		}

		if (!(curX == tx && curY == ty))
		{
			if ((uint)outLen >= (uint)destination.Length)
				return 0;
			// Цель видна — идём прямо к ней; иначе доводим до последней опоры.
			if (HasLineOfSight(curX, curY, tx, ty))
				destination[outLen++] = ty * _mapWidth + tx;
			else if (path.Length > 0)
				destination[outLen++] = path[path.Length - 1];
		}

		return outLen;
	}
}
