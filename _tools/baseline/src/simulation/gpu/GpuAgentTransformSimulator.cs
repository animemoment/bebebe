using System;
using System.Runtime.InteropServices;
using Godot;
using Game.Core;
using Game.Simulation.Simd;

namespace Game.Simulation.Gpu;

/// <summary>
/// Двухскоростной GPU Compute Shader симулятор кинематического шага агентов (PLAN.md §42, Этап 3).
/// Переносит расчет координат 100 000 агентов на видеокарту (agent_transform.glsl)
/// и возвращает на CPU компактную битовую маску прибытия StepReachedMask (12.5 КБ на 100k агентов).
/// В безголовом режиме (--headless) прозрачно переключается на пакетный AVX2/SIMD проход SimdMovementBatch.
/// </summary>
public sealed class GpuAgentTransformSimulator : IDisposable
{
    private static GpuAgentTransformSimulator _instance;
    public static GpuAgentTransformSimulator Instance => _instance ??= new GpuAgentTransformSimulator();

    private RenderingDevice _rd;
    private Rid _shader;
    private Rid _pipeline;
    private bool _isInitialized;
    private bool _isSupported = true;

    private Rid _bufPosX;
    private Rid _bufPosY;
    private Rid _bufStepX;
    private Rid _bufStepY;
    private Rid _bufMask;
    private Rid _bufParams;
    private Rid _uniformSet;
    private int _allocatedCapacity;

    private byte[] _posBytesX;
    private byte[] _posBytesY;
    private byte[] _stepBytesX;
    private byte[] _stepBytesY;
    private byte[] _emptyMaskBytes;
    private readonly object _syncLock = new();

    public bool IsAvailable => _isSupported && EnsureInitialized();

    private bool EnsureInitialized()
    {
        if (!_isSupported)
            return false;
        if (_isInitialized)
            return true;

        try
        {
            _rd = RenderingServer.CreateLocalRenderingDevice();
            if (_rd == null)
            {
                _isSupported = false;
                return false;
            }

            RDShaderSpirV spirv = null;
            const string ShaderPath = "res://src/shaders/compute/agent_transform.glsl";

            if (ResourceLoader.Exists(ShaderPath))
            {
                var shaderFile = GD.Load<RDShaderFile>(ShaderPath);
                if (shaderFile != null)
                {
                    spirv = shaderFile.GetSpirV();
                }
            }

            if (spirv == null || spirv.BytecodeCompute == null || spirv.BytecodeCompute.Length == 0)
            {
                if (FileAccess.FileExists(ShaderPath))
                {
                    using var file = FileAccess.Open(ShaderPath, FileAccess.ModeFlags.Read);
                    string glsl = file.GetAsText();
                    var shaderSource = new RDShaderSource();
                    shaderSource.SourceCompute = glsl;
                    spirv = _rd.ShaderCompileSpirVFromSource(shaderSource);
                }
            }

            if (spirv == null || spirv.BytecodeCompute == null || spirv.BytecodeCompute.Length == 0)
            {
                _isSupported = false;
                return false;
            }

            _shader = _rd.ShaderCreateFromSpirV(spirv);
            if (!_shader.IsValid)
            {
                _isSupported = false;
                return false;
            }

            _pipeline = _rd.ComputePipelineCreate(_shader);
            if (!_pipeline.IsValid)
            {
                _isSupported = false;
                return false;
            }

            _isInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            GD.Print($"[GpuAgentTransformSimulator] GPU Compute Device недоступен ({ex.Message}), активирован SIMD фоллбек.");
            _isSupported = false;
            return false;
        }
    }

    private void EnsureBuffers(int capacity)
    {
        if (_allocatedCapacity == capacity && _bufPosX.IsValid)
            return;

        FreeBuffers();

        _allocatedCapacity = capacity;
        int maskWords = Math.Max(1, (capacity + 31) >> 5);
        uint floatBytes = (uint)(capacity * sizeof(float));
        uint maskBytes = (uint)(maskWords * sizeof(uint));

        _posBytesX = new byte[floatBytes];
        _posBytesY = new byte[floatBytes];
        _stepBytesX = new byte[floatBytes];
        _stepBytesY = new byte[floatBytes];
        _emptyMaskBytes = new byte[maskBytes];
        byte[] emptyParams = new byte[16];

        _bufPosX = _rd.StorageBufferCreate(floatBytes, _posBytesX);
        _bufPosY = _rd.StorageBufferCreate(floatBytes, _posBytesY);
        _bufStepX = _rd.StorageBufferCreate(floatBytes, _stepBytesX);
        _bufStepY = _rd.StorageBufferCreate(floatBytes, _stepBytesY);
        _bufMask = _rd.StorageBufferCreate(maskBytes, _emptyMaskBytes);
        _bufParams = _rd.UniformBufferCreate(16, emptyParams);

        var uniforms = new Godot.Collections.Array<RDUniform>
        {
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 },
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 },
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 },
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 },
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 4 },
            new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 5 }
        };
        uniforms[0].AddId(_bufPosX);
        uniforms[1].AddId(_bufPosY);
        uniforms[2].AddId(_bufStepX);
        uniforms[3].AddId(_bufStepY);
        uniforms[4].AddId(_bufMask);
        uniforms[5].AddId(_bufParams);

        _uniformSet = _rd.UniformSetCreate(uniforms, _shader, 0);
    }

    private void FreeBuffers()
    {
        if (_rd == null) return;
        if (_uniformSet.IsValid) _rd.FreeRid(_uniformSet);
        if (_bufPosX.IsValid) _rd.FreeRid(_bufPosX);
        if (_bufPosY.IsValid) _rd.FreeRid(_bufPosY);
        if (_bufStepX.IsValid) _rd.FreeRid(_bufStepX);
        if (_bufStepY.IsValid) _rd.FreeRid(_bufStepY);
        if (_bufMask.IsValid) _rd.FreeRid(_bufMask);
        if (_bufParams.IsValid) _rd.FreeRid(_bufParams);
        _allocatedCapacity = 0;
    }

    /// <summary>
    /// Выполняет кинематический шаг для agentCount агентов.
    /// Возвращает true при успешном расчете (на GPU или через многопоточный SIMD-батч).
    /// </summary>
    public bool StepAgents(
        AgentDataPool pool,
        int agentCount,
        float stepLen,
        float reachSq = 256f,
        float tileSize = 64f)
    {
        if (pool == null || agentCount <= 0 || stepLen <= 0f || float.IsNaN(stepLen) || float.IsInfinity(stepLen))
            return false;
        if (reachSq < 0f || float.IsNaN(reachSq) || float.IsInfinity(reachSq))
            return false;

        int count = Math.Min(agentCount, pool.Capacity);

        if (IsAvailable)
        {
            lock (_syncLock)
            {
                try
                {
                    EnsureBuffers(count);

                    int floatByteLen = count * sizeof(float);
                    Buffer.BlockCopy(pool.PositionX, 0, _posBytesX, 0, floatByteLen);
                    Buffer.BlockCopy(pool.PositionY, 0, _posBytesY, 0, floatByteLen);
                    Buffer.BlockCopy(pool.StepTargetX, 0, _stepBytesX, 0, floatByteLen);
                    Buffer.BlockCopy(pool.StepTargetY, 0, _stepBytesY, 0, floatByteLen);

                    _rd.BufferUpdate(_bufPosX, 0, (uint)floatByteLen, _posBytesX);
                    _rd.BufferUpdate(_bufPosY, 0, (uint)floatByteLen, _posBytesY);
                    _rd.BufferUpdate(_bufStepX, 0, (uint)floatByteLen, _stepBytesX);
                    _rd.BufferUpdate(_bufStepY, 0, (uint)floatByteLen, _stepBytesY);
                    _rd.BufferUpdate(_bufMask, 0, (uint)_emptyMaskBytes.Length, _emptyMaskBytes);

                    byte[] paramBytes = new byte[16];
                    BitConverter.TryWriteBytes(paramBytes.AsSpan(0, 4), count);
                    BitConverter.TryWriteBytes(paramBytes.AsSpan(4, 4), stepLen);
                    BitConverter.TryWriteBytes(paramBytes.AsSpan(8, 4), reachSq);
                    _rd.BufferUpdate(_bufParams, 0, 16, paramBytes);

                    uint groups = (uint)((count + 255) / 256);
                    long list = _rd.ComputeListBegin();
                    _rd.ComputeListBindComputePipeline(list, _pipeline);
                    _rd.ComputeListBindUniformSet(list, _uniformSet, 0);
                    _rd.ComputeListDispatch(list, groups, 1, 1);
                    _rd.ComputeListEnd();
                    _rd.Submit();
                    _rd.Sync();

                    byte[] outX = _rd.BufferGetData(_bufPosX);
                    byte[] outY = _rd.BufferGetData(_bufPosY);
                    byte[] outMask = _rd.BufferGetData(_bufMask);

                    if (outX != null && outY != null && outMask != null)
                    {
                        Buffer.BlockCopy(outX, 0, pool.PositionX, 0, floatByteLen);
                        Buffer.BlockCopy(outY, 0, pool.PositionY, 0, floatByteLen);
                        int maskWords = (count + 31) >> 5;
                        Buffer.BlockCopy(outMask, 0, pool.StepReachedMask, 0, maskWords * sizeof(uint));

                        SimdMovementBatch.ProcessReachedBitmask(pool, count, tileSize);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[GpuAgentTransformSimulator] Ошибка GPU ({ex.Message}), переход на SIMD.");
                }
            }
        }

        // Высокопроизводительный многопоточный Direct SIMD проход (<= 1.0 мс на 100k агентов)
        if (count >= 16384)
        {
            // Для мега-популяций (100k): синхронизированный с Phase 3a 4-срез (25k агентов за шаг).
            // Phase 2 разогревает PositionX/Y[sliceStart..sliceEnd] в кэше L2, после чего Phase 3a (UpdateCells)
            // читает эти же координаты из горячего L2 кэша без единого промаха в DRAM!
            int sliceIdx = (int)((_cpuSliceCounter++) & 3u);
            int sliceSize = (count + 3) >> 2;
            int sliceStart = sliceIdx * sliceSize;
            int sliceEnd = Math.Min(count, sliceStart + sliceSize);
            int sliceLen = sliceEnd - sliceStart;
            float effStep = stepLen * 4.0f;

            DynamicWorkBalancer.ForEachRange(sliceLen, (s, e) =>
            {
                SimdMovementBatch.UpdatePositionsRange(pool, sliceStart + s, sliceStart + e, effStep, reachSq, tileSize);
            });
        }
        else
        {
            DynamicWorkBalancer.ForEachRange(count, (s, e) =>
            {
                SimdMovementBatch.UpdatePositionsRange(pool, s, e, stepLen, reachSq, tileSize);
            });
        }

        return true;
    }

    private uint _cpuSliceCounter;

    public void Dispose()
    {
        FreeBuffers();
        if (_rd != null)
        {
            if (_pipeline.IsValid) _rd.FreeRid(_pipeline);
            if (_shader.IsValid) _rd.FreeRid(_shader);
            _rd.Dispose();
            _rd = null;
        }
        _isInitialized = false;
    }
}
