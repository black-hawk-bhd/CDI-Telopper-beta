using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Infrastructure.P2P.Recovery;
using EEWTelop.Infrastructure.P2P.Transport;

namespace EEWTelop.Infrastructure.P2P.Tests;

internal sealed class FakeClock : IClock
{
    private long _ticks;

    public DateTimeOffset UtcNow { get; private set; } =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    public long GetTimestamp() => _ticks;

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        TimeSpan.FromTicks(_ticks - startingTimestamp);

    public void Advance(TimeSpan elapsed)
    {
        _ticks += elapsed.Ticks;
        UtcNow += elapsed;
    }
}

internal sealed class FixedJitterSource(double value) : IJitterSource
{
    public double NextUnit() => value;
}

internal sealed class RecordingDelay : IAsyncDelay
{
    public List<TimeSpan> Delays { get; } = [];

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        return ValueTask.CompletedTask;
    }
}

internal sealed class MemoryLogWriter : IAppLogWriter
{
    public List<AppLogEntry> Entries { get; } = [];

    public ValueTask WriteAsync(
        AppLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entries.Add(entry);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeRecoveryClient(params RawProviderMessage[] messages)
    : IP2pRestRecoveryClient
{
    public int FetchCount { get; private set; }

    public DateTimeOffset? LastIssuedAfter { get; private set; }

    public async IAsyncEnumerable<RawProviderMessage> FetchRecentAsync(
        DateTimeOffset issuedAfter,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        FetchCount++;
        LastIssuedAfter = issuedAfter;
        foreach (RawProviderMessage message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
        }

        await Task.CompletedTask;
    }
}

internal sealed class FakeWebSocketFactory(params FakeWebSocket[] sockets)
    : IProviderWebSocketFactory
{
    private readonly Queue<FakeWebSocket> _sockets = new(sockets);

    public int CreateCount { get; private set; }

    public IProviderWebSocket Create()
    {
        CreateCount++;
        return _sockets.Dequeue();
    }
}

internal sealed class FakeWebSocket : IProviderWebSocket
{
    private readonly Queue<Func<CancellationToken, ValueTask<ProviderSocketMessage>>> _receives = [];
    private readonly Exception? _connectFailure;

    public FakeWebSocket(Exception? connectFailure = null)
    {
        _connectFailure = connectFailure;
    }

    public int ConnectCount { get; private set; }

    public Uri? ConnectedUri { get; private set; }

    public bool IsDisposed { get; private set; }

    public FakeWebSocket EnqueueMessage(string json)
    {
        _receives.Enqueue(_ => ValueTask.FromResult(new ProviderSocketMessage(json)));
        return this;
    }

    public FakeWebSocket EnqueueClose(Action? beforeClose = null)
    {
        _receives.Enqueue(_ =>
        {
            beforeClose?.Invoke();
            return ValueTask.FromResult(new ProviderSocketMessage(null, IsClosed: true));
        });
        return this;
    }

    public FakeWebSocket EnqueuePending(
        TaskCompletionSource<ProviderSocketMessage> completion)
    {
        _receives.Enqueue(cancellationToken =>
        {
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return new ValueTask<ProviderSocketMessage>(completion.Task);
        });
        return this;
    }

    public ValueTask ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        ConnectCount++;
        ConnectedUri = uri;
        return _connectFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(_connectFailure);
    }

    public ValueTask<ProviderSocketMessage> ReceiveAsync(
        int maximumMessageBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumMessageBytes, 1);
        cancellationToken.ThrowIfCancellationRequested();
        return _receives.Count > 0
            ? _receives.Dequeue()(cancellationToken)
            : ValueTask.FromResult(new ProviderSocketMessage(null, IsClosed: true));
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
