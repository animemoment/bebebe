using System;

namespace Game.Core.WorldStreaming.Layers;

/// <summary>
/// L1 Temperature + L2 Precipitation. Чистые функции от абсолютных координат и
/// elevation (L0). Влага: затухание e^(−d/λ) от ближайшего океана против ветра
/// (запад→восток) + rain shadow за горой; с наветренной стороны — орография (+).
/// </summary>
public static class ClimateLayer
{
    /// <summary>L1: T = T_eq − polar·cos-like(широта) − lapse·max(0, elev−sea) + шум.</summary>
    public static ushort SampleTemperature(ulong seed, uint ver, long x, long y, ushort elevationQ16)
    {
        // Широта: |y| / LatitudeHalfSpan ∈ [0..1], полюс = 1. Косинусоидальный спад.
        double latAbs = Math.Min(1.0, Math.Abs((double)y) / WorldLayerParams.LatitudeHalfSpan);
        double latFactor = 0.5 * (1.0 + Math.Cos(Math.PI * latAbs)); // 1 на экваторе, 0 на полюсе
        int baseTemp = (int)(WorldLayerParams.EquatorTempQ16
            - (long)WorldLayerParams.PolarCoolingQ16
              * (long)(WorldLayerParams.Q16One * (1.0 - latFactor)) / WorldLayerParams.Q16One);

        // Лапс-рейт: похолодание с высотой выше уровня моря.
        int aboveSea = Math.Max(0, elevationQ16 - WorldLayerParams.SeaLevelQ16);
        int lapseDrop = (int)((long)aboveSea * WorldLayerParams.LapseRateQ16 / WorldLayerParams.Q16One);

        // Локальный шум ±Amp (GradientOctave ~[-1..1] → масштабируем).
        float noise = AbsNoise.GradientOctave(seed, ver, GenerationDomain.Temperature,
            x, y, 192f, lane: 7);
        int noiseDelta = (int)(noise * WorldLayerParams.TempNoiseAmpQ16);

        return WorldLayerParams.ClampQ16(baseTemp - lapseDrop + noiseDelta);
    }

    /// <summary>
    /// L2: осадки. Идём против ветра (на запад при W→E) шагом <paramref name="probeStep"/>
    /// до океана: d клеток → влага = exp(−d/λ). Если на пути встречаем гору (elev > Highland),
    /// применяем rain-shadow множитель (1 − strength) один раз (переваливание через главный хребет).
    /// Океан-близость с НАВЕТРЕННОЙ стороны (восток) даёт орографический + (подъём влажного воздуха).
    /// Финал: Mix(coastal·decay·shadow, inlandFloor, decay) + шум.
    /// </summary>
    public static ushort SamplePrecipitation(ulong seed, uint ver, long x, long y,
        Func<long, long, ushort> elevationAt, int probeStep = 8)
    {
        probeStep = Math.Max(1, probeStep);

        // Поиск океана против преобладающего ветра (WindDirX=1 → ветер дует с запада,
        // влага приходит с запада: идём на −x).
        int maxProbes = WorldLayerParams.OceanSearchRadius * 4 / probeStep; // запас на крупные заливы
        long distToOceanCells = -1;
        bool crossedMountainUpwind = false;

        for (int p = 1; p <= maxProbes; p++)
        {
            long px = checked(x - (long)p * probeStep * WorldLayerParams.WindDirX);
            long py = checked(y - (long)p * probeStep * WorldLayerParams.WindDirY);
            ushort pe = elevationAt(px, py);

            if (ReliefLayer.IsDeepOcean(pe))
            {
                distToOceanCells = (long)p * probeStep;
                break;
            }
            if (!crossedMountainUpwind && pe > WorldLayerParams.HighlandQ16)
                crossedMountainUpwind = true;
        }

        double moisture01;
        if (distToOceanCells < 0)
        {
            // Океан не найден в радиусе поиска — глубокая континентальнаяInterior.
            moisture01 = 0.0;
        }
        else
        {
            moisture01 = Math.Exp(-(double)distToOceanCells / WorldLayerParams.MoistureDecayLambda);
            if (crossedMountainUpwind)
                moisture01 *= 1.0 - WorldLayerParams.RainShadowStrength;
        }

        // Смешиваем прибрежную влажность с внутренним минимумом.
        int tQ16 = (int)(moisture01 * WorldLayerParams.Q16One);
        int precip = WorldLayerParams.MixQ16(WorldLayerParams.InlandPrecipFloorQ16,
            WorldLayerParams.CoastalPrecipQ16, tQ16);

        // Орографический плюс: близость к воде с НАВЕТРЕННОЙ стороны (восток, +x) —
        // влажный воздух поднимается у наветренного склона.
        if (!ReliefLayer.IsOcean(elevationAt(x, y)))
        {
            for (int p = 1; p <= 6; p++)
            {
                long dx = checked(x + (long)p * probeStep);
                if (ReliefLayer.IsOcean(elevationAt(dx, y)))
                {
                    precip += (int)((WorldLayerParams.Q16One - p * 10000L) * 3 / 10);
                    break;
                }
            }
        }

        // Фронтоновый шум осадков (конвекция).
        float noise = AbsNoise.GradientOctave(seed, ver, GenerationDomain.MoistureLarge,
            x, y, 128f, lane: 3);
        precip += (int)(noise * WorldLayerParams.PrecipNoiseAmpQ16);

        return WorldLayerParams.ClampQ16(precip);
    }
}
