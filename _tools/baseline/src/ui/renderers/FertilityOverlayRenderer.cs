using Godot;
using Game.Core;
using Game.Core.WorldStreaming.Integration;
using Game.Core.WorldStreaming;
using System;

namespace Game.UI;

/// <summary>
/// Оверлей плодородия: адаптивная текстура 128×128 с билинейной фильтрацией,
/// динамически следующая за видимым экраном игрока и бесшовно объединяющая
/// динамическую карту колонии 512×512 и бесконечный процедурный мир за ее пределами.
/// Близнец HumidityOverlayRenderer, но зелёный: бедное — почти белое, богатое — тёмно-зелёное.
/// Троттлинг 1с, ноль GC аллокаций в главном потоке.
/// </summary>
public partial class FertilityOverlayRenderer : TextureRect
{
    private const int TexSize = 128;
    private const float RefreshIntervalSec = 1.0f;

    private ImageTexture _texture;
    private float _timer;
    private Image _reuseImg;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.TopLeft);
        Size = new Vector2(MapRenderer.MapWidth * MapRenderer.TileSizePx,
            MapRenderer.MapHeight * MapRenderer.TileSizePx);
        Position = Vector2.Zero;
        StretchMode = StretchModeEnum.Scale;
        TextureFilter = CanvasItem.TextureFilterEnum.Linear;
        MouseFilter = Control.MouseFilterEnum.Ignore;

        _texture = new ImageTexture();
        Texture = _texture;
    }

    /// <summary>Принудительно перестроить текстуру из карты плодородия и стримингового мира.</summary>
    public void Refresh(FertilityMap fertility)
    {
        if (_texture == null)
            return;
        if (!Visible)
        {
            _timer = 0f;
            return;
        }

        int tileMinX = 0;
        int tileMinY = 0;
        int tileMaxX = MapRenderer.MapWidth;
        int tileMaxY = MapRenderer.MapHeight;

        var cam = GetViewport()?.GetCamera2D();
        if (cam != null)
        {
            Rect2 viewport = GetViewport().GetVisibleRect();
            float zoom = Math.Max(0.01f, cam.Zoom.X);
            float halfW = viewport.Size.X / (2f * zoom);
            float halfH = viewport.Size.Y / (2f * zoom);
            float minX = cam.Position.X - halfW;
            float minY = cam.Position.Y - halfH;
            float maxX = cam.Position.X + halfW;
            float maxY = cam.Position.Y + halfH;

            tileMinX = (int)MathF.Floor(minX / 64f) - 2;
            tileMinY = (int)MathF.Floor(minY / 64f) - 2;
            tileMaxX = (int)MathF.Ceiling(maxX / 64f) + 2;
            tileMaxY = (int)MathF.Ceiling(maxY / 64f) + 2;
        }

        int viewTilesW = Math.Max(1, tileMaxX - tileMinX);
        int viewTilesH = Math.Max(1, tileMaxY - tileMinY);

        Position = new Vector2(tileMinX * 64f, tileMinY * 64f);
        Size = new Vector2(viewTilesW * 64f, viewTilesH * 64f);

        _reuseImg ??= Image.CreateEmpty(TexSize, TexSize, false, Image.Format.Rgba8);
        var img = _reuseImg;
        ulong seed = UnifiedWorldGenerator.ActiveWorldSeed;
        int fertW = fertility?.Width ?? 0;
        int fertH = fertility?.Height ?? 0;

        for (int ty = 0; ty < TexSize; ty++)
        {
            int mapY = tileMinY + (ty * viewTilesH / TexSize);
            for (int tx = 0; tx < TexSize; tx++)
            {
                int mapX = tileMinX + (tx * viewTilesW / TexSize);
                int fert;
                if (fertility != null && (uint)mapX < (uint)fertW && (uint)mapY < (uint)fertH)
                {
                    fert = fertility.Get(mapX, mapY);
                }
                else
                {
                    var cell = WorldCellQuery.CellAt(seed, 3, mapX, mapY);
                    if (cell.Terrain == BaseTerrainKind.Water || cell.Terrain == BaseTerrainKind.Mountain)
                    {
                        fert = 0;
                    }
                    else
                    {
                        fert = (int)(cell.ForestQ16 * 150L / 65535) + 50;
                        if (fert > 200) fert = 200;
                    }
                }
                img.SetPixel(tx, ty, FertilityColor(fert));
            }
        }
        _texture.SetImage(img);
        _timer = 0f;
    }

    /// <summary>Обновление с троттлингом — звать каждый кадр, реально строит раз в 1с.</summary>
    public void RefreshThrottled(FertilityMap fertility, double delta)
    {
        _timer += (float)delta;
        if (_timer >= RefreshIntervalSec)
            Refresh(fertility);
    }

    /// <summary>
    /// Цвет по плодородию 0..200 (кламп сверху в тёмно-зелёный).
    /// Весь слой виден на 80% (alpha = 0.8): бедное — почти белое,
    /// богатое — тёмно-зелёное. Каждый процент влияет.
    /// </summary>
    public static Color FertilityColor(int fertility)
    {
        float t = fertility / 200f; // 0..1, всё что выше 1 → 1 (тёмно-зелёный)
        if (t > 1f) t = 1f;
        if (t < 0f) t = 0f;
        // Белый (1,1,1) → средний зелёный (0.45,0.85,0.4) → тёмно-зелёный (0,0.45,0.1).
        // Alpha всегда 0.8 — слой виден на 80%, как влажность.
        const float Alpha = 0.8f;
        float r, g, b;
        if (t <= 0.5f)
        {
            float k = t * 2f;
            r = 1f + (0.45f - 1f) * k;
            g = 1f + (0.85f - 1f) * k;
            b = 1f + (0.4f - 1f) * k;
        }
        else
        {
            float k = (t - 0.5f) * 2f;
            r = 0.45f * (1f - k);
            g = 0.85f + (0.45f - 0.85f) * k;
            b = 0.4f + (0.1f - 0.4f) * k;
        }
        return new Color(r, g, b, Alpha);
    }
}
