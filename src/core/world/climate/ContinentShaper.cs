using System;
using Game.Core.WorldStreaming;

namespace Game.Core.WorldClimate;

/// <summary>
/// Форма мира: континенты, архипелаги, моря и океаны.
///
/// Порядок построения одного региона:
///   1) domain-warp — координаты смещаются гладким шумом (бухты, фиорды, полуострова,
///      а не «пузыри»);
///   2) fBm континентальной высоты по сдвинутым координатам — массивы суши;
///   3) тектонические плиты (Voronoi) — щиты/впадины + складчатые пояса на границах;
///   4) островные дуги и архипелаги вдоль океанских границ плит;
///   5) мелкая детализация берега.
///
/// Функция ЧИСТАЯ и детерминированная: результат зависит только от
/// (params, seed, generatorVersion, regionX, regionY) — никакого состояния,
/// любого порядка вызовов и потоков. Соседние регионы считаются независимо,
/// поэтому бесшовность обеспечивается непрерывностью самих полей (шум + Voronoi),
/// а не порядком генерации.
/// </summary>
public static class ContinentShaper
{
    /// <summary>Верхняя граница числа плит (PlateId в байте).</summary>
    public const int MaxPlates = 64;

    /// <summary>
    /// Считает рельеф/плиты в одной точке (единица — РЕГИОН).
    /// </summary>
    public static ContinentSample Shape(in WorldClimateParams p, ulong seed, uint ver,
        long regionX, long regionY)
    {
        // --- 1. Органический вихревой Domain-Warp (плавные заливы и берега без самопересекающихся петель) ---
        int warpScale = 96;
        int warpAmp = 18 + (p.CoastWarp / 256) * 12 / 256;                  // 18..30 регионов (контролируемая амплитуда)
        ushort warpX = CoordinateHash.ValueNoiseQ16(seed, ver,
            GenerationDomain.ContinentWarp, regionX, regionY, warpScale);
        ushort warpY = CoordinateHash.ValueNoiseQ16(seed, ver,
            GenerationDomain.ContinentWarp, regionX, regionY, warpScale + 17);
        long sx = regionX + (int)((long)(warpX - Q16.Half) * warpAmp / Q16.Half);
        long sy = regionY + (int)((long)(warpY - Q16.Half) * warpAmp / Q16.Half);

        // --- 2. Органический многооктавный fBm континентов (Civ VI / RimWorld) ---
        int baseScale = Math.Max(32, (int)p.ContinentScale);
        int octaves = 4 + p.Roughness / 2;                     // 5..6
        ushort baseHeight = Fbm(seed, ver, GenerationDomain.ContinentBase, sx, sy, baseScale, octaves);

        // Плавный региональный геологический сдвиг:
        int plateLattice = PlateLatticeSize(p.PlateCount);
        (int plateId, ushort boundary, float compression, long d1, long d2) = Voronoi(seed, ver, sx, sy, plateLattice);

        ushort plateNoise = CoordinateHash.ValueNoiseQ16(seed, ver, GenerationDomain.ContinentPlate, sx, sy, plateLattice / 2);
        int regionalShift = (int)(((long)plateNoise - Q16.Half) * 6000 / Q16.Half);

        int rawElevation = (int)baseHeight + regionalShift;

        // --- 3. Орогенез на границах плит (Широкие горные хребты и предгорья) ------------------------------------
        int seaLevel = p.SeaLevel;
        seaLevel = Math.Clamp(seaLevel, 28000, 36000);

        if (boundary > 0)
        {
            if (rawElevation >= seaLevel && compression > 0.10f)
            {
                // На суше при коллизии: широкие складчатые пояса (Альпы, Анды, Кордильеры)
                // 1. Осевой хребет (Ridged multifractal spine)
                ushort ridgeMacro = CoordinateHash.ValueNoiseQ16(seed + 333ul, ver, GenerationDomain.ContinentDetail, sx, sy, 28);
                int ridgeDist = Math.Abs((int)ridgeMacro - Q16.Half);
                int ridgeSpine = Math.Max(0, Q16.Half - ridgeDist) * 2; // 0..One, пик по осевой линии хребта

                // 2. Детализация отрогов, ущелий и перевалов
                ushort ridgeDetail = CoordinateHash.ValueNoiseQ16(seed + 444ul, ver, GenerationDomain.ContinentDetail, sx, sy, 12);
                float ridgeMod = 0.55f + (float)ridgeDetail / Q16.One * 0.45f;

                // 3. Плавный подъем: широкое предгорье + высокий скалистый хребет
                float broadLift = (float)boundary / Q16.One;
                float spineFactor = broadLift * (0.35f + 0.65f * ((float)ridgeSpine / Q16.One));
                int mountainLift = (int)(spineFactor * p.OrogenyStrength * (compression * 1.6f * ridgeMod));
                rawElevation += mountainLift;
            }
            else if (rawElevation < seaLevel)
            {
                // В океане: срединно-океанический хребет ВСЕГДА удерживается глубоко под водой!
                int underwaterRidge = (int)((long)boundary * 1800 / Q16.One);
                rawElevation = Math.Min(seaLevel - 4000, rawElevation + underwaterRidge);
            }
        }

        // Островные дуги и извилистые архипелаги (Island Arcs & Archipelagos):
        bool arcZone = (boundary > 0 && compression > 0.20f && rawElevation < seaLevel)
                    || (p.IslandArcDensity > 12000 && rawElevation < seaLevel && boundary > 0);

        if (arcZone)
        {
            // 1. Извилистый направленный ворп вдоль тектонической границы:
            ushort arcWarpX = CoordinateHash.ValueNoiseQ16(seed + 8881ul, ver, GenerationDomain.ContinentWarp, sx, sy, 24);
            ushort arcWarpY = CoordinateHash.ValueNoiseQ16(seed + 8882ul, ver, GenerationDomain.ContinentWarp, sx + 23, sy, 24);
            long asx = sx + ((int)arcWarpX - Q16.Half) * 8 / Q16.Half;
            long asy = sy + ((int)arcWarpY - Q16.Half) * 8 / Q16.Half;

            // 2. Риджевый гребень дуги:
            ushort arcNoise = CoordinateHash.ValueNoiseQ16(seed + 999ul, ver, GenerationDomain.ContinentArc, asx, asy, 32);
            int ridgeDist = Math.Abs((int)arcNoise - Q16.Half);
            int arcRidge = Math.Max(0, Q16.Half - ridgeDist) * 2;

            // 3. Сегментация на отдельные острова с проливами:
            ushort breakNoise = CoordinateHash.ValueNoiseQ16(seed + 7771ul, ver, GenerationDomain.ContinentArc, asx, asy, 14);
            ushort coastFret = CoordinateHash.ValueNoiseQ16(seed + 5551ul, ver, GenerationDomain.ContinentDetail, asx, asy, 8);

            int islandStrength = (int)((long)arcRidge * breakNoise / Q16.One);
            islandStrength = (int)Q16.Weighted((ushort)islandStrength, 75, coastFret, 25);

            const int islandThreshold = 46000;
            if (islandStrength > islandThreshold)
            {
                int lift = (int)((islandStrength - islandThreshold) * 1.5f);
                rawElevation = seaLevel + lift;
            }
        }

        // Изолированные океанические архипелаги (Hotspot Island Chains):
        if (rawElevation < seaLevel - 3000 && p.IslandArcDensity > 18000)
        {
            ushort hotWarpX = CoordinateHash.ValueNoiseQ16(seed + 3331ul, ver, GenerationDomain.ContinentWarp, sx, sy, 36);
            ushort hotWarpY = CoordinateHash.ValueNoiseQ16(seed + 3332ul, ver, GenerationDomain.ContinentWarp, sx + 17, sy, 36);
            long hsx = sx + ((int)hotWarpX - Q16.Half) * 6 / Q16.Half;
            long hsy = sy + ((int)hotWarpY - Q16.Half) * 6 / Q16.Half;

            ushort hotRidgeRaw = CoordinateHash.ValueNoiseQ16(seed + 4441ul, ver, GenerationDomain.ContinentArc, hsx, hsy, 32);
            int hotDist = Math.Abs((int)hotRidgeRaw - Q16.Half);
            int hotRidge = Math.Max(0, Q16.Half - hotDist) * 2;

            ushort hotBreak = CoordinateHash.ValueNoiseQ16(seed + 4442ul, ver, GenerationDomain.ContinentArc, hsx, hsy, 12);
            int hotIsland = (int)((long)hotRidge * hotBreak / Q16.One);

            if (hotIsland > 52000)
            {
                int lift = (int)((hotIsland - 52000) * 1.3f);
                rawElevation = seaLevel + lift;
            }
        }

        // Мелкая береговая деталь
        int detailScale = Math.Max(6, baseScale / 10);
        ushort detail = Fbm(seed, ver, GenerationDomain.ContinentDetail, sx, sy, detailScale, 2);
        rawElevation = (int)Q16.Weighted((ushort)Math.Clamp(rawElevation, 0, Q16.One), 88, detail, 12);

        ushort elevation = (ushort)Math.Clamp(rawElevation, 0, Q16.One);

        // --- 4. Уровень моря (бимодальное разделение: 70% океан, 30% суша) ----

        ushort continental;
        ushort landElevation;
        ushort oceanDepth;
        ushort coastDistance;

        if (elevation >= seaLevel)
        {
            ushort inland = Q16.InverseLerp(seaLevel, Q16.One, elevation);
            float h = inland / (float)Q16.One; // 0..1

            // Земная гипсографическая кривая суши:
            // >60% площади суши — пологие равнины и низменности (0..400м)
            // Горы занимают строго 6..10% суши
            float topo;
            if (h < 0.65f)
                topo = h * (0.35f / 0.65f); // 0..0.35 (равнины)
            else if (h < 0.88f)
                topo = 0.35f + (h - 0.65f) * (0.20f / 0.23f); // 0.35..0.55 (холмы)
            else
                topo = 0.55f + (h - 0.88f) * (0.45f / 0.12f); // 0.55..1.00 (высокогорья)

            landElevation = (ushort)Math.Clamp((int)(topo * Q16.One), 0, Q16.One);
            continental = (ushort)Math.Min(Q16.One, 28000 + (long)inland * (Q16.One - 28000) / Q16.One);
            oceanDepth = 0;
            coastDistance = Q16.SmoothStep(inland);
        }
        else
        {
            ushort depth = Q16.InverseLerp(0, seaLevel, elevation);
            // Плавная глубина океана: depth = 0 (дно океана) .. One (кромка воды)
            // oceanD = 0 (кромка воды) .. One (глубокий океан)
            ushort oceanD = (ushort)(Q16.One - depth);

            // Континентальный шельф: пологий спуск у берега (d^2) вместо резкого обрыва
            float normD = oceanD / (float)Q16.One;
            float shelfD = normD * normD;
            oceanD = (ushort)Math.Clamp((int)(shelfD * Q16.One), 0, Q16.One);

            oceanDepth = oceanD;
            continental = (ushort)Math.Max(0L, 24000L - (long)oceanD * 24000L / Q16.One);
            landElevation = 0;
            coastDistance = 0;
        }

        return new ContinentSample(continental, landElevation, oceanDepth, coastDistance,
            boundary, (byte)plateId);
    }

    /// <summary>Размер решётки плит в регионах: мир 2048 x 1024, плит — PlateCount.</summary>
    public static int PlateLatticeSize(int plateCount)
    {
        int plates = Math.Clamp(plateCount, 2, MaxPlates);
        int side = (int)Math.Ceiling(Math.Sqrt(plates));
        return Math.Max(64, 2048 / Math.Max(1, side));
    }

    /// <summary>
    /// Ближайший и второй ближайший сид плиты (Voronoi).
    /// Вычисляет узкую границу шва (1.5..3 региона) и кинематическое сжатие.
    /// </summary>
    private static (int PlateId, ushort Boundary, float Compression, long D1, long D2) Voronoi(
        ulong seed, uint ver, long x, long y, int lattice)
    {
        long lx = FloorDiv(x, lattice);
        long ly = FloorDiv(y, lattice);
        long best = long.MaxValue;
        long second = long.MaxValue;
        int bestId = 0;
        int secondId = 0;
        long px1 = 0, py1 = 0;
        long px2 = 0, py2 = 0;

        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                long cx = lx + ox;
                long cy = ly + oy;
                ulong h = CoordinateHash.Hash(seed, ver, GenerationDomain.ContinentPlate, cx, cy);
                long px = cx * lattice + (long)(h % (ulong)lattice);
                long py = cy * lattice + (long)((h >> 24) % (ulong)lattice);
                long dx = px - x;
                long dy = py - y;
                long d = dx * dx + dy * dy;

                if (d < best)
                {
                    second = best;
                    secondId = bestId;
                    px2 = px1;
                    py2 = py1;

                    best = d;
                    bestId = (int)((h >> 40) % (ulong)MaxPlates);
                    px1 = px;
                    py1 = py;
                }
                else if (d < second)
                {
                    second = d;
                    secondId = (int)((h >> 40) % (ulong)MaxPlates);
                    px2 = px;
                    py2 = py;
                }
            }
        }

        if (second == long.MaxValue) { second = best; secondId = bestId; px2 = px1; py2 = py1; }
        long near = (long)Math.Sqrt(best);
        long far = (long)Math.Sqrt(second);
        long gap = far - near;

        // Зона коллизии плит — широкий орогенный пояс (25..50 регионов) с плавным спадом
        int beltWidth = Math.Max(24, lattice / 8);
        ushort boundary;
        if (gap >= beltWidth)
        {
            boundary = 0;
        }
        else
        {
            float t = (float)gap / beltWidth;
            float s = 1f - (t * t * (3f - 2f * t)); // smoothstep fade out
            boundary = (ushort)Math.Clamp((int)(s * Q16.One), 0, Q16.One);
        }

        // Кинематика плит: вычисление сжатия / расхождения
        float compression = 0f;
        if (boundary > 0)
        {
            ulong hA = CoordinateHash.Hash(seed, ver, GenerationDomain.ContinentPlate, (long)bestId, 0x1111L);
            ulong hB = CoordinateHash.Hash(seed, ver, GenerationDomain.ContinentPlate, (long)secondId, 0x2222L);
            float aA = (float)((hA % 360) * Math.PI / 180.0);
            float aB = (float)((hB % 360) * Math.PI / 180.0);
            float vxA = MathF.Cos(aA), vyA = MathF.Sin(aA);
            float vxB = MathF.Cos(aB), vyB = MathF.Sin(aB);
            float rvx = vxA - vxB, rvy = vyA - vyB;
            float nx = (float)(px2 - px1), ny = (float)(py2 - py1);
            float len = MathF.Sqrt(nx * nx + ny * ny);
            compression = len > 0.001f ? (rvx * nx + rvy * ny) / len : 0f;
        }

        return (bestId, boundary, compression, best, second);
    }

    /// <summary>Фрактальный шум: октавы с удвоением частоты и спадающей амплитудой (1/f persistence).</summary>
    internal static ushort Fbm(ulong seed, uint ver, GenerationDomain domain,
        long x, long y, int scale, int octaves)
    {
        long acc = 0;
        long weightSum = 0;
        int s = Math.Max(2, scale);
        int oct = Math.Clamp(octaves, 1, 8);

        for (int o = 0; o < oct; o++)
        {
            ushort n = CoordinateHash.ValueNoiseQ16(seed, ver, domain, x, y, s);
            long amp = 1L << (oct - 1 - o);
            acc += (long)n * amp;
            weightSum += amp;
            s = s >> 1;
            if (s <= 2) break;
        }

        return (ushort)(acc / Math.Max(1, weightSum));
    }

    private static long FloorDiv(long value, int divisor)
    {
        long q = value / divisor;
        long r = value % divisor;
        return r < 0 ? q - 1 : q;
    }
}