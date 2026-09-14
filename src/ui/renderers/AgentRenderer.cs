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
    private MultiMeshInstance2D _shadowInstance;
    private MultiMesh _shadowMultiMesh;
    private float[] _shadowBuffer;
    private DayNightCycle.SunState _sun;
    private bool _hasSunState;
    // Шаг G4: вершинный шейдер тени. Растяжение силуэта уехало в GPU
    // (AgentShadow.gdshader), CPU пишет только трансляцию с якорем у ног.
    // _shAx/_shAy — кэш якоря из ApplyShadow (O(1), главный поток).
    // _shadowUseShader=false — fallback на старый CPU-путь матриц
    // (шейдер не загрузился, например headless-юнит вне движка).
    private ShaderMaterial _shadowMaterial;
    private bool _shadowUseShader = false;
    private float _shAx = 0f;
    private float _shAy = 0f;

    private int _agentCount;
    private float _lerpFactor = 0f;
    private bool _initialized = false;
    private bool _hasInitialSnapshot = false;

    private const float AgentSize = 32f;
    private const string TexturePath = "uid://clysp24n2dgat";
    private const string ShadowShaderPath = "res://src/ui/renderers/AgentShadow.gdshader";

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

        // Тень-объект: свой MultiMesh с тем же квадом/текстурой. Шаг G4: растяжение
        // силуэта от ног вдоль солнца делает вершинный шейдер (AgentShadow.gdshader),
        // CPU пишет только трансляцию с якорем (4 записи/инстанс вместо 8 с арифметикой).
        // Z ниже тел, чтобы тени лежали под агентами. +1 draw-call.
        _shadowBuffer = new float[agentCount * 8];
        _shadowMultiMesh = new MultiMesh
        {
            Mesh = quadMesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = false,
            UseCustomData = false,
            InstanceCount = agentCount,
            VisibleInstanceCount = 0,
            CustomAabb = mapAabb
        };
        _shadowInstance = new MultiMeshInstance2D
        {
            Name = "AgentShadowInstance",
            Multimesh = _shadowMultiMesh,
            Texture = texture,
            Modulate = new Color(0f, 0f, 0f, 0f),
            ZIndex = -1,
            ShowBehindParent = true,
            Visible = false
        };
        AddChild(_shadowInstance);

        // Шаг G4: материал тени с вершинным шейдером — кодом, scenes/ не трогаем.
        // GD.Load вне движка (headless-юнит) вернёт null — тогда fallback на CPU-путь.
        Shader shadowShader = null;
        try { shadowShader = GD.Load<Shader>(ShadowShaderPath); }
        catch (Exception) { shadowShader = null; }
        if (shadowShader != null)
        {
            _shadowMaterial = new ShaderMaterial { Shader = shadowShader };
            _shadowInstance.Material = _shadowMaterial;
            _shadowUseShader = true;
        }
        else
        {
            GD.PrintErr("[AgentRenderer] Шейдер тени не загружен, тени считаются на CPU (fallback).");
            _shadowUseShader = false;
        }

        _initialized = true;
    }

    /// <summary>
    /// Тень агентов: кэширует солнце, ставит альфа/видимость и uniforms шейдера.
    /// Только главный поток, O(1).
    /// Шейдерный путь: якорь кэшируется в _shAx/_shAy, растяжение — в GPU;
    /// CPU-цикл _Process пишет только трансляции. Fallback: старый CPU-путь
    /// матриц читает _sun напрямую, как раньше.
    /// </summary>
    public void ApplyShadow(DayNightCycle.SunState sun)
    {
        if (!_initialized || _shadowInstance == null || !IsInstanceValid(_shadowInstance))
            return;
        _sun = sun;
        _hasSunState = true;
        bool show = sun.Alpha > 0.004f && sun.LengthPx >= 0.5f;
        _shadowInstance.Visible = show;
        _shadowMultiMesh.VisibleInstanceCount = show ? _agentCount : 0;
        if (show)
            _shadowInstance.Modulate = new Color(0f, 0f, 0f, sun.Alpha);
        if (!show)
            return;
        // Та же математика, что была в CPU-заливке: sLen=Length+полквада,
        // якорь = dir*(sLen/2 - четверть квада), нормировки на AgentSize.
        float sLen = sun.LengthPx + AgentSize * 0.5f;
        _shAx = sun.Dir.X * (sLen * 0.5f - AgentSize * 0.25f);
        _shAy = sun.Dir.Y * (sLen * 0.5f - AgentSize * 0.25f);
        if (_shadowUseShader && _shadowMaterial != null && IsInstanceValid(_shadowMaterial))
        {
            // O(1): 5 uniform-сетов на кадр-независимый тик, не на инстанс.
            _shadowMaterial.SetShaderParameter("sun_dir", sun.Dir);
            _shadowMaterial.SetShaderParameter("sun_len_n", sLen / AgentSize);
            _shadowMaterial.SetShaderParameter("sun_width_n", sun.WidthScale);
            _shadowMaterial.SetShaderParameter("sun_anchor_shift", new Vector2(_shAx, _shAy));
            _shadowMaterial.SetShaderParameter("quad_size", new Vector2(AgentSize, AgentSize));
        }
    }

    /// <summary>Совместимость со старым вызовом ApplyShadow(offset, alpha).</summary>
    public void ApplyShadow(Vector2 sunOffset, float sunAlpha)
    {
        float len = sunOffset.Length();
        Vector2 dir = len > 0.001f ? sunOffset / len : Vector2.Zero;
        ApplyShadow(new DayNightCycle.SunState(dir, len, DayNightCycle.ShadowWidthScale, sunAlpha));
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
                // Фиксируем текущее интерполированное положение перед подменой снапшота.
                // Формула та же, что была: prev + (target - prev) * factor (см. AgentLerpBatch).
                AgentLerpBatch.Interpolate(
                    new Span<System.Numerics.Vector2>(_prevPositions, 0, _agentCount),
                    new ReadOnlySpan<System.Numerics.Vector2>(_targetPositions, 0, _agentCount),
                    _lerpFactor);
                Array.Copy(snapshot, _targetPositions, _agentCount);
                hasNewSnapshot = true;
            }

            if (hasNewSnapshot)
            {
                if (!_hasInitialSnapshot)
                {
                    // Первый снапшот: заполняем и prev, и target стартовыми позициями,
                    // чтобы агенты не «выстреливали» из угла (0,0) в начале работы.
                    for (int i = 0; i < _agentCount; i++)
                    {
                        _prevPositions[i] = _targetPositions[i];
                    }
                    _hasInitialSnapshot = true;
                }
                _lerpFactor = 0f;
            }

            float lerpSpeed = _simulationThread.SpeedMultiplier >= 5f ? 45f : 25f;
            _lerpFactor = Mathf.Clamp(_lerpFactor + (float)delta * lerpSpeed, 0f, 1f);
            // Guard для mock-тестов: движок NaN-delta не даёт, но неконечный factor
            // ронял бы кадр через ArgumentException из AgentLerpBatch.
            if (!float.IsFinite(_lerpFactor)) _lerpFactor = 1f;

            bool shadowOn = _hasSunState && _sun.Alpha > 0.004f && _sun.LengthPx >= 0.5f
                && _shadowMultiMesh != null && _shadowBuffer != null;
            // Шейдерный путь: якорь уже закэширован в _shAx/_shAy (ApplyShadow, O(1)).
            // Fallback (без шейдера): старая матричная арифметика растяжения на CPU.
            float sdx = 0f, sdy = 0f, sLen = 0f, sLenN = 0f, sWdtN = 0f, sPx = 0f, sPy = 0f;
            float shAx = _shAx, shAy = _shAy;
            if (shadowOn && !_shadowUseShader)
            {
                sdx = _sun.Dir.X; sdy = _sun.Dir.Y;
                sLen = _sun.LengthPx + AgentSize * 0.5f;
                sLenN = sLen / AgentSize;
                sWdtN = _sun.WidthScale;
                sPx = -sdy; sPy = sdx;
            }

            // Тела: lerp prev→target + заливка единичной матрицы с трансляцией
            // (скалярный слитый проход — stride-8 scatter memory-bound, см. AgentLerpBatch).
            AgentLerpBatch.LerpFill(
                new ReadOnlySpan<System.Numerics.Vector2>(_prevPositions, 0, _agentCount),
                new ReadOnlySpan<System.Numerics.Vector2>(_targetPositions, 0, _agentCount),
                _lerpFactor,
                new Span<float>(_renderBuffer, 0, _agentCount * 8),
                _agentCount);

            // Теневой проход (шаг G4): шейдерный путь — ОДИН скалярный цикл, 4 записи
            // на инстанс (единичная матрица + трансляция со сдвигом якоря у ног).
            // Растяжение силуэта делает вершинный шейдер из локальных координат
            // квада (те же формулы: локальные ±16 дают мировые ±len/2, ±wdt/2).
            // Fallback без шейдера (_shadowUseShader=false): старый CPU-путь
            // с матричной арифметикой растяжения (8 записей), визуально идентичен.
            // При выключенных тенях цикл пропускается целиком (тел он не касается).
            if (shadowOn)
            {
                if (_shadowUseShader)
                {
                    for (int i = 0; i < _agentCount; i++)
                    {
                        int idx = i * 8;
                        float px = _renderBuffer[idx + 3];
                        float py = _renderBuffer[idx + 7];
                        _shadowBuffer[idx + 0] = 1.0f;
                        _shadowBuffer[idx + 1] = 0.0f;
                        _shadowBuffer[idx + 2] = 0.0f;
                        _shadowBuffer[idx + 3] = px + shAx;
                        _shadowBuffer[idx + 4] = 0.0f;
                        _shadowBuffer[idx + 5] = 1.0f;
                        _shadowBuffer[idx + 6] = 0.0f;
                        _shadowBuffer[idx + 7] = py + AgentSize * 0.5f + shAy;
                    }
                }
                else
                {
                    for (int i = 0; i < _agentCount; i++)
                    {
                        int idx = i * 8;
                        float px = _renderBuffer[idx + 3];
                        float py = _renderBuffer[idx + 7];

                        // Тень растягивается от ног (px, py+полтела) вдоль солнца.
                        // Порядок Buffer для Transform2D: x.x, x.y, pad, origin.x, y.x, y.y, pad, origin.y.
                        // Базис нормирован на размер квада (32px): локальные ±16px дают мировые ±len/2, ±wdt/2.
                        // Якорь сдвинут вперёд на len/2, чтобы тень начиналась у ног, а не центрировалась на них.
                        _shadowBuffer[idx + 0] = sdx * sLenN;
                        _shadowBuffer[idx + 1] = sdy * sLenN;
                        _shadowBuffer[idx + 2] = 0.0f;
                        _shadowBuffer[idx + 3] = px + sdx * (sLen * 0.5f - AgentSize * 0.25f);

                        _shadowBuffer[idx + 4] = sPx * sWdtN;
                        _shadowBuffer[idx + 5] = sPy * sWdtN;
                        _shadowBuffer[idx + 6] = 0.0f;
                        _shadowBuffer[idx + 7] = py + AgentSize * 0.5f + sdy * (sLen * 0.5f - AgentSize * 0.25f);
                    }
                }
            }

            _multiMesh.Buffer = _renderBuffer;
            if (shadowOn)
                _shadowMultiMesh.Buffer = _shadowBuffer;
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