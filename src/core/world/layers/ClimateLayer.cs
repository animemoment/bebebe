using System;

namespace Game.Core.WorldLayers;

/// <summary>
/// L1+L2: климат в абсолютных координатах.
/// Температура = широтный косинус − lapse rate по высоте + шум.
/// Осадки = влага от океана (e^(-d/λ) до ближайшей «океанской» клетки против ветра
/// и вокруг) + rain shadow за горами + шум. Всё — чистые функции (seed, x, y).
/// </summary>
public static class ClimateLayer
{
    private const float SeaLevel01 = WorldLayerParams.SeaLevel;

    /// <summary>L1: температура Q16 (0..65535).</summary>
    public static ushort TemperatureQ16(uint seed, long wx, long wy)
    {
        // Широта: мир 1024 региона × 512 = 524288 клеток по Y, экватор в центре.
        double lat = Math.Clamp((wy - 262144.0) / 262144.0, -1.0, 1.0); // −1..1
        double latFactor = Math.Cos(lat * Math.PI / 2.0);               // 1 на экваторе, 0 на полюсе

        int temp = WorldLayerParams.EquatorTempQ16
            - (int)(WorldLayerParams.PolarCoolingQ16 * (1.0 - latFactor));

        // Lapse rate: холоднее над морем
        float e = ReliefLayer.Elevation(seed, wx, wy);
        if (e > SeaLevel01)
            temp -= (int)((e - SeaLevel01) / (1f - SeaLevel01) * WorldLayerParams.LapseRatePerUnit);

        // Шум ±
        float n = WorldNoise.FbmAt(seed, WorldLayerParams.DomTempNoise, wx, wy,
            WorldLayerParams.TempNoiseScale, 3);
        temp += (int)((n - 0.5f) * 2f * WorldLayerParams.TempNoiseAmp);

        return WorldLayerParams.ClampQ16(temp);
    }

    /// <summary>Океанская ли клетка (для источников влаги)? Морская — по рельефу.</summary>
    private static bool IsOcean(uint seed, long wx, long wy)
        => ReliefLayer.Elevation(seed, wx, wy) < SeaLevel01;

    /// <summary>
    /// Блеск влаги: поиск ближайшей океанской клетки в конусе против ветра (запад)
    /// и вокруг; расстояние → moisture = exp(−d/λ). Ищем по разреженной решётке
    /// OceanFieldScale — детерминированно и независимо от окна.
    /// </summary>
    public static double MoistureFromOcean(uint seed, long wx, long wy)
    {
        // Если сами в океане — максимальная влага.
        if (IsOcean(seed, wx, wy)) return 1.0;

        float cell = WorldLayerParams.OceanFieldScale;
        long cx = FloorDiv(wx, (long)cell);
        long cy = FloorDiv(wy, (long)cell);

        double best = 0.0;
        const int R = 6; // радиус поиска в ячейках решётки (≈ 6·384 = 2300 клеток)
        for (long oy = -R; oy <= R; oy++)
        {
            for (long ox = -R; ox <= R; ox++)
            {
                // Центр ячейки-кандидата; берём хеш-точку внутри неё как «представителя».
                long ax = cx + ox, ay = cy + oy;
                uint h = WorldNoise.HashNode(seed, WorldLayerParams.DomOcean, (int)ax, (int)ay);
                long px = ax * (long)cell + (long)(h & 0xFFFF) % (long)cell;
                long py = ay * (long)cell + (long)((h >> 16) & 0xFFFF) % (long)cell;
                if (!IsOcean(seed, px, py)) continue;

                double dx = px - wx, dy = py - wy;
                // Ветер с запада: восточнее источника штраф (влага уже выпала).
                double downwind = dx > 0 ? dx : 0.0;
                double d = Math.Sqrt(dx * dx + dy * dy) + downwind * 0.5;
                double m = Math.Exp(-d / WorldLayerParams.MoistDecayLambda);
                if (m > best) best = m;
            }
        }
        return best;
    }

    /// <summary>Rain shadow: насколько местность «за горой» против ветра (ветер с запада → смотрим на запад).</summary>
    private static double UpwindRidgeDrop(uint seed, long wx, long wy)
    {
        float e = ReliefLayer.Elevation(seed, wx, wy);
        double maxDrop = 0.0;
        const int steps = 6;
        float dist = WorldLayerParams.ShadowLookDist;
        for (int i = 1; i <= steps; i++)
        {
            long sx = wx - (long)(dist * i / steps); // ветер с запада: подветренная сторона — восток
            long sy = wy;
            float se = ReliefLayer.Elevation(seed, sx, sy);
            if (se > e)
            {
                double drop = (se - e) * 2.5; // 0.4 высоты над нами ≈ полный дождевой экран
                if (drop > maxDrop) maxDrop = drop;
            }
        }
        return Math.Min(1.0, maxDrop);
    }

    /// <summary>L2: осадки Q16.</summary>
    public static ushort PrecipitationQ16(uint seed, long wx, long wy)
    {
        double oceanM = MoistureFromOcean(seed, wx, wy);
        int precip = WorldLayerParams.PrecipBaseQ16
            + (int)(oceanM * WorldLayerParams.PrecipOceanBonusQ16);

        double shadow = UpwindRidgeDrop(seed, wx, wy);
        precip = (int)(precip * (1.0 - WorldLayerParams.RainShadowStrength * shadow));

        float n = WorldNoise.FbmAt(seed, WorldLayerParams.DomPrecipNoise, wx, wy,
            WorldLayerParams.PrecipNoiseScale, 3);
        precip += (int)((n - 0.5f) * 2f * WorldLayerParams.PrecipNoiseAmp);

        return WorldLayerParams.ClampQ16(precip);
    }

    internal static long FloorDiv(long a, long b)
    {
        long q = a / b, r = a % b;
        if (r != 0 && ((r < 0) != (b < 0))) q--;
        return q;
    }
}
