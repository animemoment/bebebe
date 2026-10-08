using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Неизменяемый результат планирования одного 1024×1024 окна.
/// Все списки упорядочены стабильно: сначала Y чанка, затем X.
/// </summary>
public sealed class ChunkWindowPlan
{
    internal ChunkWindowPlan(
        WorldRect activeBounds,
        ChunkKey minChunk,
        ReadOnlyCollection<ChunkKey> desired,
        ReadOnlyCollection<ChunkKey> entered,
        ReadOnlyCollection<ChunkKey> exited)
    {
        ActiveBounds = activeBounds;
        MinChunk = minChunk;
        Desired = desired;
        Entered = entered;
        Exited = exited;
    }

    /// <summary>Глобальные границы выровненного активного окна, max exclusive.</summary>
    public WorldRect ActiveBounds { get; }

    /// <summary>Ключ северо-западного (минимального X/Y) чанка окна.</summary>
    public ChunkKey MinChunk { get; }

    /// <summary>Ровно 16×16 ключей, отсортированных по Y, затем по X.</summary>
    public IReadOnlyList<ChunkKey> Desired { get; }

    /// <summary>Ключи, добавленные относительно предыдущего Update.</summary>
    public IReadOnlyList<ChunkKey> Entered { get; }

    /// <summary>Ключи, удалённые относительно предыдущего Update.</summary>
    public IReadOnlyList<ChunkKey> Exited { get; }
}

/// <summary>
/// Планирует фиксированное выровненное окно чанков 1024×1024 клеток вокруг
/// видимого прямоугольника. Планер не генерирует и не хранит данные клеток.
/// </summary>
public sealed class ChunkWindowPlanner
{
    public const int ActiveChunkSide = 16;
    public const int ActiveTileSide = ActiveChunkSide * WorldCoordinates.ChunkSize;
    public const long MinimumMarginTiles = 50;

    /// <summary>Жёсткий предел стороны окна (чанков): 256² = 65 536 чанков, дальше — OOM.</summary>
    public const int MaxWindowSide = 256;

    private static readonly ReadOnlyCollection<ChunkKey> EmptyKeys = Array.AsReadOnly(Array.Empty<ChunkKey>());

    private readonly long _marginTiles;
    private readonly HashSet<ChunkKey> _currentSet = new();
    private readonly HashSet<ChunkKey> _nextSet = new();
    private readonly List<ChunkKey> _enteredScratch = new(256);
    private readonly List<ChunkKey> _exitedScratch = new(256);
    private int _activeChunkSide = ActiveChunkSide;
    private long _activeTileSide = ActiveTileSide;
    private ChunkKey[] _currentKeys = Array.Empty<ChunkKey>();
    private ReadOnlyCollection<ChunkKey> _currentReadOnly = EmptyKeys;
    private ChunkKey _currentMinChunk;
    private bool _hasCurrent;

    /// <param name="marginTiles">Запас от видимого края; должен быть не меньше 50 клеток.</param>
    public ChunkWindowPlanner(long marginTiles = MinimumMarginTiles)
    {
        if (marginTiles < MinimumMarginTiles)
            throw new ArgumentOutOfRangeException(nameof(marginTiles), marginTiles,
                $"Запас должен быть не меньше {MinimumMarginTiles} клеток.");
        _marginTiles = marginTiles;
    }

    public long MarginTiles => _marginTiles;

    /// <summary>Текущая сторона активного окна в чанках (по умолчанию 16 — стендовое окно).</summary>
    public int WindowChunks => _activeChunkSide;

    /// <summary>
    /// Сменить сторону активного окна (в чанках). После смены набор пересчитается на следующем
    /// Update. Меньше чанков — меньше resident-клеток, памяти и работы по тайлам.
    /// </summary>
    public void SetWindowSide(int chunks)
    {
        if (chunks < 1)
            throw new ArgumentOutOfRangeException(nameof(chunks), chunks, "Сторона окна — минимум 1 чанк.");
        // Верхний предел: больше — это уже не «окно», а OOM; и счётчик ключей
        // (_activeChunkSide²) обязан влезать в int без переполнения.
        if (chunks > MaxWindowSide)
            chunks = MaxWindowSide;
        if (chunks == _activeChunkSide)
            return;
        _activeChunkSide = chunks;
        _activeTileSide = checked((long)chunks * WorldCoordinates.ChunkSize);
        // Сдвигаем «текущий» ключ-якорь, чтобы следующее Update пересчитало набор,
        // но старый список ключей сохранился — иначе Streamer не получит exited-чанки.
        _currentMinChunk = new ChunkKey(long.MinValue, long.MinValue);
    }

    /// <summary>
    /// Обновить desired window. Видимый прямоугольник задан глобальными TILE coords,
    /// с включительной Min и исключительной Max-границей. Обновление атомарно с точки
    /// зрения состояния планера: при ошибке прежний desired set остаётся нетронутым.
    /// </summary>
    public ChunkWindowPlan Update(WorldRect visibleTileBounds)
    {
        ValidateVisibleBounds(visibleTileBounds);

        long expandedMinX = checked(visibleTileBounds.MinX - _marginTiles);
        long expandedMinY = checked(visibleTileBounds.MinY - _marginTiles);
        long expandedMaxX = checked(visibleTileBounds.MaxX + _marginTiles);
        long expandedMaxY = checked(visibleTileBounds.MaxY + _marginTiles);

        long expandedWidth = checked(expandedMaxX - expandedMinX);
        long expandedHeight = checked(expandedMaxY - expandedMinY);
        if (expandedWidth > _activeTileSide || expandedHeight > _activeTileSide)
            throw new ArgumentOutOfRangeException(nameof(visibleTileBounds),
                $"Видимая область с запасом должна помещаться в окно {_activeTileSide}×{_activeTileSide}; " +
                $"получено {expandedWidth}×{expandedHeight} клеток.");

        long firstChunkX = WorldCoordinates.FloorDiv(expandedMinX, WorldCoordinates.ChunkSize);
        long firstChunkY = WorldCoordinates.FloorDiv(expandedMinY, WorldCoordinates.ChunkSize);
        long lastChunkX = WorldCoordinates.FloorDiv(checked(expandedMaxX - 1), WorldCoordinates.ChunkSize);
        long lastChunkY = WorldCoordinates.FloorDiv(checked(expandedMaxY - 1), WorldCoordinates.ChunkSize);

        long minStartX = Math.Max(checked(lastChunkX - (_activeChunkSide - 1)),
            WorldCoordinates.FloorDiv(long.MinValue, WorldCoordinates.ChunkSize));
        long minStartY = Math.Max(checked(lastChunkY - (_activeChunkSide - 1)),
            WorldCoordinates.FloorDiv(long.MinValue, WorldCoordinates.ChunkSize));
        long maxStartX = Math.Min(firstChunkX,
            WorldCoordinates.FloorDiv(checked(long.MaxValue - _activeTileSide), WorldCoordinates.ChunkSize));
        long maxStartY = Math.Min(firstChunkY,
            WorldCoordinates.FloorDiv(checked(long.MaxValue - _activeTileSide), WorldCoordinates.ChunkSize));

        if (minStartX > maxStartX || minStartY > maxStartY)
            throw new ArgumentOutOfRangeException(nameof(visibleTileBounds),
                "Видимая область с запасом не помещается ни в одно представимое выровненное окно 1024×1024.");

        long preferredStartX = PreferredStartChunk(visibleTileBounds.MinX, visibleTileBounds.MaxX);
        long preferredStartY = PreferredStartChunk(visibleTileBounds.MinY, visibleTileBounds.MaxY);
        long startX = Math.Clamp(preferredStartX, minStartX, maxStartX);
        long startY = Math.Clamp(preferredStartY, minStartY, maxStartY);
        var minChunk = new ChunkKey(startX, startY);

        // Одинаковая камера/видимая клеточная область не должна менять desired
        // set и не должна повторно выдавать события residency.
        if (_hasCurrent && minChunk == _currentMinChunk)
            return new ChunkWindowPlan(
                BuildActiveBounds(minChunk), minChunk, _currentReadOnly, EmptyKeys, EmptyKeys);

        ChunkKey[] nextKeys = BuildKeys(minChunk);
        // Контракт: Desired/Entered/Exited — неизменяемые снимки (потребитель/тесты держат
        // план и сравнивают его с прежним), поэтому массивы диффа по-прежнему свои.
        // Переиспользуем только внутренние буферы — это убирает List/HashSet и их рост.
        _nextSet.Clear();
        foreach (var key in nextKeys)
            _nextSet.Add(key);

        _enteredScratch.Clear();
        foreach (var key in nextKeys)
        {
            if (!_currentSet.Contains(key))
                _enteredScratch.Add(key);
        }

        _exitedScratch.Clear();
        foreach (var key in _currentKeys)
        {
            if (!_nextSet.Contains(key))
                _exitedScratch.Add(key);
        }

        // nextKeys и _currentKeys уже отсортированы в одном порядке, поэтому diff
        // тоже остаётся deterministic без зависимости от HashSet enumeration.
        var enteredReadOnly = Array.AsReadOnly(_enteredScratch.ToArray());
        var exitedReadOnly = Array.AsReadOnly(_exitedScratch.ToArray());
        var desiredReadOnly = Array.AsReadOnly(nextKeys);
        WorldRect activeBounds = BuildActiveBounds(minChunk);

        _currentSet.Clear();
        foreach (var key in nextKeys)
            _currentSet.Add(key);
        _currentKeys = nextKeys;
        _currentReadOnly = desiredReadOnly;
        _currentMinChunk = minChunk;
        _hasCurrent = true;

        return new ChunkWindowPlan(activeBounds, minChunk, desiredReadOnly, enteredReadOnly, exitedReadOnly);
    }

    private static void ValidateVisibleBounds(WorldRect bounds)
    {
        if (bounds.MinX >= bounds.MaxX || bounds.MinY >= bounds.MaxY)
            throw new ArgumentException("Видимая область должна быть непустым прямоугольником с Max exclusive.", nameof(bounds));
    }

    /// <summary>
    /// Окно с шагом 64 максимально центрируется около середины видимого интервала.
    /// Вычисление использует остаток относительно чанка вместо удвоения глобальных
    /// координат, поэтому не переполняется около пределов Int64.
    /// </summary>
    private long PreferredStartChunk(long minTile, long maxTileExclusive)
    {
        long width = checked(maxTileExclusive - minTile);
        long centerFloor = checked(minTile + width / 2);
        long centerChunk = WorldCoordinates.FloorDiv(centerFloor, WorldCoordinates.ChunkSize);
        long remainderTwice = 2L * WorldCoordinates.FloorMod(centerFloor, WorldCoordinates.ChunkSize)
            + (width & 1L);
        // Ровно посередине выбираем меньший ключ для стабильности.
        long roundUp = remainderTwice > WorldCoordinates.ChunkSize ? 1L : 0L;
        return checked(centerChunk - _activeChunkSide / 2 + roundUp);
    }

    private ChunkKey[] BuildKeys(ChunkKey minChunk)
    {
        int side = _activeChunkSide;
        long count = (long)side * side;              // без переполнения int
        if (count > MaxWindowSide * (long)MaxWindowSide)
            throw new InvalidOperationException($"Сторона окна {side} слишком велика.");
        var keys = new ChunkKey[(int)count];
        int index = 0;
        for (int y = 0; y < side; y++)
        {
            long chunkY = checked(minChunk.Y + y);
            for (int x = 0; x < side; x++)
                keys[index++] = new ChunkKey(checked(minChunk.X + x), chunkY);
        }
        return keys;
    }

    private WorldRect BuildActiveBounds(ChunkKey minChunk)
    {
        WorldCell origin = WorldCoordinates.TileOrigin(minChunk);
        long maxX = checked(origin.X + _activeTileSide);
        long maxY = checked(origin.Y + _activeTileSide);
        return new WorldRect(origin.X, origin.Y, maxX, maxY);
    }
}
