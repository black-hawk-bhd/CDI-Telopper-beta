using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Obs;

public sealed partial class ObsLocalViewServer
{
    private static readonly JsonSerializerOptions ExternalJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string? ExternalApplicationVersion = typeof(ObsLocalViewServer).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
    private readonly EventReceptionService? _receptionService;
    private readonly ExternalApiState _externalState = new();
    private readonly ExternalEarthquakeState _externalEarthquakes = new();
    private readonly string _externalSession = Guid.NewGuid().ToString("N");
    private readonly object _externalCredentialGate = new();
    private readonly ExternalApiConnectionKeyStore? _externalApiKeyStore;
    private string? _savedExternalToken;
    private bool _externalApiKeyLoadFailed;
    private ApiCredential? _externalCredential;
    private ApiCredential? _rehearsalCredential;
    private readonly ExternalRehearsalState _rehearsal = new();
    private readonly ExternalApiHealth _externalHealth = new();
    private int _externalSockets;
    private readonly Func<CancellationToken, Task<EEWTelop.Domain.Events.TsunamiEvent>>? _initialTsunamiFetcher;
    private readonly object _initializationGate = new();
    private CancellationTokenSource? _initializationStop;

    public bool RehearsalApiEnabled
    {
        get => ReadApiCredential(rehearsal: true) is not null;
        set
        {
            if (!value)
            {
                Interlocked.Exchange(ref _rehearsalCredential, null);
                _rehearsal.End();
            }
            else Interlocked.CompareExchange(ref _rehearsalCredential,
                new ApiCredential(GenerateApiToken()), null);
        }
    }

    public string RehearsalApiUrl => BuildExternalApiUrl(rehearsal: true);
    public string? BeginApiRehearsal(string source) => RehearsalApiEnabled ? _rehearsal.Begin(source) : null;
    public void EndApiRehearsal(string? session = null) => _rehearsal.End(session);
    public void ReplaceApiRehearsalSnapshot(string session, IReadOnlyList<DisasterEvent> events)
    {
        if (RehearsalApiEnabled) _rehearsal.ReplaceSnapshot(session, events);
    }
    public void SetApiRehearsalHealth(string? session, string health)
    {
        if (RehearsalApiEnabled && session is not null) _rehearsal.SetHealth(session, health);
    }
    public void ObserveApiRehearsal(DisasterEvent item, string? session = null, bool manualReplay = false)
    {
        if (RehearsalApiEnabled) _rehearsal.Observe(item, session, manualReplay);
    }

    public bool ExternalApiEnabled
    {
        get => ReadApiCredential(rehearsal: false) is not null;
        set
        {
            lock (_externalCredentialGate)
            {
                if (!value)
                {
                    Volatile.Write(ref _externalCredential, null);
                    CancelExternalInitialization();
                }
                else if (_externalCredential is null)
                {
                    Volatile.Write(ref _externalCredential, new ApiCredential(_savedExternalToken ?? GenerateApiToken()));
                    StartExternalInitialization();
                }
            }
        }
    }

    public bool CanSaveExternalApiConnectionKey => _externalApiKeyStore is not null;
    public bool IsExternalApiConnectionKeySaved => Volatile.Read(ref _savedExternalToken) is not null;
    public bool ExternalApiConnectionKeyLoadFailed => Volatile.Read(ref _externalApiKeyLoadFailed);

    public void SetExternalApiConnectionKeySaved(bool save)
    {
        if (_externalApiKeyStore is null) return;
        lock (_externalCredentialGate)
        {
            if (save)
            {
                if (_savedExternalToken is not null) return;
                string token = _externalCredential?.Token ?? GenerateApiToken();
                _externalApiKeyStore.Save(token);
                Volatile.Write(ref _savedExternalToken, token);
            }
            else
            {
                if (_savedExternalToken is null && !_externalApiKeyLoadFailed) return;
                _externalApiKeyStore.Clear();
                Volatile.Write(ref _savedExternalToken, null);
                if (_externalCredential is not null)
                {
                    Volatile.Write(ref _externalCredential, new ApiCredential(GenerateApiToken()));
                }
            }

            Volatile.Write(ref _externalApiKeyLoadFailed, false);
        }
    }

    public void ReissueExternalApiConnectionKey()
    {
        lock (_externalCredentialGate)
        {
            if (_savedExternalToken is null && _externalCredential is null) return;
            string token = GenerateApiToken();
            if (_savedExternalToken is not null)
            {
                _externalApiKeyStore!.Save(token);
                Volatile.Write(ref _savedExternalToken, token);
            }

            if (_externalCredential is not null)
            {
                Volatile.Write(ref _externalCredential, new ApiCredential(token));
            }
        }
    }

    private static string GenerateApiToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private sealed class ApiCredential(string token)
    {
        public string Token { get; } = token;
    }

    private ApiCredential? ReadApiCredential(bool rehearsal) => rehearsal
        ? Volatile.Read(ref _rehearsalCredential)
        : Volatile.Read(ref _externalCredential);

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

    public string ExternalApiUrl => BuildExternalApiUrl(rehearsal: false);

    private string BuildExternalApiUrl(bool rehearsal)
    {
        ApiCredential? credential = ReadApiCredential(rehearsal);
        string path = rehearsal ? "rehearsal/status" : "status";
        return IsRunning && credential is not null
            ? $"http://127.0.0.1:{Port}/api/v1/{path}?token={credential.Token}"
            : string.Empty;
    }

    private void OnExternalEventProcessed(object? sender, EventIngestionResult result)
    {
        _externalState.Observe(result);
        _externalEarthquakes.Observe(result);
        _externalHealth.Observe(result);
        if (result.Status == EventIngestionStatus.Accepted && result.Event is { } item)
            ObserveApiRehearsal(item);
    }

    private ExternalDomainHealth ReadHealth(EventKind kind)
    {
        var route = _receptionService?.GetDataConnection(kind) ?? (Configured: false, State: (ProviderConnectionState?)null);
        return _externalHealth.Read(kind, route.Configured, route.State);
    }

    private object ReadDataHealth() => new
    {
        tsunami = ReadHealth(EventKind.Tsunami), earthquake = ReadHealth(EventKind.Quake), eew = ReadHealth(EventKind.Eew),
    };

    private object ReadExternalStatus() => new
    {
        apiVersion = "1", sessionId = _externalSession,
        applicationVersion = ExternalApplicationVersion,
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
        channel = "production", isTraining = false,
        dataHealth = ReadDataHealth(),
        capabilities = RehearsalApiEnabled
            ? new[] { "tsunami", "earthquake", "eew", "events.websocket", "dataHealth", "rehearsal" }
            : new[] { "tsunami", "earthquake", "eew", "events.websocket", "dataHealth" },
    };

    private JsonObject ReadChannel(bool rehearsal)
    {
        JsonObject result;
        if (rehearsal)
        {
            _rehearsal.UpdateSandboxHealth(_receptionService?.Connection.State);
            result = _rehearsal.Read(_clock.UtcNow);
            result["status"] = new JsonObject
            {
                ["apiVersion"] = "1", ["sessionId"] = _externalSession,
                ["channel"] = "rehearsal", ["isTraining"] = true,
                ["serverTime"] = JsonValue.Create(_clock.UtcNow), ["readOnly"] = true,
                ["applicationVersion"] = ExternalApplicationVersion,
                ["capabilities"] = new JsonArray("tsunami", "earthquake", "eew", "events.websocket", "dataHealth", "rehearsal"),
                ["sourceMode"] = result["rehearsal"]!["sourceMode"]!.DeepClone(),
                ["rehearsalSessionId"] = result["rehearsalSessionId"]!.DeepClone(),
                ["rehearsal"] = result["rehearsal"]!.DeepClone(),
                ["dataHealth"] = result["dataHealth"]!.DeepClone(),
            };
            result.Remove("dataHealth");
        }
        else result = JsonSerializer.SerializeToNode(new
        {
            status = ReadExternalStatus(), tsunami = _externalState.Read(_clock.UtcNow),
            earthquake = _externalEarthquakes.ReadQuake(), eew = _externalEarthquakes.ReadEew(_clock.UtcNow),
        }, ExternalJsonOptions)!.AsObject();
        result["apiVersion"] = "1";
        result["sessionId"] = _externalSession;
        result["channel"] = rehearsal ? "rehearsal" : "production";
        result["isTraining"] = rehearsal;
        return result;
    }

    private async Task HandleExternalApiAsync(NetworkStream stream, HttpRequest request, Uri uri,
        CancellationToken cancellationToken)
    {
        bool rehearsal = uri.AbsolutePath.StartsWith("/api/v1/rehearsal/", StringComparison.Ordinal);
        ApiCredential? credential = ReadApiCredential(rehearsal);
        if (credential is null)
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
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(credential.Token)))
        {
            await WriteResponseAsync(stream, 403, "application/json", "{\"error\":\"forbidden\"}", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (request.Method != "GET")
        {
            await WriteMethodNotAllowedAsync(stream, cancellationToken).ConfigureAwait(false);
            return;
        }
        string path = uri.AbsolutePath[(rehearsal ? "/api/v1/rehearsal/".Length : "/api/v1/".Length)..];
        if (path == "events")
        {
            await StreamExternalEventsAsync(stream, request, credential, rehearsal, cancellationToken).ConfigureAwait(false);
            return;
        }
        JsonObject channel = ReadChannel(rehearsal);
        JsonNode? payload = path switch
        {
            "status" => channel["status"],
            "earthquake" or "eew" or "tsunami" => channel,
            _ => null,
        };
        if (payload == channel)
            foreach (string resource in new[] { "tsunami", "earthquake", "eew" })
                if (resource != path) channel.Remove(resource);
        await WriteResponseAsync(stream, payload is null ? 404 : 200, "application/json; charset=utf-8",
            payload?.ToJsonString(ExternalJsonOptions) ?? "{\"error\":\"not_found\"}", cancellationToken).ConfigureAwait(false);
    }

    private async Task StreamExternalEventsAsync(NetworkStream stream, HttpRequest request,
        ApiCredential credential, bool rehearsal, CancellationToken cancellationToken)
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
                    ReferenceEquals(credential, ReadApiCredential(rehearsal)))
                {
                    JsonObject snapshot = ReadChannel(rehearsal);
                    JsonObject comparable = snapshot.DeepClone().AsObject();
                    comparable["status"]!.AsObject().Remove("serverTime");
                    string fingerprint = comparable.ToJsonString(ExternalJsonOptions);
                    if (fingerprint != previous || ticks >= 15)
                    {
                        string type = previous is null ? "snapshot" : fingerprint != previous ? "update" : "heartbeat";
                        snapshot["sequence"] = ++sequence;
                        snapshot["type"] = type;
                        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, ExternalJsonOptions);
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
