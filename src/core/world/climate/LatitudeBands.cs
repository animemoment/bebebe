using System;

namespace Game.Core.WorldClimate;

/// <summary>
/// Широтные пояса мировой карты. Мир — 2048x1024 РЕГИОНА; экватор на regionY = 512,
/// полюса на regionY = 0 и 1023. Широта — Q16: 0 = экватор, 1 = полюс.
///
/// Пояса (как на Земле):
///   |lat| 0.00..0.15  экваториальный влажный (дождевые леса)
///   |lat| 0.15..0.45  субтропический сухой  (пустыни — максимум у DesertBeltLatitude)
///   |lat| 0.45..0.70  умеренный влажный     (леса/степи)
///   |lat| 0.70..1.00  полярный              (тундра → ледники)
/// </summary>
public static class LatitudeBands
{
    /// <summary>Регионов по оси Y в мире (см. WorldRegions.WorldRegionsY).</summary>
    public const long WorldRegionsY = 1024;

    /// <summary>Полувысота мира = широта экватора.</summary>
    public const long EquatorRegionY = WorldRegionsY / 2;

    /// <summary>Полуширина субтропического сухого пояса в Q16 единицах широты.</summary>
    private const int DesertBeltHalfWidth = 15000;

    /// <summary>Полуширина экваториального влажного пояса в Q16 единицах широты.</summary>
    private const int EquatorialWetHalfWidth = 13000;

    /// <summary>Широта региона: 0 = экватор, 65535 = полюс.</summary>
    public static ushort LatitudeQ16(long regionY)
    {
        long d = regionY - EquatorRegionY;
        if (d < 0) d = -d;
        long lat = d * Q16.One / EquatorRegionY;
        return (ushort)Math.Clamp(lat, 0L, Q16.One);
    }

    /// <summary>0 = полярный, 1 = умеренный, 2 = субтропический, 3 = тропический/экваториальный.</summary>
    public static int Zone(ushort latitudeQ16)
    {
        if (latitudeQ16 >= 46000) return 0;  // |lat| >= ~0.70 — полярный
        if (latitudeQ16 >= 24000) return 1;  // умеренный
        if (latitudeQ16 >= 12000) return 2;  // субтропический
        return 3;                            // тропический/экваториальный
    }

    /// <summary>
    /// Температура по широте: тёплое плато у экватора, быстрый спад к полюсам.
    /// Возвращает Q16 (1 = жарко). TropicsWidth расширяет тёплую зону.
    /// </summary>
    public static ushort TemperatureAtLatitude(ushort latitudeQ16, in WorldClimateParams p)
    {
        // Отображаем широту так, чтобы тропики были «плоскими»: t = lat^(1 + k).
        int widen = (int)p.TropicsWidth - 12000;                 // -4000..+6000
        int shifted = (int)latitudeQ16 - widen / 2;
        ushort t = (ushort)Math.Clamp(shifted, 0, Q16.One);
        ushort shaped = Q16.SmoothStep(t);
        // Двойной smoothstep — плато у экватора и более резкий край у полюсов.
        shaped = Q16.SmoothStep(shaped);
        return Q16.Lerp(p.EquatorTemperature, p.PolarTemperature, shaped);
    }

    /// <summary>
    /// Сухость от широты (0 = влажно, 1 = сухо). Максимум — субтропический пояс
    /// высокого давления (<see cref="WorldClimateParams.DesertBeltLatitude"/>),
    /// у экватора влажно, к полюсам снова сухо (холодная пустыня).
    /// </summary>
    public static ushort AridityAtLatitude(ushort latitudeQ16, in WorldClimateParams p)
    {
        int lat = latitudeQ16;

        // Субтропический максимум.
        int dDesert = Math.Abs(lat - p.DesertBeltLatitude);
        int desert = dDesert >= DesertBeltHalfWidth
            ? 0
            : (DesertBeltHalfWidth - dDesert) * Q16.One / DesertBeltHalfWidth;

        // Экваториальный влажный пояс гасит сухость и добавляет дожди.
        int equatorial = lat >= EquatorialWetHalfWidth
            ? 0
            : (EquatorialWetHalfWidth - lat) * Q16.One / EquatorialWetHalfWidth;

        // Холодная полярная пустыня: к полюсу влага вымерзает.
        int polar = lat <= 52000 ? 0 : (lat - 52000) * Q16.One / (Q16.One - 52000);

        long value = (long)desert * 70 / 100 + (long)polar * 45 / 100 - (long)equatorial * 85 / 100;
        return (ushort)Math.Clamp(value, 0L, Q16.One);
    }

    /// <summary>
    /// Зональный ветер: &gt; Half — восточный перенос (пассаты/западные ветры),
    /// &lt; Half — западный. Возвращает Q16 с центром Half = штиль.
    /// Пояса: пассаты 0..0.30, западные 0.30..0.60, полярные восточные 0.60..1.0.
    /// </summary>
    public static ushort WindZonalQ16(ushort latitudeQ16)
    {
        int lat = latitudeQ16;
        int signal;
        if (lat < 22000) signal = -1;              // пассаты: восток → запад
        else if (lat < 42000) signal = 1;          // западный перенос
        else if (lat < 62000) signal = 1;          // ещё западные (умеренный)
        else signal = -1;                           // полярные восточные

        // Сила максимальна в середине пояса.
        int strength = Q16.One / 2;
        return (ushort)Math.Clamp(Q16.Half + signal * strength / 2, 0, Q16.One);
    }

    /// <summary>
    /// Тепло океанского течения у берега: 1 = тёплое, 0 = холодное, 0.5 = нейтраль.
    /// Тёплые течения идут вдоль восточных берегов в тропиках, холодные — вдоль
    /// западных и в высоких широтах (упрощение для генерации осадков).
    /// </summary>
    public static ushort CurrentWarmthQ16(ushort latitudeQ16, bool eastCoast)
    {
        int lat = latitudeQ16;
        int warm = eastCoast ? 1 : -1;
        // В тропиках контраст максимален, у полюсов всё холодное.
        int contrast = lat >= 52000 ? 0 : (52000 - lat) * Q16.One / 52000;
        return (ushort)Math.Clamp(Q16.Half + warm * contrast / 2, 0, Q16.One);
    }
}