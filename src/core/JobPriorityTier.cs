namespace Game.Core;

/// <summary>
/// Уровни системных приоритетов выполнения работ.
/// </summary>
public enum JobPriorityTier : byte
{
    Emergency = 0,       // Аварийная расчистка, устранение угроз
    BlueprintSupply = 1, // Доставка стройматериалов на активную стройку
    Construction = 2,    // Строительство
    Farming = 3,         // Сельское хозяйство
    TreeChopping = 4,    // Лесозаготовка
    StockpileHauling = 5,// Переноска на склады
    Low = 6              // Фоновые и второстепенные работы
}