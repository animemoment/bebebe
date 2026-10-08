namespace Game.Core.WorldStreaming;

/// <summary>Конечный базовый тип поверхности до динамических world features.</summary>
public enum BaseTerrainKind : byte
{
    Grass = 0,
    Water = 1,
    Mountain = 2
}

/// <summary>
/// Детерминированные исходные поля клетки. Поля Q0.16: ushort.MaxValue — почти 1.
/// Terrain включает статические реку/озеро после наложения feature-геометрии.
/// </summary>
public readonly record struct GeneratedCell(
    BaseTerrainKind Terrain,
    ushort ElevationQ16,
    ushort MoistureQ16,
    ushort ForestQ16,
    ushort StoneQ16,
    ushort TemperatureQ16);
