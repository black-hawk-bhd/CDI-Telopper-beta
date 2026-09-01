using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.P2P.Configuration;
using EEWTelop.Infrastructure.P2P.Recovery;

namespace EEWTelop.Infrastructure.P2P.Transport;

public sealed record P2pEventSourceOptions(
    int MaximumMessageBytes,
    TimeSpan StaleAfter,
    TimeSpan StableConnectionTime)
{
    public static P2pEventSourceOptions Default { get; } = new(
        MaximumMessageBytes: 1024 * 1024,
        StaleAfter: TimeSpan.FromSeconds(90),
        StableConnectionTime: TimeSpan.FromSeconds(30));

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumMessageBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StaleAfter, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StableConnectionTime, TimeSpan.Zero);
    }
}

public sealed class P2pEventSource : IEventSource, IProviderConfigurableEventSource
{
    private readonly object _gate = new();
    private ProviderOptions _providerOptions;
    private readonly P2pEventSourceOptions _sourceOptions;
    private readonly IClock _clock;
    private readonly IAppLogWriter _logWriter;
    private readonly IProviderWebSocketFactory _socketFactory;
    private IP2pRestRecoveryClient _recoveryClient;
    private readonly ReconnectDelayPolicy _reconnectDelayPolicy;
    private readonly IAsyncDelay _delay;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly HttpClient? _ownedHttpClient;
    private CancellationTokenSource? _activeCancellation;
    private CancellationTokenSource? _readerCancellation;
    private CancellationTokenSource? _retryCancellation;
    private ProviderConnectionSnapshot _connection;
    private ReconnectReason? _requestedReconnect;
    private int _readerActive;
    private bool _disposed;

    public P2pEventSource(
        ProviderOptions providerOptions,
        IClock clock,
        IAppLogWriter logWriter,
        P2pEventSourceOptions? sourceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(providerOptions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logWriter);
        _ownedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _providerOptions = providerOptions;
        _clock = clock;
        _logWriter = logWriter;
        _sourceOptions = sourceOptions ?? P2pEventSourceOptions.Default;
        _sourceOptions.Validate();
        _socketFactory = new ClientWebSocketFactory();
        _recoveryClient = new P2pRestRecoveryClient(_ownedHttpClient, providerOptions, clock);
        _reconnectDelayPolicy = new ReconnectDelayPolicy(new RandomJitterSource());
        _delay = new SystemAsyncDelay();
        _connection = new ProviderConnectionSnapshot(
            ProviderConnectionState.Stopped,
            clock.UtcNow,
            Detail: "Manual stop");
    }

    internal P2pEventSource(
        ProviderOptions providerOptions,
        P2pEventSourceOptions sourceOptions,
        IClock clock,
        IAppLogWriter logWriter,
        IProviderWebSocketFactory socketFactory,
        IP2pRestRecoveryClient recoveryClient,
        ReconnectDelayPolicy reconnectDelayPolicy,
        IAsyncDelay delay)
    {
        ArgumentNullException.ThrowIfNull(providerOptions);
        ArgumentNullException.ThrowIfNull(sourceOptions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logWriter);
        ArgumentNullException.ThrowIfNull(socketFactory);
        ArgumentNullException.ThrowIfNull(recoveryClient);
        ArgumentNullException.ThrowIfNull(reconnectDelayPolicy);
        ArgumentNullException.ThrowIfNull(delay);
        sourceOptions.Validate();
        _providerOptions = providerOptions;
        _sourceOptions = sourceOptions;
        _clock = clock;
        _logWriter = logWriter;
        _socketFactory = socketFactory;
        _recoveryClient = recoveryClient;
        _reconnectDelayPolicy = reconnectDelayPolicy;
        _delay = delay;
        _connection = new ProviderConnectionSnapshot(
            ProviderConnectionState.Stopped,
            clock.UtcNow,
            Detail: "Manual stop");
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

    public void ConfigureProvider(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ProviderOptions updated = ProviderOptions.FromSettings(settings);
        IReadOnlyList<string> validationErrors = updated.Validate();
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", validationErrors), nameof(settings));
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _readerActive) != 0)
            {
                throw new InvalidOperationException(
                    "The provider cannot be changed while reception is active.");
            }

            _providerOptions = updated;
            if (_ownedHttpClient is not null)
            {
                _recoveryClient = new P2pRestRecoveryClient(
                    _ownedHttpClient,
                    updated,
                    _clock);
            }
        }
    }

    public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _readerActive, 1) != 0)
        {
            throw new InvalidOperationException("Only one active event-source reader is allowed.");
        }

        using var readerCancellation = new CancellationTokenSource();
        SetReaderCancellation(readerCancellation);
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token,
            readerCancellation.Token);
        CancellationToken runToken = runCancellation.Token;
        int retryCount = 0;
        bool connectedBefore = false;
        DateTimeOffset recoveryCursor = default;

        try
        {
            IReadOnlyList<string> validationErrors = _providerOptions.Validate();
            if (validationErrors.Count > 0)
            {
                Transition(
                    ProviderConnectionState.Faulted,
                    string.Join(" ", validationErrors));
                yield break;
            }

            while (!runToken.IsCancellationRequested)
            {
                ReconnectReason? reconnectReason = TakeRequestedReconnect();
                Transition(
                    connectedBefore || retryCount > 0
                        ? ProviderConnectionState.Reconnecting
                        : ProviderConnectionState.Connecting,
                    reconnectReason?.ToString());

                IProviderWebSocket socket = _socketFactory.Create();
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                SetActiveCancellation(attemptCancellation);
                Exception? connectFailure = null;
                try
                {
                    await socket.ConnectAsync(
                        _providerOptions.WebSocketUri,
                        attemptCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    connectFailure = exception;
                }

                if (connectFailure is not null)
                {
                    await socket.DisposeAsync().ConfigureAwait(false);
                    ClearActiveCancellation(attemptCancellation);
                    if (runToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (attemptCancellation.IsCancellationRequested)
                    {
                        continue;
                    }

                    if (IsPermanentFailure(connectFailure))
                    {
                        Transition(ProviderConnectionState.Faulted, connectFailure.Message);
                        await LogAsync(
                            AppLogLevel.Error,
                            "P2pConnectionFaulted",
                            "The P2P connection cannot continue.",
                            connectFailure,
                            runToken).ConfigureAwait(false);
                        yield break;
                    }

                    await LogAsync(
                        AppLogLevel.Warning,
                        "P2pConnectFailed",
                        "The P2P WebSocket connection failed and will be retried.",
                        connectFailure,
                        runToken).ConfigureAwait(false);
                    if (!await WaitForRetryAsync(retryCount, runToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    retryCount = IncrementRetryCount(retryCount);
                    continue;
                }

                long connectedTimestamp = _clock.GetTimestamp();
                DateTimeOffset connectedAt = _clock.UtcNow;
                Transition(ProviderConnectionState.Connected, "WebSocket connected");

                if (connectedBefore)
                {
                    DateTimeOffset recoveryWindowEnd = connectedAt;
                    IReadOnlyList<RawProviderMessage> recovered = await FetchRecoveryAsync(
                            recoveryCursor,
                            runToken)
                        .ConfigureAwait(false);
                    foreach (RawProviderMessage message in recovered)
                    {
                        yield return message;
                    }

                    recoveryCursor = recoveryWindowEnd;

                    await LogAsync(
                        AppLogLevel.Information,
                        "EewRecoveryUnavailable",
                        "Code 556 EEW messages cannot be recovered after a disconnect.",
                        null,
                        runToken).ConfigureAwait(false);
                }
                else
                {
                    // Anything issued before the first successful connection is history, not
                    // a reconnect gap. It must never be replayed as a newly received alert.
                    recoveryCursor = connectedAt;
                }

                connectedBefore = true;
                bool reconnectRequested = false;
                while (!runToken.IsCancellationRequested)
                {
                    ProviderSocketMessage? socketMessage = null;
                    Exception? receiveFailure = null;
                    try
                    {
                        socketMessage = await socket.ReceiveAsync(
                            _sourceOptions.MaximumMessageBytes,
                            attemptCancellation.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not StackOverflowException)
                    {
                        receiveFailure = exception;
                    }

                    if (runToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (attemptCancellation.IsCancellationRequested)
                    {
                        reconnectRequested = true;
                        break;
                    }

                    if (receiveFailure is not null)
                    {
                        await LogAsync(
                            AppLogLevel.Warning,
                            "P2pReceiveFailed",
                            "The P2P WebSocket receive loop stopped and will reconnect.",
                            receiveFailure,
                            runToken).ConfigureAwait(false);
                        break;
                    }

                    if (socketMessage is null || socketMessage.IsClosed)
                    {
                        break;
                    }

                    if (socketMessage.RejectionReason is not null)
                    {
                        await LogAsync(
                            AppLogLevel.Warning,
                            "P2pMessageRejected",
                            socketMessage.RejectionReason,
                            null,
                            runToken).ConfigureAwait(false);
                        continue;
                    }

                    DateTimeOffset receivedAt = _clock.UtcNow;
                    recoveryCursor = receivedAt;
                    Transition(
                        ProviderConnectionState.Connected,
                        "Message received",
                        lastReceivedAt: receivedAt);
                    yield return new RawProviderMessage(
                        "p2pquake",
                        socketMessage.Json!,
                        GetSourceMode(),
                        receivedAt);
                }

                await socket.DisposeAsync().ConfigureAwait(false);
                ClearActiveCancellation(attemptCancellation);
                if (runToken.IsCancellationRequested)
                {
                    break;
                }

                if (_clock.GetElapsedTime(connectedTimestamp) >= _sourceOptions.StableConnectionTime)
                {
                    retryCount = 0;
                }

                if (reconnectRequested || TakeRequestedReconnect() is not null)
                {
                    continue;
                }

                if (!await WaitForRetryAsync(retryCount, runToken).ConfigureAwait(false))
                {
                    break;
                }

                retryCount = IncrementRetryCount(retryCount);
            }
        }
        finally
        {
            CancelCurrentOperations();
            ClearReaderCancellation(readerCancellation);
            Transition(ProviderConnectionState.Stopped, "Manual stop");
            Volatile.Write(ref _readerActive, 0);
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? reader;
        lock (_gate)
        {
            reader = _readerCancellation;
            _requestedReconnect = null;
        }

        TryCancel(reader);
        CancelCurrentOperations();
        Transition(ProviderConnectionState.Stopped, "Manual stop");
        return ValueTask.CompletedTask;
    }

    public void RequestReconnect(ReconnectReason reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _requestedReconnect = reason;
        }

        CancelCurrentOperations();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        await StopAsync().ConfigureAwait(false);
        _lifetimeCancellation.Dispose();
        _ownedHttpClient?.Dispose();
    }

    private async ValueTask<IReadOnlyList<RawProviderMessage>> FetchRecoveryAsync(
        DateTimeOffset issuedAfter,
        CancellationToken cancellationToken)
    {
        var messages = new List<RawProviderMessage>(10);
        try
        {
            await foreach (RawProviderMessage message in _recoveryClient
                .FetchRecentAsync(issuedAfter, cancellationToken).ConfigureAwait(false))
            {
                messages.Add(message);
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            exception is not StackOverflowException)
        {
            await LogAsync(
                AppLogLevel.Warning,
                "P2pRecoveryFailed",
                "REST recovery for code 551/552 failed; live reception will continue.",
                exception,
                cancellationToken).ConfigureAwait(false);
        }

        return messages;
    }

    private async ValueTask<bool> WaitForRetryAsync(
        int retryCount,
        CancellationToken cancellationToken)
    {
        TimeSpan retryDelay = _reconnectDelayPolicy.GetDelay(retryCount);
        Transition(
            ProviderConnectionState.Reconnecting,
            $"Retry {retryCount + 1}",
            retryDelay: retryDelay);
        using var retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetRetryCancellation(retryCancellation);
        try
        {
            await _delay.DelayAsync(retryDelay, retryCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            ClearRetryCancellation(retryCancellation);
        }
    }

    private SourceMode GetSourceMode() => _providerOptions.Mode == EEWTelop.Application.Configuration.ProviderMode.Production
        ? SourceMode.Production
        : SourceMode.Sandbox;

    private static bool IsPermanentFailure(Exception exception) =>
        exception is ArgumentException or AuthenticationException ||
        exception is WebSocketException { InnerException: AuthenticationException };

    private static int IncrementRetryCount(int retryCount) =>
        retryCount == int.MaxValue ? retryCount : retryCount + 1;

    private async ValueTask LogAsync(
        AppLogLevel level,
        string eventName,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await _logWriter.WriteAsync(
                new AppLogEntry(_clock.UtcNow, level, eventName, message, exception),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Transition(
        ProviderConnectionState state,
        string? detail,
        DateTimeOffset? lastReceivedAt = null,
        TimeSpan? retryDelay = null)
    {
        ProviderConnectionSnapshot snapshot;
        lock (_gate)
        {
            snapshot = new ProviderConnectionSnapshot(
                state,
                _clock.UtcNow,
                lastReceivedAt ?? _connection.LastReceivedAt,
                retryDelay,
                detail);
            _connection = snapshot;
        }

        ConnectionChanged?.Invoke(this, snapshot);
    }

    private ReconnectReason? TakeRequestedReconnect()
    {
        lock (_gate)
        {
            ReconnectReason? reason = _requestedReconnect;
            _requestedReconnect = null;
            return reason;
        }
    }

    private void SetActiveCancellation(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            _activeCancellation = cancellation;
        }
    }

    private void SetReaderCancellation(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            _readerCancellation = cancellation;
        }
    }

    private void ClearReaderCancellation(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (_readerCancellation == cancellation)
            {
                _readerCancellation = null;
            }
        }
    }

    private void ClearActiveCancellation(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (_activeCancellation == cancellation)
            {
                _activeCancellation = null;
            }
        }
    }

    private void SetRetryCancellation(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            _retryCancellation = cancellation;
        }
    }

    private void ClearRetryCancellation(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (_retryCancellation == cancellation)
            {
                _retryCancellation = null;
            }
        }
    }

    private void CancelCurrentOperations()
    {
        CancellationTokenSource? active;
        CancellationTokenSource? retry;
        lock (_gate)
        {
            active = _activeCancellation;
            retry = _retryCancellation;
        }

        TryCancel(active);
        TryCancel(retry);
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
