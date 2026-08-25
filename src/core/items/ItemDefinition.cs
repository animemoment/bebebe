namespace Game.Core;

public enum ItemId : byte
{
    None = 0,
    Log = 1,   // Бревно
    Grain = 2  // Зерно / Пшеница
}

public record ItemDefinition(
    ItemId Id,
    string Name,
    float Weight,      // Вес 1 штуки в кг
    int MaxStack,      // Максимальный стак на 1 клетку (100)
    string TextureUid  // UID спрайта
);