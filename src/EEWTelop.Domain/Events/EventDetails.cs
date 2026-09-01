namespace EEWTelop.Domain.Events;

public sealed record IssueInfo(
    string Source,
    DateTimeOffset IssuedAt,
    string RawType,
    CorrectionType Correction,
    string? Serial = null,
    string InformationType = "");

public sealed record HypocenterInfo(
    string Name,
    string ReducedName,
    double? Latitude,
    double? Longitude,
    int? DepthKilometers,
    double? Magnitude,
    string Condition,
    string MagnitudeDescription = "");

public sealed record EarthquakeInfo(
    DateTimeOffset OriginTime,
    DateTimeOffset? ArrivalTime,
    HypocenterInfo? Hypocenter,
    JmaScale MaximumScale,
    DomesticTsunami DomesticTsunami,
    ForeignTsunami ForeignTsunami);

public sealed record QuakePoint(
    string Prefecture,
    string Address,
    bool IsArea,
    JmaScale Scale,
    string DisplayName);

public sealed record LongPeriodIntensityArea(
    string Prefecture,
    string Area,
    int Class);

public sealed record LongPeriodIntensityInfo(
    int MaximumClass,
    IReadOnlyList<LongPeriodIntensityArea> Areas);

public sealed record EewArea(
    string Prefecture,
    string Name,
    JmaScale ScaleFrom,
    int ScaleTo,
    EewWarningKind WarningKind,
    DateTimeOffset? ArrivalTime);

public sealed record TsunamiFirstHeight(
    DateTimeOffset? ArrivalTime,
    string Condition);

public sealed record TsunamiMaximumHeight(
    string Description,
    double? ValueMeters,
    DateTimeOffset? ObservedAt = null,
    string Condition = "");

public sealed record TsunamiArea(
    TsunamiGrade Grade,
    bool Immediate,
    string Name,
    TsunamiFirstHeight? FirstHeight,
    TsunamiMaximumHeight? MaximumHeight)
{
    public TsunamiInformationRole Role { get; init; } = TsunamiInformationRole.ForecastArea;

    public string ParentAreaName { get; init; } = string.Empty;

    public DateTimeOffset? HighTideAt { get; init; }
}

public enum TsunamiInformationRole
{
    ForecastArea = 0,
    StationForecast,
    CoastalObservation,
    OffshoreObservation,
}

public enum WeatherWarningLevel
{
    Unknown = 0,
    Advisory = 1,
    Warning = 2,
    SpecialWarning = 3,
}

public enum WeatherInformationType
{
    WarningAndAdvisory = 0,
    RecordShortDurationHeavyRain,
    DisasterPreventionBulletin,
    TornadoAdvisory,
}

public sealed record WeatherWarningItem(
    string AreaName,
    string AreaCode,
    string KindName,
    string KindCode,
    WeatherWarningLevel Level,
    string Status,
    bool IsActive);

public enum VolcanoInformationType
{
    WarningForecast = 0,
    EruptionFlash,
}

public enum VolcanoAlertLevel
{
    Unknown = 0,
    Level1 = 1,
    Level2 = 2,
    Level3 = 3,
    Level4 = 4,
    Level5 = 5,
}

public sealed record VolcanoTargetArea(
    string Name,
    string Code,
    string KindName,
    string KindCode,
    string Status,
    string PreviousKindName = "",
    string PreviousKindCode = "");
