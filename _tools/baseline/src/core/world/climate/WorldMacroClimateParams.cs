using System;
using Game.Core.WorldStreaming;

namespace Game.Core.WorldClimate;

/// <summary>
/// 20 параметров генерации МИРОВОЙ КАРТЫ. Все выводятся детерминированно из
/// worldSeed — одна и та же карта при одном сиде, разная при разных.
///
/// Единицы: Q16 (0..65535 == 0..1), если в комментарии не сказано иначе.
///
/// Представление о мире:
///   1 единица генерации = 1 РЕГИОН = <see cref="Game.Core.WorldStreaming.WorldRegions.RegionSize"/>
///   (512) клеток. Мир = 2048 x 1024 региона = 1 048 576 x 524 288 клеток.
///   Экватор — на regionY = 512, полюса — на regionY = 0 и 1023.
///   Локальная (игровая) карта 512x512 клеток = ровно один регион.
/// </summary>
public readonly record struct WorldMacroClimateParams(
    /// <summary>1. Порог воды на континентальной высоте: выше — суша.</summary>
    ushort SeaLevel,
    /// <summary>2. Желаемая доля суши (для нормировки рельефа).</summary>
    ushort LandFraction,
    /// <summary>3. Размер континентов: период базового шума в РЕГИОНАХ (24..72).</summary>
    ushort ContinentScale,
    /// <summary>4. Амплитуда domain-warp: бухты, фиорды, полуострова.</summary>
    ushort CoastWarp,
    /// <summary>5. Число тектонических плит (8..22).</summary>
    ushort PlateCount,
    /// <summary>6. Вклад плит в рельеф (щиты/впадины).</summary>
    ushort PlateStrength,
    /// <summary>7. Горообразование на границах плит (складчатые пояса).</summary>
    ushort OrogenyStrength,
    /// <summary>8. Плотность островных дуг/архипелагов вдоль границ плит.</summary>
    ushort IslandArcDensity,
    /// <summary>9. Шероховатость: октавы fBm (3..6) и амплитуда мелкого шума.</summary>
    ushort Roughness,
    /// <summary>10. Температура на экваторе.</summary>
    ushort EquatorTemperature,
    /// <summary>11. Температура на полюсах.</summary>
    ushort PolarTemperature,
    /// <summary>12. Полуширина тропического пояса в единицах широты Q16.</summary>
    ushort TropicsWidth,
    /// <summary>13. Падение температуры с высотой (lapse rate), амплитуда.</summary>
    ushort LapseRate,
    /// <summary>14. Широта пояса пустынь (Q16 от экватора; реальный максимум — субтропики).</summary>
    ushort DesertBeltLatitude,
    /// <summary>15. Базовая влажность атмосферы.</summary>
    ushort BaseMoisture,
    /// <summary>16. Сила преобладающих ветров (перенос влаги с океана).</summary>
    ushort WindStrength,
    /// <summary>17. Орошающий/дождевой барьерный эффект хребтов (rain shadow).</summary>
    ushort RainShadowStrength,
    /// <summary>18. Сухость внутренних областей материков (континентальность).</summary>
    ushort ContinentalityDry,
    /// <summary>19. Влияние тёплых/холодных океанских течений на береговую влажность.</summary>
    ushort OceanCurrentStrength,
    /// <summary>20. Порог/сила ледников и снежного покрова у полюсов.</summary>
    ushort IceCapStrength)
{
    /// <summary>Количество параметров (для диагностики/дампов).</summary>
    public const int Count = 20;

    /// <summary>Домены-лейны для вывода параметров из сида (0..Count-1).</summary>
    private static ushort Range(ulong worldSeed, int lane, int min, int max)
    {
        ulong h = CoordinateHash.Hash(worldSeed, 0u, GenerationDomain.ClimateParams, lane, 0L);
        uint span = (uint)(max - min + 1);
        return (ushort)(min + (int)(h % span));
    }

    /// <summary>Макро-архетипы мира для разнообразия планетарной генерации.</summary>
    public enum WorldArchetype : byte
    {
        Continents = 0,   // Землеподобный мир: 2-5 больших континентов, океанические бассейны
        Pangaea = 1,      // Суперконтинент: 1 гигантский массив суши с супер-океаном
        Archipelago = 2,  // Архипелаги: океанический мир, цепочки островов вдоль плит
        InlandSeas = 3    // Средиземноморье: изрезанная суша с крупными внутренними морями и заливами
    }

    /// <summary>
    /// Определяет архетип мира детерминированно из сида.
    /// </summary>
    public static WorldArchetype GetArchetype(ulong worldSeed)
    {
        ulong h = CoordinateHash.Hash(worldSeed, 0u, GenerationDomain.ClimateParams, 9999L, 0L);
        uint roll = (uint)(h % 100u);
        if (roll < 35) return WorldArchetype.Continents;   // 35%
        if (roll < 60) return WorldArchetype.Pangaea;      // 25%
        if (roll < 80) return WorldArchetype.Archipelago;  // 20%
        return WorldArchetype.InlandSeas;                  // 20%
    }

    /// <summary>
    /// Детерминированный набор параметров мира из сида с учетом макро-архетипа.
    /// </summary>
    public static WorldMacroClimateParams FromSeed(ulong worldSeed)
    {
        WorldArchetype arch = GetArchetype(worldSeed);

        ushort seaLevel, landFraction, contScale, coastWarp, plateCount, plateStrength;
        ushort orogenyStrength, islandArcs, roughness, baseMoist, contDry;

        switch (arch)
        {
            case WorldArchetype.Pangaea:
                seaLevel = Range(worldSeed, 0, 27000, 33000);
                landFraction = Range(worldSeed, 1, 48000, 58000);
                contScale = Range(worldSeed, 2, 500, 850);
                coastWarp = Range(worldSeed, 3, 10000, 22000);
                plateCount = Range(worldSeed, 4, 6, 12);
                plateStrength = Range(worldSeed, 5, 16000, 30000);
                orogenyStrength = Range(worldSeed, 6, 18000, 35000);
                islandArcs = Range(worldSeed, 7, 6000, 16000);
                roughness = Range(worldSeed, 8, 3, 5);
                baseMoist = Range(worldSeed, 14, 26000, 38000);
                contDry = Range(worldSeed, 17, 18000, 36000);
                break;

            case WorldArchetype.Archipelago:
                seaLevel = Range(worldSeed, 0, 42000, 50000);
                landFraction = Range(worldSeed, 1, 18000, 28000);
                contScale = Range(worldSeed, 2, 36, 90);
                coastWarp = Range(worldSeed, 3, 16000, 34000);
                plateCount = Range(worldSeed, 4, 18, 32);
                plateStrength = Range(worldSeed, 5, 8000, 20000);
                orogenyStrength = Range(worldSeed, 6, 10000, 24000);
                islandArcs = Range(worldSeed, 7, 30000, 55000);
                roughness = Range(worldSeed, 8, 4, 6);
                baseMoist = Range(worldSeed, 14, 38000, 52000);
                contDry = Range(worldSeed, 17, 6000, 16000);
                break;

            case WorldArchetype.InlandSeas:
                seaLevel = Range(worldSeed, 0, 32000, 38000);
                landFraction = Range(worldSeed, 1, 42000, 52000);
                contScale = Range(worldSeed, 2, 250, 500);
                coastWarp = Range(worldSeed, 3, 20000, 38000);
                plateCount = Range(worldSeed, 4, 14, 24);
                plateStrength = Range(worldSeed, 5, 12000, 24000);
                orogenyStrength = Range(worldSeed, 6, 16000, 30000);
                islandArcs = Range(worldSeed, 7, 14000, 28000);
                roughness = Range(worldSeed, 8, 4, 6);
                baseMoist = Range(worldSeed, 14, 32000, 46000);
                contDry = Range(worldSeed, 17, 10000, 24000);
                break;

            default: // WorldArchetype.Continents
                seaLevel = Range(worldSeed, 0, 31000, 37000);
                landFraction = Range(worldSeed, 1, 36000, 46000);
                contScale = Range(worldSeed, 2, 220, 420);
                coastWarp = Range(worldSeed, 3, 12000, 26000);
                plateCount = Range(worldSeed, 4, 10, 18);
                plateStrength = Range(worldSeed, 5, 14000, 26000);
                orogenyStrength = Range(worldSeed, 6, 16000, 32000);
                islandArcs = Range(worldSeed, 7, 12000, 24000);
                roughness = Range(worldSeed, 8, 3, 5);
                baseMoist = Range(worldSeed, 14, 30000, 44000);
                contDry = Range(worldSeed, 17, 8000, 24000);
                break;
        }

        return new WorldMacroClimateParams(
            SeaLevel:             seaLevel,
            LandFraction:         landFraction,
            ContinentScale:       contScale,
            CoastWarp:            coastWarp,
            PlateCount:           plateCount,
            PlateStrength:        plateStrength,
            OrogenyStrength:      orogenyStrength,
            IslandArcDensity:     islandArcs,
            Roughness:            roughness,
            EquatorTemperature:   Range(worldSeed, 9,  52000, 62000),
            PolarTemperature:     Range(worldSeed, 10,  2000, 10000),
            TropicsWidth:         Range(worldSeed, 11,  8000, 18000),
            LapseRate:            Range(worldSeed, 12,  9000, 18000),
            DesertBeltLatitude:   Range(worldSeed, 13, 18000, 34000),
            BaseMoisture:         baseMoist,
            WindStrength:         Range(worldSeed, 15, 12000, 28000),
            RainShadowStrength:   Range(worldSeed, 16, 10000, 26000),
            ContinentalityDry:    contDry,
            OceanCurrentStrength: Range(worldSeed, 18,  8000, 24000),
            IceCapStrength:       Range(worldSeed, 19,  6000, 20000));
    }

    /// <summary>Человекочитаемый дамп (диагностика/HUD).</summary>
    public override string ToString()
        => $"sea={SeaLevel} land={LandFraction} contScale={ContinentScale} warp={CoastWarp} " +
           $"plates={PlateCount} plateStr={PlateStrength} orogeny={OrogenyStrength} arcs={IslandArcDensity} " +
           $"rough={Roughness} Teq={EquatorTemperature} Tpol={PolarTemperature} tropW={TropicsWidth} " +
           $"lapse={LapseRate} desertLat={DesertBeltLatitude} moist={BaseMoisture} wind={WindStrength} " +
           $"rainShadow={RainShadowStrength} contDry={ContinentalityDry} current={OceanCurrentStrength} ice={IceCapStrength}";
}