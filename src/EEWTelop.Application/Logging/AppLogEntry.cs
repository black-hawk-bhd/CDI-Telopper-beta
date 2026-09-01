namespace EEWTelop.Application.Logging;

public sealed record AppLogEntry(
    DateTimeOffset Timestamp,
    AppLogLevel Level,
    string EventName,
    string Message,
    Exception? Exception = null);

public enum AppLogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
    Critical = 4,
}

