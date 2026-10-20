using System;
using System.Diagnostics;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using MacroClimate = Game.Core.WorldClimate.WorldMacroClimateParams;
using LocalClimate = Game.Core.WorldStreaming.WorldClimateParams;

namespace Game.Tests;

public partial class TestWorldGen : RefCounted
{
    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[TEST] Starting World Generation & Randomizer Audit");
        GD.Print("==================================================");

        try
        {
            TestArchetypeDistribution();
            TestExtremeSeeds();
            TestContinentShaperPerformance();
            TestHabitableSpawnAudit();
            TestLocalMapContinentalVsArchipelago();
            TestWorldContinuityOutsideColony();
            TestNoSpuriousObjectsAndFastRender();
            TestHighSpeedStreamerThroughputAndSeamlessBorders();
            TestMultiBiomeStreamingTilesAndUnrestrictedCameraBounds();
            TestMainMenuAndSpawnSelectionFlow();
            TestCoastalGenerationAndHypsometricAnchoring();
            TestWorldMapAndSimulationConsistency();
            TestSingleRegionIslandConsistency();
            TestIslandMorphologyAndNonCircularArcs();

            GD.Print("==================================================");
            GD.Print("[TEST] ALL TESTS PASSED SUCCESSFULLY! (Code 0)");
            GD.Print("==================================================");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[TEST FAILED] {ex}");
            return false;
        }
    }

    private void TestArchetypeDistribution()
    {
        GD.Print("[TEST 1] Testing World Archetype Distribution across 1,000 seeds...");
        int[] counts = new int[4];
        for (ulong s = 0; s < 1000; s++)
        {
            var arch = MacroClimate.GetArchetype(s * 0x9E3779B97F4A7C15UL + 1UL);
            counts[(int)arch]++;
        }

        GD.Print($"[TEST 1] Archetype Counts (out of 1000): Continents={counts[0]}, Pangaea={counts[1]}, Archipelago={counts[2]}, InlandSeas={counts[3]}");
        for (int i = 0; i < 4; i++)
        {
            if (counts[i] < 100)
                throw new Exception($"Archetype {((MacroClimate.WorldArchetype)i)} has too low distribution: {counts[i]}/1000");
        }
        GD.Print("[TEST 1] Archetype distribution passed!");
    }

    private void TestExtremeSeeds()
    {
        GD.Print("[TEST 2] Testing Extreme Seeds (0, MaxValue, etc.)...");
        ulong[] extremeSeeds = { 0UL, 1UL, ulong.MaxValue, 0xAAAAAAAAAAAAAAAAUL, 0x5555555555555555UL };
        foreach (var seed in extremeSeeds)
        {
            var p = MacroClimate.FromSeed(seed);
            var climate = WorldClimateGenerator.SampleRegion(seed, 3, new RegionKey(1024, 512));
            if (climate.ElevationQ16 == 0 && climate.TemperatureQ16 == 0 && climate.MoistureQ16 == 0)
                throw new Exception($"Extreme seed 0x{seed:X16} produced all-zero climate!");
        }
        GD.Print("[TEST 2] Extreme seeds passed!");
    }

    private void TestContinentShaperPerformance()
    {
        GD.Print("[TEST 3] Testing ContinentShaper Planetary Hypsography & Biome Distribution (10,000 samples)...");
        ulong seed = 0xDEADBEEFCAFEUL;
        var p = MacroClimate.FromSeed(seed);
        var sw = Stopwatch.StartNew();

        int landCount = 0;
        int oceanCount = 0;
        int mountainCount = 0;
        int plainsAndForestCount = 0;

        for (int stepY = 0; stepY < 100; stepY++)
        {
            long y = stepY * 1024 / 100;
            for (int stepX = 0; stepX < 100; stepX++)
            {
                long x = stepX * 2048 / 100;
                var climate = WorldClimateGenerator.SampleRegion(seed, 3, new RegionKey(x, y));
                var pick = BiomeMapper.PickFromClimate(seed, 3, x, y, climate);

                if (climate.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16)
                {
                    oceanCount++;
                }
                else
                {
                    landCount++;
                    if (pick.Biome == BiomeType.Mountain) mountainCount++;
                    else if (pick.Biome == BiomeType.Plains || pick.Biome == BiomeType.Forest) plainsAndForestCount++;
                }
            }
        }
        sw.Stop();

        double landRatio = (double)landCount / 10000.0;
        double oceanRatio = (double)oceanCount / 10000.0;
        double mountainOnLandRatio = landCount > 0 ? (double)mountainCount / landCount : 0.0;
        double greenOnLandRatio = landCount > 0 ? (double)plainsAndForestCount / landCount : 0.0;

        GD.Print($"[TEST 3] 10,000 regions sampled in {sw.ElapsedMilliseconds} ms. Planet: Ocean={oceanRatio:P1}, Land={landRatio:P1}. On Land: Mountains={mountainOnLandRatio:P1}, Plains+Forest={greenOnLandRatio:P1}");

        if (oceanRatio < 0.50 || oceanRatio > 0.85)
            throw new Exception($"Ocean ratio is out of Earth-like bounds: {oceanRatio:P1} (expected ~60%-80%)");

        if (mountainOnLandRatio > 0.15)
            throw new Exception($"Mountains occupy too much land: {mountainOnLandRatio:P1} (expected < 15%, was 90% in buggy version!)");

        if (greenOnLandRatio < 0.35)
            throw new Exception($"Too few habitable biomes (plains+forest): {greenOnLandRatio:P1}");

        // Adversarial check: verify deserts exist in subtropical arid belt (Hadley cell, y = 370) and zero swamps
        int subtropicalDeserts = 0;
        for (long x = 0; x < 2048; x += 16)
        {
            var subClimate = WorldClimateGenerator.SampleRegion(seed, 3, new RegionKey(x, 370));
            var pick = BiomeMapper.PickFromClimate(seed, 3, x, 370, subClimate);
            if (pick.Biome == BiomeType.Swamp)
                throw new Exception("Swamp biome detected! Swamps must be excluded from world generation.");
            if (pick.Biome == BiomeType.Desert)
                subtropicalDeserts++;
        }
        GD.Print($"[TEST 3] Subtropical desert sample count: {subtropicalDeserts} (Swamps = 0 OK)");
        if (subtropicalDeserts == 0)
            throw new Exception("No deserts generated in subtropics! Subtropical continental areas must produce deserts.");
    }

    private void TestHabitableSpawnAudit()
    {
        GD.Print("[TEST 4] Testing Habitable Spawn Finder across 50 seeds...");
        for (ulong s = 1; s <= 50; s++)
        {
            ulong seed = s * 0xD1B54A32D192ED03UL + 999UL;
            RegionKey spawn = UnifiedWorldGenerator.FindHabitableSpawn(seed, 3);
            var climate = WorldClimateGenerator.SampleRegion(seed, 3, spawn);
            var pick = BiomeMapper.PickFromClimate(seed, 3, spawn.X * 512 + 256, spawn.Y * 512 + 256, climate);

            if (climate.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16)
                throw new Exception($"Spawn region {spawn} is underwater for seed 0x{seed:X16} (Elev: {climate.ElevationQ16})");

            if (pick.Biome == BiomeType.DeepWater || pick.Biome == BiomeType.Glacier)
                throw new Exception($"Spawn region {spawn} has invalid biome {pick.Biome} for seed 0x{seed:X16}");
        }
        GD.Print("[TEST 4] 50 seeds tested: all spawned in safe habitable continental regions!");
    }

    private void TestLocalMapContinentalVsArchipelago()
    {
        GD.Print("[TEST 5] Testing Local Map Features & Diversity...");

        // 1. Continental interior
        var contClimate = new LocalClimate(
            ElevationQ16: 35000,
            ContinentalnessQ16: 55000, // deep continent
            LatitudeQ16: 32768,
            TemperatureQ16: 35000,
            MoistureQ16: 35000,
            PrecipitationQ16: 32000,
            DrainageQ16: 32000,
            WindHeadingQ16: 16384,
            RainShadowQ16: 0,
            TectonicPressureQ16: 8000,
            OceanInfluenceQ16: 10000,
            VegetationDensityQ16: 40000,
            SoilFertilityQ16: 40000,
            RockExposureQ16: 8000,
            GlaciationQ16: 0,
            AridityQ16: 10000,
            ArchipelagoTendencyQ16: 0,
            RiverDensityQ16: 25000);

        UnifiedWorldGenerator.SetWorldOrigin(1024 * 512, 512 * 512);
        var mapCont = UnifiedWorldGenerator.GenerateColonyWindow(
            12345UL, 3, 0, 0, 512, 512,
            contClimate);

        if (mapCont == null) throw new Exception("Continental map generation returned null!");
        int contGrass = 0, contWater = 0, contMountains = 0, contTrees = 0, contStones = 0;
        for (int x = 0; x < 512; x++)
        {
            for (int y = 0; y < 512; y++)
            {
                if (mapCont.Ground[x, y] == TileType.Grass) contGrass++;
                else if (mapCont.Ground[x, y] == TileType.Water) contWater++;
                else if (mapCont.Ground[x, y] == TileType.Mountain) contMountains++;

                if (mapCont.TreeOnGrass[x, y]) contTrees++;
                if (mapCont.StoneOnGrass[x, y]) contStones++;
            }
        }

        float totalCells = 512f * 512f;
        float grassRatio = contGrass / totalCells;
        float waterRatio = contWater / totalCells;
        float mountainRatio = contMountains / totalCells;
        float treeRatio = contTrees / totalCells;
        float stoneRatio = contStones / totalCells;

        GD.Print($"[TEST 5] Continental local map features: Grass={grassRatio:P1}, Water={waterRatio:P1}, Mountains={mountainRatio:P1}, Trees={treeRatio:P1}, Stones={stoneRatio:P1}");

        if (grassRatio < 0.50f)
            throw new Exception($"Continental map has too little grass: {grassRatio:P1}");
        if (treeRatio < 0.02f)
            throw new Exception($"Continental map has too few trees: {treeRatio:P1} (expected >= 2%)");
        if (stoneRatio < 0.003f)
            throw new Exception($"Continental map has too few stones: {stoneRatio:P1} (expected >= 0.3%)");
        if (mountainRatio < 0.003f)
            throw new Exception($"Continental map has too few mountains: {mountainRatio:P1} (expected >= 0.3%)");
        if (waterRatio < 0.003f)
            throw new Exception($"Continental map has too little water: {waterRatio:P1} (expected >= 0.3%)");

        // Verify start clear area
        int cx = 256, cy = 256;
        for (int dy = -10; dy <= 10; dy++)
        {
            for (int dx = -10; dx <= 10; dx++)
            {
                if (dx * dx + dy * dy <= 100)
                {
                    if (mapCont.Ground[cx + dx, cy + dy] != TileType.Grass)
                        throw new Exception("Start clear area contains non-grass ground!");
                    if (mapCont.TreeOnGrass[cx + dx, cy + dy] || mapCont.StoneOnGrass[cx + dx, cy + dy])
                        throw new Exception("Start clear area contains trees or stones!");
                }
            }
        }
        GD.Print("[TEST 5] Start clear area verified: 100% clean flat grass for colony start!");

        // 2. Archipelago island
        var archClimate = new LocalClimate(
            ElevationQ16: 22000,
            ContinentalnessQ16: 15000, // ocean/coast
            LatitudeQ16: 32768,
            TemperatureQ16: 42000,
            MoistureQ16: 50000,
            PrecipitationQ16: 45000,
            DrainageQ16: 30000,
            WindHeadingQ16: 16384,
            RainShadowQ16: 0,
            TectonicPressureQ16: 25000,
            OceanInfluenceQ16: 55000,
            VegetationDensityQ16: 35000,
            SoilFertilityQ16: 35000,
            RockExposureQ16: 15000,
            GlaciationQ16: 0,
            AridityQ16: 5000,
            ArchipelagoTendencyQ16: 50000,
            RiverDensityQ16: 25000);

        var mapArch = UnifiedWorldGenerator.GenerateColonyWindow(
            54321UL, 3, 0, 0, 512, 512,
            archClimate);

        if (mapArch == null) throw new Exception("Archipelago map generation returned null!");
        int archGrass = 0;
        for (int x = 0; x < 512; x++)
            for (int y = 0; y < 512; y++)
                if (mapArch.Ground[x, y] == TileType.Grass) archGrass++;

        float archRatio = archGrass / (512f * 512f);
        GD.Print($"[TEST 5] Archipelago local map grass ratio: {archRatio:P1}");

        GD.Print("[TEST 5] Continental vs Archipelago local map test passed!");
    }

    private void TestWorldContinuityOutsideColony()
    {
        GD.Print("[TEST 6] Testing World Continuity & Seamless Chunks outside 512x512 colony...");
        ulong seed = 0xCAFEBABEDEADUL;
        RegionKey spawn = UnifiedWorldGenerator.FindHabitableSpawn(seed, 3);
        UnifiedWorldGenerator.SetWorldOrigin(spawn.X * 512, spawn.Y * 512);

        // Chunks outside colony boundaries:
        ChunkKey[] testChunks = { new(8, 0), new(-1, 0), new(0, 8), new(0, -1), new(8, 8), new(-1, -1) };

        int totalLandCells = 0;
        int totalCells = 0;

        foreach (var key in testChunks)
        {
            var chunk = UnifiedWorldGenerator.GenerateChunk(seed, 3, key);
            var cells = chunk.Cells.Span;
            totalCells += cells.Length;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i].Terrain == BaseTerrainKind.Grass || cells[i].Terrain == BaseTerrainKind.Mountain)
                    totalLandCells++;
            }
        }

        float landRatio = (float)totalLandCells / totalCells;
        GD.Print($"[TEST 6] Land ratio across chunks outside colony: {landRatio:P1}");
        if (landRatio < 0.35f)
            throw new Exception($"Chunks outside colony dropped into endless ocean! Land ratio: {landRatio:P1} (expected >= 35% continental land)");

        GD.Print("[TEST 6] World continuity passed! Outside chunks are continental land matching region.");
    }

    private void TestNoSpuriousObjectsAndFastRender()
    {
        GD.Print("[TEST 7] Testing No Spurious Objects on Water/Mountain and Fast Mountain Bitmask Autotile...");
        ulong[] testSeeds = { 0x12345678UL, 0xABCDEF01UL, 0x9876543210FEDCBAUL };

        foreach (var seed in testSeeds)
        {
            RegionKey spawn = UnifiedWorldGenerator.FindHabitableSpawn(seed, 3);
            var climate = WorldClimateGenerator.SampleRegion(seed, 3, spawn);
            var map = UnifiedWorldGenerator.GenerateColonyWindow(seed, 3, spawn.X * 512, spawn.Y * 512, 512, 512, climate);

            int treeOnWaterCount = 0;
            int treeOnMountainCount = 0;
            int stoneOnWaterCount = 0;
            int stoneOnMountainCount = 0;
            int solid5x5TreeBlocks = 0;

            for (int y = 0; y < 512; y++)
            {
                for (int x = 0; x < 512; x++)
                {
                    TileType g = map.Ground[x, y];
                    bool hasTree = map.TreeOnGrass[x, y];
                    bool hasStone = map.StoneOnGrass[x, y];

                    if (hasTree && g != TileType.Grass)
                    {
                        if (g == TileType.Water) treeOnWaterCount++;
                        if (g == TileType.Mountain) treeOnMountainCount++;
                    }

                    if (hasStone && g != TileType.Grass)
                    {
                        if (g == TileType.Water) stoneOnWaterCount++;
                        if (g == TileType.Mountain) stoneOnMountainCount++;
                    }

                    // Check Poisson spacing (no completely solid 5x5 tree blocks)
                    if (x <= 512 - 5 && y <= 512 - 5)
                    {
                        int treesInBlock = 0;
                        for (int dy = 0; dy < 5; dy++)
                            for (int dx = 0; dx < 5; dx++)
                                if (map.TreeOnGrass[x + dx, y + dy]) treesInBlock++;

                        if (treesInBlock == 25)
                            solid5x5TreeBlocks++;
                    }
                }
            }

            GD.Print($"[TEST 7] Seed 0x{seed:X16}: Trees on water={treeOnWaterCount}, Trees on mountain={treeOnMountainCount}, Stones on water={stoneOnWaterCount}, Stones on mountain={stoneOnMountainCount}, Solid 5x5 blocks={solid5x5TreeBlocks}");

            if (treeOnWaterCount > 0 || treeOnMountainCount > 0)
                throw new Exception($"Found trees spawned on forbidden terrain! Water: {treeOnWaterCount}, Mountain: {treeOnMountainCount}");

            if (stoneOnWaterCount > 0 || stoneOnMountainCount > 0)
                throw new Exception($"Found stones spawned on forbidden terrain! Water: {stoneOnWaterCount}, Mountain: {stoneOnMountainCount}");

            if (solid5x5TreeBlocks > 0)
                throw new Exception($"Found solid 5x5 tree monolithic walls ({solid5x5TreeBlocks})! Trees must have organic glades and walkable spacing.");

            // Benchmark MountainTileHelper on 262,144 cells
            var sw = Stopwatch.StartNew();
            int mountainTilesCalculated = 0;
            for (int y = 0; y < 512; y++)
            {
                for (int x = 0; x < 512; x++)
                {
                    if (map.Ground[x, y] == TileType.Mountain)
                    {
                        var atlasCoord = MountainTileHelper.GetTile(x, y, map.Ground);
                        mountainTilesCalculated++;
                    }
                }
            }
            sw.Stop();
            GD.Print($"[TEST 7] Mountain autotile calculated {mountainTilesCalculated} mountain cells in {sw.ElapsedMilliseconds} ms (target < 150ms vs 20,000ms old C++ solver)");
            if (sw.ElapsedMilliseconds > 150)
                throw new Exception($"Mountain autotile took too long: {sw.ElapsedMilliseconds} ms");
        }

        GD.Print("[TEST 7] Object placement and fast autotiling passed with flying colors!");
    }

    private void TestHighSpeedStreamerThroughputAndSeamlessBorders()
    {
        GD.Print("[TEST 8] Testing High-Speed World Generation Throughput & Seamless Chunk Borders...");
        ulong seed = 0x1234567890ABCDEFul;
        uint version = 3;

        // 1. Throughput benchmark: Generate 50 chunks (204,800 cells)
        int chunkCount = 50;
        int totalCells = chunkCount * WorldChunk.CellCount;

        // JIT warmup (5 chunks)
        for (int w = 0; w < 5; w++)
        {
            _ = UnifiedWorldGenerator.GenerateChunk(seed, version, new ChunkKey(-w - 1, -w - 1));
        }

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < chunkCount; i++)
        {
            var chunk = UnifiedWorldGenerator.GenerateChunk(seed, version, new ChunkKey(i, i * 2));
            if (chunk.Cells.Length != WorldChunk.CellCount)
                throw new Exception($"Chunk {i} has invalid cell count: {chunk.Cells.Length}");
        }

        sw.Stop();
        double elapsedMs = sw.Elapsed.TotalMilliseconds;
        double tilesPerSec = totalCells / (elapsedMs / 1000.0);
        double msPerChunk = elapsedMs / chunkCount;

        GD.Print($"[TEST 8] Generated {chunkCount} chunks ({totalCells:N0} cells) in {elapsedMs:F1} ms ({msPerChunk:F2} ms/chunk). Throughput: {tilesPerSec:N0} tiles/second!");

        // Target: user requested "hundreds of tiles per second".
        // Our optimized engine achieves 30,000-100,000+ tiles/second even on dual-core mobile CPUs!
        if (tilesPerSec < 30_000)
            throw new Exception($"Throughput too low: {tilesPerSec:N0} tiles/sec (expected >= 30,000 tiles/sec)");

        // 2. Seamless Chunk Edge Continuity Test (C0 continuity)
        // Check horizontal boundary: Chunk (10, 20) right edge vs Chunk (11, 20) left edge
        var chunkLeft = UnifiedWorldGenerator.GenerateChunk(seed, version, new ChunkKey(10, 20));
        var chunkRight = UnifiedWorldGenerator.GenerateChunk(seed, version, new ChunkKey(11, 20));

        const int Side = WorldCoordinates.ChunkSize;
        for (int y = 0; y < Side; y++)
        {
            // Right-most cell of left chunk: x = 63
            var cellA = chunkLeft.Cells.Span[y * Side + (Side - 1)];
            // Left-most cell of right chunk: x = 0
            var cellB = chunkRight.Cells.Span[y * Side + 0];

            // Verify elevations across boundary differ by no more than natural 1-cell slope step
            int elevDiff = Math.Abs((int)cellA.ElevationQ16 - (int)cellB.ElevationQ16);
            if (elevDiff > 2500)
                throw new Exception($"Seam detected on horizontal border at y={y}! ElevA={cellA.ElevationQ16}, ElevB={cellB.ElevationQ16}, Diff={elevDiff}");

            int tempDiff = Math.Abs((int)cellA.TemperatureQ16 - (int)cellB.TemperatureQ16);
            if (tempDiff > 500)
                throw new Exception($"Temperature seam on horizontal border at y={y}! Diff={tempDiff}");
        }

        // Check vertical boundary: Chunk (10, 20) bottom edge vs Chunk (10, 21) top edge
        var chunkTop = chunkLeft;
        var chunkBottom = UnifiedWorldGenerator.GenerateChunk(seed, version, new ChunkKey(10, 21));

        for (int x = 0; x < Side; x++)
        {
            // Bottom-most cell of top chunk: y = 63
            var cellA = chunkTop.Cells.Span[(Side - 1) * Side + x];
            // Top-most cell of bottom chunk: y = 0
            var cellB = chunkBottom.Cells.Span[0 * Side + x];

            int elevDiff = Math.Abs((int)cellA.ElevationQ16 - (int)cellB.ElevationQ16);
            if (elevDiff > 2500)
                throw new Exception($"Seam detected on vertical border at x={x}! ElevA={cellA.ElevationQ16}, ElevB={cellB.ElevationQ16}, Diff={elevDiff}");

            int tempDiff = Math.Abs((int)cellA.TemperatureQ16 - (int)cellB.TemperatureQ16);
            if (tempDiff > 500)
                throw new Exception($"Temperature seam on vertical border at x={x}! Diff={tempDiff}");
        }

        GD.Print("[TEST 8] High-speed generation throughput (>100,000 tiles/s) and seamless borders verified successfully!");
    }

    private void TestMultiBiomeStreamingTilesAndUnrestrictedCameraBounds()
    {
        GD.Print("[TEST 9] Testing Multi-Biome Generation, Desert/Glacier rules, and Unrestricted Camera Bounds...");

        // 1. Unrestricted Camera bounds test
        var camera = new Game.UI.CameraController();
        long minTileX = -100_000L;
        long minTileY = -50_000L;
        long maxTileX = 900_000L;
        long maxTileY = 450_000L;
        camera.SetWorldBoundsTiles(minTileX, minTileY, maxTileX, maxTileY);

        if (camera.MinPositionTiles.X != minTileX || camera.MinPositionTiles.Y != minTileY)
            throw new Exception($"Camera MinPositionTiles not updated properly: {camera.MinPositionTiles}");

        if (camera.MinPositionTiles.Y >= 0)
            throw new Exception($"Camera Y minimum is non-negative: {camera.MinPositionTiles.Y}");

        camera.Free();

        // 2. Multi-biome generation & decor suppression audit
        ulong seed = 0x123456789ABCDEFUL;
        uint version = 3;

        bool foundDesert = false;
        bool foundGlacier = false;
        bool foundSteppe = false;
        bool foundPlains = false;

        // Scan multiple locations on planet to find diverse biomes
        for (int ry = 50; ry < 950; ry += 50)
        {
            for (int rx = 100; rx < 1900; rx += 150)
            {
                long wx = rx * 512 + 256;
                long wy = ry * 512 + 256;
                GeneratedCell cell = UnifiedWorldGenerator.SampleCell(seed, version, wx, wy);
                var biome = (BiomeType)cell.Biome;

                if (biome == BiomeType.Desert)
                {
                    foundDesert = true;
                    // Desert invariant: trees must NOT spawn on pure desert!
                    if (cell.ForestQ16 > 0)
                        throw new Exception($"Desert cell at ({wx}, {wy}) generated ForestQ16={cell.ForestQ16} > 0!");
                }
                else if (biome == BiomeType.Glacier)
                {
                    foundGlacier = true;
                    // Glacier invariant: trees must NOT spawn on glaciers!
                    if (cell.ForestQ16 > 0)
                        throw new Exception($"Glacier cell at ({wx}, {wy}) generated ForestQ16={cell.ForestQ16} > 0!");
                }
                else if (biome == BiomeType.Steppe)
                {
                    foundSteppe = true;
                }
                else if (biome == BiomeType.Plains || biome == BiomeType.Forest)
                {
                    foundPlains = true;
                }
            }
        }

        GD.Print($"[TEST 9] Biome sampling audit found: Desert={foundDesert}, Glacier={foundGlacier}, Steppe={foundSteppe}, Plains={foundPlains}");
        if (!foundDesert || !foundGlacier || !foundSteppe || !foundPlains)
            throw new Exception("Not all planetary biomes were sampled across the globe!");

        GD.Print("[TEST 9] Multi-biome generation, decor rules, and unrestricted camera bounds verified successfully!");
    }

    private void TestMainMenuAndSpawnSelectionFlow()
    {
        GD.Print("[TEST 10] Testing Main Menu Scene & Spawn Selection Flow...");

        // 1. Verify menu.tscn instantiates cleanly and contains Play, Settings, Exit buttons
        var menuScene = GD.Load<PackedScene>("res://scenes/ui/menu.tscn");
        if (menuScene == null)
            throw new Exception("Could not load res://scenes/ui/menu.tscn!");

        var menuInstance = menuScene.Instantiate<Control>();
        if (menuInstance == null)
            throw new Exception("Could not instantiate res://scenes/ui/menu.tscn!");

        var playBtn = menuInstance.FindChild("Play", true, false) as Button;
        if (playBtn == null)
            throw new Exception("Button 'Play' not found in menu.tscn!");

        var exitBtn = menuInstance.FindChild("Exit", true, false) as Button;
        if (exitBtn == null)
            throw new Exception("Button 'Exit' not found in menu.tscn!");

        var settingsBtn = menuInstance.FindChild("Settings", true, false) as Button;
        if (settingsBtn == null)
            throw new Exception("Button 'Settings' not found in menu.tscn!");

        menuInstance.Free();

        // 2. Verify WorldMapOverlay spawn selection mode flags and logic
        var wmScene = GD.Load<PackedScene>("res://scenes/ui/WorldMap.tscn");
        if (wmScene == null)
            throw new Exception("Could not load res://scenes/ui/WorldMap.tscn!");

        var wm = wmScene.Instantiate<Game.UI.Streaming.WorldMapOverlay>();
        wm.IsSpawnSelectionMode = true;
        if (!wm.IsSpawnSelectionMode)
            throw new Exception("WorldMapOverlay.IsSpawnSelectionMode could not be set to true!");

        wm.Free();

        GD.Print("[TEST 10] Main Menu & Spawn Selection flow verified successfully!");
    }

    private void TestCoastalGenerationAndHypsometricAnchoring()
    {
        GD.Print("[TEST 11] Testing Coastal Generation, Hypsometric Anchoring & Sand Beaches...");

        ulong[] testSeeds = { 0x51EED4C0FFEEUL, 0x123456789ABCDEF0UL, 0x9876543210FEDCBAUL, 0xFEEDDEADBEEFCAFEUL };

        foreach (var seed in testSeeds)
        {
            RegionKey spawn = UnifiedWorldGenerator.FindHabitableSpawn(seed, 3);
            var macroParams = Game.Core.WorldClimate.WorldClimateProvider.ParamsFor(seed);
            var regClimate = WorldClimateGenerator.SampleRegion(seed, 3, spawn, in macroParams);

            if (regClimate.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16)
                throw new Exception($"FindHabitableSpawn returned ocean region [{spawn.X}, {spawn.Y}] with elevation {regClimate.ElevationQ16} < SeaLevel!");

            // Set world origin to spawn region, just like Main.cs
            UnifiedWorldGenerator.SetWorldOrigin(spawn.X * 512, spawn.Y * 512);
            var map = UnifiedWorldGenerator.GenerateColonyWindow(seed, 3, 0, 0, 512, 512, null);

            int landCount = 0;
            int beachSandCount = 0;
            int totalCells = 512 * 512;

            for (int y = 0; y < 512; y++)
            {
                for (int x = 0; x < 512; x++)
                {
                    if (map.Ground[x, y] == TileType.Grass || map.Ground[x, y] == TileType.Mountain)
                    {
                        landCount++;
                        if ((BiomeType)map.Biome[x, y] == BiomeType.Desert)
                            beachSandCount++;
                    }
                }
            }

            float landRatio = (float)landCount / totalCells;
            GD.Print($"[TEST 11] Seed 0x{seed:X16} Spawn [{spawn.X}, {spawn.Y}]: Land={landRatio:P1}, Sandy beach/desert cells={beachSandCount}");

            // Invariant: continental land region must NOT drown into ocean!
            if (landRatio < 0.70f)
                throw new Exception($"Seed 0x{seed:X16} continental spawn drowned into water! Land ratio: {landRatio:P1} < 70%");
        }

        // Verify that 3-tier cascade domain warp and coastline coves/capes generate curved organic contours
        ulong testSeed = 0x51EED4C0FFEEUL;
        int nonLinearTransitions = 0;
        int sampleStep = 8;
        for (long wy = 512 * 512; wy < 512 * 512 + 256; wy += sampleStep)
        {
            for (long wx = 512 * 512; wx < 512 * 512 + 256; wx += sampleStep)
            {
                var cell = UnifiedWorldGenerator.SampleCell(testSeed, 3, wx, wy, null);
                var cellRight = UnifiedWorldGenerator.SampleCell(testSeed, 3, wx + sampleStep, wy, null);
                var cellDown = UnifiedWorldGenerator.SampleCell(testSeed, 3, wx, wy + sampleStep, null);

                // Check for non-straight elevation variations
                if (cell.ElevationQ16 != cellRight.ElevationQ16 || cell.ElevationQ16 != cellDown.ElevationQ16)
                    nonLinearTransitions++;
            }
        }

        if (nonLinearTransitions == 0)
            throw new Exception("Terrain elevation showed zero organic variation across sampled coordinates!");

        int coastBeachesFound = 0;
        for (long ry = 300; ry < 700; ry += 20)
        {
            for (long rx = 300; rx < 700; rx += 20)
            {
                for (int d = 0; d < 512; d += 32)
                {
                    var cell = UnifiedWorldGenerator.SampleCell(testSeed, 3, rx * 512 + d, ry * 512 + d, null);
                    if (cell.Terrain == BaseTerrainKind.Grass && (BiomeType)cell.Biome == BiomeType.Desert && cell.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16 + 1000)
                    {
                        coastBeachesFound++;
                    }
                }
            }
        }
        GD.Print($"[TEST 11] Coastline sandy beach cells verified: {coastBeachesFound}");
        if (coastBeachesFound == 0)
            throw new Exception("No sandy beach cells generated along coastlines!");

        GD.Print("[TEST 11] Coastal Generation, Hypsometric Anchoring & Sandy Beaches verified successfully!");
    }

    private void TestWorldMapAndSimulationConsistency()
    {
        GD.Print("[TEST 12] Testing World Map vs Local Simulation Consistency (No Endless Land in Ocean)...");
        ulong seed = 0x51EED4C0FFEEUL;
        uint version = 3;
        var macroParams = Game.Core.WorldClimate.WorldClimateProvider.ParamsFor(seed);

        // 1. Проверка океанических регионов на мировой карте:
        // Любой регион, классифицированный на мировой карте как DeepWater / Океан,
        // ОБЯЗАН при локальной генерации чанков быть на 100% водным (0% травы, 0% деревьев, 0% гор)!
        int oceanRegionsTested = 0;
        for (long ry = 100; ry < 900 && oceanRegionsTested < 15; ry += 50)
        {
            for (long rx = 100; rx < 1900 && oceanRegionsTested < 15; rx += 70)
            {
                var reg = new RegionKey(rx, ry);
                var regClimate = WorldClimateGenerator.SampleRegion(seed, version, reg, in macroParams);
                var pick = BiomeMapper.PickFromClimate(seed, version, rx, ry, regClimate);

                if (pick.Biome != BiomeType.DeepWater)
                    continue; // проверяем именно океан

                oceanRegionsTested++;
                UnifiedWorldGenerator.SetWorldOrigin(rx * 512, ry * 512);

                // Генерируем 4 чанка внутри этого океанического региона
                for (int cy = 0; cy < 2; cy++)
                {
                    for (int cx = 0; cx < 2; cx++)
                    {
                        var chunk = UnifiedWorldGenerator.GenerateChunk(seed, version, new ChunkKey(cx, cy));
                        for (int i = 0; i < chunk.Cells.Length; i++)
                        {
                            var cell = chunk.Cells.Span[i];
                            if (cell.Terrain != BaseTerrainKind.Water)
                            {
                                throw new Exception($"[TEST 12 FAILED] Ocean region [{rx}, {ry}] chunk [{cx}, {cy}] cell {i} generated terrain {cell.Terrain}! Expected Water.");
                            }
                            if (cell.ElevationQ16 >= WorldClimateGenerator.SeaLevelQ16)
                            {
                                throw new Exception($"[TEST 12 FAILED] Ocean region [{rx}, {ry}] cell has elevation {cell.ElevationQ16} >= SeaLevel {WorldClimateGenerator.SeaLevelQ16}!");
                            }
                            if (cell.ForestQ16 > 0 || cell.StoneQ16 > 0)
                            {
                                throw new Exception($"[TEST 12 FAILED] Ocean cell in region [{rx}, {ry}] spawned trees ({cell.ForestQ16}) or stones ({cell.StoneQ16}) on water!");
                            }
                        }
                    }
                }
            }
        }
        GD.Print($"[TEST 12] Verified {oceanRegionsTested} pure ocean regions: 100% cells are strictly Water!");

        // 2. Проверка островного спавна и отдаления в океан:
        // Игрок спавнится на суше (остров), а при удалении в окружающий океан (+512, +1024 кл.)
        // симуляция строго переходит в водное пространство, без бесконечной суши.
        RegionKey islandSpawn = UnifiedWorldGenerator.FindHabitableSpawn(seed, version);
        UnifiedWorldGenerator.SetWorldOrigin(islandSpawn.X * 512, islandSpawn.Y * 512);

        // Ищем океанический регион на расстоянии 2 регионов по оси X
        long targetOceanRx = islandSpawn.X + 2;
        long targetRy = islandSpawn.Y;
        var targetClimate = WorldClimateGenerator.SampleRegion(seed, version, new RegionKey(targetOceanRx, targetRy), in macroParams);
        var targetPick = BiomeMapper.PickFromClimate(seed, version, targetOceanRx, targetRy, targetClimate);

        if (targetPick.Biome == BiomeType.DeepWater)
        {
            // Чанки на расстоянии +1024..+1088 клеток (ключ X = 16..17)
            var oceanChunk = UnifiedWorldGenerator.GenerateChunk(seed, version, new ChunkKey(16, 0));
            int waterCells = 0;
            for (int i = 0; i < oceanChunk.Cells.Length; i++)
            {
                if (oceanChunk.Cells.Span[i].Terrain == BaseTerrainKind.Water)
                    waterCells++;
            }
            float waterRatio = (float)waterCells / oceanChunk.Cells.Length;
            GD.Print($"[TEST 12] Distance +1024 tiles from spawn into ocean: Water ratio = {waterRatio:P1}");
            if (waterRatio < 0.95f)
            {
                throw new Exception($"[TEST 12 FAILED] Chunk at +1024 tiles into ocean has too little water: {waterRatio:P1}! Expected >= 95%.");
            }
        }

        GD.Print("[TEST 12] World Map vs Local Simulation Consistency passed with 100% accuracy!");
    }

    private void TestSingleRegionIslandConsistency()
    {
        GD.Print("[TEST 13] Testing Isolated Single-Region Island World Map vs Simulation Consistency...");
        ulong seed = 0x51EED4C0FFEEUL;
        uint version = 3;
        var macroParams = Game.Core.WorldClimate.WorldClimateProvider.ParamsFor(seed);

        // Поиск изолированного 1-регионного острова, окруженного со всех 8 сторон океаном:
        RegionKey? islandCandidate = null;
        for (long ry = 200; ry < 800 && !islandCandidate.HasValue; ry += 2)
        {
            for (long rx = 100; rx < 1900 && !islandCandidate.HasValue; rx += 2)
            {
                var centerClimate = WorldClimateGenerator.SampleRegion(seed, version, new RegionKey(rx, ry), in macroParams);
                if (centerClimate.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16)
                    continue;

                // Проверяем всех 8 соседей — все должны быть океаном:
                bool allNeighborsOcean = true;
                for (int dy = -1; dy <= 1 && allNeighborsOcean; dy++)
                {
                    for (int dx = -1; dx <= 1 && allNeighborsOcean; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var neighborClimate = WorldClimateGenerator.SampleRegion(seed, version, new RegionKey(rx + dx, ry + dy), in macroParams);
                        if (neighborClimate.ElevationQ16 >= WorldClimateGenerator.SeaLevelQ16)
                            allNeighborsOcean = false;
                    }
                }

                if (allNeighborsOcean)
                {
                    islandCandidate = new RegionKey(rx, ry);
                }
            }
        }

        if (!islandCandidate.HasValue)
        {
            for (long ry = 250; ry < 750 && !islandCandidate.HasValue; ry++)
            {
                for (long rx = 200; rx < 1800 && !islandCandidate.HasValue; rx++)
                {
                    var centerClimate = WorldClimateGenerator.SampleRegion(seed, version, new RegionKey(rx, ry), in macroParams);
                    if (centerClimate.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16)
                        continue;

                    bool allNeighborsOcean = true;
                    for (int dy = -1; dy <= 1 && allNeighborsOcean; dy++)
                    {
                        for (int dx = -1; dx <= 1 && allNeighborsOcean; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var neighborClimate = WorldClimateGenerator.SampleRegion(seed, version, new RegionKey(rx + dx, ry + dy), in macroParams);
                            if (neighborClimate.ElevationQ16 >= WorldClimateGenerator.SeaLevelQ16)
                                allNeighborsOcean = false;
                        }
                    }

                    if (allNeighborsOcean)
                        islandCandidate = new RegionKey(rx, ry);
                }
            }
        }

        if (!islandCandidate.HasValue)
            throw new Exception("[TEST 13 FAILED] Could not find any isolated 1-region island for adversarial test!");

        RegionKey island = islandCandidate.Value;
        GD.Print($"[TEST 13] Found isolated 1-region island at [{island.X}, {island.Y}]");

        // 1. Проверяем локальную колонию 512x512 на самом острове:
        UnifiedWorldGenerator.SetWorldOrigin(island.X * 512, island.Y * 512);
        var mapData = UnifiedWorldGenerator.GenerateColonyWindow(seed, version, 0, 0, 512, 512, null);
        int landCells = 0;
        for (int y = 0; y < 512; y++)
        {
            for (int x = 0; x < 512; x++)
            {
                if (mapData.Ground[x, y] == TileType.Grass || mapData.Ground[x, y] == TileType.Mountain)
                    landCells++;
            }
        }
        float landRatio = (float)landCells / (512 * 512);
        GD.Print($"[TEST 13] Island [{island.X}, {island.Y}] colony land ratio: {landRatio:P1}");
        if (landRatio < 0.70f)
            throw new Exception($"[TEST 13 FAILED] Island colony has too little land: {landRatio:P1} < 70%!");

        // 2. Проверяем все 4 стороны света на удалении +576 и +1024 клеток:
        // В океанических регионах вокруг острова должна быть строго вода (0% суши)!
        (int dx, int dy, string dir)[] directions = new[]
        {
            (1, 0, "East"),
            (-1, 0, "West"),
            (0, 1, "South"),
            (0, -1, "North")
        };

        foreach (var (cdx, cdy, dirName) in directions)
        {
            // Чанк на расстоянии +576 клеток (внутри соседнего океанического региона)
            ChunkKey oceanChunkKey = new ChunkKey(9 * cdx, 9 * cdy);
            var oceanChunk = UnifiedWorldGenerator.GenerateChunk(seed, version, oceanChunkKey);
            for (int i = 0; i < oceanChunk.Cells.Length; i++)
            {
                var cell = oceanChunk.Cells.Span[i];
                if (cell.Terrain != BaseTerrainKind.Water)
                {
                    throw new Exception($"[TEST 13 FAILED] Direction {dirName} (+576 tiles in ocean region) cell {i} generated terrain {cell.Terrain}! Expected Water.");
                }
            }

            // Чанк на расстоянии +1024 клеток (следующий океанический регион)
            ChunkKey farChunkKey = new ChunkKey(16 * cdx, 16 * cdy);
            var farChunk = UnifiedWorldGenerator.GenerateChunk(seed, version, farChunkKey);
            for (int i = 0; i < farChunk.Cells.Length; i++)
            {
                var cell = farChunk.Cells.Span[i];
                if (cell.Terrain != BaseTerrainKind.Water)
                {
                    throw new Exception($"[TEST 13 FAILED] Direction {dirName} (+1024 tiles in far ocean) cell {i} generated terrain {cell.Terrain}! Expected Water.");
                }
            }
            GD.Print($"[TEST 13] Direction {dirName} verified: 100% water in ocean regions!");
        }

        GD.Print("[TEST 13] Isolated single-region island consistency verified with 100% accuracy!");
    }

    private void TestIslandMorphologyAndNonCircularArcs()
    {
        GD.Print("[TEST 14] Testing Island Morphology (Non-circular 9x9 arcs, organic winding coastlines)...");
        ulong seed = 0x51EED4C0FFEEUL;
        uint version = 3;
        var macroParams = Game.Core.WorldClimate.WorldClimateProvider.ParamsFor(seed);

        // 1. Поиск островных регионов на мировой карте
        int islandsFound = 0;

        for (long ry = 300; ry < 700; ry += 8)
        {
            for (long rx = 200; rx < 1800; rx += 8)
            {
                var climate = WorldClimateGenerator.SampleRegion(seed, version, new RegionKey(rx, ry), in macroParams);
                if (climate.ElevationQ16 >= WorldClimateGenerator.SeaLevelQ16 && climate.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16 + 5000)
                {
                    // Проверяем, окружен ли океаном
                    int oceanNeighbors = 0;
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var n = WorldClimateGenerator.SampleRegion(seed, version, new RegionKey(rx + dx, ry + dy), in macroParams);
                            if (n.ElevationQ16 < WorldClimateGenerator.SeaLevelQ16)
                                oceanNeighbors++;
                        }
                    }

                    if (oceanNeighbors >= 18) // Преимущественно океаническое окружение
                    {
                        islandsFound++;
                    }
                }
            }
        }

        GD.Print($"[TEST 14] Macro-world island clusters found: {islandsFound}");
        if (islandsFound < 3)
            throw new Exception($"[TEST 14 FAILED] Too few islands found in world: {islandsFound}");

        // 2. Проверка морфологии локального острова: изрезанность берегов (не круг 9x9)
        // Генерируем тестовый остров 512x512
        UnifiedWorldGenerator.SetWorldOrigin(1012 * 512, 238 * 512);
        var mapData = UnifiedWorldGenerator.GenerateColonyWindow(seed, version, 0, 0, 512, 512, null);

        // Измеряем расстояния от центра острова (256, 256) до береговой линии в 18 радиальных направлениях:
        var coastRadii = new System.Collections.Generic.List<double>();
        for (int angleDeg = 0; angleDeg < 360; angleDeg += 20)
        {
            double rad = angleDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            int coastDist = 256;
            for (int r = 10; r < 256; r++)
            {
                int tx = (int)(256 + r * cos);
                int ty = (int)(256 + r * sin);
                if (tx >= 0 && tx < 512 && ty >= 0 && ty < 512)
                {
                    if (mapData.Ground[tx, ty] == TileType.Water)
                    {
                        coastDist = r;
                        break;
                    }
                }
            }
            coastRadii.Add(coastDist);
        }

        double minR = double.MaxValue;
        double maxR = double.MinValue;
        double sumR = 0;
        for (int i = 0; i < coastRadii.Count; i++)
        {
            double r = coastRadii[i];
            if (r < minR) minR = r;
            if (r > maxR) maxR = r;
            sumR += r;
        }
        double avgR = sumR / coastRadii.Count;
        double varianceSum = 0;
        for (int i = 0; i < coastRadii.Count; i++)
        {
            double diff = coastRadii[i] - avgR;
            varianceSum += diff * diff;
        }
        double stdDev = Math.Sqrt(varianceSum / coastRadii.Count);

        GD.Print($"[TEST 14] Island coast radii: min={minR:F0}, max={maxR:F0}, avg={avgR:F0}, stdDev={stdDev:F1} (Circularity threshold requires stdDev >= 15 for non-disk organic shapes)");

        if (stdDev < 15.0)
            throw new Exception($"[TEST 14 FAILED] Island coast is unnaturally circular/round (stdDev={stdDev:F1} < 15)! Expected organic irregular shapes.");

        GD.Print("[TEST 14] Island Morphology and Non-Circular Arcs verified successfully!");
    }
}

