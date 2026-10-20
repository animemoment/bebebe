using System;
using System.Collections.Generic;
using Game.Core.WorldStreaming;

namespace Game.Core.WorldStreaming.Integration;

/// <summary>
/// Sparse mutations for exactly one chunk. The map contains only explicitly changed
/// (local cell, kind) pairs; absent pairs continue to use base-world state.
/// </summary>
public sealed class ChunkMutations
{
    private readonly Dictionary<(ushort LocalIndex, byte Kind), byte[]> _records = new();

    public ChunkKey Key { get; }
    public bool IsEmpty => _records.Count == 0;
    public int Count => _records.Count;

    public ChunkMutations(ChunkKey key)
    {
        Key = key;
    }

    /// <summary>
    /// Assign one opaque mutation. Returns true only if a record was added or its bytes changed.
    /// The input bytes are copied so caller mutation cannot silently alter stored state.
    /// </summary>
    public bool Set(long cellX, long cellY, byte kind, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ushort localIndex = GetLocalIndex(cellX, cellY);
        var recordKey = (localIndex, kind);

        if (_records.TryGetValue(recordKey, out byte[] existing)
            && existing.AsSpan().SequenceEqual(data))
            return false;

        _records[recordKey] = (byte[])data.Clone();
        return true;
    }

    /// <summary>Remove a mutation, returning true only if one existed.</summary>
    public bool Remove(long cellX, long cellY, byte kind)
        => _records.Remove((GetLocalIndex(cellX, cellY), kind));

    /// <summary>Check whether this chunk stores a mutation for the given cell and kind.</summary>
    public bool Has(long cellX, long cellY, byte kind)
        => _records.ContainsKey((GetLocalIndex(cellX, cellY), kind));

    public void Clear() => _records.Clear();

    /// <summary>
    /// Return owned copies in canonical (LocalIndex, Kind) order for chunk-unload encoding.
    /// There is at most one state record per cell and kind in this mutation set.
    /// </summary>
    public IReadOnlyList<WorldChunkDeltaRecord> Encode()
    {
        var records = new List<WorldChunkDeltaRecord>(_records.Count);
        foreach (KeyValuePair<(ushort LocalIndex, byte Kind), byte[]> pair in _records)
        {
            records.Add(new WorldChunkDeltaRecord(
                pair.Key.LocalIndex,
                pair.Key.Kind,
                (byte[])pair.Value.Clone()));
        }

        records.Sort(static (left, right) =>
        {
            int indexOrder = left.LocalIndex.CompareTo(right.LocalIndex);
            return indexOrder != 0 ? indexOrder : left.Kind.CompareTo(right.Kind);
        });
        return records.AsReadOnly();
    }

    /// <summary>
    /// Build a mutation set from sparse records. If the input repeats a (cell, kind) key,
    /// records are applied in input order and the last value wins.
    /// </summary>
    public static ChunkMutations FromRecords(
        ChunkKey key,
        IReadOnlyList<WorldChunkDeltaRecord> records)
    {
        var mutations = new ChunkMutations(key);
        mutations.Apply(records);
        return mutations;
    }

    /// <summary>
    /// Merge/overwrite records into this mutation set. Inputs are validated and copied before
    /// state changes, so null data or an out-of-range LocalIndex cannot partially apply a batch.
    /// </summary>
    public void Apply(IReadOnlyList<WorldChunkDeltaRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var stableRecords = new WorldChunkDeltaRecord[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            WorldChunkDeltaRecord record = records[i];
            if (record is null)
                throw new ArgumentException($"Record {i} is null.", nameof(records));
            if (record.LocalIndex > WorldChunkDeltaCodec.MaxLocalIndex)
                throw new ArgumentOutOfRangeException(nameof(records),
                    $"Record {i} LocalIndex {record.LocalIndex} is outside 0..{WorldChunkDeltaCodec.MaxLocalIndex}.");
            if (record.Data is null)
                throw new ArgumentException($"Record {i} Data is null.", nameof(records));

            stableRecords[i] = new WorldChunkDeltaRecord(
                record.LocalIndex,
                record.Kind,
                (byte[])record.Data.Clone());
        }

        for (int i = 0; i < stableRecords.Length; i++)
        {
            WorldChunkDeltaRecord record = stableRecords[i];
            _records[(record.LocalIndex, record.Kind)] = record.Data;
        }
    }

    private ushort GetLocalIndex(long cellX, long cellY)
    {
        ChunkKey actualKey = WorldCoordinates.ChunkForTile(cellX, cellY);
        if (actualKey != Key)
            throw new ArgumentException(
                $"Cell ({cellX},{cellY}) belongs to chunk {actualKey}, not mutation chunk {Key}.");

        LocalCell local = WorldCoordinates.LocalForTile(cellX, cellY);
        return checked((ushort)(local.Y * WorldCoordinates.ChunkSize + local.X));
    }
}
