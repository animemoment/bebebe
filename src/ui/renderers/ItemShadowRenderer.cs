using Godot;
using System;

namespace Game.UI;

/// <summary>
/// Тени мелких штук (предметы на земле, урожай, склад) — как у Syx ShadowBatch:
/// тот же силуэт рисуется второй раз, чёрным и растянутым вдоль солнца.
/// Упрощение: силуэт = тёмный квад (не копия текстуры), высота у всех маленькая,
/// поэтому тень короткая. По Syx-правилам: рисуем только экран (InView),
/// ночью/в полдень скрываем целиком (early-out), буфер льём одним куском.
/// +1 draw-call на все мелкие тени разом. Только главный поток.
/// </summary>
public partial class ItemShadowRenderer : Node2D
{
    public const int MaxShadows = 4096;
    // Мелочь низкая: зерно/бревно/росток почти не отбрасывают.
    // Короткая тень вместо полноценной: длина солнца × 0.35, ширина × 0.5.
    private const float ItemLengthScale = 0.35f;
    private const float ItemWidthScale = 0.5f;
    private const float ItemAlphaScale = 0.5f;

    private MultiMeshInstance2D _instance;
    private MultiMesh _multiMesh;
    private readonly float[] _buffer = new float[MaxShadows * 8];

    private DayNightCycle.SunState _sun;
    private bool _shadowOn;
    private Vector2 _viewCenter;
    private Vector2 _viewHalf;
    private bool _hasView;

    public override void _Ready()
    {
        ZIndex = 3; // Под самими предметами (Z 5-6), над землёй.
        float mapPx = MapRenderer.MapWidth * MapRenderer.TileSizePx;
        _multiMesh = new MultiMesh
        {
            Mesh = new QuadMesh { Size = new Vector2(1f, 1f) },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = false,
            UseCustomData = false,
            InstanceCount = MaxShadows,
            VisibleInstanceCount = 0,
            CustomAabb = new Aabb(Godot.Vector3.Zero, new Godot.Vector3(mapPx, mapPx, 1000f))
        };
        _instance = new MultiMeshInstance2D
        {
            Name = "ItemShadows",
            Multimesh = _multiMesh,
            Modulate = new Color(0f, 0f, 0f, 0f)
        };
        AddChild(_instance);
        Visible = false;
    }

    /// <summary>Солнце на тик (O(1)). Звать из Main рядом с остальными тенями.</summary>
    public void SetSun(DayNightCycle.SunState sun)
    {
        _sun = sun;
        _shadowOn = sun.Alpha > 0.004f && sun.LengthPx >= 0.5f;
        Visible = _shadowOn;
        if (_instance != null)
        {
            _instance.Visible = _shadowOn;
            if (_shadowOn)
                _instance.Modulate = new Color(0f, 0f, 0f, sun.Alpha * ItemAlphaScale);
        }
        UpdateView();
    }

    // Аккумулятор за кадр: 3 источника (земля/урожай/склад) раньше каждый
    // перезаписывали один MultiMesh — виден был только последний тип.
    // Теперь каждый источник добавляет споты, Flush один раз в конце кадра.
    private int _accumulated;

    /// <summary>Начать кадр: сбросить аккумулятор (звать до первого PushSpots).</summary>
    public void BeginFrame()
    {
        _accumulated = 0;
    }

    /// <summary>Слить аккумулятор на GPU (звать после последнего PushSpots).</summary>
    public void Flush()
    {
        if (_multiMesh == null)
            return;
        _multiMesh.VisibleInstanceCount = _accumulated;
        if (_accumulated > 0)
            _multiMesh.Buffer = _buffer;
        bool show = _shadowOn && _accumulated > 0;
        Visible = show;
        if (_instance != null)
            _instance.Visible = show;
    }

    /// <summary>
    /// Добавить тени из позиций предметов (уже в мировых px, центр спрайта).
    /// sizePx — размер квада предмета (44 земля, 64 урожай, 28 склад).
    /// Возвращает число добавленных (обрезано экраном и капом). Upload НЕ делает —
    /// только Flush() в конце кадра. Между BeginFrame и Flush вызывать из главного потока.
    /// </summary>
    public int PushSpots(System.Numerics.Vector2[] positions, int count, float sizePx)
    {
        if (!_shadowOn || _multiMesh == null || positions == null || count <= 0)
        {
            if (_multiMesh != null)
                _multiMesh.VisibleInstanceCount = 0;
            if (_instance != null)
                _instance.Visible = false;
            Visible = false;
            return 0;
        }
        float len = _sun.LengthPx * ItemLengthScale;
        float wdt = _sun.WidthScale * sizePx * 0.5f * ItemWidthScale;
        float hx = _sun.Dir.X * len * 0.5f, hy = _sun.Dir.Y * len * 0.5f;
        float px = -_sun.Dir.Y, py = _sun.Dir.X;
        float qx = px * wdt * 0.5f, qy = py * wdt * 0.5f;
        // Базис клина: длина вдоль солнца, ширина поперёк (полуквад предмета как старт).
        float bx = _sun.Dir.X * (len * 0.5f + sizePx * 0.25f);
        float by = _sun.Dir.Y * (len * 0.5f + sizePx * 0.25f);
        int written = 0;
        int limit = Math.Min(count, positions.Length);
        for (int i = 0; i < limit && _accumulated < MaxShadows; i++)
        {
            float ax = positions[i].X;
            float ay = positions[i].Y + sizePx * 0.25f; // якорь у низа предмета
            if (!InView(ax, ay))
                continue;
            int idx = _accumulated * 8;
            _buffer[idx + 0] = (bx + _sun.Dir.X * sizePx * 0.25f) * 2f / sizePx;
            _buffer[idx + 1] = (by + _sun.Dir.Y * sizePx * 0.25f) * 2f / sizePx;
            _buffer[idx + 2] = 0f;
            _buffer[idx + 3] = ax + hx;
            _buffer[idx + 4] = qx * 2f / sizePx * 2f;
            _buffer[idx + 5] = qy * 2f / sizePx * 2f;
            _buffer[idx + 6] = 0f;
            _buffer[idx + 7] = ay + hy;
            _accumulated++;
            written++;
        }
        return written;
    }

    private void UpdateView()
    {
        var cam = GetViewport()?.GetCamera2D();
        if (cam == null || cam.Zoom.X <= 0f || cam.Zoom.Y <= 0f)
        {
            _hasView = false;
            return;
        }
        Vector2 vp = GetViewportRect().Size;
        _viewCenter = cam.GetScreenCenterPosition();
        _viewHalf = new Vector2(
            vp.X / cam.Zoom.X * 0.5f + DayNightCycle.MaxShadowLengthPx,
            vp.Y / cam.Zoom.Y * 0.5f + DayNightCycle.MaxShadowLengthPx);
        _hasView = true;
    }

    private bool InView(float x, float y)
    {
        if (!_hasView)
            return true;
        return Math.Abs(x - _viewCenter.X) <= _viewHalf.X
            && Math.Abs(y - _viewCenter.Y) <= _viewHalf.Y;
    }
}
