using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Wolfx.Configuration;

namespace EEWTelop.Infrastructure.Wolfx.Transport;

public sealed class WolfxEventSource : IEventSource, IProviderConfigurableEventSource
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly IAppLogWriter _log;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource _reconnectCancellation = new();
    private CancellationTokenSource? _readerCancellation;
    private WolfxProviderOptions _options;
    private ProviderConnectionSnapshot _connection;
    private int _readerActive;
    private bool _disposed;

    public WolfxEventSource(
        WolfxProviderOptions options,
        IClock clock,
        IAppLogWriter log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
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
        if (Volatile.Read(ref _readerActive) != 0)
        {
            throw new InvalidOperationException(
                "The Wolfx provider cannot be changed while reception is active.");
        }

        WolfxProviderOptions options = WolfxProviderOptions.FromSettings(settings);
        IReadOnlyList<string> errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        }

        lock (_gate)
        {
            _options = options;
        }
    }

    public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _readerActive, 1) != 0)
        {
            throw new InvalidOperationException("Only one Wolfx reader is allowed.");
        }

        WolfxProviderOptions options;
        lock (_gate)
        {
            options = _options;
        }

        IReadOnlyList<string> errors = options.Validate();
        if (errors.Count > 0)
        {
            Transition(ProviderConnectionState.Faulted, string.Join(" ", errors));
            Volatile.Write(ref _readerActive, 0);
            yield break;
        }

        using var readerCancellation = new CancellationTokenSource();
        lock (_gate)
        {
            _readerCancellation = readerCancellation;
        }

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token,
            readerCancellation.Token);
        ResetReconnectSignalIfCancelled();
        var channel = Channel.CreateUnbounded<RawProviderMessage>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = options.ReceiveEew ^ options.ReceiveQuake,
                AllowSynchronousContinuations = false,
            });
        var branches = new List<Task>(2);
        if (options.ReceiveEew)
        {
            branches.Add(RunBranchAsync(
                "EEW",
                options.EewWebSocketUri,
                "jma_eew",
                channel.Writer,
                runCancellation.Token));
        }

        if (options.ReceiveQuake)
        {
            branches.Add(RunBranchAsync(
                "地震情報",
                options.QuakeWebSocketUri,
                "jma_eqlist",
                channel.Writer,
                runCancellation.Token));
        }

        Task completion = CompleteWhenFinishedAsync(branches, channel.Writer);
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

            lock (_gate)
            {
                if (_readerCancellation == readerCancellation)
                {
                    _readerCancellation = null;
                }
            }

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
        }

        TryCancel(reader);
        SignalReconnect();
        Transition(ProviderConnectionState.Stopped, "Manual stop");
        return ValueTask.CompletedTask;
    }

    public void RequestReconnect(ReconnectReason reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Transition(ProviderConnectionState.Reconnecting, reason.ToString());
        SignalReconnect();
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
        lock (_gate)
        {
            _reconnectCancellation.Dispose();
        }

        _lifetimeCancellation.Dispose();
    }

    private async Task RunBranchAsync(
        string label,
        Uri endpoint,
        string expectedType,
        ChannelWriter<RawProviderMessage> writer,
        CancellationToken cancellationToken)
    {
        int retry = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            CancellationToken reconnectToken = GetReconnectToken();
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                reconnectToken);
            using var socket = new ClientWebSocket();
            try
            {
                Transition(
                    retry == 0
                        ? ProviderConnectionState.Connecting
                        : ProviderConnectionState.Reconnecting,
                    $"Wolfx {label}");
                await socket.ConnectAsync(endpoint, attemptCancellation.Token)
                    .ConfigureAwait(false);
                retry = 0;
                Transition(ProviderConnectionState.Connected, $"Wolfx {label} connected");

                while (!attemptCancellation.IsCancellationRequested &&
                       socket.State == WebSocketState.Open)
                {
                    string? payload = await ReceiveTextAsync(
                        socket,
                        attemptCancellation.Token).ConfigureAwait(false);
                    if (payload is null)
                    {
                        break;
                    }

                    string type = ReadType(payload);
                    if (type.Equals("heartbeat", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendPingAsync(socket, attemptCancellation.Token)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (type.Equals("pong", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (type.Length > 0 &&
                        !type.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
                    {
                        await LogAsync(
                            AppLogLevel.Warning,
                            "WolfxMessageIgnored",
                            $"Wolfx {label} returned unexpected type '{type}'.",
                            null,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    DateTimeOffset receivedAt = _clock.UtcNow;
                    Transition(
                        ProviderConnectionState.Connected,
                        $"Wolfx {label} message received",
                        receivedAt);
                    await writer.WriteAsync(
                        new RawProviderMessage(
                            WolfxProviderOptions.ProviderName,
                            payload,
                            SourceMode.Production,
                            receivedAt),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                await LogAsync(
                    AppLogLevel.Warning,
                    "WolfxConnectionFailed",
                    $"Wolfx {label} connection stopped and will reconnect.",
                    exception,
                    cancellationToken).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            ResetReconnectSignalIfCancelled();
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(retry, 4)));
            retry = Math.Min(retry + 1, 30);
            Transition(
                ProviderConnectionState.Reconnecting,
                $"Wolfx {label} retry",
                retryDelay: delay);
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static async Task<string?> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Wolfx returned a non-text WebSocket frame.");
            }

            if (stream.Length + result.Count > MaximumMessageBytes)
            {
                throw new InvalidDataException("Wolfx message exceeded the 4 MiB limit.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static async Task SendPingAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] ping = Encoding.UTF8.GetBytes("ping");
        await socket.SendAsync(
            new ArraySegment<byte>(ping),
            WebSocketMessageType.Text,
            true,
            cancellationToken).ConfigureAwait(false);
    }

    private static string ReadType(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("type", out JsonElement type) &&
                type.ValueKind == JsonValueKind.String)
            {
                return type.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    private static async Task CompleteWhenFinishedAsync(
        IReadOnlyCollection<Task> branches,
        ChannelWriter<RawProviderMessage> writer)
    {
        try
        {
            await Task.WhenAll(branches).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }

    private CancellationToken GetReconnectToken()
    {
        lock (_gate)
        {
            return _reconnectCancellation.Token;
        }
    }

    private void SignalReconnect()
    {
        lock (_gate)
        {
            TryCancel(_reconnectCancellation);
        }
    }

    private void ResetReconnectSignalIfCancelled()
    {
        lock (_gate)
        {
            if (_reconnectCancellation.IsCancellationRequested)
            {
                _reconnectCancellation.Dispose();
                _reconnectCancellation = new CancellationTokenSource();
            }
        }
    }

    private void Transition(
        ProviderConnectionState state,
        string detail,
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

    private async ValueTask LogAsync(
        AppLogLevel level,
        string eventName,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await _log.WriteAsync(
                new AppLogEntry(_clock.UtcNow, level, eventName, message, exception),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
