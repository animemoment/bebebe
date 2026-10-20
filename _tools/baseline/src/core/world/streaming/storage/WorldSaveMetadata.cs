namespace Game.Core.WorldStreaming;

/// <summary>
/// Идентификаторы базы мира и checkpoint для одного чанка.
/// GameTimeCheckpoint — мировое игровое время последнего обновления именно этого чанка.
/// </summary>
public readonly record struct WorldSaveMetadata(
    ushort FormatVersion,
    uint GeneratorVersion,
    uint FeatureSchemaVersion,
    ulong Seed,
    double GameTimeCheckpoint);

/// <summary>Декодированное содержимое одного файла delta-чанка.</summary>
public sealed record ChunkSnapshot(
    WorldSaveMetadata Metadata,
    long ChunkX,
    long ChunkY,
    byte[] Payload);
