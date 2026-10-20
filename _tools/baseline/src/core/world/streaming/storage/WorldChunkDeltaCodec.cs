using System;
using System.Collections.Generic;
using System.IO;

namespace Game.Core.WorldStreaming;

/// <summary>Одна opaque запись delta внутри 64×64 чанка.</summary>
public sealed record WorldChunkDeltaRecord(ushort LocalIndex, byte Kind, byte[] Data);

/// <summary>Набор записей чанка; пустой набор кодируется пустым payload.</summary>
public sealed class WorldChunkDelta
{
    private readonly WorldChunkDeltaRecord[] _records;

    public IReadOnlyList<WorldChunkDeltaRecord> Records { get; }

    public static WorldChunkDelta Empty { get; } = new(Array.Empty<WorldChunkDeltaRecord>());

    public WorldChunkDelta(IEnumerable<WorldChunkDeltaRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = new List<WorldChunkDeltaRecord>();
        foreach (var record in records)
            list.Add(record);
        _records = list.ToArray();
        Records = Array.AsReadOnly(_records);
    }
}

/// <summary>
/// Детерминированный lossless codec generic sparse payload для будущих delta DTO.
/// Не сериализует игровые менеджеры и не интерпретирует Kind/Data.
/// </summary>
public static class WorldChunkDeltaCodec
{
    public const ushort CurrentSchemaVersion = 1;
    public const int MaxLocalIndex = 4095;
    public const int MaxRecordCount = 65_536;
    public const int MaxRecordDataBytes = WorldChunkCodec.MaxPayloadBytes;
    public const int MaxPayloadBytes = WorldChunkCodec.MaxPayloadBytes;

    private const int MagicLength = 4;
    private const int PrefixLength = MagicLength + sizeof(ushort);
    private static readonly byte[] Magic = { (byte)'W', (byte)'D', (byte)'L', (byte)'T' };

    /// <summary>
    /// Encode-ить sparse delta. Записи сортируются по LocalIndex, Kind и payload
    /// (лексикографически unsigned byte); идентичные дубли сохраняются.
    /// Пустой список возвращает Array.Empty<byte>() для отсутствия chunk-файла.
    /// </summary>
    public static byte[] Encode(WorldChunkDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        IReadOnlyList<WorldChunkDeltaRecord> records = delta.Records;
        int count = records.Count;
        if (count == 0)
            return Array.Empty<byte>();
        if (count > MaxRecordCount)
            throw new ArgumentOutOfRangeException(nameof(delta), $"Число записей превышает лимит {MaxRecordCount}.");

        var sorted = new SortEntry[count];
        for (int i = 0; i < count; i++)
        {
            WorldChunkDeltaRecord record = records[i];
            if (record is null)
                throw new ArgumentException($"Запись {i} равна null.", nameof(delta));
            if (record.LocalIndex > MaxLocalIndex)
                throw new ArgumentOutOfRangeException(nameof(delta), $"LocalIndex {record.LocalIndex} вне диапазона 0..{MaxLocalIndex}.");
            if (record.Data is null)
                throw new ArgumentException($"Data записи {i} равен null.", nameof(delta));
            if (record.Data.Length > MaxRecordDataBytes)
                throw new ArgumentOutOfRangeException(nameof(delta), $"Data записи {i} превышает лимит {MaxRecordDataBytes} байт.");
            sorted[i] = new SortEntry(record, i);
        }

        Array.Sort(sorted, SortEntryComparer.Instance);
        long encodedLength = PrefixLength + GetVarUIntSize((uint)count);
        int previousLocalIndex = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            WorldChunkDeltaRecord record = sorted[i].Record;
            int localIndexDelta = record.LocalIndex - previousLocalIndex;
            previousLocalIndex = record.LocalIndex;
            encodedLength += GetVarUIntSize((uint)localIndexDelta) + sizeof(byte)
                + GetVarUIntSize((uint)record.Data.Length) + record.Data.Length;
            if (encodedLength > MaxPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(delta), $"Закодированный payload превышает лимит {MaxPayloadBytes} байт.");
        }

        byte[] payload = new byte[(int)encodedLength];
        Span<byte> destination = payload;
        Magic.AsSpan().CopyTo(destination);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(MagicLength, sizeof(ushort)),
            CurrentSchemaVersion);
        int offset = PrefixLength;
        offset = WriteVarUInt(destination, offset, (uint)count);

        previousLocalIndex = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            WorldChunkDeltaRecord record = sorted[i].Record;
            int localIndexDelta = record.LocalIndex - previousLocalIndex;
            previousLocalIndex = record.LocalIndex;
            offset = WriteVarUInt(destination, offset, (uint)localIndexDelta);
            destination[offset++] = record.Kind;
            offset = WriteVarUInt(destination, offset, (uint)record.Data.Length);
            record.Data.AsSpan().CopyTo(destination.Slice(offset));
            offset += record.Data.Length;
        }

        if (offset != payload.Length)
            throw new InvalidOperationException("Внутренняя ошибка расчёта длины sparse payload.");
        return payload;
    }

    /// <summary>
    /// Декодировать и проверить sparse payload. Пустой payload означает пустой delta.
    /// Возвращает false для неизвестной версии, неправильной сортировки, выхода за границы,
    /// некорректного varuint, truncated/trailing bytes и превышения лимитов.
    /// </summary>
    public static bool TryDecode(
        ReadOnlyMemory<byte> payload,
        out WorldChunkDelta delta,
        out string error)
    {
        delta = null;
        error = null;

        if (payload.IsEmpty)
        {
            delta = WorldChunkDelta.Empty;
            return true;
        }
        if (payload.Length > MaxPayloadBytes)
            return Fail("Sparse payload превышает общий лимит.", out error);

        ReadOnlySpan<byte> source = payload.Span;
        if (source.Length < PrefixLength)
            return Fail("Sparse payload короче magic/schema header.", out error);
        if (!source.Slice(0, MagicLength).SequenceEqual(Magic))
            return Fail("Неверная magic-сигнатура sparse payload.", out error);

        ushort schemaVersion = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            source.Slice(MagicLength, sizeof(ushort)));
        if (schemaVersion != CurrentSchemaVersion)
            return Fail($"Schema version {schemaVersion} не поддерживается (текущая {CurrentSchemaVersion}).", out error);

        int offset = PrefixLength;
        if (!TryReadVarUInt(source, ref offset, out uint rawCount))
            return Fail("Некорректный или усечённый varuint count.", out error);
        if (rawCount == 0)
            return Fail("Непустой payload не может содержать ноль записей.", out error);
        if (rawCount > MaxRecordCount)
            return Fail($"Число записей превышает лимит {MaxRecordCount}.", out error);

        int count = (int)rawCount;
        var records = new WorldChunkDeltaRecord[count];
        uint previousLocalIndex = 0;
        WorldChunkDeltaRecord previousRecord = null;

        for (int i = 0; i < count; i++)
        {
            if (!TryReadVarUInt(source, ref offset, out uint localIndexDelta))
                return Fail($"Некорректный или усечённый LocalIndex delta в записи {i}.", out error);
            if (previousLocalIndex > (uint)MaxLocalIndex
                || localIndexDelta > (uint)MaxLocalIndex - previousLocalIndex)
                return Fail($"LocalIndex записи {i} выходит за диапазон 0..{MaxLocalIndex}.", out error);
            uint localIndex = previousLocalIndex + localIndexDelta;
            previousLocalIndex = localIndex;

            if (offset >= source.Length)
                return Fail($"Отсутствует Kind записи {i}.", out error);
            byte kind = source[offset++];

            if (!TryReadVarUInt(source, ref offset, out uint rawDataLength))
                return Fail($"Некорректная или усечённая длина Data записи {i}.", out error);
            if (rawDataLength > MaxRecordDataBytes)
                return Fail($"Data записи {i} превышает лимит {MaxRecordDataBytes} байт.", out error);
            if (rawDataLength > (uint)(source.Length - offset))
                return Fail($"Data записи {i} усечён.", out error);

            int dataLength = (int)rawDataLength;
            byte[] data = source.Slice(offset, dataLength).ToArray();
            offset += dataLength;
            var record = new WorldChunkDeltaRecord((ushort)localIndex, kind, data);

            if (previousRecord != null && CompareRecordKeys(previousRecord, record) > 0)
                return Fail($"Запись {i} нарушает обязательный порядок LocalIndex/Kind/Data.", out error);
            records[i] = record;
            previousRecord = record;
        }

        if (offset != source.Length)
            return Fail("После последней записи присутствуют trailing bytes.", out error);

        delta = new WorldChunkDelta(records);
        return true;
    }

    private static bool TryReadVarUInt(ReadOnlySpan<byte> source, ref int offset, out uint value)
    {
        value = 0;
        uint result = 0;
        int shift = 0;

        for (int byteIndex = 0; byteIndex < 5; byteIndex++)
        {
            if (offset >= source.Length)
                return false;
            byte current = source[offset++];

            // UInt32 имеет только 4 полезных бита в пятой группе и не допускает continuation.
            if (byteIndex == 4 && (current & 0xF0) != 0)
                return false;

            result |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                // Единственное представление числа: reject overlong varuint.
                if (byteIndex > 0 && (current & 0x7F) == 0)
                    return false;
                value = result;
                return true;
            }
            shift += 7;
        }
        return false;
    }

    private static int WriteVarUInt(Span<byte> destination, int offset, uint value)
    {
        while (value >= 0x80)
        {
            destination[offset++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        destination[offset++] = (byte)value;
        return offset;
    }

    private static int GetVarUIntSize(uint value)
    {
        int size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }
        return size;
    }

    private static int CompareRecordKeys(WorldChunkDeltaRecord left, WorldChunkDeltaRecord right)
    {
        int compare = left.LocalIndex.CompareTo(right.LocalIndex);
        if (compare != 0) return compare;
        compare = left.Kind.CompareTo(right.Kind);
        if (compare != 0) return compare;

        int commonLength = Math.Min(left.Data.Length, right.Data.Length);
        for (int i = 0; i < commonLength; i++)
        {
            compare = left.Data[i].CompareTo(right.Data[i]);
            if (compare != 0) return compare;
        }
        return left.Data.Length.CompareTo(right.Data.Length);
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private readonly struct SortEntry
    {
        public readonly WorldChunkDeltaRecord Record;
        public readonly int OriginalIndex;

        public SortEntry(WorldChunkDeltaRecord record, int originalIndex)
        {
            Record = record;
            OriginalIndex = originalIndex;
        }
    }

    private sealed class SortEntryComparer : IComparer<SortEntry>
    {
        public static readonly SortEntryComparer Instance = new();

        public int Compare(SortEntry left, SortEntry right)
        {
            int compare = CompareRecordKeys(left.Record, right.Record);
            return compare != 0 ? compare : left.OriginalIndex.CompareTo(right.OriginalIndex);
        }
    }
}
