namespace Game.Core;

/// <summary>
/// Идентификаторы типов работ. Новые профессии добавляются сюда одной строкой.
/// </summary>
public enum JobTypeId : byte
{
    None = 0,
    TreeChopping = 1,
    Construction = 2,
    Farming = 3,
    BlueprintDelivery = 4,
    StockpileHauling = 5,
    ClearSite = 6,
    Crafting = 7,
    Mining = 8
}