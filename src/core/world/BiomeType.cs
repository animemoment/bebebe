namespace Game.Core.WorldStreaming;

/// <summary>
/// Тип поверхности чанка — маппится на тайл из ForWorldMap.png атласа.
/// Каждый biome имеет set source-index'ов в TileSetAtlasSource.
/// </summary>
public enum BiomeType : byte
{
    /// <summary>Пустыня / сухой песок. Тайлы: col 0, rows 0-2 (source 0..2).</summary>
    Desert = 0,

    /// <summary>Степь / сухая трава. Тайлы: col 1, rows 0-2 (source 3..5).</summary>
    Steppe = 1,

    /// <summary>Вода (глубокая). Тайлы: col 2, rows 0-2 (source 6..8).</summary>
    DeepWater = 2,

    /// <summary>Гора / скала. Тайл: col 3, row 0 (source 9).</summary>
    Mountain = 3,

    /// <summary>Поля / равнина (средняя влажность). Тайл: col 4, row 0 (source 10).</summary>
    Plains = 4,

    /// <summary>Лес (широкий диапазон). Тайлы: col 6-9, row 0 (source 12..15).</summary>
    Forest = 5,

    /// <summary>Болото / влажная зона. Тайл: col 5, row 0 (source 11).</summary>
    Swamp = 6
}

/// <summary>Информация о выбранном тайле для клетчки.</summary>
public readonly record struct TilePick(
    BiomeType Biome,
    int Variant // индекс варианта внутри биома (0..N)
);
