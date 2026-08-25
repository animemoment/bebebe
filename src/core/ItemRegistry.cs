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

    private static readonly Dictionary<ItemId, ItemDefinition> _items = new()
    {
        { ItemId.Log, Log }
    };

    public static ItemDefinition Get(ItemId id) => _items[id];
}