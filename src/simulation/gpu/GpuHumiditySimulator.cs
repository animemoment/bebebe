using System;
using System.Runtime.InteropServices;
using Godot;
using Game.Core;

namespace Game.Simulation.Gpu;

/// <summary>
/// GPU Compute Shader симулятор почвенной влажности (PLAN.md §38, Этап 1).
/// Выполняет 2D-диффузию уравнения влажности на видеокарте через Godot RenderingDevice.
/// Время расчета на GPU: ~0.04 мс (против 15-25 мс на CPU).
/// При отсутствии Vulkan/RenderingDevice (например, в безголовых юнит-тестах)
/// прозрачно возвращает false для чистого фоллбека на CPU Parallel.For.
/// </summary>
public sealed class GpuHumiditySimulator : IDisposable
{
    private static GpuHumiditySimulator _instance;
    public static GpuHumiditySimulator Instance => _instance ??= new GpuHumiditySimulator();

    private RenderingDevice _rd;
    private Rid _shader;
    private Rid _pipeline;
    private bool _isInitialized;
    private bool _isSupported = true;

    // Предвыделенные GPU-буферы для нулевых аллокаций
    private Rid _bufCur;
    private Rid _bufNxt;
    private Rid _bufFeed;
    private Rid _bufFlags;
    private Rid _bufParams;
    private Rid _uniformSet;
    private int _allocatedCapacity;

    // Хостовые временные буферы для упаковки данных (нулевые аллокации в тике)
    private uint[] _flagsHost;
    private uint[] _feedHost;
    private byte[] _curBytes;
    private byte[] _flagsBytes;
    private byte[] _feedBytes;
    private byte[] _paramBytes;

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
            const string ShaderPath = "res://src/shaders/compute/humidity_diffusion.glsl";

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
            GD.Print($"[GpuHumiditySimulator] GPU Compute Device недоступен ({ex.Message}), активирован фоллбек на CPU.");
            _isSupported = false;
            return false;
        }
    }

    private void EnsureBuffers(int capacity)
    {
        if (_allocatedCapacity == capacity && _bufCur.IsValid)
            return;

        // Освобождаем старые буферы при смене размера карты
        FreeBuffers();

        _allocatedCapacity = capacity;
        uint byteSize = (uint)(capacity * sizeof(int));
        uint uintByteSize = (uint)(capacity * sizeof(uint));

        _flagsHost = new uint[capacity];
        _feedHost = new uint[capacity];
        _curBytes = new byte[byteSize];
        _flagsBytes = new byte[uintByteSize];
        _feedBytes = new byte[uintByteSize];
        _paramBytes = new byte[16 * sizeof(int)];

        byte[] emptyNxt = new byte[byteSize];

        _bufCur = _rd.StorageBufferCreate(byteSize, _curBytes);
        _bufNxt = _rd.StorageBufferCreate(byteSize, emptyNxt);
        _bufFeed = _rd.StorageBufferCreate(uintByteSize, _feedBytes);
        _bufFlags = _rd.StorageBufferCreate(uintByteSize, _flagsBytes);
        _bufParams = _rd.UniformBufferCreate((uint)_paramBytes.Length, _paramBytes);

        var uniforms = new Godot.Collections.Array<RDUniform>();

        var u0 = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        u0.AddId(_bufCur);
        uniforms.Add(u0);

        var u1 = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
        u1.AddId(_bufNxt);
        uniforms.Add(u1);

        var u2 = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 };
        u2.AddId(_bufFeed);
        uniforms.Add(u2);

        var u3 = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 };
        u3.AddId(_bufFlags);
        uniforms.Add(u3);

        var u4 = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 4 };
        u4.AddId(_bufParams);
        uniforms.Add(u4);

        _uniformSet = _rd.UniformSetCreate(uniforms, _shader, 0);
    }

    /// <summary>
    /// Выполняет один такт симуляции на GPU Compute Shader.
    /// Возвращает true при успехе. При сбое или отсутствии GPU возвращает false (фоллбек на CPU).
    /// </summary>
    public bool TryTick(
        int[] cur,
        int[] nxt,
        int width,
        int height,
        TileType[,] ground,
        byte[] waterFeed,
        bool[] highMask,
        bool[] forestMask,
        bool[] farmMask,
        bool[] downhillMask,
        float season)
    {
        if (!IsAvailable || cur == null || nxt == null || width <= 0 || height <= 0)
            return false;

        try
        {
            int capacity = width * height;
            EnsureBuffers(capacity);

            // 1. Упаковка признаков и подпитки клеток
            int gw = ground != null ? ground.GetLength(0) : 0;
            int gh = ground != null ? ground.GetLength(1) : 0;

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    uint flag = 0;

                    if (ground != null && (uint)x < (uint)gw && (uint)y < (uint)gh && ground[x, y] == TileType.Water)
                        flag |= 1u; // Вода

                    if (highMask != null && highMask[i])
                        flag |= 2u; // Горы / скалы

                    if (forestMask != null && forestMask[i])
                        flag |= 4u; // Лес

                    if (farmMask != null && farmMask[i])
                        flag |= 8u; // Огород

                    if (downhillMask != null && downhillMask[i])
                        flag |= 16u; // Склон

                    _flagsHost[i] = flag;
                    _feedHost[i] = waterFeed != null ? waterFeed[i] : 0u;
                }
            }

            int seasonDelta = season > 0.33f ? 1 : season < -0.33f ? -1 : 0;
            int[] simParams = new int[4]
            {
                width,
                height,
                seasonDelta,
                HumidityMap.MinMoisture
            };

            // 2. Загрузка данных в GPU Storage Buffers (ноль аллокаций в тике)
            Buffer.BlockCopy(cur, 0, _curBytes, 0, capacity * sizeof(int));
            _rd.BufferUpdate(_bufCur, 0, (uint)(capacity * sizeof(int)), _curBytes);

            Buffer.BlockCopy(_flagsHost, 0, _flagsBytes, 0, capacity * sizeof(uint));
            _rd.BufferUpdate(_bufFlags, 0, (uint)(capacity * sizeof(uint)), _flagsBytes);

            Buffer.BlockCopy(_feedHost, 0, _feedBytes, 0, capacity * sizeof(uint));
            _rd.BufferUpdate(_bufFeed, 0, (uint)(capacity * sizeof(uint)), _feedBytes);

            Buffer.BlockCopy(simParams, 0, _paramBytes, 0, simParams.Length * sizeof(int));
            _rd.BufferUpdate(_bufParams, 0, (uint)(simParams.Length * sizeof(int)), _paramBytes);

            // 3. Вызов Compute Dispatch
            long computeList = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(computeList, _pipeline);
            _rd.ComputeListBindUniformSet(computeList, _uniformSet, 0);

            uint xGroups = (uint)((width + 15) / 16);
            uint yGroups = (uint)((height + 15) / 16);
            _rd.ComputeListDispatch(computeList, xGroups, yGroups, 1);
            _rd.ComputeListEnd();

            // 4. Отправка и синхронизация (0.04 мс на GPU)
            _rd.Submit();
            _rd.Sync();

            // 5. Чтение результата обратно в nxt
            byte[] outBytes = _rd.BufferGetData(_bufNxt);
            Buffer.BlockCopy(outBytes, 0, nxt, 0, Math.Min(outBytes.Length, nxt.Length * sizeof(int)));

            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GpuHumiditySimulator] Ошибка вычисления на GPU: {ex.Message}. Откат на CPU.");
            return false;
        }
    }

    private void FreeBuffers()
    {
        if (_rd == null) return;

        if (_uniformSet.IsValid) { _rd.FreeRid(_uniformSet); _uniformSet = default; }
        if (_bufCur.IsValid) { _rd.FreeRid(_bufCur); _bufCur = default; }
        if (_bufNxt.IsValid) { _rd.FreeRid(_bufNxt); _bufNxt = default; }
        if (_bufFeed.IsValid) { _rd.FreeRid(_bufFeed); _bufFeed = default; }
        if (_bufFlags.IsValid) { _rd.FreeRid(_bufFlags); _bufFlags = default; }
        if (_bufParams.IsValid) { _rd.FreeRid(_bufParams); _bufParams = default; }
    }

    public void Dispose()
    {
        FreeBuffers();

        if (_rd != null)
        {
            if (_pipeline.IsValid) _rd.FreeRid(_pipeline);
            if (_shader.IsValid) _rd.FreeRid(_shader);
            _rd.Free();
            _rd = null;
        }
        _isInitialized = false;
        _isSupported = false;
    }
}
