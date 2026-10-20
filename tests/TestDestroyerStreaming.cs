using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.UI.Streaming;

namespace Game.Tests;

/// <summary>
/// @destroyer (PLAN §117, задача 2): adversarial E2E стриминга мира на ЖИВОМ
/// StreamingWorldView (узел создаётся вне дерева; _Ready/_Process вызываются вручную —
/// headless-прогон без кадров).
///  a) Спавн/окно на КРОМКЕ региона (граница 512-тайловых регионов, вкл. отрицательные).
///  b) ChunkKey на отрицательных координатах; выгрузка+повторная загрузка чанка с дельтой
///     (стена обязана пережить «поставил → ушёл → вернулся»).
///  c) StreamingWorldManager.TryPlaceBlock/TryPlaceWall на НЕзагруженном чанке —
///     корректный false, никаких исключений.
///  d) WorldJobBoard: план в выгруженном чанке; Complete на чужой/несуществующей клетке;
///     Claim/Release симметрия (утечки _claimed).
///  e) Смена сида (ResetWorld): старые чанки/дельты не протекают в новый мир.
/// </summary>
public partial class TestDestroyerStreaming : RefCounted
{
    private const uint GenVersion = 3;

    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[DESTROYER STREAMING] Starting adversarial world streaming audit (@destroyer)");
        GD.Print("==================================================");
        try
        {
            UnifiedWorldGenerator.SetWorldOrigin(0, 0);

            TestRegionEdgeStreamingNoCrash();
            TestNegativeChunkKeyAndDeltaRoundTrip();
            TestTryPlaceOnUnloadedChunk();
            TestWorldJobBoardAdversarial();
            TestSeedChangeResetWorld();

            GD.Print("==================================================");
            GD.Print("[DESTROYER STREAMING OK] ALL STREAMING ADVERSARIAL TESTS PASSED! (Code 0)");
            GD.Print("==================================================");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DESTROYER STREAMING FAILED] {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string NewTempDir(string tag)
        => Path.Combine(OS.GetUserDataDir(), $"destroyer_{tag}_{Guid.NewGuid():N}");

    private static void CleanupDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // мягкая уборка: хвосты I/O воркеров не должны валить тест
        }
    }

    /// <summary>Вид вне дерева: _Ready вызываем вручную (headless, кадров нет).</summary>
    private static StreamingWorldView CreateView(ulong seed, string dir)
    {
        var view = new StreamingWorldView
        {
            Name = "DestroyerStreamingView",
            SaveDirectory = dir,
            WipeStoreOnStart = true,
            WorldSeed = seed,
            GeneratorVersion = GenVersion,
            MaxConcurrency = 4,
            MinWindowChunks = 3,
            MaxWindowChunks = 6,
            MaxReadyChunksPerFrame = 16,
            MaxUnloadSchedulesPerFrame = 16,
        };
        view._Ready();
        return view;
    }

    private static StreamingWorldManager CreateManager(StreamingWorldView view, string dir, ulong seed)
    {
        var coordinator = new WorldSaveCoordinator(dir, seed, GenVersion, UnifiedWorldGenerator.FeatureSchemaVersion);
        var manager = new StreamingWorldManager(view, coordinator);
        manager.Open(0, 0);
        return manager;
    }

    /// <summary>Гоняем Update+_Process, пока выполнится условие (или таймаут).</summary>
    private static bool Pump(StreamingWorldView view, WorldRect rect, Func<bool> done, int timeoutMs, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            view.Update(rect);
            view._Process(0.016);
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                GD.PrintErr($"[DESTROYER] Pump timeout ({timeoutMs} ms) while waiting: {what}");
                return false;
            }
            Thread.Sleep(15);
        }
        return true;
    }

    private static void DestroyView(StreamingWorldView view)
    {
        try
        {
            view.Shutdown();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DESTROYER] Shutdown exception: {ex.Message}");
        }
        view.Free();
    }

    private static bool FindGrassCell(StreamingWorldView view, StreamingWorldManager manager,
        WorldRect rect, int stride, out long x, out long y)
    {
        for (long cy = rect.MinY + 4; cy < rect.MaxY - 4; cy += stride)
        {
            for (long cx = rect.MinX + 4; cx < rect.MaxX - 4; cx += stride)
            {
                if (view.TryCellInfo(cx, cy, out GeneratedCell cell)
                    && cell.Terrain == BaseTerrainKind.Grass
                    && manager.CanPlace(cx, cy))
                {
                    x = cx;
                    y = cy;
                    return true;
                }
            }
        }
        x = 0;
        y = 0;
        return false;
    }

    /// <summary>Широкий поиск травы по всем загруженным чанкам вокруг центра (для надёжности фикстур).</summary>
    private static bool FindGrassCellWide(StreamingWorldView view, StreamingWorldManager manager,
        long centerX, long centerY, out long x, out long y, int halfSpan = 176, int stride = 8)
    {
        for (long cy = centerY - halfSpan; cy <= centerY + halfSpan; cy += stride)
        {
            for (long cx = centerX - halfSpan; cx <= centerX + halfSpan; cx += stride)
            {
                if (view.TryCellInfo(cx, cy, out GeneratedCell cell)
                    && cell.Terrain == BaseTerrainKind.Grass
                    && manager.CanPlace(cx, cy))
                {
                    x = cx;
                    y = cy;
                    return true;
                }
            }
        }
        x = 0;
        y = 0;
        return false;
    }

    /// <summary>Чистый (без вида) прескан сида: есть ли трава в целевой зоне. Выбор сида для смены мира.</summary>
    private static ulong ChooseSeedWithGrass(ulong[] candidates, long cx, long cy, int radius, int need)
    {
        foreach (ulong seed in candidates)
        {
            int grass = 0;
            for (long y = cy - radius; y <= cy + radius && grass < need; y += 32)
                for (long x = cx - radius; x <= cx + radius && grass < need; x += 32)
                    if (WorldCellQuery.TerrainAt(seed, GenVersion, x, y) == BaseTerrainKind.Grass)
                        grass++;
            if (grass >= need)
                return seed;
        }
        return candidates[0];
    }

    // ------------------------------------------------------------------
    // (a) Кромка региона: окно стриминга на границе 512×512 (и отрицательной)
    // ------------------------------------------------------------------
    private void TestRegionEdgeStreamingNoCrash()
    {
        GD.Print("[STEST 1] Streaming window ON the region edge (512,512) and (-512,-512)...");
        string dir = NewTempDir("edge");
        var view = CreateView(0xDE570001UL, dir);
        try
        {
            // Положительная кромка: окно 64×64, центр ровно на (512,512).
            var rect = new WorldRect(480, 480, 544, 544);
            ChunkKey[] need =
            {
                new(7, 7), new(8, 7), new(7, 8), new(8, 8),
            };
            if (!Pump(view, rect, () => AllLoaded(view, need), 30000, "region-edge window load"))
                throw new Exception("[P1] Chunks around region edge (512,512) did not load in 30s.");

            foreach (ChunkKey key in need)
            {
                if (!view.IsLoaded(key))
                    throw new Exception($"[P1] Chunk {key} at region edge is not loaded (seam/hole).");
            }
            GD.Print($"   -> edge window loaded: {view.LoadedChunks} chunks, summary: {view.TerrainSummary()}");

            // Непрерывность высоты через кромку региона (511|512) по данным чанков.
            int maxDiff = 0, cliffs = 0, pairs = 0;
            for (long y = 484; y < 540; y += 2)
            {
                if (!view.TryCellInfo(511, y, out GeneratedCell a) || !view.TryCellInfo(512, y, out GeneratedCell b))
                    throw new Exception($"[P1] TryCellInfo failed at region edge pair (511|512, y={y}).");
                int diff = Math.Abs((int)a.ElevationQ16 - (int)b.ElevationQ16);
                maxDiff = Math.Max(maxDiff, diff);
                pairs++;
                if (diff > 6000)
                    cliffs++;
            }
            GD.Print($"   -> elevation across edge 511|512: max diff={maxDiff}, cliffs(>6000)={cliffs}/{pairs}");
            if (cliffs > 0)
                throw new Exception($"[P1] Elevation cliffs at region boundary 512: {cliffs}/{pairs} pairs.");

            // Отрицательная кромка региона: (-512,-512).
            var rectN = new WorldRect(-544, -544, -480, -480);
            ChunkKey[] needN =
            {
                new(-9, -9), new(-8, -9), new(-9, -8), new(-8, -8),
            };
            if (!Pump(view, rectN, () => AllLoaded(view, needN), 30000, "negative region-edge window load"))
                throw new Exception("[P1] Chunks around negative region edge (-512,-512) did not load in 30s.");
            if (!view.IsLoaded(new ChunkKey(-9, -9)) || !view.IsLoaded(new ChunkKey(-8, -8)))
                throw new Exception("[P1] Negative edge chunks (-9,-9)/(-8,-8) not loaded.");

            maxDiff = 0; cliffs = 0; pairs = 0;
            for (long y = -540; y < -484; y += 2)
            {
                if (!view.TryCellInfo(-513, y, out GeneratedCell a) || !view.TryCellInfo(-512, y, out GeneratedCell b))
                    throw new Exception($"[P1] TryCellInfo failed at negative edge pair (-513|-512, y={y}).");
                int diff = Math.Abs((int)a.ElevationQ16 - (int)b.ElevationQ16);
                maxDiff = Math.Max(maxDiff, diff);
                pairs++;
                if (diff > 6000)
                    cliffs++;
            }
            GD.Print($"   -> elevation across negative edge -513|-512: max diff={maxDiff}, cliffs={cliffs}/{pairs}");
            if (cliffs > 0)
                throw new Exception($"[P1] Elevation cliffs at negative region boundary -512: {cliffs}/{pairs} pairs.");
        }
        finally
        {
            DestroyView(view);
            Thread.Sleep(100);
            CleanupDir(dir);
        }
        GD.Print(" -> Region-edge streaming OK!");
    }

    // ------------------------------------------------------------------
    // (b) Отрицательные ChunkKey + дельта: поставил стену → ушёл → вернулся
    // ------------------------------------------------------------------
    private void TestNegativeChunkKeyAndDeltaRoundTrip()
    {
        GD.Print("[STEST 2] Negative ChunkKey + wall delta survives unload/reload...");
        ulong seed = 0xDE570001UL; // проверен в STEST 1: ~59% травы у отрицательной кромки
        string dir = NewTempDir("delta");
        var view = CreateView(seed, dir);
        var manager = CreateManager(view, dir, seed);
        try
        {
            var rect = new WorldRect(-544, -544, -480, -480);
            if (!Pump(view, rect, () => view.LoadedChunks >= 9, 30000, "negative window load"))
                throw new Exception("[P1] Negative-coordinate window did not load in 30s.");

            if (!FindGrassCell(view, manager, rect, 3, out long wallX, out long wallY))
                throw new Exception("No grass cell found in negative window (fixture failure).");

            ChunkKey key = WorldCoordinates.ChunkForTile(wallX, wallY);
            if (key.X >= 0 || key.Y >= 0)
                throw new Exception($"Fixture: expected negative chunk key, got {key}.");

            // ставим стену в ОТРИЦАТЕЛЬНОМ чанке
            if (!manager.TryPlaceWall(wallX, wallY))
                throw new Exception($"[P1] TryPlaceWall failed on grass cell ({wallX},{wallY}) in loaded negative chunk {key}.");
            if (!view.HasWall(wallX, wallY))
                throw new Exception("[P1] Wall not visible after TryPlaceWall (negative chunk).");
            if (view.WallCount != 1)
                throw new Exception($"[P1] WallCount={view.WallCount} after one wall (expected 1).");

            // уходим далеко — чанк выгружается, дельта пишется на диск
            var farRect = new WorldRect(900000, 900000, 900064, 900064);
            if (!Pump(view, farRect, () => !view.IsLoaded(key), 30000, "negative chunk unload"))
                throw new Exception("[P1] Negative chunk did not unload after focus left.");
            if (view.HasWall(wallX, wallY))
                throw new Exception("[P2] HasWall must be false after chunk unload.");
            if (!view.IsChunkStored(key))
                throw new Exception("[P1] Delta file for wall chunk was NOT persisted on unload — edits lost on exit.");
            GD.Print($"   -> wall at ({wallX},{wallY}) chunk {key} unloaded & stored.");

            // возвращаемся — чанк перегенерируется, дельта применяется
            if (!Pump(view, rect, () => view.IsLoaded(key), 30000, "negative chunk reload"))
                throw new Exception("[P1] Negative chunk did not reload after focus returned.");
            if (!view.HasWall(wallX, wallY))
                throw new Exception("[P1][DATA LOSS] Wall placed in negative chunk disappeared after unload+reload " +
                    $"(cell ({wallX},{wallY}), chunk {key}). Delta snapshot not restored.");
            if (view.WallCount != 1)
                throw new Exception($"[P1] WallCount={view.WallCount} after reload (expected 1).");

            // снятие стены + повторная выгрузка: удаление тоже обязано пережить цикл
            if (!manager.TryRemoveWall(wallX, wallY))
                throw new Exception("[P1] TryRemoveWall failed on existing wall.");
            if (!Pump(view, farRect, () => !view.IsLoaded(key), 30000, "chunk unload after remove"))
                throw new Exception("[P1] Chunk did not unload after wall removal.");
            if (!Pump(view, rect, () => view.IsLoaded(key), 30000, "chunk reload after remove"))
                throw new Exception("[P1] Chunk did not reload after wall removal.");
            if (view.HasWall(wallX, wallY))
                throw new Exception("[P1][DATA LOSS] Removed wall came back after unload+reload (delta not compacted).");
            if (view.WallCount != 0)
                throw new Exception($"[P1] WallCount={view.WallCount} after remove+reload (expected 0).");
        }
        finally
        {
            DestroyView(view);
            Thread.Sleep(100);
            CleanupDir(dir);
        }
        GD.Print(" -> Negative ChunkKey + delta round-trip OK!");
    }

    // ------------------------------------------------------------------
    // (c) TryPlaceBlock на НЕзагруженном чанке — false, не исключение
    // ------------------------------------------------------------------
    private void TestTryPlaceOnUnloadedChunk()
    {
        GD.Print("[STEST 3] Manager placement ops on UNLOADED chunks must return false, never throw...");
        ulong seed = 0xDE570001UL; // та же зона, что верифицирована STEST 1
        string dir = NewTempDir("unloaded");
        var view = CreateView(seed, dir);
        var manager = CreateManager(view, dir, seed);
        try
        {
            var rect = new WorldRect(512, 512, 576, 576);
            if (!Pump(view, rect, () => view.LoadedChunks >= 9, 30000, "unloaded-test window load"))
                throw new Exception("[P1] Window did not load for unloaded-chunk test.");

            // контроль: на загруженной траве размещение работает
            if (!FindGrassCell(view, manager, rect, 3, out long gx, out long gy))
                throw new Exception("No grass cell found (fixture failure).");
            if (!manager.TryPlaceBlock(WorldDeltaKind.BuildingPlan, gx, gy))
                throw new Exception("[P1] Positive control failed: TryPlaceBlock on loaded grass returned false.");
            if (!manager.TryRemoveBlock(WorldDeltaKind.BuildingPlan, gx, gy))
                throw new Exception("[P1] Positive control failed: TryRemoveBlock returned false.");

            // клетка в незагруженном чанке (очень далеко от окна)
            (long farX, long farY)[] farCells =
            {
                (800000, 800000), (-800000, 800000), (800000, -800000), (-800000, -800000),
                (513, -800000), (-513, 800000),
            };
            foreach (var (fx, fy) in farCells)
            {
                ChunkKey farKey = WorldCoordinates.ChunkForTile(fx, fy);
                if (view.IsLoaded(farKey))
                    throw new Exception($"Fixture: chunk {farKey} unexpectedly loaded.");

                if (manager.IsBlocked(fx, fy) != null)
                    throw new Exception($"[P1] IsBlocked({fx},{fy}) on unloaded chunk must return null, got value.");

                if (manager.TryPlaceWall(fx, fy))
                    throw new Exception($"[P1] TryPlaceWall({fx},{fy}) on UNLOADED chunk returned true.");
                if (manager.TryPlaceBlock(WorldDeltaKind.Wall, fx, fy))
                    throw new Exception($"[P1] TryPlaceBlock(Wall) on UNLOADED chunk returned true.");
                if (manager.TryPlaceBlock(WorldDeltaKind.Building, fx, fy))
                    throw new Exception($"[P1] TryPlaceBlock(Building) on UNLOADED chunk returned true.");
                if (manager.TryPlaceBlock(WorldDeltaKind.CutTree, fx, fy))
                    throw new Exception($"[P1] TryPlaceBlock(CutTree) on UNLOADED chunk returned true.");
                if (manager.TryRemoveWall(fx, fy))
                    throw new Exception($"[P1] TryRemoveWall on UNLOADED chunk returned true.");
                if (manager.TryRemoveBlock(WorldDeltaKind.MinedStone, fx, fy))
                    throw new Exception($"[P1] TryRemoveBlock on UNLOADED chunk returned true.");
                if (manager.TryReplaceBlock(WorldDeltaKind.WallPlan, WorldDeltaKind.Wall, fx, fy))
                    throw new Exception($"[P1] TryReplaceBlock on UNLOADED chunk returned true.");
            }

            // самовывоз: метод обязан пережить и граничные значения
            _ = manager.IsBlocked(long.MaxValue, long.MinValue);
            _ = manager.CanPlace(0, 0);
        }
        finally
        {
            DestroyView(view);
            Thread.Sleep(100);
            CleanupDir(dir);
        }
        GD.Print(" -> Unloaded-chunk placement guards OK!");
    }

    // ------------------------------------------------------------------
    // (d) WorldJobBoard: планы, claim/release, complete, выгрузка
    // ------------------------------------------------------------------
    private void TestWorldJobBoardAdversarial()
    {
        GD.Print("[STEST 4] WorldJobBoard adversarial: claims, complete, unloaded plans...");
        ulong seed = 0xDE570001UL;
        string dir = NewTempDir("board");
        var view = CreateView(seed, dir);
        var manager = CreateManager(view, dir, seed);
        var board = new WorldJobBoard(view, manager);
        try
        {
            var rect = new WorldRect(512, 512, 704, 704); // за кромкой колонии [0,512)
            if (!Pump(view, rect, () => view.LoadedChunks >= 9, 30000, "board-test window load"))
                throw new Exception("[P1] Board-test window did not load.");

            // 4 разнесённых травяных клетки по загруженной области
            var cells = new List<(long X, long Y)>();
            for (long cy = 528; cy < 696 && cells.Count < 4; cy += 4)
                for (long cx = 528; cx < 696 && cells.Count < 4; cx += 4)
                {
                    if (!view.TryCellInfo(cx, cy, out GeneratedCell cell) || cell.Terrain != BaseTerrainKind.Grass)
                        continue;
                    if (!manager.CanPlace(cx, cy))
                        continue;
                    bool tooClose = false;
                    foreach (var c in cells)
                        if (Math.Abs(c.X - cx) < 16 || Math.Abs(c.Y - cy) < 16)
                        {
                            tooClose = true;
                            break;
                        }
                    if (!tooClose)
                        cells.Add((cx, cy));
                }
            if (cells.Count < 3)
                throw new Exception($"Fixture: found only {cells.Count} grass cells for board test.");

            // планы стен
            for (int i = 0; i < 3; i++)
                if (!manager.TryPlaceBlock(WorldDeltaKind.WallPlan, cells[i].X, cells[i].Y))
                    throw new Exception($"[P1] TryPlaceBlock(WallPlan) failed at {cells[i]}.");

            board.Refresh(700, 700);
            if (board.PendingCount != 3)
                throw new Exception($"[P1] PendingCount={board.PendingCount} after 3 plans (expected 3).");
            if (!board.HasFreeWork)
                throw new Exception("HasFreeWork must be true with 3 pending and 0 claimed.");

            // берём задачи: ближняя первой
            if (!board.TryTakeNearest(690, 690, out WorldPlan p1))
                throw new Exception("[P1] TryTakeNearest failed with pending plans.");
            (long ex, long ey) expected = Nearest(cells.GetRange(0, 3), 690, 690);
            if (p1.CellX != expected.Item1 || p1.CellY != expected.Item2)
                throw new Exception($"[P2] TryTakeNearest not nearest: got ({p1.CellX},{p1.CellY}), expected {expected}.");
            if (board.ClaimedCount != 1)
                throw new Exception($"ClaimedCount={board.ClaimedCount} after 1 take.");
            if (!board.IsChunkClaimed(WorldCoordinates.ChunkForTile(p1.CellX, p1.CellY)))
                throw new Exception("IsChunkClaimed must be true for the claimed plan chunk.");

            if (!board.TryTakeNearest(690, 690, out WorldPlan p2) || !board.TryTakeNearest(690, 690, out WorldPlan p3))
                throw new Exception("[P1] Second/third TryTakeNearest failed.");
            if (SameCell(p2, p1) || SameCell(p3, p1) || SameCell(p3, p2))
                throw new Exception("[P1] Claimed cells leaked into pending (double-take of same plan).");
            if (board.ClaimedCount != 3)
                throw new Exception($"ClaimedCount={board.ClaimedCount} after 3 takes.");
            if (board.HasFreeWork)
                throw new Exception("HasFreeWork must be false when everything claimed.");
            if (board.TryTakeNearest(690, 690, out _))
                throw new Exception("[P2] TryTakeNearest succeeded with all plans claimed.");

            // Release → клетка снова доступна
            board.Release(p2);
            if (board.ClaimedCount != 2)
                throw new Exception($"ClaimedCount={board.ClaimedCount} after Release (expected 2).");
            if (!board.TryTakeNearest(690, 690, out WorldPlan p2again) || !SameCell(p2again, p2))
                throw new Exception("[P1] Released plan was not re-taken.");

            // Complete: план → готовая стена
            if (!board.Complete(p2again))
                throw new Exception("[P1] Complete failed for a claimed plan on grass.");
            if (!view.HasBlock(WorldDeltaKind.Wall, p2.CellX, p2.CellY))
                throw new Exception("[P1] Complete did not place the finished Wall block.");
            if (view.HasBlock(WorldDeltaKind.WallPlan, p2.CellX, p2.CellY))
                throw new Exception("[P1] Complete left the WallPlan blueprint in place.");
            if (board.ClaimedCount != 2)
                throw new Exception($"ClaimedCount={board.ClaimedCount} after Complete (expected 2: p1,p3).");

            // Complete на ЧУЖОЙ (никем не взятой) план — работает без владельческих проверок
            if (!board.Complete(p1))
                throw new Exception("[P2] Complete on an unclaimed-but-existing plan returned false.");
            if (!view.HasBlock(WorldDeltaKind.Wall, p1.CellX, p1.CellY))
                throw new Exception("[P2] Complete on unclaimed plan did not finish the wall.");
            if (board.ClaimedCount != 1)
                throw new Exception($"ClaimedCount={board.ClaimedCount} after foreign Complete (expected 1: p3).");

            // Complete на клетке БЕЗ плана — false + LostPlans++, без исключений
            int lostBefore = board.LostPlans;
            var ghost = new WorldPlan(WorldDeltaKind.WallPlan, 700, 700);
            if (board.Complete(ghost))
                throw new Exception("[P2] Complete on a cell WITHOUT any plan must return false.");
            if (board.LostPlans != lostBefore + 1)
                throw new Exception($"[P2] LostPlans did not increment on ghost complete: {board.LostPlans}.");
            // повторный Complete того же госта — тоже false, счётчик растёт, исключений нет
            if (board.Complete(ghost))
                throw new Exception("[P2] Repeated ghost Complete must return false.");

            // Claim/Release симметрия — стресс на утечки _claimed
            for (int i = 0; i < 300; i++)
                if (board.TryTakeNearest(690, 690, out WorldPlan q))
                    board.Release(q);
            if (board.ClaimedCount != 1)
                throw new Exception($"[P1][LEAK] Claim/Release asymmetry: ClaimedCount={board.ClaimedCount} " +
                    "after 300 take/release cycles (expected 1: the claimed p3).");

            // план в ВЫГРУЖЕННОМ чанке исчезает из очереди
            board.Refresh(700, 700);
            if (board.PendingCount != 1)
                throw new Exception($"PendingCount={board.PendingCount} before unload (expected 1: p3).");
            ChunkKey k3 = WorldCoordinates.ChunkForTile(p3.CellX, p3.CellY);
            var farRect = new WorldRect(900000, 900000, 900064, 900064);
            if (!Pump(view, farRect, () => !view.IsLoaded(k3), 30000, "plan chunk unload"))
                throw new Exception("[P1] Plan chunk did not unload.");
            board.Refresh(1000000, 1000000);
            if (board.PendingCount != 0)
                throw new Exception($"[P1] PendingCount={board.PendingCount} with ALL plan chunks unloaded (expected 0).");
            if (board.IsStillPlanned(p3))
                throw new Exception("[P2] IsStillPlanned must be false for an unloaded chunk.");
            board.Release(p3); // без исключений на выгруженном
            if (board.IsChunkClaimed(k3))
                throw new Exception("[P2] IsChunkClaimed must be false after Release.");

            // вернулись: план снова в очереди (дельта пережила выгрузку) и завершается
            if (!Pump(view, rect, () => view.IsLoaded(k3), 30000, "plan chunk reload"))
                throw new Exception("[P1] Plan chunk did not reload.");
            board.Refresh(700, 700);
            if (board.PendingCount != 1)
                throw new Exception($"[P1] PendingCount={board.PendingCount} after reload (expected 1: p3 restored).");
            if (!board.TryTakeNearest(690, 690, out WorldPlan p3again) || !SameCell(p3again, p3))
                throw new Exception("[P1] Plan p3 not restorable after chunk reload.");
            if (!board.Complete(p3again))
                throw new Exception("[P1] Complete(p3) failed after reload.");
            board.Refresh(700, 700);
            if (board.PendingCount != 0)
                throw new Exception($"PendingCount={board.PendingCount} after completing all plans (expected 0).");
        }
        finally
        {
            DestroyView(view);
            Thread.Sleep(100);
            CleanupDir(dir);
        }
        GD.Print(" -> WorldJobBoard adversarial OK!");
    }

    // ------------------------------------------------------------------
    // (e) Смена сида (R): ResetWorld — старые чанки/дельты не протекают
    // ------------------------------------------------------------------
    private void TestSeedChangeResetWorld()
    {
        GD.Print("[STEST 5] Seed change (ResetWorld) isolation audit...");
        ulong s1 = 0xDE570001UL;
        // s2 подбираем пресканом: в целевой зоне обязана быть трава (для своей стены)
        ulong s2 = ChooseSeedWithGrass(
            new ulong[] { 0x0D00D00DUL, 0x0BADBEE5UL, 0x0DEFACEDUL, 0x0C0FFEE5UL, 0x0ABACABAUL },
            600, 600, 160, need: 8);
        GD.Print($"   -> s1=0x{s1:X}, s2=0x{s2:X}");
        string dir = NewTempDir("seed");
        var view = CreateView(s1, dir);
        var manager1 = CreateManager(view, dir, s1);
        try
        {
            var rect = new WorldRect(512, 512, 704, 704);
            if (!Pump(view, rect, () => view.LoadedChunks >= 9, 30000, "seed s1 window load"))
                throw new Exception("[P1] s1 window did not load.");
            if (!FindGrassCellWide(view, manager1, 600, 600, out long wallX, out long wallY))
                throw new Exception("No grass cell found in s1 (fixture failure).");
            if (!manager1.TryPlaceWall(wallX, wallY))
                throw new Exception("[P1] TryPlaceWall failed in s1.");
            ChunkKey key = WorldCoordinates.ChunkForTile(wallX, wallY);

            var farRect = new WorldRect(900000, 900000, 900064, 900064);
            if (!Pump(view, farRect, () => !view.IsLoaded(key), 30000, "s1 chunk unload before reset"))
                throw new Exception("[P1] s1 chunk did not unload before seed reset.");
            if (!view.IsChunkStored(key))
                throw new Exception("Fixture: s1 delta not stored before seed reset.");

            // ПОРЯДОК как в Main.OnSeedRegenerated: сначала WorldSeed, потом ResetWorld(newSeed)
            view.WorldSeed = s2;
            view.ResetWorld(s2);

            if (view.LoadedChunks != 0)
                throw new Exception($"[P1] LoadedChunks={view.LoadedChunks} right after ResetWorld (expected 0).");
            if (view.WallCount != 0)
                throw new Exception($"[P1] WallCount={view.WallCount} right after ResetWorld (expected 0).");
            if (view.HasWall(wallX, wallY))
                throw new Exception("[P1] Old-world wall survived ResetWorld in memory.");

            var manager2 = CreateManager(view, dir, s2); // InitStreamingWorld-эквивалент

            // новый мир в ТОМ ЖЕ окне (тот же ключ чанка!): дельта s1 не должна примениться
            if (!Pump(view, rect, () => view.IsLoaded(key), 30000, "s2 window load"))
                throw new Exception("[P1] s2 did not reload the same chunk key.");
            if (view.HasWall(wallX, wallY))
                throw new Exception($"[P1][LEAK] Old-seed delta leaked into the new world: wall at ({wallX},{wallY}) " +
                    "reappeared after ResetWorld(s2)+reload (snapshot seed check failed).");
            GD.Print("   -> s1 delta did not leak into s2. chunks=" + view.LoadedChunks);

            // чанк обязан оставаться загруженным: stale unload-задачи старого мира не должны
            // выкинуть его (см. _unloadTasks, не очищаемый в ResetWorld)
            for (int i = 0; i < 30; i++)
            {
                view.Update(rect);
                view._Process(0.033);
                Thread.Sleep(20);
                if (!view.IsLoaded(key))
                    throw new Exception("[P1][RACE] Chunk lost mid-run after ResetWorld: stale unload task from the " +
                        "old world drained into the new world (StreamingWorldView.ResetWorld does not clear _unloadTasks).");
            }

            // новый мир пишет свои дельты
            if (!FindGrassCellWide(view, manager2, 600, 600, out long w2x, out long w2y))
                throw new Exception("No grass cell found in s2 (fixture failure).");
            if (!manager2.TryPlaceWall(w2x, w2y))
                throw new Exception("[P1] TryPlaceWall failed in s2.");
            if (!Pump(view, farRect, () => !view.IsLoaded(WorldCoordinates.ChunkForTile(w2x, w2y)), 30000, "s2 unload"))
                throw new Exception("[P1] s2 chunk did not unload.");
            if (!Pump(view, rect, () => view.IsLoaded(WorldCoordinates.ChunkForTile(w2x, w2y)), 30000, "s2 reload"))
                throw new Exception("[P1] s2 chunk did not reload.");
            if (!view.HasWall(w2x, w2y))
                throw new Exception("[P1] New-world wall did not survive its own unload/reload cycle.");
            if (view.HasWall(wallX, wallY))
                throw new Exception("[P1] s1 wall appeared during s2 cycle (cross-seed leak).");

            // ResetWorld(0) — сид не меняется (документированное поведение)
            view.ResetWorld(0);
            if (view.WorldSeed != s2)
                throw new Exception($"[P2] ResetWorld(0) must keep current seed, changed to {view.WorldSeed}.");

            // серия быстрых сбросов подряд (R-спам) — без исключений и потери работоспособности
            ulong s3 = 0xDE570100UL, s4 = 0xDE570200UL;
            foreach (ulong s in new[] { s3, s4 })
            {
                view.WorldSeed = s;
                view.ResetWorld(s);
                view.Update(rect);
                view._Process(0.016);
                Thread.Sleep(50);
            }
            view.WorldSeed = s2;
            view.ResetWorld(s2);
            if (!Pump(view, rect, () => view.LoadedChunks >= 1, 30000, "recovery after R-spam"))
                throw new Exception("[P1] Streaming did not recover after consecutive seed resets (R-spam).");
        }
        finally
        {
            DestroyView(view);
            Thread.Sleep(100);
            CleanupDir(dir);
        }
        GD.Print(" -> Seed change / ResetWorld isolation OK!");
    }

    private static bool AllLoaded(StreamingWorldView view, ChunkKey[] keys)
    {
        foreach (ChunkKey key in keys)
            if (!view.IsLoaded(key))
                return false;
        return true;
    }

    private static bool SameCell(WorldPlan a, WorldPlan b) => a.CellX == b.CellX && a.CellY == b.CellY;

    private static (long, long) Nearest(List<(long X, long Y)> cells, float fromX, float fromY)
    {
        double best = double.MaxValue;
        (long, long) result = (0, 0);
        foreach (var c in cells)
        {
            double dx = c.X + 0.5f - fromX;
            double dy = c.Y + 0.5f - fromY;
            double d = dx * dx + dy * dy;
            if (d < best)
            {
                best = d;
                result = (c.X, c.Y);
            }
        }
        return result;
    }
}
