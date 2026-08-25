using Godot;
using Game.Simulation;
using System;

namespace Game.UI;

public partial class GroundItemRenderer : Node2D
{
    private MultiMeshInstance2D _multiMeshInstance;
    private MultiMesh _multiMesh;
    private float[] _renderBuffer;

    private const string TexturePath = "uid://by88ysblfuqqu";
    private const float ItemSize = 48f;
    private const int MaxRenderedInstances = 8192;

    public override void _Ready()
    {
        ZIndex = 5;
        _renderBuffer = new float[MaxRenderedInstances * 8];

        var texture = ResourceLoader.Load<Texture2D>(TexturePath);
        if (texture == null)
        {
            GD.PrintErr($"[GroundItemRenderer] Внимание: текстура '{TexturePath}' не найдена!");
        }

        var quadMesh = new QuadMesh
        {
            Size = new Vector2(ItemSize, ItemSize)
        };

        float mapSizePx = MapRenderer.MapWidth * MapRenderer.TileSizePx;
        var mapAabb = new Aabb(Godot.Vector3.Zero, new Godot.Vector3(mapSizePx, mapSizePx, 1000f));

        _multiMesh = new MultiMesh
        {
            Mesh = quadMesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = false,
            UseCustomData = false,
            InstanceCount = MaxRenderedInstances,
            VisibleInstanceCount = 0,
            CustomAabb = mapAabb
        };

        _multiMeshInstance = new MultiMeshInstance2D
        {
            Name = "GroundItemMultiMesh",
            Multimesh = _multiMesh,
            Texture = texture
        };
        AddChild(_multiMeshInstance);
    }

    public override void _Process(double delta)
    {
        if (_multiMesh == null) return;

        // Если в очереди нет новых снапшотов — выходим мгновенно (0.00 мс)
        if (!GroundItemManager.Instance.ItemPositionsQueue.TryDequeue(out var latestSnapshot))
            return;

        // Выгружаем в очередь только самый свежий снапшот, пропуская промежуточные
        while (GroundItemManager.Instance.ItemPositionsQueue.TryDequeue(out var newerSnapshot))
        {
            latestSnapshot = newerSnapshot;
        }

        if (latestSnapshot.Buffer != null)
        {
            int count = Math.Min(latestSnapshot.Count, MaxRenderedInstances);
            _multiMesh.VisibleInstanceCount = count;

            for (int i = 0; i < count; i++)
            {
                var pos = latestSnapshot.Buffer[i];
                int idx = i * 8;

                _renderBuffer[idx + 0] = 1.0f;
                _renderBuffer[idx + 1] = 0.0f;
                _renderBuffer[idx + 2] = 0.0f;
                _renderBuffer[idx + 3] = pos.X;

                _renderBuffer[idx + 4] = 0.0f;
                _renderBuffer[idx + 5] = 1.0f;
                _renderBuffer[idx + 6] = 0.0f;
                _renderBuffer[idx + 7] = pos.Y;
            }

            _multiMesh.Buffer = _renderBuffer;
        }
    }
}