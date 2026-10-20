using System;
using System.Buffers.Binary;
using System.IO;
using System.Security;

namespace Game.Core.WorldStreaming;

/// <summary>Минимальная глобальная identity/time metadata мира, независимая от chunk delta.</summary>
public readonly record struct WorldManifest(
    ulong WorldSeed,
    uint GeneratorVersion,
    uint FeatureSchemaVersion,
    double WorldGameTime,
    long StartTileX,
    long StartTileY);

/// <summary>
/// Lossless binary store одного world manifest. Файл фиксированного размера, без UTC/offline time.
/// </summary>
public static class WorldManifestStore
{
    public const ushort CurrentFormatVersion = 1;
    public const int HeaderLength = 50;

    // WMAN | format:u16 | world-seed:u64 | generator:u32 | feature-schema:u32 |
    // world-time:f64 | start-x:i64 | start-y:i64 | crc32:u32.
    private const int CrcOffset = HeaderLength - sizeof(uint);
    private static readonly byte[] Magic = { (byte)'W', (byte)'M', (byte)'A', (byte)'N' };
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>Сохранить manifest во временный файл и атомарно заменить целевой.</summary>
    public static void Save(string path, in WorldManifest manifest)
    {
        string fullPath = GetFullPath(path);
        ValidateManifest(manifest);
        byte[] bytes = Encode(manifest);

        string directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Не удалось определить каталог manifest.", nameof(path));
        Directory.CreateDirectory(directory);

        string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // Temp в том же каталоге; прежний manifest остаётся нетронут до готовой замены.
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    /// <summary>Строго загрузить manifest; возвращает false для отсутствующего/повреждённого файла.</summary>
    public static bool TryLoad(string path, out WorldManifest manifest, out string error)
    {
        manifest = default;
        error = null;
        string fullPath;
        try
        {
            fullPath = GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException)
        {
            error = $"Некорректный путь manifest: {ex.Message}";
            return false;
        }

        try
        {
            byte[] bytes;
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != HeaderLength)
                {
                    error = $"Длина manifest должна быть ровно {HeaderLength} байт.";
                    return false;
                }

                bytes = new byte[HeaderLength];
                int totalRead = 0;
                while (totalRead < bytes.Length)
                {
                    int read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
                    if (read == 0)
                    {
                        error = "Manifest усечён во время чтения.";
                        return false;
                    }
                    totalRead += read;
                }
                if (stream.ReadByte() != -1)
                {
                    error = "Размер manifest изменился во время чтения.";
                    return false;
                }
            }

            return TryDecode(bytes, out manifest, out error);
        }
        catch (FileNotFoundException)
        {
            error = "Файл manifest не найден.";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            error = "Каталог manifest не найден.";
            return false;
        }
        catch (IOException ex)
        {
            error = $"Ошибка чтения manifest: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"Нет доступа к manifest: {ex.Message}";
            return false;
        }
        catch (SecurityException ex)
        {
            error = $"Операция с manifest запрещена: {ex.Message}";
            return false;
        }
    }

    private static byte[] Encode(in WorldManifest manifest)
    {
        byte[] bytes = new byte[HeaderLength];
        Span<byte> header = bytes;
        Magic.AsSpan().CopyTo(header);
        int offset = Magic.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(offset, sizeof(ushort)), CurrentFormatVersion);
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(offset, sizeof(ulong)), manifest.WorldSeed);
        offset += sizeof(ulong);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(offset, sizeof(uint)), manifest.GeneratorVersion);
        offset += sizeof(uint);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(offset, sizeof(uint)), manifest.FeatureSchemaVersion);
        offset += sizeof(uint);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(offset, sizeof(long)), BitConverter.DoubleToInt64Bits(manifest.WorldGameTime));
        offset += sizeof(double);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(offset, sizeof(long)), manifest.StartTileX);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(offset, sizeof(long)), manifest.StartTileY);
        offset += sizeof(long);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(offset, sizeof(uint)), ComputeCrc32(header.Slice(0, CrcOffset)));
        return bytes;
    }

    private static bool TryDecode(ReadOnlySpan<byte> bytes, out WorldManifest manifest, out string error)
    {
        manifest = default;
        error = null;
        if (bytes.Length != HeaderLength)
            return Fail($"Длина manifest должна быть ровно {HeaderLength} байт.", out error);
        if (!bytes.Slice(0, Magic.Length).SequenceEqual(Magic))
            return Fail("Неверная magic-сигнатура manifest.", out error);

        int offset = Magic.Length;
        ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        if (formatVersion != CurrentFormatVersion)
            return Fail($"Версия manifest {formatVersion} не поддерживается (текущая {CurrentFormatVersion}).", out error);

        ulong worldSeed = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, sizeof(ulong)));
        offset += sizeof(ulong);
        uint generatorVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));
        offset += sizeof(uint);
        uint featureSchemaVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));
        offset += sizeof(uint);
        double worldGameTime = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(offset, sizeof(long))));
        offset += sizeof(double);
        long startTileX = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(offset, sizeof(long)));
        offset += sizeof(long);
        long startTileY = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(offset, sizeof(long)));
        offset += sizeof(long);
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));

        if (!double.IsFinite(worldGameTime) || worldGameTime < 0)
            return Fail("Некорректное мировое игровое время в manifest.", out error);
        if (ComputeCrc32(bytes.Slice(0, CrcOffset)) != expectedCrc)
            return Fail("CRC32 manifest не совпадает.", out error);

        manifest = new WorldManifest(
            worldSeed,
            generatorVersion,
            featureSchemaVersion,
            worldGameTime,
            startTileX,
            startTileY);
        return true;
    }

    private static void ValidateManifest(in WorldManifest manifest)
    {
        if (!double.IsFinite(manifest.WorldGameTime) || manifest.WorldGameTime < 0)
            throw new ArgumentOutOfRangeException(nameof(manifest), "WorldGameTime должен быть конечным и неотрицательным.");
    }

    private static string GetFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Путь manifest не должен быть пустым.", nameof(path));
        return Path.GetFullPath(path);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
            crc = Crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
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

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Уникальный temp не является manifest; не маскировать ошибку замены.
        }
        catch (UnauthorizedAccessException)
        {
            // Уникальный temp не является manifest; не маскировать ошибку замены.
        }
        catch (SecurityException)
        {
            // Уникальный temp не является manifest; не маскировать ошибку замены.
        }
    }
}
