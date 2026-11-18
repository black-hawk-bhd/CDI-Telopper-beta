using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Wolfx.Configuration;
using EEWTelop.Infrastructure.Wolfx.Normalization;

namespace EEWTelop.Infrastructure.Wolfx.Transport;

public sealed class WolfxEventSource : IEventSource, IProviderConfigurableEventSource
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan DetailPollInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly IAppLogWriter _log;
    private readonly HttpClient _detailHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
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
        var channel = new ProviderMessageBuffer();
        using var quakeDetails = options.ReceiveQuake ? new WolfxEventNormalizer.EarthquakeDetailReports(_clock.UtcNow) : null;
        var branches = new List<Task>(5);
        if (options.ReceiveEew)
        {
            branches.Add(RunBranchAsync(
                "EEW",
                options.EewWebSocketUri,
                "jma_eew",
                channel,
                runCancellation.Token));
        }

        if (options.ReceiveQuake)
        {
            branches.Add(RunBranchAsync(
                "地震情報",
                options.QuakeWebSocketUri,
                "jma_eqlist",
                channel,
                runCancellation.Token,
                quakeDetails: quakeDetails));
            branches.Add(RunEarthquakeDetailAsync(quakeDetails!, channel, runCancellation.Token));
        }

        if (options.ReceiveTsunami)
        {
            branches.Add(RunBranchAsync("津波情報", WolfxProviderOptions.TsunamiWebSocketUri,
                "jma_tsunami", channel, runCancellation.Token, "query_jmatsunami"));
        }

        if (options.ReceiveSpecialEarthquake)
        {
            branches.Add(RunBranchAsync("特別地震情報", WolfxProviderOptions.SpecialEarthquakeWebSocketUri,
                "jma_special_eq", channel, runCancellation.Token, "query_jmaspecialeq"));
        }

        Task completion = CompleteWhenFinishedAsync(branches, channel);
        try
        {
            await foreach (RawProviderMessage message in channel
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
        _detailHttp.Dispose();
    }

    private async Task RunBranchAsync(
        string label,
        Uri endpoint,
        string expectedType,
        ProviderMessageBuffer writer,
        CancellationToken cancellationToken,
        string? initialQuery = null,
        WolfxEventNormalizer.EarthquakeDetailReports? quakeDetails = null)
    {
        int retry = 0;
        var specialReports = expectedType == "jma_special_eq" ? new WolfxEventNormalizer.SpecialEarthquakeReports() : null;
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
                DateTimeOffset connectedAt = _clock.UtcNow;
                retry = 0;
                Transition(ProviderConnectionState.Connected, $"Wolfx {label} connected");
                if (initialQuery is not null)
                {
                    await SendCommandAsync(socket, initialQuery, attemptCancellation.Token).ConfigureAwait(false);
                }
                bool initialFrame = true;

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

                    (string type, string status) = ReadEnvelope(payload);
                    if (type.Equals("heartbeat", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendCommandAsync(socket, "ping", attemptCancellation.Token)
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
                    var raw = new RawProviderMessage(WolfxProviderOptions.ProviderName,
                        payload, SourceMode.Production, receivedAt);
                    if (specialReports is not null)
                    {
                        foreach (RawProviderMessage report in specialReports.Read(raw, initialFrame, connectedAt))
                        {
                            await writer.WriteAsync(report, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else if (quakeDetails is not null)
                    {
                        await quakeDetails.PublishSummaryAsync(raw, writer, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteAsync(raw with
                        {
                            IsStateSnapshot = initialFrame && expectedType == "jma_tsunami" &&
                                status is not ("active" or "forecast_only"),
                        },
                            cancellationToken).ConfigureAwait(false);
                    }
                    initialFrame = false;
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

    private async Task RunEarthquakeDetailAsync(WolfxEventNormalizer.EarthquakeDetailReports reports,
        ProviderMessageBuffer writer, CancellationToken cancellationToken)
    {
        bool failed = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan delay = DetailPollInterval;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await _detailHttp.GetAsync(WolfxProviderOptions.EarthquakeDetailUri,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - _clock.UtcNow;
                    if (retryAfter > delay) delay = retryAfter.Value;
                }
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(MaximumMessageBytes, timeout.Token).ConfigureAwait(false);
                string payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                await reports.PublishDetailsAsync(new RawProviderMessage(WolfxProviderOptions.ProviderName,
                    payload, SourceMode.Production, _clock.UtcNow), writer, cancellationToken).ConfigureAwait(false);
                failed = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or
                OperationCanceledException or InvalidOperationException)
            {
                if (!failed)
                    await LogAsync(AppLogLevel.Warning, "WolfxEarthquakeDetailFailed",
                        "Wolfxの地震詳細を取得できません。WebSocketの地震概要は引き続き受信します。",
                        exception, cancellationToken).ConfigureAwait(false);
                failed = true;
            }
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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

    private static async Task SendCommandAsync(
        ClientWebSocket socket,
        string command,
        CancellationToken cancellationToken)
    {
        await socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(command)),
            WebSocketMessageType.Text,
            true,
            cancellationToken).ConfigureAwait(false);
    }

    private static (string Type, string Status) ReadEnvelope(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            return (ReadString(document.RootElement, "type"), ReadString(document.RootElement, "status"));
        }
        catch (JsonException)
        {
        }

        return (string.Empty, string.Empty);

        static string ReadString(JsonElement root, string key) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static async Task CompleteWhenFinishedAsync(
        IReadOnlyCollection<Task> branches,
        ProviderMessageBuffer writer)
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
