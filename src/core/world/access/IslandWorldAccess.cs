using System;

namespace Game.Core;

/// <summary>
/// Доступ к острову поверх плотного <see cref="MapData"/>. Поведение 1:1 с текущей игрой:
/// поверхность — из Ground, деревья/камни — из карт острова.
/// Блоки (стены, здания, посевы, склады) принадлежат менеджерам симуляции, поэтому их
/// владельцы подключаются делегатами: до этого HasBlock отвечает false, а Add/Remove — false
/// (без исключений: инструмент просто не поставит объект до подключения владельца, шаг 2 §26).
/// </summary>
public sealed class IslandWorldAccess : IWorldAccess
{
    private readonly MapData _map;

    public IslandWorldAccess(MapData map)
        => _map = map ?? throw new ArgumentNullException(nameof(map));

    /// <summary>Владелец блоков острова: запрос наличия (стены/здания/посевы/склады).</summary>
    public Func<byte, long, long, bool> BlockQuery { get; set; }

    /// <summary>Владелец блоков острова: постановка.</summary>
    public Func<byte, long, long, bool> BlockAdd { get; set; }

    /// <summary>Владелец блоков острова: снятие.</summary>
    public Func<byte, long, long, bool> BlockRemove { get; set; }

    /// <summary>Карты острова (для систем, которым нужен доступ к влаге/плодородию).</summary>
    public MapData Map => _map;

    public bool Covers(long cellX, long cellY)
        => cellX >= 0 && cellY >= 0 && cellX < _map.Width && cellY < _map.Height;

    public WorldTerrainKind TerrainAt(long cellX, long cellY)
    {
        Require(cellX, cellY);
        return _map.Ground[(int)cellX, (int)cellY] switch
        {
            TileType.Water => WorldTerrainKind.Water,
            TileType.Mountain => WorldTerrainKind.Mountain,
            _ => WorldTerrainKind.Grass
        };
    }

    public bool IsWaterAt(long cellX, long cellY) => TerrainAt(cellX, cellY) == WorldTerrainKind.Water;

    public bool HasTreeAt(long cellX, long cellY)
    {
        Require(cellX, cellY);
        // GetTreeVariant возвращает 0..3 и для клеток без дерева, поэтому смотрим карту напрямую.
        return _map.TreeOnGrass[(int)cellX, (int)cellY];
    }

    public bool HasStoneAt(long cellX, long cellY)
    {
        Require(cellX, cellY);
        return _map.HasStone((int)cellX, (int)cellY);
    }

    public bool HasBlock(byte kind, long cellX, long cellY)
        => Covers(cellX, cellY) && BlockQuery != null && BlockQuery(kind, cellX, cellY);

    public bool AddBlock(byte kind, long cellX, long cellY)
        => Covers(cellX, cellY) && BlockAdd != null && BlockAdd(kind, cellX, cellY);

    public bool RemoveBlock(byte kind, long cellX, long cellY)
        => Covers(cellX, cellY) && BlockRemove != null && BlockRemove(kind, cellX, cellY);

    private void Require(long cellX, long cellY)
    {
        if (!Covers(cellX, cellY))
            throw new ArgumentOutOfRangeException(
                nameof(cellX), $"клетка ({cellX},{cellY}) вне острова {_map.Width}×{_map.Height}");
    }
}
