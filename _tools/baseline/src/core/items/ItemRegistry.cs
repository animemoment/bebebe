using System.Collections.Generic;

namespace Game.Core;

public static class ItemRegistry
{
    public static readonly ItemDefinition Log = new(
        ItemId.Log,
        "Бревно",
        0.85f,
        100,
        "uid://by88ysblfuqqu"
    );

    public static readonly ItemDefinition Grain = new(
        ItemId.Grain,
        "Зерно",
        0.15f,
        100,
        "uid://byln5m1aam7tg"
    );

    // Камень: тяжёлый (1.5кг — агент унесёт ~16 шт за раз при лимите 25кг),
    // спрайт — 128x128 атлас 2x2, кусок выбирается по варианту россыпи.
    // UID всего файла; конкретный квадрант режется кодом (AtlasTexture).
    public static readonly ItemDefinition Stone = new(
        ItemId.Stone,
        "Камень",
        1.5f,
        100,
        "uid://du6ur8mvtu3le"
    );

    private static readonly Dictionary<ItemId, ItemDefinition> _items = new()
    {
        { ItemId.Log, Log },
        { ItemId.Grain, Grain },
        { ItemId.Stone, Stone }
    };

    public static ItemDefinition Get(ItemId id) => _items[id];
}