using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.P2P.Configuration;

namespace EEWTelop.Infrastructure.P2P.Recovery;

public sealed class P2pQuakeEnrichmentClient
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(7);

    private readonly object _gate = new();
    private readonly HttpClient _httpClient;
    private readonly ProviderOptions _options;
    private readonly IClock _clock;
    private long _lastRequestTimestamp;
    private bool _hasRequested;

    public P2pQuakeEnrichmentClient(
        HttpClient httpClient,
        ProviderOptions options,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _httpClient = httpClient;
        _options = options;
        _clock = clock;
    }

    public async Task<RawProviderMessage?> TryFetchAsync(
        EventId eventId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_hasRequested &&
                _clock.GetElapsedTime(_lastRequestTimestamp) < MinimumInterval)
            {
                return null;
            }

            _hasRequested = true;
            _lastRequestTimestamp = _clock.GetTimestamp();
        }

        string escapedId = Uri.EscapeDataString(eventId.Value);
        var requestUri = new Uri(
            $"{_options.RestBaseUri.ToString().TrimEnd('/')}/jma/quake/{escapedId}",
            UriKind.Absolute);
        string json = await _httpClient.GetStringAsync(requestUri, cancellationToken)
            .ConfigureAwait(false);
        SourceMode sourceMode = _options.Mode == EEWTelop.Application.Configuration.ProviderMode.Production
            ? SourceMode.Production
            : SourceMode.Sandbox;
        return new RawProviderMessage(
            "p2pquake",
            json,
            sourceMode,
            _clock.UtcNow);
    }
}
