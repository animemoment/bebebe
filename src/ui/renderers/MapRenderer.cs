using Godot;
using System;
using System.Collections.Generic;
using Game.Core;
using Game.UI.Tools;

public partial class MapRenderer : Node
{
    // ... [предыдущий код класса, до 100 строки] ...

    /// <summary>
    /// Создать TileSet для визуала инструмента (внутри острова).
    /// </summary>
    private TileSet CreateToolVisualTileSet(ToolVisualSpec.ToolVisual visual)
    {
        var tileSet = new TileSet { TileSize = new Vector2I(TileSizePx, TileSizePx) };
        var tex = LoadTexture(visual.TextureRef, visual.Key);
        if (tex == null)
        {
            GD.PrintErr("MapRenderer: текстура " + visual.Key + " не найдена!");
            return tileSet;
        }

        // Применяем тонировку (если есть)
        if (visual.TintA != 1f || visual.TintR != 1f || visual.TintG != 1f || visual.TintB != 1f)
        {
            tex.Modulate = new Color(visual.TintR, visual.TintG, visual.TintB, visual.TintA);
        }

        var src = new TileSetAtlasSource { Texture = tex, TextureRegionSize = new Vector2I(TileSizePx, TileSizePx) };

        // Создаём тайлы в зависимости от роли:
        // - Terrain: 1 тайл (автотайл)
        // - Overlay/Sprite: 1 тайл
        // - Plan: 6 тайлов (2×3 сетка для процесса работы)
        if (visual.Role == ToolVisualSpec.ToolVisualRole.Terrain)
        {
            src.CreateTile(new Vector2I(0, 0));
        }
        else if (visual.Role == ToolVisualSpec.ToolVisualRole.Plan)
        {
            // Для процесса работы — сетка 2×3 (как в оригинальном коде)
            for (int ty = 0; ty < 2; ty++)
                for (int tx = 0; tx < 3; tx++)
                    src.CreateTile(new Vector2I(tx, ty));
        }
        else
        {
            src.CreateTile(new Vector2I(0, 0));
        }

        // Добавляем источник в TileSet
        tileSet.AddSource(src, visual.Key);
        return tileSet;
    }

    /// <summary>
    /// Обновить визуал инструмента на острове.
    /// </summary>
    public void UpdateToolVisual(int x, int y, byte kind, BuildingType type = BuildingType.WoodWall)
    {
        if (x < 0 || y < 0 || x >= IslandWidth || y >= IslandHeight) return;

        // Определяем визуал по типу
        ToolVisualSpec.ToolVisual visual = kind switch
        {
            _ when kind == ToolVisualSpec.WorldKinds.CutTreePlan => ToolVisualSpec.ForPlan(kind),
            _ when kind == ToolVisualSpec.WorldKinds.MinedStonePlan => ToolVisualSpec.ForPlan(kind),
            _ when kind == ToolVisualSpec.WorldKinds.StockpilePlan => ToolVisualSpec.ForPlan(kind),
            _ when kind == ToolVisualSpec.WorldKinds.CropPlan => ToolVisualSpec.ForPlan(kind),
            _ when kind == ToolVisualSpec.WorldKinds.BuildingPlan => ToolVisualSpec.ForPlan(kind, type),
            _ when kind == ToolVisualSpec.WorldKinds.WallPlan => ToolVisualSpec.ForPlan(kind),
            _ when kind == ToolVisualSpec.WorldKinds.Stockpile => ToolVisualSpec.ForDone(kind),
            _ when kind == ToolVisualSpec.WorldKinds.Crop => ToolVisualSpec.ForDone(kind),
            _ when kind == ToolVisualSpec.WorldKinds.Building => ToolVisualSpec.ForDone(kind, type),
            _ when kind == ToolVisualSpec.WorldKinds.Wall => ToolVisualSpec.ForDone(kind),
            _ => ToolVisualSpec.ForDone(kind),
        };

        // Создаём TileSet для визуала
        var tileSet = CreateToolVisualTileSet(visual);

        // Обновляем тайлы на карте
        UpdateTile(x, y, visual.Key);
    }

    // ... [остальной код класса, после 100 строки] ...
}