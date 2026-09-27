using Godot;
using Game.Core;

namespace Game.UI;

/// <summary>
/// Оверлей плодородия: один TextureRect 256×256 (1 тексель = 2×2 тайла),
/// растянутый на всю карту. Близнец HumidityOverlayRenderer, но зелёный:
/// бедное — почти белое, богатое — тёмно-зелёное. Каждый процент влияет.
/// Обновление — одной ImageTexture, троттлинг 1с (тик раз в 60 игровых минут).
/// +1 draw-call только когда слой видим (режим карты Fertility).
/// </summary>
public partial class FertilityOverlayRenderer : TextureRect
{
    private const int TexSize = 256;
    private const float RefreshIntervalSec = 1.0f;

    private ImageTexture _texture;
    private float _timer;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.TopLeft);
        Size = new Vector2(MapRenderer.MapWidth * MapRenderer.TileSizePx,
            MapRenderer.MapHeight * MapRenderer.TileSizePx);
        Position = Vector2.Zero;
        StretchMode = StretchModeEnum.Scale;
        TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
        MouseFilter = Control.MouseFilterEnum.Ignore;

        _texture = new ImageTexture();
        Texture = _texture;
    }

    private Image _reuseImg;

    /// <summary>Принудительно перестроить текстуру из карты плодородия.</summary>
    public void Refresh(FertilityMap fertility)
    {
        if (fertility == null || _texture == null)
            return;
        if (!Visible)
        {
            _timer = 0f;
            return;
        }
        int w = fertility.Width;
        int h = fertility.Height;
        if (w <= 0 || h <= 0)
            return;

        _reuseImg ??= Image.CreateEmpty(TexSize, TexSize, false, Image.Format.Rgba8);
        var img = _reuseImg;
        for (int ty = 0; ty < TexSize; ty++)
        {
            int mapY = ty * h / TexSize;
            for (int tx = 0; tx < TexSize; tx++)
            {
                int mapX = tx * w / TexSize;
                img.SetPixel(tx, ty, FertilityColor(fertility.Get(mapX, mapY)));
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
