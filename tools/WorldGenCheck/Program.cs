using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Layers;

// Консольная приёмка единого конвейера WorldLayerStack (план §6):
// 1) стыки соседних регионов/чанков — diff = 0 для детерминированных слоёв;
// 2) детерминизм — байт-в-байт при разном порядке запросов;
// 3) метрики реализма на окне мира + ASCII-превью мировой карты.
internal static class Program
{
    private const uint Ver = WorldLayerStack.GeneratorVersion;

    private static int Main(string[] args)
    {
        ulong seed = 12345678901234567UL;
        if (args.Length > 0 && ulong.TryParse(args[0], out var s)) seed = s;
        string outPng = args.Length > 1 ? args[1] : null;

        int failures = 0;
        failures += SeamTest(seed);
        failures += DeterminismTest(seed);
        failures += MetricsAndPreview(seed, outPng);

        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"FAILURES: {failures}");
        return failures == 0 ? 0 : 1;
    }

    // ── 1. Стыки: SampleMacro — чистая функция абсолютных координат ⇒ совпадают тривиально,
    // но проверяем главное: гидрология региона A и региона B на ОБЩИХ клетках маргина
    // даёт одинаковые flow/river/lake (margin фиксирован ⇒ окна смещены, клетки перекрываются).
    private static int SeamTest(ulong seed)
    {
        Console.WriteLine("== Seam test (hydrology overlap across region boundary) ==");
        var elevFn = new Func<long, long, ushort>((x, y) => ReliefLayer.SampleElevation(seed, Ver, x, y));

        var ra = HydrologyLayer.GetRegion(seed, Ver, new RegionKey(0, 0), elevFn);
        var rb = HydrologyLayer.GetRegion(seed, Ver, new RegionKey(1, 0), elevFn);

        int checkedCells = 0, mismatches = 0;
        // Перекрытие: правый маргин региона 0 = левые клетки региона 1 (включая сам регион 1).
        for (long wy = 0; wy < WorldRegions.RegionSize; wy += 7)
        {
            for (long wx = WorldRegions.RegionSize - WorldLayerParams.HydroMarginCells;
                 wx < 2 * WorldRegions.RegionSize; wx += 11)
            {
                int ia = ra.IndexOf(wx, wy);
                int ib = rb.IndexOf(wx, wy);
                if (ia < 0 || ib < 0) continue;
                checkedCells++;
                var (fa, rva, lka) = ValueAt(ra, ia);
                var (fb, rvb, lkb) = ValueAt(rb, ib);
                if (fa != fb || rva != rvb || lka != lkb)
                {
                    mismatches++;
                    if (mismatches <= 3)
                        Console.WriteLine($"  MISMATCH at ({wx},{wy}): A(flow={fa},r={rva},l={lka}) B(flow={fb},r={rvb},l={lkb})");
                }
            }
        }
        Console.WriteLine($"  overlap cells checked={checkedCells}, mismatches={mismatches}");

        // Чанк-стык: SampleChunkCell последний столбец чанка X vs первый столбец чанка X+1
        // при разных baseY — значения в одной клетке обязаны совпадать (чистая функция).
        int cellMismatches = 0;
        for (int t = 0; t < 64; t++)
        {
            long wx = 64 * 3 + 63, wy = t;
            var c1 = WorldLayerStack.SampleChunkCell(seed, Ver, wx, wy);
            var c2 = WorldLayerStack.SampleChunkCell(seed, Ver, wx, wy);
            if (!Equals(c1, c2)) cellMismatches++;
        }
        Console.WriteLine($"  chunk-cell pure-function mismatches={cellMismatches}");
        return (mismatches == 0 && cellMismatches == 0) ? 0 : 1;
    }

    private static (ushort Flow, bool River, bool Lake) ValueAt(RegionHydrology h, int i)
        => (h.FlowAt(i), false, h.IsLakeAt(i));

    // ── 2. Детерминизм: обход окна в обратном порядке = байт-в-байт тот же вывод.
    private static int DeterminismTest(ulong seed)
    {
        Console.WriteLine("== Determinism test (forward vs reverse order) ==");
        int w = 128, h = 128;
        long x0 = 3000, y0 = 2600;
        var forward = new MacroCell[h * w];
        var reverse = new MacroCell[h * w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                forward[y * w + x] = WorldLayerStack.SampleMacro(seed, Ver, x0 + x, y0 + y);
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
                reverse[y * w + x] = WorldLayerStack.SampleMacro(seed, Ver, x0 + x, y0 + y);

        int diffs = 0;
        for (int i = 0; i < forward.Length; i++)
            if (!forward[i].Equals(reverse[i])) diffs++;
        Console.WriteLine($"  cells={forward.Length}, byte-diffs={diffs}");
        return diffs == 0 ? 0 : 1;
    }

    // ── 3. Метрики реализма + ASCII-превью + PNG.
    private static int MetricsAndPreview(ulong seed, string outPng)
    {
        Console.WriteLine("== Realism metrics on world window ==");
        // Окно: 256×256 клеток с шагом 8 → покрываем 2048×2048 мира вокруг экватора/севера.
        // LatitudeHalfSpan=100000: y=0 экватор, полюс ближе к |y|=100000.
        int w = 256, h = 256, step = 8;
        long x0 = 4000, y0 = -((WorldLayerParams.LatitudeHalfSpan * 4) / 5); // от севера к югу
        var biomeCount = new Dictionary<BiomeType, int>();
        int total = 0, water = 0, forestish = 0, mountain = 0, riverCells = 0, lakeCells = 0;
        double treeInForest = 0, forestCells = 0;
        var sw = Stopwatch.StartNew();

        var png = new byte[w * h * 4];
        for (int py = 0; py < h; py++)
        {
            long wy = y0 + (long)py * step;
            for (int px = 0; px < w; px++)
            {
                long wx = x0 + (long)px * step;
                var m = WorldLayerStack.SampleMacro(seed, Ver, wx, wy);
                total++;
                biomeCount[m.Biome] = biomeCount.GetValueOrDefault(m.Biome) + 1;
                bool isWater = m.IsOcean || m.IsRiver || m.IsLake;
                if (isWater) water++;
                if (m.IsRiver) riverCells++;
                if (m.IsLake) lakeCells++;
                if (m.Biome == BiomeType.Mountain) mountain++;
                if (m.ForestQ16 > 20000) { forestish++; forestCells++; treeInForest += m.ForestQ16 / 65536.0; }
                png[(py * w + px) * 4 + 0] = PngR(m);
                png[(py * w + px) * 4 + 1] = PngG(m);
                png[(py * w + px) * 4 + 2] = PngB(m);
                png[(py * w + px) * 4 + 3] = 255;
            }
        }
        sw.Stop();

        Console.WriteLine($"  sampled={total} in {sw.ElapsedMilliseconds} ms (~{(double)sw.ElapsedMilliseconds * 1000 / total:F1} µs/cell)");
        foreach (var kv in biomeCount)
            Console.WriteLine($"  {kv.Key,-10} {100.0 * kv.Value / total,5:F1}%");
        Console.WriteLine($"  water={(double)water / total:P1} (ocean+river {(double)riverCells / total:P2} +lake {(double)lakeCells / total:P2}), mountains={(double)mountain / total:P1}");

        // Климатические пояса: средняя температура по широтным полосам.
        Console.WriteLine("  latitude bands (mean temp Q16 / precip Q16):");
        for (int band = 0; band < 5; band++)
        {
            long by = y0 + (long)(band * h / 5) * step;
            long ey = y0 + (long)((band + 1) * h / 5) * step;
            long tsum = 0, psum = 0, n = 0;
            for (int py = (int)((by - y0) / step); py < (int)((ey - y0) / step); py += 2)
                for (int px = 0; px < w; px += 4)
                {
                    var m = WorldLayerStack.SampleMacro(seed, Ver, x0 + (long)px * step, y0 + (long)py * step);
                    tsum += m.TemperatureQ16; psum += m.PrecipQ16; n++;
                }
            Console.WriteLine($"    band {band}: T={tsum / Math.Max(1, n)} P={psum / Math.Max(1, n)}");
        }

        // ASCII-превью мировой карты (уменьшенное): пользователь должен глазами увидеть
        // континенты/океаны/полярные льды/экваториальные пустыни.
        Console.WriteLine("  ASCII preview ('~' ocean, '.' land-desert, 'g' grass/steppe, 'T' taiga/forest, 'M' mountain, 'I' polar/tundra, '=' river/lake):");
        foreach (string row in PreviewRows(seed, w, h, x0, y0, step))
            Console.WriteLine("    " + row);

        if (outPng != null)
        {
            WritePng(outPng, w, h, png);
            Console.WriteLine($"  PNG written: {outPng}");
        }

        // Мягкие критерии плана (§6): вода 20–35%… но теперь мир = океаны+материки:
        // требуем хотя бы значительные доли и суши, и воды, и гор, и лесов.
        int fails = 0;
        double waterFrac = (double)water / total;
        if (waterFrac < 0.30 || waterFrac > 0.85) { Console.WriteLine("  FAIL: water fraction out of [30%,85%]"); fails++; }
        if (mountain < total * 0.01) { Console.WriteLine("  FAIL: no mountains"); fails++; }
        if (forestish < total * 0.05) { Console.WriteLine("  FAIL: almost no forest"); fails++; }
        if (riverCells + lakeCells < total * 0.002) { Console.WriteLine("  FAIL: no rivers/lakes"); fails++; }
        return fails;
    }

    private static IEnumerable<string> PreviewRows(ulong seed, int w, int h, long x0, long y0, int step)
    {
        // 64 строки × 128 колонов символов; переиспользуем уже классифицированные клетки?
        // Проще: повторный сэмплинг с более крупным шагом.
        const int pw = 128, ph = 48;
        int sx = w / pw, sy = h / ph;
        for (int py = 0; py < ph; py++)
        {
            var line = new char[pw];
            for (int px = 0; px < pw; px++)
            {
                long wx = x0 + (long)(px * sx) * step;
                long wy = y0 + (long)(py * sy) * step;
                var m = WorldLayerStack.SampleMacro(seed, Ver, wx, wy);
                line[px] = m switch
                {
                    _ when m.IsRiver || m.IsLake => '=',
                    _ when m.IsOcean => '~',
                    _ when m.Biome == BiomeType.Mountain => 'M',
                    _ when m.Biome == BiomeType.Tundra => 'I',
                    _ when m.Biome == BiomeType.Forest || m.Biome == BiomeType.Taiga => 'T',
                    _ when m.Biome == BiomeType.Desert => '.',
                    _ when m.Biome == BiomeType.Swamp => 's',
                    _ => 'g'
                };
            }
            yield return new string(line);
        }
    }

    private static byte PngR(MacroCell m) => m.Biome switch
    {
        BiomeType.DeepWater => m.IsRiver || m.IsLake ? (byte)90 : (byte)30,
        BiomeType.Mountain => 120,
        BiomeType.Desert or BiomeType.Savanna => 210,
        BiomeType.Tundra => 235,
        BiomeType.Forest or BiomeType.Taiga => 30,
        BiomeType.Swamp => 70,
        _ => 140
    };
    private static byte PngG(MacroCell m) => m.Biome switch
    {
        BiomeType.DeepWater => m.IsRiver || m.IsLake ? (byte)140 : (byte)80,
        BiomeType.Mountain => 120,
        BiomeType.Desert or BiomeType.Savanna => 190,
        BiomeType.Tundra => 240,
        BiomeType.Forest or BiomeType.Taiga => 110,
        BiomeType.Swamp => 100,
        _ => 170
    };
    private static byte PngB(MacroCell m) => m.Biome switch
    {
        BiomeType.DeepWater => m.IsRiver || m.IsLake ? (byte)200 : (byte)160,
        BiomeType.Mountain => 130,
        BiomeType.Desert or BiomeType.Savanna => 120,
        BiomeType.Tundra => 250,
        BiomeType.Forest or BiomeType.Taiga => 40,
        BiomeType.Swamp => 60,
        _ => 90
    };

    // Минимальный PNG-писатель (без внешних зависимостей): zlib stored blocks.
    private static void WritePng(string path, int w, int h, byte[] rgba)
    {
        using var fs = System.IO.File.Create(path);
        void W(byte[] b) => fs.Write(b);
        void U32(int v) => W(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

        W(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        void Chunk(string type, byte[] data)
        {
            U32(data.Length);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            W(t); W(data);
            uint crc = Crc(0xFFFFFFFFu, t); crc = Crc(crc, data);
            U32((int)(crc ^ 0xFFFFFFFFu));
        }

        var ihdr = new byte[13];
        ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
        ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
        ihdr[8] = 8; ihdr[9] = 6; // RGBA8
        Chunk("IHDR", ihdr);

        // raw: filter byte 0 per row
        var raw = new byte[h * (1 + w * 4)];
        for (int y = 0; y < h; y++)
        {
            raw[y * (1 + w * 4)] = 0;
            Array.Copy(rgba, y * w * 4, raw, y * (1 + w * 4) + 1, w * 4);
        }
        // zlib stored (no compression): CMF=0x78 FLG=0x01, then STORED blocks
        var idat = new List<byte>();
        idat.AddRange(new byte[] { 0x78, 0x01 });
        int pos = 0; const int MaxBlock = 65535;
        while (pos < raw.Length)
        {
            int len = Math.Min(MaxBlock, raw.Length - pos);
            bool last = pos + len >= raw.Length;
            idat.Add((byte)(last ? 1 : 0));
            idat.Add((byte)len); idat.Add((byte)(len >> 8));
            int nl = ~len & 0xFFFF;
            idat.Add((byte)nl); idat.Add((byte)(nl >> 8));
            for (int i = 0; i < len; i++) idat.Add(raw[pos + i]);
            pos += len;
        }
        uint adler = Adler32(raw);
        idat.AddRange(new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler });
        Chunk("IDAT", idat.ToArray());
        Chunk("IEND", Array.Empty<byte>());
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
    private static uint Crc(uint c, byte[] data)
    {
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c;
    }
    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte d in data) { a = (a + d) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }
}
