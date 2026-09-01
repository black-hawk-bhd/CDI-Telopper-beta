using EEWTelop.Application.Formatting;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.P2P.Normalization;

internal static class P2pEnumMapper
{
    public static QuakeIssueType ToQuakeIssueType(string? value) => value switch
    {
        "ScalePrompt" => QuakeIssueType.ScalePrompt,
        "Destination" => QuakeIssueType.Destination,
        "ScaleAndDestination" => QuakeIssueType.ScaleAndDestination,
        "DetailScale" => QuakeIssueType.DetailScale,
        "Foreign" => QuakeIssueType.Foreign,
        "Other" => QuakeIssueType.Other,
        _ => QuakeIssueType.Unknown,
    };

    public static CorrectionType ToCorrectionType(string? value) => value switch
    {
        "None" => CorrectionType.None,
        "ScaleOnly" => CorrectionType.ScaleOnly,
        "DestinationOnly" => CorrectionType.DestinationOnly,
        "ScaleAndDestination" => CorrectionType.ScaleAndDestination,
        _ => CorrectionType.Unknown,
    };

    public static DomesticTsunami ToDomesticTsunami(string? value) => value switch
    {
        "None" => DomesticTsunami.None,
        "Checking" => DomesticTsunami.Checking,
        "NonEffective" => DomesticTsunami.NonEffective,
        "Watch" => DomesticTsunami.Watch,
        "Warning" => DomesticTsunami.Warning,
        _ => DomesticTsunami.Unknown,
    };

    public static ForeignTsunami ToForeignTsunami(string? value) => value switch
    {
        "None" => ForeignTsunami.None,
        "Checking" => ForeignTsunami.Checking,
        "NonEffectiveNearby" => ForeignTsunami.NonEffectiveNearby,
        "WarningNearby" => ForeignTsunami.WarningNearby,
        "WarningPacific" => ForeignTsunami.WarningPacific,
        "WarningPacificWide" => ForeignTsunami.WarningPacificWide,
        "WarningIndian" => ForeignTsunami.WarningIndian,
        "WarningIndianWide" => ForeignTsunami.WarningIndianWide,
        "Potential" => ForeignTsunami.Potential,
        _ => ForeignTsunami.Unknown,
    };

    public static TsunamiGrade ToTsunamiGrade(string? value) => value switch
    {
        "MajorWarning" => TsunamiGrade.MajorWarning,
        "Warning" => TsunamiGrade.Warning,
        "Watch" => TsunamiGrade.Watch,
        "Forecast" => TsunamiGrade.Forecast,
        _ => TsunamiGrade.Unknown,
    };

    public static EewWarningKind ToEewWarningKind(string? value) => value switch
    {
        "10" => EewWarningKind.ForecastNotArrived,
        "11" => EewWarningKind.ForecastArrived,
        "19" => EewWarningKind.Plum,
        _ => EewWarningKind.Unknown,
    };

    public static JmaScale ToScale(double? value) => ScaleFormatter.Normalize(value);

    public static int ToScaleUpperBound(double? value)
    {
        if (value is null || !double.IsFinite(value.Value) || value.Value < 0 || value.Value > 100)
        {
            return -1;
        }

        int rounded = checked((int)Math.Round(value.Value, MidpointRounding.AwayFromZero));
        return rounded == 99 ? 99 : (int)ScaleFormatter.Normalize(value);
    }
}
