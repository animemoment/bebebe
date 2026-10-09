using System;

namespace Game.Core.WorldStreaming.Layers;

/// <summary>
/// Локальная карта = «окно» в мир (план §2.2 п.25..§3.2): проекция WorldLayerStack
/// на детальную сетку без какого-либо отдельного состояния генерации.
/// Тайлы/объекты клетки выводятся из MacroCell + детерминированных бросков —
/// любые два окна, покрывающие одну клетку, дают идентичный результат.
/// </summary>
public static class LocalMapBuilder
{
    /// <summary>Конверсия world-координат в локальные: local = world − origin (без scale).</summary>
    public static (int LocalX, int LocalY) ToLocal(long worldX, long worldY, WorldCell origin)
        => ((int)(worldX - origin.X), (int)(worldY - origin.Y));

    public static (long WorldX, long WorldY) ToWorld(int localX, int localY, WorldCell origin)
        => (checked(origin.X + localX), checked(origin.Y + localY));

    /// <summary>Заполнить плотный массив MapData для произвольного bbox мира.</summary>
    public static void FillRegion(ulong seed, uint ver, int width, int height,
        long originX, long originY, Action<int, int, LocalCellSample> writeCell)
    {
        for (int ly = 0; ly < height; ly++)
        {
            long wy = checked(originY + ly);
            for (int lx = 0; lx < width; lx++)
            {
                long wx = checked(originX + lx);
                var sample = WorldLayerStack.SampleLocal(seed, ver, wx, wy);
                writeCell(lx, ly, sample);
            }
        }
    }

    /// <summary>
    /// Построить MapData окна [originX, originX+width) × [originY, originY+height).
    /// Ground/Tree/Stones отражают проекцию macro-слоёв; Humidity/Fertility — Q16-поля L2/L5.
    /// TreeVariant — из BiomeClassifier.Variant (0..3), как делал старый генератор.
    /// </summary>
    public static Core.MapData BuildMapData(ulong seed, uint ver, long originX, long originY,
        int width, int height)
    {
        var data = new Core.MapData(width, height);
        data.Seed = unchecked((uint)seed);
        FillRegion(seed, ver, width, height, originX, originY, (lx, ly, s) =>
        {
            var m = s.Macro;
            data.Ground[lx, ly] = s.IsWater
                ? Core.TileType.Water
                : m.Biome == BiomeType.Mountain
                    ? Core.TileType.Mountain
                    : Core.TileType.Grass;
            data.TreeOnGrass[lx, ly] = s.IsTree;
            if (s.IsTree)
                data.TreeVariant[lx, ly] = (byte)BiomeClassifier.Variant(seed, ver, lx + originX, ly + originY, m.Biome);
            data.StoneOnGrass[lx, ly] = s.IsStone;
            data.Humidity.Set(lx, ly, m.PrecipQ16);
            data.Fertility.Set(lx, ly, m.FertilityQ16);
        });
        return data;
    }

    /// <summary>
    /// Стартовая поляна R12 (план §3.2): локальный «сухой клин» вокруг спавна —
    /// трава без деревьев/камней. НЕ трогает воду мира: если мир в клетке предусмотрел
    /// океан/реку/озеро, вода остаётся (в отличие от старого ClearStartArea, который
    /// засыпал воду травой — план требует «поляна = локальный клин, а не глобальная подмена»).
    /// </summary>
    public static void ClearStartGlade(Core.MapData data, int localCx, int localCy, int radius)
    {
        if (data == null) return;
        int r2 = radius * radius;
        for (int x = Math.Max(0, localCx - radius); x <= Math.Min(data.Width - 1, localCx + radius); x++)
        {
            for (int y = Math.Max(0, localCy - radius); y <= Math.Min(data.Height - 1, localCy + radius); y++)
            {
                int dx = x - localCx, dy = y - localCy;
                if (dx * dx + dy * dy > r2) continue;
                data.Ground[x, y] = Core.TileType.Grass;
                data.TreeOnGrass[x, y] = false;
                data.StoneOnGrass[x, y] = false;
            }
        }
    }

    /// <summary>
    /// Playable-окно локальной карты (замена MapGenerator.GenerateRegion(isPlayableMap:true)):
    /// детерминировано ТОЛЬКО от (worldSeed, generatorVersion, центрального клетки мира).
    /// Никакого adjustedSeed от bbox — та же точка мира всегда даёт ту же локалку,
    /// и локалка совпадает с мировой картой («лес в мире → деревья в локале»).
    /// </summary>
    public static Core.MapData BuildPlayableWindow(ulong worldSeed, uint ver,
        long centerWorldX, long centerWorldY, int width, int height)
    {
        long originX = centerWorldX - width / 2;
        long originY = centerWorldY - height / 2;
        var data = BuildMapData(worldSeed, ver, originX, originY, width, height);
        // Поляна ровно по центру окна (= точка выбора региона игроком).
        ClearStartGlade(data, width / 2, height / 2, Core.MapGenerator.StartClearRadius);
        return data;
    }
}
