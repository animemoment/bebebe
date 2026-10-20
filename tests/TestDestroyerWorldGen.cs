using System;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;

namespace Game.Tests;

/// <summary>
/// @destroyer (PLAN §117, задача 2a/2b): adversarial-аудит генерации мира.
/// 1) FloorDiv/FloorMod/ChunkKey на ОТРИЦАТЕЛЬНЫХ координатах (граница -512, -64).
/// 2) Инварианты ChunkForTile/LocalForTile/TileOrigin (round-trip, индексация чанка).
/// 3) TileOrigin на экстремальных ChunkKey — checked-overflow, а не тихий wrap.
/// 4) GenerateColonyWindow на origin, кратном 512 (кромка региона, вкл. отрицательные):
///    без исключений, размерности корректны, данные не пустые.
/// 5) Швы колонии↔стриминга на кромке x=511|512 (тип поверхности + плотность леса, F4).
/// 6) Согласованность WorldCellQuery (гейт размещения) с чанками стриминга.
/// </summary>
public partial class TestDestroyerWorldGen : RefCounted
{
    private const uint GenVersion = 3;
    private const ulong Seed = 0x1234567890ABCDEFul;

    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[DESTROYER WORLDGEN] Starting adversarial world/streaming audit (@destroyer)");
        GD.Print("==================================================");
        try
        {
            // Статическое смещение мира обязано быть нулевым: все проверки в абсолютных координатах.
            UnifiedWorldGenerator.SetWorldOrigin(0, 0);

            TestFloorDivNegativeCoordinates();
            TestChunkKeyRoundTripInvariants();
            TestTileOriginCheckedOverflow();
            TestGenerateColonyWindowAtRegionEdges();
            TestColonyEdgeVsStreamingSeam();
            TestWorldCellQueryVsChunkAgreement();

            GD.Print("==================================================");
            GD.Print("[DESTROYER WORLDGEN OK] ALL WORLDGEN ADVERSARIAL TESTS PASSED! (Code 0)");
            GD.Print("==================================================");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DESTROYER WORLDGEN FAILED] {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    // ------------------------------------------------------------------
    // (2b) Отрицательные координаты: FloorDiv/FloorMod/ChunkForTile/LocalForTile
    // ------------------------------------------------------------------
    private void TestFloorDivNegativeCoordinates()
    {
        GD.Print("[WTEST 1] FloorDiv/FloorMod on negative coordinates...");

        // -1 тайл попадает в чанк -1 (а не 0!) с локальной позицией 63.
        if (WorldCoordinates.FloorDiv(-1, 64) != -1)
            throw new Exception("FloorDiv(-1, 64) must be -1 (chunk -1), got " + WorldCoordinates.FloorDiv(-1, 64));
        if (WorldCoordinates.FloorMod(-1, 64) != 63)
            throw new Exception("FloorMod(-1, 64) must be 63, got " + WorldCoordinates.FloorMod(-1, 64));
        if (WorldCoordinates.ChunkForTile(-1, -1) != new ChunkKey(-1, -1))
            throw new Exception("ChunkForTile(-1,-1) must be (-1,-1).");
        if (WorldCoordinates.LocalForTile(-1, -1) != new LocalCell(63, 63))
            throw new Exception("LocalForTile(-1,-1) must be (63,63).");

        // -64 — ровно начало чанка -1 (FloorDiv обрезает к -inf, а не к нулю).
        if (WorldCoordinates.ChunkForTile(-64, 0) != new ChunkKey(-1, 0))
            throw new Exception("ChunkForTile(-64, 0) must be (-1, 0).");
        if (WorldCoordinates.ChunkForTile(-65, 5) != new ChunkKey(-2, 0))
            throw new Exception("ChunkForTile(-65, 5) must be (-2, 0).");
        if (WorldCoordinates.LocalForTile(-65, 5) != new LocalCell(63, 5))
            throw new Exception("LocalForTile(-65, 5) must be (63, 5).");

        // -63 — ещё чанк -1, локальная 1.
        if (WorldCoordinates.FloorDiv(-63, 64) != -1)
            throw new Exception("FloorDiv(-63, 64) must be -1.");
        if (WorldCoordinates.FloorMod(-63, 64) != 1)
            throw new Exception("FloorMod(-63, 64) must be 1, got " + WorldCoordinates.FloorMod(-63, 64));

        // Кромка региона 512 в отрицательных: -512 = -1*512 → чанк -8 ровно.
        if (WorldCoordinates.ChunkForTile(-512, -512) != new ChunkKey(-8, -8))
            throw new Exception("ChunkForTile(-512,-512) must be (-8,-8).");
        if (WorldCoordinates.ChunkForTile(-513, -513) != new ChunkKey(-9, -9))
            throw new Exception("ChunkForTile(-513,-513) must be (-9,-9).");
        if (WorldCoordinates.LocalForTile(-513, -513) != new LocalCell(63, 63))
            throw new Exception("LocalForTile(-513,-513) must be (63,63).");

        // Делитель <= 0 обязан бросать (контракт).
        ExpectThrows(() => WorldCoordinates.FloorDiv(10, 0), "FloorDiv with zero divisor");
        ExpectThrows(() => WorldCoordinates.FloorMod(10, -5), "FloorMod with negative divisor");

        GD.Print(" -> Negative FloorDiv/FloorMod/ChunkKey OK!");
    }

    private void TestChunkKeyRoundTripInvariants()
    {
        GD.Print("[WTEST 2] ChunkKey round-trip invariants (random signed)...");

        var rng = new Random(20261018);
        for (int i = 0; i < 20000; i++)
        {
            long x = rng.Next(-1_000_000, 1_000_001);
            long y = rng.Next(-1_000_000, 1_000_001);

            ChunkKey chunk = WorldCoordinates.ChunkForTile(x, y);
            LocalCell local = WorldCoordinates.LocalForTile(x, y);

            if (local.X < 0 || local.X >= WorldCoordinates.ChunkSize
                || local.Y < 0 || local.Y >= WorldCoordinates.ChunkSize)
                throw new Exception($"LocalForTile({x},{y}) out of range: {local}");

            WorldCell origin = WorldCoordinates.TileOrigin(chunk);
            if (origin.X + local.X != x || origin.Y + local.Y != y)
                throw new Exception($"Round-trip broken for ({x},{y}): chunk={chunk} local={local} origin={origin}");

            // Индексация StreamingWorldView.AddBlock: (ushort)(local.Y * 64 + local.X).
            ushort index = (ushort)(local.Y * WorldCoordinates.ChunkSize + local.X);
            if (index < 0 || index >= WorldChunk.CellCount)
                throw new Exception($"Chunk index out of range for ({x},{y}): {index}");

            // Восстановление клетки из индекса (как в CollectPendingPlans).
            int rx = index % WorldCoordinates.ChunkSize;
            int ry = index / WorldCoordinates.ChunkSize;
            if (origin.X + rx != x || origin.Y + ry != y)
                throw new Exception($"Index round-trip broken for ({x},{y}).");
        }

        // Границы чанков: ChunkBounds — полуоткрытый прямоугольник 64×64.
        WorldRect bounds = WorldCoordinates.ChunkBounds(new ChunkKey(-9, -9));
        if (!bounds.Contains(-513, -513) || !bounds.Contains(-576, -576))
            throw new Exception("ChunkBounds(-9,-9) must contain (-513,-513) and (-576,-576).");
        if (bounds.Contains(-512, -512) || bounds.Contains(-577, -513) || bounds.Contains(-513, -577))
            throw new Exception("ChunkBounds(-9,-9) must NOT contain (-512,-512)/(-577,...)/( ...,-577).");

        GD.Print(" -> 20000 round-trips OK, ChunkBounds half-open OK!");
    }

    private void TestTileOriginCheckedOverflow()
    {
        GD.Print("[WTEST 3] TileOrigin checked overflow on extreme ChunkKey...");

        // long.MinValue * 64 обязан бросать OverflowException (checked), а не молча заворачиваться.
        ExpectThrows(() => WorldCoordinates.TileOrigin(new ChunkKey(long.MinValue / 64 - 1, 0)),
            "TileOrigin overflow X");
        ExpectThrows(() => WorldCoordinates.TileOrigin(new ChunkKey(0, long.MinValue / 64 - 1)),
            "TileOrigin overflow Y");
        ExpectThrows(() => WorldCoordinates.ChunkBounds(new ChunkKey(long.MaxValue / 64 + 1, 0)),
            "ChunkBounds overflow X");

        // Допустимые экстремумы не бросают.
        _ = WorldCoordinates.TileOrigin(new ChunkKey(long.MaxValue / 64, long.MaxValue / 64));

        GD.Print(" -> Checked overflow OK!");
    }

    // ------------------------------------------------------------------
    // (2a) GenerateColonyWindow на origin, кратном 512
    // ------------------------------------------------------------------
    private void TestGenerateColonyWindowAtRegionEdges()
    {
        GD.Print("[WTEST 4] GenerateColonyWindow at region-edge origins (512*k, incl. negative)...");

        // Окна, СТРЫДАЮЩИЕ границу региона (origin за 32 клетки до кратного 512),
        // и окна, РОВНО совпадающие с регионом. Все — без исключений, с валидными данными.
        long[] origins = { -1024 - 32, -512 - 32, -32, 0, 512 - 32, 512, 1024, 1024 - 32 };
        foreach (long origin in origins)
        {
            MapData data = UnifiedWorldGenerator.GenerateColonyWindow(Seed, GenVersion, origin, origin, 64, 64);
            if (data == null)
                throw new Exception($"GenerateColonyWindow({origin}) returned null.");
            if (data.Ground.GetLength(0) != 64 || data.Ground.GetLength(1) != 64)
                throw new Exception($"GenerateColonyWindow({origin}) wrong dimensions.");

            int water = 0, grass = 0, mountain = 0;
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    switch (data.Ground[x, y])
                    {
                        case TileType.Water: water++; break;
                        case TileType.Mountain: mountain++; break;
                        default: grass++; break;
                    }
                }
            if (water + grass + mountain != 64 * 64)
                throw new Exception($"GenerateColonyWindow({origin}) produced invalid terrain sum.");
            GD.Print($"   origin=({origin},{origin}): grass={grass} water={water} mountain={mountain}");
        }

        // Полное окно региона 512×512, начинающееся ровно на 512.
        MapData big = UnifiedWorldGenerator.GenerateColonyWindow(Seed, GenVersion, 512, 512, 512, 512);
        if (big == null || big.Ground.GetLength(0) != 512)
            throw new Exception("GenerateColonyWindow(512,512,512,512) invalid.");

        // Нулевые/отрицательные размеры обязаны бросать (контракт метода).
        ExpectThrows(() => UnifiedWorldGenerator.GenerateColonyWindow(Seed, GenVersion, 0, 0, 0, 64),
            "GenerateColonyWindow zero width");
        ExpectThrows(() => UnifiedWorldGenerator.GenerateColonyWindow(Seed, GenVersion, 0, 0, 64, -1),
            "GenerateColonyWindow negative height");

        GD.Print(" -> Region-edge origins OK (no crashes, valid data)!");
    }

    // ------------------------------------------------------------------
    // (2a) Шов колонии ↔ стриминга на кромке региона x=511|512 (+ F4 лес)
    // ------------------------------------------------------------------
    private void TestColonyEdgeVsStreamingSeam()
    {
        GD.Print("[WTEST 5] Colony edge (x=511) vs streaming chunk (x=512) seam audit...");

        // Колония = окно [0,512); стриминг подхватывает с x=512 чанком (8, k).
        MapData colony = UnifiedWorldGenerator.GenerateColonyWindow(Seed, GenVersion, 0, 0, 512, 512);

        int y0 = 64, y1 = 320; // полоса внутри чанков (8,1..4)
        int terrainMismatch = 0, terrainPairs = 0;
        int colonyTrees = 0, colonyCells = 0;
        int streamingForest = 0, streamingCells = 0;
        int maxElevDiff = 0;

        for (long cy = y0; cy < y1; cy += 4)
        {
            int chunkY = (int)WorldCoordinates.ChunkForTile(0, cy).Y;
            WorldChunk right = UnifiedWorldGenerator.GenerateChunk(Seed, GenVersion, new ChunkKey(8, chunkY));

            for (long wy = cy; wy < cy + 4 && wy < y1; wy++)
            {
                // Пара через кромку: последняя клетка колонии (511) и первая чанка (512).
                int ly = (int)(wy - chunkY * WorldCoordinates.ChunkSize);
                TileType colonyEdge = colony.Ground[511, wy];
                BaseTerrainKind streamEdge = right.Cells.Span[ly * WorldCoordinates.ChunkSize + 0].Terrain;
                terrainPairs++;
                if (colonyEdge != StreamToTile(streamEdge))
                    terrainMismatch++;

                // Высотная непрерывность (SampleCell-уровень) через кромку региона.
                var a = UnifiedWorldGenerator.SampleCell(Seed, GenVersion, 511, wy);
                var b = UnifiedWorldGenerator.SampleCell(Seed, GenVersion, 512, wy);
                maxElevDiff = Math.Max(maxElevDiff, Math.Abs((int)a.ElevationQ16 - (int)b.ElevationQ16));
            }

            // Плотность леса: 8 колонок колонии против 8 колонок стриминга (F4).
            for (int lx = 504; lx < 512; lx++)
                for (long wy = cy; wy < cy + 4 && wy < y1; wy++)
                {
                    colonyCells++;
                    if (colony.TreeOnGrass[lx, wy]) colonyTrees++;
                }
            for (int lx = 0; lx < 8; lx++)
                for (long wy = cy; wy < cy + 4 && wy < y1; wy++)
                {
                    int ly = (int)(wy - chunkY * WorldCoordinates.ChunkSize);
                    ref readonly var cell = ref right.Cells.Span[ly * WorldCoordinates.ChunkSize + lx];
                    streamingCells++;
                    if (cell.Terrain == BaseTerrainKind.Grass && cell.ForestQ16 >= WorldDecor.ForestMinQ16)
                        streamingForest++;
                }
        }

        double terrainMismatchRatio = terrainPairs == 0 ? 0 : 100.0 * terrainMismatch / terrainPairs;
        double colonyDensity = colonyCells == 0 ? 0 : 100.0 * colonyTrees / colonyCells;
        double streamDensity = streamingCells == 0 ? 0 : 100.0 * streamingForest / streamingCells;

        GD.Print($"   terrain mismatch across seam: {terrainMismatch}/{terrainPairs} ({terrainMismatchRatio:F1}%)");
        GD.Print($"   max elevation diff across 511|512: {maxElevDiff} Q16");
        GD.Print($"   forest density: colony last-8-cols={colonyDensity:F1}%  streaming first-8-cols={streamDensity:F1}%");

        if (terrainMismatchRatio > 20.0)
            throw new Exception($"[P1] Terrain seam at colony edge 511|512: {terrainMismatch}/{terrainPairs} pairs " +
                $"({terrainMismatchRatio:F1}%) disagree grass<->water/mountain. Visible cliff at island border.");
        if (maxElevDiff > 6000)
            throw new Exception($"[P1] Elevation cliff at region boundary 511|512: {maxElevDiff} Q16.");

        // F4: перепад плотности леса на кромке. Жёсткий гейт — «не обрыв», не «идеальная ровность».
        if (streamingForest >= 20 && colonyTrees * 4 < streamingForest)
            throw new Exception($"[P1][F4] Forest density cliff at colony edge: colony={colonyTrees} trees vs " +
                $"streaming={streamingForest} forest cells (ratio {(double)colonyTrees / streamingForest:F2}). " +
                "Colony treePass prunes, streaming draws ALL forest cells.");
        else if (streamingForest >= 20)
            GD.Print($"   forest ratio colony/streaming = {(double)colonyTrees / streamingForest:F2} (>= 0.25 OK)");

        GD.Print(" -> Colony/streaming seam OK!");
    }

    // ------------------------------------------------------------------
    // (2) Согласованность гейта размещения (WorldCellQuery) с чанками стриминга
    // ------------------------------------------------------------------
    private void TestWorldCellQueryVsChunkAgreement()
    {
        GD.Print("[WTEST 6] WorldCellQuery (placement gate) vs GenerateChunk agreement...");

        // Гейт CanPlace в StreamingWorldManager использует WorldCellQuery.TerrainAt (чистый
        // SampleCell), а вид рисует чанки с 4×4 интерполяцией климата. Расхождение = «стена
        // на визуально-воде» (или запрет на визуально-траве). Меряем долю расхождений.
        long[] chunkCoords = { -9, -8, 0, 7, 8, 9, 16, -16 };
        int mismatch = 0, total = 0;
        foreach (long cx in chunkCoords)
        {
            foreach (long cy in chunkCoords)
            {
                WorldChunk chunk = UnifiedWorldGenerator.GenerateChunk(Seed, GenVersion, new ChunkKey(cx, cy));
                ReadOnlySpan<GeneratedCell> cells = chunk.Cells.Span;
                for (int i = 0; i < cells.Length; i += 37) // прореженный обход
                {
                    long wx = cx * WorldCoordinates.ChunkSize + (i % WorldCoordinates.ChunkSize);
                    long wy = cy * WorldCoordinates.ChunkSize + (i / WorldCoordinates.ChunkSize);
                    BaseTerrainKind gate = WorldCellQuery.TerrainAt(Seed, GenVersion, wx, wy);
                    total++;
                    if (gate != cells[i].Terrain)
                        mismatch++;
                }
            }
        }

        double ratio = total == 0 ? 0 : 100.0 * mismatch / total;
        GD.Print($"   gate-vs-chunk terrain mismatch: {mismatch}/{total} ({ratio:F1}%)");
        if (ratio > 25.0)
            throw new Exception($"[P1] WorldCellQuery disagrees with chunk terrain in {ratio:F1}% of samples: " +
                "placement gates allow/deny cells that visually differ (wall on water).");

        // Отрицательные координаты через гейт — без исключений и с корректным типом.
        for (long t = -600; t < 0; t += 37)
        {
            BaseTerrainKind q = WorldCellQuery.TerrainAt(Seed, GenVersion, t, t);
            BaseTerrainKind s = UnifiedWorldGenerator.SampleCell(Seed, GenVersion, t, t).Terrain;
            if (q != s)
                throw new Exception($"WorldCellQuery negative coords mismatch at ({t},{t}).");
        }

        GD.Print(" -> WorldCellQuery agreement OK!");
    }

    private static TileType StreamToTile(BaseTerrainKind t) => t switch
    {
        BaseTerrainKind.Water => TileType.Water,
        BaseTerrainKind.Mountain => TileType.Mountain,
        _ => TileType.Grass
    };

    private static void ExpectThrows(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return; // ожидаемо
        }
        throw new Exception($"Expected exception for: {what}, but call succeeded.");
    }
}
