namespace EEWTelop.Wpf.Obs;

public interface IObsLocalViewServer : IAsyncDisposable
{
    event Action<int>? ClientCountChanged;

    event Action<ObsDeliveryDiagnostic>? DeliveryReported
    {
        add { }
        remove { }
    }

    bool IsRunning { get; }

    int Port { get; }

    int ClientCount { get; }

    IReadOnlyDictionary<string, int> RouteClientCounts =>
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    int SnapshotIntervalMilliseconds { get; }

    string LastAudioCue { get; }

    string LastAudioPlaybackResult { get; }

    DateTimeOffset? LastAudioPlaybackAtUtc { get; }

    string OverlayUrl { get; }
    string MonitorUrl => string.Empty;

    string EewUrl { get; }

    string TsunamiUrl { get; }

    string WeatherUrl { get; }

    bool ExternalApiEnabled { get => false; set { } }

    string ExternalApiUrl => string.Empty;

    bool RehearsalApiEnabled { get => false; set { } }
    string RehearsalApiUrl => string.Empty;
    string? BeginApiRehearsal(string source) => null;
    void EndApiRehearsal(string? session = null) { }
    void ReplaceApiRehearsalSnapshot(string session, IReadOnlyList<EEWTelop.Domain.Events.DisasterEvent> events) { }
    void SetApiRehearsalHealth(string? session, string health) { }
    void ObserveApiRehearsal(EEWTelop.Domain.Events.DisasterEvent item, string? session = null, bool manualReplay = false) { }

    Task StartAsync(int port, CancellationToken cancellationToken = default);

    void UpdateSnapshotInterval(int milliseconds);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public enum ObsDeliveryStage
{
    AudioStarted = 0,
    AudioCompleted,
    AudioFailed,
}

public sealed record ObsDeliveryDiagnostic(
    ObsDeliveryStage Stage,
    string Route,
    long Sequence,
    string ProgramId,
    int PageIndex,
    DateTimeOffset ReportedAtUtc);
