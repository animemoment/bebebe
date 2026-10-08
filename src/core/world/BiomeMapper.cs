using System;
using Godot;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Маппер биомов: определяет тип тайла и вариант для клетки на основе её trait'ов.
/// Тайлы берутся из ForWorldMap.png (10×10 сетка, 16×16px).
/// 
/// Atlas grid (col, row) → BiomeType:
///   (0,0..2) = Sand/Desert variants      [source 0,1,2]
///   (1,0..2) = Steppe variants           [source 3,4,5]
///   (2,0..2) = Water variants            [source 6,7,8]
///   (3,0)    = Mountain                  [source 9]
///   (4,0)    = Plains                    [source 10]
///   (5,0)    = Swamp                      [source 11]
///   (6..9,0) = Forest variants           [source 12,13,14,15]
/// </summary>
public static class BiomeMapper
{
    // Пороги Q16 для определения биома.
    private const ushort VeryCold = 15000;   // Полярный холод
    private const ushort Cold = 22000;       // Холодно
    private const ushort Warm = 38000;       // Тёплый умеренный
    private const ushort Hot = 50000;        // Жаркий тропик

    private const ushort VeryDry = 15000;    // Сухой пустыня
    private const ushort Dry = 28000;        // Сухая степь
    private const ushort Wet = 40000;        // Влажный
    private const ushort VeryWet = 50000;    // Болото

    private const ushort DenseForest = 35000; // Плотный лес

    /// <summary>
    /// Определяет BiomeType и variant для клетки по её traits.
    /// variant = детерминированный выбор из нескольких тайлов биома через hash.
    /// </summary>
    public static TilePick Pick(ulong seed, uint ver, long wx, long wy, RegionTraits traits)
    {
        // Шаг 1: Вода? — проверяем elevation + moisture
        if (traits.ElevationQ16 < 22_000 && traits.WaterQ16 > 35_000)
        {
            int variant = HashVariant(seed, ver, wx, wy, 3);
            return new TilePick(BiomeType.DeepWater, variant);
        }

        // Шаг 2: Высокогорье — горы или заснеженные пики
        if (traits.ElevationQ16 > 45_000)
        {
            return new TilePick(BiomeType.Mountain, 0);
        }

        // Шаг 3: Биом суши — зависит от Latitude(Temperature) + Moisture + Forest
        var temp = traits.TemperatureQ16;
        var moist = traits.MoistureQ16;
        var forest = traits.ForestQ16;

        // --- Полярная зона (очень холодно) ---
        if (temp < VeryCold)
        {
            // Ледяной щит / тундра — но у нас нет спецтайлов льда, поэтому используем песок-снег
            // или plains с лёгким bias. Для реализма: если elevation высокий → горные снежники, иначе → степь-тундра
            if (traits.ElevationQ16 > 35_000)
            {
                // Заснеженная горная тундра — используем mountain
                return new TilePick(BiomeType.Mountain, 0);
            }
            // Низкая полярная степь/тундра
            return new TilePick(BiomeType.Steppe, HashVariant(seed, ver, wx, wy, 3));
        }

        // --- Умеренная зона ---
        if (temp < Warm)
        {
            if (moist < Dry)
            {
                // Сухая степь
                return new TilePick(BiomeType.Steppe, HashVariant(seed, ver, wx, wy, 3));
            }
            if (moist >= Wet && traits.ElevationQ16 < 28_000)
            {
                // Умеренное болото
                return new TilePick(BiomeType.Swamp, 0);
            }
            // Обычная степь/трава
            return new TilePick(BiomeType.Plains, 0);
        }

        // --- Тропическая / жаркая зона ---
        // Горячая зона: влажность определяет всё
        if (moist < VeryDry)
        {
            // Горячая пустыня — песок
            return new TilePick(BiomeType.Desert, HashVariant(seed, ver, wx, wy, 3));
        }
        if (moist < Wet)
        {
            // Теплая сухая трава / savanna — шаггей
            return new TilePick(BiomeType.Steppe, HashVariant(seed, ver, wx, wy, 3));
        }
        if (forest >= DenseForest || (moist >= VeryWet && traits.ElevationQ16 < 30_000))
        {
            // Тропический дождевой лес
            return new TilePick(BiomeType.Forest, HashVariant(seed, ver, wx, wy, 4));
        }
        if (moist >= VeryWet && traits.ElevationQ16 < 26_000)
        {
            // Болотистые джунгли
            return new TilePick(BiomeType.Swamp, 0);
        }

        // По умолчанию — равнина
        return new TilePick(BiomeType.Plains, 0);
    }

    /// <summary>
    /// Альтернативный pick без явного temperature — для обратной совместимости.
    /// Темпатура оценивается по Y координате напрямую.
    /// </summary>
    public static TilePick PickWithoutTemp(ulong seed, uint ver, long wx, long wy, RegionTraits traits)
    {
        // Оценка температуры через Y: север = холодно, юг = тепло
        float latBias = Mathf.Clamp((float)wy / 80_000f, -1f, 1f);
        short estTemp = (short)(Mathf.Lerp(8000, 55000, (latBias + 1f) / 2f));

        // Fallback: используем moisture как основной фактор
        return NewPickFallback(wx, wy, traits, estTemp);
    }

    /// <summary>
    /// Старый fallback-путь: biome определяется только из moisture/forest/elev/water.
    /// Без TemperatureQ16. Используется когда темпатура недоступна.
    /// </summary>
    internal static TilePick NewPickFallback(long wx, long wy, RegionTraits traits, short estimatedTemp)
    {
        // Вода
        if (traits.ElevationQ16 < 22_000 && traits.WaterQ16 > 35_000)
        {
            int variant = HashVariantFromCoords((ulong)wx, (ulong)wy, 3);
            return new TilePick(BiomeType.DeepWater, variant);
        }

        // Горы
        if (traits.ElevationQ16 > 45_000)
            return new TilePick(BiomeType.Mountain, 0);

        // Используем estimatedTemp из caller
        int temp = estimatedTemp;

        // Экватор жара
        bool hot = temp > 45000;

        if (hot)
        {
            if (traits.MoistureQ16 < 15000)
                return new TilePick(BiomeType.Desert, HashVariantFromCoords((ulong)wx, (ulong)wy, 3));
            if (traits.MoistureQ16 >= VeryWet)
            {
                if (traits.ForestQ16 >= DenseForest)
                    return new TilePick(BiomeType.Forest, HashVariantFromCoords((ulong)wx, (ulong)wy, 4));
                return new TilePick(BiomeType.Swamp, 0);
            }
            return new TilePick(BiomeType.Steppe, HashVariantFromCoords((ulong)wx, (ulong)wy, 3));
        }

        // Умеренный / холодный
        if (traits.MoistureQ16 < Dry)
            return new TilePick(BiomeType.Steppe, HashVariantFromCoords((ulong)wx, (ulong)wy, 3));
        if (traits.MoistureQ16 >= Wet && traits.ElevationQ16 < 28_000)
            return new TilePick(BiomeType.Swamp, 0);

        if (traits.ForestQ16 >= DenseForest && traits.MoistureQ16 >= Warm)
            return new TilePick(BiomeType.Forest, HashVariantFromCoords((ulong)wx, (ulong)wy, 4));

        return new TilePick(BiomeType.Plains, 0);
    }

    /// <summary>
    /// Deterministic variant index в диапазоне [0..count).
    /// Для песка: desert steppe water → 3 варианта.
    /// Для леса: 4 варианта. Остальные → 0.
    /// </summary>
    private static int HashVariant(ulong seed, uint ver, long wx, long wy, int count)
    {
        ulong h = CoordinateHash.Hash(seed, ver, GenerationDomain.FeatureId, wx, wy);
        return (int)((h >> 32) % (uint)count);
    }

    private static int HashVariantFromCoords(ulong wx, ulong wy, int count)
    {
        ulong h = CoordinateHash.Hash(0, 0, GenerationDomain.FeatureId, (long)wx, (long)wy);
        return (int)((h >> 32) % (uint)count);
    }

    /// <summary>
    /// Получить source index в TileSetAtlasSource для данного biome + variant.
    /// Устаревший — теперь используется GetTileId() в WorldMapTileMapper который
    /// знает точные tileId по порядку вставки тайлов в TileSet.
    /// </summary>
    [Obsolete("Use GetTileId() from WorldMapTileMapper instead")]
    public static int TileSourceIndex(BiomeType biome, int variant)
    {
        // Маппинг на основе atlas columns/rows в ForWorldMap.png (10 cols × ~3 rows):
        // Col 0 (Sand):  rows 0,1,2 → sources 0,1,2
        // Col 1 (Steppe): rows 0,1,2 → sources 3,4,5
        // Col 2 (Water):  rows 0,1,2 → sources 6,7,8
        // Col 3 (Mountain): row 0     → source 9
        // Col 4 (Plains):  row 0      → source 10
        // Col 5 (Swamp):   row 0      → source 11
        // Col 6-9 (Forest): rows 0    → sources 12,13,14,15
        
        return biome switch
        {
            BiomeType.Desert => Math.Clamp(variant, 0, 2),              // sources 0..2
            BiomeType.Steppe => 3 + Math.Clamp(variant, 0, 2),          // sources 3..5
            BiomeType.DeepWater => 6 + Math.Clamp(variant, 0, 2),       // sources 6..8
            BiomeType.Mountain => 9,                                    // source 9
            BiomeType.Plains => 10,                                     // source 10
            BiomeType.Forest => 12 + Math.Clamp(variant, 0, 3),         // sources 12..15
            BiomeType.Swamp => 11,                                      // source 11
            _ => 10                                                     // fallback: plains
        };
    }
}
