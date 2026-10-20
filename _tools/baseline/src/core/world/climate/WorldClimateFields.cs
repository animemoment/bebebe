using System;

namespace Game.Core.WorldClimate;

/// <summary>
/// 17 климатических полей одной точки мира (Q0.16). Это «выход» мировой карты:
/// именно эти поля читают маппер биомов и локальная генерация чанка.
/// Все поля НЕПРЕРЫВНЫ по координатам, поэтому билинейное смешивание соседних
/// регионов не даёт тайловых швов (см. WorldClimateProvider.SampleBlendedClimate).
/// </summary>
public readonly record struct WorldClimateFields(
    /// <summary>Континентальность: 0 = открытый океан, 1 = глубь материка.</summary>
    ushort Continental,
    /// <summary>Высота суши 0..1 (0 на океане).</summary>
    ushort Elevation,
    /// <summary>Глубина океана 0..1 (0 на суше).</summary>
    ushort OceanDepth,
    /// <summary>Удалённость от берега 0..1 (континентальность климата).</summary>
    ushort CoastDistance,
    /// <summary>Температура 0 = полярная, 1 = экваториальная (с поправкой на высоту).</summary>
    ushort Temperature,
    /// <summary>Атмосферная влажность 0..1.</summary>
    ushort Moisture,
    /// <summary>Осадки 0..1.</summary>
    ushort Precipitation,
    /// <summary>Сухость 0..1 (1 = гипер-арид).</summary>
    ushort Aridity,
    /// <summary>Сила преобладающего ветра 0..1.</summary>
    ushort Wind,
    /// <summary>Тепло океанского течения 0..1 (0.5 — нейтрально).</summary>
    ushort CurrentWarmth,
    /// <summary>Потенциал леса 0..1.</summary>
    ushort Forest,
    /// <summary>Ледники/снег 0..1.</summary>
    ushort Ice,
    /// <summary>Потенциал поверхностного стока (реки) 0..1.</summary>
    ushort River,
    /// <summary>Скальность поверхности 0..1.</summary>
    ushort Stone,
    /// <summary>Плодородие почвы 0..1.</summary>
    ushort Fertility,
    /// <summary>Солёность воды 0..1 (0 на суше).</summary>
    ushort Salinity,
    /// <summary>Широта: 0 = экватор, 1 = полюс.</summary>
    ushort Latitude)
{
    /// <summary>Точка считается океаном при такой и меньшей континентальности.</summary>
    public const ushort OceanContinental = 24000;

    public bool IsOcean => Continental <= OceanContinental;

    /// <summary>Международная точка «нейтрального» Q16.</summary>
    public static WorldClimateFields Neutral => new(
        Q16.Half, Q16.Half, Q16.Half, Q16.Half, Q16.Half, Q16.Half, Q16.Half, Q16.Half,
        Q16.Half, Q16.Half, Q16.Half, 0, Q16.Half, Q16.Half, Q16.Half, 0, Q16.Half);

    /// <summary>Компактный дамп для логов/тестов.</summary>
    public string Dump()
        => $"cont={Continental} elev={Elevation} ocean={OceanDepth} coast={CoastDistance} " +
           $"temp={Temperature} moist={Moisture} precip={Precipitation} arid={Aridity} " +
           $"wind={Wind} current={CurrentWarmth} forest={Forest} ice={Ice} river={River} " +
           $"stone={Stone} fert={Fertility} salt={Salinity} lat={Latitude}";
}

/// <summary>Срез рельефа/плит от <see cref="ContinentShaper"/> (до климата).</summary>
public readonly record struct ContinentSample(
    /// <summary>Континентальность 0..1.</summary>
    ushort Continental,
    /// <summary>Высота суши 0..1.</summary>
    ushort Elevation,
    /// <summary>Глубина океана 0..1.</summary>
    ushort OceanDepth,
    /// <summary>Удалённость от берега 0..1.</summary>
    ushort CoastDistance,
    /// <summary>Близость границы плит 0..1.</summary>
    ushort PlateBoundary,
    /// <summary>Устойчивый номер плиты (для мелких деталей, НЕ смешивается).</summary>
    byte PlateId);