using System;

namespace Game.Core.WorldStreaming;

/// <summary>Знаковый ключ чанка в глобальной сетке чанков.</summary>
public readonly record struct ChunkKey(long X, long Y);

/// <summary>Знаковая координата клетки в глобальной сетке мира.</summary>
public readonly record struct WorldCell(long X, long Y);

/// <summary>Локальная координата клетки внутри чанка 64×64.</summary>
public readonly record struct LocalCell(int X, int Y);

/// <summary>
/// Прямоугольник глобальных координат клеток с полуоткрытыми границами:
/// Min включён, Max исключён.
/// </summary>
public readonly record struct WorldRect(long MinX, long MinY, long MaxX, long MaxY)
{
    public bool IsEmpty => MinX >= MaxX || MinY >= MaxY;

    public bool Contains(long x, long y)
        => x >= MinX && x < MaxX && y >= MinY && y < MaxY;

    public bool Intersects(WorldRect other)
        => !IsEmpty && !other.IsEmpty
            && MinX < other.MaxX && other.MinX < MaxX
            && MinY < other.MaxY && other.MinY < MaxY;
}

/// <summary>Преобразования между глобальной сеткой клеток и чанками.</summary>
public static class WorldCoordinates
{
    public const int ChunkSize = 64;

    /// <summary>
    /// Деление с округлением к минус бесконечности. Размер делителя обязан быть положительным.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static long FloorDiv(long value, int divisor)
    {
        if (divisor <= 0)
            throw new ArgumentOutOfRangeException(nameof(divisor), "Делитель должен быть положительным.");

        long quotient = value / divisor;
        long remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    /// <summary>Остаток в диапазоне [0, divisor); размер делителя обязан быть положительным.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static int FloorMod(long value, int divisor)
    {
        if (divisor <= 0)
            throw new ArgumentOutOfRangeException(nameof(divisor), "Делитель должен быть положительным.");

        long remainder = value % divisor;
        return (int)(remainder < 0 ? remainder + divisor : remainder);
    }

    public static ChunkKey ChunkForTile(long worldX, long worldY)
        => new(FloorDiv(worldX, ChunkSize), FloorDiv(worldY, ChunkSize));

    public static LocalCell LocalForTile(long worldX, long worldY)
        => new(FloorMod(worldX, ChunkSize), FloorMod(worldY, ChunkSize));

    /// <summary>
    /// Глобальный origin чанка. Переполнение за диапазон long явно бросает OverflowException.
    /// </summary>
    public static WorldCell TileOrigin(ChunkKey key)
        => new(checked(key.X * ChunkSize), checked(key.Y * ChunkSize));

    /// <summary>Глобальные полуоткрытые границы чанка.</summary>
    public static WorldRect ChunkBounds(ChunkKey key)
    {
        WorldCell origin = TileOrigin(key);
        return new WorldRect(
            origin.X,
            origin.Y,
            checked(origin.X + ChunkSize),
            checked(origin.Y + ChunkSize));
    }
}

/// <summary>Набор независимых seed-доменов нового генератора мира.</summary>
public enum GenerationDomain : ulong
{
    ElevationLarge = 0x243F6A8885A308D3ul,
    ElevationMedium = 0x13198A2E03707344ul,
    ElevationFine = 0xA4093822299F31D0ul,
    ElevationDetail = 0x6A09E667F3BCC909ul,
    MoistureLarge = 0x082EFA98EC4E6C89ul,
    MoistureFine = 0x452821E638D01377ul,
    Forest = 0xBE5466CF34E90C6Cul,
    Stone = 0xC0AC29B7C97C50DDul,
    RiverPresence = 0x3F84D5B5B5470917ul,
    RiverGeometry = 0x9216D5D98979FB1Bul,
    LakePresence = 0xD1310BA698DFB5ACul,
    LakeGeometry = 0x2FFD72DBD01ADFB7ul,
    FeatureId = 0xB8E1AFED6A267E96ul,
    Temperature = 0xF1A2B3C4D5E6F708ul,

    // --- Мировая карта (climate, см. src/core/world/climate/) ---
    // ЗАРЕЗЕРВИРОВАНО: значения не менять — от них зависит форма континентов.
    /// <summary>20 климатических параметров мира из сида (лейны 0..19).</summary>
    ClimateParams = 0x5A17C0DE12345678ul,
    /// <summary>Domain-warp континентов (бухты/полуострова).</summary>
    ContinentWarp = 0x9B1DE4F70A2C6835ul,
    /// <summary>Базовый fBm континентальной высоты.</summary>
    ContinentBase = 0x27FA5C31E8D09426ul,
    /// <summary>Тектонические плиты (Voronoi-сиды).</summary>
    ContinentPlate = 0xC41B7E90A3F5826Dul,
    /// <summary>Островные дуги/архипелаги на границах плит.</summary>
    ContinentArc = 0x6D2987B1F0E43A5Cul,
    /// <summary>Мелкая детализация рельефа суши.</summary>
    ContinentDetail = 0xE3F1508C7B92DA64ul,
    /// <summary>Океанские течения (тепло/холод у берега).</summary>
    OceanCurrent = 0x1A8C4E6215FDB907ul,
    /// <summary>Климатический шум (влажность/осадки).</summary>
    ClimateNoise = 0xF07B3C9E5421AD86ul,

    // --- Прибрежная геоморфология (бухты, заливы, мысы, гавани) ---
    /// <summary>Крупномасштабный domain-warp береговой линии (заливы, полуострова).</summary>
    CoastWarpMacro = 0x8C3B1E45920D47A1ul,
    /// <summary>Среднемасштабный domain-warp береговой линии (бухты, гавани, мысы).</summary>
    CoastWarpMeso = 0x51E2B7A9340C68DFul,
    /// <summary>Мелкомасштабный domain-warp береговой линии (линия прибоя, пляжи).</summary>
    CoastWarpMicro = 0x2A90F84CEB13D576ul,
    /// <summary>Особенности берега: мысы (Ridged) и защищенные бухты (Voronoi/Basin).</summary>
    CoastFeature = 0xDF815C730A249BE8ul
}

/// <summary>Стабильные целочисленные хеши/шум для world-coordinate генерации.</summary>
public static class CoordinateHash
{
    private const ulong GoldenRatio = 0x9E3779B97F4A7C15ul;
    private const ulong MixA = 0xBF58476D1CE4E5B9ul;
    private const ulong MixB = 0x94D049BB133111EBul;
    private const uint Q16One = ushort.MaxValue;

    /// <summary>
    /// Детерминированный domain-separated hash. Signed координаты смешиваются как их
    /// двухкомплементарное представление; все операции намеренно выполняются unchecked.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ulong worldSeed, uint generatorVersion, GenerationDomain domain,
        long worldX, long worldY, ulong lane = 0)
    {
        unchecked
        {
            ulong h = Mix(worldSeed ^ GoldenRatio);
            h = Mix(h ^ ((ulong)generatorVersion * GoldenRatio));
            h = Mix(h ^ (ulong)domain);
            h = Mix(h ^ (ulong)worldX);
            h = Mix(h ^ RotateLeft((ulong)worldY, 29));
            return Mix(h ^ (lane * MixA));
        }
    }

    /// <summary>Верхние 16 бит hash — стабильная величина Q0.16 в диапазоне 0..65535.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static ushort SampleQ16(ulong worldSeed, uint generatorVersion, GenerationDomain domain,
        long worldX, long worldY, ulong lane = 0)
        => (ushort)(Hash(worldSeed, generatorVersion, domain, worldX, worldY, lane) >> 48);

    /// <summary>Предвычисленный префикс хеша для домена (устраняет 8 Mix операций на каждый семпл шума).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static ulong GetDomainPrefix(ulong worldSeed, uint generatorVersion, GenerationDomain domain)
    {
        unchecked
        {
            ulong h = Mix(worldSeed ^ GoldenRatio);
            h = Mix(h ^ ((ulong)generatorVersion * GoldenRatio));
            return Mix(h ^ (ulong)domain);
        }
    }

    /// <summary>Быстрый семпл Q16 с предвычисленным доменным префиксом для lane 0.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static ushort SampleQ16WithPrefix(ulong domainPrefix, long worldX, long worldY)
    {
        unchecked
        {
            ulong h = Mix(domainPrefix ^ (ulong)worldX);
            h = Mix(h ^ RotateLeft((ulong)worldY, 29));
            return (ushort)(Mix(h) >> 48);
        }
    }

    /// <summary>
    /// Координатно-глобальный сглаженный value-noise Q0.16. Размер ячейки решётки
    /// должен быть положительным; lattice values адресуются хешем, без массива и RNG-порядка.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static ushort ValueNoiseQ16(ulong worldSeed, uint generatorVersion, GenerationDomain domain,
        long worldX, long worldY, int latticeScale)
    {
        if (latticeScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(latticeScale), "Масштаб решётки должен быть положительным.");

        long latticeX = WorldCoordinates.FloorDiv(worldX, latticeScale);
        long latticeY = WorldCoordinates.FloorDiv(worldY, latticeScale);
        uint tx = (uint)((long)WorldCoordinates.FloorMod(worldX, latticeScale) * Q16One / latticeScale);
        uint ty = (uint)((long)WorldCoordinates.FloorMod(worldY, latticeScale) * Q16One / latticeScale);
        uint sx = SmoothStepQ16(tx);
        uint sy = SmoothStepQ16(ty);

        ulong prefix = GetDomainPrefix(worldSeed, generatorVersion, domain);
        uint v00 = SampleQ16WithPrefix(prefix, latticeX, latticeY);
        uint v10 = SampleQ16WithPrefix(prefix, latticeX + 1, latticeY);
        uint v01 = SampleQ16WithPrefix(prefix, latticeX, latticeY + 1);
        uint v11 = SampleQ16WithPrefix(prefix, latticeX + 1, latticeY + 1);

        uint top = LerpQ16(v00, v10, sx);
        uint bottom = LerpQ16(v01, v11, sx);
        return (ushort)LerpQ16(top, bottom, sy);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value ^= value >> 30;
            value *= MixA;
            value ^= value >> 27;
            value *= MixB;
            value ^= value >> 31;
            return value;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ulong RotateLeft(ulong value, int count)
        => (value << count) | (value >> (64 - count));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint SmoothStepQ16(uint value)
    {
        ulong t = value;
        ulong t2 = (t * t + Q16One / 2u) / Q16One;
        ulong shaped = t2 * (3ul * Q16One - 2ul * t) + Q16One / 2u;
        return (uint)Math.Min(Q16One, shaped / Q16One);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint LerpQ16(uint a, uint b, uint t)
    {
        long delta = (long)b - a;
        long value = a + (delta * t + (delta >= 0 ? Q16One / 2 : -(long)Q16One / 2)) / Q16One;
        return (uint)Math.Clamp(value, 0L, Q16One);
    }
}
