using Godot;
using Game.Core;
using Game.Core.WorldStreaming.Integration;
using Game.Core.WorldStreaming;
using System;

namespace Game.UI;

/// <summary>
/// Оверлей влажности: адаптивная текстура 128×128 с билинейной фильтрацией,
/// динамически следующая за видимым экраном игрока и бесшовно объединяющая
/// динамическую карту колонии 512×512 и бесконечный процедурный мир за ее пределами.
/// Слой виден на 80%: сухое — почти белое, мокрое — тёмно-синий.
/// Троттлинг 1с, ноль GC аллокаций в главном потоке.
/// </summary>
public partial class HumidityOverlayRenderer : TextureRect
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

    /// <summary>Принудительно перестроить текстуру из карты влажности и стримингового мира.</summary>
    public void Refresh(HumidityMap humidity)
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
        int humW = humidity?.Width ?? 0;
        int humH = humidity?.Height ?? 0;

        for (int ty = 0; ty < TexSize; ty++)
        {
            int mapY = tileMinY + (ty * viewTilesH / TexSize);
            for (int tx = 0; tx < TexSize; tx++)
            {
                int mapX = tileMinX + (tx * viewTilesW / TexSize);
                int moisture;
                if (humidity != null && (uint)mapX < (uint)humW && (uint)mapY < (uint)humH)
                {
                    moisture = humidity.Get(mapX, mapY);
                }
                else
                {
                    ushort q16 = WorldCellQuery.MoistureQ16At(seed, 3, mapX, mapY);
                    moisture = (int)(q16 * 200L / 65535);
                }
                img.SetPixel(tx, ty, MoistureColor(moisture));
            }
        }
        _texture.SetImage(img);
        _timer = 0f;
    }

    /// <summary>Обновление с троттлингом — звать каждый кадр, реально строит раз в 1с.</summary>
    public void RefreshThrottled(HumidityMap humidity, double delta)
    {
        _timer += (float)delta;
        if (_timer >= RefreshIntervalSec)
            Refresh(humidity);
    }

    /// <summary>
    /// Цвет по влажности (верха нет — всё что ≥200 клампим в тёмно-синий).
    /// Весь слой виден на 80% (alpha = 0.8): сухое — почти белый,
    /// мокрое — тёмно-синий. Каждый процент влияет.
    /// </summary>
    public static Color MoistureColor(int moisture)
    {
        float t = moisture / 200f; // 0..1, всё что выше 1 → 1 (тёмно-синий)
        if (t > 1f) t = 1f;
        // Белый (1,1,1) → средний голубой (0.35,0.65,1.0) → тёмно-синий (0,0.15,0.55).
        // Alpha всегда 0.8 — слой виден на 80%.
        const float Alpha = 0.8f;
        float r, g, b;
        if (t <= 0.5f)
        {
            float k = t * 2f;
            r = 1f + (0.35f - 1f) * k;
            g = 1f + (0.65f - 1f) * k;
            b = 1f;
        }
        else
        {
            float k = (t - 0.5f) * 2f;
            r = 0.35f * (1f - k);
            g = 0.65f + (0.15f - 0.65f) * k;
            b = 1f + (0.55f - 1f) * k;
        }
        return new Color(r, g, b, Alpha);
    }
}
