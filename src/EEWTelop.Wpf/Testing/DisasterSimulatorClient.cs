using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using EEWTelop.Domain.Events;
using static EEWTelop.Wpf.Testing.DisasterSimulatorDecoder;

namespace EEWTelop.Wpf.Testing;

internal sealed class DisasterSimulatorClient
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    internal static Uri ValidateEndpoint(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) || !IPAddress.IsLoopback(ip) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 ||
            uri.AbsolutePath != "/")
            throw new ArgumentException("接続先は http://127.0.0.1:64270/ のようなローカルURLを指定してください。トークンは専用欄に入力してください。");
        return uri;
    }

    internal static async Task RunAsync(string address, string token,
        Func<IReadOnlyList<DisasterEvent>, bool, Task> onUpdate, Action connected, CancellationToken ct)
    {
        var uri = ValidateEndpoint(address);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsControl))
            throw new ArgumentException("シミュレーターのトークンを確認してください。");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = MaximumBytes };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "api/v1/status"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var status = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        if (!Flag(status.RootElement, "simulator") || Text(status.RootElement, "apiVersion") != "1" ||
            Text(status.RootElement, "application") != "CDI-Telopper Disaster Simulator")
            throw new FormatException("CDI-Telopper Disaster Simulator API v1ではありません。");
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        var ws = new UriBuilder(uri) { Scheme = "ws", Path = "/api/v1/events" }.Uri;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(ws, timeout.Token).ConfigureAwait(false);
        }
        connected();
        var tracker = new SimulatorUpdateTracker();
        byte[] buffer = new byte[16384];
        while (!ct.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var message = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                if (part.MessageType != WebSocketMessageType.Text)
                    throw new IOException("シミュレーター接続が終了したか、不正な形式を受信しました。");
                if (message.Length + part.Count > MaximumBytes) throw new IOException("シミュレーターの電文が大きすぎます。");
                message.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            using var json = JsonDocument.Parse(message.ToArray());
            var update = tracker.Read(json.RootElement, DateTimeOffset.UtcNow);
            if (update.Reset || update.Events.Count > 0)
                await onUpdate(update.Events, update.Reset).ConfigureAwait(false);
        }
    }
}

/// <summary>Full-state WebSocket frames must not replay unchanged sections or the initial snapshot.</summary>
internal sealed class SimulatorUpdateTracker
{
    private Dictionary<string, string> _previous = new();
    private string? _session;
    private long _sequence = -1;
    internal (IReadOnlyList<DisasterEvent> Events, bool Reset) Read(JsonElement root, DateTimeOffset now)
    {
        if (Text(root, "apiVersion") != "1") throw new FormatException("未対応のAPIバージョンです。");
        string session = Text(root, "sessionId"), type = Text(root, "type");
        if (session.Length == 0 || type is not ("snapshot" or "update" or "heartbeat") ||
            !Get(root, "sequence").TryGetInt64(out long sequence)) throw new FormatException("電文の識別情報が不正です。");
        bool initial = _session is null;
        bool reset = !initial && _session != session;
        if (!initial && !reset && sequence <= _sequence) return ([], false);
        if (reset) _previous.Clear();
        var current = new Dictionary<string, string>();
        var events = new List<DisasterEvent>();
        void Add(string key, string kind, JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) return;
            string raw = item.GetRawText();
            current[key] = raw;
            if (!initial && type != "snapshot" && (!_previous.TryGetValue(key, out var old) || old != raw))
                events.Add(Decode(kind, item, now));
        }
        var quake = Get(root, "earthquake");
        if (Flag(quake, "hasInformation")) Add("quake", "earthquake", Get(quake, "earthquake"));
        var tsunami = Get(root, "tsunami");
        if (Flag(tsunami, "hasInformation"))
        {
            Add("forecast", "tsunami", Get(tsunami, "forecast"));
            Add("observation", "tsunami", Get(tsunami, "observation"));
        }
        var eew = Get(root, "eew");
        if (Flag(eew, "hasInformation") && Get(eew, "events") is var array && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray()) Add("eew:" + Text(item, "eventId"), "eew", item);
        _previous = current;
        _session = session;
        _sequence = sequence;
        return (events, reset);
    }
}
