using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.UI;
using Godot;

namespace Game.UI.Streaming;

/// <summary>
/// Главнопоточный Node2D-вид бесконечного мира. Использует полноразмерные четкие
/// игровые тайлы с предварительно скомпонованным атласом земли и декора (уменьшает вызовы TileMapLayer).
/// Дополнительно ведёт персистентные клеточные метки-«блоки» нескольких видов (стены, здания,
/// добытое, вырубленное, склады): изменения кодируются через WorldChunkDeltaCodec и сохраняются
/// при выгрузке чанка в WorldChunkStore, при повторном входе восстанавливаются из snapshot'а.
/// Никакого обращения к legacy MapGenerator/MapData/MapRenderer.
/// Реализует IWallWorld (контракт для StreamingWorldManager) и IBlockWorld
/// (мульти-kind доступ для менеджеров главной игры).
/// </summary>
public partial class StreamingWorldView : Node2D, IWallWorld, IBlockWorld, IWorldCellSource
{
	/// <summary>Kind записей delta для пользовательских постоянных меток (стен).</summary>
	public const byte WallKind = WorldDeltaKind.Wall;

	/// <summary>Пикселей текстуры на одну игровую клетку (сейчас — тайл 64×64 на клетку).</summary>
	// Рендер — настоящими тайлами игры: 1 клетка = один тайл 64×64 (TilePx).
	private const int TilePx = 64;

	[Export] public ulong WorldSeed = 0x0123456789ABCDEFUL;
	[Export] public uint GeneratorVersion = 3;
	[Export] public int MaxConcurrency = 4;
	// Hard per-frame budgets prevent a large batch of completed jobs / camera teleports
	// from turning into a single long main-thread frame.
	// §117/B3: 8→4 — применение готового чанка (декод snapshot + словари) вдвое
	// дешевле на кадр; дренирование теперь ТОЛЬКО из _Process (см. Update).
	[Export(PropertyHint.Range, "1,32,1")] public int MaxReadyChunksPerFrame = 4;
	[Export(PropertyHint.Range, "1,16,1")] public int MaxUnloadSchedulesPerFrame = 2;
	[Export] public string SaveDirectory = ""; // "" => user://infinite_world_lab_cache
	[Export] public bool WipeStoreOnStart = false; // true — стенд стартует с чистого хранилища
	// Окно стриминга подбирается под видимый прямоугольник (в чанках): меньше окно —
	// меньше resident-клеток, памяти и работы по тайлам.
	[Export] public int MinWindowChunks = 3;
	[Export] public int MaxWindowChunks = 16;
	// Each operation is a bounded slice, not a whole 64×64 chunk.
	[Export(PropertyHint.Range, "1,16,1")] public int MaxChunkOpsPerFrame = 4;
	[Export(PropertyHint.Range, "128,4096,128")] public int MaxCellsPerPaintSlice = 4096;
	[Export(PropertyHint.Range, "128,4096,128")] public int MaxCellsErasedPerFrame = 2048;
	public Func<ChunkKey, bool> IsChunkPinned { get; set; }

	private ChunkWindowPlanner _planner;
	private WorldChunkStore _store;
	private WorldChunkStreamer _streamer;
	private TileSet _tileSet;
	private TileMapLayer _groundLayer;
	private TileMapLayer _decorLayer;
	private TileMapLayer _markLayer;

	/// <summary>Слой чертежей (§29): план рисуется полупрозрачно, готовое — обычными слоями.</summary>
	private TileMapLayer _planLayer;
	// Горы, стены и грядки — свои слои с TileSet из сцен (terrain-автотайлинг, как на острове).
	private TileMapLayer _mountainLayer;
	private TileMapLayer _wallLayer;
	private TileMapLayer _wallPlanLayer;
	private TileMapLayer _farmLayer;
	private TileMapLayer _farmPlanLayer;
	private TerrainTileLayer _mountainTerrain;
	private TerrainTileLayer _wallTerrain;
	private TerrainTileLayer _wallPlanTerrain;
	private TerrainTileLayer _farmTerrain;
	private TerrainTileLayer _farmPlanTerrain;
	private ShaderMaterial _groundMaterial;
	private float _waterAccumulatedTime;
	private readonly Dictionary<ChunkKey, WorldChunk> _chunkData = new();

	/// <summary>Загруженные персистентные метки: чанк → kind → множество локальных индексов.</summary>
	private readonly Dictionary<ChunkKey, Dictionary<byte, HashSet<ushort>>> _blocks = new();
	private readonly HashSet<ChunkKey> _textureDirty = new();
	// Reused instead of allocating a new array each time a batch of chunks arrives.
	private readonly List<ChunkKey> _dirtyBuffer = new();
	// A just-loaded chunk has no overlay tiles to clear; skip redundant EraseCell calls.
	private readonly HashSet<ChunkKey> _freshPaint = new();
	// §117/B3: чанки, которые когда-либо клали тайлы в наши слои (чанковая нарезка или
	// одиночная перекраска). Erase-очередь на выгрузке гоняется ТОЛЬКО для них: свежие
	// и околониевые (IsOccludedByColony) чанки тайлов не клали, а полный проход
	// 4096 EraseCell по каждому сжигал MaxCellsErasedPerFrame вхолостую.
	private readonly HashSet<ChunkKey> _paintedChunks = new();
	// §117/F4: кэш сетки плодородия 5×5 на чанк (шаг 16, как в GenerateChunk/
	// GenerateColonyWindow). GeneratedCell плодородия не хранит, а семплы
	// SampleBlendedCell нужны только 25 на чанк — строится лениво, при первом декоре.
	private readonly ushort[] _fertilityGrid = new ushort[FertilityGridSide * FertilityGridSide];
	private ChunkKey _fertilityChunk;
	private bool _fertilityValid;
	private readonly Queue<ChunkKey> _eraseQueue = new();
	private readonly HashSet<ChunkKey> _eraseScheduled = new();
	private readonly Dictionary<ChunkKey, ChunkEraseJob> _eraseJobs = new();
	private readonly Queue<ChunkKey> _paintQueue = new();
	private readonly HashSet<ChunkKey> _paintQueued = new();
	private readonly Dictionary<ChunkKey, ChunkPaintJob> _paintJobs = new();
	private long _focusCellX;
	private long _focusCellY;
	private readonly Queue<ChunkKey> _exitQueue = new();
	private readonly HashSet<ChunkKey> _exitScheduled = new();
	private readonly List<(ChunkKey Key, Task Task)> _unloadTasks = new();
	private readonly ChunkStreamResult[] _readyBuffer = new ChunkStreamResult[64];
	private readonly ChunkStreamFailure[] _failureBuffer = new ChunkStreamFailure[8];
	private ChunkWindowPlan _lastPlan;
	private bool _initialized;

	public ulong ActiveSeed => WorldSeed;
	public uint ActiveGeneratorVersion => GeneratorVersion;
	public int LoadedChunks => _chunkData.Count;
	public int WallCount { get; private set; }

	public override void _Ready()
	{
		string dir = SaveDirectory;
		if (string.IsNullOrWhiteSpace(dir))
		{
			string userDir = ProjectSettings.GlobalizePath("user://infinite_world_lab_cache");
			dir = System.IO.Path.Combine(userDir, $"world_{WorldSeed:x16}");
		}
		if (WipeStoreOnStart && System.IO.Directory.Exists(dir))
		{
			try
			{
				System.IO.Directory.Delete(dir, recursive: true);
				GD.Print($"[World] wiped store: {dir}");
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[World] store wipe failed: {ex.Message}");
			}
		}
		_planner = new ChunkWindowPlanner();
		_store = new WorldChunkStore(dir);
		// Выделяем до (ядер - 2) воркеров (от 2 до 8) для высокой пропускной способности без конкуренции за CPU
		int workerLimit = Math.Clamp(System.Environment.ProcessorCount - 2, 2, 8);
		int workerCount = Math.Clamp(MaxConcurrency, 1, workerLimit);
		_streamer = new WorldChunkStreamer(_planner, WorldSeed, GeneratorVersion, _store, workerCount);
		BuildTileLayers();
		_initialized = true;
	}

	/// <summary>
	/// Сбросить кэш чанков, очереди и слои при смене сида мира или выборе нового региона.
	/// Гарантирует немедленную очистку устаревших чанков и бесшовный стриминг новой зоны.
	/// </summary>
	public void ResetWorld(ulong newSeed = 0)
	{
		if (newSeed != 0)
			WorldSeed = newSeed;

		_eraseQueue.Clear();
		_eraseScheduled.Clear();
		_eraseJobs.Clear();
		_paintQueue.Clear();
		_paintQueued.Clear();
		_paintJobs.Clear();
		_freshPaint.Clear();
		_textureDirty.Clear();
		_exitQueue.Clear();
		_exitScheduled.Clear();
		_dirtyBuffer.Clear();
		_paintedChunks.Clear();
		_fertilityValid = false;

		_groundLayer?.Clear();
		_markLayer?.Clear();
		_planLayer?.Clear();
		_mountainLayer?.Clear();
		_wallLayer?.Clear();
		_wallPlanLayer?.Clear();
		_farmLayer?.Clear();
		_farmPlanLayer?.Clear();
		_mountainTerrain?.Clear();
		_wallTerrain?.Clear();
		_wallPlanTerrain?.Clear();
		_farmTerrain?.Clear();
		_farmPlanTerrain?.Clear();

		_chunkData.Clear();
		_blocks.Clear();
		WallCount = 0;
		_lastPlan = default;

		if (_streamer != null)
		{
			_streamer.CancelPending();
			try
			{
				_streamer.DisposeAsync().AsTask().Wait(2000);
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[StreamingWorldView] Ошибка ожидания закрытия streamer: {ex.Message}");
			}
			_streamer = null;
		}

		string dir = SaveDirectory;
		if (string.IsNullOrWhiteSpace(dir))
		{
			string userDir = ProjectSettings.GlobalizePath("user://infinite_world_lab_cache");
			dir = System.IO.Path.Combine(userDir, $"world_{WorldSeed:x16}");
		}
		_planner = new ChunkWindowPlanner();
		_store = new WorldChunkStore(dir);
		int workerLimit = Math.Clamp(System.Environment.ProcessorCount - 2, 2, 8);
		int workerCount = Math.Clamp(MaxConcurrency, 1, workerLimit);
		_streamer = new WorldChunkStreamer(_planner, WorldSeed, GeneratorVersion, _store, workerCount);
	}

	public override void _Process(double delta)
	{
		if (!_initialized)
			return;

		// Непрерывно каждый кадр: дренируем завершившиеся результаты из воркеров
		int readyBudget = Math.Clamp(MaxReadyChunksPerFrame, 1, _readyBuffer.Length);
		int readyCount = _streamer.DrainReady(_readyBuffer.AsSpan(0, readyBudget), readyBudget);
		for (int i = 0; i < readyCount; i++)
			ApplyReady(_readyBuffer[i]);

		int failureCount = _streamer.DrainFailures(_failureBuffer.AsSpan(0, _failureBuffer.Length), _failureBuffer.Length);
		for (int i = 0; i < failureCount; i++)
			GD.PrintErr($"[World] chunk failure {_failureBuffer[i].Key}: {_failureBuffer[i].Message}");

		ProcessExits();
		RepaintDirty();

		// §117 (синхронизация воды): water_time — копия расчёта MapRenderer._waterAccumulatedTime
		// (rate = speed<=0 ? 0 : pow(speed,0.45), тот же TimeManager), чтобы вода
		// streaming_ground.gdshader и terrain_blend.gdshader анимировалась одинаково —
		// иначе на стыке слоёв виден шов фазы (волна глубины/блики в противофазе).
		if (_groundMaterial != null)
		{
			float speed = Game.Simulation.TimeManager.Instance != null ? Game.Simulation.TimeManager.Instance.SpeedMultiplier : 1f;
			float rate = speed <= 0f ? 0f : Mathf.Pow(speed, 0.45f);
			_waterAccumulatedTime += (float)delta * rate;
			_groundMaterial.SetShaderParameter("water_time", _waterAccumulatedTime);
		}
	}

	/// <summary>Применить новое видимое окно, обновить план окна.</summary>
	public void Update(WorldRect visibleTileBounds)
	{
		if (!_initialized)
			return;

		long currentFocusX = (visibleTileBounds.MinX + visibleTileBounds.MaxX) / 2;
		long currentFocusY = (visibleTileBounds.MinY + visibleTileBounds.MaxY) / 2;

		_focusCellX = currentFocusX;
		_focusCellY = currentFocusY;
		ApplyAdaptiveWindow(visibleTileBounds);
		_lastPlan = _streamer.Update(ClampVisibleToWindow(visibleTileBounds));

		foreach (ChunkKey key in _lastPlan.Exited)
		{
			if (_streamer.IsResident(key) && !_streamer.IsDesired(key) && !_exitScheduled.Contains(key))
			{
				_exitQueue.Enqueue(key);
				_exitScheduled.Add(key);
			}
		}

		// §117/B3: дренирование готовых чанков, выгрузки и покраска — ТОЛЬКО в _Process.
		// Main (родитель) вызывает Update раньше нашего _Process в том же кадре, поэтому
		// отложенная обработка попадает на экран в том же кадре, зато все кадровые бюджеты
		// (MaxReadyChunksPerFrame, MaxCellsErasedPerFrame, 4 мс нарезки) стали действительно
		// ПОКАДРОВЫМИ: раньше в кадрах с Update порции дренировались и RepaintDirty
		// запускался дважды (Update + _Process), удваивая работу главного потока.
	}

	/// <summary>Добавить персистентную метку-блок в клетку; чанк должен быть загружен.</summary>
	public bool AddBlock(byte kind, long cellX, long cellY)
	{
		if (!_initialized)
			return false;
		ChunkKey key = WorldCoordinates.ChunkForTile(cellX, cellY);
		if (!_chunkData.ContainsKey(key))
			return false;
		HashSet<ushort> set = GetOrCreateSet(key, kind);
		LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
		if (!set.Add((ushort)(local.Y * WorldCoordinates.ChunkSize + local.X)))
			return false;
		if (kind == WallKind)
			WallCount++;
		RepaintCell(cellX, cellY);
		OnBlockAdded?.Invoke(kind, cellX, cellY);
		if (kind == WallKind)
			OnWallAdded?.Invoke(cellX, cellY);
		return true;
	}

	/// <summary>Добавить постоянную стену (удобная обёртка над AddBlock).</summary>
	public bool AddWall(long cellX, long cellY) => AddBlock(WallKind, cellX, cellY);

	public bool HasBlock(byte kind, long cellX, long cellY)
	{
		ChunkKey key = WorldCoordinates.ChunkForTile(cellX, cellY);
		HashSet<ushort> set = KindSet(key, kind);
		if (set == null)
			return false;
		LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
		return set.Contains((ushort)(local.Y * WorldCoordinates.ChunkSize + local.X));
	}

	public bool HasWall(long cellX, long cellY) => HasBlock(WallKind, cellX, cellY);

	/// <summary>Снять персистентную метку-блок; false, если её нет или чанк не загружен.</summary>
	public bool RemoveBlock(byte kind, long cellX, long cellY)
	{
		if (!_initialized)
			return false;
		ChunkKey key = WorldCoordinates.ChunkForTile(cellX, cellY);
		HashSet<ushort> set = KindSet(key, kind);
		if (set == null)
			return false;
		LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
		ushort index = (ushort)(local.Y * WorldCoordinates.ChunkSize + local.X);
		if (set.Remove(index))
		{
			if (kind == WallKind)
			{
				WallCount--;
				if (WallCount < 0)
					WallCount = 0;
			}
			RepaintCell(cellX, cellY);
			OnBlockRemoved?.Invoke(kind, cellX, cellY);
			if (kind == WallKind)
				OnWallRemoved?.Invoke(cellX, cellY);
			return true;
		}
		return false;
	}

	/// <summary>Снять постоянную стену (удобная обёртка над RemoveBlock).</summary>
	public bool RemoveWall(long cellX, long cellY) => RemoveBlock(WallKind, cellX, cellY);

	/// <summary>
	/// Собрать чертежи, по которым ещё нет готового (§29): очередь задач для людей мира.
	/// Только загруженные чанки; ближайшие к фокусу — первыми; не больше <paramref name="max"/> клеток.
	/// </summary>
	public int CollectPendingPlans(List<WorldPlan> into, long focusX, long focusY, int max)
	{
		into.Clear();
		if (max <= 0)
			return 0;
		_planFocusX = focusX;
		_planFocusY = focusY;
		_planComparer ??= new PlanFocusComparer(this);

		// Keep only the closest `max` candidates while scanning. The previous implementation
		// materialized and sorted every pending plan (O(N log N) time and O(N) memory), even
		// when callers needed only a handful. The max-heap costs O(N log max) and O(max) memory.
		foreach (KeyValuePair<ChunkKey, Dictionary<byte, HashSet<ushort>>> pair in _blocks)
		{
			if (!_chunkData.ContainsKey(pair.Key))
				continue;
			Dictionary<byte, HashSet<ushort>> map = pair.Value;
			foreach (byte planKind in PlanKinds)
			{
				if (!map.TryGetValue(planKind, out HashSet<ushort> set) || set.Count == 0)
					continue;
				map.TryGetValue(WorldDeltaKind.DoneFor(planKind), out HashSet<ushort> done);
				foreach (ushort index in set)
				{
					if (done != null && done.Contains(index))
						continue;
					int localX = index % WorldCoordinates.ChunkSize;
					int localY = index / WorldCoordinates.ChunkSize;
					AddPlanCandidate(into, new WorldPlan(planKind,
						pair.Key.X * (long)WorldCoordinates.ChunkSize + localX,
						pair.Key.Y * (long)WorldCoordinates.ChunkSize + localY), max);
				}
			}
		}
		if (into.Count > 1)
			into.Sort(_planComparer);
		return into.Count;
	}

	private static readonly byte[] PlanKinds =
	{
		WorldDeltaKind.WallPlan,
		WorldDeltaKind.BuildingPlan,
		WorldDeltaKind.CropPlan,
		WorldDeltaKind.StockpilePlan,
		WorldDeltaKind.CutTreePlan,
		WorldDeltaKind.MinedStonePlan
	};

	private long _planFocusX;
	private long _planFocusY;
	private PlanFocusComparer _planComparer;

	/// <summary>Сортировка чертежей по близости к фокусу (камера/игрок).</summary>
	private sealed class PlanFocusComparer : IComparer<WorldPlan>
	{
		private readonly StreamingWorldView _view;

		public PlanFocusComparer(StreamingWorldView view) => _view = view;

		public int Compare(WorldPlan a, WorldPlan b)
			=> Distance(a, _view._planFocusX, _view._planFocusY)
				.CompareTo(Distance(b, _view._planFocusX, _view._planFocusY));

		private static double Distance(WorldPlan plan, long focusX, long focusY)
		{
			// Convert before subtracting: signed long subtraction/squaring can overflow
			// for valid world coordinates far from the current focus.
			double dx = (double)plan.CellX - focusX;
			double dy = (double)plan.CellY - focusY;
			return dx * dx + dy * dy;
		}
	}

	private void AddPlanCandidate(List<WorldPlan> heap, WorldPlan candidate, int max)
	{
		if (heap.Count < max)
		{
			heap.Add(candidate);
			int i = heap.Count - 1;
			while (i > 0)
			{
				int parent = (i - 1) / 2;
				if (_planComparer.Compare(heap[i], heap[parent]) <= 0)
					break;
				(heap[i], heap[parent]) = (heap[parent], heap[i]);
				i = parent;
			}
			return;
		}

		// The root is the farthest retained candidate.
		if (_planComparer.Compare(candidate, heap[0]) >= 0)
			return;
		heap[0] = candidate;
		int root = 0;
		while (true)
		{
			int left = root * 2 + 1;
			if (left >= heap.Count)
				break;
			int right = left + 1;
			int farther = right < heap.Count
				&& _planComparer.Compare(heap[right], heap[left]) > 0 ? right : left;
			if (_planComparer.Compare(heap[farther], heap[root]) <= 0)
				break;
			(heap[root], heap[farther]) = (heap[farther], heap[root]);
			root = farther;
		}
	}

	/// <summary>Число загруженных в память меток данного вида.</summary>
	public int BlockCount(byte kind)
	{
		int count = 0;
		foreach (KeyValuePair<ChunkKey, Dictionary<byte, HashSet<ushort>>> pair in _blocks)
		{
			if (pair.Value.TryGetValue(kind, out HashSet<ushort> set))
				count += set.Count;
		}
		return count;
	}

	/// <summary>Есть ли данные чанка в памяти; его покраска может продолжаться по кадрам.</summary>
	public bool IsLoaded(ChunkKey key) => _chunkData.ContainsKey(key);

	/// <summary>Чанк клетки загружен (контракт единого доступа к миру, §26.1).</summary>
	public bool CoversChunk(long cellX, long cellY)
		=> _chunkData.ContainsKey(WorldCoordinates.ChunkForTile(cellX, cellY));

	/// <summary>Исходные поля клетки из resident-чанка; false — чанк не загружен.</summary>
	public bool TryCellInfo(long cellX, long cellY, out GeneratedCell cell)
	{
		ChunkKey key = WorldCoordinates.ChunkForTile(cellX, cellY);
		if (!_chunkData.TryGetValue(key, out WorldChunk chunk))
		{
			cell = default;
			return false;
		}
		LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
		cell = chunk.Cells.Span[local.Y * WorldCoordinates.ChunkSize + local.X];
		return true;
	}

	/// <summary>Существует ли сохранённый delta-файл чанка в хранилище.</summary>
	public bool IsChunkStored(ChunkKey key) => _store.ContainsChunk(key.X, key.Y);

	/// <summary>FNV-1a 64 по всем детерминированным полям клеток загруженного чанка (0, если не загружен).</summary>
	public ulong ChunkHash(ChunkKey key)
	{
		if (!_chunkData.TryGetValue(key, out WorldChunk chunk))
			return 0;
		ulong hash = 0xcbf29ce484222325ul;
		ReadOnlySpan<GeneratedCell> cells = chunk.Cells.Span;
		foreach (ref readonly GeneratedCell cell in cells)
		{
			hash = FoldByte(hash, (byte)cell.Terrain);
			hash = FoldByte(hash, (byte)(cell.ElevationQ16 & 0xFF));
			hash = FoldByte(hash, (byte)(cell.ElevationQ16 >> 8));
			hash = FoldByte(hash, (byte)(cell.MoistureQ16 & 0xFF));
			hash = FoldByte(hash, (byte)(cell.MoistureQ16 >> 8));
			hash = FoldByte(hash, (byte)(cell.ForestQ16 & 0xFF));
			hash = FoldByte(hash, (byte)(cell.ForestQ16 >> 8));
			hash = FoldByte(hash, (byte)(cell.StoneQ16 & 0xFF));
			hash = FoldByte(hash, (byte)(cell.StoneQ16 >> 8));
		}
		return hash;
	}

	private static ulong FoldByte(ulong hash, byte value)
	{
		hash ^= value;
		return hash * 0x100000001b3ul;
	}

	/// <summary>Сводка по типам поверхности всех загруженных чанков (проверка воды/гор/травы).</summary>
	public string TerrainSummary()
	{
		long water = 0, grass = 0, mountain = 0;
		foreach (KeyValuePair<ChunkKey, WorldChunk> pair in _chunkData)
		{
			ReadOnlySpan<GeneratedCell> cells = pair.Value.Cells.Span;
			foreach (ref readonly GeneratedCell cell in cells)
			{
				switch (cell.Terrain)
				{
					case BaseTerrainKind.Water: water++; break;
					case BaseTerrainKind.Mountain: mountain++; break;
					default: grass++; break;
				}
			}
		}
		long total = water + grass + mountain;
		if (total == 0)
			return "water=0 grass=0 mountain=0";
		return $"water={100.0 * water / total:F1}% grass={100.0 * grass / total:F1}% mountain={100.0 * mountain / total:F1}%";
	}

	public override string ToString() => $"seed={WorldSeed:X16} ver={GeneratorVersion} loaded={LoadedChunks} walls={WallCount}";

	/// <summary>Блокирующий останов стримера (workers не трогают Godot API).</summary>
	public void Shutdown()
	{
		if (!_initialized)
			return;
		_streamer.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_initialized = false;
	}

	// --- §118 фаза 2: события изменений (аудит WorldDeltaApi §6.7 — в мировом слое их не было) ---

	/// <summary>Метка добавлена в загруженную клетку: (kind, cellX, cellY).</summary>
	public event Action<byte, long, long> OnBlockAdded;

	/// <summary>Метка снята: (kind, cellX, cellY).</summary>
	public event Action<byte, long, long> OnBlockRemoved;

	/// <summary>Стена поставлена: (cellX, cellY) — частный случай OnBlockAdded(Wall).</summary>
	public event Action<long, long> OnWallAdded;

	/// <summary>Стена снята: (cellX, cellY) — частный случай OnBlockRemoved(Wall).</summary>
	public event Action<long, long> OnWallRemoved;

	/// <summary>
	/// §118 фаза 2 (аудит §6.4): принудительно сохранить дельты ВСЕХ resident-чанков,
	/// включая desired (UnloadChunkAsync их не принимает — поэтому штатный путь при
	/// выходе терял правки текущего окна). Пишет напрямую в стор вида той же атомарной
	/// заменой, что и штатная выгрузка. Идемпотентен; ошибки пишутся в лог и не
	/// прерывают цепочку выхода. Вызывать ДО Shutdown().
	/// Пустая память + файла на диске нет → диск не трогаем (аудит §6.9: пустой
	/// payload удаляет файл — нельзя стереть чужие валидные дельты).
	/// </summary>
	/// <returns>Число чанков с непустыми записанными дельтами.</returns>
	public int FlushResidentDeltas(double gameTimeCheckpoint = 0.0)
	{
		if (!_initialized || _store == null)
			return 0;
		var metadata = new WorldSaveMetadata(
			WorldChunkCodec.CurrentFormatVersion, GeneratorVersion,
			UnifiedWorldGenerator.FeatureSchemaVersion, WorldSeed,
			GameTimeCheckpoint: gameTimeCheckpoint);
		int saved = 0;
		foreach (KeyValuePair<ChunkKey, WorldChunk> pair in _chunkData)
		{
			try
			{
				byte[] payload = EncodeBlocks(pair.Key);
				if (payload.Length == 0 && !_store.ContainsChunk(pair.Key.X, pair.Key.Y))
					continue;
				if (_store.SaveChunk(metadata, pair.Key.X, pair.Key.Y, payload))
					saved++;
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[World] FlushResidentDeltas {pair.Key}: {ex.Message}");
			}
		}
		if (saved > 0)
			GD.Print($"[World] FlushResidentDeltas: {saved} чанков сохранено (gameTime={gameTimeCheckpoint:0.0}s)");
		return saved;
	}

	/// <summary>
	/// §118 фаза 2 (аудит §6.2): перечислить метки resident-чанков в прямоугольнике
	/// (глобальные координаты). kind == 0 → все виды. Дисковое (нерезидентное)
	/// перечисление появится вместе с материализатором фазы 3.
	/// </summary>
	public int EnumerateDeltas(WorldRect area, byte kind, List<(byte Kind, long CellX, long CellY)> into)
	{
		if (into == null)
			return 0;
		into.Clear();
		if (_blocks.Count == 0)
			return 0;
		ChunkKey min = WorldCoordinates.ChunkForTile(area.MinX, area.MinY);
		ChunkKey max = WorldCoordinates.ChunkForTile(area.MaxX, area.MaxY);
		for (long cy = min.Y; cy <= max.Y; cy++)
		{
			for (long cx = min.X; cx <= max.X; cx++)
			{
				if (!_blocks.TryGetValue(new ChunkKey(cx, cy), out Dictionary<byte, HashSet<ushort>> map))
					continue;
				long originX = cx * (long)WorldCoordinates.ChunkSize;
				long originY = cy * (long)WorldCoordinates.ChunkSize;
				foreach (KeyValuePair<byte, HashSet<ushort>> kindSet in map)
				{
					if (kind != 0 && kindSet.Key != kind)
						continue;
					foreach (ushort index in kindSet.Value)
					{
						long cellX = originX + index % WorldCoordinates.ChunkSize;
						long cellY = originY + index / WorldCoordinates.ChunkSize;
						if (cellX < area.MinX || cellX > area.MaxX || cellY < area.MinY || cellY > area.MaxY)
							continue;
						into.Add((kindSet.Key, cellX, cellY));
					}
				}
			}
		}
		return into.Count;
	}

	private void ApplyReady(ChunkStreamResult result)
	{
		ChunkKey key = result.Key;
		bool wasLoaded = _chunkData.ContainsKey(key);
		// Снимок delta, если чанк сохранялся ранее (постоянные изменения).
		if (result.Snapshot != null)
		{
			if (WorldChunkDeltaCodec.TryDecode(result.Snapshot.Payload, out WorldChunkDelta delta, out string error))
			{
				var map = new Dictionary<byte, HashSet<ushort>>();
				foreach (WorldChunkDeltaRecord record in delta.Records)
				{
					if (!map.TryGetValue(record.Kind, out HashSet<ushort> set))
					{
						set = new HashSet<ushort>();
						map[record.Kind] = set;
					}
					set.Add(record.LocalIndex);
				}

				// A re-delivered snapshot replaces, rather than double-counts, its old walls.
				if (_blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> previous)
					&& previous.TryGetValue(WallKind, out HashSet<ushort> oldWalls))
					WallCount = Math.Max(0, WallCount - oldWalls.Count);
				_blocks.Remove(key);
				if (map.Count > 0)
				{
					_blocks[key] = map;
					if (map.TryGetValue(WallKind, out HashSet<ushort> walls))
						WallCount += walls.Count;
				}
			}
			else
			{
				GD.PrintErr($"[World] delta decode failed for {key}: {error}");
			}
		}

		_paintJobs.Remove(key); // any queued token will pick up the replacement job
		_chunkData[key] = result.Chunk;
		if (wasLoaded)
			_freshPaint.Remove(key);
		else
			_freshPaint.Add(key);

		// Тайлы ставит RepaintDirty (главный поток) — здесь только помечаем чанк.
		_textureDirty.Add(key);
	}

	private void ProcessExits()
	{
		int scheduleBudget = Math.Clamp(MaxUnloadSchedulesPerFrame, 1, 16);
		while (_exitQueue.Count > 0 && scheduleBudget > 0)
		{
			ChunkKey key = _exitQueue.Peek();
			if (!_streamer.IsResident(key) || _streamer.IsDesired(key))
			{
				_exitQueue.Dequeue();
				_exitScheduled.Remove(key);
				continue;
			}

			if (IsChunkPinned != null && IsChunkPinned(key))
			{
				_exitQueue.Dequeue();
				_exitScheduled.Remove(key);
				continue;
			}

			Task task;
			try
			{
				task = _streamer.UnloadChunkAsync(key, EncodeBlocks(key), MakeMetadata());
			}
			catch (InvalidOperationException)
			{
				// Очередь сохранений переполнена — попробуем в следующем кадре.
				break;
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[World] unload schedule failed {key}: {ex.Message}");
				_exitQueue.Dequeue();
				_exitScheduled.Remove(key);
				continue;
			}

			_unloadTasks.Add((key, task));
			_exitQueue.Dequeue();
			_exitScheduled.Remove(key);
			scheduleBudget--;
		}

		for (int i = _unloadTasks.Count - 1; i >= 0; i--)
		{
			var (k, t) = _unloadTasks[i];
			if (!t.IsCompleted)
				continue;
			if (t.IsFaulted)
			{
				GD.PrintErr($"[World] unload task failed for {k}: {t.Exception?.GetBaseException().Message}");
				if (!_exitScheduled.Contains(k))
				{
					_exitQueue.Enqueue(k);
					_exitScheduled.Add(k);
				}
			}
			else
			{
				RemoveChunkTiles(k);
			}
			_unloadTasks.RemoveAt(i);
		}
	}

	private void RemoveChunkTiles(ChunkKey key)
	{
		MarkTerrainForUnload(key);

		long originX = key.X * (long)WorldCoordinates.ChunkSize;
		long originY = key.Y * (long)WorldCoordinates.ChunkSize;
		// §117/B3: erase нужен только чанкам, которые реально клали тайлы (колониальная
		// окклюзия и выгрузка до первой нарезки тайлов не клала). Холостой полный проход
		// 4096 EraseCell на каждый такой чанк выжигал бюджет MaxCellsErasedPerFrame.
		if (FitsTileMap(originX, originY) && _paintedChunks.Contains(key))
		{
			_blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> oldMap);
			ScheduleEraseChunk(key,
				CollectOverlayIndices(oldMap, plans: false),
				CollectOverlayIndices(oldMap, plans: true));
		}

		_paintedChunks.Remove(key);
		_chunkData.Remove(key);
		if (_blocks.Remove(key, out Dictionary<byte, HashSet<ushort>> map)
			&& map.TryGetValue(WallKind, out HashSet<ushort> walls))
			WallCount = Math.Max(0, WallCount - walls.Count);
		_textureDirty.Remove(key);
		_freshPaint.Remove(key);
		_paintJobs.Remove(key);
	}

	/// <summary>
	/// Подобрать сторону окна под видимый прямоугольник с запасом планировщика.
	/// Раньше окно было жёстко 16×16 чанков = 1024×1024 клетки ≈ 1M тайлов даже тогда,
	/// когда видно ~60×34 клетки.
	/// </summary>
	private void ApplyAdaptiveWindow(WorldRect visible)
	{
		long margin = _planner.MarginTiles;
		long needW = ChunkSpan(visible.MinX - margin, visible.MaxX + margin);
		long needH = ChunkSpan(visible.MinY - margin, visible.MaxY + margin);
		long need = Math.Max(needW, needH);

		// §117/B7: zoom-исключение «окно 24 при zoom<0.25» УБРАНО (выбран вариант «проще
		// и надёжнее» вместо гистерезиса 0.20/0.30). Обоснование: окно MaxWindowChunks=16
		// чанков = 1024×1024 клетки покрывает весь экран уже на минимальном зуме 0.12
		// (1920/0.12/64 ≈ 250 видимых тайлов ширины, 4K ≈ 500 — запас ×2), а порог без
		// гистерезиса сам был источником дребезга: у zoom≈0.25 сторона прыгала 16↔24,
		// SetWindowSide при смене инвалидирует якорь плана → полный rebuild окна на каждое
		// переключение. Теперь сторона зависит только от видимого прямоугольника (при зуме
		// меняется редко и монотонно), и из горячего пути Update ушёл лишний поиск камеры.
		int side = (int)Math.Clamp(need, MinWindowChunks, MaxWindowChunks);
		_planner.SetWindowSide(side);
	}

	/// <summary>Сколько чанков покрывает полуинтервал клеток [minCell, maxCellExclusive).</summary>
	private static long ChunkSpan(long minCell, long maxCellExclusive)
	{
		long first = WorldCoordinates.FloorDiv(minCell, WorldCoordinates.ChunkSize);
		long last = WorldCoordinates.FloorDiv(maxCellExclusive - 1, WorldCoordinates.ChunkSize);
		return last - first + 1;
	}

	/// <summary>
	/// Планировщик требует, чтобы видимая область с запасом помещалась в целое число выровненных
	/// чанков. Если она больше максимального окна — сужаем прямоугольник вокруг центра (дальние
	/// края дозаполнятся при движении камеры), а не падаем исключением из планировщика.
	/// </summary>
	private WorldRect ClampVisibleToWindow(WorldRect visible)
	{
		long side = _planner.WindowChunks;
		long available = Math.Max(WorldCoordinates.ChunkSize,
			(side - 1) * WorldCoordinates.ChunkSize - 2 * _planner.MarginTiles);
		long width = visible.MaxX - visible.MinX;
		long height = visible.MaxY - visible.MinY;
		long centerX = (visible.MinX + visible.MaxX) / 2;
		long centerY = (visible.MinY + visible.MaxY) / 2;
		long halfW = Math.Min(width, available) / 2;
		long halfH = Math.Min(height, available) / 2;
		// Страховка от выравнивания: прямоугольник с запасом должен попадать в side чанков.
		while (halfW > 1
			&& ChunkSpan(centerX - halfW - _planner.MarginTiles, centerX + halfW + _planner.MarginTiles) > side)
			halfW -= WorldCoordinates.ChunkSize / 2;
		while (halfH > 1
			&& ChunkSpan(centerY - halfH - _planner.MarginTiles, centerY + halfH + _planner.MarginTiles) > side)
			halfH -= WorldCoordinates.ChunkSize / 2;
		return new WorldRect(centerX - halfW, centerY - halfH, centerX + halfW, centerY + halfH);
	}

	/// <summary>
	/// Закодировать все персистентные метки чанка в delta-полезную нагрузку
	/// (канонический порядок: LocalIndex, затем Kind). Пусто — пустой payload
	/// (стример воспринимает как «без изменений»).
	/// </summary>
	private byte[] EncodeBlocks(ChunkKey key)
	{
		if (!_blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> map) || map.Count == 0)
			return Array.Empty<byte>();

		var records = new List<WorldChunkDeltaRecord>();
		foreach (KeyValuePair<byte, HashSet<ushort>> kindSet in map)
		{
			foreach (ushort index in kindSet.Value)
				records.Add(new WorldChunkDeltaRecord(index, kindSet.Key, Array.Empty<byte>()));
		}
		records.Sort(static (left, right) =>
		{
			int indexOrder = left.LocalIndex.CompareTo(right.LocalIndex);
			return indexOrder != 0 ? indexOrder : left.Kind.CompareTo(right.Kind);
		});
		return WorldChunkDeltaCodec.Encode(new WorldChunkDelta(records));
	}

	private WorldSaveMetadata MakeMetadata()
		=> new(WorldChunkCodec.CurrentFormatVersion, GeneratorVersion,
			UnifiedWorldGenerator.FeatureSchemaVersion, WorldSeed, GameTimeCheckpoint: 0.0);

	/// <summary>
	/// Поставить тайлы изменившимся чанкам и стереть выгруженные — с бюджетом на кадр
	/// (ближние к камере чанки первыми), чтобы телепорт/pan не давал шторм SetCell/EraseCell.
	/// </summary>
	private void RepaintDirty()
	{
		int operationBudget = Mathf.Max(1, MaxChunkOpsPerFrame);

		ProcessEraseWork(Math.Max(1, MaxCellsErasedPerFrame));

		if (_textureDirty.Count > 0)
		{
			SelectNearestDirtyChunks(operationBudget);
			foreach (ChunkKey key in _dirtyBuffer)
			{
				if (!_textureDirty.Contains(key))
					continue;
				if (!_chunkData.ContainsKey(key))
				{
					_textureDirty.Remove(key); // stale dirty entry must not be reconsidered forever
					continue;
				}
				if (_eraseJobs.ContainsKey(key))
					continue; // never let an old erase job erase a newly-painted chunk
				_textureDirty.Remove(key);
				bool clearEmptyLayers = !_freshPaint.Remove(key);
				ScheduleChunkPaint(key, clearEmptyLayers);
			}
		}

		ProcessPaintSlices(operationBudget);
		_mountainTerrain?.Flush(IsMountainCell);
		_wallTerrain?.Flush(IsWallCell);
		_wallPlanTerrain?.Flush(IsWallPlanCell);
		_farmTerrain?.Flush(IsCropCell);
		_farmPlanTerrain?.Flush(IsCropPlanCell);
	}

	private sealed class ChunkPaintJob
	{
		public readonly ChunkKey Key;
		public readonly WorldChunk Chunk;
		public readonly BlockLookup Lookup;
		public readonly long OriginX;
		public readonly long OriginY;
		public readonly bool ClearEmptyLayers;
		public int Cursor;

		public ChunkPaintJob(ChunkKey key, WorldChunk chunk, BlockLookup lookup,
			long originX, long originY, bool clearEmptyLayers)
		{
			Key = key; Chunk = chunk; Lookup = lookup;
			OriginX = originX; OriginY = originY; ClearEmptyLayers = clearEmptyLayers;
		}
	}

	private sealed class ChunkEraseJob
	{
		public readonly ChunkKey Key;
		public readonly ushort[] MarkIndices;
		public readonly ushort[] PlanIndices;
		public int Cursor;
		public int MarkCursor;
		public int PlanCursor;

		public ChunkEraseJob(ChunkKey key, ushort[] markIndices, ushort[] planIndices)
		{
			Key = key;
			MarkIndices = markIndices ?? Array.Empty<ushort>();
			PlanIndices = planIndices ?? Array.Empty<ushort>();
		}
	}

	private void ScheduleChunkPaint(ChunkKey key, bool clearEmptyLayers)
	{
		if (!_chunkData.TryGetValue(key, out WorldChunk chunk))
			return;
		long originX = key.X * (long)WorldCoordinates.ChunkSize;
		long originY = key.Y * (long)WorldCoordinates.ChunkSize;
		if (!FitsTileMap(originX, originY))
			return;
		if (IsOccludedByColony(originX, originY))
			return;
		_blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> map);
		_paintedChunks.Add(key); // §117/B3: будет класть тайлы — выгрузке понадобится erase
		_paintJobs[key] = new ChunkPaintJob(key, chunk, new BlockLookup(map),
			originX, originY, clearEmptyLayers);
		if (_paintQueued.Add(key))
			_paintQueue.Enqueue(key);
	}

	private static bool IsOccludedByColony(long originX, long originY)
	{
		return originX >= 0 && originX + WorldCoordinates.ChunkSize <= MapRenderer.MapWidth
			&& originY >= 0 && originY + WorldCoordinates.ChunkSize <= MapRenderer.MapHeight;
	}

	private void ProcessPaintSlices(int maxSlices)
	{
		int slices = Math.Max(1, maxSlices);
		int maxCells = Math.Clamp(MaxCellsPerPaintSlice, 128, WorldCoordinates.ChunkSize * WorldCoordinates.ChunkSize);
		var sw = System.Diagnostics.Stopwatch.StartNew();
		while (slices > 0 && _paintQueue.Count > 0)
		{
			if (sw.ElapsedMilliseconds >= 4)
				break;

			ChunkKey key = _paintQueue.Dequeue();
			_paintQueued.Remove(key);
			if (!_paintJobs.TryGetValue(key, out ChunkPaintJob job))
				continue; // stale queue token after unload
			if (!_chunkData.ContainsKey(key))
			{
				_paintJobs.Remove(key);
				continue;
			}

			ReadOnlySpan<GeneratedCell> cells = job.Chunk.Cells.Span;
			int total = cells.Length;
			if (job.Cursor >= total)
			{
				_paintJobs.Remove(key);
				MarkTerrainRing(job.OriginX, job.OriginY);
				continue;
			}

			int end = Math.Min(total, job.Cursor + maxCells);
			for (int index = job.Cursor; index < end; index++)
			{
				int x = index % WorldCoordinates.ChunkSize;
				int y = index / WorldCoordinates.ChunkSize;
				PaintCell(job.OriginX + x, job.OriginY + y, index, cells[index],
					job.Lookup, job.ClearEmptyLayers);
			}
			job.Cursor = end;
			slices--;

			if (job.Cursor >= total)
			{
				_paintJobs.Remove(key);
				MarkTerrainRing(job.OriginX, job.OriginY);
			}
			else if (_paintQueued.Add(key))
			{
				_paintQueue.Enqueue(key);
			}
		}
	}

	private void ScheduleEraseChunk(ChunkKey key, ushort[] markIndices, ushort[] planIndices)
	{
		// Replace an existing job for the same key but retain its queue token.
		_eraseJobs[key] = new ChunkEraseJob(key, markIndices, planIndices);
		if (_eraseScheduled.Add(key))
			_eraseQueue.Enqueue(key);
	}

	private ushort[] CollectOverlayIndices(Dictionary<byte, HashSet<ushort>> map, bool plans)
	{
		if (map == null || map.Count == 0)
			return Array.Empty<ushort>();
		var indices = new HashSet<ushort>();
		if (plans)
		{
			AddIndices(indices, map, WorldDeltaKind.WallPlan);
			AddIndices(indices, map, WorldDeltaKind.BuildingPlan);
			AddIndices(indices, map, WorldDeltaKind.CropPlan);
			AddIndices(indices, map, WorldDeltaKind.StockpilePlan);
			AddIndices(indices, map, WorldDeltaKind.CutTreePlan);
			AddIndices(indices, map, WorldDeltaKind.MinedStonePlan);
		}
		else
		{
			if (_wallLayer == null)
				AddIndices(indices, map, WallKind);
			AddIndices(indices, map, WorldDeltaKind.Crop);
			AddIndices(indices, map, WorldDeltaKind.Stockpile);
			AddIndices(indices, map, WorldDeltaKind.Building);
		}
		if (indices.Count == 0)
			return Array.Empty<ushort>();
		var result = new ushort[indices.Count];
		indices.CopyTo(result);
		Array.Sort(result);
		return result;
	}

	private static void AddIndices(HashSet<ushort> into,
		Dictionary<byte, HashSet<ushort>> map, byte kind)
	{
		if (!map.TryGetValue(kind, out HashSet<ushort> set))
			return;
		foreach (ushort index in set)
			if (index < WorldCoordinates.ChunkSize * WorldCoordinates.ChunkSize)
				into.Add(index);
	}

	private void ProcessEraseWork(int maxCells)
	{
		int budget = Math.Max(1, maxCells);
		int chunkCellCount = WorldCoordinates.ChunkSize * WorldCoordinates.ChunkSize;
		while (budget > 0 && _eraseQueue.Count > 0)
		{
			ChunkKey key = _eraseQueue.Peek();
			if (!_eraseJobs.TryGetValue(key, out ChunkEraseJob job))
			{
				_eraseQueue.Dequeue();
				_eraseScheduled.Remove(key);
				continue;
			}

			long originX = key.X * (long)WorldCoordinates.ChunkSize;
			long originY = key.Y * (long)WorldCoordinates.ChunkSize;
			while (budget > 0 && job.Cursor < chunkCellCount)
			{
				int index = job.Cursor;
				var pos = new Vector2I((int)(originX + index % WorldCoordinates.ChunkSize),
					(int)(originY + index / WorldCoordinates.ChunkSize));
				_groundLayer?.EraseCell(pos);

				if (job.MarkCursor < job.MarkIndices.Length && job.MarkIndices[job.MarkCursor] == index)
				{
					_markLayer?.EraseCell(pos);
					job.MarkCursor++;
				}
				if (job.PlanCursor < job.PlanIndices.Length && job.PlanIndices[job.PlanCursor] == index)
				{
					_planLayer?.EraseCell(pos);
					job.PlanCursor++;
				}
				job.Cursor++;
				budget--;
			}

			if (job.Cursor >= chunkCellCount)
			{
				_eraseQueue.Dequeue();
				_eraseJobs.Remove(key);
				_eraseScheduled.Remove(key);
			}
			else
			{
				break; // continue the head chunk next frame; don't produce a long frame
			}
		}
	}

	// Select only the closest `budget` dirty chunks. Sorting the entire dirty set every
	// frame was wasteful during teleports, when arrivals can outpace the paint budget.
	private void SelectNearestDirtyChunks(int limit)
	{
		_dirtyBuffer.Clear();
		foreach (ChunkKey candidate in _textureDirty)
		{
			if (_eraseJobs.ContainsKey(candidate))
				continue;
			if (_dirtyBuffer.Count < limit)
			{
				_dirtyBuffer.Add(candidate);
				int i = _dirtyBuffer.Count - 1;
				while (i > 0)
				{
					int parent = (i - 1) / 2;
					if (CompareByDistanceToFocus(_dirtyBuffer[i], _dirtyBuffer[parent]) <= 0)
						break;
					(_dirtyBuffer[i], _dirtyBuffer[parent]) = (_dirtyBuffer[parent], _dirtyBuffer[i]);
					i = parent;
				}
				continue;
			}

			// Max-heap root is the farthest currently selected chunk.
			if (CompareByDistanceToFocus(candidate, _dirtyBuffer[0]) >= 0)
				continue;
			_dirtyBuffer[0] = candidate;
			int root = 0;
			while (true)
			{
				int left = root * 2 + 1;
				if (left >= _dirtyBuffer.Count)
					break;
				int right = left + 1;
				int farther = right < _dirtyBuffer.Count
					&& CompareByDistanceToFocus(_dirtyBuffer[right], _dirtyBuffer[left]) > 0 ? right : left;
				if (CompareByDistanceToFocus(_dirtyBuffer[farther], _dirtyBuffer[root]) <= 0)
					break;
				(_dirtyBuffer[root], _dirtyBuffer[farther]) = (_dirtyBuffer[farther], _dirtyBuffer[root]);
				root = farther;
			}
		}
		_dirtyBuffer.Sort(CompareByDistanceToFocus);
	}

	/// <summary>Пометить горы/стены выгружаемого чанка к стиранию (данные ещё в памяти).</summary>
	private void MarkTerrainForUnload(ChunkKey key)
	{
		if (_mountainTerrain == null && _wallTerrain == null)
			return;
		long originX = key.X * (long)WorldCoordinates.ChunkSize;
		long originY = key.Y * (long)WorldCoordinates.ChunkSize;
		if (!FitsTileMap(originX, originY))
			return;
		if (_mountainTerrain != null && _chunkData.TryGetValue(key, out WorldChunk chunk))
		{
			ReadOnlySpan<GeneratedCell> cells = chunk.Cells.Span;
			for (int i = 0; i < cells.Length; i++)
			{
				if (cells[i].Terrain != BaseTerrainKind.Mountain)
					continue;
				int cx = i % WorldCoordinates.ChunkSize;
				int cy = i / WorldCoordinates.ChunkSize;
				_mountainTerrain.MarkDirty(new Vector2I((int)(originX + cx), (int)(originY + cy)));
			}
		}
		if (_wallTerrain != null
			&& _blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> map)
			&& map.TryGetValue(WallKind, out HashSet<ushort> walls))
		{
			foreach (ushort index in walls)
			{
				int cx = index % WorldCoordinates.ChunkSize;
				int cy = index / WorldCoordinates.ChunkSize;
				_wallTerrain.MarkDirty(new Vector2I((int)(originX + cx), (int)(originY + cy)));
			}
		}
		// The neighbour-side terrain tiles must reconnect after this chunk is removed.
		MarkTerrainRing(originX, originY);
	}

	/// <summary>Сортировка чанков по удалённости от центра видимого окна (клетки).</summary>
	private int CompareByDistanceToFocus(ChunkKey a, ChunkKey b)
	{
		double half = WorldCoordinates.ChunkSize / 2.0;
		double ax = (double)a.X * WorldCoordinates.ChunkSize + half - _focusCellX;
		double ay = (double)a.Y * WorldCoordinates.ChunkSize + half - _focusCellY;
		double bx = (double)b.X * WorldCoordinates.ChunkSize + half - _focusCellX;
		double by = (double)b.Y * WorldCoordinates.ChunkSize + half - _focusCellY;
		double da = ax * ax + ay * ay;
		double db = bx * bx + by * by;
		return da.CompareTo(db);
	}

	private HashSet<ushort> KindSet(ChunkKey key, byte kind)
		=> _blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> map)
			&& map.TryGetValue(kind, out HashSet<ushort> set) ? set : null;

	private HashSet<ushort> GetOrCreateSet(ChunkKey key, byte kind)
	{
		if (!_blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> map))
		{
			map = new Dictionary<byte, HashSet<ushort>>();
			_blocks[key] = map;
		}
		if (!map.TryGetValue(kind, out HashSet<ushort> set))
		{
			set = new HashSet<ushort>();
			map[kind] = set;
		}
		return set;
	}

	// ---------- Рендер: настоящие тайлы игры (1 клетка = 1 тайл 64×64) ----------

	/// <summary>Прозрачность слоя чертежей (§29): план отличим от готовой постройки.</summary>
	private const float PlanAlpha = 0.55f;

	private const int SrcCrop = 8;       // food/corn.png
	private const int SrcStockpile = 9;  // icons/logistic_zone.png
	private const int SrcWork = 10;      // sprites/ProcessOfWork.png
	private const int SrcWall = 11;      // construction items/wooden_wall.png (7×7)

	// Terrain-разметка сцен (mountains_tile, wood_wall_tile) — как в MapRenderer.
	private const int MountainTerrainSetId = 0;
	private const int MountainTerrainId = 0;
	private const int WallTerrainSetId = 0;
	private const int WallTerrainId = 0;

	/// <summary>Деревья/камни — пороги декора (общий источник истины с доступом к миру).</summary>
	private const ushort ForestDecorMin = WorldDecor.ForestMinQ16;
	private const ushort StoneDecorMin = WorldDecor.StoneMinQ16;

	// §117/F4: шаг сетки климата плодородия и сторона сетки 5×5 на чанк 64×64 —
	// ровно как в UnifiedWorldGenerator.GenerateChunk/GenerateColonyWindow,
	// чтобы SoilFertilityQ16 (порог рощ) совпадал с островом клетка в клетку.
	private const int FertilityGridStep = 16;
	private const int FertilityGridSide = WorldCoordinates.ChunkSize / FertilityGridStep + 1;

	private static readonly string[] TexTrees =
	{
		"res://ui/assets/textures/objects/tree.png",
		"res://ui/assets/textures/objects/tree_1.png",
		"res://ui/assets/textures/objects/tree_2.png",
        "res://ui/assets/textures/objects/tree_3.png"
	};

	/// <summary>
	/// Слои мира: земля (трава/вода/горы), декор (лес/камни), метки игрока.
	/// TileSet — из стандартных текстур игры, размер тайла 64 px: клетка = тайл.
	/// </summary>
	private void BuildTileLayers()
	{
		_tileSet = new TileSet { TileSize = new Vector2I(TilePx, TilePx) };
		AddSingleTile("res://ui/assets/textures/food/corn.png", SrcCrop);
		AddStockpileZoneTile(SrcStockpile);
		AddSingleTile("res://ui/assets/textures/sprites/ProcessOfWork.png", SrcWork);
		AddAtlasTileSet("res://ui/assets/textures/construction items/wooden_wall.png", SrcWall);
		BuildCompositeGroundSource();

		_groundLayer = MakeLayer("WorldGroundAndDecor", _tileSet);
		try
		{
			Shader groundShader = GD.Load<Shader>("res://src/shaders/streaming_ground.gdshader");
			if (groundShader != null)
			{
				_groundMaterial = new ShaderMaterial { Shader = groundShader };
				_groundLayer.Material = _groundMaterial;
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[World] Ground shader load failed: {ex.Message}");
		}

		_markLayer = MakeLayer("WorldMark", _tileSet);
		AddChild(_groundLayer);
		AddChild(_markLayer);

		// Горы, стены и грядки выглядят как в игре: TileSet берём из сцен (там terrain-разметка),
		// и соединения подбирает движок, а не мы вручную атласом.
		TileSet mountainTiles = MapRenderer.GetMountainTerrainTileSet();
		if (mountainTiles != null)
		{
			_mountainLayer = MakeLayer("WorldMountain", mountainTiles);
			AddChild(_mountainLayer);
			_mountainTerrain = new TerrainTileLayer(_mountainLayer, MountainTerrainSetId, MountainTerrainId);
		}
		TileSet wallTiles = MapRenderer.GetWallTerrainTileSet();
		if (wallTiles != null)
		{
			// Коллизии стен считаются геймплеем по меткам-блокам (WorldGameRules /
			// _blocks / MapData), НЕ через физику тайлов: в проекте нет ни одного
			// CharacterBody/RigidBody/Area2D/запроса пересечений — поэтому у ВСЕХ
			// слоёв вида tile-физика/навигация/окклюзия отключены (§117, researcher-аудит).
			_wallLayer = MakeLayer("WorldWall", wallTiles);
			AddChild(_wallLayer);
			_wallTerrain = new TerrainTileLayer(_wallLayer, WallTerrainSetId, WallTerrainId);

			_wallPlanLayer = MakeLayer("WorldWallPlan", wallTiles);
			_wallPlanLayer.Modulate = new Color(1f, 1f, 1f, PlanAlpha);
			AddChild(_wallPlanLayer);
			_wallPlanTerrain = new TerrainTileLayer(_wallPlanLayer, WallTerrainSetId, WallTerrainId);
		}

		TileSet farmTiles = MapRenderer.GetGardenBedTerrainTileSet();
		if (farmTiles != null)
		{
			_farmLayer = MakeLayer("WorldFarm", farmTiles);
			AddChild(_farmLayer);
			_farmTerrain = new TerrainTileLayer(_farmLayer, 0, 0);

			_farmPlanLayer = MakeLayer("WorldFarmPlan", farmTiles);
			_farmPlanLayer.Modulate = new Color(1f, 1f, 1f, PlanAlpha);
			AddChild(_farmPlanLayer);
			_farmPlanTerrain = new TerrainTileLayer(_farmPlanLayer, 0, 0);
		}

		// §29: чертежи (планы) — своим слоем и полупрозрачно, чтобы отличать от готового.
		_planLayer = MakeLayer("WorldPlan", _tileSet);
		_planLayer.Modulate = new Color(1f, 1f, 1f, PlanAlpha);
		AddChild(_planLayer);
	}

	/// <summary>
	/// §117 (researcher-аудит): у TileMapLayer дефолты collision/navigation/occlusion = true —
	/// каждая партия SetCell/EraseCell прогоняла клетки через эти подсистемы даром.
	/// Геймплей их не использует (коллизии — свои, по MapData/меткам-блокам; в проекте нет
	/// ни одного физического тела или запроса пересечений), поэтому отключаем всё.
	/// RenderingQuadrantSize=64 = размеру чанка (рекомендация мейнтейнера TileMapLayer),
	/// Y-sort остаётся выключенным.
	/// </summary>
	private static TileMapLayer MakeLayer(string name, TileSet tileSet)
		=> new()
		{
			Name = name,
			TileSet = tileSet,
			RenderingQuadrantSize = 64,
			CollisionEnabled = false,
			NavigationEnabled = false,
			OcclusionEnabled = false
		};

	/// <summary>Процедурный фиолетовый тайл складской зоны (64×64) с четкой границей вместо иконки.</summary>
	private void AddStockpileZoneTile(int sourceId)
	{
		var img = Image.CreateEmpty(TilePx, TilePx, false, Image.Format.Rgba8);
		var fillColor = new Color(0.65f, 0.15f, 0.9f, 0.5f);
		var borderColor = new Color(0.85f, 0.35f, 1f, 0.9f);
		for (int y = 0; y < TilePx; y++)
		{
			for (int x = 0; x < TilePx; x++)
			{
				bool isBorder = x <= 1 || x >= TilePx - 2 || y <= 1 || y >= TilePx - 2;
				img.SetPixel(x, y, isBorder ? borderColor : fillColor);
			}
		}
		var tex = ImageTexture.CreateFromImage(img);
		var source = new TileSetAtlasSource
		{
			Texture = tex,
			TextureRegionSize = new Vector2I(TilePx, TilePx)
		};
		source.CreateTile(Vector2I.Zero);
		_tileSet.AddSource(source, sourceId);
	}

	/// <summary>Одиночный тайл 64×64 (трава, вода, дерево, посев, склад, стройка).</summary>
	private void AddSingleTile(string path, int sourceId)
	{
		Texture2D tex = GD.Load<Texture2D>(path);
		if (tex == null)
		{
			GD.PrintErr($"[World] нет текстуры {path}");
			return;
		}
		var source = new TileSetAtlasSource
		{
			Texture = tex,
			TextureRegionSize = new Vector2I(TilePx, TilePx)
		};
		source.CreateTile(Vector2I.Zero);
		_tileSet.AddSource(source, sourceId);
	}

	/// <summary>Атлас N×M тайлов 64×64 (горы 7×7, камень 2×2, стены 7×7).</summary>
	private void AddAtlasTileSet(string path, int sourceId)
	{
		Texture2D tex = GD.Load<Texture2D>(path);
		if (tex == null)
		{
			GD.PrintErr($"[World] нет текстуры {path}");
			return;
		}
		var source = new TileSetAtlasSource
		{
			Texture = tex,
			TextureRegionSize = new Vector2I(TilePx, TilePx)
		};
		int cols = Mathf.Max(1, tex.GetWidth() / TilePx);
		int rows = Mathf.Max(1, tex.GetHeight() / TilePx);
		for (int y = 0; y < rows; y++)
		{
			for (int x = 0; x < cols; x++)
				source.CreateTile(new Vector2I(x, y));
		}
		_tileSet.AddSource(source, sourceId);
	}

	private const int SrcCompositeGround = 12;
	private const int CompositeGrass = 0;
	private const int CompositeWater = 1;
	private const int CompositeMountain = 2;
	private const int CompositeTree0 = 3;
	private const int CompositeStone0 = 7;
	private const int CompositeSand = 11;
	private const int CompositeSandStone0 = 12;
	private const int CompositeSteppe = 16;
	private const int CompositeSteppeTree0 = 17;
	private const int CompositeSteppeStone0 = 21;
	private const int CompositeGlacier = 25;
	private const int CompositeGlacierStone0 = 26;
	private const int CompositeAtlasTileCount = 30;

	private static Image LoadTileImage(string path, int atlasX = 0, int atlasY = 0, Color fallback = default)
	{
		Texture2D texture = GD.Load<Texture2D>(path);
		Image image = texture?.GetImage();
		int x = atlasX * TilePx;
		int y = atlasY * TilePx;
		if (image == null || image.GetWidth() < x + TilePx || image.GetHeight() < y + TilePx)
		{
			var missing = Image.CreateEmpty(TilePx, TilePx, false, Image.Format.Rgba8);
			missing.Fill(fallback.A == 0f ? new Color(1f, 0f, 1f, 1f) : fallback);
			GD.PrintErr($"[World] composite source tile unavailable: {path} [{atlasX},{atlasY}]");
			return missing;
		}
		Image tile = image.GetRegion(new Rect2I(x, y, TilePx, TilePx));
		if (tile.GetFormat() != Image.Format.Rgba8)
			tile.Convert(Image.Format.Rgba8);
		return tile;
	}

	private static Image CreateTintedTile(Image source, Color tint)
	{
		Image result = (Image)source.Duplicate();
		for (int y = 0; y < result.GetHeight(); y++)
		{
			for (int x = 0; x < result.GetWidth(); x++)
			{
				Color c = result.GetPixel(x, y);
				result.SetPixel(x, y, new Color(c.R * tint.R, c.G * tint.G, c.B * tint.B, c.A * tint.A));
			}
		}
		return result;
	}

	private static Image CreateGlacierTile(Image source)
	{
		Image result = (Image)source.Duplicate();
		for (int y = 0; y < result.GetHeight(); y++)
		{
			for (int x = 0; x < result.GetWidth(); x++)
			{
				Color c = result.GetPixel(x, y);
				float r = Mathf.Clamp(c.R * 0.35f + 0.65f, 0f, 1f);
				float g = Mathf.Clamp(c.G * 0.35f + 0.70f, 0f, 1f);
				float b = Mathf.Clamp(c.B * 0.35f + 0.85f, 0f, 1f);
				result.SetPixel(x, y, new Color(r, g, b, c.A));
			}
		}
		return result;
	}

	private static void BlitTile(Image target, Image tile, int atlasX)
		=> target.BlitRect(tile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(atlasX * TilePx, 0));

	private void BuildCompositeGroundSource()
	{
		// Полнопланетарный мульти-биомный атлас (30 тайлов 64×64):
		// - 0..10: умеренные широты (трава, вода, горы, 4 варианта деревьев, 4 камня)
		// - 11..15: пустыня (золотой песок sand.png + 4 варианта камней на песке)
		// - 16..24: степь / саванна (сухая трава + 4 дерева + 4 камня)
		// - 25..29: ледник / тундра (арктический снег и лед + 4 валуна)
		var atlas = Image.CreateEmpty(TilePx * CompositeAtlasTileCount, TilePx, false, Image.Format.Rgba8);
		Image grass = LoadTileImage("res://ui/assets/textures/tiles/grass.png", fallback: new Color(0.2f, 0.55f, 0.2f));
		Image water = LoadTileImage("res://ui/assets/textures/tiles/water.png", fallback: new Color(0.1f, 0.3f, 0.8f));
		Image mountain = LoadTileImage("res://ui/assets/textures/tiles/mountains.png", 3, 3, new Color(0.45f, 0.45f, 0.45f));
		Image sand = LoadTileImage("res://ui/assets/textures/tiles/sand.png", fallback: new Color(0.85f, 0.75f, 0.35f));
		Image steppe = CreateTintedTile(grass, new Color(0.85f, 0.88f, 0.50f));
		Image glacier = CreateGlacierTile(grass);

		BlitTile(atlas, grass, CompositeGrass);
		BlitTile(atlas, water, CompositeWater);
		BlitTile(atlas, mountain, CompositeMountain);
		BlitTile(atlas, sand, CompositeSand);
		BlitTile(atlas, steppe, CompositeSteppe);
		BlitTile(atlas, glacier, CompositeGlacier);

		for (int v = 0; v < TexTrees.Length; v++)
		{
			Image tree = LoadTileImage(TexTrees[v], fallback: new Color(0.1f, 0.35f, 0.1f, 1f));
			int gIndex = CompositeTree0 + v;
			atlas.BlendRect(tree, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(gIndex * TilePx, 0));

			int sIndex = CompositeSteppeTree0 + v;
			atlas.BlendRect(tree, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(sIndex * TilePx, 0));
		}

		Image stone = LoadTileImage("res://ui/assets/textures/objects/stone.png", 0, 0,
			new Color(0.45f, 0.45f, 0.45f, 1f));
		for (int variant = 0; variant < 4; variant++)
		{
			Image stoneTile = variant == 0 ? stone : LoadTileImage(
				"res://ui/assets/textures/objects/stone.png", variant & 1, (variant >> 1) & 1,
				new Color(0.45f, 0.45f, 0.45f, 1f));

			int gStoneIndex = CompositeStone0 + variant;
			atlas.BlendRect(stoneTile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(gStoneIndex * TilePx, 0));

			int sandStoneIndex = CompositeSandStone0 + variant;
			atlas.BlendRect(stoneTile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(sandStoneIndex * TilePx, 0));

			int steppeStoneIndex = CompositeSteppeStone0 + variant;
			atlas.BlendRect(stoneTile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(steppeStoneIndex * TilePx, 0));

			int glacierStoneIndex = CompositeGlacierStone0 + variant;
			atlas.BlendRect(stoneTile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(glacierStoneIndex * TilePx, 0));
		}

		var source = new TileSetAtlasSource
		{
			Texture = ImageTexture.CreateFromImage(atlas),
			TextureRegionSize = new Vector2I(TilePx, TilePx)
		};
		for (int i = 0; i < CompositeAtlasTileCount; i++)
			source.CreateTile(new Vector2I(i, 0));
		_tileSet.AddSource(source, SrcCompositeGround);
	}

	// ---------- §117/F4: паритет прореживания декора с островом ----------

	/// <summary>
	/// Прореживание декора — ТОЧНАЯ копия формулы UnifiedWorldGenerator.GenerateColonyWindow
	/// (инвариант «деревья и камни только на чистой траве», порог рощ 55..80 по плодородию,
	/// порог осыпей 45..65 по камню). Раньше стриминг рисовал ВСЕ клетки выше порога
	/// ForestQ16/StoneQ16 — за кромкой острова лес был резко гуще (видимый шов F4).
	/// wx/wy — МИРОВЫЕ координаты клетки (у острова wx = WorldOrigin+lx — тот же номер).
	/// Единственное намеренное отличие: остаток %100 приведён к [0,100) (евклидов) —
	/// для домена острова (wx,wy ≥ 0) результат побитово совпадает с %, а в отрицательных
	/// квадрантах знаковый % давал бы r&lt;0 ⇒ «деревьев нет вообще» на пол-планеты.
	/// Публичный статический — чтобы правила игры (WorldGameRules/ChunkWorldAccess)
	/// могли принимать те же решения, что рисует вид.
	/// </summary>
	public static (bool HasTree, bool HasStone) IslandDecorPass(long wx, long wy, in GeneratedCell cell, ushort soilFertilityQ16)
	{
		bool isGrass = cell.Terrain == BaseTerrainKind.Grass;

		// Естественная плотность рощ: группами с полянками; на плодородных почвах гуще.
		bool inForestGrove = isGrass && cell.ForestQ16 >= ForestDecorMin;
		bool hasTree = false;
		if (inForestGrove)
		{
			int groveThreshold = 55 + soilFertilityQ16 * 25 / 65535;
			hasTree = ((wx ^ (wy * 3)) & 3) != 0
				&& Mod100(wx * 17 ^ wy * 31) < groveThreshold;
		}

		// Камни: только на свободной сухой траве, не на дереве.
		bool hasStone = false;
		if (isGrass && !hasTree && cell.StoneQ16 >= StoneDecorMin)
		{
			int stoneThreshold = 45 + (int)cell.StoneQ16 * 20 / 65535;
			hasStone = Mod100(wx * 7 ^ wy * 13) < stoneThreshold;
		}
		return (hasTree, hasStone);
	}

	/// <summary>Евклидов остаток по модулю 100 (для значений ≥ 0 эквивалентен C# %).</summary>
	private static int Mod100(long value)
	{
		int r = (int)(value % 100);
		return r < 0 ? r + 100 : r;
	}

	/// <summary>
	/// Плодородие почвы клетки (SoilFertilityQ16) — та же 16×16-билинейная сетка, которой
	/// его видит GenerateColonyWindow/GenerateChunk: якоря кратны 16 от начала чанка в
	/// МИРОВЫХ координатах, интерполяция — LerpQ16 (копия генераторной). GeneratedCell
	/// плодородия не хранит. Одноразовый вариант (4 семпла) — для тестов/правил;
	/// горячий путь рендера идёт через кэш CellSoilFertilityQ16 (25 семплов на чанк).
	/// </summary>
	public static ushort SoilFertilityAtCell(ulong worldSeed, uint generatorVersion, long cellX, long cellY)
	{
		ChunkKey key = WorldCoordinates.ChunkForTile(cellX, cellY);
		LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
		long baseX = UnifiedWorldGenerator.WorldOriginX + key.X * (long)WorldCoordinates.ChunkSize;
		long baseY = UnifiedWorldGenerator.WorldOriginY + key.Y * (long)WorldCoordinates.ChunkSize;
		long ax = baseX + (local.X / FertilityGridStep) * FertilityGridStep;
		long ay = baseY + (local.Y / FertilityGridStep) * FertilityGridStep;
		uint u = (uint)((local.X % FertilityGridStep) * 65535 / FertilityGridStep);
		uint v = (uint)((local.Y % FertilityGridStep) * 65535 / FertilityGridStep);

		ushort nw = WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, ax, ay).SoilFertilityQ16;
		ushort ne = WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, ax + FertilityGridStep, ay).SoilFertilityQ16;
		ushort sw = WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, ax, ay + FertilityGridStep).SoilFertilityQ16;
		ushort se = WorldClimateGenerator.SampleBlendedCell(worldSeed, generatorVersion, ax + FertilityGridStep, ay + FertilityGridStep).SoilFertilityQ16;
		return LerpQ16(LerpQ16(nw, ne, u), LerpQ16(sw, se, u), v);
	}

	/// <summary>Кэшированный для рендера аналог SoilFertilityAtCell (сетка 5×5 на чанк).</summary>
	private ushort CellSoilFertilityQ16(long cellX, long cellY)
	{
		ChunkKey key = WorldCoordinates.ChunkForTile(cellX, cellY);
		if (!_fertilityValid || _fertilityChunk != key)
			BuildFertilityGrid(key);
		LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
		int row = (local.Y / FertilityGridStep) * FertilityGridSide;
		int col = local.X / FertilityGridStep;
		uint u = (uint)((local.X % FertilityGridStep) * 65535 / FertilityGridStep);
		uint v = (uint)((local.Y % FertilityGridStep) * 65535 / FertilityGridStep);
		ushort top = LerpQ16(_fertilityGrid[row + col], _fertilityGrid[row + col + 1], u);
		ushort bottom = LerpQ16(_fertilityGrid[row + FertilityGridSide + col],
			_fertilityGrid[row + FertilityGridSide + col + 1], u);
		return LerpQ16(top, bottom, v);
	}

	/// <summary>Якори сетки — ровно как в GenerateChunk: WorldOrigin + начало чанка + 16·g.</summary>
	private void BuildFertilityGrid(ChunkKey key)
	{
		long originX = UnifiedWorldGenerator.WorldOriginX + key.X * (long)WorldCoordinates.ChunkSize;
		long originY = UnifiedWorldGenerator.WorldOriginY + key.Y * (long)WorldCoordinates.ChunkSize;
		for (int gy = 0; gy < FertilityGridSide; gy++)
		{
			int row = gy * FertilityGridSide;
			long wy = originY + gy * FertilityGridStep;
			for (int gx = 0; gx < FertilityGridSide; gx++)
			{
				_fertilityGrid[row + gx] = WorldClimateGenerator.SampleBlendedCell(
					WorldSeed, GeneratorVersion, originX + gx * FertilityGridStep, wy).SoilFertilityQ16;
			}
		}
		_fertilityChunk = key;
		_fertilityValid = true;
	}

	/// <summary>LerpQ16 — побитовая копия интерполяции генератора (округление +32768).</summary>
	private static ushort LerpQ16(ushort a, ushort b, uint t)
	{
		uint clampedT = Math.Min(t, 65535);
		long value = (long)a * (65535 - clampedT) + (long)b * clampedT + 32768;
		return (ushort)Math.Clamp(value / 65535, 0, 65535);
	}

	private int GetCompositeGroundIndex(long cellX, long cellY, int localIndex, GeneratedCell cell, BlockLookup kinds)
	{
		if (cell.Terrain == BaseTerrainKind.Water)
			return CompositeWater;

		bool isCut = BlockLookup.Contains(kinds.CutTree, localIndex);
		bool isMined = BlockLookup.Contains(kinds.MinedStone, localIndex);

		if (cell.Terrain == BaseTerrainKind.Mountain)
		{
			if (isMined)
				return CompositeGrass;
			if (_mountainLayer == null)
				return CompositeMountain;
		}

		if (cell.Terrain == BaseTerrainKind.Grass)
		{
			var biome = (BiomeType)cell.Biome;
			if (biome == BiomeType.DeepWater)
				return CompositeWater;

			// §117/F4: мировые координаты клетки (у острова wx = WorldOrigin+lx — тот же
			// номер клетки, что и здесь) и прореживание декора ЕГО формулой, чтобы
			// плотность леса/камней через границу 512 совпадала клетка в клетку.
			long wx = UnifiedWorldGenerator.WorldOriginX + cellX;
			long wy = UnifiedWorldGenerator.WorldOriginY + cellY;
			// Плодородие (порог рощ) нужно только клеткам зоны леса; остальным — 0.
			bool needsFertility = cell.ForestQ16 >= ForestDecorMin;
			ushort fertility = needsFertility ? CellSoilFertilityQ16(cellX, cellY) : (ushort)0;
			(bool hasTree, bool hasStone) = IslandDecorPass(wx, wy, in cell, fertility);

			// Варианты спрайтов — как у острова (MapRenderer): дерево по МИРОВЫМ
			// координатам (TreeVariantFor), камень — по клеточным (StoneVariantFor).
			uint seed32 = (uint)WorldSeed;
			int treeVariant = UnifiedWorldGenerator.TreeVariantFor((int)wx, (int)wy, seed32) & 3;
			int stoneVariant = UnifiedWorldGenerator.StoneVariantFor((int)cellX, (int)cellY, seed32) & 3;

			if (biome == BiomeType.Desert)
			{
				if (!isMined && hasStone)
					return CompositeSandStone0 + stoneVariant;
				return CompositeSand;
			}
			if (biome == BiomeType.Glacier)
			{
				if (!isMined && hasStone)
					return CompositeGlacierStone0 + stoneVariant;
				return CompositeGlacier;
			}
			if (biome == BiomeType.Steppe)
			{
				if (!isCut && hasTree)
					return CompositeSteppeTree0 + treeVariant;
				if (!isMined && hasStone)
					return CompositeSteppeStone0 + stoneVariant;
				return CompositeSteppe;
			}

			// Plains, Forest, Swamp
			if (!isCut && hasTree)
				return CompositeTree0 + treeVariant;
			if (!isMined && hasStone)
				return CompositeStone0 + stoneVariant;
			return CompositeGrass;
		}
		return CompositeGrass;
	}

	/// <summary>Поставить тайлы всем клеткам чанка.</summary>
	private void PaintChunk(ChunkKey key, bool clearEmptyLayers)
		=> ScheduleChunkPaint(key, clearEmptyLayers);

	/// <summary>
	/// Пометить только внешнюю кромку чанка и соседние клетки. Внутренние клетки уже
	/// отмечены во время PaintCell; обход полного 66×66 прямоугольника был лишним.
	/// </summary>
	private void MarkTerrainRing(long originX, long originY)
	{
		if (_mountainTerrain == null && _wallTerrain == null)
			return;
		if (!FitsTileMap(originX, originY, 1))
			return;
		long maxX = originX + WorldCoordinates.ChunkSize;
		long maxY = originY + WorldCoordinates.ChunkSize;

		// Top and bottom rows, including the two outside corners.
		for (long x = originX - 1; x <= maxX; x++)
		{
			MarkTerrainAt(x, originY - 1);
			MarkTerrainAt(x, maxY);
		}
		// Left and right columns, excluding corners already handled above.
		for (long y = originY; y < maxY; y++)
		{
			MarkTerrainAt(originX - 1, y);
			MarkTerrainAt(maxX, y);
		}
	}

	private void MarkTerrainAt(long x, long y)
	{
		// Mark even cells whose feature was just removed. The terrain predicate passed
		// to Flush decides whether to keep or erase the tile. Checking IsWallCell here
		// would miss a wall that has already been removed from _blocks.
		if (!FitsTileMapCell(x, y))
			return;
		var pos = new Vector2I((int)x, (int)y);
		_mountainTerrain?.MarkDirty(pos);
		_wallTerrain?.MarkDirty(pos);
		_wallPlanTerrain?.MarkDirty(pos);
		_farmTerrain?.MarkDirty(pos);
		_farmPlanTerrain?.MarkDirty(pos);
	}

	/// <summary>Кольцо 3×3 вокруг одной клетки — для одиночных меток (стена/её снос).</summary>
	private void MarkTerrainCellRing(long cellX, long cellY)
	{
		if (_mountainTerrain == null && _wallTerrain == null && _wallPlanTerrain == null && _farmTerrain == null && _farmPlanTerrain == null)
			return;
		for (long y = cellY - 1; y <= cellY + 1; y++)
		{
			for (long x = cellX - 1; x <= cellX + 1; x++)
			{
				if (!FitsTileMapCell(x, y))
					continue;
				MarkTerrainAt(x, y);
			}
		}
	}

	/// <summary>Есть ли гора в мировой клетке (по resident-чанку).</summary>
	private bool IsMountainAt(long cellX, long cellY)
	{
		ChunkKey key = WorldCoordinates.ChunkForTile(cellX, cellY);
		if (!_chunkData.TryGetValue(key, out WorldChunk chunk))
			return false;
		if (HasBlock(WorldDeltaKind.MinedStone, cellX, cellY))
			return false;
		LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
		return chunk.Cells.Span[local.Y * WorldCoordinates.ChunkSize + local.X].Terrain
			== BaseTerrainKind.Mountain;
	}

	private bool IsMountainCell(Vector2I cell) => IsMountainAt(cell.X, cell.Y);

	private bool IsWallCell(Vector2I cell) => HasBlock(WallKind, cell.X, cell.Y);

	private bool IsWallPlanCell(Vector2I cell) => HasBlock(WorldDeltaKind.WallPlan, cell.X, cell.Y);

	private bool IsCropCell(Vector2I cell) => HasBlock(WorldDeltaKind.Crop, cell.X, cell.Y);

	private bool IsCropPlanCell(Vector2I cell) => HasBlock(WorldDeltaKind.CropPlan, cell.X, cell.Y);

	/// <summary>Перерисовать одну клетку (после AddBlock/RemoveBlock).</summary>
	private void RepaintCell(long cellX, long cellY)
	{
		ChunkKey key = WorldCoordinates.ChunkForTile(cellX, cellY);
		if (!_chunkData.TryGetValue(key, out WorldChunk chunk))
			return;
		long originX = key.X * (long)WorldCoordinates.ChunkSize;
		long originY = key.Y * (long)WorldCoordinates.ChunkSize;
		if (!FitsTileMap(originX, originY) || !FitsTileMapCell(cellX, cellY))
			return;
		if (_paintJobs.ContainsKey(key))
		{
			_textureDirty.Add(key);
			return;
		}
		LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
		int localIndex = local.Y * WorldCoordinates.ChunkSize + local.X;
		_blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> kinds);
		_paintedChunks.Add(key); // §117/B3: одиночная перекраска тоже кладёт тайлы
		PaintCell(cellX, cellY, localIndex, chunk.Cells.Span[localIndex], new BlockLookup(kinds), clearEmptyLayers: true);
		MarkTerrainCellRing(cellX, cellY);
	}

	/// <summary>Один тайл на клетку: земля + декор (лес/камни) + метки игрока.</summary>
	private void PaintCell(long cellX, long cellY, int localIndex, GeneratedCell cell,
		BlockLookup kinds, bool clearEmptyLayers)
	{
		if (cellX >= 0 && cellY >= 0 && cellX < MapRenderer.MapWidth && cellY < MapRenderer.MapHeight)
			return;

		var pos = new Vector2I((int)cellX, (int)cellY);
		int compositeIndex = GetCompositeGroundIndex(cellX, cellY, localIndex, cell, kinds);
		if (compositeIndex == CompositeWater || compositeIndex == CompositeGrass ||
			compositeIndex == CompositeSand || compositeIndex == CompositeSteppe ||
			compositeIndex == CompositeGlacier)
		{
			_groundLayer.EraseCell(pos);
		}
		else
		{
			_groundLayer.SetCell(pos, SrcCompositeGround, new Vector2I(compositeIndex, 0));
		}

		// A single ground tile now already contains grass/water/mountain fallback and
		// its tree/stone pixels, so no per-cell decor EraseCell/SetCell calls are needed.
		if (_mountainLayer != null && cell.Terrain == BaseTerrainKind.Mountain)
			_mountainTerrain.MarkDirty(pos);

		if (clearEmptyLayers)
		{
			_markLayer.EraseCell(pos);
			_planLayer?.EraseCell(pos);
			_wallPlanLayer?.EraseCell(pos);
			_farmPlanLayer?.EraseCell(pos);
		}
		if (BlockLookup.Contains(kinds.Wall, localIndex))
		{
			if (_wallLayer != null)
				_wallTerrain.MarkDirty(pos);
			else
				_markLayer.SetCell(pos, SrcWall, Vector2I.Zero);
		}
		else if (BlockLookup.Contains(kinds.Crop, localIndex))
		{
			if (_farmLayer != null)
				_farmTerrain.MarkDirty(pos);
			else
				_markLayer.SetCell(pos, SrcCrop, Vector2I.Zero);
		}
		else if (BlockLookup.Contains(kinds.Stockpile, localIndex))
			_markLayer.SetCell(pos, SrcStockpile, Vector2I.Zero);
		else if (BlockLookup.Contains(kinds.Building, localIndex))
			_markLayer.SetCell(pos, SrcWork, Vector2I.Zero);

		if (BlockLookup.Contains(kinds.WallPlan, localIndex))
		{
			if (_wallPlanLayer != null)
				_wallPlanTerrain.MarkDirty(pos);
			else
				_planLayer?.SetCell(pos, SrcWall, Vector2I.Zero);
		}
		else if (BlockLookup.Contains(kinds.CropPlan, localIndex))
		{
			if (_farmPlanLayer != null)
				_farmPlanTerrain.MarkDirty(pos);
			else
				_planLayer?.SetCell(pos, SrcCrop, Vector2I.Zero);
		}
		else if (BlockLookup.Contains(kinds.StockpilePlan, localIndex))
			_planLayer?.SetCell(pos, SrcStockpile, Vector2I.Zero);
		else if (BlockLookup.Contains(kinds.BuildingPlan, localIndex)
			|| BlockLookup.Contains(kinds.CutTreePlan, localIndex)
			|| BlockLookup.Contains(kinds.MinedStonePlan, localIndex))
			_planLayer?.SetCell(pos, SrcWork, Vector2I.Zero);
	}

	private readonly struct BlockLookup
	{
		public readonly HashSet<ushort> CutTree;
		public readonly HashSet<ushort> MinedStone;
		public readonly HashSet<ushort> Wall;
		public readonly HashSet<ushort> Crop;
		public readonly HashSet<ushort> Stockpile;
		public readonly HashSet<ushort> Building;
		public readonly HashSet<ushort> WallPlan;
		public readonly HashSet<ushort> CropPlan;
		public readonly HashSet<ushort> StockpilePlan;
		public readonly HashSet<ushort> BuildingPlan;
		public readonly HashSet<ushort> CutTreePlan;
		public readonly HashSet<ushort> MinedStonePlan;

		public BlockLookup(Dictionary<byte, HashSet<ushort>> map)
		{
			CutTree = Get(map, WorldDeltaKind.CutTree);
			MinedStone = Get(map, WorldDeltaKind.MinedStone);
			Wall = Get(map, WallKind);
			Crop = Get(map, WorldDeltaKind.Crop);
			Stockpile = Get(map, WorldDeltaKind.Stockpile);
			Building = Get(map, WorldDeltaKind.Building);
			WallPlan = Get(map, WorldDeltaKind.WallPlan);
			CropPlan = Get(map, WorldDeltaKind.CropPlan);
			StockpilePlan = Get(map, WorldDeltaKind.StockpilePlan);
			BuildingPlan = Get(map, WorldDeltaKind.BuildingPlan);
			CutTreePlan = Get(map, WorldDeltaKind.CutTreePlan);
			MinedStonePlan = Get(map, WorldDeltaKind.MinedStonePlan);
		}

		private static HashSet<ushort> Get(Dictionary<byte, HashSet<ushort>> map, byte kind)
			=> map != null && map.TryGetValue(kind, out HashSet<ushort> set) ? set : null;

		public static bool Contains(HashSet<ushort> set, int localIndex)
			=> set != null && set.Contains((ushort)localIndex);
	}

	/// <summary>
	/// Клетки чанка представимы в int32: TileMapLayer адресует клетки int32, и за этим
	/// диапазоном рисование/стирание «завернулось» бы и стёрло чужие тайлы. Такой чанк
	/// просто не рисуем (мир дальше ~±2.1 млрд клеток не отображается — предел TileMapLayer).
	/// </summary>
	private static bool FitsTileMap(long originX, long originY, long margin = 0)
		=> originX >= (long)int.MinValue + margin
			&& originY >= (long)int.MinValue + margin
			&& originX <= (long)int.MaxValue - WorldCoordinates.ChunkSize + 1 - margin
			&& originY <= (long)int.MaxValue - WorldCoordinates.ChunkSize + 1 - margin;

	private static bool FitsTileMapCell(long x, long y)
		=> x >= int.MinValue && x <= int.MaxValue
			&& y >= int.MinValue && y <= int.MaxValue;

}
