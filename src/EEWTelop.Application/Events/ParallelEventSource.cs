using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Operations;

namespace EEWTelop.Application.Events;

/// <summary>
/// Reads several providers concurrently and merges only the messages assigned
/// to each provider branch. A branch failure does not stop the remaining
/// providers, which keeps the independently operated reception paths useful.
/// </summary>
public sealed class ParallelEventSource : IEventSource, IProviderConfigurableEventSource,
    IProviderConnectionDiagnostics
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<ParallelEventSourceBranch> _branches;
    private readonly Dictionary<IEventSource, ProviderConnectionSnapshot> _connections;
    private ProviderConnectionSnapshot _connection;
    private int _readerActive;
    private bool _disposed;

    public ParallelEventSource(IReadOnlyList<ParallelEventSourceBranch> branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Count < 2)
        {
            throw new ArgumentException(
                "At least two provider branches are required.",
                nameof(branches));
        }

        if (branches.Any(static branch => branch is null ||
            string.IsNullOrWhiteSpace(branch.Name) || branch.Source is null))
        {
            throw new ArgumentException(
                "Every provider branch requires a name and event source.",
                nameof(branches));
        }

        if (branches.Select(static branch => branch.Source).Distinct().Count() != branches.Count)
        {
            throw new ArgumentException(
                "A provider source can be registered only once.",
                nameof(branches));
        }

        _branches = branches.ToArray();
        _connections = _branches.ToDictionary(
            static branch => branch.Source,
            static branch => branch.Source.Connection);
        foreach (ParallelEventSourceBranch branch in _branches)
        {
            branch.Source.ConnectionChanged += OnSourceConnectionChanged;
        }

        _connection = AggregateConnection();
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
            return _branches.Select(branch => new ProviderBranchConnectionSnapshot(
                branch.Name,
                _connections[branch.Source])).ToArray();
        }
    }

    public void ConfigureProvider(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Volatile.Read(ref _readerActive) != 0)
        {
            throw new InvalidOperationException(
                "Parallel providers cannot be changed while reception is active.");
        }

        foreach (ParallelEventSourceBranch branch in _branches)
        {
            if (branch.Source is IProviderConfigurableEventSource configurable)
            {
                configurable.ConfigureProvider(settings);
            }
        }
    }

    public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _readerActive, 1) != 0)
        {
            throw new InvalidOperationException("Only one parallel provider reader is allowed.");
        }

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var channel = Channel.CreateUnbounded<RawProviderMessage>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        Task[] pumps = _branches
            .Select(branch => PumpAsync(branch, channel.Writer, runCancellation.Token))
            .ToArray();
        Task completion = CompleteWhenFinishedAsync(pumps, channel.Writer);

        try
        {
            await foreach (RawProviderMessage message in channel.Reader
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
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
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(_branches.Select(branch =>
            branch.Source.StopAsync(cancellationToken).AsTask())).ConfigureAwait(false);
    }

    public void RequestReconnect(ReconnectReason reason)
    {
        foreach (ParallelEventSourceBranch branch in _branches)
        {
            branch.Source.RequestReconnect(reason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (ParallelEventSourceBranch branch in _branches)
        {
            branch.Source.ConnectionChanged -= OnSourceConnectionChanged;
            await branch.Source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task PumpAsync(
        ParallelEventSourceBranch branch,
        ChannelWriter<RawProviderMessage> writer,
        CancellationToken cancellationToken)
    {
        await foreach (RawProviderMessage message in branch.Source
            .ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (branch.Accept(message))
            {
                await writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }
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
        if (sender is not IEventSource source)
        {
            return;
        }

        ProviderConnectionSnapshot aggregate;
        lock (_gate)
        {
            if (!_connections.ContainsKey(source))
            {
                return;
            }

            _connections[source] = snapshot;
            aggregate = AggregateConnection();
            _connection = aggregate;
        }

        ConnectionChanged?.Invoke(this, aggregate);
    }

    private ProviderConnectionSnapshot AggregateConnection()
    {
        ProviderConnectionSnapshot[] snapshots = _branches
            .Select(branch => _connections[branch.Source])
            .ToArray();
        ProviderConnectionState state = AggregateState(snapshots);
        DateTimeOffset changedAt = snapshots.Max(static snapshot => snapshot.ChangedAt);
        DateTimeOffset? lastReceivedAt = snapshots
            .Where(static snapshot => snapshot.LastReceivedAt is not null)
            .Select(static snapshot => snapshot.LastReceivedAt)
            .Max();
        TimeSpan? retryDelay = snapshots
            .Where(static snapshot => snapshot.RetryDelay is not null)
            .Select(static snapshot => snapshot.RetryDelay)
            .Min();
        string detail = string.Join(
            " / ",
            _branches.Select(branch =>
                $"{branch.Name}: {_connections[branch.Source].State}"));
        return new ProviderConnectionSnapshot(
            state,
            changedAt,
            lastReceivedAt,
            retryDelay,
            detail);
    }

    private static ProviderConnectionState AggregateState(
        IReadOnlyCollection<ProviderConnectionSnapshot> snapshots)
    {
        if (snapshots.Any(static snapshot => snapshot.State == ProviderConnectionState.Faulted))
        {
            return ProviderConnectionState.Faulted;
        }

        if (snapshots.All(static snapshot => snapshot.State is
            ProviderConnectionState.Connected or ProviderConnectionState.Stale))
        {
            return ProviderConnectionState.Connected;
        }

        if (snapshots.Any(static snapshot => snapshot.State == ProviderConnectionState.Reconnecting))
        {
            return ProviderConnectionState.Reconnecting;
        }

        if (snapshots.Any(static snapshot => snapshot.State == ProviderConnectionState.Connecting) ||
            snapshots.Any(static snapshot => snapshot.State is
                ProviderConnectionState.Connected or ProviderConnectionState.Stale))
        {
            return ProviderConnectionState.Connecting;
        }

        return ProviderConnectionState.Stopped;
    }
}

public sealed record ParallelEventSourceBranch(
    string Name,
    IEventSource Source,
    Func<RawProviderMessage, bool> Accept)
{
    public ParallelEventSourceBranch(string name, IEventSource source)
        : this(name, source, static _ => true)
    {
    }
}
