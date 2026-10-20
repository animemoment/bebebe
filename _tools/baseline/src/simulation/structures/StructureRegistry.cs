using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

public static class StructureRegistry
{
    private static readonly Dictionary<string, StructureDefinition> _byId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<BuildingType, StructureDefinition> _byType = new();

    public static StructureDefinition WoodWall { get; }
    public static StructureDefinition WorkTable { get; }
    public static StructureDefinition Bed { get; }
    public static StructureDefinition Bench { get; }
    public static StructureDefinition Nightstand { get; }

    static StructureRegistry()
    {
        WoodWall = Register(new StructureDefinition(
            id: "wood_wall",
            displayName: "Деревянная стена",
            type: BuildingType.WoodWall,
            category: StructureCategory.Wall,
            materials: new[] { (ItemId.Log, BuildConfig.LogsWall) },
            buildDurationSec: BuildConfig.BuildDurationSec,
            blocksMovement: true,
            castsShadow: true
        ));

        WorkTable = Register(new StructureDefinition(
            id: "work_table",
            displayName: "Верстак",
            type: BuildingType.WorkTable,
            category: StructureCategory.Production,
            materials: new[] { (ItemId.Log, BuildConfig.LogsWorkTable) },
            buildDurationSec: BuildConfig.BuildDurationSec,
            blocksMovement: false,
            castsShadow: true
        ));

        Bed = Register(new StructureDefinition(
            id: "bed",
            displayName: "Кровать",
            type: BuildingType.Bed,
            category: StructureCategory.Furniture,
            materials: new[] { (ItemId.Log, BuildConfig.LogsBed) },
            buildDurationSec: BuildConfig.BuildDurationSec,
            blocksMovement: false,
            castsShadow: false
        ));

        Bench = Register(new StructureDefinition(
            id: "bench",
            displayName: "Скамья",
            type: BuildingType.Bench,
            category: StructureCategory.Furniture,
            materials: new[] { (ItemId.Log, BuildConfig.LogsBench) },
            buildDurationSec: BuildConfig.BuildDurationSec,
            blocksMovement: false,
            castsShadow: false
        ));

        Nightstand = Register(new StructureDefinition(
            id: "nightstand",
            displayName: "Тумбочка",
            type: BuildingType.Nightstand,
            category: StructureCategory.Furniture,
            materials: new[] { (ItemId.Log, BuildConfig.LogsNightstand) },
            buildDurationSec: BuildConfig.BuildDurationSec,
            blocksMovement: false,
            castsShadow: false
        ));
    }

    public static StructureDefinition Register(StructureDefinition def)
    {
        if (def == null) throw new ArgumentNullException(nameof(def));
        _byId[def.Id] = def;
        _byType[def.Type] = def;
        return def;
    }

    public static StructureDefinition Get(BuildingType type)
    {
        if (_byType.TryGetValue(type, out var def))
            return def;
        return WoodWall;
    }

    public static StructureDefinition Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return WoodWall;
        if (_byId.TryGetValue(id, out var def))
            return def;
        return WoodWall;
    }

    public static bool TryGet(string id, out StructureDefinition def)
    {
        if (string.IsNullOrEmpty(id))
        {
            def = null;
            return false;
        }
        return _byId.TryGetValue(id, out def);
    }

    public static IEnumerable<StructureDefinition> All => _byId.Values;
}
