using Godot;
using System.Collections.Generic;

namespace Game.Core;

/// <summary>
/// Автотайлинг гор (mountains.png, атлас 5×5, blob).
/// Белый край = ОТКРЫТАЯ сторона (нет горы-соседа). Маска — 4 бита открытости:
/// OpenUp=1, OpenRight=2, OpenDown=4, OpenLeft=8 (инверсия маски связности).
/// Последний столбец (x=4) и строка (y=4) — пустые, не используются.
/// </summary>
public static class MountainTileHelper
{
    // openMask (0..15) -> atlas (x,y). По спецификации:
    // углы (0,0)/(3,0)/(0,3)/(3,3); края верх (1,0)/(2,0), низ (1,3)/(2,3),
    // лево (0,1)/(0,2), право (3,1)/(3,2); центр 2×2 (1,1),(2,1),(1,2),(2,2).
    // Коридоры (открыты 2 противоположные стороны) и 3-открытые маски
    // маппятся на ближайший край/центр; выбор варианта края/центра — по хэшу.
    private static readonly Vector2I[] TileMap = new Vector2I[16]
    {
        new(1, 1), // 0  : закрыт везде (центр; вариант выбирается хэшем в GetTile)
        new(1, 0), // 1  : открыт верх (верхний край)
        new(3, 1), // 2  : открыт право (правый край)
        new(3, 0), // 3  : открыт верх+право (верхний правый угол)
        new(1, 3), // 4  : открыт низ (нижний край)
        new(1, 1), // 5  : открыт верх+низ (вертикальный коридор — центр)
        new(3, 3), // 6  : открыт низ+право (нижний правый угол)
        new(0, 1), // 7  : закрыто только лево (тупик влево — левый край)
        new(0, 1), // 8  : открыт лево (левый край)
        new(0, 0), // 9  : открыт верх+лево (верхний левый угол)
        new(1, 1), // 10 : открыт лево+право (горизонтальный коридор — центр)
        new(1, 0), // 11 : закрыт только низ (одиночная связь вверх — верхний край)
        new(0, 3), // 12 : открыт низ+лево (нижний левый угол)
        new(0, 1), // 13 : закрыто только право (одиночная связь влево — левый край)
        new(1, 0), // 14 : закрыт только верх (тупик вверх — верхний край)
        new(0, 0), // 15 : одиночка (открыт везде)
    };

    public static Vector2I GetTile((int X, int Y) pos, HashSet<(int X, int Y)> mountains)
    {
        int open = 0;
        int x = pos.X;
        int y = pos.Y;

        if (!mountains.Contains((x, y - 1))) open |= 1; // Up
        if (!mountains.Contains((x + 1, y))) open |= 2; // Right
        if (!mountains.Contains((x, y + 1))) open |= 4; // Down
        if (!mountains.Contains((x - 1, y))) open |= 8; // Left

        return Resolve(open, x, y);
    }

    /// <summary>Перегрузка для MapRenderer: соседи читаются прямо из Ground, без HashSet.</summary>
    public static Vector2I GetTile(int x, int y, TileType[,] ground)
    {
        int open = 0;
        if (!IsMountain(x, y - 1, ground)) open |= 1;
        if (!IsMountain(x + 1, y, ground)) open |= 2;
        if (!IsMountain(x, y + 1, ground)) open |= 4;
        if (!IsMountain(x - 1, y, ground)) open |= 8;
        return Resolve(open, x, y);
    }

    /// <summary>
    /// Выбор варианта: закрытый центр (open=0) — один из 4 центральных по хэшу coords;
    /// края с 2 вариантами (верх/низ/лево/право) — по чётности перпендикулярной оси;
    /// углы и одиночка — детерминированы.
    /// </summary>
    private static Vector2I Resolve(int open, int x, int y)
    {
        switch (open)
        {
            case 0: // закрыт везде — 4 центральных
                return new Vector2I(1 + ((x & 1) != 0 ? 1 : 0), 1 + ((y & 1) != 0 ? 1 : 0));
            case 1: // верх — (1,0)/(2,0)
                return new Vector2I((x & 1) != 0 ? 2 : 1, 0);
            case 4: // низ — (1,3)/(2,3)
                return new Vector2I((x & 1) != 0 ? 2 : 1, 3);
            case 8: // лево — (0,1)/(0,2)
                return new Vector2I(0, (y & 1) != 0 ? 2 : 1);
            case 2: // право — (3,1)/(3,2)
                return new Vector2I(3, (y & 1) != 0 ? 2 : 1);
            default:
                return TileMap[open];
        }
    }

    /// <summary>Bounds-safe проверка горы.</summary>
    public static bool IsMountain(int x, int y, TileType[,] ground)
    {
        if (ground == null) return false;
        if ((uint)x >= (uint)ground.GetLength(0) || (uint)y >= (uint)ground.GetLength(1))
            return false;
        return ground[x, y] == TileType.Mountain;
    }
}
