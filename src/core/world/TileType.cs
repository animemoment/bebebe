namespace Game.Core;

/// <summary>
/// Типы тайлов поверхности на игровой карте.
/// Деревья вынесены в отдельный слой (MapData.TreeOnGrass).
/// </summary>
public enum TileType
{
    Grass,
    Water,
    /// <summary>Гора: непроходима (семантика стены, не воды).</summary>
    Mountain
}