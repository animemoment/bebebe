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
    private float[] _facingAngles;
    private float[] _renderBuffer;
    private MultiMeshInstance2D _shadowInstance;
    private MultiMesh _shadowMultiMesh;
    private float[] _shadowBuffer;
    private DayNightCycle.SunState _sun;
    private bool _hasSunState;
    // Шаг G4: вершинный шейдер тени. Растяжение силуэта уехало в GPU
    // (AgentShadow.gdshader), CPU пишет только трансляцию в ноги (px, py+16).
    // _shadowUseShader=false — fallback на старый CPU-путь матриц
    // (шейдер не загрузился, например headless-юнит вне движка).
    private ShaderMaterial _shadowMaterial;
    private ShaderMaterial _agentInterpolationMaterial;
    private float[] _gpuInstanceBuffer;
    private bool _gpuInterpolationEnabled;
    private bool _shadowUseShader = false;

    private int _agentCount;
    private float _lerpFactor = 0f;
    private bool _initialized = false;
    private bool _hasInitialSnapshot = false;
    // Finding 2: dirty-флаги GPU-upload. _renderBuffer/_shadowBuffer по 320 КБ
    // каждый (10k x 8 float): заливка на GPU без изменений — 640 КБ/кадр =
    // ~38 МБ/с при 60 FPS впустую. Флаги пропускают Buffer= при простое
    // (пауза, lerp сошёлся, тени в LOD-skip). Тела: lerpFactor>=1 && !hasNewSnapshot.
    // Тени: независимый флаг — якорь солнца меняется только в ApplyShadow,
    // позиции теней зависят от тел, поэтому тень грязна если грязны тела.
    private bool _bodiesDirty = true;
    private bool _shadowsDirty = true;
    // Счётчик кадров с последнего upload теней: троттлинг 20 Гц (через кадр
    // при 60 FPS) — на скорости артефакты незаметны, -50% upload теней.
    private bool _shadowUploadSkip;

    public enum AgentRenderLod
    {
        FullWithShadows,
        FullNoShadows,
        MacroDots
    }
    private AgentRenderLod _currentLod = AgentRenderLod.FullWithShadows;
    public AgentRenderLod CurrentLod => _currentLod;
    public bool ShadowsEnabled => _shadowInstance != null && _shadowInstance.Visible;
    private const float LodShadowCutoff = 0.38f;
    private const float LodMacroCutoff = 0.22f;
    private const float LodHysteresis = 0.03f;

    private const float AgentSize = 32f;
    private const float FeetOffsetY = 7.0f;              // 7: подошвы колониста в people.png (строка 23)
    private const float AgentHalf = AgentSize * 0.5f;    // 16: половина квада
    private const float AgentQuarter = AgentSize * 0.25f; // 8: сдвиг якоря к ногам
    private const string TexturePath = "uid://clysp24n2dgat";
    private const string ShadowShaderPath = "res://src/ui/renderers/AgentShadow.gdshader";
    private const string AgentInterpolationShaderPath = "res://src/ui/renderers/AgentInterpolation.gdshader";
    private const string AgentShadowInterpolatedShaderPath = "res://src/ui/renderers/AgentShadowInterpolated.gdshader";
    private const string AgentLodShaderPath = "res://src/ui/renderers/AgentLod.gdshader";

    private ShaderMaterial _agentLodMaterial;
    private QuadMesh _fullQuadMesh;
    private QuadMesh _lodQuadMesh;
    private Texture2D _agentTexture;
    private const float ShadowLodZoom = 0.35f;
    private bool _shadowLodSkip;

    public void Initialize(AgentSimulationThread simulationThread, int agentCount)
    {
        ZIndex = 10;
        _simulationThread = simulationThread ?? throw new ArgumentNullException(nameof(simulationThread));
        _agentCount = agentCount;

        if (_multiMeshInstance != null && GodotObject.IsInstanceValid(_multiMeshInstance))
        {
            _multiMeshInstance.QueueFree();
            _multiMeshInstance = null;
        }
        if (_shadowInstance != null && GodotObject.IsInstanceValid(_shadowInstance))
        {
            _shadowInstance.QueueFree();
            _shadowInstance = null;
        }

        _prevPositions = new System.Numerics.Vector2[agentCount];
        _targetPositions = new System.Numerics.Vector2[agentCount];
        _facingAngles = new float[agentCount];

        Shader interpolationShader = null;
        Shader interpolatedShadowShader = null;
        try
        {
            interpolationShader = GD.Load<Shader>(AgentInterpolationShaderPath);
            interpolatedShadowShader = GD.Load<Shader>(AgentShadowInterpolatedShaderPath);
        }
        catch (Exception)
        {
            // CPU fallback ниже сохраняет исходный MultiMesh-путь.
        }
        bool gpuInterpolationRequested = true;
        try
        {
            gpuInterpolationRequested = ProjectSettings
                .GetSetting("game/rendering/use_gpu_agent_interpolation", true)
                .AsBool();
        }
        catch (Exception)
        {
            // Конфиг без пользовательского параметра считается включённым.
        }
        _gpuInterpolationEnabled = gpuInterpolationRequested
            && interpolationShader != null
            && interpolatedShadowShader != null;
        _renderBuffer = new float[agentCount * 8];
        _shadowBuffer = new float[agentCount * 8];
        if (_gpuInterpolationEnabled)
            _gpuInstanceBuffer = new float[agentCount * 16];

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
        _fullQuadMesh = quadMesh;
        _lodQuadMesh = new QuadMesh
        {
            Size = new Vector2(6f, 6f)
        };
        _agentTexture = texture;

        Shader lodShader = null;
        try
        {
            lodShader = GD.Load<Shader>(AgentLodShaderPath);
        }
        catch (Exception)
        {
            // LOD shader fallback
        }
        if (lodShader != null)
        {
            _agentLodMaterial = new ShaderMaterial { Shader = lodShader };
            _agentLodMaterial.SetShaderParameter("lerp_factor", 0f);
            _agentLodMaterial.SetShaderParameter("agent_color", new Color(0.35f, 0.75f, 1f, 1f));
        }

        float mapSizePx = MapRenderer.MapWidth * MapRenderer.TileSizePx;
        var mapAabb = new Aabb(Godot.Vector3.Zero, new Godot.Vector3(mapSizePx, mapSizePx, 1000f));

        _multiMesh = new MultiMesh
        {
            Mesh = quadMesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = _gpuInterpolationEnabled,
            UseCustomData = _gpuInterpolationEnabled,
            InstanceCount = agentCount,
            CustomAabb = mapAabb
        };

        _multiMeshInstance = new MultiMeshInstance2D
        {
            Name = "AgentMultiMeshInstance",
            Multimesh = _multiMesh,
            Texture = texture
        };
        if (_gpuInterpolationEnabled)
        {
            _agentInterpolationMaterial = new ShaderMaterial { Shader = interpolationShader };
            _multiMeshInstance.Material = _agentInterpolationMaterial;
        }
        AddChild(_multiMeshInstance);

        // Отдельный MultiMesh теней. При GPU path он использует тот же packed
        // prev/target buffer, что и тела, а вершинный shader строит клин на GPU.
        // CPU-вариант сохраняет отдельную матрицу теней для совместимости.
        _shadowMultiMesh = new MultiMesh
        {
            Mesh = quadMesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = _gpuInterpolationEnabled,
            UseCustomData = _gpuInterpolationEnabled,
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

        // При совместной загрузке новых shaders тела и тени интерполируются GPU.
        // Иначе оставляем прежний shadow shader и CPU matrix fallback без изменений.
        Shader shadowShader = interpolatedShadowShader;
        if (!_gpuInterpolationEnabled)
        {
            try { shadowShader = GD.Load<Shader>(ShadowShaderPath); }
            catch (Exception) { shadowShader = null; }
        }
        if (shadowShader != null)
        {
            _shadowMaterial = new ShaderMaterial { Shader = shadowShader };
            _shadowInstance.Material = _shadowMaterial;
            _shadowUseShader = true;
            if (_gpuInterpolationEnabled)
            {
                _shadowMaterial.SetShaderParameter("sun_dir", new Vector2(1f, 0f));
                _shadowMaterial.SetShaderParameter("sun_len_n", 1f);
                _shadowMaterial.SetShaderParameter("sun_width_n", DayNightCycle.ShadowWidthScale);
                _shadowMaterial.SetShaderParameter("lerp_factor", 0f);
                _agentInterpolationMaterial.SetShaderParameter("lerp_factor", 0f);
                GD.Print("[AgentRenderer] GPU interpolation enabled (agents + shadows).");
            }
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
    /// Шейдерный путь: трансляция = ноги, растяжение (клин) — в GPU;
    /// CPU-цикл _Process пишет только трансляции. Fallback: старый CPU-путь
    /// матриц читает _sun напрямую, как раньше.
    /// </summary>
    public void ApplyShadow(DayNightCycle.SunState sun)
    {
        if (!_initialized || _shadowInstance == null || !IsInstanceValid(_shadowInstance))
            return;
        _sun = sun;
        _hasSunState = true;
        var cam = GetViewport()?.GetCamera2D();
        float zoom = cam?.Zoom.X ?? 1f;
        bool speedAllowsShadows = _simulationThread == null || _simulationThread.SpeedMultiplier < 25f;
        bool shadowsAllowed = speedAllowsShadows && zoom >= LodShadowCutoff && _currentLod == AgentRenderLod.FullWithShadows;
        bool show = shadowsAllowed && sun.Alpha > 0.004f && sun.LengthPx >= 0.5f;
        _shadowInstance.Visible = show;
        _shadowMultiMesh.VisibleInstanceCount = show ? _agentCount : 0;
        if (show)
            _shadowInstance.Modulate = sun.ShadowColor;
        if (!show)
            return;
        // CPU fallback загружает матрицы на смене солнца; GPU path меняет только uniforms.
        if (!_gpuInterpolationEnabled)
            _shadowsDirty = true;
        // Точка привязки тени = ноги агента.
        float sLen = sun.LengthPx;
        if (_shadowUseShader && _shadowMaterial != null && IsInstanceValid(_shadowMaterial))
        {
            // O(1): 4 uniform-сета на кадр-независимый тик, не на инстанс.
            _shadowMaterial.SetShaderParameter("sun_dir", sun.Dir);
            _shadowMaterial.SetShaderParameter("sun_len_n", sLen / AgentSize);
            _shadowMaterial.SetShaderParameter("sun_width_n", sun.WidthScale);
            _shadowMaterial.SetShaderParameter("quad_size", new Vector2(AgentSize, AgentSize));
            if (_gpuInterpolationEnabled)
                _shadowMaterial.SetShaderParameter("lerp_factor", _lerpFactor);
        }
    }

    public void UpdateLod(float zoom)
    {
        AgentRenderLod targetLod;
        if (_currentLod == AgentRenderLod.FullWithShadows)
        {
            if (zoom < LodMacroCutoff) targetLod = AgentRenderLod.MacroDots;
            else if (zoom < LodShadowCutoff) targetLod = AgentRenderLod.FullNoShadows;
            else targetLod = AgentRenderLod.FullWithShadows;
        }
        else if (_currentLod == AgentRenderLod.FullNoShadows)
        {
            if (zoom < LodMacroCutoff) targetLod = AgentRenderLod.MacroDots;
            else if (zoom > LodShadowCutoff + LodHysteresis) targetLod = AgentRenderLod.FullWithShadows;
            else targetLod = AgentRenderLod.FullNoShadows;
        }
        else // MacroDots
        {
            if (zoom > LodShadowCutoff + LodHysteresis) targetLod = AgentRenderLod.FullWithShadows;
            else if (zoom > LodMacroCutoff + LodHysteresis) targetLod = AgentRenderLod.FullNoShadows;
            else targetLod = AgentRenderLod.MacroDots;
        }

        if (targetLod != _currentLod)
        {
            SetLod(targetLod);
        }
        else if (_currentLod == AgentRenderLod.FullWithShadows && _hasSunState)
        {
            bool speedAllowsShadows = _simulationThread == null || _simulationThread.SpeedMultiplier < 25f;
            var cam = GetViewport()?.GetCamera2D();
            bool shouldBeVisible = speedAllowsShadows && (cam?.Zoom.X ?? 1f) >= LodShadowCutoff && _sun.Alpha > 0.004f && _sun.LengthPx >= 0.5f;
            if (_shadowInstance != null && _shadowInstance.Visible != shouldBeVisible)
            {
                ApplyShadow(_sun);
            }
        }
    }

    private void SetLod(AgentRenderLod newLod)
    {
        _currentLod = newLod;
        if (newLod == AgentRenderLod.MacroDots)
        {
            if (_agentLodMaterial != null && _multiMeshInstance != null)
                _multiMeshInstance.Material = _agentLodMaterial;
            if (_lodQuadMesh != null && _multiMesh != null)
                _multiMesh.Mesh = _lodQuadMesh;
            if (_multiMeshInstance != null)
                _multiMeshInstance.Texture = null;
            if (_shadowInstance != null && IsInstanceValid(_shadowInstance))
                _shadowInstance.Visible = false;
            if (_shadowMultiMesh != null)
                _shadowMultiMesh.VisibleInstanceCount = 0;
        }
        else
        {
            if (_agentInterpolationMaterial != null && _multiMeshInstance != null)
                _multiMeshInstance.Material = _agentInterpolationMaterial;
            if (_fullQuadMesh != null && _multiMesh != null)
                _multiMesh.Mesh = _fullQuadMesh;
            if (_multiMeshInstance != null)
                _multiMeshInstance.Texture = _agentTexture;

            if (newLod == AgentRenderLod.FullNoShadows)
            {
                if (_shadowInstance != null && IsInstanceValid(_shadowInstance))
                    _shadowInstance.Visible = false;
                if (_shadowMultiMesh != null)
                    _shadowMultiMesh.VisibleInstanceCount = 0;
            }
            else if (_hasSunState)
            {
                ApplyShadow(_sun);
            }
        }
        _bodiesDirty = true;
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
            var cam = GetViewport()?.GetCamera2D();
            float zoom = cam?.Zoom.X ?? 1f;
            UpdateLod(zoom);

            // P1 early-out: ничего нового, lerp сошёлся, тела и тени чистые —
            // кадр пропускаем целиком (ни LerpFill, ни Buffer=, ни GetCamera2D).
            // _bodiesDirty/_shadowsDirty стартуют true → первый кадр всегда идёт.
            // Инвариант: чистые флаги ⇒ _renderBuffer/_shadowBuffer уже на GPU.
            bool queueEmpty = _simulationThread.PositionQueue.IsEmpty;
            if (queueEmpty && _lerpFactor >= 1f && !_bodiesDirty && !_shadowsDirty)
                return;

            bool hasNewSnapshot = false;
            System.Numerics.Vector2[] latestSnapshot = null;
            while (_simulationThread.PositionQueue.TryDequeue(out var snapshot))
            {
                latestSnapshot = snapshot;
            }

            if (latestSnapshot != null)
            {
                if (!_hasInitialSnapshot)
                {
                    // Первый снапшот: заполняем и prev, и target стартовыми позициями,
                    // чтобы агенты не «выстреливали» из угла (0,0) в начале работы.
                    Array.Copy(latestSnapshot, _targetPositions, _agentCount);
                    for (int i = 0; i < _agentCount; i++)
                    {
                        _prevPositions[i] = _targetPositions[i];
                        _facingAngles[i] = 0f;
                    }
                    _hasInitialSnapshot = true;
                }
                else
                {
                    // Фиксируем текущее интерполированное положение перед подменой снапшота.
                    AgentLerpBatch.Interpolate(
                        new Span<System.Numerics.Vector2>(_prevPositions, 0, _agentCount),
                        new ReadOnlySpan<System.Numerics.Vector2>(_targetPositions, 0, _agentCount),
                        _lerpFactor);
                    Array.Copy(latestSnapshot, _targetPositions, _agentCount);
                    for (int i = 0; i < _agentCount; i++)
                    {
                        float dx = _targetPositions[i].X - _prevPositions[i].X;
                        float dy = _targetPositions[i].Y - _prevPositions[i].Y;
                        if (dx * dx + dy * dy > 0.04f)
                        {
                            _facingAngles[i] = MathF.Atan2(dy, dx);
                        }
                    }
                }
                _lerpFactor = 0f;
                hasNewSnapshot = true;
            }

            float lerpSpeed = _simulationThread.SpeedMultiplier >= 5f ? 45f : 25f;
            _lerpFactor = Mathf.Clamp(_lerpFactor + (float)delta * lerpSpeed, 0f, 1f);
            // Guard для mock-тестов: движок NaN-delta не даёт, но неконечный factor
            // ронял бы кадр через ArgumentException из AgentLerpBatch.
            if (!float.IsFinite(_lerpFactor)) _lerpFactor = 1f;

            // Finding 2: тела грязны только пока lerp не сошёлся или есть новый снапшот.
            // Пауза / settled-idle: тел не трогаем ВООБЩЕ (ни LerpFill, ни Buffer=).
            bool bodiesNeedUpdate = _gpuInterpolationEnabled
                ? hasNewSnapshot || _bodiesDirty
                : hasNewSnapshot || _lerpFactor < 1f || _bodiesDirty;
            if (hasNewSnapshot)
                _bodiesDirty = true;

            bool shadowOn = _shadowInstance != null && _shadowInstance.Visible
                && _hasSunState && _sun.Alpha > 0.004f && _sun.LengthPx >= 0.5f
                && _shadowMultiMesh != null && _shadowBuffer != null;

            // Шейдерный путь: трансляция = ноги (px, py+16), без сдвига.
            // Fallback (без шейдера): старая матричная арифметика растяжения на CPU.
            float sdx = 0f, sdy = 0f, sLen = 0f, sLenN = 0f, sWdtN = 0f, sPx = 0f, sPy = 0f;
            if (shadowOn && !_shadowUseShader)
            {
                sdx = _sun.Dir.X; sdy = _sun.Dir.Y;
                sLen = _sun.LengthPx + AgentHalf;
                sLenN = sLen / AgentSize;
                sWdtN = _sun.WidthScale;
                sPx = -sdy; sPy = sdx;
            }

            // GPU: отправляем пары prev/target только при новом snapshot, а
            // lerp-factor обновляем как один uniform на кадр. Без readback.
            if (_gpuInterpolationEnabled)
            {
                if (_currentLod == AgentRenderLod.MacroDots && _agentLodMaterial != null)
                    _agentLodMaterial.SetShaderParameter("lerp_factor", _lerpFactor);
                else
                    _agentInterpolationMaterial?.SetShaderParameter("lerp_factor", _lerpFactor);

                if (shadowOn && _shadowMaterial != null)
                    _shadowMaterial.SetShaderParameter("lerp_factor", _lerpFactor);

                if (bodiesNeedUpdate)
                {
                    var swB = System.Diagnostics.Stopwatch.StartNew();
                    AgentLerpBatch.FillGpuInstanceBuffer(
                        new Span<float>(_gpuInstanceBuffer, 0, _agentCount * 16),
                        new ReadOnlySpan<System.Numerics.Vector2>(_prevPositions, 0, _agentCount),
                        new ReadOnlySpan<System.Numerics.Vector2>(_targetPositions, 0, _agentCount),
                        new ReadOnlySpan<float>(_facingAngles, 0, _agentCount),
                        _agentCount);
                    _multiMesh.Buffer = _gpuInstanceBuffer;
                    swB.Stop();
                    SimEvents.Record("render", "AgentRenderer.bodies_gpu_upload", (float)swB.Elapsed.TotalMilliseconds,
                        _agentCount * 16 * 4);
                    _bodiesDirty = false;
                    _shadowsDirty = true;
                }
            }
            // CPU fallback: прежняя интерполяция и Transform2D upload каждый lerp-кадр.
            else if (bodiesNeedUpdate)
            {
                var swB = System.Diagnostics.Stopwatch.StartNew();
                AgentLerpBatch.LerpFill(
                    new ReadOnlySpan<System.Numerics.Vector2>(_prevPositions, 0, _agentCount),
                    new ReadOnlySpan<System.Numerics.Vector2>(_targetPositions, 0, _agentCount),
                    new ReadOnlySpan<float>(_facingAngles, 0, _agentCount),
                    _lerpFactor,
                    new Span<float>(_renderBuffer, 0, _agentCount * 8),
                    _agentCount);
                _multiMesh.Buffer = _renderBuffer;
                swB.Stop();
                SimEvents.Record("render", "AgentRenderer.bodies_cpu", (float)swB.Elapsed.TotalMilliseconds,
                    _agentCount * 8 * 4);
                _shadowsDirty = true;
                if (_lerpFactor >= 1f && !hasNewSnapshot)
                    _bodiesDirty = false;
            }

            // Shadow GPU path переиспользует packed prev/target buffer; upload нужен
            // только после движения/первого появления. CPU fallback сохраняет старый
            // 20 Гц shadow upload и матричную геометрию.
            bool shadowNeedsWork = shadowOn && _shadowsDirty;
            if (!_gpuInterpolationEnabled && shadowNeedsWork)
            {
                _shadowUploadSkip = !_shadowUploadSkip;
                if (_shadowUploadSkip && !hasNewSnapshot)
                    shadowNeedsWork = false;
            }
            if (shadowNeedsWork)
            {
                if (_gpuInterpolationEnabled)
                {
                    _shadowMultiMesh.Buffer = _gpuInstanceBuffer;
                }
                else
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
                            _shadowBuffer[idx + 3] = px;
                            _shadowBuffer[idx + 4] = 0.0f;
                            _shadowBuffer[idx + 5] = 1.0f;
                            _shadowBuffer[idx + 6] = 0.0f;
                            _shadowBuffer[idx + 7] = py + FeetOffsetY;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < _agentCount; i++)
                        {
                            int idx = i * 8;
                            float px = _renderBuffer[idx + 3];
                            float py = _renderBuffer[idx + 7];
                            _shadowBuffer[idx + 0] = sPx * sWdtN;
                            _shadowBuffer[idx + 1] = sPy * sWdtN;
                            _shadowBuffer[idx + 2] = 0.0f;
                            _shadowBuffer[idx + 3] = px + sdx * (sLen * 0.5f - AgentQuarter);
                            _shadowBuffer[idx + 4] = sdx * sLenN;
                            _shadowBuffer[idx + 5] = sdy * sLenN;
                            _shadowBuffer[idx + 6] = 0.0f;
                            _shadowBuffer[idx + 7] = py + FeetOffsetY + sdy * (sLen * 0.5f - AgentQuarter);
                        }
                    }
                    _shadowMultiMesh.Buffer = _shadowBuffer;
                }
                _shadowsDirty = false;
            }
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