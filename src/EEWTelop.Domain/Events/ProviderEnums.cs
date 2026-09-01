namespace EEWTelop.Domain.Events;

public enum QuakeIssueType
{
    Unknown = 0,
    ScalePrompt,
    Destination,
    ScaleAndDestination,
    DetailScale,
    Foreign,
    Other,
    LongPeriodObservation,
    SubsequentEarthquakeAdvisory,
    NankaiTroughTemporaryInformation,
}

public enum CorrectionType
{
    None = 0,
    Unknown,
    ScaleOnly,
    DestinationOnly,
    ScaleAndDestination,
    Generic,
}

public enum DomesticTsunami
{
    Unknown = 0,
    None,
    Checking,
    NonEffective,
    Watch,
    Warning,
}

public enum ForeignTsunami
{
    Unknown = 0,
    None,
    Checking,
    NonEffectiveNearby,
    WarningNearby,
    WarningPacific,
    WarningPacificWide,
    WarningIndian,
    WarningIndianWide,
    Potential,
}

public enum TsunamiGrade
{
    Unknown = 0,
    MajorWarning,
    Warning,
    Watch,
    Forecast,
}

public enum EewWarningKind
{
    Unknown = 0,
    ForecastNotArrived = 10,
    ForecastArrived = 11,
    Plum = 19,
}
