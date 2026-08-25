using Godot;
using Game.Core;
using Game.Simulation;
using System;

namespace Game.UI;

public partial class AgentRenderer : Node2D
{
    private MultiMeshInstance2D _multiMeshInstance;
    private MultiMesh _multiMesh;
    private AgentSimulationThread _simulationThread;

    private System.Numerics.Vector2[] _prevPositions;
    private System.Numerics.Vector2[] _targetPositions;
    private float[] _renderBuffer;

    private int _agentCount;
    private float _lerpFactor = 0f;
    private bool _initialized = false;

    private const float AgentSize = 64f;
    private const string TexturePath = "uid://clysp24n2dgat";

    public void Initialize(AgentSimulationThread simulationThread, int agentCount)
    {
        ZIndex = 10;
        _simulationThread = simulationThread ?? throw new ArgumentNullException(nameof(simulationThread));
        _agentCount = agentCount;

        _prevPositions = new System.Numerics.Vector2[agentCount];
        _targetPositions = new System.Numerics.Vector2[agentCount];
        _renderBuffer = new float[agentCount * 8];

        var texture = ResourceLoader.Load<Texture2D>(TexturePath);
        if (texture == null)
        {
            GD.PrintErr($"[AgentRenderer] Текстура '{TexturePath}' не найдена!");
            return;
        }

        var quadMesh = new QuadMesh
        {
            Size = new Vector2(AgentSize, AgentSize)
        };

        float mapSizePx = MapRenderer.MapWidth * MapRenderer.TileSizePx;
        var mapAabb = new Aabb(Godot.Vector3.Zero, new Godot.Vector3(mapSizePx, mapSizePx, 1000f));

        _multiMesh = new MultiMesh
        {
            Mesh = quadMesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = false,
            UseCustomData = false,
            InstanceCount = agentCount,
            CustomAabb = mapAabb
        };

        _multiMeshInstance = new MultiMeshInstance2D
        {
            Name = "AgentMultiMeshInstance",
            Multimesh = _multiMesh,
            Texture = texture
        };
        AddChild(_multiMeshInstance);

        _initialized = true;
    }

    public override void _Process(double delta)
    {
        if (!_initialized || _simulationThread == null)
            return;

        using (GameProfiler.Scope("Render: Agent MultiMesh"))
        {
            bool hasNewSnapshot = false;
            while (_simulationThread.PositionQueue.TryDequeue(out var snapshot))
            {
                Array.Copy(_targetPositions, _prevPositions, _agentCount);
                Array.Copy(snapshot, _targetPositions, _agentCount);
                hasNewSnapshot = true;
            }

            if (hasNewSnapshot)
            {
                _lerpFactor = 0f;
            }

            float lerpSpeed = _simulationThread.SpeedMultiplier >= 5f ? 45f : 25f;
            _lerpFactor = Mathf.Clamp(_lerpFactor + (float)delta * lerpSpeed, 0f, 1f);

            for (int i = 0; i < _agentCount; i++)
            {
                float px = Mathf.Lerp(_prevPositions[i].X, _targetPositions[i].X, _lerpFactor);
                float py = Mathf.Lerp(_prevPositions[i].Y, _targetPositions[i].Y, _lerpFactor);

                int idx = i * 8;
                _renderBuffer[idx + 0] = 1.0f;
                _renderBuffer[idx + 1] = 0.0f;
                _renderBuffer[idx + 2] = 0.0f;
                _renderBuffer[idx + 3] = px;

                _renderBuffer[idx + 4] = 0.0f;
                _renderBuffer[idx + 5] = 1.0f;
                _renderBuffer[idx + 6] = 0.0f;
                _renderBuffer[idx + 7] = py;
            }

            _multiMesh.Buffer = _renderBuffer;
        }
    }

    public override void _ExitTree()
    {
        _initialized = false;
        _simulationThread?.Stop();
        _simulationThread?.Dispose();
        base._ExitTree();
    }
}