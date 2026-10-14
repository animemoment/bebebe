namespace Game.Core.WorldStreaming;

/// <summary>
/// 18 физико-климатических параметров клетки/региона мира в формате Q0.16 (0..65535).
/// Readonly record struct, zero-alloc, передается по значению/in, детерминирована на любой платформе.
/// </summary>
public readonly record struct WorldClimateParams(
    ushort ElevationQ16,
    ushort ContinentalnessQ16,
    ushort LatitudeQ16,
    ushort TemperatureQ16,
    ushort MoistureQ16,
    ushort PrecipitationQ16,
    ushort DrainageQ16,
    ushort WindHeadingQ16,
    ushort RainShadowQ16,
    ushort TectonicPressureQ16,
    ushort OceanInfluenceQ16,
    ushort VegetationDensityQ16,
    ushort SoilFertilityQ16,
    ushort RockExposureQ16,
    ushort GlaciationQ16,
    ushort AridityQ16,
    ushort ArchipelagoTendencyQ16,
    ushort RiverDensityQ16)
{
    public const ushort Q16Max = ushort.MaxValue; // 65535
    public const ushort Q16Half = 32768;

    public float Elevation => ElevationQ16 / 65535f;
    public float Continentalness => ContinentalnessQ16 / 65535f;
    public float Latitude => LatitudeQ16 / 65535f;
    public float Temperature => TemperatureQ16 / 65535f;
    public float Moisture => MoistureQ16 / 65535f;
    public float Precipitation => PrecipitationQ16 / 65535f;
    public float Drainage => DrainageQ16 / 65535f;
    public float WindHeading => WindHeadingQ16 / 65535f;
    public float RainShadow => RainShadowQ16 / 65535f;
    public float TectonicPressure => TectonicPressureQ16 / 65535f;
    public float OceanInfluence => OceanInfluenceQ16 / 65535f;
    public float VegetationDensity => VegetationDensityQ16 / 65535f;
    public float SoilFertility => SoilFertilityQ16 / 65535f;
    public float RockExposure => RockExposureQ16 / 65535f;
    public float Glaciation => GlaciationQ16 / 65535f;
    public float Aridity => AridityQ16 / 65535f;
    public float ArchipelagoTendency => ArchipelagoTendencyQ16 / 65535f;
    public float RiverDensity => RiverDensityQ16 / 65535f;

    /// <summary>Дефолтный умеренный климат (равнина, умеренная влажность и температура).</summary>
    public static WorldClimateParams DefaultTemperate => new(
        ElevationQ16: 32000,
        ContinentalnessQ16: 30000,
        LatitudeQ16: 32768,
        TemperatureQ16: 32768,
        MoistureQ16: 32768,
        PrecipitationQ16: 32768,
        DrainageQ16: 32768,
        WindHeadingQ16: 16384,
        RainShadowQ16: 0,
        TectonicPressureQ16: 10000,
        OceanInfluenceQ16: 25000,
        VegetationDensityQ16: 35000,
        SoilFertilityQ16: 35000,
        RockExposureQ16: 10000,
        GlaciationQ16: 0,
        AridityQ16: 15000,
        ArchipelagoTendencyQ16: 5000,
        RiverDensityQ16: 25000);
}
