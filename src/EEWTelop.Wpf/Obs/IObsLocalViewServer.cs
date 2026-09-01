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

    string EewUrl { get; }

    string TsunamiUrl { get; }

    string WeatherUrl { get; }

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
