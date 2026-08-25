using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

public sealed class FarmZone
{
    public int Id { get; }
    public string Name { get; set; }
    public HashSet<(int X, int Y)> Tiles { get; }
    public ItemId RequiredSeedItem { get; set; } = ItemId.Grain;
    public bool AutoPlantEnabled { get; set; } = false;

    public int TotalTiles => Tiles.Count;
    public int RequiredSeedCount => Tiles.Count;
    public int PlantedCount { get; set; }

    public FarmZone(int id, string name, IEnumerable<(int X, int Y)> tiles)
    {
        Id = id;
        Name = name;
        Tiles = new HashSet<(int X, int Y)>(tiles);
    }
}