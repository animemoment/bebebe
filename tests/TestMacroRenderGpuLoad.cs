using System;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.UI;
using Game.UI.Streaming;

namespace Game.Tests;

public partial class TestMacroRenderGpuLoad : Node2D
{
    private CameraController _camera;
    private AgentSimulationThread _simThread;
    private AgentRenderer _agentRenderer;
    private ShadowCasterRenderer _shadowCaster;
    private ItemShadowRenderer _itemShadows;
    private StreamingWorldView _worldView;
    private int _framesProcessed = 0;
    private const int TargetFrames = 120; // ~2 секунды при 60 FPS для стабильного бенчмарка

    public override void _ExitTree()
    {
        try
        {
            _simThread?.Stop();
            _simThread?.Dispose();
        }
        catch
        {
            // ignored
        }
    }

    public override void _Ready()
    {
        GD.Print("==================================================");
        GD.Print("[GPU BENCH] Initializing Full Macro Render Scene...");
        GD.Print("==================================================");

        // 1. Камера на максимальном отдалении (Zoom = 0.12)
        _camera = new CameraController { Name = "Camera" };
        AddChild(_camera);
        _camera.MakeCurrent();
        _camera.Position = new Vector2(
            MapRenderer.MapWidth * MapRenderer.TileSizePx / 2f,
            MapRenderer.MapHeight * MapRenderer.TileSizePx / 2f);
        _camera.Zoom = new Vector2(0.12f, 0.12f);

        // 2. Стриминговый мир
        _worldView = new StreamingWorldView
        {
            Name = "StreamingWorldView",
            WorldSeed = 0x51EED4C0FFEEUL,
            GeneratorVersion = 3,
            ZIndex = -1,
            Visible = true
        };
        AddChild(_worldView);

        // 3. Симуляция 10,000 агентов
        const int agentCount = 10000;
        _simThread = new AgentSimulationThread();
        var ground = new TileType[MapRenderer.MapWidth, MapRenderer.MapHeight];
        var trees = new bool[MapRenderer.MapWidth, MapRenderer.MapHeight];
        var stones = new bool[MapRenderer.MapWidth, MapRenderer.MapHeight];
        var hum = new byte[MapRenderer.MapWidth, MapRenderer.MapHeight];
        var fert = new byte[MapRenderer.MapWidth, MapRenderer.MapHeight];
        for (int x = 0; x < MapRenderer.MapWidth; x++)
        {
            for (int y = 0; y < MapRenderer.MapHeight; y++)
            {
                ground[x, y] = TileType.Grass;
                if ((x + y) % 17 == 0) trees[x, y] = true;
            }
        }
        _simThread.Start(agentCount, ground, trees, 0);

        // 4. AgentRenderer с 10,000 агентов
        _agentRenderer = new AgentRenderer { Name = "AgentRenderer" };
        AddChild(_agentRenderer);
        _agentRenderer.Initialize(_simThread, agentCount);

        // 5. Тени статики
        _shadowCaster = new ShadowCasterRenderer { Name = "ShadowCasterRenderer" };
        AddChild(_shadowCaster);
        for (int i = 0; i < 500; i++)
        {
            _shadowCaster.AddCaster(i % 512, (i * 7) % 512);
        }

        // 6. Тени предметов
        _itemShadows = new ItemShadowRenderer { Name = "ItemShadowRenderer" };
        AddChild(_itemShadows);

        GD.Print("[GPU BENCH] Scene nodes added. Starting frame processing...");
    }

    public override void _Process(double delta)
    {
        try
        {
            _framesProcessed++;

            // Обновляем тени и циклы
            var sun = new DayNightCycle.SunState(new Vector2(1f, 0.5f).Normalized(), 24f, 0.55f, 0.4f);
            _shadowCaster.Tick(sun, (float)delta);
            _agentRenderer.ApplyShadow(sun);
            _itemShadows.SetSun(sun);
            _itemShadows.BeginFrame();
            _itemShadows.Flush();

            if (_framesProcessed % 30 == 0)
            {
                double fps = Performance.GetMonitor(Performance.Monitor.TimeFps);
                double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
                double objects = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
                double primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
                double vramMb = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0);

                GD.Print($"[GPU BENCH F#{_framesProcessed}] FPS: {fps:F0} | DrawCalls: {drawCalls} | Objects: {objects} | Primitives: {primitives} | VRAM: {vramMb:F1} MB | LOD: {_agentRenderer.CurrentLod}");
            }

            if (_framesProcessed >= TargetFrames)
            {
                // Финальная проверка соблюдения условий
                GD.Print("==================================================");
                GD.Print("[GPU BENCH] Final Audit Verification:");
                GD.Print($"[GPU BENCH] Agent LOD State: {_agentRenderer.CurrentLod} (Expected: MacroDots)");
                GD.Print($"[GPU BENCH] Agent Shadows Enabled: {_agentRenderer.ShadowsEnabled} (Expected: False)");
                GD.Print($"[GPU BENCH] Static Shadows Visible: {_shadowCaster.Visible} (Expected: False)");
                GD.Print($"[GPU BENCH] Item Shadows Visible: {_itemShadows.Visible} (Expected: False)");

                if (_agentRenderer.CurrentLod != AgentRenderer.AgentRenderLod.MacroDots)
                    throw new Exception($"Agent LOD state mismatch: expected MacroDots, got {_agentRenderer.CurrentLod}");
                if (_agentRenderer.ShadowsEnabled)
                    throw new Exception("Agent shadows were not disabled at zoom 0.12!");
                if (_shadowCaster.Visible)
                    throw new Exception("Static shadows were not disabled at zoom 0.12!");
                if (_itemShadows.Visible)
                    throw new Exception("Item shadows were not disabled at zoom 0.12!");

                GD.Print("[GPU BENCH] ALL AUDIT CHECKS PASSED SUCCESSFULLY!");
                GD.Print("==================================================");

                _simThread.Stop();
                GetTree().Quit(0);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GPU BENCH FAILED] {ex}");
            try { _simThread?.Stop(); } catch { }
            GetTree().Quit(1);
        }
    }
}
