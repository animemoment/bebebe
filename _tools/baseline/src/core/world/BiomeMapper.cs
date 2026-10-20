using System;
using Godot;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Маппер биомов: определяет тип тайла и вариант (0..4) для клетки на основе климатических параметров.
/// Поддерживает полную 18-параметрическую модель (PickFromClimate) и RegionTraits (Pick).
/// </summary>
public static class BiomeMapper
{
    private const ushort VeryCold = 15000;   // Полярный холод / оледенение
    private const ushort Cold = 24000;       // Субарктика / тундра
    private const ushort Warm = 38000;       // Тёплый умеренный
    private const ushort Hot = 48000;        // Жаркий тропик / экватор

    private const ushort VeryDry = 18000;    // Сухая пустыня
    private const ushort Dry = 28000;        // Сухая степь / саванна
    private const ushort Wet = 38000;        // Влажный лес
    private const ushort VeryWet = 48000;    // Болото / джунгли

    private const ushort DenseForest = 36000;

    /// <summary>
    /// Вычисляет BiomeType и вариант (0..4) напрямую из 18 физических параметров WorldClimateParams.
    /// </summary>
    public static TilePick PickFromClimate(ulong seed, uint ver, long wx, long wy, in WorldClimateParams c)
    {
        int variant = HashVariant(seed, ver, wx, wy, 5);

        // 1. Океаны и моря
        if (c.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16)
        {
            // Океанический лед на полюсах
            if (c.TemperatureQ16 < 13000 || c.GlaciationQ16 > 35000)
                return new TilePick(BiomeType.Glacier, variant);

            return new TilePick(BiomeType.DeepWater, variant);
        }

        // 2. Оледенение, снежные шапки и полярные ледники
        if (c.GlaciationQ16 > 28000 || c.TemperatureQ16 < 13000)
        {
            return new TilePick(BiomeType.Glacier, variant);
        }

        // 3. Высокогорные скалы, альпийские пики и хребты (только по реальной высоте рельефа)
        if (c.ElevationQ16 >= WorldClimateGenerator.SeaLevelQ16)
        {
            if (c.ElevationQ16 > 47000)
            {
                // Заснеженные альпийские вершины при холоде
                if (c.TemperatureQ16 < 22000 || c.GlaciationQ16 > 22000)
                    return new TilePick(BiomeType.Glacier, variant);

                return new TilePick(BiomeType.Mountain, variant);
            }
            if (c.ElevationQ16 > 43000 && c.RockExposureQ16 > 46000)
            {
                return new TilePick(BiomeType.Mountain, variant);
            }
        }

        // 4. Суша по 2D матрице Уиттекера (Whittaker Diagram: Температура x Влажность)
        // Жаркий пояс (Экватор, тропики и субтропики):
        if (c.TemperatureQ16 >= 42000)
        {
            if (c.MoistureQ16 < 24000)
                return new TilePick(BiomeType.Desert, variant);   // Желтый: пустыня (Сахара, Аравия)

            if (c.MoistureQ16 < 36000)
                return new TilePick(BiomeType.Steppe, variant);   // Темно-зеленый: саванна / сахель (3..5)

            if (c.MoistureQ16 < 46000)
                return new TilePick(BiomeType.Plains, variant);   // Кислотно-зеленый: травянистые равнины (10)

            return new TilePick(BiomeType.Forest, variant);       // Коричневый с кронами: влажные джунгли / сельва (11..15)
        }

        // Умеренный пояс:
        if (c.TemperatureQ16 >= 24000)
        {
            if (c.MoistureQ16 < 22000)
                return new TilePick(BiomeType.Desert, variant);   // Полупустыни умеренного пояса (Гоби, Большой Бассейн)

            if (c.MoistureQ16 < 34000)
                return new TilePick(BiomeType.Steppe, variant);   // Темно-зеленый: сухая степь / прерии (3..5)

            if (c.MoistureQ16 < 46000)
                return new TilePick(BiomeType.Plains, variant);   // Кислотно-зеленый: плодородные поля / луга (10)

            return new TilePick(BiomeType.Forest, variant);       // Коричневый с кронами: смешанный и широколиственный лес (11..15)
        }

        // Холодный / субарктический пояс:
        if (c.MoistureQ16 < 34000)
            return new TilePick(BiomeType.Steppe, variant);       // Темно-зеленый: тундра / лесотундра (3..5)

        return new TilePick(BiomeType.Forest, variant);           // Коричневый с кронами: хвойная тайга (11..15)
    }

    /// <summary>
    /// Определение биома и варианта из RegionTraits (для обратной совместимости).
    /// </summary>
    public static TilePick Pick(ulong seed, uint ver, long wx, long wy, RegionTraits traits)
    {
        int variant = HashVariant(seed, ver, wx, wy, 5);

        // Вода
        if (traits.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16 && traits.WaterQ16 > 30_000)
        {
            if (traits.TemperatureQ16 < 13000)
                return new TilePick(BiomeType.Glacier, variant);

            return new TilePick(BiomeType.DeepWater, variant);
        }

        // Высокогорье
        if (traits.ElevationQ16 > 48_000)
        {
            if (traits.TemperatureQ16 < 16_000)
                return new TilePick(BiomeType.Glacier, variant);

            return new TilePick(BiomeType.Mountain, variant);
        }

        // Полярный холод / ледник
        if (traits.TemperatureQ16 < 13000)
        {
            return new TilePick(BiomeType.Glacier, variant);
        }

        var temp = traits.TemperatureQ16;
        var moist = traits.MoistureQ16;

        if (temp >= 42000)
        {
            if (moist < 26000)
                return new TilePick(BiomeType.Desert, variant);
            if (moist < 37000)
                return new TilePick(BiomeType.Steppe, variant);
            if (moist < 48000)
                return new TilePick(BiomeType.Plains, variant);
            return new TilePick(BiomeType.Forest, variant);
        }

        if (temp >= 25000)
        {
            if (moist < 26000)
                return new TilePick(BiomeType.Steppe, variant);
            if (moist < 46000)
                return new TilePick(BiomeType.Plains, variant);
            return new TilePick(BiomeType.Forest, variant);
        }

        if (moist < 36000)
            return new TilePick(BiomeType.Steppe, variant);

        return new TilePick(BiomeType.Forest, variant);
    }

    /// <summary>
    /// Детерминированный хеш варианта [0..count).
    /// </summary>
    public static int HashVariant(ulong seed, uint ver, long wx, long wy, int count)
    {
        if (count <= 1) return 0;
        ulong h = CoordinateHash.Hash(seed, ver, GenerationDomain.FeatureId, wx, wy);
        return (int)((h >> 32) % (uint)count);
    }
}
