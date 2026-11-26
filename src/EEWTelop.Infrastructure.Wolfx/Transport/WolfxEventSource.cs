using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly IAppLogWriter _log;
    private readonly HttpClient _detailHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource _reconnectCancellation = new();
    private CancellationTokenSource? _readerCancellation;
    private WolfxProviderOptions _options;
    private ProviderConnectionSnapshot _connection;
    private long? _lastDetailAttemptFinished;
    private TimeSpan _detailCooldown = WolfxRetryPolicy.DetailPollInterval;
    private int _detailFailureCount;
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
                "緊急地震速報",
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
        int failureCount = 0;
        var specialReports = expectedType == "jma_special_eq" ? new WolfxEventNormalizer.SpecialEarthquakeReports() : null;
        while (!cancellationToken.IsCancellationRequested)
        {
            CancellationToken reconnectToken = GetReconnectToken();
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                reconnectToken);
            using var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;
            long? connectedTimestamp = null;
            Exception? failure = null;
            try
            {
                Transition(
                    failureCount == 0
                        ? ProviderConnectionState.Connecting
                        : ProviderConnectionState.Reconnecting,
                    $"Wolfx {label}");
                await socket.ConnectAsync(endpoint, attemptCancellation.Token)
                    .ConfigureAwait(false);
                DateTimeOffset connectedAt = _clock.UtcNow;
                connectedTimestamp = _clock.GetTimestamp();
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
                failure = exception;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            bool reconnectRequested = reconnectToken.IsCancellationRequested;
            ResetReconnectSignalIfCancelled();
            TimeSpan delay = TimeSpan.FromSeconds(1);
            if (!reconnectRequested)
            {
                TimeSpan? duration = connectedTimestamp is long started ? _clock.GetElapsedTime(started) : null;
                failureCount = WolfxRetryPolicy.NextWebSocketFailureCount(failureCount, duration);
                HttpStatusCode? status = (int)socket.HttpStatusCode >= 400 ? socket.HttpStatusCode : null;
                delay = WolfxRetryPolicy.WebSocketDelay(failureCount, status, ReadWebSocketRetryAfter(socket));
                string reason = DescribeWebSocketFailure(socket, status, failure);
                string action = connectedTimestamp.HasValue ? "切断されました" : "接続に失敗しました";
                await LogAsync(AppLogLevel.Warning, "WolfxConnectionFailed",
                    $"Wolfx {label}のWebSocketは{action}（{reason}）。{Math.Ceiling(delay.TotalSeconds):0}秒後に再接続します。",
                    failure, cancellationToken).ConfigureAwait(false);
            }
            Transition(
                ProviderConnectionState.Reconnecting,
                $"Wolfx {label} retry",
                retryDelay: delay);
            try
            {
                await WaitForRetryAsync(delay, cancellationToken).ConfigureAwait(false);
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
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_lastDetailAttemptFinished is long finished)
                await WaitForRetryAsync(_detailCooldown - _clock.GetElapsedTime(finished), cancellationToken).ConfigureAwait(false);

            HttpStatusCode? status = null;
            TimeSpan? retryAfter = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await _detailHttp.GetAsync(WolfxProviderOptions.EarthquakeDetailUri,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    status = response.StatusCode;
                    retryAfter = ReadRetryAfter(response.Headers.RetryAfter);
                }
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(MaximumMessageBytes, timeout.Token).ConfigureAwait(false);
                string payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                await reports.PublishDetailsAsync(new RawProviderMessage(WolfxProviderOptions.ProviderName,
                    payload, SourceMode.Production, _clock.UtcNow), writer, cancellationToken).ConfigureAwait(false);
                if (_detailFailureCount > 0)
                    await LogAsync(AppLogLevel.Information, "WolfxEarthquakeDetailRecovered",
                        "Wolfxの地震詳細HTTP取得が復旧しました。", null, cancellationToken).ConfigureAwait(false);
                _detailFailureCount = 0;
                _detailCooldown = WolfxRetryPolicy.DetailPollInterval;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or
                OperationCanceledException or InvalidOperationException)
            {
                TimeSpan previousCooldown = _detailCooldown;
                _detailFailureCount = Math.Min(_detailFailureCount + 1, 4);
                _detailCooldown = WolfxRetryPolicy.DetailDelay(_detailFailureCount, status, retryAfter);
                if (previousCooldown != _detailCooldown)
                {
                    string reason = status is HttpStatusCode http ? $"HTTP {(int)http}"
                        : exception is OperationCanceledException ? "10秒でタイムアウト" : exception.GetType().Name;
                    await LogAsync(AppLogLevel.Warning, "WolfxEarthquakeDetailFailed",
                        $"Wolfxの地震詳細HTTP取得に失敗しました（{reason}）。次の取得まで{Math.Ceiling(_detailCooldown.TotalSeconds):0}秒以上待ちます。",
                        exception, cancellationToken).ConfigureAwait(false);
                }
            }
            finally { _lastDetailAttemptFinished = _clock.GetTimestamp(); }
        }
    }

    private TimeSpan? ReadWebSocketRetryAfter(ClientWebSocket socket)
    {
        if (socket.HttpResponseHeaders is not { } headers) return null;

        foreach (var (name, values) in headers)
        {
            if (!name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string value in values)
                if (RetryConditionHeaderValue.TryParse(value, out var retryAfter)) return ReadRetryAfter(retryAfter);
        }
        return null;
    }

    private TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? header) =>
        header?.Delta ?? (header?.Date - _clock.UtcNow);

    private static string DescribeWebSocketFailure(ClientWebSocket socket, HttpStatusCode? status, Exception? failure)
    {
        if (status is HttpStatusCode http) return $"HTTP {(int)http}";
        if (socket.CloseStatus is WebSocketCloseStatus close) return $"終了コード {(int)close}";
        if (failure is WebSocketException webSocket) return webSocket.WebSocketErrorCode.ToString();
        return failure?.GetType().Name ?? "接続終了";
    }

    private static async Task WaitForRetryAsync(TimeSpan remaining, CancellationToken cancellationToken)
    {
        // Split long Retry-After values so they stay within Task.Delay's supported range.
        TimeSpan maximumDelay = TimeSpan.FromDays(1);
        while (remaining > maximumDelay)
        {
            await Task.Delay(maximumDelay, cancellationToken).ConfigureAwait(false);
            remaining -= maximumDelay;
        }
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
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
