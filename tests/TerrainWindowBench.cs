using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.UI;

namespace Game.Tests;

/// <summary>
/// Headless-бенчмарк follow-окна террейна MapRenderer (dev-terrain-window, PLAN §117, F2).
///
/// Замеряет:
///  (а) СТАРЫЙ путь: полный пересчёт окна 512×512 внешних клеток построчным
///      WorldCellQuery.CellAt (Parallel.For БЕЗ капа — как было в проде):
///      каждый семпл = полный SampleCell без overrideClimate → SampleBlendedCell
///      (тектоника/Voronoi) на КАЖДУЮ клетку + domain warp + ~15 ValueNoise;
///  (б) НОВЫЙ полный путь: сетка климата 33×33 (шаг 16) + Lerp + SampleCell(override)
///      (как GenerateChunk; кап DOP как в проде);
///  (в) НОВЫЙ инкрементальный сдвиг +64: сдвиг буфера + полоса 64×512;
///  (г) корректность: побитовое совпадение нового заполнения с GenerateChunk
///      на 2×2 чанках (гарантия 0 швов окно↔стриминг).
///
/// Каждый замер печатает fingerprint (детерминизм-фингерпринт).
/// Условия = живая сессия lead §117.1: спавн-регион [950, 637], сид 987654321, ver 3.
/// Окно берётся ЗА границей острова (локальный origin 512,512) — худший случай
/// старого пути (100% внешних клеток).
///
/// Run: godot --headless --path . --script tests/run_terrain_window_bench.gd
/// </summary>
public partial class TerrainWindowBench : Node
{
    private const ulong Seed = 987654321ul;
    private const uint Ver = UnifiedWorldGenerator.DefaultGeneratorVersion;
    private const long SpawnRegionX = 950;
    private const long SpawnRegionY = 637;
    private const long BenchOriginX = 512;
    private const long BenchOriginY = 512;
    private const int W = MapRenderer.TerrainWindowTiles;

    public string Run(string mode = "all")
    {
        try
        {
            int fillDop = Math.Max(2, System.Environment.ProcessorCount / 4);
            UnifiedWorldGenerator.SetWorldOrigin(SpawnRegionX * WorldRegions.RegionSize, SpawnRegionY * WorldRegions.RegionSize);
            GD.Print($"[TWIN] env cores={System.Environment.ProcessorCount} seed={Seed} ver={Ver} window={W}x{W} fillDop={fillDop} origin=({BenchOriginX},{BenchOriginY}) worldOrigin=({UnifiedWorldGenerator.WorldOriginX},{UnifiedWorldGenerator.WorldOriginY})");

            Warmup();

            // Режимы через запятую (по одному прогону, чтобы вписаться в таймаут
            // headless-раннера: старый путь один реп ~десятки секунд).
            string[] modes = mode.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool Has(string m) => modes.Length == 0 || Array.IndexOf(modes, m) >= 0 || Array.IndexOf(modes, "all") >= 0;
            if (Has("old"))
                BenchOldFullExternal();
            if (Has("new"))
                BenchNewFullExternal();
            if (Has("shift"))
                BenchNewIncrementalShift();
            if (Has("seam"))
                CheckSeamlessVsChunks();

            GD.Print("[TWIN] DONE");
            return "";
        }
        catch (Exception ex)
        {
            string msg = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            GD.PrintErr("[TWIN] FAILED " + msg);
            return msg;
        }
    }

    private void Warmup()
    {
        // Прогрев JIT + ThreadStatic кэшей регионов — замеры устоявшегося кода.
        _ = UnifiedWorldGenerator.GenerateChunk(Seed, Ver, new ChunkKey(0, 0));
        var warm = new byte[64 * 64];
        MapRenderer.FillTerrainWindowExternal(warm, 64, Seed, Ver, 0, 0,
            UnifiedWorldGenerator.WorldOriginX, UnifiedWorldGenerator.WorldOriginY,
            0, 64, 0, 0, null);
        ulong acc = 0;
        for (int i = 0; i < 300; i++)
            acc += WorldCellQuery.CellAt(Seed, Ver, i, i).ElevationQ16;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GD.Print($"[TWIN] warmup ok acc={acc}");
    }

    // ---------------- (а) СТАРЫЙ путь: полная перегенерация окна ----------------

    private void BenchOldFullExternal()
    {
        const int Reps = 2;
        var buffer = new byte[W * W];
        for (int rep = 1; rep <= Reps; rep++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            ulong fp = 0;
            var sw = Stopwatch.StartNew();
            // Реплика старого RequestTerrainWindowUpdate: Parallel.For без капа
            // (дефолт = все ядра, как было в проде), каждая клетка — WorldCellQuery.CellAt.
            Parallel.For(0, W, y =>
            {
                long cy = BenchOriginY + y;
                int rowOffset = y * W;
                for (int x = 0; x < W; x++)
                {
                    var cell = WorldCellQuery.CellAt(Seed, Ver, BenchOriginX + x, cy);
                    byte type = cell.Terrain == BaseTerrainKind.Water ? (byte)2
                        : (cell.Biome == (byte)BiomeType.Desert ? (byte)1 : (byte)0);
                    buffer[rowOffset + x] = type;
                    fp += type;
                }
            });
            sw.Stop();

            GD.Print($"[TWIN] OLD_full_window_512 rep={rep} ms={sw.Elapsed.TotalMilliseconds:F1} fingerprint={fp}");
        }
    }

    // ---------------- (б) НОВЫЙ полный путь ----------------

    private void BenchNewFullExternal()
    {
        const int Reps = 3;
        var buffer = new byte[W * W];
        for (int rep = 1; rep <= Reps; rep++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var sw = Stopwatch.StartNew();
            MapRenderer.FillTerrainWindowExternal(buffer, W, Seed, Ver,
                BenchOriginX, BenchOriginY,
                UnifiedWorldGenerator.WorldOriginX, UnifiedWorldGenerator.WorldOriginY,
                0, W, 0, 0, null);
            sw.Stop();

            ulong fp = 0;
            for (int i = 0; i < buffer.Length; i++)
                fp += buffer[i];
            GD.Print($"[TWIN] NEW_full_window_512 rep={rep} ms={sw.Elapsed.TotalMilliseconds:F1} fingerprint={fp}");
        }
    }

    // ---------------- (в) НОВЫЙ инкрементальный сдвиг +64 ----------------

    private void BenchNewIncrementalShift()
    {
        var front = new byte[W * W];
        var back = new byte[W * W];
        long wox = UnifiedWorldGenerator.WorldOriginX;
        long woy = UnifiedWorldGenerator.WorldOriginY;

        // Исходное окно (ситуация «после телепорта»).
        MapRenderer.FillTerrainWindowExternal(front, W, Seed, Ver,
            BenchOriginX, BenchOriginY, wox, woy, 0, W, 0, 0, null);

        // 8 сдвигов по 64 = 512 тайлов пути (панорама lead 137 тайл/с ≈ 3.7с).
        const int Reps = 8;
        double shiftCopyMs = 0, bandFillMs = 0;
        long curX = BenchOriginX;
        for (int rep = 1; rep <= Reps; rep++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            curX += 64;
            var sw = Stopwatch.StartNew();
            // 1) Сдвиг буфера front→back (в проде — главный поток, ~BlockCopy по строкам).
            for (int y = 0; y < W; y++)
                Buffer.BlockCopy(front, y * W + 64, back, y * W, W - 64);
            sw.Stop();
            double copyMs = sw.Elapsed.TotalMilliseconds;
            shiftCopyMs += copyMs;

            // 2) Новая полоса 64×512 (в проде — фоновый таск с капом DOP).
            sw.Restart();
            MapRenderer.FillTerrainWindowExternal(back, W, Seed, Ver,
                curX, BenchOriginY, wox, woy, W - 64, W, 0, 0, null);
            sw.Stop();
            double bandMs = sw.Elapsed.TotalMilliseconds;
            bandFillMs += bandMs;

            // 3) front := back (в проде — копия в ApplyTerrainBuffer).
            Buffer.BlockCopy(back, 0, front, 0, W * W);

            GD.Print($"[TWIN] NEW_shift64 rep={rep} shiftCopy={copyMs:F2}ms bandFill={bandMs:F1}ms");
        }

        double avgBand = bandFillMs / Reps;
        GD.Print($"[TWIN] NEW_shift64 x{Reps} avg shiftCopy={shiftCopyMs / Reps:F2}ms avg bandFill={avgBand:F1}ms total={(shiftCopyMs + bandFillMs):F1}ms (против {Reps} ПОЛНЫХ пересчётов старого пути)");
    }

    // ---------------- (г) Бесшовность окно↔стриминг ----------------

    private void CheckSeamlessVsChunks()
    {
        var buffer = new byte[W * W];
        // Окно с локальным origin 0 (mapData=null → все клетки внешним путём):
        // клетки [0..128)² обязаны ПОБИТОВО совпасть с GenerateChunk — та же
        // решётка сетки климата (узлы кратны 16, origin кратен 64).
        MapRenderer.FillTerrainWindowExternal(buffer, W, Seed, Ver, 0, 0,
            UnifiedWorldGenerator.WorldOriginX, UnifiedWorldGenerator.WorldOriginY,
            0, W, 0, 0, null);

        int mismatches = 0;
        const int Chunk = 64;
        ulong fp = 0;
        for (int cy = 0; cy < 2; cy++)
        {
            for (int cx = 0; cx < 2; cx++)
            {
                var chunk = UnifiedWorldGenerator.GenerateChunk(Seed, Ver, new ChunkKey(cx, cy));
                var cells = chunk.Cells.Span;
                for (int y = 0; y < Chunk; y++)
                {
                    for (int x = 0; x < Chunk; x++)
                    {
                        ref readonly GeneratedCell c = ref cells[y * Chunk + x];
                        byte expected = c.Terrain == BaseTerrainKind.Water ? (byte)2
                            : (c.Biome == (byte)BiomeType.Desert ? (byte)1 : (byte)0);
                        byte actual = buffer[(cy * Chunk + y) * W + (cx * Chunk + x)];
                        fp += actual;
                        if (actual != expected)
                            mismatches++;
                    }
                }
            }
        }
        GD.Print($"[TWIN] seamless_vs_GenerateChunk mismatches={mismatches}/16384 fingerprint={fp} {(mismatches == 0 ? "OK: побитовая идентичность — 0 швов окно↔стриминг" : "FAIL: расхождения с чанками!")}");
    }
}
