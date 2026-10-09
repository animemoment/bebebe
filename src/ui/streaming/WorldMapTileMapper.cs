using System;
using Godot;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Маппинг биом-variant → точный tileId для вставки в TileMapLayer.SetCell.
/// Соответствует сетке 10×3 атласа ForWorldMap.png (колонка→строка):
///   col 0 (Sand):     rows 0,1,2   → tileId 0,1,2
///   col 1 (Steppe):   rows 0,1,2   → tileId 3,4,5
///   col 2 (Water):    rows 0,1,2   → tileId 6,7,8
///   col 3 (Mountain): row 0        → tileId 9
///   col 4 (Plains):   row 0        → tileId 10
///   col 5 (Swamp):    row 0        → tileId 11
///   col 6-9 (Forest): rows 0       → tileId 12,13,14,15
/// </summary>
public static class WorldMapTileMapper
{
    public static int GetTileId(BiomeType biome, int variant)
    {
        return biome switch
        {
            BiomeType.Desert    => Math.Clamp(variant, 0, 2),
            BiomeType.Steppe    => 3 + Math.Clamp(variant, 0, 2),
            BiomeType.DeepWater => 6 + Math.Clamp(variant, 0, 2),
            BiomeType.Mountain  => 9,
            BiomeType.Plains    => 10,
            BiomeType.Forest    => 12 + Math.Clamp(variant, 0, 3),
            BiomeType.Swamp     => 11,
            // Новые биомы Уиттакера: снежных/хвойных атлас-тайлов нет — рендер существующими (план §4).
            BiomeType.Tundra    => 3 + Math.Clamp(variant, 0, 2),   // степной тайл (светлая трава)
            BiomeType.Taiga     => 12 + Math.Clamp(variant, 0, 3),  // лесной тайл
            BiomeType.Savanna   => 3 + Math.Clamp(variant, 0, 2),   // степной тайл
            BiomeType.Grassland => 10,                              // поляной тайл
            _                   => 10
        };
    }
}
