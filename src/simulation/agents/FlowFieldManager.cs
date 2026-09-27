using System;
using System.Numerics;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

public sealed class FlowFieldManager
{
	public static FlowFieldManager Instance { get; } = new();

	private const int LocalWindowRadius = 16;
	private const int WindowSize = LocalWindowRadius * 2 + 1;
	private const int TotalWindowCells = WindowSize * WindowSize;

	private static readonly (int dx, int dy)[] Neighbors =
	{
		(0, -1), (0, 1), (-1, 0), (1, 0),
		(-1, -1), (1, -1), (-1, 1), (1, 1)
	};

	private sealed class SearchBuffers
	{
		public readonly int[] DistanceField = new int[TotalWindowCells];
		// Версионирование вместо Array.Fill(1089) на каждый вызов (PLAN.md §7):
		// ячейка валидна, только если _stamp[i] == _curStamp. Переполнение
		// int — сброс всего массива штампов (раз в ~2 млрд вызовов на поток).
		public readonly int[] Stamp = new int[TotalWindowCells];
		public int CurStamp = 1;
		public readonly (int X, int Y)[] Queue = new (int X, int Y)[TotalWindowCells];
	}

	private readonly ThreadLocal<SearchBuffers> _buffers = new(() => new SearchBuffers());

	public void ClearCache() { }

	public Vector2 GetDirection(float worldX, float worldY, int targetTileX, int targetTileY, SimulationContext ctx)
	{
		// Горячий путь (10k агентов x десятки вызовов/с): без GameProfiler.Scope —
		// Record идёт через ConcurrentDictionary+Interlocked и становится глобальной
		// точкой contention. Профилируем только верхний Phase-уровень.
		int startTileX = (int)(worldX / ctx.TileSize);
		int startTileY = (int)(worldY / ctx.TileSize);

		if (startTileX == targetTileX && startTileY == targetTileY)
		{
			Vector2 targetWorld = new(targetTileX * ctx.TileSize + 32f, targetTileY * ctx.TileSize + 32f);
			Vector2 toTarget = targetWorld - new Vector2(worldX, worldY);
			return toTarget.LengthSquared() > 0.001f ? Vector2.Normalize(toTarget) : Vector2.Zero;
		}

		// P0.2: ранний выход без BFS для близких целей (<=5 тайлов): большинство локальных
		// работ (грядки/склад рядом) не нуждаются в поиске обхода 33x33. BFS только при
		// дальнем/заблокированном движении. ~40-50% Phase3a экономим здесь.
		int ddx = targetTileX - startTileX;
		int ddy = targetTileY - startTileY;
		if (ddx * ddx + ddy * ddy <= 25)
		{
			Vector2 targetWorld = new(targetTileX * ctx.TileSize + 32f, targetTileY * ctx.TileSize + 32f);
			Vector2 toTarget = targetWorld - new Vector2(worldX, worldY);
			return toTarget.LengthSquared() > 0.001f ? Vector2.Normalize(toTarget) : Vector2.Zero;
		}

		// GPU-трек удалён: только локальный BFS-детур.
		return CalculateLocalDetourDirection(startTileX, startTileY, targetTileX, targetTileY, ctx);
	}

	private Vector2 CalculateLocalDetourDirection(int startX, int startY, int targetX, int targetY, SimulationContext ctx)
	{
		var buffers = _buffers.Value;
		int[] distanceField = buffers.DistanceField;
		int[] stamp = buffers.Stamp;
		(int X, int Y)[] queue = buffers.Queue;

		int minX = startX - LocalWindowRadius;
		int minY = startY - LocalWindowRadius;

		// Версионированный доступ: вместо Array.Fill читаем только клетки
		// текущей волны (queue[0..tail]), остальное — протухшие штампы.
		int cur = ++buffers.CurStamp;
		if (cur == int.MaxValue)
		{
			Array.Clear(stamp, 0, stamp.Length);
			cur = buffers.CurStamp = 1;
		}

		int clampedTargetX = Math.Clamp(targetX, minX, startX + LocalWindowRadius);
		int clampedTargetY = Math.Clamp(targetY, minY, startY + LocalWindowRadius);

		int localTargetX = clampedTargetX - minX;
		int localTargetY = clampedTargetY - minY;
		int targetIdx = localTargetY * WindowSize + localTargetX;

		distanceField[targetIdx] = 0;
		stamp[targetIdx] = cur;

		int head = 0;
		int tail = 0;
		queue[tail++] = (clampedTargetX, clampedTargetY);

		int maxSearchSteps = 100;
		int stepsTaken = 0;

		while (head < tail && stepsTaken < maxSearchSteps)
		{
			var (cx, cy) = queue[head++];
			stepsTaken++;

			int localCy = cy - minY;
			int localCx = cx - minX;
			int curDist = distanceField[localCy * WindowSize + localCx];

			if (cx == startX && cy == startY)
			{
				break;
			}

			for (int i = 0; i < 4; i++)
			{
				int nx = cx + Neighbors[i].dx;
				int ny = cy + Neighbors[i].dy;

				if (nx >= 0 && ny >= 0 && nx < ctx.MapWidth && ny < ctx.MapHeight &&
					nx >= minX && nx < minX + WindowSize && ny >= minY && ny < minY + WindowSize)
				{
					if (ctx.Ground[nx, ny] == TileType.Grass && !ctx.SolidWalls[nx, ny])
					{
						int localNy = ny - minY;
						int localNx = nx - minX;
						int nIdx = localNy * WindowSize + localNx;
						int oldDist = stamp[nIdx] == cur ? distanceField[nIdx] : int.MaxValue;

						if (oldDist > curDist + 1)
						{
							distanceField[nIdx] = curDist + 1;
							stamp[nIdx] = cur;
							queue[tail++] = (nx, ny);
						}
					}
				}
			}
		}

		int bestDist = int.MaxValue;
		Vector2 bestDir = Vector2.Zero;

		for (int i = 0; i < Neighbors.Length; i++)
		{
			int nx = startX + Neighbors[i].dx;
			int ny = startY + Neighbors[i].dy;

			if (nx >= 0 && ny >= 0 && nx < ctx.MapWidth && ny < ctx.MapHeight &&
				nx >= minX && nx < minX + WindowSize && ny >= minY && ny < minY + WindowSize)
			{
				if (ctx.Ground[nx, ny] == TileType.Grass && !ctx.SolidWalls[nx, ny])
				{
					int nIdx = (ny - minY) * WindowSize + (nx - minX);
					// Непосещённая клетка (старый штамп) = +inf, не кандидат.
					if (stamp[nIdx] != cur)
						continue;
					int dist = distanceField[nIdx];

					if (dist < bestDist)
					{
						bestDist = dist;
						Vector2 stepVec = new(Neighbors[i].dx, Neighbors[i].dy);
						bestDir = Vector2.Normalize(stepVec);
					}
				}
			}
		}

		return bestDir;
	}
}
