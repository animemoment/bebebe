using System;
using System.Globalization;
using System.IO;
using System.Security;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Файловое хранилище delta-чанков. Не обращается к Godot API и не знает формат payload.
/// Каталог хранилища задаёт вызывающая сторона (например, абсолютный путь user://).
/// </summary>
public sealed class WorldChunkStore
{
    private const int MaxStoredFileBytes = WorldChunkCodec.HeaderLength + WorldChunkCodec.MaxCompressedPayloadBytes;
    private readonly string _saveDirectory;
    private readonly object _ioGate = new();

    public WorldChunkStore(string saveDirectory)
    {
        if (string.IsNullOrWhiteSpace(saveDirectory))
            throw new ArgumentException("Каталог сохранений не должен быть пустым.", nameof(saveDirectory));

        _saveDirectory = Path.GetFullPath(saveDirectory);
    }

    /// <summary>
    /// Проверить наличие записи по пути чанка без чтения/декодирования.
    /// false означает только FileNotFound/DirectoryNotFound; access/IO ошибки не маскируются.
    /// true не гарантирует валидность файла (например, путь может быть занят директорией).
    /// Синхронизировано с SaveChunk/DeleteChunk данного экземпляра.
    /// </summary>
    public bool ContainsChunk(long chunkX, long chunkY)
    {
        lock (_ioGate)
        {
            try
            {
                _ = File.GetAttributes(GetChunkFilePath(chunkX, chunkY));
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Записать delta-чанк атомарной заменой. Пустой payload удаляет устаревший delta
    /// атомарным переименованием в tombstone и возвращает false (файл чанка отсутствует).
    /// </summary>
    /// <returns>true, если сохранён непустой delta; false, если delta пустой.</returns>
    public bool SaveChunk(
        in WorldSaveMetadata metadata,
        long chunkX,
        long chunkY,
        ReadOnlyMemory<byte> payload)
    {
        lock (_ioGate)
        {
            string targetPath = GetChunkFilePath(chunkX, chunkY);
            if (payload.IsEmpty)
            {
                DeleteChunkFileAtomically(targetPath);
                return false;
            }

            // Кодирование/валидация завершаются до затрагивания текущего файла.
            byte[] encoded = WorldChunkCodec.Encode(metadata, chunkX, chunkY, payload);
            Directory.CreateDirectory(_saveDirectory);

            string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: FileOptions.WriteThrough))
                {
                    stream.Write(encoded, 0, encoded.Length);
                    stream.Flush(flushToDisk: true);
                }

                // Temp находится рядом с target: Move с overwrite выполняет замену в рамках
                // одного тома только после полной записи и flush; до этого старый файл цел.
                File.Move(tempPath, targetPath, overwrite: true);
                return true;
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }
    }

    /// <summary>Загрузить и проверить файл чанка, включая совпадение запрошенных координат.</summary>
    public bool TryLoadChunk(
        long chunkX,
        long chunkY,
        out ChunkSnapshot snapshot,
        out string error)
    {
        snapshot = null;
        error = null;

        lock (_ioGate)
        {
            string path = GetChunkFilePath(chunkX, chunkY);
            try
            {
                byte[] fileBytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long fileLength = stream.Length;
                    if (fileLength < WorldChunkCodec.HeaderLength || fileLength > MaxStoredFileBytes)
                    {
                        error = "Размер файла чанка вне допустимых границ.";
                        return false;
                    }

                    fileBytes = new byte[(int)fileLength];
                    int totalRead = 0;
                    while (totalRead < fileBytes.Length)
                    {
                        int read = stream.Read(fileBytes, totalRead, fileBytes.Length - totalRead);
                        if (read == 0)
                        {
                            error = "Файл чанка усечён во время чтения.";
                            return false;
                        }
                        totalRead += read;
                    }

                    if (stream.ReadByte() != -1)
                    {
                        error = "Размер файла изменился во время чтения.";
                        return false;
                    }
                }

                if (!WorldChunkCodec.TryDecode(fileBytes, out snapshot, out error))
                    return false;
                if (snapshot.ChunkX != chunkX || snapshot.ChunkY != chunkY)
                {
                    snapshot = null;
                    error = "Координаты чанка в header не совпадают с именем/запросом.";
                    return false;
                }
                return true;
            }
            catch (FileNotFoundException)
            {
                snapshot = null;
                error = "Файл delta-чанка не найден.";
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                snapshot = null;
                error = "Каталог delta-чанка не найден.";
                return false;
            }
            catch (IOException ex)
            {
                snapshot = null;
                error = $"Ошибка чтения чанка: {ex.Message}";
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                snapshot = null;
                error = $"Нет доступа к файлу чанка: {ex.Message}";
                return false;
            }
            catch (SecurityException ex)
            {
                snapshot = null;
                error = $"Операция с файлом чанка запрещена: {ex.Message}";
                return false;
            }
        }
    }

    /// <summary>
    /// Удалить delta чанка. Возвращает false, если файла не существовало.
    /// Сам delta сначала атомарно исчезает из имени чанка переименованием в tombstone;
    /// очистка временного имени выполняется после этого.
    /// </summary>
    public bool DeleteChunk(long chunkX, long chunkY)
    {
        lock (_ioGate)
            return DeleteChunkFileAtomically(GetChunkFilePath(chunkX, chunkY));
    }

    /// <summary>Путь для диагностики/тестов. Имя состоит только из десятичных signed-long координат.</summary>
    public string GetChunkFilePath(long chunkX, long chunkY)
    {
        string fileName = string.Concat(
            "chunk_",
            chunkX.ToString(CultureInfo.InvariantCulture),
            "_",
            chunkY.ToString(CultureInfo.InvariantCulture),
            ".bin");
        return Path.Combine(_saveDirectory, fileName);
    }

    private bool DeleteChunkFileAtomically(string targetPath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(targetPath);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        // Не дать платформам, где rename разрешён для директорий, переместить каталог
        // вместо chunk-файла. Access/IO ошибки GetAttributes намеренно не маскируются.
        if ((attributes & FileAttributes.Directory) != 0)
            throw new IOException("Путь delta-чанка занят директорией, а не файлом.");

        string tombstonePath = targetPath + "." + Guid.NewGuid().ToString("N") + ".deleted";
        try
        {
            // Атомарно переименовать сам файл; до rename исходный delta не затрагивается.
            File.Move(targetPath, tombstonePath);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        // Target уже отсутствует. Tombstone не сканируется как chunk; при сбое очистки
        // он безопасен для загрузки и может быть удалён позднее обслуживающим кодом.
        TryDeleteFile(tombstonePath);
        return true;
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
            // Оставшийся уникальный temp/tombstone не считается chunk-файлом.
        }
        catch (UnauthorizedAccessException)
        {
            // Не маскировать результат уже завершённого атомарного rename.
        }
        catch (SecurityException)
        {
            // Не маскировать результат уже завершённого атомарного rename.
        }
    }
}
