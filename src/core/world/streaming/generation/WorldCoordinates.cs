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
    public static long FloorDiv(long value, int divisor)
    {
        if (divisor <= 0)
            throw new ArgumentOutOfRangeException(nameof(divisor), "Делитель должен быть положительным.");

        long quotient = value / divisor;
        long remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    /// <summary>Остаток в диапазоне [0, divisor); размер делителя обязан быть положительным.</summary>
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
    Temperature = 0xF1A2B3C4D5E6F708ul
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
    public static ushort SampleQ16(ulong worldSeed, uint generatorVersion, GenerationDomain domain,
        long worldX, long worldY, ulong lane = 0)
        => (ushort)(Hash(worldSeed, generatorVersion, domain, worldX, worldY, lane) >> 48);

    /// <summary>
    /// Координатно-глобальный сглаженный value-noise Q0.16. Размер ячейки решётки
    /// должен быть положительным; lattice values адресуются хешем, без массива и RNG-порядка.
    /// </summary>
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

        uint v00 = SampleQ16(worldSeed, generatorVersion, domain, latticeX, latticeY);
        uint v10 = SampleQ16(worldSeed, generatorVersion, domain, checked(latticeX + 1), latticeY);
        uint v01 = SampleQ16(worldSeed, generatorVersion, domain, latticeX, checked(latticeY + 1));
        uint v11 = SampleQ16(worldSeed, generatorVersion, domain,
            checked(latticeX + 1), checked(latticeY + 1));

        uint top = LerpQ16(v00, v10, sx);
        uint bottom = LerpQ16(v01, v11, sx);
        return (ushort)LerpQ16(top, bottom, sy);
    }

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

    private static ulong RotateLeft(ulong value, int count)
        => (value << count) | (value >> (64 - count));

    private static uint SmoothStepQ16(uint value)
    {
        ulong t = value;
        ulong t2 = (t * t + Q16One / 2u) / Q16One;
        ulong shaped = t2 * (3ul * Q16One - 2ul * t) + Q16One / 2u;
        return (uint)Math.Min(Q16One, shaped / Q16One);
    }

    private static uint LerpQ16(uint a, uint b, uint t)
    {
        long delta = (long)b - a;
        long value = a + (delta * t + (delta >= 0 ? Q16One / 2 : -(long)Q16One / 2)) / Q16One;
        return (uint)Math.Clamp(value, 0L, Q16One);
    }
}
