using System;

namespace Game.Core.WorldStreaming.Layers;

/// <summary>
/// Клетка мировой карты — полный выход конвейера WorldLayerStack (план §2).
/// Все значения — чистые функции от (seed, generatorVersion, абсолютные x, y);
/// ни одно поле не зависит от bbox, порядка загрузки или потока.
/// </summary>
public readonly record struct MacroCell(
    long X,
    long Y,
    ushort ElevationQ16,   // L0 рельеф
    ushort TemperatureQ16, // L1 температура (широта + lapse + шум)
    ushort PrecipQ16,      // L2 осадки (океан-близость + rain shadow + шум)
    ushort FlowQ16,        // L3 накопленный сток (priority-flood + D8 accumulation)
    bool IsRiver,          // L3 река (flow > порога)
    bool IsLake,           // L3 озеро (заполненная впадина)
    bool IsOcean,          // L0 elevation ниже уровня моря
    ushort StoneQ16,       // L4 геология (осыпь/ворли)
    ushort FertilityQ16,   // L5 почва (аллювий/вулканизм)
    ushort ForestQ16,      // L6 индекс растительности
    BiomeType Biome);      // L7 классификатор Уиттакера

/// <summary>Локальная клетка — «окно» мира на детальной сетке (план §2, §3.2).</summary>
public readonly record struct LocalCellSample(
    MacroCell Macro,
    bool IsWater,     // вода тайла = ocean || river || lake
    bool IsTreeRoll,  // бросок деревьев (сравнение hash < forest·k_biome)
    bool IsStoneRoll);// бросок камней из L4
