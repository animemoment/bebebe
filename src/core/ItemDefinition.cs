namespace Game.Core;

public enum ItemId : byte
{
    None = 0,
    Log = 1 // Бревно (в будущем: Wheat, Fish, Plank, Stone и т.д.)
}

public record ItemDefinition(
    ItemId Id,
    string Name,
    float Weight,      // Вес 1 штуки в кг (0.85 кг для бревна)
    int MaxStack,      // Максимальный стак на 1 клетку (100)
    string TextureUid  // UID спрайта
);