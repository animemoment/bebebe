using System;

namespace Game.Core.WorldLayers;

/// <summary>
/// Единый набор параметров генерации мира (версия v4). Все слои читают только эти
/// константы — никаких bbox-зависимых сидов и случайных параметрических RNG.
/// Значения подобраны под метрики приёмки: вода 20–35%, леса 15–35%, горы 5–12%.
/// </summary>
public static class WorldLayerParams
{
    /// <summary>Версия алгоритма генерации. v4 = единый WorldLayerStack (мировая карта = источник истины).</summary>
    public const uint GeneratorVersion = 4u;

    // ---------- Домены шума (стабильные id, не пересекать) ----------
    public const uint DomElevBase = 0x1001;   // базовый fBm рельефа
    public const uint DomElevRidge = 0x1002;  // ridged-хребты
    public const uint DomWarpX = 0x1003;      // domain-warp X
    public const uint DomWarpY = 0x1004;      // domain-warp Y
    public const uint DomTempNoise = 0x1005;  // шум температуры
    public const uint DomPrecipNoise = 0x1006;// шум осадков
    public const uint DomOcean = 0x1007;      // океанские «источники влаги» для L2
    public const uint DomSoil = 0x1008;       // фоновое плодородие
    public const uint DomVegNoise = 0x1009;   // мелкий шум растительности
    public const uint DomStone = 0x100A;      // геология (вороний)
    public const uint DomTreeRoll = 0x100B;   // бросок дерева на клетку
    public const uint DomStoneRoll = 0x100C;  // бросок камня на клетку

    // ---------- Рельеф (L0) ----------
    public const float ReliefScale = 128f;    // масштаб крупных форм (континенты)
    public const int ReliefOctaves = 5;
    public const float RidgeWeight = 0.25f;   // доля хребтов
    public const float RidgeScale = 76.8f;    // ReliefScale * 0.6
    public const int RidgeOctaves = 3;
    public const float WarpStrength = 26f;    // сила domain-warp (извилистость берегов)
    public const float SeaLevel = 0.42f;      // порог воды по высоте
    public const float MountainElevHi = 0.66f;// высота, с которой горы

    // ---------- Температура (L1), шкала Q16 ----------
    public const ushort EquatorTempQ16 = 52000;
    public const ushort PolarCoolingQ16 = 44000;   // вычитается на полюсе
    public const int LapseRatePerUnit = 16000;     // падение Q16 на 1.0 высоты над морем
    public const ushort TempNoiseAmp = 3500;
    public const float TempNoiseScale = 96f;

    // ---------- Осадки (L2) ----------
    public const float OceanFieldScale = 384f;  // решётка «океанских источников»
    public const float MoistDecayLambda = 420f; // e^(-d/λ): затухание влаги вглубь суши
    public const float RainShadowStrength = 0.55f; // ×(1 - strength·upwindDrop) за горами
    public const float WindDirX = 1f;           // преобладающий ветер — с запада
    public const float WindDirY = 0f;
    public const float ShadowLookDist = 48f;    // дистанция просмотра против ветра
    public const ushort PrecipBaseQ16 = 30000;
    public const ushort PrecipOceanBonusQ16 = 22000;
    public const ushort PrecipNoiseAmp = 6000;
    public const float PrecipNoiseScale = 144f;

    // ---------- Гидрология (L3) ----------
    public const int HydroMargin = 64;          // запас региона-окна ≥ river reach
    public const int RiverThreshold = 260;      // flow accumulation для реки
    public const int LakeMinCells = 18;         // минимальная впадина-озеро
    public const int LakeMaxDepth = 14;         // глубина заполнения впадин (height*1000)
    public const int SpringMinFlow = 6;         // родник: локальный минимум с накоплением ≥
    public const int RiverMeanderAmp = 1;       // извилистость русла (сдвиг в клетках)

    // ---------- Геология (L4) ----------
    public const float StoneVoronoiScale = 40f;
    public const double StoneBaseDensity = 0.010;
    public const double StoneSlopeMult = 4.0;   // у гор/осыпей
    public const double StoneWaterMult = 2.0;   // у воды
    public const double StoneDryMult = 0.3;     // вдали от всего

    // ---------- Почва (L5) ----------
    public const int FertilityAlluvialBonus = 40;   // пойма у рек/озёр
    public const int FertilityForestBonus = 20;     // органика под лесом

    // ---------- Растительность (L6) ----------
    public const ushort ForestTempOptimumQ16 = 34000;
    public const ushort ForestTempWidthQ16 = 16000;
    public const int ForestPrecipWeight = 2;        // forestIdx = precip*2 - |temp-opt| + soil*0.5 - elevPenalty
    public const ushort ForestIndexLo = 22000;      // ниже — ничего не растёт
    public const ushort ForestIndexHi = 46000;      // выше — максимальная плотность

    // ---------- Биомы (L7, Уиттакер в Q16) ----------
    public const ushort TundraTempHi = 14000;
    public const ushort TaigaTempHi = 24000;
    public const ushort TemperateTempHi = 40000;
    public const ushort DryPrecipHi = 20000;
    public const ushort AridPrecipHi = 12000;
    public const ushort WetPrecipLo = 40000;
    public const ushort SwampPrecipMin = 46000;

    // ---------- Плотности деревьев по биомам (процент ×100) ----------
    public const int TreeKDesert = 3;
    public const int TreeKSavanna = 8;
    public const int TreeKSteppeGrassland = 4;
    public const int TreeKTundra = 2;
    public const int TreeKTemperate = 55;
    public const int TreeKTropical = 75;
    public const int TreeKSwamp = 30;
    public const int TreeKMeadowDefault = 20;

    public static int ClampI(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    public static ushort ClampQ16(int v) => (ushort)(v < 0 ? 0 : (v > 65535 ? 65535 : v));
}
