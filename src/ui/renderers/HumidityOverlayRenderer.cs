using Godot;
using Game.Core;

namespace Game.UI;

/// <summary>
/// Оверлей влажности: один TextureRect 256×256 (1 тексель = 2×2 тайла),
/// растянутый на всю карту. Слой виден на 80%: сухое — почти белое,
/// мокрое — тёмно-синий. Каждый процент влияет на цвет.
/// Обновление — одной ImageTexture, троттлинг 1с (тик раз в 30 игровых минут).
/// +1 draw-call только когда слой видим (режим карты Humidity).
/// </summary>
public partial class HumidityOverlayRenderer : TextureRect
{
    // Разрешение текстуры: 256² = 65k текселей, заливка ~0.1мс. 512² не нужно —
    // глаз разницу на полупрозрачной синеве не видит, а SetData в 4 раза дороже.
    private const int TexSize = 256;
    private const float RefreshIntervalSec = 1.0f;

    private ImageTexture _texture;
    private float _timer;

    public override void _Ready()
    {
        // Растянуть на всю карту 512×512×64px.
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

    // Переиспользуемый Image: SetImage 65k текселей раз в секунду и так дёшево,
    // но new Image каждый раз — лишний GC-пресс в главном потоке.
    private Image _reuseImg;

    /// <summary>Принудительно перестроить текстуру из карты влажности.</summary>
    public void Refresh(HumidityMap humidity)
    {
        if (humidity == null || _texture == null)
            return;
        // Невидимому слою текстуру не строим (режим Normal — зря жечь CPU).
        if (!Visible)
        {
            _timer = 0f;
            return;
        }
        int w = humidity.Width;
        int h = humidity.Height;
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
                img.SetPixel(tx, ty, MoistureColor(humidity.Get(mapX, mapY)));
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
