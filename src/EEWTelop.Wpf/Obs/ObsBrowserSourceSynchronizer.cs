using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Logging;

namespace EEWTelop.Wpf.Obs;

public sealed record ObsBrowserSourceUrls(
    string General,
    string Eew,
    string Tsunami,
    string Weather);

public interface IObsBrowserSourceSynchronizer : IAsyncDisposable
{
    event Action<string>? StatusChanged;

    string Status { get; }

    IReadOnlyList<string> RegisteredBrowserSources => [];

    void Configure(ObsSettings settings, ObsBrowserSourceUrls urls);

    void RequestSynchronization();
}

public sealed class ObsBrowserSourceSynchronizer : IObsBrowserSourceSynchronizer
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private const string GeneralSourceName = "CDI-Telopper 地震字幕・全ての音声";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);
    private static readonly (
        string Name,
        Func<ObsBrowserSourceUrls, string> Url,
        bool ControlsAudio)[] Sources =
    [
        (GeneralSourceName, static urls => urls.General, true),
        ("CDI-Telopper 緊急地震速報", static urls => urls.Eew, false),
        ("CDI-Telopper 津波字幕", static urls => urls.Tsunami, false),
        ("CDI-Telopper 気象情報", static urls => urls.Weather, false),
    ];
    private static readonly (string LegacyName, string CurrentName)[] LegacySourceNames =
    [
        ("QTelopper 通常字幕", GeneralSourceName),
        ("QTelopper 地震字幕・全ての音声", GeneralSourceName),
        ("QTelopper 緊急地震速報", "CDI-Telopper 緊急地震速報"),
        ("QTelopper 津波字幕", "CDI-Telopper 津波字幕"),
        ("QTelopper 気象情報", "CDI-Telopper 気象情報"),
    ];
    private static readonly string[] ObsoleteMapSourceNames =
    [
        "CDI-Telopper 地震地図",
        "CDI-Telopper 津波地図",
        "QTelopper 地震地図",
        "QTelopper 津波地図",
    ];

    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly IAppLogWriter _logWriter;
    private readonly Task _worker;
    private ObsSyncConfiguration? _configuration;
    private string _lastSuccessfulSignature = string.Empty;
    private string _status = "無効";
    private string[] _registeredBrowserSources = [];
    private bool _disposed;

    public ObsBrowserSourceSynchronizer(IAppLogWriter logWriter)
    {
        ArgumentNullException.ThrowIfNull(logWriter);
        _logWriter = logWriter;
        _worker = RunAsync(_stop.Token);
    }

    public event Action<string>? StatusChanged;

    public string Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public IReadOnlyList<string> RegisteredBrowserSources
    {
        get { lock (_gate) return _registeredBrowserSources.ToArray(); }
    }

    internal static bool ShouldControlAudio(string inputName) =>
        Sources.Any(source =>
            source.ControlsAudio &&
            string.Equals(source.Name, inputName, StringComparison.Ordinal));

    public void Configure(ObsSettings settings, ObsBrowserSourceUrls urls)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(urls);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _configuration = new ObsSyncConfiguration(settings, urls);
            if (!settings.BrowserSourceSyncEnabled)
            {
                _lastSuccessfulSignature = string.Empty;
                _registeredBrowserSources = [];
            }
        }

        RequestSynchronization();
    }

    public void RequestSynchronization()
    {
        if (_disposed)
        {
            return;
        }

        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        RequestSynchronization();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _signal.Dispose();
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(RetryInterval, cancellationToken).ConfigureAwait(false);
            ObsSyncConfiguration? configuration;
            lock (_gate)
            {
                configuration = _configuration;
            }

            if (configuration is null || !configuration.Settings.BrowserSourceSyncEnabled)
            {
                await SetStatusAsync("無効", AppLogLevel.Information, null, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            string signature = configuration.CreateSignature();
            lock (_gate)
            {
                if (string.Equals(signature, _lastSuccessfulSignature, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(AttemptTimeout);
                string[] synchronized = await SynchronizeAsync(configuration, timeout.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    _lastSuccessfulSignature = signature;
                    _registeredBrowserSources = synchronized;
                }

                await SetStatusAsync(
                    "同期済み",
                    AppLogLevel.Information,
                    $"OBSのブラウザーソース{synchronized.Length}件を確認し、作成またはURL更新しました。",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await SetStatusAsync(
                    "接続待ち（再試行中）",
                    AppLogLevel.Warning,
                    "OBS WebSocketへの接続がタイムアウトしました。10秒後に再試行します。",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                await SetStatusAsync(
                    "接続待ち（再試行中）",
                    AppLogLevel.Warning,
                    $"OBS WebSocket同期に失敗しました。10秒後に再試行します: {exception.Message}",
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string[]> SynchronizeAsync(
        ObsSyncConfiguration configuration,
        CancellationToken cancellationToken)
    {
        const string monitorType = "OBS_MONITORING_TYPE_NONE";
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(configuration.Settings.WebSocketUrl), cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument hello = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        JsonElement helloRoot = hello.RootElement;
        if (helloRoot.GetProperty("op").GetInt32() != 0)
        {
            throw new InvalidDataException("OBS WebSocket Helloを受信できませんでした。");
        }

        JsonElement helloData = helloRoot.GetProperty("d");
        string password = ObsCredentialProtector.Unprotect(
            configuration.Settings.WebSocketProtectedPassword);
        string? authentication = null;
        if (helloData.TryGetProperty("authentication", out JsonElement authenticationData))
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("OBS WebSocketパスワードが設定されていません。");
            }

            authentication = ObsWebSocketAuthentication.Create(
                password,
                authenticationData.GetProperty("salt").GetString() ?? string.Empty,
                authenticationData.GetProperty("challenge").GetString() ?? string.Empty);
        }

        object identify = authentication is null
            ? new { op = 1, d = (object)new { rpcVersion = 1, eventSubscriptions = 0 } }
            : new { op = 1, d = (object)new { rpcVersion = 1, authentication, eventSubscriptions = 0 } };
        await SendAsync(socket, identify, cancellationToken).ConfigureAwait(false);

        using JsonDocument identified = await ReceiveUntilAsync(socket, 2, cancellationToken)
            .ConfigureAwait(false);

        JsonElement inputList = await RequestAsync(
            socket,
            "GetInputList",
            new { inputKind = "browser_source" },
            cancellationToken).ConfigureAwait(false);
        var existing = new HashSet<string>(StringComparer.Ordinal);
        if (inputList.TryGetProperty("inputs", out JsonElement inputs))
        {
            foreach (JsonElement input in inputs.EnumerateArray())
            {
                string? name = input.GetProperty("inputName").GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    existing.Add(name);
                }
            }
        }

        foreach (string obsoleteName in ObsoleteMapSourceNames)
        {
            if (!existing.Contains(obsoleteName))
            {
                continue;
            }

            await RequestAsync(
                socket,
                "RemoveInput",
                new { inputName = obsoleteName },
                cancellationToken).ConfigureAwait(false);
            existing.Remove(obsoleteName);
        }

        foreach ((string legacyName, string currentName) in LegacySourceNames)
        {
            if (existing.Contains(currentName) || !existing.Contains(legacyName))
            {
                continue;
            }

            await RequestAsync(
                socket,
                "SetInputName",
                new
                {
                    inputName = legacyName,
                    newInputName = currentName,
                },
                cancellationToken).ConfigureAwait(false);
            existing.Remove(legacyName);
            existing.Add(currentName);
        }

        var activeSources = Sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Url(configuration.Urls)))
            .ToArray();
        string sceneName = configuration.Settings.TargetSceneName.Trim();
        if (activeSources.Any(source => !existing.Contains(source.Name)) && sceneName.Length == 0)
        {
            JsonElement currentScene = await RequestAsync(
                socket,
                "GetCurrentProgramScene",
                new { },
                cancellationToken).ConfigureAwait(false);
            sceneName = currentScene.GetProperty("currentProgramSceneName").GetString()
                ?? throw new InvalidDataException("OBSの現在シーン名を取得できませんでした。");
        }

        foreach ((
            string name,
            Func<ObsBrowserSourceUrls, string> urlSelector,
            bool controlsAudio) in activeSources)
        {
            string url = urlSelector(configuration.Urls);
            if (existing.Contains(name))
            {
                await RequestAsync(
                    socket,
                    "SetInputSettings",
                    new
                    {
                        inputName = name,
                        inputSettings = new
                        {
                            url,
                            width = 1920,
                            height = 1080,
                            // 音声ミキサーには一般音声ソースだけを表示する。
                            reroute_audio = controlsAudio,
                        },
                        overlay = true,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RequestAsync(
                    socket,
                    "CreateInput",
                    new
                    {
                        sceneName,
                        inputName = name,
                        inputKind = "browser_source",
                        inputSettings = new
                        {
                            url,
                            width = 1920,
                            height = 1080,
                            reroute_audio = controlsAudio,
                        },
                        sceneItemEnabled = true,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            if (controlsAudio)
            {
                // 音声はOBS出力のみに固定し、PC側のモニター再生を無効にする。
                await RequestAsync(
                    socket,
                    "SetInputAudioMonitorType",
                    new
                    {
                        inputName = name,
                        monitorType,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "CDI-Telopper synchronization completed",
                cancellationToken).ConfigureAwait(false);
        }
        return activeSources.Select(static source => source.Name).ToArray();
    }

    private static async Task<JsonElement> RequestAsync(
        ClientWebSocket socket,
        string requestType,
        object requestData,
        CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        await SendAsync(socket, new
        {
            op = 6,
            d = new { requestType, requestId, requestData },
        }, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            using JsonDocument response = await ReceiveAsync(socket, cancellationToken)
                .ConfigureAwait(false);
            JsonElement root = response.RootElement;
            if (root.GetProperty("op").GetInt32() != 7)
            {
                continue;
            }

            JsonElement data = root.GetProperty("d");
            if (!string.Equals(data.GetProperty("requestId").GetString(), requestId, StringComparison.Ordinal))
            {
                continue;
            }

            JsonElement status = data.GetProperty("requestStatus");
            if (!status.GetProperty("result").GetBoolean())
            {
                int code = status.GetProperty("code").GetInt32();
                string comment = status.TryGetProperty("comment", out JsonElement commentElement)
                    ? commentElement.GetString() ?? string.Empty
                    : string.Empty;
                throw new InvalidOperationException($"OBS要求 {requestType} が失敗しました（{code}）: {comment}");
            }

            return data.TryGetProperty("responseData", out JsonElement responseData)
                ? responseData.Clone()
                : default;
        }
    }

    private static async Task<JsonDocument> ReceiveUntilAsync(
        ClientWebSocket socket,
        int operationCode,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            JsonDocument message = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message.RootElement.GetProperty("op").GetInt32() == operationCode)
            {
                return message;
            }

            message.Dispose();
        }
    }

    private static async Task<JsonDocument> ReceiveAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        while (true)
        {
            Memory<byte> memory = buffer.GetMemory(4096);
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(memory, cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("OBS WebSocketが接続を閉じました。");
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("OBS WebSocketから非テキストデータを受信しました。");
            }

            buffer.Advance(result.Count);
            if (buffer.WrittenCount > MaximumMessageBytes)
            {
                throw new InvalidDataException("OBS WebSocket応答が上限サイズを超えました。");
            }

            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(buffer.WrittenMemory);
            }
        }
    }

    private static Task SendAsync(
        ClientWebSocket socket,
        object payload,
        CancellationToken cancellationToken)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload);
        return socket.SendAsync(
            data,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private async Task SetStatusAsync(
        string status,
        AppLogLevel level,
        string? logMessage,
        CancellationToken cancellationToken)
    {
        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_status, status, StringComparison.Ordinal);
            _status = status;
        }

        if (!changed)
        {
            return;
        }

        StatusChanged?.Invoke(status);
        if (!string.IsNullOrWhiteSpace(logMessage))
        {
            await _logWriter.WriteAsync(new AppLogEntry(
                DateTimeOffset.UtcNow,
                level,
                "ObsBrowserSourceSync",
                logMessage), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record ObsSyncConfiguration(ObsSettings Settings, ObsBrowserSourceUrls Urls)
    {
        public string CreateSignature() => string.Join(
            '\n',
            Settings.WebSocketUrl,
            Settings.WebSocketProtectedPassword,
            Settings.TargetSceneName,
            Urls.General,
            Urls.Eew,
            Urls.Tsunami,
            Urls.Weather);
    }
}

public static class ObsWebSocketAuthentication
{
    public static string Create(string password, string salt, string challenge)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(challenge);
        byte[] secret = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        string encodedSecret = Convert.ToBase64String(secret);
        byte[] authentication = SHA256.HashData(
            Encoding.UTF8.GetBytes(encodedSecret + challenge));
        return Convert.ToBase64String(authentication);
    }
}
