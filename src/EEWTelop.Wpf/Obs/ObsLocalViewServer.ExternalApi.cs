using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Events;

namespace EEWTelop.Wpf.Obs;

public sealed partial class ObsLocalViewServer
{
    private static readonly JsonSerializerOptions ExternalJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly EventReceptionService? _receptionService;
    private readonly ExternalApiState _externalState = new();
    private readonly ExternalEarthquakeState _externalEarthquakes = new();
    private readonly string _externalSession = Guid.NewGuid().ToString("N");
    private string? _externalToken;
    private int _externalSockets;
    private readonly Func<CancellationToken, Task<EEWTelop.Domain.Events.TsunamiEvent>>? _initialTsunamiFetcher;
    private readonly object _initializationGate = new();
    private CancellationTokenSource? _initializationStop;

    public bool ExternalApiEnabled
    {
        get => Volatile.Read(ref _externalToken) is not null;
        set
        {
            if (!value)
            {
                Interlocked.Exchange(ref _externalToken, null);
                CancelExternalInitialization();
            }
            else if (Interlocked.CompareExchange(ref _externalToken,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), null) is null)
                StartExternalInitialization();
        }
    }

    private void StartExternalInitialization()
    {
        lock (_initializationGate)
        {
            if (_initialTsunamiFetcher is null || !IsRunning || !ExternalApiEnabled) return;
            _initializationStop?.Cancel();
            var stop = new CancellationTokenSource();
            _initializationStop = stop;
            var ticket = _externalState.BeginInitialization(_clock.UtcNow);
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(15));
                            var forecast = await _initialTsunamiFetcher(timeout.Token).ConfigureAwait(false);
                            if (stop.IsCancellationRequested) return;
                            _externalState.CompleteInitialization(ticket, forecast, _clock.UtcNow);
                            return;
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException)
                        {
                            if (stop.IsCancellationRequested) return;
                            _externalState.FailInitialization(ticket.Id, _clock.UtcNow);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(60), stop.Token).ConfigureAwait(false);
                        lock (_initializationGate)
                        {
                            if (stop.IsCancellationRequested) return;
                            ticket = _externalState.BeginInitialization(_clock.UtcNow);
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                finally
                {
                    lock (_initializationGate)
                    {
                        if (ReferenceEquals(_initializationStop, stop)) _initializationStop = null;
                        stop.Dispose();
                    }
                }
            });
        }
    }

    private void CancelExternalInitialization()
    {
        lock (_initializationGate)
        {
            _initializationStop?.Cancel();
            _initializationStop = null;
            _externalState.CancelInitialization();
        }
    }

    public string ExternalApiUrl => IsRunning && ExternalApiEnabled
        ? $"http://127.0.0.1:{Port}/api/v1/status?token={Volatile.Read(ref _externalToken)}"
        : string.Empty;

    private void OnExternalEventProcessed(object? sender, EventIngestionResult result)
    {
        _externalState.Observe(result);
        _externalEarthquakes.Observe(result);
    }

    private object ReadExternalStatus() => new
    {
        apiVersion = "1", sessionId = _externalSession,
        applicationVersion = typeof(ObsLocalViewServer).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
        serverTime = _clock.UtcNow,
        tsunamiInitialization = _externalState.Read(_clock.UtcNow).Initialization,
        connection = _receptionService?.Connection.State.ToString() ?? "Unavailable",
        lastReceivedAt = _receptionService?.Connection.LastReceivedAt,
        providers = _receptionService?.GetProviderConnections().Select(p => new
        {
            provider = p.Name, state = p.Connection.State.ToString(),
            lastReceivedAt = p.Connection.LastReceivedAt,
        }).ToArray(),
        sourceMode = "production", readOnly = true,
        capabilities = new[] { "tsunami", "earthquake", "eew", "events.websocket" },
    };

    private async Task HandleExternalApiAsync(NetworkStream stream, HttpRequest request, Uri uri,
        CancellationToken cancellationToken)
    {
        string? token = Volatile.Read(ref _externalToken);
        if (token is null)
        {
            await WriteResponseAsync(stream, 404, "application/json", "{\"error\":\"disabled\"}", cancellationToken).ConfigureAwait(false);
            return;
        }

        // This API has its own revocable credential; OBS URLs do not grant API access.
        string supplied = request.Headers.TryGetValue("Authorization", out string? auth) &&
            auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] :
            TryGetQueryParameter(uri.Query, "token", out string queryToken) ? queryToken : string.Empty;
        bool originAllowed = !request.Headers.TryGetValue("Origin", out string? origin) ||
            (Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri) &&
             originUri.Scheme == "http" && originUri.IsLoopback);
        if (!originAllowed || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(token)))
        {
            await WriteResponseAsync(stream, 403, "application/json", "{\"error\":\"forbidden\"}", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (request.Method != "GET")
        {
            await WriteMethodNotAllowedAsync(stream, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (uri.AbsolutePath == "/api/v1/events")
        {
            await StreamExternalEventsAsync(stream, request, token, cancellationToken).ConfigureAwait(false);
            return;
        }
        object? payload = uri.AbsolutePath switch
        {
            "/api/v1/status" => ReadExternalStatus(),
            "/api/v1/earthquake" => new { sessionId = _externalSession, status = ReadExternalStatus(), earthquake = _externalEarthquakes.ReadQuake() },
            "/api/v1/eew" => new { sessionId = _externalSession, status = ReadExternalStatus(), eew = _externalEarthquakes.ReadEew(_clock.UtcNow) },
            "/api/v1/tsunami" => new
            {
                sessionId = _externalSession,
                status = ReadExternalStatus(),
                tsunami = _externalState.Read(_clock.UtcNow),
            },
            _ => null,
        };
        await WriteResponseAsync(stream, payload is null ? 404 : 200, "application/json; charset=utf-8",
            JsonSerializer.Serialize(payload ?? new { error = "not_found" }, ExternalJsonOptions), cancellationToken).ConfigureAwait(false);
    }

    private async Task StreamExternalEventsAsync(NetworkStream stream, HttpRequest request,
        string token, CancellationToken cancellationToken)
    {
        bool valid = request.Headers.TryGetValue("Upgrade", out string? upgrade) &&
            upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase) &&
            request.Headers.TryGetValue("Connection", out string? connection) &&
            connection.Split(',').Any(p => p.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase)) &&
            request.Headers.TryGetValue("Sec-WebSocket-Version", out string? version) && version == "13";
        request.Headers.TryGetValue("Sec-WebSocket-Key", out string? key);
        byte[] keyBytes = new byte[16];
        if (!valid || key is null || !Convert.TryFromBase64String(key, keyBytes, out int bytes) || bytes != 16)
        {
            await WriteResponseAsync(stream, 400, "application/json", "{\"error\":\"websocket_upgrade_required\"}", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (Interlocked.Increment(ref _externalSockets) > 16)
        {
            Interlocked.Decrement(ref _externalSockets);
            await WriteResponseAsync(stream, 503, "application/json", "{\"error\":\"too_many_clients\"}", cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
#pragma warning disable CA5350 // RFC 6455 requires SHA-1 for the handshake, not credential protection.
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
                key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), cancellationToken).ConfigureAwait(false);
            using WebSocket socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task receiving = ReceiveExternalCloseAsync(socket, lifetime.Token);
            try
            {
                string? previous = null;
                int ticks = 0;
                long sequence = 0;
                while (!receiving.IsCompleted && !lifetime.IsCancellationRequested &&
                    string.Equals(token, Volatile.Read(ref _externalToken), StringComparison.Ordinal))
                {
                    ExternalTsunamiSnapshot tsunami = _externalState.Read(_clock.UtcNow);
                    object earthquake = _externalEarthquakes.ReadQuake();
                    object eew = _externalEarthquakes.ReadEew(_clock.UtcNow);
                    string fingerprint = JsonSerializer.Serialize(new
                    {
                        tsunami,
                        earthquake, eew,
                        connection = _receptionService?.Connection,
                    }, ExternalJsonOptions);
                    if (fingerprint != previous || ticks >= 15)
                    {
                        string type = previous is null ? "snapshot" : fingerprint != previous ? "update" : "heartbeat";
                        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            apiVersion = "1", sessionId = _externalSession,
                            sequence = ++sequence, type, status = ReadExternalStatus(), tsunami, earthquake, eew,
                        }, ExternalJsonOptions);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                        previous = fingerprint;
                        ticks = 0;
                    }
                    await Task.Delay(1000, lifetime.Token).ConfigureAwait(false);
                    ticks++;
                }
            }
            finally
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
                socket.Abort();
                try { await receiving.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        finally { Interlocked.Decrement(ref _externalSockets); }
    }

    private static async Task ReceiveExternalCloseAsync(WebSocket socket, CancellationToken token)
    {
        // No commands are accepted. A close or any application frame ends this read-only session.
        byte[] buffer = new byte[1];
        await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
    }
}
