using System;
using System.Runtime.InteropServices;
using Godot;
using Game.Core;

namespace Game.Simulation.Gpu;

/// <summary>
/// GPU Compute Shader симулятор векторных полей навигации (PLAN.md §41, Этап 2).
/// Вычисляет поле дистанций и векторное поле направлений (Flow Field) к целевой клетке
/// на видеокарте через Godot RenderingDevice (Jump Flooding / Distance Transform).
/// При отсутствии Vulkan/RenderingDevice (безголовые тесты) прозрачно фоллбечит на CPU.
/// </summary>
public sealed class GpuFlowFieldSimulator : IDisposable
{
    private static GpuFlowFieldSimulator _instance;
    public static GpuFlowFieldSimulator Instance => _instance ??= new GpuFlowFieldSimulator();

    private RenderingDevice _rd;
    private Rid _shader;
    private Rid _pipeline;
    private bool _isInitialized;
    private bool _isSupported = true;

    // Предвыделенные GPU-буферы
    private Rid _bufCost;
    private Rid _bufDistIn;
    private Rid _bufDistOut;
    private Rid _bufFlow;
    private Rid _bufParams;
    private Rid _uniformSetPing;
    private Rid _uniformSetPong;
    private int _allocatedCapacity;

    // Хостовые буферы для загрузки
    private uint[] _costHost;
    private byte[] _costBytes;
    private float[] _floatBuf;
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
            const string ShaderPath = "res://src/shaders/compute/flow_field_jfa.glsl";

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
            GD.Print($"[GpuFlowFieldSimulator] GPU Compute Device недоступен ({ex.Message}), активирован фоллбек на CPU.");
            _isSupported = false;
            return false;
        }
    }

    private void EnsureBuffers(int capacity)
    {
        if (_allocatedCapacity == capacity && _bufCost.IsValid)
            return;

        FreeBuffers();

        _allocatedCapacity = capacity;
        uint costBytes = (uint)(capacity * sizeof(uint));
        uint distBytes = (uint)(capacity * sizeof(float));
        uint flowBytes = (uint)(capacity * 2 * sizeof(float)); // vec2 = 8 байт
        uint paramsBytes = 32; // 8 x int (32 байта)

        _costHost = new uint[capacity];
        _costBytes = new byte[costBytes];
        _floatBuf = new float[capacity * 2];

        byte[] emptyDist = new byte[distBytes];
        byte[] emptyFlow = new byte[flowBytes];
        byte[] emptyParams = new byte[paramsBytes];

        _bufCost = _rd.StorageBufferCreate(costBytes, _costBytes);
        _bufDistIn = _rd.StorageBufferCreate(distBytes, emptyDist);
        _bufDistOut = _rd.StorageBufferCreate(distBytes, emptyDist);
        _bufFlow = _rd.StorageBufferCreate(flowBytes, emptyFlow);
        _bufParams = _rd.UniformBufferCreate(paramsBytes, emptyParams);

        // Uniform Set Ping: In = _bufDistIn, Out = _bufDistOut
        var uPing = new Godot.Collections.Array<RDUniform>
        {
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 },
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 },
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 },
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 },
            new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 4 }
        };
        uPing[0].AddId(_bufCost);
        uPing[1].AddId(_bufDistIn);
        uPing[2].AddId(_bufDistOut);
        uPing[3].AddId(_bufFlow);
        uPing[4].AddId(_bufParams);
        _uniformSetPing = _rd.UniformSetCreate(uPing, _shader, 0);

        // Uniform Set Pong: In = _bufDistOut, Out = _bufDistIn
        var uPong = new Godot.Collections.Array<RDUniform>
        {
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 },
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 },
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 },
            new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 },
            new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 4 }
        };
        uPong[0].AddId(_bufCost);
        uPong[1].AddId(_bufDistOut);
        uPong[2].AddId(_bufDistIn);
        uPong[3].AddId(_bufFlow);
        uPong[4].AddId(_bufParams);
        _uniformSetPong = _rd.UniformSetCreate(uPong, _shader, 0);
    }

    private void FreeBuffers()
    {
        if (_rd == null) return;
        if (_uniformSetPing.IsValid) _rd.FreeRid(_uniformSetPing);
        if (_uniformSetPong.IsValid) _rd.FreeRid(_uniformSetPong);
        if (_bufCost.IsValid) _rd.FreeRid(_bufCost);
        if (_bufDistIn.IsValid) _rd.FreeRid(_bufDistIn);
        if (_bufDistOut.IsValid) _rd.FreeRid(_bufDistOut);
        if (_bufFlow.IsValid) _rd.FreeRid(_bufFlow);
        if (_bufParams.IsValid) _rd.FreeRid(_bufParams);
        _allocatedCapacity = 0;
    }

    /// <summary>
    /// Вычисляет векторное поле навигации к цели (targetX, targetY).
    /// Возвращает true при успехе. Заполняет outFlowVectors единичными векторами направлений.
    /// </summary>
    public bool ComputeFlowField(byte[] costMap, int width, int height, int targetX, int targetY, System.Numerics.Vector2[] outFlowVectors)
    {
        if (costMap == null || outFlowVectors == null)
            return false;
        if (width <= 0 || height <= 0 || (uint)targetX >= (uint)width || (uint)targetY >= (uint)height)
            return false;

        int totalCells = width * height;
        if (costMap.Length < totalCells || outFlowVectors.Length < totalCells)
            return false;

        // Попытка расчета на GPU
        if (IsAvailable)
        {
            lock (_syncLock)
            {
                try
                {
                    EnsureBuffers(totalCells);

                    // Упаковываем стоимость в uint
                    for (int i = 0; i < totalCells; i++)
                    {
                        _costHost[i] = costMap[i];
                    }

                    Buffer.BlockCopy(_costHost, 0, _costBytes, 0, totalCells * sizeof(uint));
                    _rd.BufferUpdate(_bufCost, 0, (uint)(totalCells * sizeof(uint)), _costBytes);

                    uint xGroups = (uint)((width + 15) / 16);
                    uint yGroups = (uint)((height + 15) / 16);

                    // Pass 0: Инициализация семян
                    UploadParams(width, height, 0, 0, targetX, targetY);
                    DispatchCompute(_uniformSetPing, xGroups, yGroups);

                    // Pass 1: Шаги Jump Flooding (например: 16, 8, 4, 2, 1)
                    int[] stepSizes = { 16, 8, 4, 2, 1 };
                    bool pingCurrent = true;
                    for (int s = 0; s < stepSizes.Length; s++)
                    {
                        UploadParams(width, height, stepSizes[s], 1, targetX, targetY);
                        DispatchCompute(pingCurrent ? _uniformSetPong : _uniformSetPing, xGroups, yGroups);
                        pingCurrent = !pingCurrent;
                    }

                    // Pass 2: Расчет градиентного вектора движения
                    UploadParams(width, height, 1, 2, targetX, targetY);
                    DispatchCompute(pingCurrent ? _uniformSetPing : _uniformSetPong, xGroups, yGroups);

                    // Чтение результата
                    byte[] flowData = _rd.BufferGetData(_bufFlow);
                    if (flowData != null && flowData.Length >= totalCells * 8)
                    {
                        Buffer.BlockCopy(flowData, 0, _floatBuf, 0, totalCells * 8);
                        for (int i = 0; i < totalCells; i++)
                        {
                            outFlowVectors[i] = new System.Numerics.Vector2(_floatBuf[i * 2], _floatBuf[i * 2 + 1]);
                        }
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[GpuFlowFieldSimulator] Ошибка GPU выполнения ({ex.Message}), откат на CPU.");
                }
            }
        }

        // Прозрачный fallback на чистый CPU волновой алгоритм
        return ComputeFlowFieldCpu(costMap, width, height, targetX, targetY, outFlowVectors);
    }

    private void UploadParams(int width, int height, int stepSize, int passMode, int targetX, int targetY)
    {
        int[] p = new int[8];
        p[0] = width;
        p[1] = height;
        p[2] = stepSize;
        p[3] = passMode;
        p[4] = targetX;
        p[5] = targetY;
        p[6] = 0;
        p[7] = 0;

        byte[] bytes = new byte[32];
        Buffer.BlockCopy(p, 0, bytes, 0, 32);
        _rd.BufferUpdate(_bufParams, 0, (uint)bytes.Length, bytes);
    }

    private void DispatchCompute(Rid uniformSet, uint xGroups, uint yGroups)
    {
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, uniformSet, 0);
        _rd.ComputeListDispatch(list, xGroups, yGroups, 1);
        _rd.ComputeListEnd();
        _rd.Submit();
        _rd.Sync();
    }

    /// <summary>
    /// Высокопроизводительный CPU фоллбек (BFS / волновое поле дистанций)
    /// </summary>
    public static bool ComputeFlowFieldCpu(byte[] costMap, int width, int height, int targetX, int targetY, System.Numerics.Vector2[] outFlowVectors)
    {
        int total = width * height;
        int targetIdx = targetY * width + targetX;

        // Если цель внутри стены — вектор нулевой
        if (costMap[targetIdx] == 0)
        {
            Array.Clear(outFlowVectors, 0, total);
            return true;
        }

        int[] dist = new int[total];
        Array.Fill(dist, int.MaxValue);

        int[] queue = new int[total];
        int head = 0;
        int tail = 0;

        dist[targetIdx] = 0;
        queue[tail++] = targetIdx;

        // 4-соседний BFS
        while (head < tail)
        {
            int curr = queue[head++];
            int cx = curr % width;
            int cy = curr / width;
            int cd = dist[curr];

            // Соседи: (0, -1), (0, 1), (-1, 0), (1, 0)
            if (cy > 0) TryRelax(cx, cy - 1, cd, costMap, dist, queue, ref tail, width);
            if (cy < height - 1) TryRelax(cx, cy + 1, cd, costMap, dist, queue, ref tail, width);
            if (cx > 0) TryRelax(cx - 1, cy, cd, costMap, dist, queue, ref tail, width);
            if (cx < width - 1) TryRelax(cx + 1, cy, cd, costMap, dist, queue, ref tail, width);
        }

        // Извлечение векторов направления
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (costMap[idx] == 0 || (x == targetX && y == targetY))
                {
                    outFlowVectors[idx] = System.Numerics.Vector2.Zero;
                    continue;
                }

                int myDist = dist[idx];
                int bestDist = myDist;
                int bestDx = 0;
                int bestDy = 0;

                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx;
                        int ny = y + dy;
                        if ((uint)nx < (uint)width && (uint)ny < (uint)height)
                        {
                            int nd = dist[ny * width + nx];
                            if (nd < bestDist)
                            {
                                bestDist = nd;
                                bestDx = dx;
                                bestDy = dy;
                            }
                        }
                    }
                }

                if (bestDx != 0 || bestDy != 0)
                {
                    var v = new System.Numerics.Vector2(bestDx, bestDy);
                    outFlowVectors[idx] = System.Numerics.Vector2.Normalize(v);
                }
                else
                {
                    outFlowVectors[idx] = System.Numerics.Vector2.Zero;
                }
            }
        }

        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void TryRelax(int x, int y, int curDist, byte[] costMap, int[] dist, int[] queue, ref int tail, int width)
    {
        int nIdx = y * width + x;
        byte c = costMap[nIdx];
        if (c > 0)
        {
            int cand = curDist + c;
            if (cand < dist[nIdx])
            {
                dist[nIdx] = cand;
                queue[tail++] = nIdx;
            }
        }
    }

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
