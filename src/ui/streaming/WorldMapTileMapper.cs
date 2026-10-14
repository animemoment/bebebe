using System;
using Godot;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Маппинг биом-variant -> точный tileId для TileMapLayer мировой карты.
/// Соответствует сетке атласа ForWorldMap.png (16×16 px тайлы, 160×160 px):
///   col 0, rows 0-2 = Desert (3 варианта)      -> tileId 0, 1, 2
///   col 1, rows 0-2 = Steppe (3 варианта)      -> tileId 3, 4, 5
///   col 2, rows 0-2 = DeepWater (3 варианта)   -> tileId 6, 7, 8
///   col 3, row 0    = Mountain                 -> tileId 9
///   col 4, row 0    = Plains                   -> tileId 10
///   col 5, row 0    = Swamp / Forest 0         -> tileId 11
///   cols 6-9, row 0 = Forest 1..4 (всего 5 вар)-> tileId 12, 13, 14, 15
///   col 0, row 3    = Glacier / Ice            -> tileId 16
/// </summary>
public static class WorldMapTileMapper
{
	public const int TotalTileCount = 17;

	/// <summary>Получить координаты тайла в атласе ForWorldMap.png по индексу 0..16.</summary>
	public static Vector2I GetAtlasCoords(int tileId)
	{
		return tileId switch
		{
			0 => new Vector2I(0, 0), // Desert 0
			1 => new Vector2I(0, 1), // Desert 1
			2 => new Vector2I(0, 2), // Desert 2
			3 => new Vector2I(1, 0), // Steppe 0
			4 => new Vector2I(1, 1), // Steppe 1
			5 => new Vector2I(1, 2), // Steppe 2
			6 => new Vector2I(2, 0), // DeepWater 0
			7 => new Vector2I(2, 1), // DeepWater 1
			8 => new Vector2I(2, 2), // DeepWater 2
			9 => new Vector2I(3, 0), // Mountain
			10 => new Vector2I(4, 0), // Plains
			11 => new Vector2I(5, 0), // Swamp
			12 => new Vector2I(6, 0), // Forest 0
			13 => new Vector2I(7, 0), // Forest 1
			14 => new Vector2I(8, 0), // Forest 2
			15 => new Vector2I(9, 0), // Forest 3
			16 => new Vector2I(0, 3), // Glacier / Ice
			_ => new Vector2I(4, 0)
		};
	}

	/// <summary>
	/// Маппит биом и вариант (0..4) в конкретный TileId атласа ForWorldMap.png.
	/// Безопасен к любым отрицательным или большим значениям variant.
	/// </summary>
	public static int GetTileId(BiomeType biome, int variant)
	{
		int v = Math.Abs(variant) % 5;
		return biome switch
		{
			BiomeType.Desert => v switch
			{
				0 => 0,
				1 => 1,
				2 => 2,
				3 => 1,
				_ => 0
			},
			BiomeType.Steppe => v switch
			{
				0 => 3,
				1 => 4,
				2 => 5,
				3 => 4,
				_ => 3
			},
			BiomeType.DeepWater => v switch
			{
				0 => 6,
				1 => 7,
				2 => 8,
				3 => 7,
				_ => 6
			},
			BiomeType.Mountain => 9,
			BiomeType.Plains => 10,
			BiomeType.Swamp => 10,
			BiomeType.Forest => v switch
			{
				0 => 11, // col 5, row 0 (Forest 0)
				1 => 12, // col 6, row 0 (Forest 1)
				2 => 13, // col 7, row 0 (Forest 2)
				3 => 14, // col 8, row 0 (Forest 3)
				4 => 15, // col 9, row 0 (Forest 4)
				_ => 11
			},
			BiomeType.Glacier => 16,
			_ => 10
		};
	}
}
