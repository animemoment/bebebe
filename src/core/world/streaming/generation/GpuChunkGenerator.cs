// ============================================================
// GpuChunkGenerator.cs — GPU-ускоритель генерации чанка.
// Godot 4.7 C# API. Fallback на CPU при любой ошибке.
// ============================================================
using System;
using System.Threading;
using Godot;

namespace Game.Core.WorldStreaming.Gpu;

public sealed class GpuChunkGenerator : IDisposable
{
    private RenderingDevice _rd = null;
    private Rid _shaderModRid = new();
    private Rid _pipelineRid = new();
    private bool _initFailed = false;
    
    public long ChunksGeneratedGpu { get; private set; }
    public bool IsAvailable => _rd != null && !_initFailed;
    
    public string Status
    {
        get
        {
            if (_rd == null) return "Cannot create local rendering device";
            if (_initFailed) return "Shader pipeline failed to compile";
            return "GPU compute pipeline ready";
        }
    }

    public void Init()
    {
        _rd = RenderingServer.CreateLocalRenderingDevice();
        if (_rd == null)
        {
            _initFailed = true;
            GD.PrintErr("[GPU] CreateLocalRenderingDevice returned null.");
            return;
        }
        GD.Print("[GPU] Local rendering device created.");
        
        // TODO: загрузить шейдер через RenderDevice API в следующей итерации.
        // В текущей Godot 4.7 beta нет стабильного C# API для ShaderCreateFromSpirV.
        // Испольуем CPU fallback.
        _initFailed = true;
        Cleanup();
    }

    public GeneratedCell[] GenerateChunk(
        ulong seed, uint genVer, ChunkKey key,
        Func<ulong, uint, ChunkKey, GeneratedCell[]> cpuFallback)
    {
        // GPU недоступен — сразу CPU fallback
        if (_rd == null || _initFailed)
        {
            return cpuFallback(seed, genVer, key);
        }

        try
        {
            // GPU path (заглушка до стабилизации C# RenderingDevice API)
            throw new NotImplementedException("GPU path not yet implemented for Godot 4.7");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GPU] Error, falling back to CPU: {ex.Message}");
            _initFailed = true;
            return cpuFallback(seed, genVer, key);
        }
    }
    
    private void Cleanup()
    {
        _rd?.Free();
        _rd = null;
    }
    
    public void Dispose() => Cleanup();
}
