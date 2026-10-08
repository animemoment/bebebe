using System;

namespace Game.Core;

/// <summary>
/// Маршрутизация единого доступа: клетки [0,width)×[0,height) — остров (плотный MapData),
/// всё остальное — бесконечный мир (чанки). Это единственное место, где известна кромка:
/// инструменты и симуляция работают в глобальных координатах через этот фасад.
/// </summary>
public sealed class CompositeWorldAccess : IWorldAccess
{
    private readonly IWorldAccess _island;
    private readonly IWorldAccess _world;
    private readonly long _islandWidth;
    private readonly long _islandHeight;

    public CompositeWorldAccess(IWorldAccess island, IWorldAccess world, long islandWidth, long islandHeight)
    {
        _island = island ?? throw new ArgumentNullException(nameof(island));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        if (islandWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(islandWidth), islandWidth, "ширина острова должна быть > 0");
        if (islandHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(islandHeight), islandHeight, "высота острова должна быть > 0");
        _islandWidth = islandWidth;
        _islandHeight = islandHeight;
    }

    /// <summary>Клетка принадлежит острову (включая отрицательные — они всегда мир).</summary>
    public bool IsIslandCell(long cellX, long cellY)
        => cellX >= 0 && cellY >= 0 && cellX < _islandWidth && cellY < _islandHeight;

    /// <summary>Источник, обслуживающий клетку (остров или мир) — для диагностики и тестов.</summary>
    public IWorldAccess SourceFor(long cellX, long cellY) => IsIslandCell(cellX, cellY) ? _island : _world;

    public bool Covers(long cellX, long cellY) => SourceFor(cellX, cellY).Covers(cellX, cellY);

    public WorldTerrainKind TerrainAt(long cellX, long cellY)
        => SourceFor(cellX, cellY).TerrainAt(cellX, cellY);

    public bool IsWaterAt(long cellX, long cellY) => SourceFor(cellX, cellY).IsWaterAt(cellX, cellY);

    public bool HasTreeAt(long cellX, long cellY) => SourceFor(cellX, cellY).HasTreeAt(cellX, cellY);

    public bool HasStoneAt(long cellX, long cellY) => SourceFor(cellX, cellY).HasStoneAt(cellX, cellY);

    public bool HasBlock(byte kind, long cellX, long cellY)
        => SourceFor(cellX, cellY).HasBlock(kind, cellX, cellY);

    public bool AddBlock(byte kind, long cellX, long cellY)
        => SourceFor(cellX, cellY).AddBlock(kind, cellX, cellY);

    public bool RemoveBlock(byte kind, long cellX, long cellY)
        => SourceFor(cellX, cellY).RemoveBlock(kind, cellX, cellY);
}
