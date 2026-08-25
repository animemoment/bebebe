using Godot;
using Game.Simulation;
using System;

namespace Game.UI;

public partial class CropRenderer : Node2D
{
    private const int MaxCrops = 4096;
    private const float CropSize = 64f;

    private static readonly string[] StageTextureUids =
    {
        "uid://deq1c2pg5ln3o", // Фаза 1
        "uid://cq1k2nlma5qxs", // Фаза 2
        "uid://fn3w8iv243e7", // Фаза 3
        "uid://d4b1fcpa6txcf"  // Фаза 4
    };

    private readonly MultiMeshInstance2D[] _instances = new MultiMeshInstance2D[4];
    private readonly MultiMesh[] _multiMeshes = new MultiMesh[4];
    private readonly float[][] _renderBuffers = new float[4][];

    public override void _Ready()
    {
        ZIndex = 6; // Поверх грядок (ZIndex=0..5), под агентами (ZIndex=10)

        var quadMesh = new QuadMesh { Size = new Vector2(CropSize, CropSize) };
        float mapSizePx = MapRenderer.MapWidth * MapRenderer.TileSizePx;
        var mapAabb = new Aabb(Godot.Vector3.Zero, new Godot.Vector3(mapSizePx, mapSizePx, 1000f));

        for (int i = 0; i < 4; i++)
        {
            _renderBuffers[i] = new float[MaxCrops * 8];

            var tex = ResourceLoader.Load<Texture2D>(StageTextureUids[i]);
            if (tex == null)
            {
                GD.PrintErr($"[CropRenderer] Текстура фазы {i + 1} '{StageTextureUids[i]}' не найдена!");
            }

            _multiMeshes[i] = new MultiMesh
            {
                Mesh = quadMesh,
                TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
                UseColors = false,
                UseCustomData = false,
                InstanceCount = MaxCrops,
                VisibleInstanceCount = 0,
                CustomAabb = mapAabb
            };

            _instances[i] = new MultiMeshInstance2D
            {
                Name = $"CropStage_{i + 1}",
                Multimesh = _multiMeshes[i],
                Texture = tex
            };
            AddChild(_instances[i]);
        }
    }

    public override void _Process(double delta)
    {
        if (!CropGrowthManager.Instance.SnapshotQueue.TryDequeue(out var latest))
            return;

        while (CropGrowthManager.Instance.SnapshotQueue.TryDequeue(out var newer))
        {
            latest = newer;
        }

        if (latest.PositionsByStage == null || latest.Counts == null) return;

        for (int stage = 0; stage < 4; stage++)
        {
            int count = Math.Min(latest.Counts[stage], MaxCrops);
            _multiMeshes[stage].VisibleInstanceCount = count;

            if (count > 0)
            {
                var positions = latest.PositionsByStage[stage];
                var buffer = _renderBuffers[stage];

                for (int i = 0; i < count; i++)
                {
                    var pos = positions[i];
                    int idx = i * 8;

                    buffer[idx + 0] = 1.0f;
                    buffer[idx + 1] = 0.0f;
                    buffer[idx + 2] = 0.0f;
                    buffer[idx + 3] = pos.X;

                    buffer[idx + 4] = 0.0f;
                    buffer[idx + 5] = 1.0f;
                    buffer[idx + 6] = 0.0f;
                    buffer[idx + 7] = pos.Y;
                }

                _multiMeshes[stage].Buffer = buffer;
            }
        }
    }
}