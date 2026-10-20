using System;

namespace Game.Simulation;

/// <summary>
/// Неизменяемый снимок состояния графа навигации и стоимостей тайлов.
/// Публикуется атомарно в HierarchicalPathfinder, гарантируя отсутствие гонок
/// и частично заполненных данных при обращении из пула рабочих потоков симуляции.
/// </summary>
public sealed class NavGridState
{
	public readonly int MapWidth;
	public readonly int MapHeight;
	public readonly int RegionDimX;
	public readonly int RegionDimY;
	public readonly byte[] Cost;
	public readonly int[] EdgeStart;
	public readonly int[] EdgeCount;
	public readonly int[] Portals;
	public readonly int PortalCount;

	public NavGridState(
		int mapWidth,
		int mapHeight,
		int regionDimX,
		int regionDimY,
		byte[] cost,
		int[] edgeStart,
		int[] edgeCount,
		int[] portals,
		int portalCount)
	{
		MapWidth = mapWidth;
		MapHeight = mapHeight;
		RegionDimX = regionDimX;
		RegionDimY = regionDimY;
		Cost = cost;
		EdgeStart = edgeStart;
		EdgeCount = edgeCount;
		Portals = portals;
		PortalCount = portalCount;
	}
}
