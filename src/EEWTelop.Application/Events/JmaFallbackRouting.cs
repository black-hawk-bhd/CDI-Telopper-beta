using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;

namespace EEWTelop.Application.Events;

/// <summary>Runtime-only failover; never changes the user's saved provider choices.</summary>
public sealed class JmaFallbackRouting(ProviderSettings settings, IClock clock)
{
    public static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private ProviderSettings _settings = settings;
    private readonly Dictionary<ReceptionProvider, DateTimeOffset> _failures = [];

    public static bool IsEligible(ReceptionProvider provider) => provider is
        ReceptionProvider.P2pQuake or ReceptionProvider.Dmdata or ReceptionProvider.Axis;

    public void Configure(ProviderSettings value)
    {
        lock (_gate) { _settings = value; _failures.Clear(); }
    }

    public void Reset() { lock (_gate) _failures.Clear(); }

    public void Observe(ReceptionProvider provider, ProviderConnectionState state)
    {
        lock (_gate)
        {
            if (!IsEligible(provider)) return;
            if (state is ProviderConnectionState.Faulted or ProviderConnectionState.Reconnecting)
                _failures.TryAdd(provider, clock.UtcNow);
            // Connecting between retries is not recovery. Stale alone is not proof of failure.
            else if (state == ProviderConnectionState.Connected)
                _failures.Remove(provider);
        }
    }

    public DateTimeOffset? GetFailureSince(ReceptionProvider provider)
    {
        lock (_gate) return _failures.TryGetValue(provider, out var since) ? since : null;
    }

    public ProviderRoutingSettings GetRouting()
    {
        lock (_gate)
        {
            var route = _settings.Routing;
            if (!_settings.JmaXmlAutoFallback || _settings.Mode != ProviderMode.Production) return route;
            ReceptionProvider Resolve(ReceptionProvider provider) =>
                _failures.TryGetValue(provider, out var since) && clock.UtcNow - since >= FailureDelay
                    ? ReceptionProvider.JmaXml : provider;
            return route with
            {
                Quake = Resolve(route.Quake), Tsunami = Resolve(route.Tsunami),
                Weather = Resolve(route.Weather), Volcano = Resolve(route.Volcano),
                NankaiTrough = Resolve(route.NankaiTrough),
            };
        }
    }
}
