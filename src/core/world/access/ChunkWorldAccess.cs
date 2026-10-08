using System;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;

namespace Game.Core;

/// <summary>
/// Доступ к бесконечному миру поверх чанков. Клетка обслуживается только если её чанк
/// загружен: запись в выгруженный чанк возвращает false (инструмент должен сам решить —
/// подгрузить область или отказать, см. §26.3), а чтение поверхности бросает исключение,
/// чтобы «пустая» клетка не выглядела травой молча.
/// </summary>
public sealed class ChunkWorldAccess : IWorldAccess
{
    private readonly IWorldCellSource _cells;

    public ChunkWorldAccess(IWorldCellSource cells)
        => _cells = cells ?? throw new ArgumentNullException(nameof(cells));

    public bool Covers(long cellX, long cellY) => _cells.CoversChunk(cellX, cellY);

    public WorldTerrainKind TerrainAt(long cellX, long cellY)
    {
        GeneratedCell cell = Require(cellX, cellY);
        return cell.Terrain switch
        {
            BaseTerrainKind.Water => WorldTerrainKind.Water,
            BaseTerrainKind.Mountain => WorldTerrainKind.Mountain,
            _ => WorldTerrainKind.Grass
        };
    }

    public bool IsWaterAt(long cellX, long cellY) => TerrainAt(cellX, cellY) == WorldTerrainKind.Water;

    public bool HasTreeAt(long cellX, long cellY)
    {
        GeneratedCell cell = Require(cellX, cellY);
        return cell.Terrain == BaseTerrainKind.Grass
            && !_cells.HasBlock(WorldDeltaKind.CutTree, cellX, cellY)
            && cell.ForestQ16 >= WorldDecor.ForestMinQ16;
    }

    public bool HasStoneAt(long cellX, long cellY)
    {
        GeneratedCell cell = Require(cellX, cellY);
        return cell.Terrain == BaseTerrainKind.Grass
            && !_cells.HasBlock(WorldDeltaKind.MinedStone, cellX, cellY)
            && cell.StoneQ16 >= WorldDecor.StoneMinQ16;
    }

    public bool HasBlock(byte kind, long cellX, long cellY)
        => Covers(cellX, cellY) && _cells.HasBlock(kind, cellX, cellY);

    public bool AddBlock(byte kind, long cellX, long cellY)
        => Covers(cellX, cellY) && _cells.AddBlock(kind, cellX, cellY);

    public bool RemoveBlock(byte kind, long cellX, long cellY)
        => Covers(cellX, cellY) && _cells.RemoveBlock(kind, cellX, cellY);

    private GeneratedCell Require(long cellX, long cellY)
    {
        if (!_cells.TryCellInfo(cellX, cellY, out GeneratedCell cell))
            throw new InvalidOperationException($"чанк клетки ({cellX},{cellY}) не загружен");
        return cell;
    }
}
