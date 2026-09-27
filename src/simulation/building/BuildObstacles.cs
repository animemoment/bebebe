using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// СПИСОК ПРЕПЯТСТВИЙ для стройки. Простыми словами:
/// стройка всегда идет в 3 шага: 1) расчистка, 2) поднос, 3) стройка.
/// Что именно чистим — записано здесь списком. Появится новый мусор
/// (кусты, снег, мох) — добавишь одну строчку в NeedsClear и одну
/// проверку в HasAny, остальное само подхватится (пайплайн, гейты, сверка).
/// </summary>
public static class BuildObstacles
{
    /// <summary>Виды препятствий. Новое — добавить сюда.</summary>
    public enum Kind : byte
    {
        Tree = 0,      // дерево на траве
        Stone = 1,     // камень-россыпь
        GroundItems = 2, // вещи лежат на земле
        GardenBed = 3,   // готовая грядка (рукотворная — сносить нельзя, стройку туда не ставим)
        MarkedPlot = 4,  // метка вскопки (чужая работа — ждем или отменяем, но не топчем)
        Building = 5,    // здание/мебель (не сносим, стройку туда не ставим)
        Blueprint = 6,   // чужой чертеж (не сносим, стройку туда не ставим)
    }

    /// <summary>
    /// Что надо РАСЧИЩАТЬ работой (по этим видам создаются работы этапа 1).
    /// Хочешь чистить что-то еще — добавь сюда.
    /// </summary>
    public static readonly Kind[] NeedsClear =
    {
        Kind.Tree,
        Kind.Stone,
        Kind.GroundItems,
    };

    /// <summary>
    /// Что ЗАПРЕЩАЕТ ставить стройку на клетку (не чистим, а просто не ставим).
    /// </summary>
    public static readonly Kind[] BlocksPlacement =
    {
        Kind.GardenBed,
        Kind.MarkedPlot,
        Kind.Building,
        Kind.Blueprint,
    };

    /// <summary>
    /// Есть ли на клетке хоть что-то из чистимого. Одна точка правды
    /// для пайплайна, инструментов и гардов хендлеров.
    /// </summary>
    public static bool HasAny(int x, int y, MapData map)
    {
        if (map == null) return false;
        if ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height)
            return false;
        if (map.Ground[x, y] != TileType.Grass)
            return true; // вода/гора — тоже препятствие
        if (map.TreeOnGrass[x, y])
            return true;
        if (map.HasStone(x, y))
            return true;
        if (GroundItemManager.Instance.HasItemsAt(x, y))
            return true;
        return false;
    }

    /// <summary>Клетка занята рукотворным (не чистим, стройку не ставим).</summary>
    public static bool IsBlockedByHandmade(int x, int y)
    {
        return FarmJobManager.Instance.IsGardenBed(x, y)
            || FarmJobManager.Instance.IsPlotMarked(x, y)
            || BuildingManager.Instance.HasBuildingAt(x, y)
            || BlueprintManager.Instance.IsBlueprintAt(x, y);
    }

    /// <summary>Можно ли ставить стройку на клетку (трава + без рукотворного).</summary>
    public static bool CanBuildAt(int x, int y, MapData map)
    {
        if (map == null)
            return false;
        if ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height)
            return false;
        if (map.Ground[x, y] != TileType.Grass)
            return false;
        if (map.HasStone(x, y))
            return false;
        if (IsBlockedByHandmade(x, y))
            return false;
        return true;
    }
}
