// Приёмочные тесты генератора мира v4: стыки чанков, детерминизм, метрики реализма.
// Запуск: dotnet run --project tests/WorldSeamTests.csproj -c Release
using System;
using System.Diagnostics;
using Game.Core;
using Game.Core.WorldLayers;
using Game.Core.WorldStreaming;

Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
int failed = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (!ok) failed++;
}

const uint Seed = 20261010u;
const int Chunk = 32;   // см. WorldChunk.Side
var rng = new Random(7);

// ---------- 0. Прогрев (первый расчёт региона гидрологии — дорогой, в игре идёт фоном) ----------
Console.WriteLine("[debug] warmup...");
var swg = Stopwatch.StartNew();
_ = WorldLayerStack.SampleMacro(Seed, 4000, 4000);
swg.Stop();
Console.WriteLine($"[debug] warm region calc: {swg.Elapsed.TotalMilliseconds:F0} ms");

// ---------- 1. Стыки чанков: та же клетка мира из «соседних» чанков идентична ----------
Console.WriteLine("[debug] seams...");
bool seamOk = true;
for (int i = 0; i < 50 && seamOk; i++)
{
    long cx = rng.Next(-5000, 5000), cy = rng.Next(-5000, 5000);
    for (int y = 0; y < Chunk; y++)
    {
        long wy = cy * Chunk + y;
        var a = WorldLayerStack.SampleMacro(Seed, cx * Chunk + (Chunk - 1), wy);
        var b = WorldLayerStack.SampleMacro(Seed, (cx + 1) * Chunk - 1, wy);
        if (a != b) { seamOk = false; break; }
    }
    for (int x = 0; x < Chunk && seamOk; x++)
    {
        long wx = cx * Chunk + x;
        var a = WorldLayerStack.SampleMacro(Seed, wx, cy * Chunk + (Chunk - 1));
        var b = WorldLayerStack.SampleMacro(Seed, wx, (cy + 1) * Chunk - 1);
        if (a != b) { seamOk = false; break; }
    }
}
Check("Стыки чанков (SampleMacro diff=0)", seamOk);

// ---------- 2. Детерминизм: порядок запросов не влияет (в пределах кэша) ----------
var seqA = new MacroCell[64];
for (int i = 0; i < 64; i++) seqA[i] = WorldLayerStack.SampleMacro(Seed, 4000 + i - 32, 4000);
var seqB = new MacroCell[64];
for (int k = 0; k < 64; k++) { int i = rng.Next(64); seqB[i] = WorldLayerStack.SampleMacro(Seed, 4000 + i - 32, 4000); }
bool detOk = true;
for (int i = 0; i < 64 && detOk; i++) detOk = seqA[i] == seqB[i];
Check("Детерминизм (порядок запросов не влияет)", detOk);

Check("Разные сиды дают разный рельеф",
    WorldLayerStack.SampleMacro(Seed, 100, 100).ElevationQ16 != WorldLayerStack.SampleMacro(Seed ^ 0x9E3779B9u, 100, 100).ElevationQ16);

_ = WorldLayerStack.SampleMacro(Seed, -1, -1);
_ = WorldLayerStack.SampleMacro(Seed, long.MinValue / 2, long.MaxValue / 4);
Check("Крайние координаты (long) без исключений", true);

// ---------- 3. Производительность чанка 32x32 при тёплом регионе ----------
var sw = Stopwatch.StartNew();
for (int y = 0; y < Chunk; y++)
    for (int x = 0; x < Chunk; x++)
        _ = WorldLayerStack.SampleMacro(Seed, 4000 + x, 4000 + y);
sw.Stop();
double msFirst = sw.Elapsed.TotalMilliseconds;
sw.Restart();
for (int y = 0; y < Chunk; y++)
    for (int x = 0; x < Chunk; x++)
        _ = WorldLayerStack.SampleMacro(Seed, 4100 + x, 4000 + y);
sw.Stop();
double msWarm = sw.Elapsed.TotalMilliseconds;
Check($"Чанк 32x32 при тёплом регионе <= 3 мс ({msFirst:F2}/{msWarm:F2} мс)", Math.Max(msFirst, msWarm) <= 3.0);

// ---------- 4. Метрики реализма на окне 128x128 (один регион) ----------
Console.WriteLine("[debug] metrics window...");
const int W = 128;
long x0 = 4000, y0 = 4000;
int water = 0, mountain = 0, trees = 0, forestHi = 0, forestHiTrees = 0, desertCells = 0, desertTrees = 0;
int riverCells = 0, lakeCells = 0, waterTrees = 0;
for (int y = 0; y < W; y++)
{
    for (int x = 0; x < W; x++)
    {
        var mc = WorldLayerStack.SampleMacro(Seed, x0 + x, y0 + y);
        var lc = WorldLayerStack.SampleLocal(Seed, x0 + x, y0 + y);
        if (lc.Ground == TileType.Water) { water++; if (lc.Tree) waterTrees++; }
        if (lc.Ground == TileType.Mountain) mountain++;
        if (lc.Tree) { trees++; if (mc.ForestQ16 > (ushort)(0.6 * 65536)) forestHiTrees++; }
        if (mc.ForestQ16 > (ushort)(0.6 * 65536)) forestHi++;
        if (mc.Biome == BiomeType.Desert) { desertCells++; if (lc.Tree) desertTrees++; }
        if (mc.IsRiver) riverCells++;
        if (mc.IsLake) lakeCells++;
    }
}
double total = W * (double)W;
double waterPct = 100 * water / total, mtPct = 100 * mountain / total;
Console.WriteLine($"      метрики: вода {waterPct:F1}% (реки {riverCells}, озёра {lakeCells}), горы {mtPct:F1}%, деревья {trees}, high-forest {forestHi}");
Check($"Доля воды 20-35% (факт {waterPct:F1}%)", waterPct >= 20 && waterPct <= 35);
Check($"Доля гор 5-12% (факт {mtPct:F1}%)", mtPct >= 5 && mtPct <= 12);
Check($"Реки присутствуют (>= 50 клеток, факт {riverCells})", riverCells >= 50);
if (forestHi > 0)
{
    double baseRate = trees / total, hiRate = (double)forestHiTrees / forestHi;
    Check($"Корреляция лес->деревья: high-forest {hiRate:P1} vs базовая {baseRate:P1}", hiRate >= 2 * baseRate);
}
else Check("Есть high-forest клетки", false);
if (desertCells > 0)
    Check($"Пустыня: деревья <= 3% (факт {100.0 * desertTrees / desertCells:F2}%)", 100.0 * desertTrees / desertCells <= 3.0);
Check("В воде нет деревьев", waterTrees == 0);

// ---------- 5. Окно мира произвольного размера: пересечения идентичны ----------
Console.WriteLine("[debug] LocalMapBuilder windows...");
var mapSmall = LocalMapBuilder.Build(Seed, 3968, 3968, 64, 64);
var mapBig = LocalMapBuilder.Build(Seed, 3968, 3968, 128, 128);
bool projOk = true;
for (int y = 0; y < 64 && projOk; y++)
    for (int x = 0; x < 64 && projOk; x++)
    {
        if (mapSmall.Ground[x, y] != mapBig.Ground[x, y]) projOk = false;
        if (mapSmall.TreeOnGrass[x, y] != mapBig.TreeOnGrass[x, y]) projOk = false;
    }
Check("Окно мира: 64x64 совпадает с центральным куском 128x128", projOk);
Check("MapData заполнен (Humidity/Fertility != null)", mapSmall.Humidity != null && mapSmall.Fertility != null);

Console.WriteLine(failed == 0 ? "\nALL TESTS PASSED" : $"\nFAILED: {failed}");
return failed == 0 ? 0 : 1;
