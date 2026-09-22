using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Operations;

namespace EEWTelop.Application.Events;

/// <summary>
/// Connects only the distinct providers required by the per-information route
/// settings and merges their raw streams. Provider-specific filtering occurs
/// after normalization in ProviderSelectionEventNormalizer.
/// </summary>
public sealed class RoutedProviderEventSource : IEventSource,
    IProviderConfigurableEventSource,
    IProviderConnectionDiagnostics
{
    private readonly object _gate = new();
    private readonly IReadOnlyDictionary<ReceptionProvider, IEventSource> _sources;
    private ReceptionProvider[] _selectedProviders;
    private ProviderConnectionSnapshot _connection;
    private int _readerActive;
    private bool _disposed;
    private readonly JmaFallbackRouting? _fallback;
    private CancellationTokenSource? _runCancellation;

    public RoutedProviderEventSource(
        ProviderSettings settings,
        IReadOnlyDictionary<ReceptionProvider, IEventSource> sources,
        JmaFallbackRouting? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one provider source is required.", nameof(sources));
        }

        _sources = sources;
        _fallback = sources.ContainsKey(ReceptionProvider.JmaXml) ? fallback : null;
        _selectedProviders = ResolveProviders(settings, sources, _fallback is not null);
        foreach (IEventSource source in sources.Values.Distinct())
        {
            source.ConnectionChanged += OnSourceConnectionChanged;
        }

        _connection = AggregateConnection(_selectedProviders);
    }

    public ProviderConnectionSnapshot Connection
    {
        get
        {
            lock (_gate)
            {
                return _connection;
            }
        }
    }

    public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged;

    public IReadOnlyList<ProviderBranchConnectionSnapshot> GetProviderConnections()
    {
        lock (_gate)
        {
            return _selectedProviders
                .Select(provider => new ProviderBranchConnectionSnapshot(
                    GetProviderName(provider),
                    _sources[provider].Connection))
                .ToArray();
        }
    }

    public void ConfigureProvider(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Volatile.Read(ref _readerActive) != 0)
        {
            throw new InvalidOperationException(
                "Information providers cannot be changed while reception is active.");
        }

        _fallback?.Configure(settings);
        ReceptionProvider[] selected = ResolveProviders(settings, _sources, _fallback is not null);
        foreach (ReceptionProvider provider in selected)
        {
            if (_sources[provider] is IProviderConfigurableEventSource configurable)
            {
                configurable.ConfigureProvider(settings);
            }
        }

        ProviderConnectionSnapshot snapshot;
        lock (_gate)
        {
            _selectedProviders = selected;
            snapshot = AggregateConnection(selected);
            _connection = snapshot;
        }

        ConnectionChanged?.Invoke(this, snapshot);
    }

    public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _readerActive, 1) != 0)
        {
            throw new InvalidOperationException("Only one routed provider reader is allowed.");
        }

        ReceptionProvider[] selected;
        lock (_gate)
        {
            _fallback?.Reset();
            selected = _selectedProviders.ToArray();
        }

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        lock (_gate) _runCancellation = runCancellation;
        var channel = Channel.CreateUnbounded<RawProviderMessage>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = selected.Length == 1,
                AllowSynchronousContinuations = false,
            });
        Task[] pumps = selected
            .Select(provider => PumpAsync(
                provider,
                _sources[provider],
                channel.Writer,
                runCancellation.Token))
            .ToArray();
        Task completion = CompleteWhenFinishedAsync(pumps, channel.Writer);

        try
        {
            await foreach (RawProviderMessage message in channel.Reader
                .ReadAllAsync(runCancellation.Token).ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            runCancellation.Cancel();
            try
            {
                await completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
            {
            }

            Interlocked.Exchange(ref _readerActive, 0);
            lock (_gate) _runCancellation = null;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) _runCancellation?.Cancel();
        IEventSource[] sources = _sources.Values.Distinct().ToArray();
        foreach (IEventSource source in sources)
        {
            await source.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void RequestReconnect(ReconnectReason reason)
    {
        ReceptionProvider[] selected;
        lock (_gate)
        {
            selected = _selectedProviders.ToArray();
        }

        foreach (ReceptionProvider provider in selected)
        {
            _sources[provider].RequestReconnect(reason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        foreach (IEventSource source in _sources.Values.Distinct())
        {
            source.ConnectionChanged -= OnSourceConnectionChanged;
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ReceptionProvider[] ResolveProviders(
        ProviderSettings settings,
        IReadOnlyDictionary<ReceptionProvider, IEventSource> sources,
        bool withFallback = false)
    {
        ReceptionProvider[] selected = settings.Routing.GetDistinctProviders().ToArray();
        var routing = settings.Routing;
        if (withFallback && settings.JmaXmlAutoFallback && settings.Mode == ProviderMode.Production &&
            new[] { routing.Quake, routing.Tsunami, routing.Weather, routing.Volcano, routing.NankaiTrough }
                .Any(JmaFallbackRouting.IsEligible))
            selected = selected.Append(ReceptionProvider.JmaXml).Distinct().ToArray();
        ReceptionProvider[] missing = selected.Where(provider => !sources.ContainsKey(provider))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                "No event source is registered for: " + string.Join(", ", missing),
                nameof(settings));
        }

        return selected;
    }

    private async Task PumpAsync(
        ReceptionProvider provider,
        IEventSource source,
        ChannelWriter<RawProviderMessage> writer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (RawProviderMessage message in source
                    .ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    await writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                if (_fallback is null) return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception) when (_fallback is not null) { }
            // A terminated source must not terminate the other providers or the backup.
            _fallback?.Observe(provider, ProviderConnectionState.Faulted);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CompleteWhenFinishedAsync(
        Task[] pumps,
        ChannelWriter<RawProviderMessage> writer)
    {
        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }

    private void OnSourceConnectionChanged(
        object? sender,
        ProviderConnectionSnapshot snapshot)
    {
        ProviderConnectionSnapshot aggregate;
        lock (_gate)
        {
            foreach (var provider in _selectedProviders.Where(provider => ReferenceEquals(_sources[provider], sender)))
                _fallback?.Observe(provider, snapshot.State);
            if (!_selectedProviders.Any(provider =>
                ReferenceEquals(_sources[provider], sender)))
            {
                return;
            }

            aggregate = AggregateConnection(_selectedProviders);
            _connection = aggregate;
        }

        ConnectionChanged?.Invoke(this, aggregate);
    }

    private ProviderConnectionSnapshot AggregateConnection(
        IReadOnlyCollection<ReceptionProvider> providers)
    {
        bool backupActive = _fallback?.GetRouting().GetDistinctProviders().Contains(ReceptionProvider.JmaXml) == true;
        if (_fallback is not null && !backupActive)
            providers = providers.Where(p => p != ReceptionProvider.JmaXml).ToArray();
        if (providers.Count == 0)
        {
            return new ProviderConnectionSnapshot(
                ProviderConnectionState.Stopped,
                DateTimeOffset.UtcNow,
                Detail: "No information providers selected");
        }

        ProviderConnectionSnapshot[] snapshots = providers
            .Select(provider => _sources[provider].Connection)
            .ToArray();
        if (snapshots.Length == 1)
        {
            return snapshots[0];
        }

        ProviderConnectionState state = snapshots.Any(static value =>
            value.State == ProviderConnectionState.Faulted)
                ? ProviderConnectionState.Faulted
                : snapshots.All(static value => value.State is
                    ProviderConnectionState.Connected or ProviderConnectionState.Stale)
                    ? ProviderConnectionState.Connected
                    : snapshots.Any(static value =>
                        value.State == ProviderConnectionState.Reconnecting)
                        ? ProviderConnectionState.Reconnecting
                        : snapshots.Any(static value => value.State is
                            ProviderConnectionState.Connecting or
                            ProviderConnectionState.Connected or
                            ProviderConnectionState.Stale)
                            ? ProviderConnectionState.Connecting
                            : ProviderConnectionState.Stopped;
        return new ProviderConnectionSnapshot(
            state,
            snapshots.Max(static value => value.ChangedAt),
            snapshots.Where(static value => value.LastReceivedAt is not null)
                .Select(static value => value.LastReceivedAt)
                .Max(),
            snapshots.Where(static value => value.RetryDelay is not null)
                .Select(static value => value.RetryDelay)
                .Min(),
            (backupActive ? "気象庁XML受信中（自動代替を含む・EEW対象外） / " : "") + string.Join(" / ", providers.Select(provider =>
                $"{GetProviderName(provider)}: {_sources[provider].Connection.State}")));
    }

    private static string GetProviderName(ReceptionProvider provider) => provider switch
    {
        ReceptionProvider.P2pQuake => "P2P",
        ReceptionProvider.Dmdata => "DMDATA.JP",
        ReceptionProvider.Axis => "AXIS",
        ReceptionProvider.Wolfx => "Wolfx",
        ReceptionProvider.JmaXml => "気象庁XML",
        _ => provider.ToString(),
    };
}
