using Game.Core;

namespace Game.Simulation;

public sealed class StructureInstance
{
    public int X { get; }
    public int Y { get; }
    public StructureDefinition Definition { get; }
    public BuildingType Type => Definition.Type;
    public float Health { get; set; }
    public float MaxHealth { get; set; }
    public bool IsOperational { get; set; } = true;

    public StructureInstance(int x, int y, StructureDefinition definition, float maxHealth = 100f)
    {
        X = x;
        Y = y;
        Definition = definition;
        MaxHealth = maxHealth;
        Health = maxHealth;
    }
}
