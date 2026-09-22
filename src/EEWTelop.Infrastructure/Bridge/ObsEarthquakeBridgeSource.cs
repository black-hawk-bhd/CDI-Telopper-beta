using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Bridge;

/// <summary>Adapter for the local OBS-Earthquake Bridge; not CDI's public API.</summary>
public sealed class ObsEarthquakeBridgeSource : IEventSource
{
    public const string ProviderName = "obs-earthquake-bridge";
    private const int MaximumPayload = 4 * 1024 * 1024;
    private static readonly Uri BaseUri = new("http://127.0.0.1:51235/");
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private CancellationTokenSource? _reader;
    private CancellationTokenSource? _attempt;
    private bool _disposed;

    public ObsEarthquakeBridgeSource(IClock clock)
        : this(clock, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })) { }

    public ObsEarthquakeBridgeSource(IClock clock, HttpClient http)
    {
        _clock = clock;
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        Connection = new(ProviderConnectionState.Stopped, clock.UtcNow);
    }

    public ProviderConnectionSnapshot Connection { get; private set; }
    public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged;

    public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_reader is not null) throw new InvalidOperationException("Bridge receiver is already running.");
            _reader = reader;
        }
        int delay = 2;
        try
        {
            while (!reader.IsCancellationRequested)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(reader.Token);
                lock (_gate) _attempt = attempt;
                var channel = Channel.CreateBounded<RawProviderMessage>(new BoundedChannelOptions(256)
                {
                    SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
                });
                Task producer = ProduceAsync(channel.Writer, attempt.Token);
                try
                {
                    await foreach (RawProviderMessage message in channel.Reader.ReadAllAsync(reader.Token).ConfigureAwait(false))
                    {
                        delay = 2;
                        yield return message;
                    }
                }
                finally
                {
                    attempt.Cancel();
                    await producer.ConfigureAwait(false);
                    lock (_gate) _attempt = null;
                }
                if (!reader.IsCancellationRequested)
                {
                    SetState(ProviderConnectionState.Reconnecting, "Bridge再接続待ち（情報の解除は行いません）");
                    await Task.Delay(TimeSpan.FromSeconds(delay), reader.Token).ConfigureAwait(false);
                    delay = Math.Min(30, delay * 2);
                }
            }
        }
        finally
        {
            lock (_gate) _reader = null;
            SetState(ProviderConnectionState.Stopped, "停止");
        }
    }

    private async Task ProduceAsync(ChannelWriter<RawProviderMessage> writer, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task health = Task.CompletedTask;
        try
        {
            SetState(ProviderConnectionState.Connecting, "OBS-Earthquake Bridgeへ接続中");
            // Subscribe before reading snapshots: updates during HTTP sync remain in the stream.
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, "api/v1/events"));
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new IOException("Bridge SSE endpoint unavailable.");
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using JsonDocument status = await GetAsync("status", token).ConfigureAwait(false);
            UpdateHealth(status.RootElement);
            health = MonitorHealthAsync(lifetime.Token);
            foreach (string kind in new[] { "eew", "earthquake", "tsunami" })
            {
                using JsonDocument snapshot = await GetAsync(kind + "/current", token).ConfigureAwait(false);
                JsonElement root = snapshot.RootElement;
                bool v11 = Text(root, "schema") != "cdi.bridge.snapshot.v1";
                if ((Text(root, "schema") is { Length: > 0 } schema && schema is not ("cdi.bridge.snapshot.v1" or "obs-earthquake.bridge.snapshot.v1")) ||
                    (Text(root, "type") is { Length: > 0 } snapshotType && snapshotType != kind))
                    throw new IOException("Unsupported Bridge snapshot schema.");
                if (kind == "eew" && root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
                    foreach (var payload in events.EnumerateArray())
                        await WriteSnapshotAsync(writer, "eew", payload, v11, token).ConfigureAwait(false);
                if (kind == "earthquake" && root.TryGetProperty("event", out var quake))
                    await WriteSnapshotAsync(writer, "earthquake", quake, v11, token).ConfigureAwait(false);
                if (kind == "tsunami")
                    foreach (string part in new[] { "forecast", "observation" })
                        if (root.TryGetProperty(part, out var payload))
                            await WriteSnapshotAsync(writer, "tsunami." + part, payload, v11, token).ConfigureAwait(false);
            }
            // Byte-bounded SSE parser, including individual lines, avoids unbounded ReadLineAsync.
            byte[] bytes = new byte[8192];
            using var frame = new MemoryStream();
            int previous = -1;
            while (!token.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                int count = await stream.ReadAsync(bytes, idle.Token).ConfigureAwait(false);
                if (count == 0) break;
                for (int i = 0; i < count; i++)
                {
                    byte b = bytes[i];
                    if (b == '\r') continue;
                    if (frame.Length >= MaximumPayload) throw new IOException("Bridge SSE frame too large.");
                    frame.WriteByte(b);
                    if (b == '\n' && previous == '\n')
                    {
                        string data = string.Join("\n", Encoding.UTF8.GetString(frame.ToArray()).Split('\n')
                            .Where(static line => line.StartsWith("data:", StringComparison.Ordinal))
                            .Select(static line => line[5..].TrimStart(' ')));
                        frame.SetLength(0);
                        if (data.Length > 0)
                        {
                            using JsonDocument doc = JsonDocument.Parse(data);
                            JsonElement envelope = doc.RootElement;
                            if (!BridgeV11Payload.IsSchema(Text(envelope, "schema"))) throw new IOException("Unsupported Bridge event schema.");
                            if (Text(envelope, "type") == "bridge.status") UpdateHealth(envelope.GetProperty("payload"));
                            else
                            {
                                Connection = Connection with { LastReceivedAt = _clock.UtcNow };
                                ConnectionChanged?.Invoke(this, Connection);
                                await writer.WriteAsync(new(ProviderName, data, SourceMode.Production, _clock.UtcNow), token).ConfigureAwait(false);
                            }
                        }
                    }
                    previous = b;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            if (!token.IsCancellationRequested) SetState(ProviderConnectionState.Stale, "Bridge受信失敗・再接続します（保持情報は未解除）");
        }
        finally
        {
            lifetime.Cancel();
            await health.ConfigureAwait(false);
            writer.TryComplete();
        }
    }

    private async Task MonitorHealthAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                try
                {
                    using JsonDocument status = await GetAsync("status", token).ConfigureAwait(false);
                    UpdateHealth(status.RootElement);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
                {
                    if (token.IsCancellationRequested) return;
                    SetState(ProviderConnectionState.Stale, "Bridge上流状態の確認失敗（保持情報は未解除）");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private ValueTask WriteSnapshotAsync(ChannelWriter<RawProviderMessage> writer, string type, JsonElement payload, bool v11, CancellationToken token)
    {
        if (payload.ValueKind != JsonValueKind.Object) return ValueTask.CompletedTask;
        string envelope = JsonSerializer.Serialize(new { schema = v11 ? "obs-earthquake.bridge.event.v1" : "cdi.bridge.event.v1",
            type, mode = v11 ? Text(payload, "mode") : "live", payload });
        return writer.WriteAsync(new RawProviderMessage(ProviderName, envelope, SourceMode.Production, _clock.UtcNow)
            { IsStateSnapshot = true }, token);
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using HttpResponseMessage response = await _http.GetAsync(new Uri(BaseUri, "api/v1/" + path), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("Bridge snapshot unavailable.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaximumPayload) throw new IOException("Bridge snapshot too large.");
            memory.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(memory.ToArray());
    }

    private void UpdateHealth(JsonElement status)
    {
        bool healthy = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("feeds", out var feeds) && feeds.ValueKind == JsonValueKind.Object &&
            feeds.EnumerateObject().Any(static feed => Configured(feed.Value)) &&
            feeds.EnumerateObject().Where(static feed => Configured(feed.Value)).All(static feed =>
                feed.Value.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.True &&
                (!feed.Value.TryGetProperty("stale", out var stale) || stale.ValueKind != JsonValueKind.True));
        if (status.TryGetProperty("upstreamHealthy", out var upstream) && upstream.ValueKind != JsonValueKind.True) healthy = false;
        if (Text(status, "upstreamState") is "degraded" or "disconnected") healthy = false;
        SetState(healthy ? ProviderConnectionState.Connected : ProviderConnectionState.Stale,
            healthy ? "Bridge受信中" : "Bridge上流の接続を確認してください（保持情報は未解除）");
    }

    private static bool Configured(JsonElement feed) => feed.ValueKind == JsonValueKind.Object &&
        (!feed.TryGetProperty("configured", out var configured) || configured.ValueKind == JsonValueKind.True);

    private static string Text(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";

    private void SetState(ProviderConnectionState state, string detail)
    {
        Connection = new(state, _clock.UtcNow, Connection.LastReceivedAt, Detail: detail);
        ConnectionChanged?.Invoke(this, Connection);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) _reader?.Cancel();
        return ValueTask.CompletedTask;
    }
    public void RequestReconnect(ReconnectReason reason) { lock (_gate) _attempt?.Cancel(); }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _http.Dispose();
    }
}
