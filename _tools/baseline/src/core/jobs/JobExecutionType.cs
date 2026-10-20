namespace Game.Core;

/// <summary>
/// Базовые архетипы выполнения любых игровых задач.
/// </summary>
public enum JobExecutionType : byte
{
    Stationary = 0, // Пришел на место -> Прогресс работы -> Завершил
    Hauling = 1,    // Взял в точке Source -> Доставил в точку Target
    Patrol = 2      // Обход контрольных точек / Патруль
}