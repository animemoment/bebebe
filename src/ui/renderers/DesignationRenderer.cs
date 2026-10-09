using Godot;
using Game.Simulation;
using System;
using System.Collections.Generic;

namespace Game.UI;

public partial class DesignationRenderer : Node2D
{
    private MultiMeshInstance2D _multiMeshInstance;
    private MultiMesh _multiMesh;
    private float[] _renderBuffer;

    private const int MaxDesignations = 8192;
    private const float MarkerSize = 64f;

    // Нейтральный маркер (рамка + крест), рисуется ровно на клетку.
    // Не использует спрайт дерева -> нет «тени сверху» от несовпадения якорей/размеров.
    private const int MarkerTexSize = 64;
    private const int MarkerBorder = 3;

    private static readonly Color MarkModulate = new Color(1f, 0.25f, 0.2f, 0.9f);

    private bool _isDirty = true;
    // Pending-флаг редрава: ставится каждым событием метки; сливается в _Process.
    // Guarantee: ни одно изменение не теряется между троттл-окнами 0.1с (FIX «залипших
    // маркеров» — раньше событие, пришедшее после сброса флага до ближайшего редрава,
    // не доезжало до экрана).
    private bool _pendingRebuild = true;
    private float _updateTimer;
    private readonly HashSet<(int X, int Y)> _markedPositions = new(4096);

    public override void _Ready()
    {
        ZIndex = 15;
        _renderBuffer = new float[MaxDesignations * 8];

        var texture = CreateMarkerTexture();

        var quadMesh = new QuadMesh
        {
            Size = new Vector2(MarkerSize, MarkerSize)
        };

        float mapSizePx = MapRenderer.MapWidth * MapRenderer.TileSizePx;
        var mapAabb = new Aabb(Godot.Vector3.Zero, new Godot.Vector3(mapSizePx, mapSizePx, 1000f));

        _multiMesh = new MultiMesh
        {
            Mesh = quadMesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = false,
            UseCustomData = false,
            InstanceCount = MaxDesignations,
            VisibleInstanceCount = 0,
            CustomAabb = mapAabb
        };

        _multiMeshInstance = new MultiMeshInstance2D
        {
            Name = "DesignationMultiMesh",
            Multimesh = _multiMesh,
            Texture = texture,
            Modulate = MarkModulate
        };
        AddChild(_multiMeshInstance);

        TreeJobManager.Instance.OnTreeMarked += OnTreeMarked;
        TreeJobManager.Instance.OnTreesBatchMarked += OnTreesBatchMarked;
        TreeJobManager.Instance.OnTreeUnmarked += OnTreeUnmarked;
        TreeJobManager.Instance.OnTreesBatchUnmarked += OnTreesBatchUnmarked;
        TreeJobManager.Instance.OnTreeChopped += OnTreeUnmarked;
        // Метки камня — тем же красным маркером (общий пул _markedPositions).
        StoneJobManager.Instance.OnStoneMarked += OnTreeMarked;
        StoneJobManager.Instance.OnStonesBatchMarked += OnTreesBatchMarked;
        StoneJobManager.Instance.OnStoneUnmarked += OnTreeUnmarked;
        StoneJobManager.Instance.OnStonesBatchUnmarked += OnTreesBatchUnmarked;
        StoneJobManager.Instance.OnStoneMined += OnTreeUnmarked;
    }

    private void OnTreeMarked((int X, int Y) pos)
    {
        _markedPositions.Add(pos);
        _isDirty = true;
        _pendingRebuild = true;
    }

    private void OnTreesBatchMarked(List<(int X, int Y)> positions)
    {
        foreach (var pos in positions)
        {
            _markedPositions.Add(pos);
        }
        _isDirty = true;
        _pendingRebuild = true;
    }

    private void OnTreeUnmarked((int X, int Y) pos)
    {
        _markedPositions.Remove(pos);
        _isDirty = true;
        _pendingRebuild = true;
    }

    private void OnTreesBatchUnmarked(List<(int X, int Y)> positions)
    {
        foreach (var pos in positions)
        {
            _markedPositions.Remove(pos);
        }
        _isDirty = true;
        _pendingRebuild = true;
    }

    public override void _Process(double delta)
    {
        if (_multiMesh == null)
            return;

        // FIX (залипшие маркеры): раньше редрав выполнялся ТОЛЬКО в кадре, когда флаг
        // _isDirty ещё был поднят: событие, пришедшее в 0.1с окне между двумя редравами,
        // сбрасывалось флагом, но сам буфер не пересобирался — метка/снятие «не доезжало»
        // до экрана до следующего случайного события (крестик на дереве не появлялся,
        // метка оставалась после рубки). Теперь pending-изменения накапливаются в
        // _pendingRebuild и гарантированно сливаются в ближайший плановый редрав.
        _updateTimer += (float)delta;
        if (_updateTimer < 0.1f)
            return;
        _updateTimer = 0f;

        if (!_pendingRebuild)
            return;
        _pendingRebuild = false;
        _isDirty = false;

        int count = 0;
        float tile = MapRenderer.TileSizePx;
        float half = tile * 0.5f;

        foreach (var (x, y) in _markedPositions)
        {
            if (count >= MaxDesignations)
                break;

            int idx = count * 8;
            float px = x * tile + half;
            float py = y * tile + half;

            _renderBuffer[idx + 0] = 1.0f;
            _renderBuffer[idx + 1] = 0.0f;
            _renderBuffer[idx + 2] = 0.0f;
            _renderBuffer[idx + 3] = px;

            _renderBuffer[idx + 4] = 0.0f;
            _renderBuffer[idx + 5] = 1.0f;
            _renderBuffer[idx + 6] = 0.0f;
            _renderBuffer[idx + 7] = py;

            count++;
        }

        _multiMesh.VisibleInstanceCount = count;
        // FIX (целостность отрисовки MultiMesh): присваиваем Buffer ВСЕГДА, а не только
        // при count > 0. Раньше при снятии последней метки VisibleInstanceCount становился
        // 0, но GPU-буфер оставался с устаревшими трансформациями; при следующем открытии
        // карты/редраве в окно между записью count и обновлением буфера могли мелькнуть
        // старые маркеры (и Unity-style артефакт «призрачные Instance»). Один присвоение
        // буфера целиком согласует VisibleInstanceCount и данные.
        _multiMesh.Buffer = _renderBuffer;
    }

    /// <summary>
    /// Генерирует текстуру маркера 64x64: рамка + крест. Белый цвет,
    /// итоговый оттенок задаётся через Modulate инстанса.
    /// </summary>
    private static ImageTexture CreateMarkerTexture()
    {
        var img = Image.CreateEmpty(MarkerTexSize, MarkerTexSize, false, Image.Format.Rgba8);
        img.Fill(new Color(0f, 0f, 0f, 0f));

        var white = new Color(1f, 1f, 1f, 1f);
        int s = MarkerTexSize;
        int b = MarkerBorder;

        // Рамка.
        for (int i = 0; i < s; i++)
        {
            for (int k = 0; k < b; k++)
            {
                img.SetPixel(i, k, white);
                img.SetPixel(i, s - 1 - k, white);
                img.SetPixel(k, i, white);
                img.SetPixel(s - 1 - k, i, white);
            }
        }

        // Крест по диагоналям (толщина 2px).
        for (int i = b * 2; i < s - b * 2; i++)
        {
            img.SetPixel(i, i, white);
            img.SetPixel(i + 1, i, white);
            img.SetPixel(i, s - 1 - i, white);
            img.SetPixel(i + 1, s - 1 - i, white);
        }

        return ImageTexture.CreateFromImage(img);
    }

    public override void _ExitTree()
    {
        TreeJobManager.Instance.OnTreeMarked -= OnTreeMarked;
        TreeJobManager.Instance.OnTreesBatchMarked -= OnTreesBatchMarked;
        TreeJobManager.Instance.OnTreeUnmarked -= OnTreeUnmarked;
        TreeJobManager.Instance.OnTreesBatchUnmarked -= OnTreesBatchUnmarked;
        TreeJobManager.Instance.OnTreeChopped -= OnTreeUnmarked;
        StoneJobManager.Instance.OnStoneMarked -= OnTreeMarked;
        StoneJobManager.Instance.OnStonesBatchMarked -= OnTreesBatchMarked;
        StoneJobManager.Instance.OnStoneUnmarked -= OnTreeUnmarked;
        StoneJobManager.Instance.OnStonesBatchUnmarked -= OnTreesBatchUnmarked;
        StoneJobManager.Instance.OnStoneMined -= OnTreeUnmarked;
        base._ExitTree();
    }
}