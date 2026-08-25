using Godot;
using System;
using System.Runtime.InteropServices;
using Game.Core;

namespace Game.Simulation.Gpu;

/// <summary>
/// Сервис выполнения вычислений физики и движения агентов на GPU через RenderingDevice (Compute Shaders).
/// Предусматривает автоматический fallback на CPU в случае недоступности видеокарты.
/// </summary>
public sealed class AgentGpuComputeService : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct GpuAgentData
    {
        public float PosX;
        public float PosY;
        public float TargetX;
        public float TargetY;
        public float Speed;
        public uint State;
        public uint Pad0;
        public uint Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuPushConstants
    {
        public uint AgentCount;
        public float DeltaTime;
        public float ReachDistance;
        public float AgentRadius;
        public int MapWidth;
        public int MapHeight;
        public int TileSize;
        public uint Pad;
    }

    private RenderingDevice _rd;
    private Rid _shaderRid;
    private Rid _pipelineRid;
    private Rid _agentBufferRid;
    private Rid _gridBufferRid;
    private Rid _uniformSetRid;

    private GpuAgentData[] _cpuGpuAgentBuffer;
    private byte[] _agentByteArray;
    private uint[] _cpuGridBuffer;
    private byte[] _gridByteArray;

    public bool IsInitialized { get; private set; }

    public bool Initialize(int agentCount, int mapWidth, int mapHeight)
    {
        try
        {
            _rd = RenderingServer.CreateLocalRenderingDevice();
            if (_rd == null)
            {
                GD.Print("[GPU Compute] Локальный RenderingDevice недоступен. Используется CPU fallback.");
                return false;
            }

            var shaderFile = ResourceLoader.Load<RDShaderFile>("res://src/simulation/gpu/agent_movement.glsl");
            if (shaderFile == null)
            {
                GD.PrintErr("[GPU Compute] Не удалось загрузить agent_movement.glsl.");
                return false;
            }

            var shaderBytecode = shaderFile.GetSpirV();
            if (shaderBytecode == null)
            {
                GD.PrintErr("[GPU Compute] Ошибка SPIR-V байткода шейдера.");
                return false;
            }

            _shaderRid = _rd.ShaderCreateFromSpirV(shaderBytecode);
            _pipelineRid = _rd.ComputePipelineCreate(_shaderRid);

            _cpuGpuAgentBuffer = new GpuAgentData[agentCount];
            _agentByteArray = new byte[agentCount * Marshal.SizeOf<GpuAgentData>()];
            _agentBufferRid = _rd.StorageBufferCreate((uint)_agentByteArray.Length);

            int gridLength = mapWidth * mapHeight;
            _cpuGridBuffer = new uint[gridLength];
            _gridByteArray = new byte[gridLength * sizeof(uint)];
            _gridBufferRid = _rd.StorageBufferCreate((uint)_gridByteArray.Length);

            var uAgent = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.StorageBuffer,
                Binding = 0
            };
            uAgent.AddId(_agentBufferRid);

            var uGrid = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.StorageBuffer,
                Binding = 1
            };
            uGrid.AddId(_gridBufferRid);

            _uniformSetRid = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uAgent, uGrid }, _shaderRid, 0);

            IsInitialized = true;
            GD.Print($"[GPU Compute] Успешно инициализирован пайплайн на {agentCount} агентов.");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GPU Compute] Ошибка инициализации: {ex.Message}. Переключение на CPU.");
            IsInitialized = false;
            return false;
        }
    }

    public void DispatchMovement(AgentDataPool pool, SimulationContext ctx, float deltaTime)
    {
        if (!IsInitialized) return;

        int agentCount = pool.Capacity;

        // 1. Упаковка данных агентов
        for (int i = 0; i < agentCount; i++)
        {
            _cpuGpuAgentBuffer[i] = new GpuAgentData
            {
                PosX = pool.PositionX[i],
                PosY = pool.PositionY[i],
                TargetX = pool.TargetPositionX[i],
                TargetY = pool.TargetPositionY[i],
                Speed = 120.0f,
                State = (uint)pool.States[i]
            };
        }
        Buffer.BlockCopy(_cpuGpuAgentBuffer, 0, _agentByteArray, 0, _agentByteArray.Length);
        _rd.BufferUpdate(_agentBufferRid, 0, (uint)_agentByteArray.Length, _agentByteArray);

        // 2. Обновление сетки коллизий
        int mapW = ctx.MapWidth;
        int mapH = ctx.MapHeight;
        for (int y = 0; y < mapH; y++)
        {
            for (int x = 0; x < mapW; x++)
            {
                bool blocked = ctx.Ground[x, y] == TileType.Water || ctx.SolidWalls[x, y];
                _cpuGridBuffer[y * mapW + x] = blocked ? 1u : 0u;
            }
        }
        Buffer.BlockCopy(_cpuGridBuffer, 0, _gridByteArray, 0, _gridByteArray.Length);
        _rd.BufferUpdate(_gridBufferRid, 0, (uint)_gridByteArray.Length, _gridByteArray);

        // 3. Push-константы
        var pushConstants = new GpuPushConstants
        {
            AgentCount = (uint)agentCount,
            DeltaTime = deltaTime,
            ReachDistance = 48.0f,
            AgentRadius = 8.0f,
            MapWidth = mapW,
            MapHeight = mapH,
            TileSize = ctx.TileSize
        };

        byte[] pushBytes = new byte[Marshal.SizeOf<GpuPushConstants>()];
        IntPtr ptr = Marshal.AllocHGlobal(pushBytes.Length);
        Marshal.StructureToPtr(pushConstants, ptr, false);
        Marshal.Copy(ptr, pushBytes, 0, pushBytes.Length);
        Marshal.FreeHGlobal(ptr);

        // 4. Диспетчеризация
        long computeList = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(computeList, _pipelineRid);
        _rd.ComputeListBindUniformSet(computeList, _uniformSetRid, 0);
        _rd.ComputeListSetPushConstant(computeList, pushBytes, (uint)pushBytes.Length);

        int workgroups = (agentCount + 63) / 64;
        _rd.ComputeListDispatch(computeList, (uint)workgroups, 1, 1);
        _rd.ComputeListEnd();

        _rd.Submit();
        _rd.Sync();

        // 5. Выгрузка новых позиций в пул памяти CPU
        byte[] outputData = _rd.BufferGetData(_agentBufferRid);
        Buffer.BlockCopy(outputData, 0, _cpuGpuAgentBuffer, 0, outputData.Length);

        for (int i = 0; i < agentCount; i++)
        {
            pool.PositionX[i] = _cpuGpuAgentBuffer[i].PosX;
            pool.PositionY[i] = _cpuGpuAgentBuffer[i].PosY;
        }
    }

    public void Dispose()
    {
        if (_rd != null)
        {
            if (_uniformSetRid.IsValid) _rd.FreeRid(_uniformSetRid);
            if (_agentBufferRid.IsValid) _rd.FreeRid(_agentBufferRid);
            if (_gridBufferRid.IsValid) _rd.FreeRid(_gridBufferRid);
            if (_pipelineRid.IsValid) _rd.FreeRid(_pipelineRid);
            if (_shaderRid.IsValid) _rd.FreeRid(_shaderRid);
            _rd.Free();
            _rd = null;
        }
        IsInitialized = false;
    }
}