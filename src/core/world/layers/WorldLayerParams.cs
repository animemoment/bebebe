using System;

namespace Game.Core.WorldStreaming.Layers;

/// <summary>
/// Единая таблица параметров многослойной генерации мира (план §2.2).
/// Все слои WorldLayerStack читают значения только отсюда — никакого bbox-состояния.
/// Значения фиксированы константами: результат зависит ИСКЛЮЧИТЕЛЬНО от
/// (worldSeed, generatorVersion, абсолютные координаты) — детерминизм и бесшовность.
/// Изменение любого параметра требует повышения GeneratorVersion (см. WorldLayerStack.GeneratorVersion).
/// </summary>
public static class WorldLayerParams
{
    // ===== Группа 1: сид и версия =====
    // worldSeed / generatorVersion приходят аргументами в каждую чистую функцию слоя.

    // ===== Группа 2: рельеф (L0) =====
    /// <summary>Масштаб крупных форм (континенты), клеток на ячейку решётки шума.</summary>
    public const float ReliefScale = 768f;
    /// <summary>Детализация fBm рельефа (октавы).</summary>
    public const int ReliefOctaves = 5;
    /// <summary>
    /// Масштаб маски архипелагов (низкочастотный шум): области с малым значением —
    /// «разреженные острова» (вода выше обычного порога берегов). 0 — выключено.
    /// </summary>
    public const float ArchipelagoMaskScale = 192f;
    /// <summary>Сила поднятия локального уровня моря в архипелажных зонах (Q16).</summary>
    public const ushort ArchipelagoSeaBoostQ16 = 9000;
    /// <summary>Нижняя граница маски архипелага (Q16-порог noise→boost линейный).</summary>
    public const ushort ArchipelagoMaskLoQ16 = 20000;
    /// <summary>Верхняя граница маски (выше — полный boost).</summary>
    public const ushort ArchipelagoMaskHiQ16 = 40000;
    /// <summary>Доля ridged-хребтов в рельефе (0..1).</summary>
    public const float RidgeWeight = 0.30f;
    /// <summary>Масштаб ridged-хребтов.</summary>
    public const float RidgeScale = 160f;
    /// <summary>Сила domain-warp (извилистость берегов/хребтов), клеток.</summary>
    public const float WarpStrength = 72f;
    /// <summary>Масштаб warp-шума.</summary>
    public const float WarpScale = 256f;
    /// <summary>Порог воды по высоте (Q16): ниже — Water/Ocean. ~55% мира — океаны/моря.</summary>
    public const ushort SeaLevelQ16 = 30000;
    /// <summary>Верхний порог «предгорий».</summary>
    public const ushort HighlandQ16 = 42000;
    /// <summary>Порог гор по высоте (Q16), L7 переопределение Mountain.</summary>
    public const ushort MountainElevHiQ16 = 49000;

    // ===== Группа 3: климат (L1/L2) =====
    /// <summary>Базовая температура экватора (Q16).</summary>
    public const ushort EquatorTempQ16 = 52000;
    /// <summary>Падение температуры от экватора к полюсу (Q16).</summary>
    public const ushort PolarCoolingQ16 = 46000;
    /// <summary>Полуширота мира (клеток), за которой |lat| = 1 (полюс).</summary>
    public const long LatitudeHalfSpan = 100_000;
    /// <summary>Лапс-рейт: падение температуры с высотой выше уровня моря (Q16 на Q16 превышения).</summary>
    public const int LapseRateQ16 = 22000;
    /// <summary>Амплитуда температурного шума (Q16).</summary>
    public const ushort TempNoiseAmpQ16 = 7000;
    /// <summary>Направление преобладающего ветра: запад → восток (совпадает с HumidityMap.WindDirX).</summary>
    public const int WindDirX = 1;
    public const int WindDirY = 0;
    /// <summary>Затухание влаги вглубь суши, клеток (e^-d/λ).</summary>
    public const float MoistureDecayLambda = 900f;
    /// <summary>Радиус поиска океана для «distance to ocean» (клеток).</summary>
    public const int OceanSearchRadius = 24;
    /// <summary>Сила rain shadow: множитель осадков за горой (0..1).</summary>
    public const float RainShadowStrength = 0.40f;
    /// <summary>Базовые осадки у океана (Q16).</summary>
    public const ushort CoastalPrecipQ16 = 52000;
    /// <summary>Минимальные осадки в глубине материка (Q16).</summary>
    public const ushort InlandPrecipFloorQ16 = 12000;
    /// <summary>Амплитуда шума осадков (Q16).</summary>
    public const ushort PrecipNoiseAmpQ16 = 12000;

    // ===== Группа 4: гидрология (L3) =====
    /// <summary>Порог flow accumulation, при котором клетка — река (Q16 накопления).</summary>
    public const ushort RiverThresholdQ16 = 43000;
    /// <summary>Доля впадин, становящихся озёрами (1 из N по hash presence).</summary>
    public const int LakeFillDivisor = 9;
    /// <summary>Извилистость русла: амплитуда меандр-шума вдоль потока.</summary>
    public const float RiverMeanderStrength = 6f;
    /// <summary>Запас региона-окна для hydrology (margin ≥ riverReach), клеток.</summary>
    public const int HydroMarginCells = 128;

    // ===== Группа 5: геология/почва/растительность (L4..L6) =====
    /// <summary>Базовая плотность камней (Q16 порога roll).</summary>
    public const ushort StoneBaseQ16 = 3000;
    /// <summary>Дополнительная плотность камней на крутых склонах (осыпь, Q16).</summary>
    public const ushort StoneSlopeBonusQ16 = 12000;
    /// <summary>Вес поймы (аллювия) у рек в плодородии (Q16).</summary>
    public const ushort FertilityAlluvialQ16 = 26000;
    /// <summary>Базовая лесистость (Q16 сигмоиды до погоды/почвы).</summary>
    public const ushort ForestBaseQ16 = 8000;
    /// <summary>Оптимальная температура леса (Q16).</summary>
    public const ushort ForestTempOptimumQ16 = 32000;
    /// <summary>Штраф леса за отклонение температуры от оптимума (Q16 на Q16 дельты).</summary>
    public const int ForestTempPenaltyQ16 = 24000;
    /// <summary>Вес осадков в индексе леса (Q16 на Q16 осадков).</summary>
    public const int ForestPrecipWeightQ16 = 30000;
    /// <summary>Вес почвы в индексе леса (Q16).</summary>
    public const int ForestSoilWeightQ16 = 8000;
    /// <summary>Высотный штраф леса (выше — редколесье), Q16 на Q16 превышения предгорий.</summary>
    public const int ForestElevPenaltyQ16 = 20000;

    // ===== Группа 6: биомы (L7) — пороги Уиттакера (Q16) =====
    public const ushort TundraTempMaxQ16 = 15000;   // холоднее — тундра
    public const ushort TaigaTempMaxQ16 = 24000;    // холоднее — тайга
    public const ushort WarmTempMinQ16 = 38000;     // теплее — жаркая зона
    public const ushort HotTempMinQ16 = 47000;      // тропики
    public const ushort VeryDryPrecipQ16 = 14000;   // пустыня
    public const ushort DryPrecipQ16 = 26000;       // степь/саванна
    public const ushort WetPrecipQ16 = 42000;       // лес
    public const ushort VeryWetPrecipQ16 = 52000;   // болото/троплес
    /// <summary>Минимальный поток для болота (Q16 accumulation).</summary>
    public const ushort SwampFlowMinQ16 = 30000;

    // ===== Группа 7: локальная карта (проекция мира) =====
    /// <summary>Масштабный множитель P(tree) = forest · k_biome (Q16).</summary>
    public const ushort LocalTreeDensityFromForestQ16 = 65535;
    /// <summary>Максимум деревьев в пустыне (Q16 вероятности) — приёмка ≤ 3%.</summary>
    public const ushort DesertTreeMaxQ16 = 2000;

    // ===== Гидрология: сетки и масштаб накопления =====
    /// <summary>Декремент шага решётки priority-flood относительно клеточной сетки.</summary>
    public const int HydroGridStep = 4;
    /// <summary>Порог «истинного моря» для поиска океана (ниже seaLevel с запасом).</summary>
    public const ushort OceanElevQ16 = 23000;
    /// <summary>Лог2-нормировка flow accumulation в Q16: log2(8·gridCells), grid=512/4 → ~14.7.</summary>
    public const float FlowLog2Divisor = 15f;

    /// <summary>Q16 = 1.0 (для целочисленных знаменателей; максимальное значение ushort — 65535).</summary>
    public const int Q16One = 65536;

    private static readonly int[] SigmoidTable = BuildSigmoidTable();

    /// <summary>Таблица smooth-сигмоиды t*t*(3-2t) в Q16, 1024 ступени.</summary>
    public static int SigmoidQ16(int xQ16)
    {
        if (xQ16 <= -Q16One) return 0;
        if (xQ16 >= Q16One) return Q16One;
        int idx = (xQ16 + Q16One) * (SigmoidTable.Length - 1) / (2 * Q16One);
        return SigmoidTable[idx];
    }

    private static int[] BuildSigmoidTable()
    {
        const int steps = 1024;
        var table = new int[steps];
        for (int i = 0; i < steps; i++)
        {
            double t = (double)i / (steps - 1);
            table[i] = (int)Math.Round(t * t * (3.0 - 2.0 * t) * Q16One);
        }
        return table;
    }

    /// <summary>Линейное смешивание двух Q16 значений с весом t (0..Q16One), целочисленно.</summary>
    public static int MixQ16(int a, int b, int tQ16)
    {
        tQ16 = Math.Clamp(tQ16, 0, Q16One);
        return a + (int)(((long)(b - a) * tQ16 + Q16One / 2) / Q16One);
    }

    public static ushort ClampQ16(long v)
        => (ushort)Math.Clamp(v, 0, ushort.MaxValue);
}
