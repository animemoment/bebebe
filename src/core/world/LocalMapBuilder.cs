using System;
using Game.Core.WorldLayers;

namespace Game.Core;

/// <summary>
/// Строитель игровой области («локальной карты») — окна произвольного размера
/// в единый мир WorldLayerStack. Мировая карта = источник истины: тайлы Ground,
/// деревья, камни, влажность и плодородие берутся из слоёв мира по абсолютным
/// координатам (seed, wx, wy). Никакого отдельного «островного» генератора.
/// </summary>
public static class LocalMapBuilder
{
    /// <summary>
    /// Строит MapData для прямоугольника [minX..maxX) × [minY..maxY) мировых клеток.
    /// playable=true — гарантии старта: поляна вокруг спавна, связность суши,
    /// при неудаче — сдвиг окна к ближайшей суше (поиск по кольцам).
    /// </summary>
    public static MapData Build(uint seed, int minX, int minY, int width, int height,
        bool playable = false, int spawnX = -1, int spawnY = -1)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Размеры игровой области должны быть > 0.");

        if (spawnX < 0) { spawnX = minX + width / 2; spawnY = minY + height / 2; }

        if (playable)
            EnsureLandAroundSpawn(seed, ref spawnX, ref spawnY);

        // Окно центрируем на спавне (если requested окно не содержит его — сдвигаем origin).
        int ox = minX, oy = minY;
        if (playable)
        {
            ox = spawnX - width / 2;
            oy = spawnY - height / 2;
        }

        var data = new MapData(width, height);
        data.Seed = seed;

        for (int y = 0; y < height; y++)
        {
            long wy = (long)oy + y;
            for (int x = 0; x < width; x++)
            {
                long wx = (long)ox + x;
                var s = WorldLayerStack.SampleLocal(seed, wx, wy);
                data.Ground[x, y] = s.Ground;
                data.TreeOnGrass[x, y] = s.Tree;
                data.StoneOnGrass[x, y] = s.Stone;
                if (s.Tree) data.TreeVariant[x, y] = (byte)(WorldNoise.HashNode(seed, 7, unchecked((int)wx), unchecked((int)wy)) % 4u);
                data.Humidity.Set(x, y, s.Humidity0200);
                data.Fertility.Set(x, y, s.Fertility0200);
            }
        }

        if (playable)
        {
            ClearStartArea(data, spawnX - ox, spawnY - oy, MapGenerator.StartClearRadius);
            data.LandConnectivity = ComputeLandConnectivity(data);
        }
        else
        {
            data.LandConnectivity = 1f;
        }

        return data;
    }

    /// <summary>Стартовая поляна R12: сухая расчистка без подмены мира — только снятие
    /// деревьев/камлей и, если мир поставил здесь гору, локальная замена на траву.</summary>
    private static void ClearStartArea(MapData data, int cx, int cy, int r)
    {
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                int x = cx + dx, y = cy + dy;
                if ((uint)x >= data.Width || (uint)y >= data.Height) continue;
                if (data.Ground[x, y] == TileType.Water) continue; // воду мир не отменяем
                data.TreeOnGrass[x, y] = false;
                data.StoneOnGrass[x, y] = false;
                if (data.Ground[x, y] == TileType.Mountain)
                    data.Ground[x, y] = TileType.Grass;
            }
    }

    /// <summary>Если спавн в воде/горах — ищем ближайшую сухую клетку (кольца до 40).</summary>
    private static void EnsureLandAroundSpawn(uint seed, ref int spawnX, ref int spawnY)
    {
        if (IsSpawnLand(seed, spawnX, spawnY)) return;
        for (int ring = 1; ring <= 40; ring++)
        {
            for (int a = 0; a < 8 * ring; a++)
            {
                int dx, dy;
                int side = a / ring;
                int t = a % ring;
                switch (side)
                {
                    case 0: dx = -ring; dy = -ring + t; break;
                    case 1: dx = -ring + t; dy = ring; break;
                    case 2: dx = ring; dy = ring - t; break;
                    default: dx = ring - t; dy = -ring; break;
                }
                if (IsSpawnLand(seed, spawnX + dx, spawnY + dy))
                {
                    spawnX += dx; spawnY += dy;
                    return;
                }
            }
        }
        // Не нашли — оставляем как есть (поляна всё равно расчистит горы).
    }

    private static bool IsSpawnLand(uint seed, int wx, int wy)
    {
        var biome = BiomeClassifier.Classify(seed, wx, wy);
        if (biome == BiomeType.DeepWater || biome == BiomeType.Swamp) return false;
        // Избегаем гор прямо на спавне — иначе поляна превратится в карьер.
        if (biome == BiomeType.Mountain &&
            ReliefLayer.Elevation(seed, wx, wy) > WorldLayerParams.MountainElevHi + 0.05f)
            return false;
        return true;
    }

    /// <summary>Доля largest-компоненты суши (BFS по 4-связности).</summary>
    public static float ComputeLandConnectivity(MapData data)
    {
        int w = data.Width, h = data.Height;
        var seen = new bool[w, h];
        int landTotal = 0, best = 0;
        var stack = new System.Collections.Generic.Stack<(int, int)>();
        for (int y0 = 0; y0 < h; y0++)
            for (int x0 = 0; x0 < w; x0++)
            {
                if (seen[x0, y0] || data.Ground[x0, y0] == TileType.Water) continue;
                int size = 0;
                stack.Push((x0, y0)); seen[x0, y0] = true;
                while (stack.Count > 0)
                {
                    var (x, y) = stack.Pop(); size++;
                    if (x > 0 && !seen[x - 1, y] && data.Ground[x - 1, y] != TileType.Water) { seen[x - 1, y] = true; stack.Push((x - 1, y)); }
                    if (x < w - 1 && !seen[x + 1, y] && data.Ground[x + 1, y] != TileType.Water) { seen[x + 1, y] = true; stack.Push((x + 1, y)); }
                    if (y > 0 && !seen[x, y - 1] && data.Ground[x, y - 1] != TileType.Water) { seen[x, y - 1] = true; stack.Push((x, y - 1)); }
                    if (y < h - 1 && !seen[x, y + 1] && data.Ground[x, y + 1] != TileType.Water) { seen[x, y + 1] = true; stack.Push((x, y + 1)); }
                }
                landTotal += size;
                if (size > best) best = size;
            }
        return landTotal == 0 ? 0f : best / (float)landTotal;
    }
}
