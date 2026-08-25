namespace Game.Core;

/// <summary>
/// Идентификаторы типов работ.
/// </summary>
public enum JobTypeId : byte
{
    None = 0,
    TreeChopping = 1,
    Construction = 2,
    Farming = 3,            // Вспашка грядки
    BlueprintDelivery = 4,
    StockpileHauling = 5,
    ClearSite = 6,
    Crafting = 7,
    Mining = 8,
    Planting = 9,           // Посадка семян
    Harvesting = 10         // Сбор урожая
}