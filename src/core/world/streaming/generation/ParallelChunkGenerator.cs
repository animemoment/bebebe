using System;
using System.Threading.Tasks;

namespace Game.Core.WorldStreaming.Gpu;

/// <summary>
/// Параллельная генерация клеток чанка через <see cref="Parallel.For"/>.
///
/// Намеренно НЕ дублирует математику генератора: каждая клетка семплится тем же
/// <see cref="WorldChunkGenerator.SampleCell"/>, поэтому результат байт-в-байт
/// совпадает с последовательным путём (детерминизм по seed сохраняется).
/// Ускорение даёт только распараллеливание независимых клеток.
/// </summary>
public static class ParallelChunkGenerator
{
    /// <summary>
    /// Сгенерировать клетки чанка `Side × Side` параллельно, начиная с мирового origin
    /// (worldX, worldY). Порядок элементов row-major: index = localY * Side + localX.
    /// </summary>
    public static GeneratedCell[] GenerateCellsParallel(
        ulong seed, uint genVer, long worldX, long worldY)
    {
        var cells = new GeneratedCell[WorldChunk.CellCount];

        Parallel.For(0, WorldChunk.CellCount, i =>
        {
            int localX = i % WorldChunk.Side;
            int localY = i / WorldChunk.Side;
            long wx = checked(worldX + localX);
            long wy = checked(worldY + localY);
            cells[i] = WorldChunkGenerator.SampleCell(seed, genVer, wx, wy);
        });

        return cells;
    }
}
