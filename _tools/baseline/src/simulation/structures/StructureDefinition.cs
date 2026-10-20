using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

public sealed class StructureDefinition
{
    public string Id { get; }
    public string DisplayName { get; }
    public BuildingType Type { get; }
    public StructureCategory Category { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<(ItemId Item, int Count)> RequiredMaterials { get; }
    public float BuildDurationSec { get; }
    public bool BlocksMovement { get; }
    public bool CastsShadow { get; }
    public bool IsWall => Category == StructureCategory.Wall;

    public StructureDefinition(
        string id,
        string displayName,
        BuildingType type,
        StructureCategory category,
        IReadOnlyList<(ItemId Item, int Count)> materials,
        float buildDurationSec,
        bool blocksMovement,
        bool castsShadow,
        int width = 1,
        int height = 1)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = displayName ?? id;
        Type = type;
        Category = category;
        RequiredMaterials = materials ?? Array.Empty<(ItemId, int)>();
        BuildDurationSec = buildDurationSec;
        BlocksMovement = blocksMovement;
        CastsShadow = castsShadow;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    public int GetRequiredCount(ItemId item)
    {
        for (int i = 0; i < RequiredMaterials.Count; i++)
        {
            if (RequiredMaterials[i].Item == item)
                return RequiredMaterials[i].Count;
        }
        return 0;
    }

    public int TotalRequiredItems
    {
        get
        {
            int sum = 0;
            for (int i = 0; i < RequiredMaterials.Count; i++)
                sum += RequiredMaterials[i].Count;
            return sum;
        }
    }
}
