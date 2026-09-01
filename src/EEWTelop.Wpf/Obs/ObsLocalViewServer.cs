using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Logging;

namespace EEWTelop.Wpf.Obs;

public sealed class ObsLocalViewServer : IObsLocalViewServer
{
    private const string HtmlResource = "EEWTelop.Wpf.Obs.Assets.overlay.html";
    private const string ScriptResource = "EEWTelop.Wpf.Obs.Assets.overlay.js";
    private const int MaximumHeaderCharacters = 32 * 1024;
    private static readonly HashSet<string> AllowedAudioResults = new(StringComparer.Ordinal)
    {
        "Started",
        "Completed",
        "Failed",
        "SkippedStale",
        "Interrupted",
        "Stopped",
    };
    private readonly ObsSnapshotStore _snapshotStore;
    private readonly IClock _clock;
    private readonly IAppLogWriter _logWriter;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly ConcurrentDictionary<int, Task> _clientTasks = new();
    private readonly ConcurrentDictionary<string, int> _routeClientCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _stop;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _nextClientId;
    private int _clientCount;
    private int _snapshotIntervalMilliseconds;
    private bool _disposed;

    public ObsLocalViewServer(
        ObsSnapshotStore snapshotStore,
        IClock clock,
        IAppLogWriter logWriter,
        int snapshotIntervalMilliseconds = ObsSettings.DefaultSnapshotIntervalMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(snapshotStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logWriter);
        _snapshotStore = snapshotStore;
        _clock = clock;
        _logWriter = logWriter;
        UpdateSnapshotInterval(snapshotIntervalMilliseconds);
    }

    public event Action<int>? ClientCountChanged;

    public event Action<ObsDeliveryDiagnostic>? DeliveryReported;

    public bool IsRunning => _listener is not null;

    public int Port { get; private set; }

    public int ClientCount => Volatile.Read(ref _clientCount);

    public IReadOnlyDictionary<string, int> RouteClientCounts =>
        new Dictionary<string, int>(_routeClientCounts, StringComparer.OrdinalIgnoreCase);

    public int SnapshotIntervalMilliseconds =>
        Volatile.Read(ref _snapshotIntervalMilliseconds);

    public string LastAudioCue => _snapshotStore.ReadAudioDiagnostics().Cue;

    public string LastAudioPlaybackResult =>
        _snapshotStore.ReadAudioDiagnostics().PlaybackResult;

    public DateTimeOffset? LastAudioPlaybackAtUtc =>
        _snapshotStore.ReadAudioDiagnostics().ReportedAtUtc;

    public string OverlayUrl => IsRunning
        ? $"http://127.0.0.1:{Port}/overlay/?token={_token}"
        : string.Empty;

    public string EewUrl => BuildViewUrl("eew");

    public string TsunamiUrl => BuildViewUrl("tsunami");

    public string WeatherUrl => BuildViewUrl("weather");

    public void UpdateSnapshotInterval(int milliseconds)
    {
        if (milliseconds is < ObsSettings.MinimumSnapshotIntervalMilliseconds or
            > ObsSettings.MaximumSnapshotIntervalMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(milliseconds),
                milliseconds,
                $"OBS snapshot interval must be between {ObsSettings.MinimumSnapshotIntervalMilliseconds} and {ObsSettings.MaximumSnapshotIntervalMilliseconds} milliseconds.");
        }

        Volatile.Write(ref _snapshotIntervalMilliseconds, milliseconds);
    }

    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_listener is not null)
            {
                throw new InvalidOperationException("The OBS local view server is already running.");
            }

            var stop = new CancellationTokenSource();
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
            }
            catch
            {
                stop.Dispose();
                throw;
            }

            _stop = stop;
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync(listener, stop.Token);
        }
        finally
        {
            _lifecycle.Release();
        }

        await LogAsync(
            AppLogLevel.Information,
            "ObsLocalViewStarted",
            $"OBS Local Viewを127.0.0.1:{Port}で開始しました。",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task? acceptLoop;
        CancellationTokenSource? stop;
        try
        {
            if (_listener is null)
            {
                return;
            }

            stop = _stop;
            acceptLoop = _acceptLoop;
            stop?.Cancel();
            _listener.Stop();
            _listener = null;
            _acceptLoop = null;
            _stop = null;
            Port = 0;
        }
        finally
        {
            _lifecycle.Release();
        }

        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] clientTasks = _clientTasks.Values.ToArray();
        if (clientTasks.Length > 0)
        {
            await Task.WhenAll(clientTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        stop?.Dispose();
        await LogAsync(
            AppLogLevel.Information,
            "ObsLocalViewStopped",
            "OBS Local Viewを停止しました。",
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _lifecycle.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                int clientId = Interlocked.Increment(ref _nextClientId);
                Task task = HandleClientAsync(client, cancellationToken);
                _clientTasks[clientId] = task;
                _ = RemoveCompletedClientAsync(clientId, task);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException exception) when (!cancellationToken.IsCancellationRequested)
            {
                await LogAsync(
                    AppLogLevel.Warning,
                    "ObsAcceptFailed",
                    "OBS Local Viewのクライアント受付に失敗しました。",
                    cancellationToken,
                    exception).ConfigureAwait(false);
            }
        }
    }

    private async Task RemoveCompletedClientAsync(int clientId, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            await LogAsync(
                AppLogLevel.Error,
                "ObsClientFailed",
                "OBS Local Viewのクライアント処理に失敗しました。",
                CancellationToken.None,
                exception).ConfigureAwait(false);
        }
        finally
        {
            _clientTasks.TryRemove(clientId, out _);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remote ||
                !IPAddress.IsLoopback(remote.Address))
            {
                return;
            }

            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 2048,
                leaveOpen: true);
            HttpRequest? request = await ReadRequestAsync(reader, cancellationToken)
                .ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            if (!HasAllowedHost(request.Headers))
            {
                await WriteResponseAsync(
                    stream,
                    403,
                    "text/plain; charset=utf-8",
                    "Forbidden",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!TryParseTarget(request.Target, out Uri? uri))
            {
                await WriteResponseAsync(
                    stream,
                    400,
                    "text/plain; charset=utf-8",
                    "Bad Request",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            string path = uri!.AbsolutePath;
            if (path == "/healthz")
            {
                if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                {
                    await WriteMethodNotAllowedAsync(stream, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(
                    stream,
                    200,
                    "application/json; charset=utf-8",
                    "{\"status\":\"ok\"}",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/assets/overlay.js")
            {
                if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                {
                    await WriteMethodNotAllowedAsync(stream, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(
                    stream,
                    200,
                    "text/javascript; charset=utf-8",
                    ReadResource(ScriptResource),
                    cancellationToken,
                    securityPolicy: false).ConfigureAwait(false);
                return;
            }

            if (!HasValidToken(uri.Query))
            {
                await WriteResponseAsync(
                    stream,
                    403,
                    "text/plain; charset=utf-8",
                    "Forbidden",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/audio-status" &&
                string.Equals(request.Method, "POST", StringComparison.Ordinal))
            {
                await HandleAudioStatusAsync(stream, uri, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
            {
                await WriteMethodNotAllowedAsync(stream, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path is "/overlay" or "/overlay/" or "/eew" or "/eew/" or
                "/tsunami" or "/tsunami/" or "/weather" or "/weather/")
            {
                await WriteResponseAsync(
                    stream,
                    200,
                    "text/html; charset=utf-8",
                    ReadResource(HtmlResource),
                    cancellationToken,
                    securityPolicy: true).ConfigureAwait(false);
                return;
            }


            if (path.StartsWith("/audio/", StringComparison.Ordinal) &&
                long.TryParse(
                    path.AsSpan("/audio/".Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long audioSequence) &&
                _snapshotStore.TryReadAudio(
                    audioSequence,
                    _clock.UtcNow,
                    out ObsAudioPayload? audio) &&
                audio is not null &&
                File.Exists(audio.FilePath))
            {
                request.Headers.TryGetValue("Range", out string? rangeHeader);
                await WriteFileResponseAsync(
                    stream,
                    audio.FilePath,
                    audio.ContentType,
                    rangeHeader,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/state")
            {
                ObsViewChannel channel = ParseViewChannel(uri.Query);
                await WriteResponseAsync(
                    stream,
                    200,
                    "application/json; charset=utf-8",
                    SerializeSnapshot(_snapshotStore.Read(channel, _clock.UtcNow)),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/events")
            {
                await WriteEventStreamAsync(
                    stream,
                    ParseViewChannel(uri.Query),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(
                stream,
                404,
                "text/plain; charset=utf-8",
                "Not Found",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleAudioStatusAsync(
        NetworkStream stream,
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (!TryGetQueryParameter(uri.Query, "sequence", out string sequenceText) ||
            !long.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out long sequence) ||
            !TryGetQueryParameter(uri.Query, "result", out string result) ||
            !AllowedAudioResults.Contains(result) ||
            !_snapshotStore.TryReportAudioPlayback(
                sequence,
                result,
                _clock.UtcNow,
                out ObsAudioDiagnostics diagnostics))
        {
            await WriteResponseAsync(
                stream,
                400,
                "application/json; charset=utf-8",
                "{\"status\":\"rejected\"}",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        AppLogLevel level = result == "Failed"
            ? AppLogLevel.Error
            : result is "SkippedStale" or "Interrupted"
                ? AppLogLevel.Warning
                : AppLogLevel.Information;
        await LogAsync(
            level,
            $"ObsAudio{result}",
            $"OBSブラウザーソースから音声再生結果を受信しました。区分={diagnostics.Cue}, 結果={result}, 連番={sequence}",
            cancellationToken).ConfigureAwait(false);
        ObsDeliveryStage deliveryStage = result switch
        {
            "Started" => ObsDeliveryStage.AudioStarted,
            "Completed" => ObsDeliveryStage.AudioCompleted,
            "Failed" => ObsDeliveryStage.AudioFailed,
            _ => ObsDeliveryStage.AudioCompleted,
        };
        DeliveryReported?.Invoke(new ObsDeliveryDiagnostic(
            deliveryStage, "audio", sequence, diagnostics.Cue, 0, _clock.UtcNow));
        await WriteResponseAsync(
            stream,
            200,
            "application/json; charset=utf-8",
            "{\"status\":\"ok\"}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteEventStreamAsync(
        NetworkStream stream,
        ObsViewChannel channel,
        CancellationToken cancellationToken)
    {
        const string headers =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream; charset=utf-8\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Connection: keep-alive\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        int count = Interlocked.Increment(ref _clientCount);
        string route = ViewRoute(channel);
        _routeClientCounts.AddOrUpdate(route, 1, static (_, value) => value + 1);
        ClientCountChanged?.Invoke(count);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ObsViewSnapshot snapshot = _snapshotStore.Read(channel, _clock.UtcNow);
                string payload = string.Create(
                    CultureInfo.InvariantCulture,
                    $"id: {snapshot.Sequence}\ndata: {SerializeSnapshot(snapshot)}\n\n");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(payload), cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(SnapshotIntervalMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            count = Interlocked.Decrement(ref _clientCount);
            _routeClientCounts.AddOrUpdate(route, 0, static (_, value) => Math.Max(0, value - 1));
            ClientCountChanged?.Invoke(count);
        }
    }

    private static string ViewRoute(ObsViewChannel channel) => channel switch
    {
        ObsViewChannel.Eew => "eew",
        ObsViewChannel.Tsunami => "tsunami",
        ObsViewChannel.Weather => "weather",
        _ => "general",
    };

    private static string NormalizeRoute(string route) => route.Trim().ToLowerInvariant() switch
    {
        "eew" => "eew",
        "tsunami" => "tsunami",
        "weather" => "weather",
        _ => "general",
    };

    private static async Task<HttpRequest?> ReadRequestAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        string? requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine) || requestLine.Length > 4096)
        {
            return null;
        }

        string[] parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int totalCharacters = requestLine.Length;
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || line.Length == 0)
            {
                break;
            }

            totalCharacters += line.Length;
            if (totalCharacters > MaximumHeaderCharacters)
            {
                return null;
            }

            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return new HttpRequest(parts[0], parts[1], headers);
    }

    private bool HasAllowedHost(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Host", out string? hostValue) ||
            !Uri.TryCreate($"http://{hostValue}", UriKind.Absolute, out Uri? hostUri))
        {
            return false;
        }

        bool allowedHost = string.Equals(hostUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(hostUri.Host, out IPAddress? address) && IPAddress.IsLoopback(address));
        if (!allowedHost || (hostUri.Port != Port && !hostUri.IsDefaultPort))
        {
            return false;
        }

        if (!headers.TryGetValue("Origin", out string? origin) ||
            string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri) &&
            (string.Equals(originUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
             (IPAddress.TryParse(originUri.Host, out IPAddress? originAddress) &&
              IPAddress.IsLoopback(originAddress)));
    }

    private static bool TryParseTarget(string target, out Uri? uri)
    {
        if (target.Length == 0 || target[0] != '/')
        {
            uri = null;
            return false;
        }

        return Uri.TryCreate($"http://127.0.0.1{target}", UriKind.Absolute, out uri);
    }

    private bool HasValidToken(string query)
    {
        string supplied = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Split('=', 2))
            .Where(static pair => pair.Length == 2 && pair[0] == "token")
            .Select(static pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault() ?? string.Empty;

        byte[] expected = Encoding.UTF8.GetBytes(_token);
        byte[] actual = Encoding.UTF8.GetBytes(supplied);
        return expected.Length == actual.Length &&
            CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool TryGetQueryParameter(
        string query,
        string name,
        out string value)
    {
        foreach (string part in query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], name, StringComparison.Ordinal))
            {
                value = Uri.UnescapeDataString(pair[1]);
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private string BuildViewUrl(string path) => IsRunning
        ? $"http://127.0.0.1:{Port}/{path}/?token={_token}"
        : string.Empty;

    private static ObsViewChannel ParseViewChannel(string query)
    {
        if (!TryGetQueryParameter(query, "view", out string value))
        {
            return ObsViewChannel.General;
        }

        return value switch
        {
            "eew" => ObsViewChannel.Eew,
            "tsunami" => ObsViewChannel.Tsunami,
            "weather" => ObsViewChannel.Weather,
            _ => ObsViewChannel.General,
        };
    }

    private static string SerializeSnapshot(ObsViewSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, ObsJsonContext.Default.ObsViewSnapshot);

    private static string ReadResource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string contentType,
        string content,
        CancellationToken cancellationToken,
        bool securityPolicy = false)
    {
        string reason = statusCode switch
        {
            200 => "OK",
            206 => "Partial Content",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error",
        };
        byte[] body = Encoding.UTF8.GetBytes(content);
        var headers = new StringBuilder()
            .Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("Cross-Origin-Resource-Policy: same-origin\r\n")
            .Append("Connection: close\r\n");
        if (securityPolicy)
        {
            headers.Append(
                "Content-Security-Policy: default-src 'none'; script-src 'self'; " +
                "style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; " +
                "media-src 'self'; font-src 'none'; base-uri 'none'; form-action 'none'; " +
                "frame-ancestors 'none'\r\n");
        }

        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteMethodNotAllowedAsync(
        NetworkStream stream,
        CancellationToken cancellationToken) => WriteResponseAsync(
            stream,
            405,
            "text/plain; charset=utf-8",
            "Method Not Allowed",
            cancellationToken);

    private static async Task WriteFileResponseAsync(
        NetworkStream stream,
        string filePath,
        string contentType,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        long start = 0;
        long end = file.Length - 1;
        bool partial = !string.IsNullOrWhiteSpace(rangeHeader);
        if (partial && !TryParseByteRange(rangeHeader!, file.Length, out start, out end))
        {
            string invalidRangeHeaders =
                "HTTP/1.1 416 Range Not Satisfiable\r\n" +
                $"Content-Range: bytes */{file.Length}\r\n" +
                "Content-Length: 0\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n";
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(invalidRangeHeaders),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        long contentLength = end - start + 1;
        var headers = new StringBuilder()
            .Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(contentLength).Append("\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Cross-Origin-Resource-Policy: same-origin\r\n")
            .Append("Connection: close\r\n");
        if (partial)
        {
            headers.Append("Content-Range: bytes ")
                .Append(start).Append('-').Append(end).Append('/').Append(file.Length)
                .Append("\r\n");
        }

        headers.Append("\r\n");
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes(headers.ToString()),
            cancellationToken).ConfigureAwait(false);
        file.Position = start;
        byte[] buffer = new byte[64 * 1024];
        long remaining = contentLength;
        while (remaining > 0)
        {
            int read = await file.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static bool TryParseByteRange(
        string rangeHeader,
        long fileLength,
        out long start,
        out long end)
    {
        start = 0;
        end = fileLength - 1;
        if (fileLength <= 0 ||
            !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) ||
            rangeHeader.Contains(','))
        {
            return false;
        }

        string[] bounds = rangeHeader["bytes=".Length..].Split('-', 2);
        if (bounds.Length != 2)
        {
            return false;
        }

        if (bounds[0].Length == 0)
        {
            if (!long.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out long suffixLength) ||
                suffixLength <= 0)
            {
                return false;
            }

            start = Math.Max(0, fileLength - suffixLength);
            return true;
        }

        if (!long.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
            start < 0 || start >= fileLength)
        {
            return false;
        }

        if (bounds[1].Length == 0)
        {
            return true;
        }

        if (!long.TryParse(
                bounds[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long requestedEnd) ||
            requestedEnd < start)
        {
            return false;
        }

        end = Math.Min(requestedEnd, fileLength - 1);
        return true;
    }

    private ValueTask LogAsync(
        AppLogLevel level,
        string eventName,
        string message,
        CancellationToken cancellationToken,
        Exception? exception = null) => _logWriter.WriteAsync(
            new AppLogEntry(_clock.UtcNow, level, eventName, message, exception),
            cancellationToken);

    private sealed record HttpRequest(
        string Method,
        string Target,
        IReadOnlyDictionary<string, string> Headers);
}
