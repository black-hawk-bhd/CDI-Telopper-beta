using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Obs;

internal sealed class ExternalApiHealth
{
    private readonly object _gate = new();
    private readonly Dictionary<EventKind, DateTimeOffset> _accepted = [];
    private readonly HashSet<EventKind> _wasHealthy = [];

    internal void Observe(EventIngestionResult result)
    {
        if (result.Status != EventIngestionStatus.Accepted || result.Event?.SourceMode != SourceMode.Production) return;
        lock (_gate) _accepted[result.Event.Kind] = result.Event.ReceivedAt;
    }

    internal ExternalDomainHealth Read(EventKind kind, bool configured, ProviderConnectionState? state)
    {
        lock (_gate)
        {
            if (state == ProviderConnectionState.Connected && configured) _wasHealthy.Add(kind);
            string health = !configured ? "disabled" : state switch
            {
                ProviderConnectionState.Connected => "healthy",
                ProviderConnectionState.Faulted => "failed",
                ProviderConnectionState.Stale or ProviderConnectionState.Stopped => "stale",
                ProviderConnectionState.Connecting or ProviderConnectionState.Reconnecting =>
                    _wasHealthy.Contains(kind) ? "stale" : "connecting",
                _ => "unknown",
            };
            return new(configured, health, _accepted.TryGetValue(kind, out var time) ? time : null);
        }
    }
}

internal sealed record ExternalDomainHealth(bool Configured, string State, DateTimeOffset? LastAcceptedAt);
