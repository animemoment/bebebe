using System;

namespace Game.Core.WorldStreaming;

/// <summary>Базовый chunk 64×64, row-major (index = localY*64 + localX).</summary>
public sealed class WorldChunk
{
    public const int Side = WorldCoordinates.ChunkSize;
    public const int CellCount = Side * Side;

    private readonly GeneratedCell[] _cells;

    public ChunkKey Key { get; }
    public ReadOnlyMemory<GeneratedCell> Cells => _cells;

    internal WorldChunk(ChunkKey key, GeneratedCell[] cells)
    {
        if (cells == null)
            throw new ArgumentNullException(nameof(cells));
        if (cells.Length != CellCount)
            throw new ArgumentException($"Chunk должен содержать ровно {CellCount} клеток.", nameof(cells));

        Key = key;
        _cells = cells;
    }

    public GeneratedCell this[LocalCell local]
    {
        get
        {
            if ((uint)local.X >= Side || (uint)local.Y >= Side)
                throw new ArgumentOutOfRangeException(nameof(local), "Локальная координата вне чанка.");
            return _cells[local.Y * Side + local.X];
        }
    }

    public GeneratedCell GetCell(int localX, int localY) => this[new LocalCell(localX, localY)];
}
