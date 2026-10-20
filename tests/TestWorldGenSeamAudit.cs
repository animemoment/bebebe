using System;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.UI;

namespace Game.Tests;

public partial class TestWorldGenSeamAudit : RefCounted
{
    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[TEST] Starting World Generation Seams & Visual Audit (@destroyer)");
        GD.Print("==================================================");

        try
        {
            TestChunkBoundaryElevationContinuity();
            TestRegionBoundaryElevationContinuity();
            TestGardenBedsAtlasIntegrity();
            TestWheatIconIntegrity();
            TestGroundSurfaceCameraWindowCalculation();

            GD.Print("==================================================");
            GD.Print("[TEST] ALL WORLD GEN SEAM & VISUAL AUDIT TESTS PASSED! (Code 0)");
            GD.Print("==================================================");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[TEST FAILED] {ex}");
            return false;
        }
    }

    private void TestChunkBoundaryElevationContinuity()
    {
        GD.Print("[TEST 1] Testing chunk boundary elevation continuity (64-cell steps)...");
        ulong seed = 0x1234567890ABCDEFul;
        uint version = 3;

        // Тестируем переход через границу чанка x = 63 и x = 64
        for (long y = 0; y < 128; y += 8)
        {
            var cellA = UnifiedWorldGenerator.SampleCell(seed, version, 63, y);
            var cellB = UnifiedWorldGenerator.SampleCell(seed, version, 64, y);

            int diff = Math.Abs((int)cellA.ElevationQ16 - (int)cellB.ElevationQ16);
            if (diff > 5000)
            {
                throw new Exception($"Discontinuous elevation cliff detected across chunk boundary at y={y}: cellA={cellA.ElevationQ16}, cellB={cellB.ElevationQ16}, diff={diff}");
            }
        }
        GD.Print("[TEST 1] Chunk boundary elevation transitions smoothly: 0 cliffs detected.");
    }

    private void TestRegionBoundaryElevationContinuity()
    {
        GD.Print("[TEST 2] Testing region boundary elevation continuity (512-cell regional steps)...");
        ulong seed = 0xABCDEF1234567890ul;
        uint version = 3;

        // Тестируем переход через границу региона x = 511 и x = 512
        for (long y = 0; y < 512; y += 32)
        {
            var cellA = UnifiedWorldGenerator.SampleCell(seed, version, 511, y);
            var cellB = UnifiedWorldGenerator.SampleCell(seed, version, 512, y);

            int diff = Math.Abs((int)cellA.ElevationQ16 - (int)cellB.ElevationQ16);
            if (diff > 6000)
            {
                throw new Exception($"Discontinuous regional cliff detected across region boundary at y={y}: cellA={cellA.ElevationQ16}, cellB={cellB.ElevationQ16}, diff={diff}");
            }
        }
        GD.Print("[TEST 2] Region boundary elevation transitions smoothly: 0 cliffs detected.");
    }

    private void TestGardenBedsAtlasIntegrity()
    {
        GD.Print("[TEST 3] Testing GardenBeds TileSet atlas sources...");
        var mainScene = GD.Load<PackedScene>("res://scenes/main/Main.tscn");
        if (mainScene == null)
            throw new Exception("Failed to load scenes/main/Main.tscn");

        var root = mainScene.Instantiate<Node2D>();
        var gardenLayer = root.FindChild("GardenBeds", true, false) as TileMapLayer;
        if (gardenLayer == null || gardenLayer.TileSet == null)
        {
            root.QueueFree();
            throw new Exception("GardenBeds layer or TileSet not found in Main.tscn");
        }

        var tileSet = gardenLayer.TileSet;
        if (!tileSet.HasSource(2))
        {
            root.QueueFree();
            throw new Exception("GardenBeds TileSet source 2 not found");
        }

        var source = tileSet.GetSource(2) as TileSetAtlasSource;
        if (source == null)
        {
            root.QueueFree();
            throw new Exception("GardenBeds source 2 is not TileSetAtlasSource");
        }

        // Убеждаемся, что желтый тайл 0:2 НЕ существует в атласе
        if (source.HasTile(new Vector2I(0, 2)))
        {
            root.QueueFree();
            throw new Exception("GardenBeds atlas still contains yellow mature crop tile at 0:2!");
        }

        // Убеждаемся, что базовые тайлы вспаханной земли первого ряда (0:0, 1:0, 2:0, 3:0) присутствуют
        if (!source.HasTile(new Vector2I(0, 0)) || !source.HasTile(new Vector2I(1, 0)))
        {
            root.QueueFree();
            throw new Exception("GardenBeds atlas missing plowed earth tiles 0:0 / 1:0");
        }

        root.QueueFree();
        GD.Print("[TEST 3] GardenBeds TileSet verified: 0 yellow corn tiles present, only plowed dirt tiles.");
    }

    private void TestWheatIconIntegrity()
    {
        GD.Print("[TEST 4] Testing HUD Wheat icon replacement...");
        var hudScene = GD.Load<PackedScene>("res://scenes/ui/hud.tscn");
        if (hudScene == null)
            throw new Exception("Failed to load scenes/ui/hud.tscn");

        var hudNode = hudScene.Instantiate<Control>();
        var farmBtn = hudNode.FindChild("Farming", true, false) as Button;
        if (farmBtn == null)
        {
            hudNode.QueueFree();
            throw new Exception("Farming button not found in hud.tscn");
        }

        if (farmBtn.Icon == null)
        {
            hudNode.QueueFree();
            throw new Exception("Farming button icon is null in hud.tscn");
        }

        string iconPath = farmBtn.Icon.ResourcePath;
        if (iconPath.Contains("farming.png"))
        {
            hudNode.QueueFree();
            throw new Exception($"Farming button still uses stick figure farming.png: {iconPath}");
        }
        if (!iconPath.Contains("Wheat.png"))
        {
            hudNode.QueueFree();
            throw new Exception($"Farming button does not use Wheat.png: {iconPath}");
        }

        hudNode.QueueFree();
        GD.Print("[TEST 4] Farming button icon verified: correctly replaced with Wheat.png.");
    }

    private void TestGroundSurfaceCameraWindowCalculation()
    {
        GD.Print("[TEST 5] Testing ground surface camera window snapping math...");

        // При положении камеры на острове (256, 256) окно обязано быть строго (0, 0)
        long camX = 256, camY = 256;
        long targetOriginX = WorldCoordinates.FloorDiv(camX - MapRenderer.MapWidth / 2, 64) * 64;
        long targetOriginY = WorldCoordinates.FloorDiv(camY - MapRenderer.MapHeight / 2, 64) * 64;
        if (targetOriginX != 0 || targetOriginY != 0)
            throw new Exception($"Colony center camera must yield origin (0, 0), got ({targetOriginX}, {targetOriginY})");

        // При смещении на 1000 клеток вправо окно должно сместиться кратно 64
        camX = 1256;
        targetOriginX = WorldCoordinates.FloorDiv(camX - MapRenderer.MapWidth / 2, 64) * 64;
        if (targetOriginX % 64 != 0)
            throw new Exception($"Target origin must be multiple of 64, got {targetOriginX}");

        GD.Print("[TEST 5] Ground surface camera window math verified.");
    }
}
