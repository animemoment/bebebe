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
    private const string TextureTree0 = "uid://c70p6ktr0vcx6";

    private bool _isDirty = true;
    private float _updateTimer;
    private readonly HashSet<(int X, int Y)> _markedPositions = new(4096);

    public override void _Ready()
    {
        ZIndex = 15;
        _renderBuffer = new float[MaxDesignations * 8];

        var texture = ResourceLoader.Load<Texture2D>(TextureTree0);
        if (texture == null)
        {
            GD.PrintErr($"[DesignationRenderer] Текстура '{TextureTree0}' не найдена!");
            return;
        }

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
            Modulate = new Color(0f, 0f, 0f, 0.55f)
        };
        AddChild(_multiMeshInstance);

        TreeJobManager.Instance.OnTreeMarked += OnTreeMarked;
        TreeJobManager.Instance.OnTreesBatchMarked += OnTreesBatchMarked;
        TreeJobManager.Instance.OnTreeUnmarked += OnTreeUnmarked;
        TreeJobManager.Instance.OnTreesBatchUnmarked += OnTreesBatchUnmarked;
        TreeJobManager.Instance.OnTreeChopped += OnTreeUnmarked;
    }

    private void OnTreeMarked((int X, int Y) pos)
    {
        _markedPositions.Add(pos);
        _isDirty = true;
    }

    private void OnTreesBatchMarked(List<(int X, int Y)> positions)
    {
        foreach (var pos in positions)
        {
            _markedPositions.Add(pos);
        }
        _isDirty = true;
    }

    private void OnTreeUnmarked((int X, int Y) pos)
    {
        _markedPositions.Remove(pos);
        _isDirty = true;
    }

    private void OnTreesBatchUnmarked(List<(int X, int Y)> positions)
    {
        foreach (var pos in positions)
        {
            _markedPositions.Remove(pos);
        }
        _isDirty = true;
    }

    public override void _Process(double delta)
    {
        if (!_isDirty || _multiMesh == null)
            return;

        _updateTimer += (float)delta;
        if (_updateTimer < 0.033f)
            return;

        _updateTimer = 0f;
        _isDirty = false;
        int count = 0;

        foreach (var (x, y) in _markedPositions)
        {
            if (count >= MaxDesignations)
                break;

            int idx = count * 8;
            float px = x * 64f + 32f;
            float py = y * 64f + 32f;

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
        _multiMesh.Buffer = _renderBuffer;
    }

    public override void _ExitTree()
    {
        TreeJobManager.Instance.OnTreeMarked -= OnTreeMarked;
        TreeJobManager.Instance.OnTreesBatchMarked -= OnTreesBatchMarked;
        TreeJobManager.Instance.OnTreeUnmarked -= OnTreeUnmarked;
        TreeJobManager.Instance.OnTreesBatchUnmarked -= OnTreesBatchUnmarked;
        TreeJobManager.Instance.OnTreeChopped -= OnTreeUnmarked;
        base._ExitTree();
    }
}