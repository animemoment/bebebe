using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.UI;
using Godot;

namespace Game.UI.Streaming;

/// <summary>
/// Главнопоточный Node2D-вид бесконечного мира. На близком масштабе использует
/// полноразмерные игровые тайлы, но земля и декор заранее скомпонованы в один
/// точный атлас (уменьшает вызовы TileMapLayer). На обзорном масштабе каждый чанк
/// представлен 64×64 LOD-текстурой: один пиксель на клетку. Дополнительно ведёт персистентные
/// клеточные метки-«блоки» нескольких видов (стены, здания, добытое, вырубленное,
/// склады): изменения кодируются через WorldChunkDeltaCodec и сохраняются
/// при выгрузке чанка в WorldChunkStore, при повторном входе восстанавливаются из
/// snapshot'а. Никакого обращения к legacy MapGenerator/MapData/MapRenderer.
/// Реализует IWallWorld (контракт для StreamingWorldManager) и IBlockWorld
/// (мульти-kind доступ для менеджеров главной игры).
/// </summary>
public partial class StreamingWorldView : Node2D, IWallWorld, IBlockWorld, IWorldCellSource
{
	/// <summary>Kind записей delta для пользовательских постоянных меток (стен).</summary>
	public const byte WallKind = WorldDeltaKind.Wall;

	/// <summary>Пикселей текстуры на одну игровую клетку (сейчас — тайл 64×64 на клетку).</summary>
	// Рендер — настоящими тайлами игры: 1 клетка = один тайл 64×64 (TilePx).
	// Раньше чанк рисовался одной текстурой с 4 текселами на клетку — при 64 px
	// на клетку это давало «пиксельную кашу» вместо тайлов.
	private const int TilePx = 64;

	[Export] public ulong WorldSeed = 0x0123456789ABCDEFUL;
	[Export] public uint GeneratorVersion = 3;
	[Export] public int MaxConcurrency = 2;
	// Hard per-frame budgets prevent a large batch of completed jobs / camera teleports
	// from turning into a single long main-thread frame.
	[Export(PropertyHint.Range, "1,16,1")] public int MaxReadyChunksPerFrame = 4;
	[Export(PropertyHint.Range, "1,16,1")] public int MaxUnloadSchedulesPerFrame = 2;
	[Export] public string SaveDirectory = ""; // "" => user://infinite_world_lab_cache
	[Export] public bool WipeStoreOnStart = false; // true — стенд стартует с чистого хранилища
	// Окно стриминга подбирается под видимый прямоугольник (в чанках): меньше окно —
	// меньше resident-клеток, памяти и работы по тайлам.
	[Export] public int MinWindowChunks = 3;
	[Export] public int MaxWindowChunks = 16;
	// Each operation is a bounded slice, not a whole 64×64 chunk.
	[Export(PropertyHint.Range, "1,8,1")] public int MaxChunkOpsPerFrame = 2;
	[Export(PropertyHint.Range, "128,4096,128")] public int MaxCellsPerPaintSlice = 1024;
	[Export(PropertyHint.Range, "128,4096,128")] public int MaxCellsErasedPerFrame = 1024;
	// Enter low detail below this many screen pixels per world cell; leave with hysteresis.
	[Export(PropertyHint.Range, "0.25,1.5,0.05")] public float LowDetailPixelsPerCellEnter = 0.80f;
	[Export(PropertyHint.Range, "0.5,2.0,0.05")] public float LowDetailPixelsPerCellExit = 1.15f;
	[Export(PropertyHint.Range, "1,16,1")] public int MaxLodChunksPerFrame = 2;

	private ChunkWindowPlanner _planner;
	private WorldChunkStore _store;
	private WorldChunkStreamer _streamer;
	private TileSet _tileSet;
	private TileMapLayer _groundLayer;
	private TileMapLayer _decorLayer;
	private TileMapLayer _markLayer;

	/// <summary>Слой чертежей (§29): план рисуется полупрозрачно, готовое — обычными слоями.</summary>
	private TileMapLayer _planLayer;
	// Far-zoom renderer. It is last in draw order; LOD sprites cover chunks until
	// their full-detail representation has been rebuilt after zooming back in.
	private Node2D _lodRoot;
	private readonly Dictionary<ChunkKey, Sprite2D> _lodSprites = new();
	private bool _lowDetailMode;
	private readonly AverageColor[] _groundCompositeColors = new AverageColor[11];
	private AverageColor _mountainOverlayColor;
	private AverageColor _wallOverlayColor;
	private AverageColor _cropOverlayColor;
	private AverageColor _stockpileOverlayColor;
	private AverageColor _workOverlayColor;
	// Горы и стены — свои слои с TileSet из сцен (terrain-автотайлинг, как на острове).
	private TileMapLayer _mountainLayer;
	private TileMapLayer _wallLayer;
	private TerrainTileLayer _mountainTerrain;
	private TerrainTileLayer _wallTerrain;
	private readonly Dictionary<ChunkKey, WorldChunk> _chunkData = new();

	/// <summary>Загруженные персистентные метки: чанк → kind → множество локальных индексов.</summary>
	private readonly Dictionary<ChunkKey, Dictionary<byte, HashSet<ushort>>> _blocks = new();
	private readonly HashSet<ChunkKey> _textureDirty = new();
	// Reused instead of allocating a new array each time a batch of chunks arrives.
	private readonly List<ChunkKey> _dirtyBuffer = new();
	// A just-loaded chunk has no overlay tiles to clear; skip redundant EraseCell calls.
	private readonly HashSet<ChunkKey> _freshPaint = new();
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
	private readonly List<Task> _unloadTasks = new();
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
			dir = System.IO.Path.Combine(userDir, $"seed_{WorldSeed:X16}");
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
		// A serialized scene may still override MaxConcurrency with a large value.
		// Keep worker pressure modest on low-end CPUs and avoid oversubscribing them.
		int workerLimit = Math.Min(2, Math.Max(1, Environment.ProcessorCount));
		int workerCount = Math.Clamp(MaxConcurrency, 1, workerLimit);
		_streamer = new WorldChunkStreamer(_planner, WorldSeed, GeneratorVersion, _store, workerCount);
		BuildTileLayers();
		_initialized = true;
	}

	/// <summary>Применить новое видимое окно, дренировать готовые чанки, запланировать выгрузки.</summary>
	public void Update(WorldRect visibleTileBounds)
	{
		if (!_initialized)
			return;
		_focusCellX = (visibleTileBounds.MinX + visibleTileBounds.MaxX) / 2;
		_focusCellY = (visibleTileBounds.MinY + visibleTileBounds.MaxY) / 2;
		UpdateDetailMode(visibleTileBounds);
		ApplyAdaptiveWindow(visibleTileBounds);
		_lastPlan = _streamer.Update(ClampVisibleToWindow(visibleTileBounds));

		int readyBudget = Math.Clamp(MaxReadyChunksPerFrame, 1, _readyBuffer.Length);
		int readyCount = _streamer.DrainReady(_readyBuffer.AsSpan(0, readyBudget), readyBudget);
		for (int i = 0; i < readyCount; i++)
			ApplyReady(_readyBuffer[i]);

		int failureCount = _streamer.DrainFailures(_failureBuffer.AsSpan(0, _failureBuffer.Length), _failureBuffer.Length);
		for (int i = 0; i < failureCount; i++)
			GD.PrintErr($"[World] chunk failure {_failureBuffer[i].Key}: {_failureBuffer[i].Message}");

		foreach (ChunkKey key in _lastPlan.Exited)
		{
			if (_streamer.IsResident(key) && !_streamer.IsDesired(key) && !_exitScheduled.Contains(key))
			{
				_exitQueue.Enqueue(key);
				_exitScheduled.Add(key);
			}
		}

		ProcessExits();
		RepaintDirty();
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
		WorldDeltaKind.StockpilePlan
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

			_unloadTasks.Add(task);
			RemoveChunkTiles(key);
			_exitQueue.Dequeue();
			_exitScheduled.Remove(key);
			scheduleBudget--;
		}

		for (int i = _unloadTasks.Count - 1; i >= 0; i--)
		{
			Task t = _unloadTasks[i];
			if (!t.IsCompleted)
				continue;
			if (t.IsFaulted)
				GD.PrintErr($"[World] unload task failed: {t.Exception?.GetBaseException().Message}");
			_unloadTasks.RemoveAt(i);
		}
	}

	private void RemoveChunkTiles(ChunkKey key)
	{
		// Low-detail mode has already cleared every detailed TileMap layer.
		if (_lowDetailMode)
			RemoveLodChunk(key);
		else
			MarkTerrainForUnload(key);

		long originX = key.X * (long)WorldCoordinates.ChunkSize;
		long originY = key.Y * (long)WorldCoordinates.ChunkSize;
		if (!_lowDetailMode && FitsTileMap(originX, originY))
		{
			_blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> oldMap);
			ScheduleEraseChunk(key,
				CollectOverlayIndices(oldMap, plans: false),
				CollectOverlayIndices(oldMap, plans: true));
		}

		_chunkData.Remove(key);
		if (_blocks.Remove(key, out Dictionary<byte, HashSet<ushort>> map)
			&& map.TryGetValue(WallKind, out HashSet<ushort> walls))
			WallCount = Math.Max(0, WallCount - walls.Count);
		_textureDirty.Remove(key);
		_freshPaint.Remove(key);
		_paintJobs.Remove(key);

		if (_lowDetailMode)
			return;
		// Keep the old queue token if one exists; replacing the job is safe because the
		// queue resolves the latest state by key, and the new paint waits for erase.
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
			WorldFeatureGenerator.FeatureSchemaVersion, WorldSeed, GameTimeCheckpoint: 0.0);

	/// <summary>
	/// Поставить тайлы изменившимся чанкам и стереть выгруженные — с бюджетом на кадр
	/// (ближние к камере чанки первыми), чтобы телепорт/pan не давал шторм SetCell/EraseCell.
	/// </summary>
	private void RepaintDirty()
	{
		int operationBudget = Mathf.Max(1, MaxChunkOpsPerFrame);
		if (_lowDetailMode)
		{
			if (_textureDirty.Count > 0)
			{
				SelectNearestDirtyChunks(Math.Max(1, MaxLodChunksPerFrame));
				foreach (ChunkKey key in _dirtyBuffer)
				{
					if (!_textureDirty.Remove(key))
						continue;
					if (_chunkData.ContainsKey(key))
						PaintLodChunk(key);
					else
						RemoveLodChunk(key);
				}
			}
			return; // hidden TileMap and terrain layers do no per-cell work
		}

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
					RemoveLodChunk(key);
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
		{
			RemoveLodChunk(key);
			return;
		}
		_blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> map);
		_paintJobs[key] = new ChunkPaintJob(key, chunk, new BlockLookup(map),
			originX, originY, clearEmptyLayers);
		if (_paintQueued.Add(key))
			_paintQueue.Enqueue(key);
	}

	private void ProcessPaintSlices(int maxSlices)
	{
		int slices = Math.Max(1, maxSlices);
		int maxCells = Math.Clamp(MaxCellsPerPaintSlice, 128, WorldCoordinates.ChunkSize * WorldCoordinates.ChunkSize);
		while (slices > 0 && _paintQueue.Count > 0)
		{
			ChunkKey key = _paintQueue.Dequeue();
			_paintQueued.Remove(key);
			if (!_paintJobs.TryGetValue(key, out ChunkPaintJob job))
				continue; // stale queue token after unload or LOD transition
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
				RemoveLodChunk(key);
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
				RemoveLodChunk(key);
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
			if (!_lowDetailMode && _eraseJobs.ContainsKey(candidate))
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
		AddSingleTile("res://ui/assets/textures/icons/logistic_zone.png", SrcStockpile);
		AddSingleTile("res://ui/assets/textures/sprites/ProcessOfWork.png", SrcWork);
		AddAtlasTileSet("res://ui/assets/textures/construction items/wooden_wall.png", SrcWall);
		BuildCompositeGroundSource();
		BuildLodPalette();

		_groundLayer = new TileMapLayer { Name = "WorldGroundAndDecor", TileSet = _tileSet };
		_markLayer = new TileMapLayer { Name = "WorldMark", TileSet = _tileSet };
		AddChild(_groundLayer);
		AddChild(_markLayer);

		// Горы и стены выглядят как в игре: TileSet берём из сцен (там terrain-разметка),
		// и соединения подбирает движок, а не мы вручную атласом.
		TileSet mountainTiles = MapRenderer.GetMountainTerrainTileSet();
		if (mountainTiles != null)
		{
			_mountainLayer = new TileMapLayer { Name = "WorldMountain", TileSet = mountainTiles };
			AddChild(_mountainLayer);
			_mountainTerrain = new TerrainTileLayer(_mountainLayer, MountainTerrainSetId, MountainTerrainId);
		}
		TileSet wallTiles = MapRenderer.GetWallTerrainTileSet();
		if (wallTiles != null)
		{
			_wallLayer = new TileMapLayer { Name = "WorldWall", TileSet = wallTiles };
			AddChild(_wallLayer);
			_wallTerrain = new TerrainTileLayer(_wallLayer, WallTerrainSetId, WallTerrainId);
		}

		// §29: чертежи (планы) — своим слоем и полупрозрачно, чтобы отличать от готового.
		_planLayer = new TileMapLayer { Name = "WorldPlan", TileSet = _tileSet };
		_planLayer.Modulate = new Color(1f, 1f, 1f, PlanAlpha);
		AddChild(_planLayer);

		// Last child so that its averaged LOD image can temporarily cover the whole
		// chunk while high-detail cells are being recreated after a zoom transition.
		_lodRoot = new Node2D { Name = "WorldFarLod" };
		AddChild(_lodRoot);
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

	private readonly struct AverageColor
	{
		// RGB is premultiplied by alpha, which makes blending layer averages cheap.
		public readonly float R;
		public readonly float G;
		public readonly float B;
		public readonly float A;

		public AverageColor(float r, float g, float b, float a)
		{
			R = r; G = g; B = b; A = a;
		}

		public static AverageColor FromColor(Color c)
			=> new(c.R * c.A, c.G * c.A, c.B * c.A, c.A);

		public Color AsOpaque()
			=> new(Mathf.Clamp(R, 0f, 1f), Mathf.Clamp(G, 0f, 1f), Mathf.Clamp(B, 0f, 1f), 1f);
	}

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
			GD.PrintErr($"[World] LOD/composite source tile unavailable: {path} [{atlasX},{atlasY}]");
			return missing;
		}
		Image tile = image.GetRegion(new Rect2I(x, y, TilePx, TilePx));
		if (tile.GetFormat() != Image.Format.Rgba8)
			tile.Convert(Image.Format.Rgba8);
		return tile;
	}

	private static void BlitTile(Image target, Image tile, int atlasX)
		=> target.BlitRect(tile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(atlasX * TilePx, 0));

	private void BuildCompositeGroundSource()
	{
		// The original ground + transparent decor layers are composited pixel-perfectly
		// into 11 atlas tiles: grass, water, fallback mountain, 4 trees, 4 stone variants.
		// This removes up to two TileMap calls per cell without changing close-up pixels.
		var atlas = Image.CreateEmpty(TilePx * 11, TilePx, false, Image.Format.Rgba8);
		Image grass = LoadTileImage("res://ui/assets/textures/tiles/grass.png", fallback: new Color(0.2f, 0.55f, 0.2f));
		Image water = LoadTileImage("res://ui/assets/textures/tiles/water.png", fallback: new Color(0.1f, 0.3f, 0.8f));
		Image mountain = LoadTileImage("res://ui/assets/textures/tiles/mountains.png", 3, 3, new Color(0.45f, 0.45f, 0.45f));
		BlitTile(atlas, grass, CompositeGrass);
		BlitTile(atlas, water, CompositeWater);
		BlitTile(atlas, mountain, CompositeMountain);
		_groundCompositeColors[CompositeGrass] = SampleImageTile(grass, 0, 0);
		_groundCompositeColors[CompositeWater] = SampleImageTile(water, 0, 0);
		_groundCompositeColors[CompositeMountain] = SampleImageTile(mountain, 0, 0);

		for (int v = 0; v < TexTrees.Length; v++)
		{
			Image tree = LoadTileImage(TexTrees[v], fallback: new Color(0.1f, 0.35f, 0.1f, 1f));
			int index = CompositeTree0 + v;
			BlitTile(atlas, grass, index);
			atlas.BlendRect(tree, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(index * TilePx, 0));
			_groundCompositeColors[index] = SampleImageTile(atlas, index, 0);
		}

		Image stone = LoadTileImage("res://ui/assets/textures/objects/stone.png", 0, 0,
			new Color(0.45f, 0.45f, 0.45f, 1f));
		// Load each stone atlas quadrant separately to preserve the exact chosen variant.
		for (int variant = 0; variant < 4; variant++)
		{
			Image stoneTile = variant == 0 ? stone : LoadTileImage(
				"res://ui/assets/textures/objects/stone.png", variant & 1, (variant >> 1) & 1,
				new Color(0.45f, 0.45f, 0.45f, 1f));
			int index = CompositeStone0 + variant;
			BlitTile(atlas, grass, index);
			atlas.BlendRect(stoneTile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(index * TilePx, 0));
			_groundCompositeColors[index] = SampleImageTile(atlas, index, 0);
		}

		var source = new TileSetAtlasSource
		{
			Texture = ImageTexture.CreateFromImage(atlas),
			TextureRegionSize = new Vector2I(TilePx, TilePx)
		};
		for (int i = 0; i < 11; i++)
			source.CreateTile(new Vector2I(i, 0));
		_tileSet.AddSource(source, SrcCompositeGround);
	}

	private void BuildLodPalette()
	{
		_mountainOverlayColor = SampleImageTile(
			LoadTileImage("res://ui/assets/textures/tiles/mountains.png", 3, 3, new Color(0.45f, 0.45f, 0.45f)), 0, 0);
		_wallOverlayColor = SampleImageTile(
			LoadTileImage("res://ui/assets/textures/construction items/wooden_wall.png", 0, 0, new Color(0.4f, 0.25f, 0.1f)), 0, 0);
		_cropOverlayColor = SampleImageTile(
			LoadTileImage("res://ui/assets/textures/food/corn.png", fallback: new Color(0.7f, 0.65f, 0.1f)), 0, 0);
		_stockpileOverlayColor = SampleImageTile(
			LoadTileImage("res://ui/assets/textures/icons/logistic_zone.png", fallback: new Color(0.65f, 0.5f, 0.25f)), 0, 0);
		_workOverlayColor = SampleImageTile(
			LoadTileImage("res://ui/assets/textures/sprites/ProcessOfWork.png", fallback: new Color(0.7f, 0.7f, 0.7f)), 0, 0);
	}

	private static AverageColor SampleImageTile(Image image, int atlasX, int atlasY)
	{
		int x0 = atlasX * TilePx;
		int y0 = atlasY * TilePx;
		int width = Math.Min(TilePx, image.GetWidth() - x0);
		int height = Math.Min(TilePx, image.GetHeight() - y0);
		if (width <= 0 || height <= 0)
			return AverageColor.FromColor(new Color(0.5f, 0.5f, 0.5f, 1f));

		const int samples = 12;
		double r = 0, g = 0, b = 0, a = 0;
		int count = samples * samples;
		for (int sy = 0; sy < samples; sy++)
		{
			int py = y0 + Math.Min(height - 1, (sy * 2 + 1) * height / (samples * 2));
			for (int sx = 0; sx < samples; sx++)
			{
				int px = x0 + Math.Min(width - 1, (sx * 2 + 1) * width / (samples * 2));
				Color c = image.GetPixel(px, py);
				r += c.R * c.A;
				g += c.G * c.A;
				b += c.B * c.A;
				a += c.A;
			}
		}
		return new AverageColor((float)(r / count), (float)(g / count), (float)(b / count), (float)(a / count));
	}

	private void UpdateDetailMode(WorldRect visible)
	{
		Vector2 viewport = GetViewportRect().Size;
		double cellsWide = Math.Max(1.0, visible.MaxX - visible.MinX);
		double cellsHigh = Math.Max(1.0, visible.MaxY - visible.MinY);
		if (viewport.X <= 0f || viewport.Y <= 0f)
			return;
		float pixelsPerCell = (float)Math.Min(viewport.X / cellsWide, viewport.Y / cellsHigh);
		float enter = Math.Clamp(LowDetailPixelsPerCellEnter, 0.25f, 1.5f);
		float exit = Math.Max(enter + 0.1f, Math.Clamp(LowDetailPixelsPerCellExit, 0.5f, 2.0f));
		if (!_lowDetailMode && pixelsPerCell < enter)
			SetLowDetailMode(true);
		else if (_lowDetailMode && pixelsPerCell > exit)
			SetLowDetailMode(false);
	}

	private void SetLowDetailMode(bool enabled)
	{
		if (_lowDetailMode == enabled)
			return;
		_lowDetailMode = enabled;

		SetDetailedLayersVisible(!enabled);
		if (enabled)
		{
			// Drop all detailed cells in one engine call per layer. This is much cheaper
			// than issuing 4K-16K EraseCell calls for every resident chunk on far zoom.
			_groundLayer?.Clear();
			_decorLayer?.Clear();
			_markLayer?.Clear();
			_planLayer?.Clear();
			_mountainLayer?.Clear();
			_wallLayer?.Clear();
			_eraseQueue.Clear();
			_eraseScheduled.Clear();
			_eraseJobs.Clear();
			_paintQueue.Clear();
			_paintQueued.Clear();
			_paintJobs.Clear();
			_textureDirty.Clear();
			_freshPaint.Clear();
			foreach (ChunkKey key in _chunkData.Keys)
				_textureDirty.Add(key);
		}
		else
		{
			_eraseQueue.Clear();
			_eraseScheduled.Clear();
			_eraseJobs.Clear();
			_paintQueue.Clear();
			_paintQueued.Clear();
			_paintJobs.Clear();
			_textureDirty.Clear();
			_freshPaint.Clear();
			foreach (ChunkKey key in _chunkData.Keys)
			{
				_textureDirty.Add(key);
				_freshPaint.Add(key); // all detailed layers were cleared above
			}
			// Existing LOD sprites remain on top until each full-detail chunk is ready.
		}
	}

	private void SetDetailedLayersVisible(bool visible)
	{
		if (_groundLayer != null) _groundLayer.Visible = visible;
		if (_decorLayer != null) _decorLayer.Visible = visible;
		if (_markLayer != null) _markLayer.Visible = visible;
		if (_planLayer != null) _planLayer.Visible = visible;
		if (_mountainLayer != null) _mountainLayer.Visible = visible;
		if (_wallLayer != null) _wallLayer.Visible = visible;
	}

	private int GetCompositeGroundIndex(long cellX, long cellY, int localIndex, GeneratedCell cell, BlockLookup kinds)
	{
		if (cell.Terrain == BaseTerrainKind.Water)
			return CompositeWater;
		if (cell.Terrain == BaseTerrainKind.Mountain && _mountainLayer == null)
			return CompositeMountain;

		if (cell.Terrain == BaseTerrainKind.Grass)
		{
			int variant = (int)((cellX * 73856093L ^ cellY * 19349663L) & 3L);
			bool isCut = BlockLookup.Contains(kinds.CutTree, localIndex);
			bool isMined = BlockLookup.Contains(kinds.MinedStone, localIndex);
			if (!isCut && cell.ForestQ16 >= ForestDecorMin)
				return CompositeTree0 + variant;
			if (!isMined && cell.StoneQ16 >= StoneDecorMin)
				return CompositeStone0 + variant;
		}
		return CompositeGrass;
	}

	private void PaintLodChunk(ChunkKey key)
	{
		if (!_chunkData.TryGetValue(key, out WorldChunk chunk))
		{
			RemoveLodChunk(key);
			return;
		}
		long originX = key.X * (long)WorldCoordinates.ChunkSize;
		long originY = key.Y * (long)WorldCoordinates.ChunkSize;
		if (!FitsTileMap(originX, originY))
		{
			RemoveLodChunk(key);
			return;
		}

		_blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> kinds);
		var lookup = new BlockLookup(kinds);
		int side = WorldCoordinates.ChunkSize;
		byte[] rgba = new byte[side * side * 4];
		ReadOnlySpan<GeneratedCell> cells = chunk.Cells.Span;
		for (int y = 0; y < WorldCoordinates.ChunkSize; y++)
		{
			for (int x = 0; x < WorldCoordinates.ChunkSize; x++)
			{
				int localIndex = y * WorldCoordinates.ChunkSize + x;
				GeneratedCell cell = cells[localIndex];
				int groundIndex = GetCompositeGroundIndex(originX + x, originY + y, localIndex, cell, lookup);
				Color color = _groundCompositeColors[groundIndex].AsOpaque();

				// Normal marker layer is drawn before mountain/wall terrain layers.
				if (BlockLookup.Contains(kinds.Wall, localIndex))
				{
					if (_wallLayer == null)
						BlendAverage(ref color, _wallOverlayColor, 1f);
				}
				else if (BlockLookup.Contains(kinds.Crop, localIndex))
					BlendAverage(ref color, _cropOverlayColor, 1f);
				else if (BlockLookup.Contains(kinds.Stockpile, localIndex))
					BlendAverage(ref color, _stockpileOverlayColor, 1f);
				else if (BlockLookup.Contains(kinds.Building, localIndex))
					BlendAverage(ref color, _workOverlayColor, 1f);

				// Terrain layers are above marks in the original scene's draw order.
				if (_mountainLayer != null && cell.Terrain == BaseTerrainKind.Mountain)
					BlendAverage(ref color, _mountainOverlayColor, 1f);
				if (_wallLayer != null && BlockLookup.Contains(kinds.Wall, localIndex))
					BlendAverage(ref color, _wallOverlayColor, 1f);

				// Plans are the final layer and use the same 55% alpha as the detailed view.
				if (BlockLookup.Contains(kinds.WallPlan, localIndex))
					BlendAverage(ref color, _wallOverlayColor, PlanAlpha);
				else if (BlockLookup.Contains(kinds.CropPlan, localIndex))
					BlendAverage(ref color, _cropOverlayColor, PlanAlpha);
				else if (BlockLookup.Contains(kinds.StockpilePlan, localIndex))
					BlendAverage(ref color, _stockpileOverlayColor, PlanAlpha);
				else if (BlockLookup.Contains(kinds.BuildingPlan, localIndex))
					BlendAverage(ref color, _workOverlayColor, PlanAlpha);

				int pixel = localIndex * 4;
				rgba[pixel] = ToByte(color.R);
				rgba[pixel + 1] = ToByte(color.G);
				rgba[pixel + 2] = ToByte(color.B);
				rgba[pixel + 3] = 255;
			}
		}
		Image image = Image.CreateFromData(side, side, false, Image.Format.Rgba8, rgba);
		image.GenerateMipmaps();
		Texture2D texture = ImageTexture.CreateFromImage(image);
		if (!_lodSprites.TryGetValue(key, out Sprite2D sprite) || !GodotObject.IsInstanceValid(sprite))
		{
			sprite = new Sprite2D
			{
				Name = $"LOD_{key.X}_{key.Y}",
				Centered = false,
				Position = new Vector2((float)(originX * TilePx), (float)(originY * TilePx)),
				Scale = new Vector2(TilePx, TilePx),
				TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
				Texture = texture
			};
			_lodRoot.AddChild(sprite);
			_lodSprites[key] = sprite;
		}
		else
		{
			sprite.Texture = texture;
		}
	}

	private static byte ToByte(float value)
		=> (byte)Math.Clamp((int)MathF.Round(Mathf.Clamp(value, 0f, 1f) * 255f), 0, 255);

	private static void BlendAverage(ref Color destination, AverageColor source, float opacity)
	{
		float alpha = Mathf.Clamp(source.A * opacity, 0f, 1f);
		destination = new Color(
			Mathf.Clamp(source.R * opacity + destination.R * (1f - alpha), 0f, 1f),
			Mathf.Clamp(source.G * opacity + destination.G * (1f - alpha), 0f, 1f),
			Mathf.Clamp(source.B * opacity + destination.B * (1f - alpha), 0f, 1f),
			1f);
	}

	private void RemoveLodChunk(ChunkKey key)
	{
		if (!_lodSprites.Remove(key, out Sprite2D sprite))
			return;
		if (GodotObject.IsInstanceValid(sprite))
			sprite.QueueFree();
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
	}

	/// <summary>Кольцо 3×3 вокруг одной клетки — для одиночных меток (стена/её снос).</summary>
	private void MarkTerrainCellRing(long cellX, long cellY)
	{
		if (_mountainTerrain == null && _wallTerrain == null)
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
		LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
		return chunk.Cells.Span[local.Y * WorldCoordinates.ChunkSize + local.X].Terrain
			== BaseTerrainKind.Mountain;
	}

	private bool IsMountainCell(Vector2I cell) => IsMountainAt(cell.X, cell.Y);

	private bool IsWallCell(Vector2I cell) => HasBlock(WallKind, cell.X, cell.Y);

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
		if (_lowDetailMode || _paintJobs.ContainsKey(key))
		{
			_textureDirty.Add(key);
			return;
		}
		LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
		int localIndex = local.Y * WorldCoordinates.ChunkSize + local.X;
		_blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> kinds);
		PaintCell(cellX, cellY, localIndex, chunk.Cells.Span[localIndex], new BlockLookup(kinds), clearEmptyLayers: true);
		MarkTerrainCellRing(cellX, cellY);
	}

	/// <summary>Один тайл на клетку: земля + декор (лес/камни) + метки игрока.</summary>
	private void PaintCell(long cellX, long cellY, int localIndex, GeneratedCell cell,
		BlockLookup kinds, bool clearEmptyLayers)
	{
		var pos = new Vector2I((int)cellX, (int)cellY);
		int compositeIndex = GetCompositeGroundIndex(cellX, cellY, localIndex, cell, kinds);
		_groundLayer.SetCell(pos, SrcCompositeGround, new Vector2I(compositeIndex, 0));

		// A single ground tile now already contains grass/water/mountain fallback and
		// its tree/stone pixels, so no per-cell decor EraseCell/SetCell calls are needed.
		if (_mountainLayer != null && cell.Terrain == BaseTerrainKind.Mountain)
			_mountainTerrain.MarkDirty(pos);

		if (clearEmptyLayers)
		{
			_markLayer.EraseCell(pos);
			_planLayer?.EraseCell(pos);
		}
		if (BlockLookup.Contains(kinds.Wall, localIndex))
		{
			if (_wallLayer != null)
				_wallTerrain.MarkDirty(pos);
			else
				_markLayer.SetCell(pos, SrcWall, Vector2I.Zero);
		}
		else if (BlockLookup.Contains(kinds.Crop, localIndex))
			_markLayer.SetCell(pos, SrcCrop, Vector2I.Zero);
		else if (BlockLookup.Contains(kinds.Stockpile, localIndex))
			_markLayer.SetCell(pos, SrcStockpile, Vector2I.Zero);
		else if (BlockLookup.Contains(kinds.Building, localIndex))
			_markLayer.SetCell(pos, SrcWork, Vector2I.Zero);

		if (BlockLookup.Contains(kinds.WallPlan, localIndex))
			_planLayer?.SetCell(pos, SrcWall, Vector2I.Zero);
		else if (BlockLookup.Contains(kinds.CropPlan, localIndex))
			_planLayer?.SetCell(pos, SrcCrop, Vector2I.Zero);
		else if (BlockLookup.Contains(kinds.StockpilePlan, localIndex))
			_planLayer?.SetCell(pos, SrcStockpile, Vector2I.Zero);
		else if (BlockLookup.Contains(kinds.BuildingPlan, localIndex))
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
