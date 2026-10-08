namespace Game.UI.Tools;

/// <summary>
/// Возможности инструмента в БЕСКОНЕЧНОМ МИРЕ (PLAN §26 шаг 2, §28): клетки за кромкой
/// острова ставятся прямо в мир (метки-блоки), без островных менеджеров и пайплайнов.
/// Реализуется адаптером над StreamingWorldManager + WorldGameRules.
/// </summary>
public interface IToolWorldPlacement
{
    /// <summary>Клетка вне острова и её чанк загружен — можно ставить/снимать.</summary>
    bool IsWorldCell(long cellX, long cellY);

    /// <summary>
    /// Пройдёт ли зона склада на этой клетке мира. Нужен черновику зон: ghost показывает
    /// ровно то, что встанет на Commit (иначе превью обещает клетки, которые молча отсеются).
    /// </summary>
    bool CanPlaceStockpile(long cellX, long cellY);

    /// <summary>Пройдёт ли зона посева на этой клетке мира (трава + влажность + клетка свободна).</summary>
    bool CanPlaceFarm(long cellX, long cellY);

    bool TryPlaceWall(long cellX, long cellY);

    /// <summary>Пройдёт ли стена гейт мира (трава, чанк загружен, клетка свободна) — для ghost.</summary>
    bool CanPlaceWall(long cellX, long cellY);

    bool TryRemoveWall(long cellX, long cellY);

    bool TryPlaceStockpile(long cellX, long cellY);

    bool TryRemoveStockpile(long cellX, long cellY);

    /// <summary>Клетка зоны фермы в мире (метка посева).</summary>
    bool TryPlaceFarmPlot(long cellX, long cellY);

    bool TryRemoveFarmPlot(long cellX, long cellY);

    bool TryPlaceBuilding(long cellX, long cellY);

    bool TryRemoveBuilding(long cellX, long cellY);

    bool TryCutTree(long cellX, long cellY);

    bool TryCancelCutTree(long cellX, long cellY);

    bool TryMineStone(long cellX, long cellY);

    bool TryCancelMineStone(long cellX, long cellY);
}
