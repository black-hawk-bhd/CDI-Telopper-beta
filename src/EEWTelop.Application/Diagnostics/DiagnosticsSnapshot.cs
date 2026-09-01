using EEWTelop.Application.Configuration;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Operations;

namespace EEWTelop.Application.Diagnostics;

public sealed record DiagnosticsSnapshot(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string ApplicationVersion,
    string RuntimeVersion,
    string OperatingSystem,
    string ConnectionState,
    DateTimeOffset? LastReceivedAtUtc,
    int ReconnectCount,
    string ObsStatus,
    int ObsClientCount,
    string LastObsAudioCue,
    string LastObsAudioPlaybackResult,
    DateTimeOffset? LastObsAudioPlaybackAtUtc,
    AppSettings Settings,
    IReadOnlyList<AppLogEntry> Logs)
{
    public const int CurrentSchemaVersion = 3;

    public IReadOnlyList<OperationalAlert> OperationalAlerts { get; init; } = [];
    public IReadOnlyList<SourceComparisonResult> SourceComparisons { get; init; } = [];
    public IReadOnlyList<ProviderBranchConnectionSnapshot> ProviderConnections { get; init; } = [];
    public IReadOnlyDictionary<string, int> ObsRouteConnections { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
}
