using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.UI;
using Godot;

namespace Game.UI.Streaming;

/// <summary>
/// Главнопоточный Node2D-вид бесконечного мира: владеет планировщиком окна,
/// стримером чанков и файловым хранилищем; рисует каждый чанк как один Sprite2D
/// с текстурой 64×64 (1 пиксель = 1 клетка). Дополнительно ведёт персистентные
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
    [Export] public int MaxConcurrency = 4;
    [Export] public string SaveDirectory = ""; // "" => user://infinite_world_lab_cache
    [Export] public bool WipeStoreOnStart = false; // true — стенд стартует с чистого хранилища
    // Окно стриминга подбирается под видимый прямоугольник (в чанках): меньше окно —
    // меньше resident-клеток, памяти и работы по тайлам.
    [Export] public int MinWindowChunks = 3;
    [Export] public int MaxWindowChunks = 16;
    // Бюджет покраски/стирания чанков за кадр (1 чанк = 64×64 клетки × 3 слоя).
    [Export] public int MaxChunkOpsPerFrame = 3;

    private ChunkWindowPlanner _planner;
    private WorldChunkStore _store;
    private WorldChunkStreamer _streamer;
    private TileSet _tileSet;
    private TileMapLayer _groundLayer;
    private TileMapLayer _decorLayer;
    private TileMapLayer _markLayer;

    /// <summary>Слой чертежей (§29): план рисуется полупрозрачно, готовое — обычными слоями.</summary>
    private TileMapLayer _planLayer;
    // Горы и стены — свои слои с TileSet из сцен (terrain-автотайлинг, как на острове).
    private TileMapLayer _mountainLayer;
    private TileMapLayer _wallLayer;
    private TerrainTileLayer _mountainTerrain;
    private TerrainTileLayer _wallTerrain;
    private readonly Dictionary<ChunkKey, WorldChunk> _chunkData = new();

    /// <summary>Загруженные персистентные метки: чанк → kind → множество локальных индексов.</summary>
    private readonly Dictionary<ChunkKey, Dictionary<byte, HashSet<ushort>>> _blocks = new();
    private readonly HashSet<ChunkKey> _textureDirty = new();
    private readonly Queue<ChunkKey> _eraseQueue = new();
    private readonly HashSet<ChunkKey> _eraseScheduled = new();
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
        _streamer = new WorldChunkStreamer(_planner, WorldSeed, GeneratorVersion, _store, MaxConcurrency);
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
        ApplyAdaptiveWindow(visibleTileBounds);
        _lastPlan = _streamer.Update(ClampVisibleToWindow(visibleTileBounds));

        int readyCount = _streamer.DrainReady(_readyBuffer.AsSpan(0, _readyBuffer.Length), _readyBuffer.Length);
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
                    into.Add(new WorldPlan(planKind,
                        pair.Key.X * (long)WorldCoordinates.ChunkSize + localX,
                        pair.Key.Y * (long)WorldCoordinates.ChunkSize + localY));
                }
            }
        }
        if (into.Count > 1)
        {
            _planComparer ??= new PlanFocusComparer(this);
            into.Sort(_planComparer);
        }
        if (into.Count > max)
            into.RemoveRange(max, into.Count - max);
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

        private static long Distance(WorldPlan plan, long focusX, long focusY)
        {
            long dx = plan.CellX - focusX;
            long dy = plan.CellY - focusY;
            return dx * dx + dy * dy;
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

    /// <summary>Загружен ли чанк в память (данные и спрайт готовы).</summary>
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

        _chunkData[key] = result.Chunk;
        // Тайлы ставит RepaintDirty (главный поток) — здесь только помечаем чанк.
        _textureDirty.Add(key);
    }

    private void ProcessExits()
    {
        while (_exitQueue.Count > 0)
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
        // Пока данные ещё в памяти — помечаем горы/стены чанка к стиранию:
        // после удаления предикат exists вернёт false и движок сотрёт тайлы.
        MarkTerrainForUnload(key);
        _chunkData.Remove(key);
        if (_blocks.Remove(key, out Dictionary<byte, HashSet<ushort>> map)
            && map.TryGetValue(WallKind, out HashSet<ushort> walls))
            WallCount -= walls.Count;
        _textureDirty.Remove(key);
        // Стирание тайлов — по бюджету в RepaintDirty, чтобы выгрузка не фризила кадр.
        if (_eraseScheduled.Add(key))
            _eraseQueue.Enqueue(key);
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
        int budget = Mathf.Max(1, MaxChunkOpsPerFrame);
        while (budget > 0 && _eraseQueue.Count > 0)
        {
            ChunkKey key = _eraseQueue.Dequeue();
            _eraseScheduled.Remove(key);
            EraseChunkTiles(key);
            budget--;
        }
        if (_textureDirty.Count > 0)
        {
            ChunkKey[] keys = new ChunkKey[_textureDirty.Count];
            _textureDirty.CopyTo(keys);
            Array.Sort(keys, CompareByDistanceToFocus);
            foreach (ChunkKey key in keys)
            {
                if (budget <= 0)
                    break;
                if (!_textureDirty.Remove(key))
                    continue;
                if (_chunkData.ContainsKey(key))
                    PaintChunk(key);
                else
                    EraseChunkTiles(key);
                budget--;
            }
        }

        // Terrain-слои (горы/стены): набор ограничен обработанными чанками,
        // соединения подбирает движок. Пересчёт каждый кадр — копейки.
        _mountainTerrain?.Flush(IsMountainCell);
        _wallTerrain?.Flush(IsWallCell);
    }

    /// <summary>Пометить горы/стены выгружаемого чанка к стиранию (данные ещё в памяти).</summary>
    private void MarkTerrainForUnload(ChunkKey key)
    {
        if (_mountainTerrain == null && _wallTerrain == null)
            return;
        long originX = key.X * (long)WorldCoordinates.ChunkSize;
        long originY = key.Y * (long)WorldCoordinates.ChunkSize;
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
    }

    /// <summary>Сортировка чанков по удалённости от центра видимого окна (клетки).</summary>
    private int CompareByDistanceToFocus(ChunkKey a, ChunkKey b)
    {
        long half = WorldCoordinates.ChunkSize / 2;
        long ax = a.X * WorldCoordinates.ChunkSize + half - _focusCellX;
        long ay = a.Y * WorldCoordinates.ChunkSize + half - _focusCellY;
        long bx = b.X * WorldCoordinates.ChunkSize + half - _focusCellX;
        long by = b.Y * WorldCoordinates.ChunkSize + half - _focusCellY;
        long da = ax * ax + ay * ay;
        long db = bx * bx + by * by;
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

    private const int SrcGrass = 0;    private const int SrcWater = 1;
    private const int SrcMountain = 2;
    private const int SrcTree0 = 3;      // 3..6 — tree.png, tree_1..3 (как на острове)
    private const int SrcStone = 7;      // атлас 2×2: квадранты stone.png
    private const int SrcCrop = 8;       // food/corn.png
    private const int SrcStockpile = 9;  // icons/logistic_zone.png
    private const int SrcWork = 10;      // sprites/ProcessOfWork.png
    private const int SrcWall = 11;      // construction items/wooden_wall.png (7×7)

    private static readonly Vector2I MountainAtlas = new(3, 3);

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
        AddSingleTile("res://ui/assets/textures/tiles/grass.png", SrcGrass);
        AddSingleTile("res://ui/assets/textures/tiles/water.png", SrcWater);
        AddAtlasTileSet("res://ui/assets/textures/tiles/mountains.png", SrcMountain);
        for (int v = 0; v < TexTrees.Length; v++)
            AddSingleTile(TexTrees[v], SrcTree0 + v);
        AddAtlasTileSet("res://ui/assets/textures/objects/stone.png", SrcStone);
        AddSingleTile("res://ui/assets/textures/food/corn.png", SrcCrop);
        AddSingleTile("res://ui/assets/textures/icons/logistic_zone.png", SrcStockpile);
        AddSingleTile("res://ui/assets/textures/sprites/ProcessOfWork.png", SrcWork);
        AddAtlasTileSet("res://ui/assets/textures/construction items/wooden_wall.png", SrcWall);

        _groundLayer = new TileMapLayer { Name = "WorldGround", TileSet = _tileSet };
        _decorLayer = new TileMapLayer { Name = "WorldDecor", TileSet = _tileSet };
        _markLayer = new TileMapLayer { Name = "WorldMark", TileSet = _tileSet };
        AddChild(_groundLayer);
        AddChild(_decorLayer);
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

    /// <summary>Поставить тайлы всем клеткам чанка.</summary>
    private void PaintChunk(ChunkKey key)
    {
        if (!_chunkData.TryGetValue(key, out WorldChunk chunk))
            return;
        _blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> kinds);
        long originX = key.X * (long)WorldCoordinates.ChunkSize;
        long originY = key.Y * (long)WorldCoordinates.ChunkSize;
        if (!FitsTileMap(originX, originY))
            return;
        ReadOnlySpan<GeneratedCell> cells = chunk.Cells.Span;
        for (int cy = 0; cy < WorldCoordinates.ChunkSize; cy++)
        {
            for (int cx = 0; cx < WorldCoordinates.ChunkSize; cx++)
            {
                int localIndex = cy * WorldCoordinates.ChunkSize + cx;
                PaintCell(originX + cx, originY + cy, localIndex, cells[localIndex], kinds);
            }
        }
        // Кольцо соседей в 1 клетку: на кромке чанка соединения гор/стен надо пересчитать.
        MarkTerrainRing(originX, originY);
    }

    /// <summary>
    /// Пометить в terrain-слоях горы/стены прямоугольника чанка, расширенного на 1 клетку
    /// (соседние чанки тоже). Без этого тайлы на кромке остаются с прежней разметкой.
    /// </summary>
    private void MarkTerrainRing(long originX, long originY)
    {
        if (_mountainTerrain == null && _wallTerrain == null)
            return;
        if (!FitsTileMap(originX, originY, 1))
            return;
        long maxX = originX + WorldCoordinates.ChunkSize;
        long maxY = originY + WorldCoordinates.ChunkSize;
        for (long y = originY - 1; y <= maxY; y++)
        {
            for (long x = originX - 1; x <= maxX; x++)
            {
                if (_mountainTerrain != null && IsMountainAt(x, y))
                    _mountainTerrain.MarkDirty(new Vector2I((int)x, (int)y));
                if (_wallTerrain != null && HasBlock(WallKind, x, y))
                    _wallTerrain.MarkDirty(new Vector2I((int)x, (int)y));
            }
        }
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
                if (_mountainTerrain != null && IsMountainAt(x, y))
                    _mountainTerrain.MarkDirty(new Vector2I((int)x, (int)y));
                if (_wallTerrain != null && HasBlock(WallKind, x, y))
                    _wallTerrain.MarkDirty(new Vector2I((int)x, (int)y));
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
        LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
        int localIndex = local.Y * WorldCoordinates.ChunkSize + local.X;
        _blocks.TryGetValue(key, out Dictionary<byte, HashSet<ushort>> kinds);
        PaintCell(cellX, cellY, localIndex, chunk.Cells.Span[localIndex], kinds);
        MarkTerrainCellRing(cellX, cellY);
    }

    /// <summary>Один тайл на клетку: земля + декор (лес/камни) + метки игрока.</summary>
    private void PaintCell(long cellX, long cellY, int localIndex, GeneratedCell cell,
        Dictionary<byte, HashSet<ushort>> kinds)
    {
        var pos = new Vector2I((int)cellX, (int)cellY);

        int groundSource;
        Vector2I groundAtlas;
        switch (cell.Terrain)
        {
            case BaseTerrainKind.Water:
                groundSource = SrcWater;
                groundAtlas = Vector2I.Zero;
                break;
            case BaseTerrainKind.Mountain:
                if (_mountainLayer != null)
                {
                    // Земля под горой — трава, гору рисует terrain-слой (автотайлинг сцены).
                    groundSource = SrcGrass;
                    groundAtlas = Vector2I.Zero;
                    _mountainTerrain.MarkDirty(pos);
                }
                else
                {
                    groundSource = SrcMountain;
                    groundAtlas = MountainAtlas;
                }
                break;
            default:
                groundSource = SrcGrass;
                groundAtlas = Vector2I.Zero;
                break;
        }
        _groundLayer.SetCell(pos, groundSource, groundAtlas);

        // Вариант декора 0..3 — детерминированно по мировым координатам.
        int variant = (int)((cellX * 73856093L ^ cellY * 19349663L) & 3L);
        bool isGrass = cell.Terrain == BaseTerrainKind.Grass;
        bool isCut = HasKind(kinds, WorldDeltaKind.CutTree, localIndex);
        bool isMined = HasKind(kinds, WorldDeltaKind.MinedStone, localIndex);

        _decorLayer.EraseCell(pos);
        if (isGrass && !isCut && cell.ForestQ16 >= ForestDecorMin)
        {
            _decorLayer.SetCell(pos, SrcTree0 + variant, Vector2I.Zero);
        }
        else if (isGrass && !isMined && cell.StoneQ16 >= StoneDecorMin)
        {
            _decorLayer.SetCell(pos, SrcStone, new Vector2I(variant & 1, (variant >> 1) & 1));
        }

        _markLayer.EraseCell(pos);
        _planLayer?.EraseCell(pos);
        if (HasKind(kinds, WallKind, localIndex))
        {
            // Стены: соединения подбирает движок на terrain-слое (как на острове).
            if (_wallLayer != null)
                _wallTerrain.MarkDirty(pos);
            else
                _markLayer.SetCell(pos, SrcWall, Vector2I.Zero);
        }
        else if (HasKind(kinds, WorldDeltaKind.Crop, localIndex))
            _markLayer.SetCell(pos, SrcCrop, Vector2I.Zero);
        else if (HasKind(kinds, WorldDeltaKind.Stockpile, localIndex))
            _markLayer.SetCell(pos, SrcStockpile, Vector2I.Zero);
        else if (HasKind(kinds, WorldDeltaKind.Building, localIndex))
            _markLayer.SetCell(pos, SrcWork, Vector2I.Zero);

        // §29: чертежи — в слой плана (план и готовое на одной клетке не встречаются).
        if (HasKind(kinds, WorldDeltaKind.WallPlan, localIndex))
            _planLayer?.SetCell(pos, SrcWall, Vector2I.Zero);
        else if (HasKind(kinds, WorldDeltaKind.CropPlan, localIndex))
            _planLayer?.SetCell(pos, SrcCrop, Vector2I.Zero);
        else if (HasKind(kinds, WorldDeltaKind.StockpilePlan, localIndex))
            _planLayer?.SetCell(pos, SrcStockpile, Vector2I.Zero);
        else if (HasKind(kinds, WorldDeltaKind.BuildingPlan, localIndex))
            _planLayer?.SetCell(pos, SrcWork, Vector2I.Zero);
    }

    private static bool HasKind(Dictionary<byte, HashSet<ushort>> kinds, byte kind, int localIndex)
        => kinds != null && kinds.TryGetValue(kind, out HashSet<ushort> set) && set.Contains((ushort)localIndex);

    /// <summary>
    /// Клетки чанка представимы в int32: TileMapLayer адресует клетки int32, и за этим
    /// диапазоном рисование/стирание «завернулось» бы и стёрло чужие тайлы. Такой чанк
    /// просто не рисуем (мир дальше ~±2.1 млрд клеток не отображается — предел TileMapLayer).
    /// </summary>
    private static bool FitsTileMap(long originX, long originY, long margin = 0)
        => originX - margin >= int.MinValue && originY - margin >= int.MinValue
            && originX + WorldCoordinates.ChunkSize - 1 + margin <= int.MaxValue
            && originY + WorldCoordinates.ChunkSize - 1 + margin <= int.MaxValue;

    /// <summary>Убрать тайлы всех клеток чанка (выгрузка).</summary>
    private void EraseChunkTiles(ChunkKey key)
    {
        if (_groundLayer == null)
            return;
        long originX = key.X * (long)WorldCoordinates.ChunkSize;
        long originY = key.Y * (long)WorldCoordinates.ChunkSize;
        if (!FitsTileMap(originX, originY))
            return;
        for (int cy = 0; cy < WorldCoordinates.ChunkSize; cy++)
        {
            for (int cx = 0; cx < WorldCoordinates.ChunkSize; cx++)
            {
                var pos = new Vector2I((int)(originX + cx), (int)(originY + cy));
                _groundLayer.EraseCell(pos);
                _decorLayer.EraseCell(pos);
                _markLayer.EraseCell(pos);
                _planLayer?.EraseCell(pos);
            }
        }
    }
}
