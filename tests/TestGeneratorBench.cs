using System;
using System.Diagnostics;
using System.IO;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;

namespace Game.Tests;

/// <summary>
/// Headless-бенчмарк генератора мира (dev-generator, PLAN §117, задача 1).
///
/// Замеряет:
///  (а) GenerateColonyWindow(seed, 3, 0, 0, N, N) для N=512 и N=1024 — мс;
///  (б) GenerateChunk × 100 чанков — мс (сумма и среднее);
///  (в) WorldCellQuery.CellAt × 10000 — мкс/вызов (row-major, паттерн MapRenderer);
///  (г) [после внедрения bulk-API] BeginBulkSample + массовый семпл 512×512
///      + проверка побитовой идентичности с GenerateChunk.
///
/// Каждый замер печатает контрольную сумму (детерминизм-фингерпринт):
/// одинаковые суммы ДО и ПОСЛЕ оптимизаций = значения генерации не изменились
/// (сид-инвариант/совместимость сохранений не нарушены).
///
/// Результаты также дозаписываются в tests/bench_results.txt построчно —
/// переживают таймаут/убийство процесса.
///
/// Режимы (driver .gd передаёт в RunBenchmarks):
///   all | windows | chunks | cellat | bulk
/// Run: godot --headless --path . --script tests/run_generator_bench.gd
/// </summary>
public partial class TestGeneratorBench : Node
{
    private const ulong Seed = 987654321ul;
    private const uint Ver = UnifiedWorldGenerator.DefaultGeneratorVersion; // 3

    // Живая сессия lead (§117.1): спавн в регионе [950, 637] — те же условия.
    private const long SpawnRegionX = 950;
    private const long SpawnRegionY = 637;

    private static string _resultsPath;

    public string RunBenchmarks(string mode = "all")
    {
        try
        {
            _resultsPath = ProjectSettings.GlobalizePath("res://tests/bench_results.txt");
            GD.Print($"[BENCH] env cores={System.Environment.ProcessorCount} serverGC={System.Runtime.GCSettings.IsServerGC} seed={Seed} ver={Ver} mode={mode}");
            Log($"[BENCH] env cores={System.Environment.ProcessorCount} serverGC={System.Runtime.GCSettings.IsServerGC} seed={Seed} ver={Ver} mode={mode}");

            UnifiedWorldGenerator.SetWorldOrigin(SpawnRegionX * WorldRegions.RegionSize, SpawnRegionY * WorldRegions.RegionSize);
            GD.Print($"[BENCH] worldOrigin=({UnifiedWorldGenerator.WorldOriginX},{UnifiedWorldGenerator.WorldOriginY})");

            WarmupJit();

            bool all = mode == "all";
            if (all || mode == "windows")
            {
                BenchColonyWindow(512, all ? 3 : 1);
                BenchColonyWindow(1024, all ? 3 : 1);
            }
            if (all || mode == "chunks")
                BenchChunks();
            if (all || mode == "cellat")
                BenchCellAt();
            if (all || mode == "micro")
                BenchMicro();
            if (all || mode == "bulk")
                BenchBulkSampler();

            GD.Print("[BENCH] DONE mode=" + mode);
            Log("[BENCH] DONE mode=" + mode);
            return "";
        }
        catch (Exception ex)
        {
            string msg = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            Log("[BENCH] FAILED " + msg);
            return msg;
        }
    }

    private static void Log(string line)
    {
        GD.Print(line);
        try
        {
            File.AppendAllText(_resultsPath, line + System.Environment.NewLine);
        }
        catch
        {
            // Файловый лог опционален — stdout первичен.
        }
    }

    private void WarmupJit()
    {
        // Прогрев JIT + ThreadStatic кэшей, чтобы замеры отражали устоявшийся код.
        _ = UnifiedWorldGenerator.GenerateColonyWindow(Seed, Ver, 0, 0, 64, 64);
        _ = UnifiedWorldGenerator.GenerateChunk(Seed, Ver, new ChunkKey(0, 0));

        ulong acc = 0;
        for (int i = 0; i < 200; i++)
            acc += (ulong)WorldCellQuery.CellAt(Seed, Ver, i, i).ElevationQ16;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Log($"[BENCH] warmup ok (acc={acc})");
    }

    // ---------------- (а) GenerateColonyWindow ----------------

    private void BenchColonyWindow(int size, int reps)
    {
        for (int rep = 1; rep <= reps; rep++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var sw = Stopwatch.StartNew();
            MapData data = UnifiedWorldGenerator.GenerateColonyWindow(Seed, Ver, 0, 0, size, size);
            sw.Stop();

            ulong fp = ColonyWindowFingerprint(data, size);
            Log($"[BENCH] colony_window size={size}x{size} rep={rep} ms={sw.Elapsed.TotalMilliseconds:F1} fingerprint={fp}");
        }
    }

    private static ulong ColonyWindowFingerprint(MapData data, int size)
    {
        // Сумма по фиксированному порядку обхода — детерминирована.
        ulong sum = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                sum += (ulong)((int)data.Ground[x, y] + data.Biome[x, y] + data.Variant[x, y] + data.TreeVariant[x, y]);
                if (data.TreeOnGrass[x, y]) sum += 1000ul;
                if (data.StoneOnGrass[x, y]) sum += 100ul;
            }
        }
        return sum;
    }

    // ---------------- (б) GenerateChunk × 100 ----------------

    private void BenchChunks()
    {
        const int Count = 100;
        var keys = new ChunkKey[Count];
        int k = 0;
        for (int cy = 0; cy < 10 && k < Count; cy++)
            for (int cx = 0; cx < 10 && k < Count; cx++)
                keys[k++] = new ChunkKey(cx, cy);

        const int Reps = 1;
        for (int rep = 1; rep <= Reps; rep++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var sw = Stopwatch.StartNew();
            ulong fp = 0;
            for (int i = 0; i < Count; i++)
            {
                WorldChunk chunk = UnifiedWorldGenerator.GenerateChunk(Seed, Ver, keys[i]);
                fp += ChunkFingerprint(chunk);
            }
            sw.Stop();

            Log($"[BENCH] chunks_100 rep={rep} total_ms={sw.Elapsed.TotalMilliseconds:F1} mean_ms={sw.Elapsed.TotalMilliseconds / Count:F2} fingerprint={fp}");
        }
    }

    private static ulong ChunkFingerprint(WorldChunk chunk)
    {
        var cells = chunk.Cells.Span;
        ulong sum = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            ref readonly GeneratedCell c = ref cells[i];
            sum += (ulong)(c.ElevationQ16 + c.MoistureQ16 + c.ForestQ16 + c.StoneQ16 + c.TemperatureQ16
                + (int)c.Terrain + c.Biome + c.Variant);
        }
        return sum;
    }

    // ---------------- (в) WorldCellQuery.CellAt × 10000 ----------------

    private void BenchCellAt()
    {
        const int Count = 10000;
        const int Reps = 3;

        for (int rep = 1; rep <= Reps; rep++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var sw = Stopwatch.StartNew();
            ulong fp = 0;
            for (int i = 0; i < Count; i++)
            {
                // Row-major последовательный обход полосы 512×N — паттерн MapRenderer.
                long x = i % 512;
                long y = i / 512;
                GeneratedCell c = WorldCellQuery.CellAt(Seed, Ver, x, y);
                fp += (ulong)(c.ElevationQ16 + c.MoistureQ16 + c.ForestQ16 + c.StoneQ16 + c.TemperatureQ16
                    + (int)c.Terrain + c.Biome + c.Variant);
            }
            sw.Stop();

            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / Count;
            Log($"[BENCH] cellat_10000 rep={rep} total_ms={sw.Elapsed.TotalMilliseconds:F1} us_per_call={us:F2} fingerprint={fp}");
        }
    }

    // ---------------- Профилирование фаз (micro) ----------------

    private void BenchMicro()
    {
        const int Count = 10000;
        long ox = UnifiedWorldGenerator.WorldOriginX;
        long oy = UnifiedWorldGenerator.WorldOriginY;

        // 1) SampleBlendedCell — климат полного пути CellAt (4 региона + BlendBilinear).
        {
            var sw = Stopwatch.StartNew();
            ulong fp = 0;
            for (int i = 0; i < Count; i++)
            {
                long x = ox + i % 512;
                long y = oy + i / 512;
                fp += WorldClimateGenerator.SampleBlendedCell(Seed, Ver, x, y).ElevationQ16;
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / Count;
            Log($"[BENCH] micro_sampleblended rep total_ms={sw.Elapsed.TotalMilliseconds:F1} us_per_call={us:F2} fingerprint={fp}");
        }

        // 2) SampleCell с фиксированным overrideClimate — ядро пути GenerateColonyWindow/чанков.
        {
            var climate = WorldClimateGenerator.SampleBlendedCell(Seed, Ver, ox + 256, oy + 256);
            var sw = Stopwatch.StartNew();
            ulong fp = 0;
            for (int i = 0; i < Count; i++)
            {
                long x = ox + i % 512;
                long y = oy + i / 512;
                GeneratedCell c = UnifiedWorldGenerator.SampleCell(Seed, Ver, x, y, climate);
                fp += (ulong)(c.ElevationQ16 + c.ForestQ16 + c.StoneQ16);
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / Count;
            Log($"[BENCH] micro_samplecell_override rep total_ms={sw.Elapsed.TotalMilliseconds:F1} us_per_call={us:F2} fingerprint={fp}");
        }

        // 3) Тройной LerpClimate1D — интерполяция климата клетки окна (как в окне/чанке).
        {
            var nw = WorldClimateGenerator.SampleBlendedCell(Seed, Ver, ox, oy);
            var ne = WorldClimateGenerator.SampleBlendedCell(Seed, Ver, ox + 16, oy);
            var swc = WorldClimateGenerator.SampleBlendedCell(Seed, Ver, ox, oy + 16);
            var se = WorldClimateGenerator.SampleBlendedCell(Seed, Ver, ox + 16, oy + 16);
            var sw = Stopwatch.StartNew();
            ulong fp = 0;
            for (int i = 0; i < Count; i++)
            {
                uint u = (uint)((i % 16) * 65535 / 16);
                uint v = (uint)(((i / 16) % 16) * 65535 / 16);
                var top = UnifiedWorldGenerator.LerpClimate1D(in nw, in ne, u);
                var bot = UnifiedWorldGenerator.LerpClimate1D(in swc, in se, u);
                var cell = UnifiedWorldGenerator.LerpClimate1D(in top, in bot, v);
                fp += (ulong)(cell.ElevationQ16 + cell.MoistureQ16);
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / Count;
            Log($"[BENCH] micro_lerp3 total_ms={sw.Elapsed.TotalMilliseconds:F1} us_per_call={us:F2} fingerprint={fp}");
        }

        // 4) Почвенный хвост окна: MapData + Humidity.Initialize + Fertility.Initialize (512²).
        {
            const int S = 512;
            var map = new MapData(S, S);
            var moist = new float[S, S];
            var height = new float[S, S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    // ~4% воды — реалистичная доля для старта колонии.
                    map.Ground[x, y] = ((x * 31 + y * 17) % 23 == 0) ? TileType.Water : TileType.Grass;
                    uint h = (uint)(x * 73856093 ^ y * 19349663);
                    moist[x, y] = (h & 0xFFFF) / 65535f;
                    height[x, y] = (h >> 16 & 0xFFFF) / 65535f;
                }

            var sw = Stopwatch.StartNew();
            map.Humidity.Initialize(map.Ground, moist, height);
            long humMs = (long)sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            map.Fertility.Initialize(map.Ground, moist, height, null, map.Humidity.WaterFeedAt);
            long fertMs = (long)sw.Elapsed.TotalMilliseconds;

            Log($"[BENCH] micro_soil_512 humidity_ms={humMs} fertility_ms={fertMs} (последовательно, входит в GenerateColonyWindow)");
        }
    }

    // ---------------- (г) Bulk-семплер: корректность + скорость ----------------

    private void BenchBulkSampler()
    {
        // 1) Побитовая идентичность с GenerateChunk на выровненном окне 64×64 (чанк 0,0):
        //    узлы сетки совпадают — значения обязаны совпасть все 4096.
        {
            var bulk = WorldCellQuery.BeginBulkSample(Seed, Ver, 0, 0, 64, 64);
            var chunk = UnifiedWorldGenerator.GenerateChunk(Seed, Ver, new ChunkKey(0, 0));
            int mismatches = 0;
            ulong fp = 0;
            for (int ly = 0; ly < 64; ly++)
            {
                for (int lx = 0; lx < 64; lx++)
                {
                    GeneratedCell a = bulk.CellAtLocal(lx, ly);
                    GeneratedCell b = chunk.GetCell(lx, ly);
                    if (a != b) mismatches++;
                    fp += (ulong)(a.ElevationQ16 + a.MoistureQ16 + a.ForestQ16 + a.StoneQ16 + a.TemperatureQ16
                        + (int)a.Terrain + a.Biome + a.Variant);
                }
            }
            Log($"[BENCH] bulk_vs_chunk_64 mismatches={mismatches} fingerprint={fp}");
            if (mismatches > 0)
                throw new InvalidOperationException($"BulkCellSampler != GenerateChunk: {mismatches} клеток из 4096.");
        }

        // 2) Совпадение с GenerateColonyWindow 512² (Ground/Biome/Variant по сетке 5×7).
        {
            var data = UnifiedWorldGenerator.GenerateColonyWindow(Seed, Ver, 0, 0, 512, 512);
            var bulk = WorldCellQuery.BeginBulkSample(Seed, Ver, 0, 0, 512, 512);
            int mismatches = 0;
            int checkedCells = 0;
            for (int ly = 0; ly < 512; ly += 7)
            {
                for (int lx = 0; lx < 512; lx += 5)
                {
                    GeneratedCell c = bulk.CellAtLocal(lx, ly);
                    TileType ground = c.Terrain switch
                    {
                        BaseTerrainKind.Water => TileType.Water,
                        BaseTerrainKind.Mountain => TileType.Mountain,
                        _ => TileType.Grass
                    };
                    if (ground != data.Ground[lx, ly] || c.Biome != data.Biome[lx, ly] || c.Variant != data.Variant[lx, ly])
                        mismatches++;
                    checkedCells++;
                }
            }
            Log($"[BENCH] bulk_vs_colony_window_512 mismatches={mismatches} checked={checkedCells}");
            if (mismatches > 0)
                throw new InvalidOperationException($"BulkCellSampler != GenerateColonyWindow: {mismatches} клеток из {checkedCells}.");
        }

        // 3) Скорость: BeginBulkSample(512²) + полный последовательный проход 512×512
        //    (сравнить с micro_samplecell_override и cellat_10000).
        for (int rep = 1; rep <= 3; rep++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var sw = Stopwatch.StartNew();
            var bulk = WorldCellQuery.BeginBulkSample(Seed, Ver, 0, 0, 512, 512);
            long beginMs = (long)sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            ulong acc = 0;
            for (int ly = 0; ly < 512; ly++)
            {
                for (int lx = 0; lx < 512; lx++)
                {
                    GeneratedCell c = bulk.CellAtLocal(lx, ly);
                    acc += (ulong)(c.ElevationQ16 + c.ForestQ16);
                }
            }
            sw.Stop();

            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / (512.0 * 512.0);
            Log($"[BENCH] bulk_512x512 rep={rep} begin_ms={beginMs} scan_ms={sw.Elapsed.TotalMilliseconds:F1} us_per_cell={us:F2} acc={acc}");
        }

        // 4) Fallback вне окна обязан совпадать с WorldCellQuery.CellAt (полный путь).
        {
            var bulk = WorldCellQuery.BeginBulkSample(Seed, Ver, 0, 0, 64, 64);
            GeneratedCell a = bulk.CellAt(999, 999);
            GeneratedCell b = WorldCellQuery.CellAt(Seed, Ver, 999, 999);
            GeneratedCell c = bulk.CellAt(-3, -7);
            GeneratedCell d = WorldCellQuery.CellAt(Seed, Ver, -3, -7);
            bool ok = a == b && c == d;
            Log($"[BENCH] bulk_outside_matches_cellat={ok}");
            if (!ok)
                throw new InvalidOperationException("BulkCellSampler fallback вне окна != WorldCellQuery.CellAt.");
        }
    }
}
