using Godot;
using System;
using System.Collections.Generic;

namespace Game.UI;

/// <summary>
/// Тени мелких штук (предметы на земле, урожай, склад) — как у Syx ShadowBatch:
/// тот же силуэт рисуется второй раз, чёрным и растянутым вдоль солнца.
/// Геометрия пересчитывается каждый кадр в _Process в соответствии с текущим положением солнца,
/// а видимость и отсечение по камере динамически обновляются без задержек.
/// Поддерживает раздельные источники (земля, урожай, склад), исключая их взаимное вытеснение.
/// </summary>
public partial class ItemShadowRenderer : Node2D
{
    public const int MaxShadows = 16384;
    // Мелочь низкая: зерно/бревно/росток почти не отбрасывают.
    // Короткая тень вместо полноценной: длина солнца × 0.35, ширина × 0.5.
    private const float ItemLengthScale = 0.35f;
    private const float ItemWidthScale = 0.5f;
    private const float ItemAlphaScale = 0.5f;

    public enum ShadowSource
    {
        Default = 0,
        Ground = 1,
        Crop = 2,
        Stockpile = 3
    }

    public readonly struct ShadowSpot
    {
        public readonly float X;
        public readonly float Y;
        public readonly float SizePx;

        public ShadowSpot(float x, float y, float sizePx)
        {
            X = x;
            Y = y;
            SizePx = sizePx;
        }
    }

    private MultiMeshInstance2D _instance;
    private MultiMesh _multiMesh;
    private readonly float[] _buffer = new float[MaxShadows * 8];

    private readonly List<ShadowSpot>[] _sourceSpots = new List<ShadowSpot>[4]
    {
        new List<ShadowSpot>(4096),
        new List<ShadowSpot>(4096),
        new List<ShadowSpot>(4096),
        new List<ShadowSpot>(4096)
    };

    private DayNightCycle.SunState _sun;
    private bool _shadowOn;
    private Vector2 _viewCenter;
    private Vector2 _viewHalf;
    private bool _hasView;

    public const float ItemShadowZoomCutoff = 0.28f;

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
        var cam = GetViewport()?.GetCamera2D();
        float zoom = cam?.Zoom.X ?? 1f;
        bool zoomAllowed = zoom >= ItemShadowZoomCutoff;
        _shadowOn = zoomAllowed && sun.Alpha > 0.004f && sun.LengthPx >= 0.5f;
        UpdateView();
    }

    public void ClearSourceSpots(ShadowSource source)
    {
        int idx = (int)source;
        if (idx >= 0 && idx < _sourceSpots.Length)
        {
            _sourceSpots[idx].Clear();
        }
    }

    public void AddSourceSpots(ShadowSource source, System.Numerics.Vector2[] positions, int count, float sizePx)
    {
        if (positions == null || count <= 0) return;
        int idx = (int)source;
        if (idx < 0 || idx >= _sourceSpots.Length) return;

        var list = _sourceSpots[idx];
        int limit = Math.Min(count, positions.Length);
        for (int i = 0; i < limit; i++)
        {
            list.Add(new ShadowSpot(positions[i].X, positions[i].Y, sizePx));
        }
    }

    public void SetSourceSpots(ShadowSource source, System.Numerics.Vector2[] positions, int count, float sizePx)
    {
        ClearSourceSpots(source);
        AddSourceSpots(source, positions, count, sizePx);
    }

    /// <summary>Начать кадр: сбросить аккумулятор Default-спотов (обратная совместимость).</summary>
    public void BeginFrame()
    {
        ClearSourceSpots(ShadowSource.Default);
    }

    /// <summary>Слить аккумулятор на GPU немедленно (обратная совместимость).</summary>
    public void Flush()
    {
        UpdateAndRender();
    }

    /// <summary>
    /// Добавить тени из позиций предметов (уже в мировых px, центр спрайта).
    /// </summary>
    public int PushSpots(System.Numerics.Vector2[] positions, int count, float sizePx)
    {
        if (positions == null || count <= 0) return 0;
        AddSourceSpots(ShadowSource.Default, positions, count, sizePx);
        return Math.Min(count, positions.Length);
    }

    public override void _Process(double delta)
    {
        UpdateAndRender();
    }

    private void UpdateAndRender()
    {
        UpdateView();

        if (!_shadowOn || _multiMesh == null)
        {
            if (_multiMesh != null) _multiMesh.VisibleInstanceCount = 0;
            if (_instance != null) _instance.Visible = false;
            Visible = false;
            return;
        }

        float len = _sun.LengthPx * ItemLengthScale;
        float hx = _sun.Dir.X * len * 0.5f;
        float hy = _sun.Dir.Y * len * 0.5f;
        float px = -_sun.Dir.Y;
        float py = _sun.Dir.X;

        int visibleCount = 0;

        for (int s = 0; s < _sourceSpots.Length && visibleCount < MaxShadows; s++)
        {
            var list = _sourceSpots[s];
            if (list == null) continue;
            int c = list.Count;
            for (int i = 0; i < c && visibleCount < MaxShadows; i++)
            {
                var spot = list[i];
                float sizePx = spot.SizePx;
                float ax = spot.X;
                float ay = spot.Y + sizePx * 0.25f; // якорь у низа предмета

                if (!InView(ax, ay))
                    continue;

                float wdt = _sun.WidthScale * sizePx * 0.5f * ItemWidthScale;
                float qx = px * wdt * 0.5f;
                float qy = py * wdt * 0.5f;
                // Базис клина: длина вдоль солнца, ширина поперёк
                float bx = _sun.Dir.X * (len * 0.5f + sizePx * 0.25f);
                float by = _sun.Dir.Y * (len * 0.5f + sizePx * 0.25f);

                int idx = visibleCount * 8;
                _buffer[idx + 0] = (bx + _sun.Dir.X * sizePx * 0.25f) * 2f / sizePx;
                _buffer[idx + 1] = (by + _sun.Dir.Y * sizePx * 0.25f) * 2f / sizePx;
                _buffer[idx + 2] = 0f;
                _buffer[idx + 3] = ax + hx;
                _buffer[idx + 4] = qx * 2f / sizePx * 2f;
                _buffer[idx + 5] = qy * 2f / sizePx * 2f;
                _buffer[idx + 6] = 0f;
                _buffer[idx + 7] = ay + hy;

                visibleCount++;
            }
        }

        _multiMesh.VisibleInstanceCount = visibleCount;
        if (visibleCount > 0)
        {
            _multiMesh.Buffer = _buffer;
            Visible = true;
            if (_instance != null)
            {
                _instance.Visible = true;
                _instance.Modulate = new Color(0f, 0f, 0f, _sun.Alpha * ItemAlphaScale);
            }
        }
        else
        {
            Visible = false;
            if (_instance != null) _instance.Visible = false;
        }
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
