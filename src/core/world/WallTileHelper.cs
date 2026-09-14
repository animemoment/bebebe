using Godot;
using System.Collections.Generic;

namespace Game.Core;

/// <summary>
/// Автотайлинг стен (wooden_wall, 448×448 = сетка 7×7 по 64px).
/// Источник истины — ручная terrain-разметка в Main.tscn (нода wood_wall_tile,
/// terrain_set "wood_wall", mode MatchCornersAndSides). Peering-биты из сцены
/// (стороны + углы), сгруппированы по маске сторон (Up=1, Right=2, Down=4, Left=8):
/// m0: (0,0);
/// m1(T): (1,6);
/// m2(R): (1,0),(0,3);
/// m4(B): (0,1);
/// m8(L): (6,1);
/// m5(B+T): (6,5),(3,1);
/// m10(R+L): (1,3),(5,6),(1,4:TL+TR);
/// m3(R+T): (0,5),(2,6:TR);
/// m6(R+B): (1,1);
/// m9(L+T): (6,6:TL),(5,5:TL);
/// m12(B+L): (5,0),(2,0:BL),(6,2:BL);
/// m7(R+B+T): (5,1),(0,4:TR);
/// m13(B+L+T): (6,3),(6,4),(7,4),(2,1:BL+TL),(4,1:BL+TL);
/// m11(R+L+T): (6,5),(4,6),(3,2:TR),(1,2:TL+TR);
/// m14(R+B+L): (1,5),(6,0:BR+BL);
/// m15(все): (2,2),(6,4),(2,4:TL),(4,2:BR+TL),(5,2:BR+BL),(3,4:BR+TR),
///   (5,4:BL+TL),(3,5:BL+TR),(4,5:TL+TR),(4,3:BR+BL+TR),(5,3:BL+TL+TR),
///   (4,4:все),(1,3:все).
/// Выбор: среди записей с той же маской сторон берётся та, чьи угловые биты
/// ⊆ фактических углов, с максимальным числом углов (скоринг движка);
/// равные варианты (визуальные дубли) — детерминированно по хэшу координат.
/// Используется для blueprint-слоя и слоя построенных стен.
/// Ghost-превью в BuildTool идёт через движковый SetCellsTerrainConnect
/// поверх TileSet из сцены (нода wood_wall_tile) — см. MapRenderer.
/// </summary>
public static class WallTileHelper
{
	// Биты углов: TR=1, TL=2, BR=4, BL=8.
	private const int TR = 1;
	private const int TL = 2;
	private const int BR = 4;
	private const int BL = 8;

	private readonly struct Entry
	{
		public readonly int Corners;
		public readonly Vector2I Atlas;
		public Entry(int corners, int x, int y) { Corners = corners; Atlas = new Vector2I(x, y); }
	}

	private static readonly Entry[] M0 = { new(0, 0, 0) };
	private static readonly Entry[] M1 = { new(0, 1, 6) };
	private static readonly Entry[] M2 = { new(0, 1, 0), new(0, 0, 3) };
	private static readonly Entry[] M4 = { new(0, 0, 1) };
	private static readonly Entry[] M8 = { new(0, 6, 1) };
	private static readonly Entry[] M5 = { new(0, 6, 5), new(0, 3, 1) };
	private static readonly Entry[] M10 = { new(0, 1, 3), new(0, 5, 6), new(TR | TL, 1, 4) };
	private static readonly Entry[] M3 = { new(0, 0, 5), new(TR, 2, 6) };
	private static readonly Entry[] M6 = { new(0, 1, 1) };
	private static readonly Entry[] M9 = { new(TL, 6, 6), new(TL, 5, 5) };
	private static readonly Entry[] M12 = { new(0, 5, 0), new(BL, 2, 0), new(BL, 6, 2) };
	private static readonly Entry[] M7 = { new(0, 5, 1), new(TR, 0, 4) };
	private static readonly Entry[] M13 = { new(0, 6, 3), new(0, 6, 4), new(0, 7, 4), new(BL | TL, 2, 1), new(BL | TL, 4, 1) };
	private static readonly Entry[] M11 = { new(0, 6, 5), new(0, 4, 6), new(TR, 3, 2), new(TL | TR, 1, 2) };
	private static readonly Entry[] M14 = { new(0, 1, 5), new(BR | BL, 6, 0) };
	private static readonly Entry[] M15 =
	{
		new(0, 2, 2), new(0, 6, 4),
		new(TL, 2, 4), new(BR | TL, 4, 2), new(BR | BL, 5, 2),
		new(BR | TR, 3, 4), new(BL | TL, 5, 4), new(BL | TR, 3, 5),
		new(TL | TR, 4, 5), new(BR | BL | TR, 4, 3), new(BL | TL | TR, 5, 3),
		new(TR | TL | BR | BL, 4, 4), new(TR | TL | BR | BL, 1, 3),
	};

	private static int PopCount(int v)
	{
		int c = 0;
		while (v != 0) { c += v & 1; v >>= 1; }
		return c;
	}

	/// <summary>
	/// Есть ли у клетки атласа terrain-разметка (используется ли хелпером).
	/// Для MapRenderer: пустые клетки в TileSet не создаём.
	/// </summary>
	public static bool IsAtlasTileUsed(int tx, int ty)
	{
		if ((uint)tx >= 7u || (uint)ty >= 7u) return false;
		Entry[][] groups = { M0, M1, M2, M4, M8, M5, M10, M3, M6, M9, M12, M7, M13, M11, M14, M15 };
		foreach (var g in groups)
			foreach (var e in g)
				if (e.Atlas.X == tx && e.Atlas.Y == ty) return true;
		return false;
	}

	public static Vector2I GetTile((int X, int Y) pos, HashSet<(int X, int Y)> walls)
	{
		int x = pos.X;
		int y = pos.Y;

		bool u = walls.Contains((x, y - 1));
		bool r = walls.Contains((x + 1, y));
		bool d = walls.Contains((x, y + 1));
		bool l = walls.Contains((x - 1, y));

		int mask = (u ? 1 : 0) | (r ? 2 : 0) | (d ? 4 : 0) | (l ? 8 : 0);

		int corners = 0;
		if (walls.Contains((x + 1, y - 1))) corners |= TR;
		if (walls.Contains((x - 1, y - 1))) corners |= TL;
		if (walls.Contains((x + 1, y + 1))) corners |= BR;
		if (walls.Contains((x - 1, y + 1))) corners |= BL;

		Entry[] group = mask switch
		{
			0 => M0, 1 => M1, 2 => M2, 4 => M4, 8 => M8, 5 => M5, 10 => M10,
			3 => M3, 6 => M6, 9 => M9, 12 => M12, 7 => M7, 13 => M13,
			11 => M11, 14 => M14, _ => M15,
		};

		// Скоринг как у движка: углы записи ⊆ фактических, максимум совпадений.
		// Ничья (визуальные дубли) — reservoir по хэшу координат, детерминированно.
		uint h = (uint)(x * 73856093 ^ y * 19349663 ^ mask * 83492791);
		Vector2I best = group[0].Atlas;
		int bestScore = -1;
		int ties = 0;
		for (int i = 0; i < group.Length; i++)
		{
			int need = group[i].Corners;
			if ((need & ~corners) != 0) continue;
			int score = PopCount(need);
			if (score > bestScore)
			{
				bestScore = score;
				best = group[i].Atlas;
				ties = 1;
			}
			else if (score == bestScore)
			{
				ties++;
				if (h % (uint)ties == 0) best = group[i].Atlas;
			}
		}
		return best;
	}
}
