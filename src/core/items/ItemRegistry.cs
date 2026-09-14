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

    private static readonly Dictionary<ItemId, ItemDefinition> _items = new()
    {
        { ItemId.Log, Log },
        { ItemId.Grain, Grain }
    };

    public static ItemDefinition Get(ItemId id) => _items[id];
}