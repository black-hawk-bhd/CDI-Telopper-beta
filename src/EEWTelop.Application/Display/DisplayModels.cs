using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

public enum EndPolicy
{
    AutoHide = 0,
    HoldUntilCancelled,
    LoopUntilReplaced,
    Manual,
}

public enum OverlayPriority
{
    Quake = 100,
    WeatherAdvisory = 180,
    UnknownTsunami = 250,
    TsunamiWatch = 300,
    WeatherWarning = 350,
    Eew = 400,
    WeatherSpecialWarning = 450,
    VolcanoWarning = 470,
    TsunamiWarning = 500,
    EruptionFlash = 520,
    TsunamiCancel = 550,
}

public sealed record DisplayProgram(
    string ProgramId,
    EventId EventId,
    EventKind Kind,
    SourceMode SourceMode,
    DateTimeOffset IssuedAt,
    OverlayPriority Priority,
    IReadOnlyList<DisplayPage> Pages,
    DateTimeOffset StartedAtUtc,
    EndPolicy EndPolicy,
    string RehearsalLabel);

public sealed record DisplayPage(
    int Index,
    IReadOnlyList<DisplayBlock> Blocks,
    string AccessibleText,
    TimeSpan? DurationOverride);

public sealed record DisplayBlock(
    string Badge,
    string PrimaryText,
    string SecondaryText,
    string StyleToken);

public static class DisplayStyleTokens
{
    public const string Correction = "correction";
    public const string EewHeader = "eew-header";
    public const string EewHeaderCancel = "eew-header-cancel";
    public const string EewHeaderTest = "eew-header-test";
    public const string EewWarning = "eew-warning";
    public const string EewAreas = "eew-areas";
    public const string Summary = "summary";
    public const string Advisory = "advisory";
    public const string Intensity = "intensity";
    public const string Comment = "comment";
    public const string Tsunami = "tsunami";
    public const string TsunamiCancel = "tsunami-cancel";
    public const string WeatherAdvisory = "weather-advisory";
    public const string WeatherWarning = "weather-warning";
    public const string WeatherDangerWarning = "weather-danger-warning";
    public const string WeatherSpecialWarning = "weather-special-warning";
    public const string WeatherCancel = "weather-cancel";
    public const string VolcanoForecast = "volcano-forecast";
    public const string VolcanoWarning = "volcano-warning";
    public const string EruptionFlash = "eruption-flash";
    public const string Empty = "empty";
    public const string PageIndicator = "page-indicator";
}
