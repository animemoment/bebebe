using Godot;
using Game.Core;
using Game.Simulation;
using System;
using System.Collections.Generic;

namespace Game.UI;

/// <summary>
/// Фейковые направленные тени статики клином от ствола: один MultiMesh,
/// текстура-клин 32x64 (узко и плотно у основания, широко и прозрачно к концу).
/// Якорь инстанса = основание кастера (ствол/низ стены), матрица = R(dir)*S(len,width).
/// Позиции статичны: буфер перезаливается только при смене солнца или Add/Remove.
/// +1 draw-call. Ночью/в полдень скрывается целиком. Только главный поток.
/// </summary>
public partial class ShadowCasterRenderer : Node2D
{
    public const int MaxCasters = 65536;
    private const int WedgeTexW = 32;
    private const int WedgeTexH = 64;

    // Масштабы статики относительно солнца: в плотных рощах сотни клиньев
    // перекрываются и альфа складывается в сплошное чёрное пятно размером с рощу.
    // Короче/уже/прозрачнее => читаемая направленная тень вместо blobs.
    private const float StaticLengthScale = 0.65f;
    private const float StaticWidthScale = 0.7f;
    private const float StaticAlphaScale = 0.45f;

    private MultiMeshInstance2D _instance;
    private MultiMesh _multiMesh;
    private readonly float[] _buffer = new float[MaxCasters * 8];
    private readonly List<Vector2> _anchors = new(4096);
    private readonly HashSet<(int X, int Y)> _cells = new(4096);

    private DayNightCycle.SunState _current;
    private bool _hasState;
    private bool _hasSun;

    public override void _Ready()
    {
        ZIndex = 4;

        // Квад единичный: реальный размер задаёт матрица инстанса (R*S от якоря).
        var quad = new QuadMesh { Size = new Vector2(1f, 1f) };
        float mapPx = MapRenderer.MapWidth * MapRenderer.TileSizePx;
        _multiMesh = new MultiMesh
        {
            Mesh = quad,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = false,
            UseCustomData = false,
            InstanceCount = MaxCasters,
            VisibleInstanceCount = 0,
            CustomAabb = new Aabb(Godot.Vector3.Zero, new Godot.Vector3(mapPx, mapPx, 1000f))
        };
        _instance = new MultiMeshInstance2D
        {
            Name = "StaticShadows",
            Multimesh = _multiMesh,
            Texture = CreateWedgeTexture(),
            Modulate = new Color(0f, 0f, 0f, 0f)
        };
        AddChild(_instance);
        Visible = false;
    }

    public void RebuildStatic(MapData map, HashSet<(int X, int Y)> walls, Dictionary<(int X, int Y), BuildingType> buildings)
    {
        _anchors.Clear();
        _cells.Clear();
        if (map != null)
        {
            float tile = MapRenderer.TileSizePx;
            for (int x = 0; x < map.Width; x++)
                for (int y = 0; y < map.Height; y++)
                {
                    if (map.Ground[x, y] == TileType.Grass && map.TreeOnGrass[x, y])
                        AddCell(x, y, tile);
                }
        }
        if (walls != null)
        {
            float tile = MapRenderer.TileSizePx;
            foreach (var (x, y) in walls)
                AddCell(x, y, tile);
        }
        if (buildings != null)
        {
            float tile = MapRenderer.TileSizePx;
            foreach (var (x, y) in buildings.Keys)
                AddCell(x, y, tile);
        }
        PushBuffer();
    }

    public void AddCaster(int tx, int ty)
    {
        float tile = MapRenderer.TileSizePx;
        if (AddCell(tx, ty, tile))
        {
            _bufferDirty = true;
            _bufferTimer = 0f;
        }
    }

    public void RemoveCaster(int tx, int ty)
    {
        if (!_cells.Remove((tx, ty)))
            return;
        _anchors.Clear();
        float tile = MapRenderer.TileSizePx;
        foreach (var (cx, cy) in _cells)
            _anchors.Add(BasePoint(cx, cy, tile));
        _bufferDirty = true;
        _bufferTimer = 0f;
    }

    // П.2: дебаунс PushBuffer — при массовой рубке/стройке сотни Add/Remove за тик
    // только помечают dirty, тяжёлая перезаливка MultiMesh идёт в Tick троттлингом.
    private bool _bufferDirty;
    private float _bufferTimer;
    private const float BufferDebounceSec = 0.15f;

    public const float StaticShadowZoomCutoff = 0.25f;

    // Дебаунс заливки геометрии солнца: 2 МБ буфер обновляется максимум ~12 раз/с,
    // а альфа обновляется каждый кадр через быстрый Modulate без перезаливки геометрии.
    private float _sunGeoTimer;
    private const float SunGeoDebounceSec = 0.08f;

    public void Tick(DayNightCycle.SunState sun, float realDeltaSec = 0.016f)
    {
        var cam = GetViewport()?.GetCamera2D();
        float zoom = cam?.Zoom.X ?? 1f;
        bool zoomAllowed = zoom >= StaticShadowZoomCutoff;

        bool show = zoomAllowed && sun.Alpha > 0.004f && sun.LengthPx >= 0.5f && _anchors.Count > 0;
        _hasSun = show;
        Visible = show;
        if (_instance != null)
        {
            _instance.Visible = show;
            if (!show && _multiMesh != null)
                _multiMesh.VisibleInstanceCount = 0;
        }
        if (!show)
            return;

        // Плавная смена атмосферного цвета и альфы через свойство Modulate (ноль нагрузки на GPU шину)
        if (_instance != null)
        {
            Color c = sun.ShadowColor;
            _instance.Modulate = new Color(c.R, c.G, c.B, sun.Alpha * StaticAlphaScale);
        }

        // Геометрическое изменение длины и направления солнца с защитой от спайков
        _sunGeoTimer += realDeltaSec;
        bool sunGeoChanged = !_hasState
            || Math.Abs(_current.LengthPx - sun.LengthPx) >= 1.5f
            || (_current.Dir - sun.Dir).LengthSquared() >= 0.002f;

        if (sunGeoChanged && (!_hasState || _sunGeoTimer >= SunGeoDebounceSec))
        {
            _current = sun;
            _hasState = true;
            _sunGeoTimer = 0f;
            PushBuffer();
            _bufferDirty = false;
            _bufferTimer = 0f;
            return;
        }

        // Солнце стабильно — сливаем накопленные Add/Remove дебаунсом 150мс.
        if (_bufferDirty)
        {
            _bufferTimer += realDeltaSec;
            if (_bufferTimer >= BufferDebounceSec)
            {
                _bufferDirty = false;
                _bufferTimer = 0f;
                PushBuffer();
            }
        }
    }

    // Совместимость со старым вызовом Tick(offset, alpha): длина = |offset|, dir = norm(offset).
    public void Tick(Vector2 sunOffset, float sunAlpha)
    {
        float len = sunOffset.Length();
        Vector2 dir = len > 0.001f ? sunOffset / len : Vector2.Zero;
        Tick(new DayNightCycle.SunState(dir, len, DayNightCycle.ShadowWidthScale, sunAlpha));
    }

    private static Vector2 BasePoint(int tx, int ty, float tile)
    {
        // Якорь = основание кастера: центр по X, низ тайла по Y (ствол/стена стоит на земле).
        return new Vector2(tx * tile + tile * 0.5f, ty * tile + tile);
    }

    private bool AddCell(int tx, int ty, float tile)
    {
        if (!_cells.Add((tx, ty)))
            return false;
        _anchors.Add(BasePoint(tx, ty, tile));
        return true;
    }

    private void PushBuffer()
    {
        if (_multiMesh == null)
            return;
        Vector2 dir = _hasState ? _current.Dir : Vector2.Zero;
        float rawLen = _hasState ? _current.LengthPx : 0f;
        // Порядок Buffer для Transform2D: x.x, x.y, pad, origin.x, y.x, y.y, pad, origin.y.
        // Квад единичный (±0.5): базис = мировые полуоси (len/2 вдоль солнца, wdt/2 поперёк).
        // Origin сдвинут вперёд на len/2 — тень начинается у якоря (ствол), а не центрируется на нём.
        float wdt = _hasState ? _current.WidthScale * MapRenderer.TileSizePx * 0.5f * StaticWidthScale : 0f;
        float len = rawLen * StaticLengthScale;
        float hx = dir.X * len * 0.5f, hy = dir.Y * len * 0.5f;
        float px = -dir.Y, py = dir.X;
        float qx = px * wdt * 0.5f, qy = py * wdt * 0.5f;
        int limit = Math.Min(_anchors.Count, MaxCasters);
        for (int i = 0; i < limit; i++)
        {
            Vector2 anchor = _anchors[i];
            int idx = i * 8;
            _buffer[idx + 0] = hx * 2f;
            _buffer[idx + 1] = hy * 2f;
            _buffer[idx + 2] = 0f;
            _buffer[idx + 3] = anchor.X + hx;
            _buffer[idx + 4] = qx * 2f;
            _buffer[idx + 5] = qy * 2f;
            _buffer[idx + 6] = 0f;
            _buffer[idx + 7] = anchor.Y + hy;
        }
        _multiMesh.VisibleInstanceCount = limit;
        if (limit > 0)
            _multiMesh.Buffer = _buffer;
        bool show = _hasSun && limit > 0;
        Visible = show;
        if (_instance != null)
            _instance.Visible = show;
    }

    /// <summary>
    /// Силуэтная текстура тени дерева 32x64: основание у ствола (низ, v < 0.22) узкое,
    /// затем раскрывается выраженная округлая крона дерева (v 0.22..0.95)
    /// с мягким спаданием альфы (penumbra) по краям.
    /// </summary>
    private static ImageTexture CreateWedgeTexture()
    {
        var img = Image.CreateEmpty(WedgeTexW, WedgeTexH, false, Image.Format.Rgba8);
        img.Fill(new Color(0f, 0f, 0f, 0f));
        for (int y = 0; y < WedgeTexH; y++)
        {
            // v: 0 у основания (низ текстуры, y=max) -> 1 на конце (верх, y=0).
            float v = 1f - (y + 0.5f) / WedgeTexH;
            
            // Силуэт дерева: ствол у основания, переходящий в круглую крону
            float halfW;
            if (v < 0.22f)
            {
                // Ствол у основания
                halfW = 0.08f + v * 0.15f;
            }
            else
            {
                // Крона: эллиптическая выпуклость кроны дерева
                float crownT = (v - 0.22f) / (1f - 0.22f); // 0..1
                float crownCurve = Mathf.Sin(crownT * Mathf.Pi);
                halfW = 0.12f + crownCurve * 0.36f;
            }

            // Альфа плавно спадает к макушке кроны
            float a = Mathf.Clamp(1f - v * 0.70f, 0.25f, 1f);

            for (int x = 0; x < WedgeTexW; x++)
            {
                float u = Math.Abs((x + 0.5f) / WedgeTexW - 0.5f) * 2f;
                if (u > halfW * 2f)
                    continue;
                float edge = 1f - Mathf.Clamp((u - halfW) / (halfW * 0.5f + 0.01f), 0f, 1f);
                img.SetPixel(x, y, new Color(1f, 1f, 1f, a * edge));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
