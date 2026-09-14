namespace Game.Core;

/// <summary>
/// Хранит данные сгенерированной карты.
/// Ground — тип поверхности (трава/вода).
/// TreeOnGrass — наличие дерева на клетке (только если Ground == Grass).
/// TreeVariant — вариант спрайта дерева 0..3 (tree/tree_1/tree_2/tree_3).
/// Seed — сид, которым карта была сгенерирована (для воспроизводимости/дебага).
/// </summary>
public sealed class MapData
{
    public TileType[,] Ground { get; }
    public bool[,] TreeOnGrass { get; }
    public byte[,] TreeVariant { get; }
    /// <summary>Почвенная влажность 0..200. Заливается генератором, живёт тиками.</summary>
    public HumidityMap Humidity { get; }
    public uint Seed { get; set; }
    public int LakeCount { get; set; }
    public int[] LakeSizes { get; set; } = System.Array.Empty<int>();
    public int RiverBranchCount { get; set; }
    public int MainRiverLength { get; set; }
    public float LandConnectivity { get; set; } = 1f;


    public int Width => Ground.GetLength(0);
    public int Height => Ground.GetLength(1);

    public MapData(int width, int height)
    {
        Ground = new TileType[width, height];
        TreeOnGrass = new bool[width, height];
        TreeVariant = new byte[width, height];
        Humidity = new HumidityMap(width, height);
    }

    /// <summary>
    /// Вариант дерева 0..3 для клетки. Безопасен к выходу за границы.
    /// </summary>
    public int GetTreeVariant(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return 0;
        return TreeVariant[x, y] & 3;
    }
}