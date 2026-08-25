namespace Game.Core;

/// <summary>
/// Хранит данные сгенерированной карты.
/// Ground — тип поверхности (трава/вода).
/// TreeOnGrass — наличие дерева на клетке (только если Ground == Grass).
/// </summary>
public sealed class MapData
{
    public TileType[,] Ground { get; }
    public bool[,] TreeOnGrass { get; }

    public int Width => Ground.GetLength(0);
    public int Height => Ground.GetLength(1);

    public MapData(int width, int height)
    {
        Ground = new TileType[width, height];
        TreeOnGrass = new bool[width, height];
    }
}