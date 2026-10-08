using System;
using Game.Core.WorldStreaming;namespace Game.Core.WorldStreaming;

/// <summary>
/// Собственные seed-домены характеристик регионов. Значения намеренно НЕ пересекаются
/// с <see cref="GenerationDomain"/> (слои шума не читают чужой домен — §18/§23): добавление
/// trait-слоя не меняет ни рельеф, ни биомы, ни фичи генерации. Домены — произвольные
/// 64-битные константы; вводятся новые члены enum или произвольные id через SampleExtra.
/// </summary>
public enum RegionTraitKind : ulong
{
    Moisture = 0x7E5A3B6C1D9F2048ul,
    Water = 0xD3C2B1A0F9E88776ul,
    Forest = 0xAB12CD34EF567890ul,
    Stone = 0x123456789ABCDEF0ul,
    Elevation = 0x0F1E2D3C4B5A6978ul,
    Temperature = 0xE7A1B2C3D4F50687ul
}

/// <summary>Характеристики региона: пять Q0.16 значений (0..65535) в диапазоне 0..1.</summary>
public readonly record struct RegionTraits(
    ushort MoistureQ16,
    ushort WaterQ16,
    ushort ForestQ16,
    ushort StoneQ16,
    ushort ElevationQ16,
    ushort TemperatureQ16);

/// <summary>Собственный цвет мировой карты: 1 px = 1 регион (RGB, без Godot-типов).</summary>
public readonly record struct Rgb24(byte R, byte G, byte B);

/// <summary>
/// Детерминированный провайдер характеристик региона. Каждое значение — верхние 16 бит
/// domain-separated hash от координат региона: зависит только от seed, версии генератора
/// и абсолютных координат, но НЕ от порядка/параллельности загрузки. Стоимость — один hash
/// на характеристику (мировая карта 1024×2048 ≈ 10.5 млн hash, <100 мс).
/// </summary>
public static class RegionTraitProvider
{
    private const int Q16One = ushort.MaxValue;

    /// <summary>
    /// Детерминированные случайные характеристики из координат клетки.
    /// Используется для мировой карты — каждый тайл получает рандомный биом.
    /// </summary>
    public static RegionTraits FromCoords(long wx, long wy, ulong seed, uint ver)
    {
        // Используем те же домены что и Sample(), но с world-координатами вместо региона
        return new(
            SampleQ16Wd(seed, ver, (ulong)RegionTraitKind.Moisture, wx, wy),
            SampleQ16Wd(seed, ver, (ulong)RegionTraitKind.Water, wx, wy),
            SampleQ16Wd(seed, ver, (ulong)RegionTraitKind.Forest, wx, wy),
            SampleQ16Wd(seed, ver, (ulong)RegionTraitKind.Stone, wx, wy),
            SampleQ16Wd(seed, ver, (ulong)RegionTraitKind.Elevation, wx, wy),
            SampleQ16Wd(seed, ver, (ulong)RegionTraitKind.Temperature, wx, wy));
    }

    private static ushort SampleQ16Wd(ulong seed, uint ver, ulong domain, long wx, long wy)
        => (ushort)(CoordinateHash.Hash(
            seed, ver, (GenerationDomain)domain, wx, wy) >> 48);

    /// <summary>Порог «водного» региона для цвета мировой карты (0.55).</summary>
    public const ushort WaterHigh = 36044;

    /// <summary>Порог «горного» региона для цвета мировой карты (0.68).</summary>
    public const ushort MountainElevation = 44563;

    /// <summary>Ниже этой влажности луг считается сухим (0.30).</summary>
    public const ushort DryMoisture = 19660;

    /// <summary>Выше этой плотности леса луг затемняется (0.60).</summary>
    public const ushort ForestHigh = 39321;

    /// <summary>Пять базовых характеристик региона одним вызовом.</summary>
    public static RegionTraits Sample(ulong worldSeed, uint generatorVersion, RegionKey region)
        => new(
            SampleQ16(worldSeed, generatorVersion, (ulong)RegionTraitKind.Moisture, region),
            SampleQ16(worldSeed, generatorVersion, (ulong)RegionTraitKind.Water, region),
            SampleQ16(worldSeed, generatorVersion, (ulong)RegionTraitKind.Forest, region),
            SampleQ16(worldSeed, generatorVersion, (ulong)RegionTraitKind.Stone, region),
            SampleQ16(worldSeed, generatorVersion, (ulong)RegionTraitKind.Elevation, region),
            SampleQ16(worldSeed, generatorVersion, (ulong)RegionTraitKind.Temperature, region));

    /// <summary>
    /// Произвольная характеристика по числовому id домена — точка расширения для будущих
    /// десятков условий без изменения struct RegionTraits. Для именованных доменов
    /// эквивалентна чтению поля Sample (тот же hash-путь).
    /// </summary>
    public static ushort SampleExtra(ulong worldSeed, uint generatorVersion, RegionKey region, ulong traitId)
        => SampleQ16(worldSeed, generatorVersion, traitId, region);

    /// <summary>
    /// Цвет региона для мировой карты: вода → синий; высокогорье → серо-коричневый;
    /// иначе луг (зелёный), сухость сдвигает к жёлто-коричневому, лес затемняет.
    /// Чистая целочисленная арифметика Q16 — детерминирована на любой платформе.
    /// </summary>
    public static Rgb24 SummaryColor(RegionTraits traits)
    {
        if (traits.WaterQ16 >= WaterHigh)
            return new Rgb24(34, 110, 230);

        if (traits.ElevationQ16 >= MountainElevation)
            return new Rgb24(142, 130, 124);

        ushort dryness = traits.MoistureQ16 >= DryMoisture
            ? (ushort)0
            : (ushort)((uint)(DryMoisture - traits.MoistureQ16) * Q16One / DryMoisture);
        byte r = MixByte(114, 194, dryness);
        byte g = MixByte(184, 166, dryness);
        byte b = MixByte(76, 90, dryness);

        ushort forest = traits.ForestQ16 >= ForestHigh
            ? (ushort)Q16One
            : (ushort)((uint)traits.ForestQ16 * Q16One / ForestHigh);
        r = MixByte(r, 40, forest);
        g = MixByte(g, 112, forest);
        b = MixByte(b, 46, forest);

        return new Rgb24(r, g, b);
    }

    private static ushort SampleQ16(ulong worldSeed, uint generatorVersion, ulong domain, RegionKey region)
        => (ushort)(CoordinateHash.Hash(
            worldSeed, generatorVersion, (GenerationDomain)domain, region.X, region.Y) >> 48);

    private static byte MixByte(byte a, byte b, ushort t)
    {
        int value = ((int)a * (Q16One - t) + (int)b * t + Q16One / 2) / Q16One;
        return (byte)Math.Clamp(value, 0, 255);
    }

    // ---------- Билинейное смешивание характеристик соседних регионов (бесшовные стыки) ----------

    /// <summary>
    /// Cell-centered веса Q16: u = (lx+0.5)/RegionSize, v = (ly+0.5)/RegionSize.
    /// Функция весов непрерывна по клеткам, поэтому смешанные характеристики не имеют
    /// тайловых границ (максимальный скачок между соседними клетками ≤ range/RegionSize).
    /// </summary>
    public static (uint U, uint V) BlendWeights(long worldX, long worldY)
    {
        (int lx, int ly) = WorldRegions.LocalForCell(worldX, worldY);
        uint u = (uint)(((long)lx * 2 + 1) * (long)Q16One / (2L * WorldRegions.RegionSize));
        uint v = (uint)(((long)ly * 2 + 1) * (long)Q16One / (2L * WorldRegions.RegionSize));
        return (u, v);
    }

    /// <summary>
    /// Смешанные характеристики в точке клетки: билинейная интерполяция четырех регионов,
    /// содержащих [x,y]. Точка расширения для встраивания регионального bias в генерацию
    /// (§23 шаг 2) — один вызов на клетку, без правок доменов генерации.
    /// </summary>
    public static RegionTraits SampleBlended(ulong worldSeed, uint generatorVersion, long worldX, long worldY)
    {
        RegionKey region = WorldRegions.RegionForCell(worldX, worldY);
        (uint u, uint v) = BlendWeights(worldX, worldY);
        return BlendBilinear(
            Sample(worldSeed, generatorVersion, region),
            Sample(worldSeed, generatorVersion, new RegionKey(checked(region.X + 1), region.Y)),
            Sample(worldSeed, generatorVersion, new RegionKey(region.X, checked(region.Y + 1))),
            Sample(worldSeed, generatorVersion, new RegionKey(checked(region.X + 1), checked(region.Y + 1))),
            u, v);
    }

    /// <summary>
    /// Билинейная интерполяция четырех угловых наборов (nw/ne/sw/se) с весами u,v (Q16).
    /// Чистая целочисленная арифметика — результат байт-в-байт тот же при любом порядке вызовов.
    /// </summary>
    public static RegionTraits BlendBilinear(
        RegionTraits nw, RegionTraits ne, RegionTraits sw, RegionTraits se, uint u, uint v)
    {
        ushort top = Blend2(nw.MoistureQ16, ne.MoistureQ16, u);
        ushort bottom = Blend2(sw.MoistureQ16, se.MoistureQ16, u);
        ushort moisture = Blend2(top, bottom, v);

        top = Blend2(nw.WaterQ16, ne.WaterQ16, u);
        bottom = Blend2(sw.WaterQ16, se.WaterQ16, u);
        ushort water = Blend2(top, bottom, v);

        top = Blend2(nw.ForestQ16, ne.ForestQ16, u);
        bottom = Blend2(sw.ForestQ16, se.ForestQ16, u);
        ushort forest = Blend2(top, bottom, v);

        top = Blend2(nw.StoneQ16, ne.StoneQ16, u);
        bottom = Blend2(sw.StoneQ16, se.StoneQ16, u);
        ushort stone = Blend2(top, bottom, v);

        top = Blend2(nw.ElevationQ16, ne.ElevationQ16, u);
        bottom = Blend2(sw.ElevationQ16, se.ElevationQ16, u);
        ushort elevation = Blend2(top, bottom, v);

        top = Blend2(nw.TemperatureQ16, ne.TemperatureQ16, u);
        bottom = Blend2(sw.TemperatureQ16, se.TemperatureQ16, u);
        ushort temperature = Blend2(top, bottom, v);

        return new RegionTraits(moisture, water, forest, stone, elevation, temperature);
    }

    private static ushort Blend2(ushort a, ushort b, uint t)
    {
        long value = (long)a * ((long)Q16One - t) + (long)b * t + Q16One / 2;
        return (ushort)Math.Clamp(value / Q16One, 0, Q16One);
    }
}