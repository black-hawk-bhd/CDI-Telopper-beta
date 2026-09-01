using System.Runtime.CompilerServices;
using EEWTelop.Application.Configuration;

namespace EEWTelop.Application.Events;

/// <summary>
/// Selects exactly one live provider for a reception run. It never merges streams,
/// so switching between P2P and DMDATA.JP is explicit and mutually exclusive.
/// </summary>
public sealed class ExclusiveProviderEventSource : IEventSource, IProviderConfigurableEventSource
{
    private readonly object _gate = new();
    private readonly IReadOnlyDictionary<ReceptionProvider, IEventSource> _sources;
    private IEventSource _activeSource;
    private int _readerActive;
    private bool _disposed;

    public ExclusiveProviderEventSource(
        ReceptionProvider selectedProvider,
        IReadOnlyDictionary<ReceptionProvider, IEventSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (!sources.TryGetValue(selectedProvider, out IEventSource? source))
        {
            throw new ArgumentException(
                $"No event source is registered for {selectedProvider}.",
                nameof(selectedProvider));
        }

        _sources = sources;
        _activeSource = source;
        foreach (IEventSource registeredSource in sources.Values.Distinct())
        {
            registeredSource.ConnectionChanged += OnSourceConnectionChanged;
        }
    }

    public ProviderConnectionSnapshot Connection
    {
        get
        {
            lock (_gate)
            {
                return _activeSource.Connection;
            }
        }
    }

    public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged;

    public void ConfigureProvider(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (Volatile.Read(ref _readerActive) != 0)
            {
                throw new InvalidOperationException(
                    "Reception provider cannot be switched while reception is active.");
            }

            if (!_sources.TryGetValue(settings.ReceptionProvider, out IEventSource? selected))
            {
                throw new ArgumentException(
                    $"No event source is registered for {settings.ReceptionProvider}.",
                    nameof(settings));
            }

            if (selected is IProviderConfigurableEventSource configurable)
            {
                configurable.ConfigureProvider(settings);
            }

            _activeSource = selected;
        }
    }

    public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _readerActive, 1) != 0)
        {
            throw new InvalidOperationException("Only one exclusive provider reader is allowed.");
        }

        IEventSource selected;
        lock (_gate)
        {
            selected = _activeSource;
        }

        try
        {
            await foreach (RawProviderMessage message in selected
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _readerActive, 0);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        IEventSource[] sources = _sources.Values.Distinct().ToArray();
        foreach (IEventSource source in sources)
        {
            await source.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void RequestReconnect(ReconnectReason reason)
    {
        IEventSource selected;
        lock (_gate)
        {
            selected = _activeSource;
        }

        selected.RequestReconnect(reason);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IEventSource source in _sources.Values.Distinct())
        {
            source.ConnectionChanged -= OnSourceConnectionChanged;
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnSourceConnectionChanged(
        object? sender,
        ProviderConnectionSnapshot snapshot)
    {
        bool isSelected;
        lock (_gate)
        {
            isSelected = ReferenceEquals(sender, _activeSource);
        }

        if (isSelected)
        {
            ConnectionChanged?.Invoke(this, snapshot);
        }
    }
}
