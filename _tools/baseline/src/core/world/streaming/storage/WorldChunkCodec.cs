using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Lossless codec одного opaque delta-чанка.
/// Файл: фиксированный little-endian header + поток Brotli.
/// </summary>
public static class WorldChunkCodec
{
    public const ushort CurrentFormatVersion = 1;

    /// <summary>Жёсткий предел распакованного payload на один чанк.</summary>
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Brotli-представление может быть больше входа; оставляем ограниченный overhead,
    /// не отказывая валидному payload на предельном размере.
    /// </summary>
    public const int MaxCompressedPayloadBytes = MaxPayloadBytes + 64 * 1024;

    // WCHK | format:u16 | generator:u32 | feature-schema:u32 | seed:u64 |
    // chunk-x:i64 | chunk-y:i64 | game-time:f64 | plain-len:u32 |
    // brotli-len:u32 | crc32:u32 (header prefix + uncompressed payload).
    public const int HeaderLength = 58;

    private static readonly byte[] Magic = { (byte)'W', (byte)'C', (byte)'H', (byte)'K' };
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>Сжать payload и сформировать версионированный бинарный файл.</summary>
    /// <exception cref="ArgumentException">Метаданные или payload некорректны.</exception>
    /// <exception cref="InvalidDataException">Сжатый payload превысил лимит.</exception>
    public static byte[] Encode(
        in WorldSaveMetadata metadata,
        long chunkX,
        long chunkY,
        ReadOnlyMemory<byte> payload)
    {
        ValidateMetadataForEncode(metadata);
        if (payload.IsEmpty)
            throw new ArgumentException("Пустой delta не кодируется в файл.", nameof(payload));
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Payload превышает лимит {MaxPayloadBytes} байт.");

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var brotli = new BrotliStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                brotli.Write(payload.Span);
            compressed = buffer.ToArray();
        }

        if (compressed.Length == 0 || compressed.Length > MaxCompressedPayloadBytes)
            throw new InvalidDataException($"Сжатый payload имеет недопустимый размер: {compressed.Length}.");

        byte[] file = new byte[HeaderLength + compressed.Length];
        Span<byte> header = file.AsSpan(0, HeaderLength);
        Magic.AsSpan().CopyTo(header);
        int offset = Magic.Length;

        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(offset, 2), metadata.FormatVersion);
        offset += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(offset, 4), metadata.GeneratorVersion);
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(offset, 4), metadata.FeatureSchemaVersion);
        offset += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(offset, 8), metadata.Seed);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(offset, 8), chunkX);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(offset, 8), chunkY);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(offset, 8), BitConverter.DoubleToInt64Bits(metadata.GameTimeCheckpoint));
        offset += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(offset, 4), checked((uint)payload.Length));
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(offset, 4), checked((uint)compressed.Length));
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.Slice(offset, 4),
            ComputeCrc32(header.Slice(0, offset), payload.Span));

        compressed.AsSpan().CopyTo(file.AsSpan(HeaderLength));
        return file;
    }

    /// <summary>
    /// Проверить header/границы, распаковать payload и проверить CRC32.
    /// Ошибочное, повреждённое или несовместимое содержимое возвращается как false.
    /// </summary>
    public static bool TryDecode(
        ReadOnlyMemory<byte> fileBytes,
        out ChunkSnapshot snapshot,
        out string error)
    {
        snapshot = null;
        error = null;

        if (fileBytes.Length < HeaderLength)
            return Fail("Файл короче фиксированного заголовка.", out error);
        if (fileBytes.Length > HeaderLength + MaxCompressedPayloadBytes)
            return Fail("Файл превышает максимальный размер чанка.", out error);

        ReadOnlySpan<byte> file = fileBytes.Span;
        ReadOnlySpan<byte> header = file.Slice(0, HeaderLength);
        if (!header.Slice(0, Magic.Length).SequenceEqual(Magic))
            return Fail("Неверная magic-сигнатура chunk-файла.", out error);

        int offset = Magic.Length;
        ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(offset, 2));
        offset += 2;
        uint generatorVersion = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(offset, 4));
        offset += 4;
        uint featureSchemaVersion = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(offset, 4));
        offset += 4;
        ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(offset, 8));
        offset += 8;
        long chunkX = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(offset, 8));
        offset += 8;
        long chunkY = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(offset, 8));
        offset += 8;
        double checkpoint = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(header.Slice(offset, 8)));
        offset += 8;
        uint uncompressedLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(offset, 4));
        offset += 4;
        uint compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(offset, 4));
        offset += 4;
        uint expectedCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(offset, 4));

        if (formatVersion != CurrentFormatVersion)
            return Fail($"Версия формата {formatVersion} не поддерживается (текущая {CurrentFormatVersion}).", out error);
        if (!double.IsFinite(checkpoint) || checkpoint < 0)
            return Fail("Некорректный game-time checkpoint.", out error);
        if (uncompressedLength == 0 || uncompressedLength > MaxPayloadBytes)
            return Fail("Недопустимая длина распакованного payload.", out error);
        if (compressedLength == 0 || compressedLength > MaxCompressedPayloadBytes)
            return Fail("Недопустимая длина Brotli payload.", out error);
        if ((ulong)fileBytes.Length != (ulong)HeaderLength + compressedLength)
            return Fail("Размер файла не совпадает с длиной из заголовка.", out error);

        byte[] payload = new byte[(int)uncompressedLength];
        var decoder = new BrotliDecoder();
        OperationStatus status;
        int bytesConsumed;
        int bytesWritten;
        try
        {
            status = decoder.Decompress(
                file.Slice(HeaderLength, (int)compressedLength),
                payload,
                out bytesConsumed,
                out bytesWritten);
        }
        catch (InvalidDataException ex)
        {
            return Fail($"Ошибка Brotli-декодирования: {ex.Message}", out error);
        }
        finally
        {
            decoder.Dispose();
        }

        if (status != OperationStatus.Done)
            return Fail($"Brotli-поток не завершён корректно: {status}.", out error);
        if (bytesConsumed != (int)compressedLength)
            return Fail("В Brotli-потоке обнаружены лишние или непрочитанные байты.", out error);
        if (bytesWritten != payload.Length)
            return Fail("Фактическая длина payload не совпадает с заголовком.", out error);
        if (ComputeCrc32(header.Slice(0, HeaderLength - sizeof(uint)), payload) != expectedCrc32)
            return Fail("CRC32 header/payload не совпадает.", out error);

        var metadata = new WorldSaveMetadata(
            formatVersion,
            generatorVersion,
            featureSchemaVersion,
            seed,
            checkpoint);
        snapshot = new ChunkSnapshot(metadata, chunkX, chunkY, payload);
        return true;
    }

    private static void ValidateMetadataForEncode(in WorldSaveMetadata metadata)
    {
        if (metadata.FormatVersion != CurrentFormatVersion)
            throw new ArgumentException($"Поддерживается только format version {CurrentFormatVersion}.", nameof(metadata));
        if (!double.IsFinite(metadata.GameTimeCheckpoint) || metadata.GameTimeCheckpoint < 0)
            throw new ArgumentOutOfRangeException(nameof(metadata), "GameTimeCheckpoint должен быть конечным и неотрицательным.");
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> headerPrefix, ReadOnlySpan<byte> payload)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < headerPrefix.Length; i++)
            crc = Crc32Table[(crc ^ headerPrefix[i]) & 0xFF] ^ (crc >> 8);
        for (int i = 0; i < payload.Length; i++)
            crc = Crc32Table[(crc ^ payload[i]) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
