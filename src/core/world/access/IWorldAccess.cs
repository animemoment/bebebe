namespace Game.Core;

/// <summary>Поверхность в мировых координатах — единая для острова и бесконечного мира.</summary>
public enum WorldTerrainKind : byte
{
    Grass = 0,
    Water = 1,
    Mountain = 2
}

/// <summary>
/// Единый доступ к миру в ГЛОБАЛЬНЫХ клеточных координатах (PLAN §26.1):
/// остров (плотный MapData, [0,width)×[0,height)) + бесконечный мир (чанки).
/// Инструменты, работы и поиск пути работают через этот интерфейс и не знают,
/// где проходит кромка острова — никаких клампов по MapRenderer.MapWidth.
/// </summary>
public interface IWorldAccess
{
    /// <summary>
    /// Обслуживает ли источник эту клетку: остров — всегда в своём прямоугольнике,
    /// бесконечный мир — только если чанк клетки загружен (resident).
    /// </summary>
    bool Covers(long cellX, long cellY);

    /// <summary>Поверхность клетки. Требует Covers(cellX, cellY) == true.</summary>
    WorldTerrainKind TerrainAt(long cellX, long cellY);

    /// <summary>Вода на клетке (непроходима). Требует Covers == true.</summary>
    bool IsWaterAt(long cellX, long cellY);

    /// <summary>Дерево на клетке (с учётом вырубленного). Требует Covers == true.</summary>
    bool HasTreeAt(long cellX, long cellY);

    /// <summary>Каменная россыпь на клетке (с учётом добытого). Требует Covers == true.</summary>
    bool HasStoneAt(long cellX, long cellY);

    /// <summary>Метка-блок вида kind на клетке (стена/здание/посев/склад/добыто/срублено).</summary>
    bool HasBlock(byte kind, long cellX, long cellY);

    /// <summary>Поставить блок. false — клетка не обслуживается или источник не умеет писать.</summary>
    bool AddBlock(byte kind, long cellX, long cellY);

    /// <summary>Убрать блок. false — клетка не обслуживается или блока не было.</summary>
    bool RemoveBlock(byte kind, long cellX, long cellY);
}
