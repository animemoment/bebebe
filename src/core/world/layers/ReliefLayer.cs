using System;

namespace Game.Core.WorldStreaming.Layers;

/// <summary>
/// L0 Relief: domain-warped fBm (2 прохода warp) + ridged-хребты, абсолютные координаты.
/// Чистая функция (seed, ver, x, y) → elevation Q16. Никакого bbox-состояния (план §2.1).
/// </summary>
public static class ReliefLayer
{
    /// <summary>Высота клетки в Q16 (0..65535), детерминирована абсолютными координатами.</summary>
    public static ushort SampleElevation(ulong seed, uint ver, long x, long y)
    {
        // Domain warp: два независимых смещения координат (lane 0/1 одного домена Warp).
        float wx = AbsNoise.GradientOctave(seed, ver, GenerationDomain.ElevationMedium,
            x, y, WorldLayerParams.WarpScale, lane: 0);
        float wy = AbsNoise.GradientOctave(seed, ver, GenerationDomain.ElevationMedium,
            x, y, WorldLayerParams.WarpScale, lane: 1);

        long ax = unchecked(x + (long)(wx * WorldLayerParams.WarpStrength));
        long ay = unchecked(y + (long)(wy * WorldLayerParams.WarpStrength));

        float baseFbm = AbsNoise.Fbm(seed, ver, GenerationDomain.ElevationLarge,
            ax, ay, WorldLayerParams.ReliefScale, WorldLayerParams.ReliefOctaves);

        float ridge = AbsNoise.RidgedFbm(seed, ver, GenerationDomain.ElevationFine,
            x, y, WorldLayerParams.RidgeScale, 4);

        // Хребты поднимаем только там, где база уже суша/предгорья — иначе горы «в океане».
        float landGate = Math.Clamp((baseFbm - 0.52f) / 0.30f, 0f, 1f);
        float h = baseFbm * (1f - WorldLayerParams.RidgeWeight)
                + ridge * WorldLayerParams.RidgeWeight * landGate;

        return AbsNoise.ToQ16(h);
    }

    /// <summary>
    /// Архипелажная маска: низкочастотный шум → в «разреженных» зонах локальный уровень
    /// моря поднимается (берега размываются в цепи островов). Чистая функция координат.
    /// </summary>
    public static ushort LocalSeaLevel(ulong seed, uint ver, long x, long y)
    {
        float mask = AbsNoise.Fbm(seed, ver, GenerationDomain.ElevationMedium,
            x, y, WorldLayerParams.ArchipelagoMaskScale, 2);
        int maskQ16 = AbsNoise.ToQ16(mask);
        if (maskQ16 <= WorldLayerParams.ArchipelagoMaskLoQ16)
            return WorldLayerParams.SeaLevelQ16;
        int t = (int)((long)(Math.Min(maskQ16, WorldLayerParams.ArchipelagoMaskHiQ16)
            - WorldLayerParams.ArchipelagoMaskLoQ16) * WorldLayerParams.Q16One
            / (WorldLayerParams.ArchipelagoMaskHiQ16 - WorldLayerParams.ArchipelagoMaskLoQ16));
        return WorldLayerParams.ClampQ16(WorldLayerParams.SeaLevelQ16
            + (long)WorldLayerParams.ArchipelagoSeaBoostQ16 * t / WorldLayerParams.Q16One);
    }

    /// <summary>Океан ли клетка — с учётом локального (архипелажного) уровня моря.</summary>
    public static bool IsOceanAt(ulong seed, uint ver, long x, long y, ushort elevationQ16)
        => elevationQ16 < LocalSeaLevel(seed, ver, x, y);

    public static bool IsOcean(ushort elevationQ16)
        => elevationQ16 < WorldLayerParams.SeaLevelQ16;

    public static bool IsDeepOcean(ushort elevationQ16)
        => elevationQ16 < WorldLayerParams.OceanElevQ16;
}
