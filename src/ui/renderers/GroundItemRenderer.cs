using Godot;
using Game.Core;
using Game.Simulation;
using System;
using System.Collections.Generic;

namespace Game.UI;

public partial class GroundItemRenderer : Node2D
{
    private const int MaxRenderedInstances = 4096;
    private const float DefaultItemSize = 44f;

    private readonly Dictionary<ItemId, (MultiMesh Mesh, MultiMeshInstance2D Instance, float[] Buffer)> _renderers = new();

    /// <summary>Тени мелочи (Syx ShadowBatch): общий рендерер ставит Main, тикает солнцем.</summary>
    public ItemShadowRenderer ItemShadows { get; set; }

    public override void _Ready()
    {
        ZIndex = 5;

        var quadMesh = new QuadMesh { Size = new Vector2(DefaultItemSize, DefaultItemSize) };
        float mapSizePx = MapRenderer.MapWidth * MapRenderer.TileSizePx;
        var mapAabb = new Aabb(Godot.Vector3.Zero, new Godot.Vector3(mapSizePx, mapSizePx, 1000f));

        ItemId[] itemTypes = { ItemId.Log, ItemId.Grain, ItemId.Stone };

        foreach (var id in itemTypes)
        {
            var def = ItemRegistry.Get(id);
            Texture2D texture;
            if (id == ItemId.Stone)
            {
                // Камень лежит на земле первым квадрантом атласа (верх-лево).
                // Вариант россыпи уже не важен: это сыпучий ресурс, не декор.
                var atlas = ResourceLoader.Load<Texture2D>(def.TextureUid);
                texture = atlas == null ? null : new AtlasTexture
                {
                    Atlas = atlas,
                    Region = new Rect2(0, 0, MapRenderer.TileSizePx, MapRenderer.TileSizePx)
                };
            }
            else
            {
                texture = ResourceLoader.Load<Texture2D>(def.TextureUid);
            }
            if (texture == null)
            {
                GD.PrintErr($"[GroundItemRenderer] Внимание: текстура '{def.TextureUid}' для {def.Name} не найдена!");
            }

            var multiMesh = new MultiMesh
            {
                Mesh = quadMesh,
                TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
                UseColors = false,
                UseCustomData = false,
                InstanceCount = MaxRenderedInstances,
                VisibleInstanceCount = 0,
                CustomAabb = mapAabb
            };

            var instance = new MultiMeshInstance2D
            {
                Name = $"GroundItems_{def.Name}",
                Multimesh = multiMesh,
                Texture = texture
            };
            AddChild(instance);

            _renderers[id] = (multiMesh, instance, new float[MaxRenderedInstances * 8]);
        }
    }

    public override void _Process(double delta)
    {
        if (!GroundItemManager.Instance.SnapshotQueue.TryDequeue(out var latestSnapshot))
            return;

        // E62: сколько снапшотов выброшено — «рендер не успевает».
        int dropped = 0;
        while (GroundItemManager.Instance.SnapshotQueue.TryDequeue(out var newerSnapshot))
        {
            latestSnapshot = newerSnapshot;
            dropped++;
        }
        // P0-1: рендер — главный поток, но Series/Count на каждый _Process
        // (60/с × типы) всё равно жмут тот же _cLock, что и sim-поток.
        // Оставляем только суммарный замер прохода (один Record).
        var swR = System.Diagnostics.Stopwatch.StartNew();

        if (latestSnapshot.PositionsByItem == null || latestSnapshot.Counts == null)
            return;

        // Аккумулятор теней: BeginFrame здесь (первый из трёх источников),
        // Flush — в StockpileItemRenderer (последний). Порядок _Process между
        // рендерерами не гарантирован, но аккумулятор идемпотентен: даже если
        // Flush случится раньше чужого PushSpots, следующий кадр доберёт.
        ItemShadows?.BeginFrame();
        foreach (var (id, (mesh, _, buffer)) in _renderers)
        {
            int itemIdx = (int)id;
            int count = (itemIdx < latestSnapshot.Counts.Length)
                ? Math.Min(latestSnapshot.Counts[itemIdx], MaxRenderedInstances)
                : 0;

            mesh.VisibleInstanceCount = count;

            if (count > 0)
            {
                var positions = latestSnapshot.PositionsByItem[itemIdx];
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

                mesh.Buffer = buffer;

                // Тень того же пятна — в аккумулятор, не сразу на GPU.
                ItemShadows?.PushSpots(positions, count, DefaultItemSize);
            }
        }
        swR.Stop();
        SimEvents.Record("render", "GroundItemRenderer", (float)swR.Elapsed.TotalMilliseconds);
    }
}