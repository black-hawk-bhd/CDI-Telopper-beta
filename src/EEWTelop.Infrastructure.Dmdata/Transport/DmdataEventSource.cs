using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.Configuration;
using EEWTelop.Infrastructure.Dmdata.Security;

namespace EEWTelop.Infrastructure.Dmdata.Transport;

public sealed class DmdataEventSource : IEventSource, IProviderConfigurableEventSource
{
    private const int MaximumFrameBytes = 24 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly IAppLogWriter _logWriter;
    private readonly HttpClient _httpClient;
    private readonly bool _allowExtendedCategories;
    private DmdataProviderOptions _options;
    private CancellationTokenSource? _readerCancellation;
    private CancellationTokenSource? _attemptCancellation;
    private ProviderConnectionSnapshot _connection;
    private int _readerActive;
    private bool _disposed;

    public DmdataEventSource(
        DmdataProviderOptions options,
        IClock clock,
        IAppLogWriter logWriter,
        bool allowExtendedCategories = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logWriter);
        _options = options;
        _clock = clock;
        _logWriter = logWriter;
        _allowExtendedCategories = allowExtendedCategories;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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
        DmdataProviderOptions updated = DmdataProviderOptions.FromSettings(
            settings,
            _allowExtendedCategories);
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
                    "The DMDATA.JP provider cannot be changed while reception is active.");
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
            throw new InvalidOperationException("Only one DMDATA.JP reader is allowed.");
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
        bool terminalFault = false;
        try
        {
            while (!runToken.IsCancellationRequested)
            {
                DmdataProviderOptions options;
                lock (_gate)
                {
                    options = _options;
                }

                IReadOnlyList<string> errors = options.Validate();
                if (errors.Count > 0)
                {
                    Transition(ProviderConnectionState.Faulted, string.Join(" ", errors));
                    yield break;
                }

                Transition(
                    retry == 0 ? ProviderConnectionState.Connecting : ProviderConnectionState.Reconnecting,
                    "DMDATA.JP raw XML");
                var credentialProvider = new FixedDmdataCredentialProvider(
                    options.Credential,
                    options.AuthenticationMode);
                var socketApi = new DmdataSocketApiClient(_httpClient, options, credentialProvider);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                lock (_gate)
                {
                    _attemptCancellation = attempt;
                }

                await using (IAsyncEnumerator<RawProviderMessage> attemptReader =
                    ReadAttemptAsync(socketApi, attempt, runToken)
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
                        catch (DmdataApiException exception) when (exception.StatusCode is 401 or 402 or 403)
                        {
                            terminalFault = true;
                            string authorizationFailure = DescribeAuthorizationFailure(exception);
                            Transition(
                                ProviderConnectionState.Faulted,
                                authorizationFailure);
                            await LogAsync(
                                AppLogLevel.Error,
                                "DmdataAuthorizationFailed",
                                authorizationFailure,
                                exception,
                                runToken).ConfigureAwait(false);
                            break;
                        }
                        catch (InvalidOperationException exception)
                        {
                            terminalFault = true;
                            Transition(ProviderConnectionState.Faulted, exception.Message);
                            await LogAsync(
                                AppLogLevel.Error,
                                "DmdataCredentialMissing",
                                "DMDATA.JP credential configuration is incomplete.",
                                exception,
                                runToken).ConfigureAwait(false);
                            break;
                        }
                        catch (Exception exception) when (exception is not StackOverflowException)
                        {
                            await LogAsync(
                                AppLogLevel.Error,
                                "DmdataReceptionFailed",
                                "DMDATA.JP raw XML reception failed; reconnecting.",
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

                if (runToken.IsCancellationRequested)
                {
                    break;
                }

                if (terminalFault)
                {
                    break;
                }

                retry++;
                TimeSpan delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(retry, 5))));
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

    private async IAsyncEnumerable<RawProviderMessage> ReadAttemptAsync(
        DmdataSocketApiClient socketApi,
        CancellationTokenSource attempt,
        [EnumeratorCancellation] CancellationToken runToken)
    {
        DmdataSocketTicket? ticket = null;
        try
        {
            ticket = await socketApi.StartAsync(attempt.Token).ConfigureAwait(false);
            using var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(ticket.Protocol);
            await socket.ConnectAsync(ticket.WebSocketUri, attempt.Token).ConfigureAwait(false);
            Transition(ProviderConnectionState.Connected, "DMDATA.JP raw XML connected");

            while (socket.State == WebSocketState.Open && !attempt.IsCancellationRequested)
            {
                string? frameJson = await ReceiveTextAsync(socket, attempt.Token)
                    .ConfigureAwait(false);
                if (frameJson is null)
                {
                    break;
                }

                DmdataDecodedFrame frame;
                try
                {
                    frame = DmdataWebSocketFrameDecoder.Decode(frameJson);
                }
                catch (Exception exception) when (exception is JsonException or
                    InvalidDataException or FormatException)
                {
                    await LogAsync(
                        AppLogLevel.Warning,
                        "DmdataFrameRejected",
                        "A malformed DMDATA.JP WebSocket frame was rejected.",
                        exception,
                        runToken).ConfigureAwait(false);
                    continue;
                }

                if (frame.Kind == DmdataFrameKind.Ping)
                {
                    await SendTextAsync(
                        socket,
                        DmdataWebSocketFrameDecoder.CreatePong(frame.PingId),
                        attempt.Token).ConfigureAwait(false);
                    continue;
                }

                if (frame.Kind == DmdataFrameKind.Error)
                {
                    await LogAsync(
                        AppLogLevel.Warning,
                        "DmdataWebSocketError",
                        frame.Error ?? "DMDATA.JP WebSocket error",
                        null,
                        runToken).ConfigureAwait(false);
                    if (frame.CloseRequested)
                    {
                        break;
                    }

                    continue;
                }

                if (frame.Kind != DmdataFrameKind.Data || frame.Xml is null)
                {
                    continue;
                }

                DateTimeOffset receivedAt = _clock.UtcNow;
                Transition(
                    ProviderConnectionState.Connected,
                    frame.TelegramType ?? "XML telegram received",
                    receivedAt);
                yield return new RawProviderMessage(
                    "dmdata.jp",
                    frame.Xml,
                    frame.IsTest ? SourceMode.Sandbox : SourceMode.Production,
                    receivedAt)
                {
                    ContentFormat = RawProviderContentFormat.JmaXml,
                    TransportPayload = frameJson,
                    TransportContentFormat = RawProviderContentFormat.Json,
                };
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_attemptCancellation, attempt))
                {
                    _attemptCancellation = null;
                }
            }

            if (ticket is not null)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await socketApi.CloseAsync(ticket.SocketId, closeTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    await LogAsync(
                        AppLogLevel.Warning,
                        "DmdataSocketCloseFailed",
                        "DMDATA.JP socket close request failed.",
                        exception,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
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
                throw new InvalidDataException("DMDATA.JP sent a non-text WebSocket frame.");
            }

            if (payload.Length + result.Count > MaximumFrameBytes)
            {
                throw new InvalidDataException("DMDATA.JP WebSocket frame exceeded the safety limit.");
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
            endOfMessage: true,
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

    internal static string DescribeAuthorizationFailure(DmdataApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.StatusCode == 403 &&
            (exception.Message.Contains("Request Origin", StringComparison.OrdinalIgnoreCase) ||
             exception.Message.Contains("configured IP", StringComparison.OrdinalIgnoreCase)))
        {
            return "DMDATA.JPで許可された接続元IPまたはRequest Originと一致しません。" +
                   "契約者ページの接続元制限を確認してください。";
        }

        return exception.Message;
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
