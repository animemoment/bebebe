using Game.Core;

namespace Game.Simulation.Gpu;

// Мост fBm GPU → MapGenerator (пункт 4).
// MapGenerator — чистый C# (Game.Core, без Godot), поэтому прямая зависимость
// Core → Simulation запрещена: вместо неё NoiseGenerator.FbmOverride (делегат).
// Enable ставит override, Disable снимает. Контекст персистентный: буферы
// fbm_out/fbm_grad переиспользуются между heightMap/groveMap/moisture.
// Из главного потока или фонового Task — мост только проксирует вызовы
// в GpuNoiseGenerator (у него свой CPU-fallback при недоступном GPU).
public static class GpuMapBridge
{
    private static readonly GpuComputeContext _ctx = new();

    // Включить GPU-путь для NoiseGenerator.GenerateFbmMap.
    public static void Enable()
    {
        NoiseGenerator.FbmOverride = (w, h, s, bs, o, l, g) =>
            GpuNoiseGenerator.GenerateFbmMapGpu(_ctx, w, h, s, bs, o, l, g);
    }

    // Снять override (вернуть чистый CPU-путь).
    public static void Disable()
    {
        NoiseGenerator.FbmOverride = null;
    }
}
