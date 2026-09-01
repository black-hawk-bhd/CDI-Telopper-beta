using EEWTelop.Application.Configuration;

namespace EEWTelop.Application.Events;

public interface IEventSource : IAsyncDisposable
{
    ProviderConnectionSnapshot Connection { get; }

    event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged;

    IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
        CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    void RequestReconnect(ReconnectReason reason);
}

public interface IProviderConfigurableEventSource
{
    void ConfigureProvider(ProviderSettings settings);
}

public enum ProviderConnectionState
{
    Stopped = 0,
    Connecting,
    Connected,
    Stale,
    Reconnecting,
    Faulted,
}

public enum ReconnectReason
{
    ConnectionLost = 0,
    NetworkAvailable,
    SystemResume,
    RuntimeGap,
    EndpointChanged,
}

public sealed record ProviderConnectionSnapshot(
    ProviderConnectionState State,
    DateTimeOffset ChangedAt,
    DateTimeOffset? LastReceivedAt = null,
    TimeSpan? RetryDelay = null,
    string? Detail = null);
