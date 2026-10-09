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
        float landGate = Math.Clamp((baseFbm - 0.35f) / 0.35f, 0f, 1f);
        float h = baseFbm * (1f - WorldLayerParams.RidgeWeight)
                + ridge * WorldLayerParams.RidgeWeight * landGate;

        return AbsNoise.ToQ16(h);
    }

    public static bool IsOcean(ushort elevationQ16)
        => elevationQ16 < WorldLayerParams.SeaLevelQ16;

    public static bool IsDeepOcean(ushort elevationQ16)
        => elevationQ16 < WorldLayerParams.OceanElevQ16;
}
