#nullable enable
using System;
using System.IO;

namespace Game.Core.WorldStreaming;

/// <summary>Восстановленные identity и checkpoint данные мира.</summary>
public readonly record struct ResumeState(
    ulong Seed,
    uint GeneratorVersion,
    uint FeatureSchemaVersion,
    double GameTime,
    long StartTileX,
    long StartTileY,
    bool IsNewWorld);

/// <summary>
/// Координирует world manifest и существующий chunk delta store. Форматы и контрольные суммы
/// остаются ответственностью WorldManifestStore и WorldChunkStore/WorldChunkCodec.
/// </summary>
public sealed class WorldSaveCoordinator
{
    private const string ManifestFileName = "world.manifest";

    private readonly object _manifestGate = new();
    private readonly string _saveRootDir;
    private readonly string _manifestPath;
    private readonly ulong _seed;
    private readonly uint _generatorVersion;
    private readonly uint _featureSchemaVersion;
    private readonly WorldChunkStore _chunkStore;

    private bool _isOpened;

    public WorldSaveCoordinator(
        string saveRootDir,
        ulong seed,
        uint generatorVersion,
        uint featureSchemaVersion)
    {
        if (string.IsNullOrWhiteSpace(saveRootDir))
            throw new ArgumentException("Каталог сохранения мира не должен быть пустым.", nameof(saveRootDir));

        _saveRootDir = Path.GetFullPath(saveRootDir);
        _manifestPath = Path.Combine(_saveRootDir, ManifestFileName);
        _seed = seed;
        _generatorVersion = generatorVersion;
        _featureSchemaVersion = featureSchemaVersion;
        _chunkStore = new WorldChunkStore(_saveRootDir);
    }

    /// <summary>Каталог, в котором живут manifest и файлы чанков (абсолютный путь).</summary>
    public string SaveRootDir => _saveRootDir;

    /// <summary>
    /// Загрузить совместимый manifest или создать новый с указанной стартовой клеткой.
    /// Существующий повреждённый manifest не перезаписывается автоматически.
    /// </summary>
    public ResumeState OpenOrCreate(long desiredStartTileX, long desiredStartTileY)
    {
        lock (_manifestGate)
        {
            if (!ManifestFileExistsStrict(_manifestPath))
            {
                var initial = new WorldManifest(
                    _seed,
                    _generatorVersion,
                    _featureSchemaVersion,
                    0d,
                    desiredStartTileX,
                    desiredStartTileY);
                WorldManifestStore.Save(_manifestPath, initial);
                _isOpened = true;
                return ToResumeState(initial, isNewWorld: true);
            }

            if (!WorldManifestStore.TryLoad(_manifestPath, out WorldManifest manifest, out string error))
                throw new InvalidDataException(
                    $"Манифест мира существует, но повреждён или недоступен: '{_manifestPath}'. Причина: {error}");

            EnsureCompatible(manifest);
            _isOpened = true;
            return ToResumeState(manifest, isNewWorld: false);
        }
    }

    /// <summary>Атомарно сохранить мировое время и стартовый глобальный tile coordinate.</summary>
    public void PersistCheckpoint(double gameTime, long startTileX, long startTileY)
    {
        if (!double.IsFinite(gameTime) || gameTime < 0d)
            throw new ArgumentOutOfRangeException(nameof(gameTime), gameTime,
                "Мировое игровое время должно быть конечным и неотрицательным.");

        lock (_manifestGate)
        {
            if (!_isOpened)
                throw new InvalidOperationException("Сначала вызовите OpenOrCreate для этого мира.");

            var updated = new WorldManifest(
                _seed,
                _generatorVersion,
                _featureSchemaVersion,
                gameTime,
                startTileX,
                startTileY);
            WorldManifestStore.Save(_manifestPath, updated);
        }
    }

    /// <summary>Сохранить уже сериализованный payload через атомарный WorldChunkStore.</summary>
    public void StoreChunkSnapshot(ChunkSnapshot snapshot)
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.Payload == null)
            throw new ArgumentException("Payload chunk snapshot не должен быть null.", nameof(snapshot));

        EnsureCompatible(snapshot.Metadata, nameof(snapshot));
        _chunkStore.SaveChunk(snapshot.Metadata, snapshot.ChunkX, snapshot.ChunkY, snapshot.Payload);
    }

    /// <summary>Прочитать snapshot через существующие ContainsChunk/TryLoadChunk и проверить совместимость.</summary>
    public ChunkSnapshot? ReadChunkSnapshot(long chunkX, long chunkY)
    {
        if (!_chunkStore.ContainsChunk(chunkX, chunkY))
            return null;

        if (!_chunkStore.TryLoadChunk(chunkX, chunkY, out ChunkSnapshot snapshot, out string error))
            throw new InvalidDataException(
                $"Не удалось прочитать delta чанка ({chunkX},{chunkY}): {error}");

        try
        {
            EnsureCompatible(snapshot.Metadata, nameof(chunkX));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"Delta чанка ({chunkX},{chunkY}) несовместима с текущим миром: {ex.Message}", ex);
        }

        return snapshot;
    }

    /// <summary>Получить путь к файлу чанка, сформированный существующим store.</summary>
    public string ChunkPath(long chunkX, long chunkY)
        => _chunkStore.GetChunkFilePath(chunkX, chunkY);

    private void EnsureCompatible(WorldManifest manifest)
    {
        if (manifest.WorldSeed == _seed
            && manifest.GeneratorVersion == _generatorVersion
            && manifest.FeatureSchemaVersion == _featureSchemaVersion)
            return;

        throw new InvalidDataException(
            "Мир несовместим с запрошенной конфигурацией. " +
            $"Ожидалось: seed={_seed}, generatorVersion={_generatorVersion}, featureSchemaVersion={_featureSchemaVersion}; " +
            $"фактически: seed={manifest.WorldSeed}, generatorVersion={manifest.GeneratorVersion}, " +
            $"featureSchemaVersion={manifest.FeatureSchemaVersion}.");
    }

    private void EnsureCompatible(WorldSaveMetadata metadata, string parameterName)
    {
        if (metadata.Seed == _seed
            && metadata.GeneratorVersion == _generatorVersion
            && metadata.FeatureSchemaVersion == _featureSchemaVersion)
            return;

        throw new ArgumentException(
            "Metadata чанка должна совпадать с seed, generatorVersion и featureSchemaVersion мира. " +
            $"Ожидалось: seed={_seed}, generatorVersion={_generatorVersion}, " +
            $"featureSchemaVersion={_featureSchemaVersion}; фактически: seed={metadata.Seed}, " +
            $"generatorVersion={metadata.GeneratorVersion}, featureSchemaVersion={metadata.FeatureSchemaVersion}.",
            parameterName);
    }

    private static ResumeState ToResumeState(WorldManifest manifest, bool isNewWorld)
        => new(
            manifest.WorldSeed,
            manifest.GeneratorVersion,
            manifest.FeatureSchemaVersion,
            manifest.WorldGameTime,
            manifest.StartTileX,
            manifest.StartTileY,
            isNewWorld);

    private static bool ManifestFileExistsStrict(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
                throw new IOException($"Путь манифеста занят директорией: '{path}'.");
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
