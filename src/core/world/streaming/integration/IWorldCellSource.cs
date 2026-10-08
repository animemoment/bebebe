namespace Game.Core.WorldStreaming.Integration;

/// <summary>
/// Источник клеток бесконечного мира для единого доступа (§26.1): реализуется
/// <c>StreamingWorldView</c>, но интерфейс позволяет подставить фейк в тестах
/// и не тащить в ядро зависимость от узла Godot.
/// </summary>
public interface IWorldCellSource : IBlockWorld
{
    /// <summary>Чанк клетки загружен (resident).</summary>
    bool CoversChunk(long cellX, long cellY);

    /// <summary>Исходные поля клетки из resident-чанка. false — чанк не загружен.</summary>
    bool TryCellInfo(long cellX, long cellY, out GeneratedCell cell);
}
