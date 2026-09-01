using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Axis.Configuration;
using EEWTelop.Infrastructure.Axis.Recovery;

namespace EEWTelop.Infrastructure.Axis.Transport;

public sealed class AxisEventSource : IEventSource, IProviderConfigurableEventSource
{
    private const int MaximumFrameBytes = 24 * 1024 * 1024;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly IAppLogWriter _logWriter;
    private readonly HttpClient _httpClient;
    private readonly IAxisRecoveryClient _recoveryClient;
    private AxisProviderOptions _options;
    private CancellationTokenSource? _readerCancellation;
    private CancellationTokenSource? _attemptCancellation;
    private ProviderConnectionSnapshot _connection;
    private int _readerActive;
    private DateTimeOffset? _lastProviderMessageAt;
    private DateTimeOffset? _lastConnectedAt;
    private bool _disposed;

    public AxisEventSource(
        AxisProviderOptions options,
        IClock clock,
        IAppLogWriter logWriter)
        : this(options, clock, logWriter, new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
    {
    }

    internal AxisEventSource(
        AxisProviderOptions options,
        IClock clock,
        IAppLogWriter logWriter,
        HttpClient httpClient,
        IAxisRecoveryClient? recoveryClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logWriter = logWriter ?? throw new ArgumentNullException(nameof(logWriter));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _recoveryClient = recoveryClient ??
            new AxisJmaAtomRecoveryClient(_httpClient, clock, logWriter);
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
        AxisProviderOptions updated = AxisProviderOptions.FromSettings(settings);
        IReadOnlyList<string> errors = updated.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _readerActive) != 0)
            {
                throw new InvalidOperationException(
                    "The AXIS provider cannot be changed while reception is active.");
            }

            _options = updated;
        }
    }

    public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _readerActive, 1) != 0)
        {
            throw new InvalidOperationException("Only one AXIS reader is allowed.");
        }

        using var stop = new CancellationTokenSource();
        lock (_gate)
        {
            _readerCancellation = stop;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            stop.Token);
        CancellationToken runToken = lifetime.Token;
        int retry = 0;
        int serverOffset = 0;
        bool terminalFault = false;
        try
        {
            while (!runToken.IsCancellationRequested)
            {
                AxisProviderOptions options;
                lock (_gate)
                {
                    options = _options;
                }

                IReadOnlyList<string> validationErrors = options.Validate();
                if (validationErrors.Count > 0)
                {
                    terminalFault = true;
                    Transition(ProviderConnectionState.Faulted, string.Join(" ", validationErrors));
                    break;
                }

                Transition(
                    retry == 0 ? ProviderConnectionState.Connecting : ProviderConnectionState.Reconnecting,
                    "AXIS server discovery");
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                lock (_gate)
                {
                    _attemptCancellation = attempt;
                }

                await using (IAsyncEnumerator<RawProviderMessage> attemptReader =
                    ReadSelectedAttemptAsync(options, serverOffset++, attempt.Token)
                        .GetAsyncEnumerator(runToken))
                {
                    while (true)
                    {
                        bool hasMessage;
                        try
                        {
                            hasMessage = await attemptReader.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (runToken.IsCancellationRequested ||
                            attempt.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (AxisApiException exception) when (exception.StatusCode is 401 or 402 or 403)
                        {
                            terminalFault = true;
                            Transition(ProviderConnectionState.Faulted, exception.Message);
                            await LogAsync(
                                AppLogLevel.Error,
                                "AxisAuthorizationFailed",
                                "AXIS contract or access-token validation failed.",
                                exception,
                                runToken).ConfigureAwait(false);
                            break;
                        }
                        catch (Exception exception) when (exception is not StackOverflowException and
                            not OutOfMemoryException)
                        {
                            await LogAsync(
                                AppLogLevel.Warning,
                                "AxisReceptionFailed",
                                "AXIS reception stopped and will reconnect.",
                                exception,
                                runToken).ConfigureAwait(false);
                            break;
                        }

                        if (!hasMessage)
                        {
                            break;
                        }

                        retry = 0;
                        yield return attemptReader.Current;
                    }
                }

                lock (_gate)
                {
                    if (ReferenceEquals(_attemptCancellation, attempt))
                    {
                        _attemptCancellation = null;
                    }
                }

                if (terminalFault || runToken.IsCancellationRequested)
                {
                    break;
                }

                retry++;
                double backoff = Math.Min(60, Math.Pow(2, Math.Min(retry, 6)));
                // Small jitter prevents multiple clients from reconnecting in lockstep.
                TimeSpan delay = TimeSpan.FromMilliseconds(
                    (backoff * 1000) + Random.Shared.Next(0, 1000));
                Transition(ProviderConnectionState.Reconnecting, "Backoff", retryDelay: delay);
                try
                {
                    await Task.Delay(delay, runToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_readerCancellation, stop))
                {
                    _readerCancellation = null;
                }
            }

            Interlocked.Exchange(ref _readerActive, 0);
            if (!terminalFault)
            {
                Transition(ProviderConnectionState.Stopped, "Manual stop");
            }
        }
    }

    private async IAsyncEnumerable<RawProviderMessage> ReadSelectedAttemptAsync(
        AxisProviderOptions options,
        int serverOffset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var api = new AxisApiClient(_httpClient, options);
        IReadOnlyList<Uri> servers = await api
            .GetServersAsync(cancellationToken)
            .ConfigureAwait(false);
        Uri server = servers[Math.Abs(serverOffset % servers.Count)];
        await foreach (RawProviderMessage message in ReadAttemptAsync(
            server,
            options,
            cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    private async IAsyncEnumerable<RawProviderMessage> ReadAttemptAsync(
        Uri server,
        AxisProviderOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(
            "Authorization",
            new AuthenticationHeaderValue("Bearer", options.AccessToken).ToString());
        await socket.ConnectAsync(server, cancellationToken).ConfigureAwait(false);

        string? greeting = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
        if (greeting is null ||
            AxisEnvelopeDecoder.Decode(greeting, options.Channel).Kind != AxisFrameKind.Hello)
        {
            throw new InvalidDataException("AXIS did not send the required hello greeting.");
        }

        DateTimeOffset connectedAt = _clock.UtcNow;
        DateTimeOffset? recoverySince = _lastProviderMessageAt ?? _lastConnectedAt;
        _lastConnectedAt = connectedAt;
        Transition(
            ProviderConnectionState.Connected,
            $"AXIS {options.Channel} connected",
            connectedAt);
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task heartbeat = SendHeartbeatLoopAsync(socket, heartbeatCancellation.Token);
        // Never replay old telegrams on the first application connection.
        // Gap recovery is reserved for a reconnect after a known connected
        // interval, preventing stale warnings from appearing at startup.
        Task<IReadOnlyList<RawProviderMessage>> recovery = recoverySince is null
            ? Task.FromResult<IReadOnlyList<RawProviderMessage>>([])
            : FetchRecoveryAsync(
                recoverySince.Value,
                options,
                recoveryCancellation.Token);
        bool recoveryEmitted = false;
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                Task<string?> receive = ReceiveTextAsync(socket, cancellationToken);
                if (!recoveryEmitted &&
                    await Task.WhenAny(receive, recovery).ConfigureAwait(false) == recovery)
                {
                    foreach (RawProviderMessage recovered in await recovery.ConfigureAwait(false))
                    {
                        yield return recovered;
                    }

                    recoveryEmitted = true;
                }

                string? payload = await receive.ConfigureAwait(false);
                if (payload is null)
                {
                    if (!recoveryEmitted)
                    {
                        foreach (RawProviderMessage recovered in
                            await recovery.ConfigureAwait(false))
                        {
                            yield return recovered;
                        }

                        recoveryEmitted = true;
                    }

                    break;
                }

                AxisDecodedFrame frame;
                try
                {
                    frame = AxisEnvelopeDecoder.Decode(payload, options.Channel);
                }
                catch (Exception exception) when (exception is JsonException or
                    InvalidDataException or FormatException)
                {
                    await LogAsync(
                        AppLogLevel.Warning,
                        "AxisFrameRejected",
                        "A malformed AXIS frame was rejected.",
                        exception,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (frame.Kind != AxisFrameKind.Data || frame.ProviderPayload is null)
                {
                    continue;
                }

                if (!AxisWeatherTelegramPolicy.IsAssignedToAxis(
                    frame.Channel,
                    frame.TelegramType))
                {
                    bool isUnsupportedVolcanoTelegram = string.Equals(
                        frame.Channel,
                        AxisProviderOptions.VolcanologyChannel,
                        StringComparison.OrdinalIgnoreCase);
                    await LogAsync(
                        AppLogLevel.Debug,
                        isUnsupportedVolcanoTelegram
                            ? "AxisUnsupportedVolcanoTelegramSuppressed"
                            : "AxisTelegramRouteSuppressed",
                        isUnsupportedVolcanoTelegram
                            ? $"未対応のAXIS火山電文を除外しました。channel={frame.Channel} type={frame.TelegramType}"
                            : $"AXIS telegram assigned to the P2P route was suppressed. " +
                              $"channel={frame.Channel} type={frame.TelegramType}",
                        exception: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!AxisWeatherTelegramPolicy.ShouldAccept(frame.TelegramType))
                {
                    await LogAsync(
                        AppLogLevel.Debug,
                        "AxisLegacyWeatherTelegramSuppressed",
                        $"Legacy AXIS weather telegram was discarded. type={frame.TelegramType}",
                        exception: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    continue;
                }

                DateTimeOffset receivedAt = _clock.UtcNow;
                _lastProviderMessageAt = receivedAt;
                Transition(
                    ProviderConnectionState.Connected,
                    $"AXIS {frame.Channel}",
                    receivedAt);
                yield return new RawProviderMessage(
                    AxisProviderOptions.ProviderName,
                    frame.ProviderPayload,
                    frame.IsTest ? SourceMode.Sandbox : SourceMode.Production,
                    receivedAt)
                {
                    ContentFormat = frame.ContentFormat,
                    TransportPayload = payload,
                    TransportContentFormat = RawProviderContentFormat.Json,
                };
            }
        }
        finally
        {
            heartbeatCancellation.Cancel();
            recoveryCancellation.Cancel();
            try
            {
                await Task.WhenAll(heartbeat, recovery).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or
                WebSocketException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task<IReadOnlyList<RawProviderMessage>> FetchRecoveryAsync(
        DateTimeOffset since,
        AxisProviderOptions options,
        CancellationToken cancellationToken)
    {
        var recovered = new List<RawProviderMessage>();
        try
        {
            await foreach (RawProviderMessage message in _recoveryClient
                .FetchRecentAsync(since, options, cancellationToken)
                .ConfigureAwait(false))
            {
                string telegramType = AxisWeatherTelegramPolicy.ReadTelegramType(
                    message.Payload);
                if (AxisWeatherTelegramPolicy.IsAssignedRecoveryTelegram(telegramType) &&
                    AxisWeatherTelegramPolicy.ShouldAccept(telegramType))
                {
                    recovered.Add(message);
                }
            }

            await LogAsync(
                AppLogLevel.Information,
                "AxisGapRecoveryCompleted",
                $"AXIS start/reconnect recovery completed. messages={recovered.Count}",
                exception: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not StackOverflowException and
            not OutOfMemoryException)
        {
            await LogAsync(
                AppLogLevel.Warning,
                "AxisGapRecoveryFailed",
                "AXIS was connected, but the one-shot JMA Atom gap recovery failed. Live reception continues.",
                exception,
                cancellationToken).ConfigureAwait(false);
        }

        return recovered;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? reader;
        lock (_gate)
        {
            reader = _readerCancellation;
        }

        reader?.Cancel();
        return ValueTask.CompletedTask;
    }

    public void RequestReconnect(ReconnectReason reason)
    {
        CancellationTokenSource? attempt;
        lock (_gate)
        {
            attempt = _attemptCancellation;
        }

        attempt?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }

    private static async Task SendHeartbeatLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendTextAsync(socket, "hb", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using var payload = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket
                .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("AXIS sent a non-text WebSocket frame.");
            }

            if (payload.Length + result.Count > MaximumFrameBytes)
            {
                throw new InvalidDataException("AXIS frame exceeded the safety limit.");
            }

            payload.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return new UTF8Encoding(false, true).GetString(payload.ToArray());
            }
        }
    }

    private static Task SendTextAsync(
        ClientWebSocket socket,
        string text,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
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

    private ValueTask LogAsync(
        AppLogLevel level,
        string eventName,
        string message,
        Exception? exception,
        CancellationToken cancellationToken) => _logWriter.WriteAsync(
            new AppLogEntry(_clock.UtcNow, level, eventName, message, exception),
            cancellationToken);
}
