using System;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.UI.Streaming;

namespace Game.Tests;

/// <summary>
/// §118 фаза 1 (PLAN.md §118.2, гейт фазы 1): WorldGenV2 — единственный источник
/// формул мира. Проверяет:
///  1) Fingerprint: 10 000 случайных клеток (3 сида, координаты ±1M, вкл.
///     отрицательные) + область 64×64 × 2 прогона — хэш бит-в-бит;
///  2) Тайлинг: GenerateArea(128×128) == 4 независимых GenerateArea(64×64)
///     для 3 origin (вкл. отрицательные мировые координаты);
///  3) Parity со старым окном: 256×256 × 3 смещения (вкл. отрицательные) ==
///     UnifiedWorldGenerator.GenerateColonyWindow по 6 полям (Ground/Tree/Stone/
///     TreeVariant/Biome/Variant), вне стартовой поляны;
///  4) Known-delta: ЕДИНСТВЕННОЕ допустимое расхождение с окном — стартовая
///     расчистка (круг r=14 в центре окна: Ground=Grass, без декора); в фазе 3
///     становится дельтой GroundOverride;
///  5) Parity со старым чанковым путём: 20 случайных чанков ==
///     UnifiedWorldGenerator.GenerateChunk по всем 8 полям GeneratedCell;
///  6) Отрицательный wx: v2 == GenerateChunk + StreamingWorldView.IslandDecorPass
///     (доказывает выбор евклидова Mod100 — поведение стриминга, которое видит
///     игрок за кромкой; в домене окна (wx,wy >= 0) оно побитово == формуле окна).
///
/// WorldOrigin выставляется как в tests/test_streamingview_decor_seam.cs:
/// FindHabitableSpawn → SetWorldOrigin (иначе остров может быть безлесным и
/// parity декора слепым). Origin сохраняется/восстанавливается.
///
/// ЗАПУСК (Lead; раннер по образцу tests/run_world_gen_seam_audit.gd —
/// создать tests/run_worldgen_v2.gd с содержимым):
///   extends SceneTree
///   func _init():
///       print("[GD] Running TestWorldGenV2...")
///       var test = load("res://tests/TestWorldGenV2.cs").new()
///       var ok = test.Run()
///       if ok:
///           print("[GD OK] TestWorldGenV2 passed with Code 0.")
///           quit(0)
///       else:
///           printerr("[GD ERR] TestWorldGenV2 failed!")
///           quit(1)
/// и выполнить: godot --headless --path &lt;project&gt; --script tests/run_worldgen_v2.gd
/// </summary>
public partial class TestWorldGenV2 : RefCounted
{
    private const ulong Seed = 0x5EED00000BADC0DEul; // как test_streamingview_decor_seam: известный лесной спавн
    private const uint Ver = 3;

    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[TEST] Starting WorldGenV2 (§118 phase 1: единый источник формул)");
        GD.Print("==================================================");

        long savedOriginX = UnifiedWorldGenerator.WorldOriginX;
        long savedOriginY = UnifiedWorldGenerator.WorldOriginY;
        try
        {
            // Как Main.OnRegionSelected / test_streamingview_decor_seam: ищем
            // пригодный регион и ставим WorldOrigin (v2 работает в абсолютных
            // мировых координатах = WorldOrigin + локальные).
            RegionKey spawn = UnifiedWorldGenerator.FindHabitableSpawn(Seed, Ver);
            UnifiedWorldGenerator.SetWorldOrigin(
                spawn.X * WorldRegions.RegionSize, spawn.Y * WorldRegions.RegionSize);
            GD.Print($"[TEST] WorldOrigin = регион ({spawn.X},{spawn.Y})");

            TestFingerprintDeterminism();
            TestAreaTiling();
            TestParityWithColonyWindow();
            TestKnownDeltaStartClear();
            TestParityWithGenerateChunk();
            TestStreamingDecorParityAtNegativeWorldX();

            GD.Print("==================================================");
            GD.Print("[TEST] ALL WORLDGENV2 TESTS PASSED! (Code 0)");
            GD.Print("==================================================");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[TEST FAILED] {ex}");
            return false;
        }
        finally
        {
            UnifiedWorldGenerator.SetWorldOrigin(savedOriginX, savedOriginY);
        }
    }

    // ------------------------------------------------------------------ //
    //  1. Fingerprint: детерминизм бит-в-бит                              //
    // ------------------------------------------------------------------ //

    private void TestFingerprintDeterminism()
    {
        GD.Print("[TEST 1] Fingerprint: 10 000 случайных клеток (3 сида) × 2 прогона, бит-в-бит...");
        var r1 = ComputeFingerprint();
        var r2 = ComputeFingerprint();
        if (r1.Hash != r2.Hash)
            throw new Exception($"Fingerprint не стабилен между прогонами: 0x{r1.Hash:X16} vs 0x{r2.Hash:X16}.");
        if (r1.Water == 0 || r1.Grass == 0)
            throw new Exception($"Выборка слепа: water={r1.Water}, grass={r1.Grass} (смените сид).");
        GD.Print($"[TEST 1] OK: 10 000 клеток (координаты ±1M, вкл. отрицательные) + область 64×64 "
            + $"(клетки+декор+варианты); hash 0x{r1.Hash:X16} бит-в-бит совпадает в обоих прогонах; "
            + $"water={r1.Water}, grass={r1.Grass}, mountain={r1.Mountain}.");
    }

    private static (ulong Hash, int Water, int Grass, int Mountain) ComputeFingerprint()
    {
        ulong[] seeds = { Seed, 987654321ul, 0x1234567890ABCDEFul };
        var rng = new Random(20261019);
        ulong h = 1469598103934665603ul; // FNV-1a 64
        int water = 0, grass = 0, mountain = 0;

        for (int si = 0; si < seeds.Length; si++)
        {
            int count = si == 0 ? 3334 : 3333; // 3334 + 3333 + 3333 = 10 000
            for (int i = 0; i < count; i++)
            {
                long wx = rng.Next(-1_000_000, 1_000_000);
                long wy = rng.Next(-1_000_000, 1_000_000);
                WorldGenV2.CellSample c = WorldGenV2.SampleCell(seeds[si], Ver, wx, wy);
                FoldCell(ref h, in c);
                if (c.Terrain == BaseTerrainKind.Water) water++;
                else if (c.Terrain == BaseTerrainKind.Grass) grass++;
                else mountain++;
            }
        }

        // Область 64×64 с отрицательным origin: клетки + декор + варианты — в тот же хэш.
        WorldGenV2.Area area = WorldGenV2.GenerateArea(987654321ul, Ver, -192, -128, 64, 64);
        ReadOnlySpan<GeneratedCell> cells = area.Cells.Span;
        ReadOnlySpan<bool> trees = area.TreeOnGrass.Span;
        ReadOnlySpan<bool> stones = area.StoneOnGrass.Span;
        ReadOnlySpan<byte> treeVar = area.TreeVariant.Span;
        for (int i = 0; i < cells.Length; i++)
        {
            Fold(ref h, (ushort)cells[i].Terrain);
            Fold(ref h, cells[i].ElevationQ16);
            Fold(ref h, cells[i].MoistureQ16);
            Fold(ref h, cells[i].ForestQ16);
            Fold(ref h, cells[i].StoneQ16);
            Fold(ref h, cells[i].TemperatureQ16);
            Fold(ref h, cells[i].Biome);
            Fold(ref h, cells[i].Variant);
            Fold(ref h, (ushort)(trees[i] ? 1 : 0));
            Fold(ref h, (ushort)(stones[i] ? 1 : 0));
            Fold(ref h, treeVar[i]);
        }
        return (h, water, grass, mountain);
    }

    private static void FoldCell(ref ulong h, in WorldGenV2.CellSample c)
    {
        Fold(ref h, (ushort)c.Terrain);
        Fold(ref h, c.ElevationQ16);
        Fold(ref h, c.MoistureQ16);
        Fold(ref h, c.TemperatureQ16);
        Fold(ref h, c.SoilFertilityQ16);
        Fold(ref h, c.ForestQ16);
        Fold(ref h, c.StoneQ16);
        Fold(ref h, c.Biome);
        Fold(ref h, c.Variant);
        Fold(ref h, c.TreeVariant);
        Fold(ref h, c.StoneVariant);
    }

    private static void Fold(ref ulong h, ushort v)
        => h = (h ^ v) * 1099511628211ul;

    // ------------------------------------------------------------------ //
    //  2. Тайлинг: 128×128 == 4×64×64                                     //
    // ------------------------------------------------------------------ //

    private void TestAreaTiling()
    {
        GD.Print("[TEST 2] Тайлинг: GenerateArea(128×128) == 4×GenerateArea(64×64), 3 origin (вкл. отрицательный)...");
        long wox = UnifiedWorldGenerator.WorldOriginX;
        long woy = UnifiedWorldGenerator.WorldOriginY;
        (long Ox, long Oy)[] origins = { (wox, woy), (wox - 256, woy + 128), (-2048, -4096) };
        long compared = 0;
        int trees = 0, stones = 0;

        foreach (var (ox, oy) in origins)
        {
            WorldGenV2.Area big = WorldGenV2.GenerateArea(Seed, Ver, ox, oy, 128, 128);
            ReadOnlySpan<GeneratedCell> bigCells = big.Cells.Span;
            ReadOnlySpan<bool> bigTree = big.TreeOnGrass.Span;
            ReadOnlySpan<bool> bigStone = big.StoneOnGrass.Span;
            ReadOnlySpan<byte> bigVar = big.TreeVariant.Span;

            for (int i = 0; i < bigCells.Length; i++)
            {
                if (bigTree[i]) trees++;
                if (bigStone[i]) stones++;
            }

            for (int cy = 0; cy < 2; cy++)
            {
                for (int cx = 0; cx < 2; cx++)
                {
                    WorldGenV2.Area sub = WorldGenV2.GenerateArea(Seed, Ver, ox + cx * 64, oy + cy * 64, 64, 64);
                    ReadOnlySpan<GeneratedCell> subCells = sub.Cells.Span;
                    ReadOnlySpan<bool> subTree = sub.TreeOnGrass.Span;
                    ReadOnlySpan<bool> subStone = sub.StoneOnGrass.Span;
                    ReadOnlySpan<byte> subVar = sub.TreeVariant.Span;

                    for (int ly = 0; ly < 64; ly++)
                    {
                        for (int lx = 0; lx < 64; lx++)
                        {
                            int bi = (cy * 64 + ly) * 128 + (cx * 64 + lx);
                            int si = ly * 64 + lx;
                            if (bigCells[bi] != subCells[si])
                                throw new Exception($"Тайлинг [{ox},{oy}] под-область ({cx},{cy}), клетка ({lx},{ly}): {DiffFields(in subCells[si], in bigCells[bi])}");
                            if (bigTree[bi] != subTree[si])
                                throw new Exception($"Тайлинг [{ox},{oy}] под-область ({cx},{cy}), клетка ({lx},{ly}): Tree {bigTree[bi]} != {subTree[si]}.");
                            if (bigStone[bi] != subStone[si])
                                throw new Exception($"Тайлинг [{ox},{oy}] под-область ({cx},{cy}), клетка ({lx},{ly}): Stone {bigStone[bi]} != {subStone[si]}.");
                            if (bigVar[bi] != subVar[si])
                                throw new Exception($"Тайлинг [{ox},{oy}] под-область ({cx},{cy}), клетка ({lx},{ly}): TreeVariant {bigVar[bi]} != {subVar[si]}.");
                            compared++;
                        }
                    }
                }
            }
        }

        if (trees == 0)
            throw new Exception("Тайлинг ни разу не увидел дерева — сравнение декора слепо (смените сид).");
        GD.Print($"[TEST 2] OK: 3 origin × 16 384 клеток × 4 массива (Cells/Tree/Stone/TreeVariant) — "
            + $"побитово; деревьев {trees}, камней {stones} в опорных областях (не слеп).");
    }

    // ------------------------------------------------------------------ //
    //  3. Parity с GenerateColonyWindow (256×256 × 3 смещения)            //
    // ------------------------------------------------------------------ //

    private void TestParityWithColonyWindow()
    {
        GD.Print("[TEST 3] Parity: окно 256×256 × 3 смещения (вкл. отрицательные) == GenerateColonyWindow...");
        long wox = UnifiedWorldGenerator.WorldOriginX;
        long woy = UnifiedWorldGenerator.WorldOriginY;

        // Отрицательные смещения НЕ выводят окно из домена wx,wy >= 0: там формула
        // окна (C# %) и формула v2 (евклидов Mod100) побитово совпадают. За доменом —
        // задокументированная дельта (шапка WorldGenV2.cs; поведение стриминга там
        // проверяет тест 6).
        long negOx = -Math.Min(160, Math.Max(0L, wox));
        long negOy = -Math.Min(224, Math.Max(0L, woy));
        (long Ox, long Oy)[] offsets = { (0, 0), (negOx, 96), (192, negOy) };

        const int Side = 256;
        const long ClearR2 = 14L * 14L; // стартовая поляна окна — известная дельта (тест 4)
        long compared = 0;
        int trees = 0, stones = 0;

        foreach (var (ox, oy) in offsets)
        {
            MapData map = UnifiedWorldGenerator.GenerateColonyWindow(Seed, Ver, ox, oy, Side, Side);
            WorldGenV2.Area area = WorldGenV2.GenerateArea(Seed, Ver, wox + ox, woy + oy, Side, Side);
            ReadOnlySpan<GeneratedCell> cells = area.Cells.Span;
            ReadOnlySpan<bool> areaTree = area.TreeOnGrass.Span;
            ReadOnlySpan<bool> areaStone = area.StoneOnGrass.Span;
            ReadOnlySpan<byte> areaVar = area.TreeVariant.Span;

            for (int y = 0; y < Side; y++)
            {
                for (int x = 0; x < Side; x++)
                {
                    long dx = x - Side / 2, dy = y - Side / 2;
                    if (dx * dx + dy * dy <= ClearR2)
                        continue; // стартовая поляна: known-delta, тест 4

                    int i = y * Side + x;
                    GeneratedCell cell = cells[i];
                    if (map.Ground[x, y] != TerrainToTile(cell.Terrain))
                        throw new Exception($"Окно [{ox},{oy}] ({x},{y}): Ground window={map.Ground[x, y]}, v2={TerrainToTile(cell.Terrain)}.");
                    if (map.TreeOnGrass[x, y] != areaTree[i])
                        throw new Exception($"Окно [{ox},{oy}] ({x},{y}): Tree window={map.TreeOnGrass[x, y]}, v2={areaTree[i]} (ForestQ16={cell.ForestQ16}).");
                    if (map.StoneOnGrass[x, y] != areaStone[i])
                        throw new Exception($"Окно [{ox},{oy}] ({x},{y}): Stone window={map.StoneOnGrass[x, y]}, v2={areaStone[i]} (StoneQ16={cell.StoneQ16}).");
                    if (map.TreeVariant[x, y] != areaVar[i])
                        throw new Exception($"Окно [{ox},{oy}] ({x},{y}): TreeVariant window={map.TreeVariant[x, y]}, v2={areaVar[i]}.");
                    if (map.Biome[x, y] != cell.Biome)
                        throw new Exception($"Окно [{ox},{oy}] ({x},{y}): Biome window={map.Biome[x, y]}, v2={cell.Biome}.");
                    if (map.Variant[x, y] != cell.Variant)
                        throw new Exception($"Окно [{ox},{oy}] ({x},{y}): Variant window={map.Variant[x, y]}, v2={cell.Variant}.");

                    compared++;
                    if (map.TreeOnGrass[x, y]) trees++;
                    if (map.StoneOnGrass[x, y]) stones++;
                }
            }
        }

        if (trees == 0)
            throw new Exception("Окна без деревьев: parity декора слеп (смените сид).");
        if (stones == 0)
            GD.Print("[TEST 3] WARN: камней в сравнении 0 — сторона камней parity слепа на этом сиде "
                + "(формула всё равно сравнена по всем клеткам: обе стороны false).");
        GD.Print($"[TEST 3] OK: {compared} клеток × 6 полей (Ground/Tree/Stone/TreeVariant/Biome/Variant) "
            + "× 3 окна — побитово (вне стартовой поляны); "
            + $"деревьев {trees}, камней {stones}.");
    }

    // ------------------------------------------------------------------ //
    //  4. Known-delta: стартовая расчистка — единственное расхождение     //
    // ------------------------------------------------------------------ //

    private void TestKnownDeltaStartClear()
    {
        GD.Print("[TEST 4] Known-delta: единственное расхождение с окном — стартовая расчистка (r=14, центр)...");
        long wox = UnifiedWorldGenerator.WorldOriginX;
        long woy = UnifiedWorldGenerator.WorldOriginY;
        const int Side = 256;

        MapData map = UnifiedWorldGenerator.GenerateColonyWindow(Seed, Ver, 0, 0, Side, Side);
        WorldGenV2.Area area = WorldGenV2.GenerateArea(Seed, Ver, wox, woy, Side, Side);
        ReadOnlySpan<GeneratedCell> cells = area.Cells.Span;
        ReadOnlySpan<bool> areaTree = area.TreeOnGrass.Span;
        ReadOnlySpan<bool> areaStone = area.StoneOnGrass.Span;
        ReadOnlySpan<byte> areaVar = area.TreeVariant.Span;

        const int C = Side / 2;
        const long ClearR2 = 14L * 14L;
        long outside = 0, inCircle = 0, delta = 0;

        for (int y = 0; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                int i = y * Side + x;
                GeneratedCell cell = cells[i];
                long dx = x - C, dy = y - C;
                bool inClear = dx * dx + dy * dy <= ClearR2;

                if (!inClear)
                {
                    // Вне поляны: полное совпадение всех 6 полей (явная дублировка теста 3).
                    if (map.Ground[x, y] != TerrainToTile(cell.Terrain)
                        || map.TreeOnGrass[x, y] != areaTree[i]
                        || map.StoneOnGrass[x, y] != areaStone[i]
                        || map.TreeVariant[x, y] != areaVar[i]
                        || map.Biome[x, y] != cell.Biome
                        || map.Variant[x, y] != cell.Variant)
                        throw new Exception($"Вне стартовой поляны ({x},{y}) — недопустимое расхождение: "
                            + $"Ground {map.Ground[x, y]}/{TerrainToTile(cell.Terrain)}, "
                            + $"tree {map.TreeOnGrass[x, y]}/{areaTree[i]}, "
                            + $"stone {map.StoneOnGrass[x, y]}/{areaStone[i]}, "
                            + $"treeVar {map.TreeVariant[x, y]}/{areaVar[i]}, "
                            + $"biome {map.Biome[x, y]}/{cell.Biome}, var {map.Variant[x, y]}/{cell.Variant}.");
                    outside++;
                }
                else
                {
                    inCircle++;
                    // Семантика расчистки окна: безусловная трава без декора.
                    if (map.Ground[x, y] != TileType.Grass)
                        throw new Exception($"В круге расчистки ({x},{y}): окно обязано иметь Ground=Grass, имеет {map.Ground[x, y]}.");
                    if (map.TreeOnGrass[x, y] || map.StoneOnGrass[x, y])
                        throw new Exception($"В круге расчистки ({x},{y}): окно обязано быть без декора.");
                    // Поля, которые расчистка НЕ трогает, обязаны совпадать и внутри круга.
                    if (map.TreeVariant[x, y] != areaVar[i]
                        || map.Biome[x, y] != cell.Biome
                        || map.Variant[x, y] != cell.Variant)
                        throw new Exception($"В круге расчистки ({x},{y}): нетронутые поля обязаны совпадать: "
                            + $"biome {map.Biome[x, y]}/{cell.Biome}, var {map.Variant[x, y]}/{cell.Variant}, "
                            + $"treeVar {map.TreeVariant[x, y]}/{areaVar[i]}.");
                    // Допустимая дельта — ТОЛЬКО по Ground/Tree/Stone (расчистка).
                    bool differs = map.Ground[x, y] != TerrainToTile(cell.Terrain)
                        || map.TreeOnGrass[x, y] != areaTree[i]
                        || map.StoneOnGrass[x, y] != areaStone[i];
                    if (differs)
                        delta++;
                }
            }
        }

        GD.Print($"[TEST 4] OK: вне круга {outside} клеток — 0 расхождений; в круге {inCircle} клеток "
            + $"(окно принудительно: трава без декора), дельта v2↔окно проявляется на {delta} из них "
            + "и ТОЛЬКО в Ground/TreeOnGrass/StoneOnGrass. Это единственное расхождение; в фазе 3 "
            + "заменяется дельтой GroundOverride (§118.1).");
    }

    // ------------------------------------------------------------------ //
    //  5. Parity с GenerateChunk (20 случайных чанков)                    //
    // ------------------------------------------------------------------ //

    private void TestParityWithGenerateChunk()
    {
        GD.Print("[TEST 5] Parity: 20 случайных чанков == GenerateChunk (все 8 полей GeneratedCell)...");
        long wox = UnifiedWorldGenerator.WorldOriginX;
        long woy = UnifiedWorldGenerator.WorldOriginY;
        var rng = new Random(1180118);

        long compared = 0;
        int forestCells = 0, waterCells = 0, mountainCells = 0;

        for (int i = 0; i < 20; i++)
        {
            long ckx = rng.Next(-8, 9); // -8..8, вкл. отрицательные чанки
            long cky = rng.Next(-8, 9);
            var key = new ChunkKey(ckx, cky);

            WorldChunk chunk = UnifiedWorldGenerator.GenerateChunk(Seed, Ver, key);
            WorldGenV2.Area area = WorldGenV2.GenerateArea(
                Seed, Ver, wox + ckx * 64, woy + cky * 64, 64, 64);

            ReadOnlySpan<GeneratedCell> oldCells = chunk.Cells.Span;
            ReadOnlySpan<GeneratedCell> newCells = area.Cells.Span;
            for (int j = 0; j < WorldChunk.CellCount; j++)
            {
                if (oldCells[j] != newCells[j])
                    throw new Exception($"Чанк ({ckx},{cky}), клетка {j} "
                        + $"(wx={wox + ckx * 64 + j % 64},wy={woy + cky * 64 + j / 64}): "
                        + $"{DiffFields(in oldCells[j], in newCells[j])}");
                compared++;
                if (oldCells[j].ForestQ16 > 0) forestCells++;
                if (oldCells[j].Terrain == BaseTerrainKind.Water) waterCells++;
                else if (oldCells[j].Terrain == BaseTerrainKind.Mountain) mountainCells++;
            }
        }

        if (forestCells == 0 || waterCells == 0)
            throw new Exception($"Выборка чанков слепа: forest>0={forestCells}, water={waterCells} (смените сид).");
        GD.Print($"[TEST 5] OK: {compared} клеток (20 чанков) × 8 полей — побитово; "
            + $"клеток с ForestQ16>0: {forestCells}, вода: {waterCells}, горы: {mountainCells}.");
    }

    // ------------------------------------------------------------------ //
    //  6. Отрицательный wx: v2 == стриминг (GenerateChunk + DecorPass)    //
    // ------------------------------------------------------------------ //

    private void TestStreamingDecorParityAtNegativeWorldX()
    {
        GD.Print("[TEST 6] Отрицательный wx: v2 == GenerateChunk + IslandDecorPass (Mod100, поведение стриминга)...");
        long wox = UnifiedWorldGenerator.WorldOriginX;
        long woy = UnifiedWorldGenerator.WorldOriginY;

        // Скаутинг «лесного» чанка в отрицательном wx (планета заворачивает X по
        // модулю 2048 регионов). Выбор кандидата по v2 не ослабляет проверку:
        // сравниваются v2 и СТАРЫЙ генератор на одной и той же области.
        long firstKeyX = -(wox / WorldCoordinates.ChunkSize) - 2; // wox + key*64 < 0
        ChunkKey found = default;
        bool have = false;
        int probes = 0;
        int lastForestNoise = -1, lastGrass = -1, lastWater = -1, lastTrees = -1;
        // Широкий скан с джиттером ~регион (шаг 15 чанков + псевдослучайный сдвиг):
        // узкая полоса соседних регионов может быть безлесной на сиде — это слепота
        // фикстуры, не баг. Выбор кандидата по v2 не ослабляет проверку: сравниваются
        // v2 и СТАРЫЙ генератор на одной и той же области. При неудаче в сообщении —
        // диагностика последнего чанка (шум леса/трава/вода/деревья).
        long[] probeRows = { 3, 0, 7, -3, 11, -7, 15, -11, 19, -15, 23, -19 };
        foreach (long rowY in probeRows)
        {
            for (int k = 0; k < 48 && !have; k++)
            {
                var key = new ChunkKey(firstKeyX - k * 15 - (k * 7) % 13, rowY);
                WorldGenV2.Area probe = WorldGenV2.GenerateArea(
                    Seed, Ver, wox + key.X * 64, woy + key.Y * 64, 64, 64);
                probes++;
                ReadOnlySpan<bool> t = probe.TreeOnGrass.Span;
                int trees = 0;
                int forestNoise = 0, grass = 0, water = 0;
                ReadOnlySpan<GeneratedCell> pc = probe.Cells.Span;
                for (int i = 0; i < t.Length; i++)
                {
                    if (t[i]) trees++;
                    if (pc[i].ForestQ16 >= WorldDecor.ForestMinQ16) forestNoise++;
                    if (pc[i].Terrain == BaseTerrainKind.Grass) grass++;
                    if (pc[i].Terrain == BaseTerrainKind.Water) water++;
                }
                lastForestNoise = forestNoise; lastGrass = grass;
                lastWater = water; lastTrees = trees;
                if (trees >= 20)
                {
                    found = key;
                    have = true;
                }
            }
            if (have)
                break;
        }
        if (!have)
            throw new Exception($"Не найден лесной чанк в отрицательном wx ({probes} кандидатов, "
                + $"{probeRows.Length} рядов). Диагностика последнего: forestNoise={lastForestNoise}, "
                + $"grass={lastGrass}, water={lastWater}, trees={lastTrees} из 4096. "
                + "Если forestNoise>0, а деревьев нет — смотреть гейт IslandDecorPass/Mod100.");

        long originX = wox + found.X * 64;
        long originY = woy + found.Y * 64;
        WorldChunk chunk = UnifiedWorldGenerator.GenerateChunk(Seed, Ver, found);
        WorldGenV2.Area area = WorldGenV2.GenerateArea(Seed, Ver, originX, originY, 64, 64);
        ushort[] fertGrid = BuildFertilityGrid(found);

        ReadOnlySpan<GeneratedCell> oldCells = chunk.Cells.Span;
        ReadOnlySpan<GeneratedCell> newCells = area.Cells.Span;
        int trees2 = 0, stones2 = 0;
        for (int local = 0; local < WorldChunk.CellCount; local++)
        {
            int lx = local % WorldCoordinates.ChunkSize;
            int ly = local / WorldCoordinates.ChunkSize;
            if (oldCells[local] != newCells[local])
                throw new Exception($"Отрицательный wx, чанк ({found.X},{found.Y}), клетка ({lx},{ly}): "
                    + $"{DiffFields(in oldCells[local], in newCells[local])}");

            ushort fertility = InterpolateFertility(fertGrid, lx, ly);
            var decor = StreamingWorldView.IslandDecorPass(
                originX + lx, originY + ly, in oldCells[local], fertility);
            if (decor.HasTree != area.TreeOnGrass.Span[local]
                || decor.HasStone != area.StoneOnGrass.Span[local])
                throw new Exception($"Отрицательный wx ({originX + lx},{originY + ly}): decor "
                    + $"v2 tree={area.TreeOnGrass.Span[local]} stone={area.StoneOnGrass.Span[local]}, "
                    + $"streaming tree={decor.HasTree} stone={decor.HasStone} "
                    + $"(forest={oldCells[local].ForestQ16}, stoneQ16={oldCells[local].StoneQ16}, fert={fertility}).");
            if (decor.HasTree) trees2++;
            if (decor.HasStone) stones2++;
        }
        if (trees2 < 20)
            throw new Exception($"Лес в отрицательном wx исчез при сравнении: {trees2} (ожидалось >= 20).");

        GD.Print($"[TEST 6] OK: чанк ({found.X},{found.Y}) при wx<0 (мировые клетки "
            + $"[{originX}..{originX + 63}]): 4096 клеток и декор побитово == стримингу; "
            + $"деревьев {trees2}, камней {stones2}. Евклидов Mod100 подтверждён поведением стриминга.");
    }

    // ------------------------------------------------------------------ //
    //  Вспомогательные                                                    //
    // ------------------------------------------------------------------ //

    /// <summary>Тот же маппинг, что использует GenerateColonyWindow для Ground.</summary>
    private static TileType TerrainToTile(BaseTerrainKind t) => t switch
    {
        BaseTerrainKind.Water => TileType.Water,
        BaseTerrainKind.Mountain => TileType.Mountain,
        _ => TileType.Grass
    };

    private static string DiffFields(in GeneratedCell a, in GeneratedCell b)
    {
        var sb = new System.Text.StringBuilder("GeneratedCell differs:");
        if (a.Terrain != b.Terrain) sb.Append($" Terrain {a.Terrain}!={b.Terrain}");
        if (a.ElevationQ16 != b.ElevationQ16) sb.Append($" ElevationQ16 {a.ElevationQ16}!={b.ElevationQ16}");
        if (a.MoistureQ16 != b.MoistureQ16) sb.Append($" MoistureQ16 {a.MoistureQ16}!={b.MoistureQ16}");
        if (a.ForestQ16 != b.ForestQ16) sb.Append($" ForestQ16 {a.ForestQ16}!={b.ForestQ16}");
        if (a.StoneQ16 != b.StoneQ16) sb.Append($" StoneQ16 {a.StoneQ16}!={b.StoneQ16}");
        if (a.TemperatureQ16 != b.TemperatureQ16) sb.Append($" TemperatureQ16 {a.TemperatureQ16}!={b.TemperatureQ16}");
        if (a.Biome != b.Biome) sb.Append($" Biome {a.Biome}!={b.Biome}");
        if (a.Variant != b.Variant) sb.Append($" Variant {a.Variant}!={b.Variant}");
        return sb.ToString();
    }

    // Сетка плодородия 5×5 — независимая копия (как в test_streamingview_decor_seam.cs),
    // якоря = WorldOrigin + чанк + 16·g, интерполяция — копия LerpQ16.

    private const int GridStep = 16;
    private const int GridSide = WorldCoordinates.ChunkSize / GridStep + 1; // 5

    private static ushort[] BuildFertilityGrid(ChunkKey key)
    {
        var grid = new ushort[GridSide * GridSide];
        long originX = UnifiedWorldGenerator.WorldOriginX + key.X * (long)WorldCoordinates.ChunkSize;
        long originY = UnifiedWorldGenerator.WorldOriginY + key.Y * (long)WorldCoordinates.ChunkSize;
        for (int gy = 0; gy < GridSide; gy++)
            for (int gx = 0; gx < GridSide; gx++)
                grid[gy * GridSide + gx] = WorldClimateGenerator.SampleBlendedCell(
                    Seed, Ver, originX + gx * GridStep, originY + gy * GridStep).SoilFertilityQ16;
        return grid;
    }

    private static ushort InterpolateFertility(ushort[] grid, int lx, int ly)
    {
        int row = (ly / GridStep) * GridSide;
        int col = lx / GridStep;
        uint u = (uint)((lx % GridStep) * 65535 / GridStep);
        uint v = (uint)((ly % GridStep) * 65535 / GridStep);
        ushort top = LerpQ16Copy(grid[row + col], grid[row + col + 1], u);
        ushort bottom = LerpQ16Copy(grid[row + GridSide + col], grid[row + GridSide + col + 1], u);
        return LerpQ16Copy(top, bottom, v);
    }

    private static ushort LerpQ16Copy(ushort a, ushort b, uint t)
    {
        uint clampedT = Math.Min(t, 65535);
        long value = (long)a * (65535 - clampedT) + (long)b * clampedT + 32768;
        return (ushort)Math.Clamp(value / 65535, 0, 65535);
    }
}
