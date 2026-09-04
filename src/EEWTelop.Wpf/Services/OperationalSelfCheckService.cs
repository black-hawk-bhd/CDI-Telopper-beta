using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Operations;
using EEWTelop.Infrastructure.Settings;
using EEWTelop.Wpf.Obs;

namespace EEWTelop.Wpf.Services;

public sealed class OperationalSelfCheckService(
    EventReceptionService reception,
    IObsLocalViewServer? obsServer,
    IObsBrowserSourceSynchronizer? obsSynchronizer,
    string dataDirectory)
{
    private static readonly HashSet<string> SupportedAudioExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".ogg" };

    public Task<IReadOnlyList<SelfCheckResult>> RunAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<SelfCheckResult>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            AppSettings normalizedSettings = JsonSettingsStore.NormalizeDocument(settings);
            Add("現在設定", SelfCheckStatus.Passed,
                $"スキーマ・必須項目・値域を検証済み / スキーマ {normalizedSettings.SchemaVersion}");
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            Add("現在設定", SelfCheckStatus.Failed, "設定検証失敗: " + exception.Message);
        }
        AddSettingsFile();
        AddProviderAuthentication(settings);

        IReadOnlyList<ProviderBranchConnectionSnapshot> providers = reception.GetProviderConnections();
        foreach (ProviderBranchConnectionSnapshot provider in providers)
        {
            SelfCheckStatus status = provider.Connection.State switch
            {
                ProviderConnectionState.Connected or ProviderConnectionState.Stale => SelfCheckStatus.Passed,
                ProviderConnectionState.Connecting or ProviderConnectionState.Reconnecting => SelfCheckStatus.Warning,
                _ => SelfCheckStatus.Failed,
            };
            string state = provider.Connection.State == ProviderConnectionState.Stale
                ? ProviderConnectionState.Connected.ToString()
                : provider.Connection.State.ToString();
            string detail = provider.Connection.State == ProviderConnectionState.Stale
                ? string.Empty
                : provider.Connection.Detail ?? string.Empty;
            Add($"受信元: {provider.Name}", status,
                $"{state} {detail}".Trim());
        }

        Add("OBS Local View", obsServer?.IsRunning == true ? SelfCheckStatus.Passed : SelfCheckStatus.Failed,
            obsServer?.IsRunning == true ? $"127.0.0.1:{obsServer.Port} 稼働中" : "停止中");
        if (obsServer is not null)
        {
            foreach (string route in new[] { "general", "eew", "tsunami", "weather" })
            {
                int count = obsServer.RouteClientCounts.TryGetValue(route, out int value) ? value : 0;
                Add($"OBSルート: {route}", count > 0 ? SelfCheckStatus.Passed : SelfCheckStatus.Warning,
                    $"接続数 {count}");
            }
        }
        bool obsSynchronizationSucceeded = obsSynchronizer is not null &&
            string.Equals(obsSynchronizer.Status, "同期済み", StringComparison.Ordinal) &&
            obsSynchronizer.RegisteredBrowserSources.Count > 0;
        Add("OBS WebSocket", obsSynchronizationSucceeded
                ? SelfCheckStatus.Passed
                : SelfCheckStatus.Warning,
            obsSynchronizer?.Status ?? "利用不可");
        if (settings.Obs.BrowserSourceSyncEnabled)
        {
            IReadOnlyList<string> registered = obsSynchronizer?.RegisteredBrowserSources ?? [];
            Add("OBS Browser Source登録", registered.Count > 0 ? SelfCheckStatus.Passed : SelfCheckStatus.Warning,
                registered.Count > 0 ? string.Join("、", registered) : "登録済みソースを確認できません");
        }

        foreach ((string label, string path) in EnumerateAudio(settings.Audio))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            bool exists = File.Exists(path);
            bool supported = SupportedAudioExtensions.Contains(Path.GetExtension(path));
            bool readable = exists && CanRead(path);
            Add($"音声: {label}", readable && supported ? SelfCheckStatus.Passed : SelfCheckStatus.Failed,
                !exists ? "ファイルがありません" : !readable ? "読み取れません" : supported ? "読取可能" : "未対応形式");
        }

        foreach (string directory in new[]
        {
            dataDirectory,
            Path.Combine(dataDirectory, "raw-reception"),
            Path.Combine(dataDirectory, "profiles"),
            Path.Combine(dataDirectory, "test-library"),
        })
        {
            Add($"書込: {Path.GetFileName(directory)}", CanWrite(directory)
                ? SelfCheckStatus.Passed : SelfCheckStatus.Failed, directory);
        }

        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(dataDirectory)) ??
                throw new InvalidDataException("保存先ドライブを特定できません。");
            long free = new DriveInfo(root).AvailableFreeSpace;
            SelfCheckStatus diskStatus = free < 256L * 1024 * 1024 ? SelfCheckStatus.Failed :
                free < 1024L * 1024 * 1024 ? SelfCheckStatus.Warning : SelfCheckStatus.Passed;
            Add("空き容量", diskStatus, $"{free / 1024d / 1024d:N0} MB");
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            Add("空き容量", SelfCheckStatus.Failed, "確認できません: " + exception.Message);
        }
        return Task.FromResult<IReadOnlyList<SelfCheckResult>>(results);

        void Add(string name, SelfCheckStatus status, string detail) =>
            results.Add(new SelfCheckResult(name, status, detail, now));

        void AddProviderAuthentication(AppSettings value)
        {
            ProviderSettings provider = value.Provider;
            switch (provider.ReceptionProvider)
            {
                case ReceptionProvider.Disabled:
                    Add("受信API設定", SelfCheckStatus.Passed,
                        "すべての情報種別が「受信しない」のためAPIへ接続しません");
                    break;
                case ReceptionProvider.Axis:
                    Add("AXIS認証設定", string.IsNullOrWhiteSpace(provider.AxisProtectedAccessToken)
                        ? SelfCheckStatus.Failed : SelfCheckStatus.Passed,
                        string.IsNullOrWhiteSpace(provider.AxisProtectedAccessToken) ? "アクセストークン未設定" : "暗号化済みトークン設定あり");
                    break;
                case ReceptionProvider.Dmdata:
                    bool encrypted = !string.IsNullOrWhiteSpace(
                        provider.DmdataProtectedCredential);
                    string variable = provider.DmdataCredentialEnvironmentVariable;
                    bool legacyEnvironment = !encrypted &&
                        !string.IsNullOrWhiteSpace(variable) &&
                        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable));
                    bool configured = encrypted || legacyEnvironment;
                    Add("dmdata認証設定", configured ? SelfCheckStatus.Passed : SelfCheckStatus.Failed,
                        encrypted ? "DPAPI暗号化済み認証情報あり" :
                        legacyEnvironment ? $"旧環境変数 {variable} に設定あり（次回保存時に移行）" :
                        "認証情報未設定");
                    break;
                default:
                    Add("P2P認証設定", SelfCheckStatus.Passed, "認証情報は不要です");
                    break;
            }
        }

        void AddSettingsFile()
        {
            string path = Path.Combine(dataDirectory, "settings.json");
            if (!File.Exists(path))
            {
                Add("設定保存ファイル", SelfCheckStatus.Warning, "settings.jsonはまだ保存されていません");
                return;
            }
            try
            {
                (AppSettings normalized, int sourceSchema) =
                    JsonSettingsStore.ReadAndNormalizeDocument(path);
                Add("設定保存ファイル", SelfCheckStatus.Passed,
                    sourceSchema == normalized.SchemaVersion
                        ? $"読取・検証可能 / スキーマ {normalized.SchemaVersion}"
                        : $"読取・移行・検証可能 / スキーマ {sourceSchema} → {normalized.SchemaVersion}");
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                Add("設定保存ファイル", SelfCheckStatus.Failed, "読取・移行・設定検証失敗: " + exception.Message);
            }
        }
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $".selfcheck-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(path, "ok");
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException) { return false; }
    }

    private static bool CanRead(string path)
    {
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _ = stream.ReadByte();
            return true;
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException) { return false; }
    }

    private static IEnumerable<(string Label, string Path)> EnumerateAudio(AudioSettings audio)
    {
        yield return ("EEW初報", audio.EewInitialFilePath);
        yield return ("EEW続報", audio.EewContinuationFilePath);
        yield return ("EEW取消", audio.EewCancellationFilePath);
        yield return ("気象レベル5", audio.WeatherSpecialWarningFilePath);
        yield return ("気象防災速報", audio.WeatherDisasterPreventionBulletinFilePath);
        yield return ("気象レベル4～3", audio.WeatherWarningFilePath);
        yield return ("気象レベル2", audio.WeatherAdvisoryFilePath);
        foreach (var pair in audio.QuakeScaleCues) yield return ($"震度{pair.Key}", pair.Value.FilePath);
        foreach (var pair in audio.TsunamiGradeCues) yield return ($"津波{pair.Key}", pair.Value.FilePath);
    }
}
