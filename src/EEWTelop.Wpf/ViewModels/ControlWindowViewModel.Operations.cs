using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using EEWTelop.Application.Audio;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Events;
using EEWTelop.Application.Operations;
using EEWTelop.Domain.Events;
#if QTELOPPER_AXIS_PROVIDER
using EEWTelop.Infrastructure.Axis.Configuration;
using EEWTelop.Infrastructure.Axis.Transport;
#endif
using EEWTelop.Infrastructure.Operations;
using EEWTelop.Wpf.Mvvm;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.Services;

namespace EEWTelop.Wpf.ViewModels;

public sealed partial class ControlWindowViewModel
{
    private readonly Dictionary<string, Queue<DateTimeOffset>> _parseFailures = new(StringComparer.Ordinal);
    private CancellationTokenSource? _operationsStop;
    private Task _operationsMonitorTask = Task.CompletedTask;
    private string _newProfileName = "通常運用";
    private string? _selectedProfileName;
    private OperationalTestCaseViewModel? _selectedLibraryCase;
    private string _testLibrarySearchText = string.Empty;
    private string _operationalStatusText = "点検待ち";
    private bool _isSelfCheckRunning;

    public event Action? ImportProfileRequested;
    public event Action<string>? ExportProfileRequested;
    public event Action? ImportTestFilesRequested;
    public event Action? ImportDmdataArchiveRequested;
    public event Action? ImportTestPackageRequested;
    public event Action<OperationalTestCaseViewModel>? ExportTestPackageRequested;
    public event Action<OperationalAlert>? OperatorNotificationRequested;
    public event Action<SettingsEditorViewModel, SettingsEditorViewModel>? SettingsEditorChanged;

    public ObservableCollection<SelfCheckResult> SelfCheckResults { get; } = [];
    public ObservableCollection<OperationalAlert> OperationalAlerts { get; } = [];
    public ObservableCollection<SourceComparisonResult> SourceComparisons { get; } = [];
    public ObservableCollection<string> ProfileNames { get; } = [];
    public ObservableCollection<OperationalTestCaseViewModel> TestLibraryCases { get; } = [];

    public string TestLibrarySearchText
    {
        get => _testLibrarySearchText;
        set
        {
            if (SetProperty(ref _testLibrarySearchText, value)) RefreshTestLibrary();
        }
    }

    public string NewProfileName
    {
        get => _newProfileName;
        set => SetProperty(ref _newProfileName, value);
    }

    public string? SelectedProfileName
    {
        get => _selectedProfileName;
        set
        {
            if (SetProperty(ref _selectedProfileName, value))
            {
                ApplyProfileCommand.RaiseCanExecuteChanged();
                DeleteProfileCommand.RaiseCanExecuteChanged();
                ExportProfileCommand.RaiseCanExecuteChanged();
                DuplicateProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public OperationalTestCaseViewModel? SelectedLibraryCase
    {
        get => _selectedLibraryCase;
        set
        {
            if (SetProperty(ref _selectedLibraryCase, value))
            {
                RunLibraryCaseCommand.RaiseCanExecuteChanged();
                DeleteLibraryCaseCommand.RaiseCanExecuteChanged();
                ExportLibraryCaseCommand.RaiseCanExecuteChanged();
                DuplicateLibraryCaseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string OperationalStatusText
    {
        get => _operationalStatusText;
        private set => SetProperty(ref _operationalStatusText, value);
    }

    public bool IsSelfCheckRunning
    {
        get => _isSelfCheckRunning;
        private set
        {
            if (SetProperty(ref _isSelfCheckRunning, value)) RunSelfCheckCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand RunSelfCheckCommand { get; private set; } = null!;
    public RelayCommand SaveProfileCommand { get; private set; } = null!;
    public RelayCommand ApplyProfileCommand { get; private set; } = null!;
    public RelayCommand DuplicateProfileCommand { get; private set; } = null!;
    public RelayCommand DeleteProfileCommand { get; private set; } = null!;
    public RelayCommand ImportProfileCommand { get; private set; } = null!;
    public RelayCommand ExportProfileCommand { get; private set; } = null!;
    public RelayCommand ImportTestFilesCommand { get; private set; } = null!;
    public RelayCommand ImportDmdataArchiveCommand { get; private set; } = null!;
    public RelayCommand ImportTestPackageCommand { get; private set; } = null!;
    public RelayCommand ExportLibraryCaseCommand { get; private set; } = null!;
    public RelayCommand DeleteLibraryCaseCommand { get; private set; } = null!;
    public RelayCommand DeleteAllLibraryCasesCommand { get; private set; } = null!;
    public RelayCommand RunLibraryCaseCommand { get; private set; } = null!;
    public RelayCommand DuplicateLibraryCaseCommand { get; private set; } = null!;

    private void InitializeOperationalFeatures()
    {
        RunSelfCheckCommand = new RelayCommand(() => _ = RunSelfCheckAsync(), () => !IsSelfCheckRunning);
        SaveProfileCommand = new RelayCommand(() => _ = SaveProfileAsync(NewProfileName));
        ApplyProfileCommand = new RelayCommand(() => _ = ApplyProfileAsync(), () => SelectedProfileName is not null);
        DuplicateProfileCommand = new RelayCommand(() => _ = DuplicateProfileAsync(), () => SelectedProfileName is not null);
        DeleteProfileCommand = new RelayCommand(() => _ = DeleteProfileAsync(), () => SelectedProfileName is not null);
        ImportProfileCommand = new RelayCommand(() => ImportProfileRequested?.Invoke());
        ExportProfileCommand = new RelayCommand(() =>
        {
            if (SelectedProfileName is { } name) ExportProfileRequested?.Invoke(name);
        }, () => SelectedProfileName is not null);
        ImportTestFilesCommand = new RelayCommand(() => ImportTestFilesRequested?.Invoke());
        ImportDmdataArchiveCommand = new RelayCommand(() => ImportDmdataArchiveRequested?.Invoke());
        ImportTestPackageCommand = new RelayCommand(() => ImportTestPackageRequested?.Invoke());
        ExportLibraryCaseCommand = new RelayCommand(() =>
        {
            if (SelectedLibraryCase is { } item) ExportTestPackageRequested?.Invoke(item);
        }, () => SelectedLibraryCase is not null);
        DeleteLibraryCaseCommand = new RelayCommand(() => _ = DeleteLibraryCaseAsync(), () => SelectedLibraryCase is not null);
        DeleteAllLibraryCasesCommand = new RelayCommand(
            () => _ = DeleteAllLibraryCasesAsync(),
            () => _services.TestCaseLibrary?.List().Count > 0);
        RunLibraryCaseCommand = new RelayCommand(() => _ = RunLibraryCaseAsync(SelectedLibraryCase!), () => SelectedLibraryCase is not null);
        DuplicateLibraryCaseCommand = new RelayCommand(() => _ = DuplicateLibraryCaseAsync(), () => SelectedLibraryCase is not null);

        RefreshTestLibrary();
    }

    private void DisposeOperationalFeatures()
    {
        if (_services.OperationalAlerts is { } alerts) alerts.AlertRaised -= OnOperationalAlertRaised;
        if (_services.SourceComparison is { } comparison) comparison.ResultUpdated -= OnSourceComparisonUpdated;
        if (_obsServer is not null) _obsServer.DeliveryReported -= OnObsDeliveryReported;
        _operationsStop?.Cancel();
        _operationsStop?.Dispose();
        _operationsStop = null;
    }

    private async Task RunSelfCheckAsync()
    {
        IsSelfCheckRunning = true;
        OperationalStatusText = "点検中（現在の字幕・音声・接続状態は変更しません）";
        try
        {
            var checker = new OperationalSelfCheckService(
                _services.ReceptionService, _obsServer, _obsBrowserSourceSynchronizer, _services.DataDirectory);
            IReadOnlyList<SelfCheckResult> results = await checker.RunAsync(_settings).ConfigureAwait(false);
            _dispatcher.Invoke(() =>
            {
                SelfCheckResults.Clear();
                foreach (SelfCheckResult result in results) SelfCheckResults.Add(result);
                int failures = results.Count(static result => result.Status == SelfCheckStatus.Failed);
                int warnings = results.Count(static result => result.Status == SelfCheckStatus.Warning);
                OperationalStatusText = $"点検完了: 異常 {failures} / 警告 {warnings} / 正常 {results.Count - failures - warnings}";
            });
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            _dispatcher.Invoke(() => OperationalStatusText = "点検失敗: " + exception.Message);
        }
        finally { _dispatcher.Invoke(() => IsSelfCheckRunning = false); }
    }

    private async Task SaveProfileAsync(string name)
    {
        if (_services.ProfileStore is null) return;
        try
        {
            await _services.ProfileStore.SaveAsync(name, Settings.ToSettings(_settings), ApplicationVersionText).ConfigureAwait(false);
            _dispatcher.Invoke(() => { RefreshProfiles(); SelectedProfileName = name.Trim(); OperationalStatusText = "プロファイルを保存しました"; });
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { _dispatcher.Invoke(() => OperationalStatusText = "プロファイルを保存できません: " + exception.Message); }
    }

    private async Task ApplyProfileAsync()
    {
        if (_services.ProfileStore is null || SelectedProfileName is null) return;
        try
        {
            SettingsProfileDocument profile = await _services.ProfileStore.LoadAsync(SelectedProfileName, _settings).ConfigureAwait(false);
            string differences = DescribeSettingsDifferences(_settings, profile.Settings);
            if (profile.MigrationIssues.Count > 0)
                differences = "移行・適用上の注意:\n- " +
                    string.Join("\n- ", profile.MigrationIssues) + "\n\n" + differences;
            bool confirmed = false;
            _dispatcher.Invoke(() => confirmed = _confirmationService.ConfirmProfileApply(differences));
            if (!confirmed) return;
            _dispatcher.Invoke(() =>
            {
                SettingsEditorViewModel oldEditor = Settings;
                Settings = new SettingsEditorViewModel(profile.Settings);
                OnPropertyChanged(nameof(Settings));
                SettingsEditorChanged?.Invoke(oldEditor, Settings);
            });
            await SaveSettingsAsync().ConfigureAwait(false);
            _dispatcher.Invoke(() => OperationalStatusText = "プロファイルを適用しました。端末の認証情報は維持されています。" );
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { _dispatcher.Invoke(() => OperationalStatusText = "プロファイルを適用できません: " + exception.Message); }
    }

    private async Task DuplicateProfileAsync()
    {
        if (_services.ProfileStore is null || SelectedProfileName is null) return;
        SettingsProfileDocument source = await _services.ProfileStore.LoadAsync(SelectedProfileName, _settings).ConfigureAwait(false);
        string name = string.IsNullOrWhiteSpace(NewProfileName) ? source.Name + " のコピー" : NewProfileName;
        await _services.ProfileStore.SaveAsync(name, source.Settings, ApplicationVersionText).ConfigureAwait(false);
        _dispatcher.Invoke(RefreshProfiles);
    }

    private async Task DeleteProfileAsync()
    {
        if (_services.ProfileStore is null || SelectedProfileName is null) return;
        await _services.ProfileStore.DeleteAsync(SelectedProfileName).ConfigureAwait(false);
        _dispatcher.Invoke(() => { SelectedProfileName = null; RefreshProfiles(); });
    }

    public async Task ImportProfileAsync(string path)
    {
        if (_services.ProfileStore is null) return;
        SettingsProfileDocument imported = await _services.ProfileStore.ImportAsync(path, _settings).ConfigureAwait(false);
        _dispatcher.Invoke(() =>
        {
            RefreshProfiles();
            SelectedProfileName = imported.Name;
            OperationalStatusText = imported.MigrationIssues.Count == 0
                ? "プロファイルを取り込みました"
                : $"プロファイルを取り込みました（移行通知 {imported.MigrationIssues.Count}件）";
        });
    }

    public Task ExportProfileAsync(string name, string path) =>
        _services.ProfileStore?.ExportAsync(name, path) ?? Task.CompletedTask;

    public async Task ImportTestFilesAsync(IReadOnlyList<string> paths)
    {
        if (_services.TestCaseLibrary is null) return;
        string name = Path.GetFileNameWithoutExtension(paths.Count > 0 ? paths[0] : "テストケース");
        await _services.TestCaseLibrary.ImportFilesAsync(name, paths).ConfigureAwait(false);
        _dispatcher.Invoke(RefreshTestLibrary);
    }

    public async Task ImportDmdataArchiveAsync(string telegramsIndexPath)
    {
        if (_services.TestCaseLibrary is null) return;
        IReadOnlyList<TestCaseManifest> imported = await _services.TestCaseLibrary
            .ImportDmdataArchiveAsync(telegramsIndexPath).ConfigureAwait(false);
        _dispatcher.Invoke(() =>
        {
            RefreshTestLibrary();
            OperationalStatusText = $"dmdata生データを{imported.Count}件登録しました（表示解析はXML正本のみ）。";
        });
    }

    public async Task ImportTestPackageAsync(string path)
    {
        if (_services.TestCaseLibrary is null) return;
        await _services.TestCaseLibrary.ImportPackageAsync(path).ConfigureAwait(false);
        _dispatcher.Invoke(RefreshTestLibrary);
    }

    public Task ExportTestPackageAsync(string id, string path) =>
        _services.TestCaseLibrary?.ExportAsync(id, path) ?? Task.CompletedTask;

    private async Task DeleteLibraryCaseAsync()
    {
        if (_services.TestCaseLibrary is null || SelectedLibraryCase is null) return;
        await _services.TestCaseLibrary.DeleteAsync(SelectedLibraryCase.Manifest.Id).ConfigureAwait(false);
        _dispatcher.Invoke(RefreshTestLibrary);
    }

    private async Task DeleteAllLibraryCasesAsync()
    {
        if (_services.TestCaseLibrary is null) return;
        int count = _services.TestCaseLibrary.List().Count;
        if (count == 0) return;

        bool confirmed = false;
        _dispatcher.Invoke(() => confirmed = _confirmationService.ConfirmDeleteAllTestCases(count));
        if (!confirmed) return;

        try
        {
            await _services.TestCaseLibrary.DeleteAllAsync().ConfigureAwait(false);
            _dispatcher.Invoke(() =>
            {
                SelectedLibraryCase = null;
                RefreshTestLibrary();
                OperationalStatusText = $"テストケースを{count}件削除しました。";
            });
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            _dispatcher.Invoke(() => OperationalStatusText = "テストケースを一括削除できません: " + exception.Message);
        }
    }

    private async Task DuplicateLibraryCaseAsync()
    {
        if (_services.TestCaseLibrary is null || SelectedLibraryCase is null) return;
        await _services.TestCaseLibrary.DuplicateAsync(SelectedLibraryCase.Manifest.Id).ConfigureAwait(false);
        _dispatcher.Invoke(RefreshTestLibrary);
    }

    private Task RunLibraryCaseAsync(OperationalTestCaseViewModel item) => Task.Run(() =>
    {
        try
        {
            IReadOnlyList<RawProviderMessage> messages = _services.TestCaseLibrary!
                .LoadMessages(item.Manifest.Id, SourceMode.HistoryRehearsal)
                .Select(PrepareIsolatedTestMessage)
                .ToArray();
            var coordinator = new PriorityCoordinator(_services.Clock, _settings.Display);
            var pipeline = new EventIngestionPipeline(_services.EventNormalizer, new EventVersionCache(),
                _services.PageComposer, coordinator, _settings.Display, _settings.Filter);
            EventIngestionResult[] results = messages.Select(pipeline.Process).ToArray();
            string verdict = EvaluateExpectation(item.Manifest.Expectation, results);
            EventIngestionResult? visible = results.LastOrDefault(static result =>
                result.Status == EventIngestionStatus.Accepted &&
                result.Event is not null &&
                result.Program is { Pages.Count: > 0 });
            if (visible is null)
            {
                EventIngestionResult? last = results.LastOrDefault();
                string reason = last is null
                    ? "入力なし"
                    : last.Status == EventIngestionStatus.Accepted
                        ? "表示ページが生成されませんでした"
                        : $"処理結果={last.Status}";
                verdict = $"失敗: {reason}";
            }
            _dispatcher.Invoke(() =>
            {
                item.ResultText = verdict;
                if (visible?.Event is { } disasterEvent && visible.Program is { } program)
                {
                    DisplayRehearsal(disasterEvent, program, "テストライブラリ／訓練");
                    ReceptionStatusText = "テストライブラリをリハーサル出力しました";
                }
            });
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { _dispatcher.Invoke(() => item.ResultText = "失敗: " + exception.Message); }
    });

    private static RawProviderMessage PrepareIsolatedTestMessage(RawProviderMessage message)
    {
#if QTELOPPER_AXIS_PROVIDER
        if (message.ContentFormat != RawProviderContentFormat.Json ||
            !string.Equals(message.Provider, "axis", StringComparison.OrdinalIgnoreCase))
            return message;

        if (!AxisTestPayloadDecoder.TryDecode(
            message.Payload,
            out string providerPayload,
            out RawProviderContentFormat contentFormat,
            out string reason))
            throw new InvalidDataException($"AXIS transport.jsonを復元できません: {reason}");

        string provider = contentFormat == RawProviderContentFormat.JmaXml
            ? FileTestCaseLibrary.JmaXmlTestProviderName
            : AxisProviderOptions.ProviderName;
        return new RawProviderMessage(provider, providerPayload,
            message.SourceMode, message.ReceivedAt)
        {
            ContentFormat = contentFormat,
            TransportPayload = message.Payload,
            TransportContentFormat = RawProviderContentFormat.Json,
        };
#else
        return message;
#endif
    }

    private string EvaluateExpectation(TestCaseExpectation expectation, EventIngestionResult[] results)
    {
        EventIngestionResult? result = null;
        for (int index = results.Length - 1; index >= 0; index--)
        {
            if (results[index].Event is not null)
            {
                result = results[index];
                break;
            }
        }
        result ??= results.Length > 0 ? results[^1] : null;
        if (result is null) return "失敗: 入力なし";
        var failures = new List<string>();
        if (!string.IsNullOrWhiteSpace(expectation.EventKind) && !string.Equals(result.Event?.Kind.ToString(), expectation.EventKind, StringComparison.OrdinalIgnoreCase)) failures.Add("種別");
        if (!string.IsNullOrWhiteSpace(expectation.Status) && !string.Equals(result.Status.ToString(), expectation.Status, StringComparison.OrdinalIgnoreCase)) failures.Add("状態");
        if (expectation.IsCancelled is bool cancelled && result.Event?.IsCancelled != cancelled) failures.Add("取消判定");
        if (expectation.IsUpdate is bool update && IsUpdateEvent(result.Event) != update) failures.Add("更新判定");
        if (expectation.IsReleased is bool released && IsReleaseEvent(result.Event) != released) failures.Add("解除判定");
        if (expectation.PageCount is int pages && result.Program?.Pages.Count != pages) failures.Add("ページ数");
        string text = result.Program is null ? string.Empty : string.Join("\n", result.Program.Pages.SelectMany(static page => page.Blocks).Select(static block => $"{block.Badge}\n{block.PrimaryText}\n{block.SecondaryText}"));
        if (expectation.RequiredBadges.Any(badge => !text.Contains(badge, StringComparison.Ordinal))) failures.Add("バッジ");
        if (expectation.RequiredTextFragments.Any(fragment => !text.Contains(fragment, StringComparison.Ordinal))) failures.Add("本文");
        string[] areas = GetEventAreas(result.Event).Distinct(StringComparer.CurrentCulture).ToArray();
        if (expectation.RequiredAreas.Any(required => !areas.Contains(required, StringComparer.CurrentCulture))) failures.Add("表示地域");
        if (!string.IsNullOrWhiteSpace(expectation.AudioCue))
        {
            string cue = result.Event is null
                ? string.Empty
                : new AudioPolicy().Evaluate(result.Event, _settings.Audio with { TestUsesProductionSound = true }).Cue?.ToString() ?? string.Empty;
            if (!string.Equals(cue, expectation.AudioCue, StringComparison.OrdinalIgnoreCase)) failures.Add("音声種別");
        }
        if (!string.IsNullOrWhiteSpace(expectation.SuppressionReason) && !(result.DisplaySuppressionReason ?? string.Empty).Contains(expectation.SuppressionReason, StringComparison.Ordinal)) failures.Add("非表示理由");
        return failures.Count == 0 ? $"合格 ({results.Length}入力 / {result.Program?.Pages.Count ?? 0}ページ)" : "不合格: " + string.Join("、", failures);
    }

    private static bool IsUpdateEvent(DisasterEvent? disasterEvent)
    {
        string? serial = disasterEvent switch
        {
            QuakeEvent quake => quake.Issue.Serial,
            TsunamiEvent tsunami => tsunami.Issue.Serial,
            EewEvent eew => eew.Issue.Serial,
            WeatherWarningEvent weather => weather.Issue.Serial,
            VolcanoEvent volcano => volcano.Issue.Serial,
            _ => null,
        };
        return int.TryParse(serial, out int value) && value > 1;
    }

    private static bool IsReleaseEvent(DisasterEvent? disasterEvent) => disasterEvent switch
    {
        TsunamiEvent tsunami => tsunami.IsCancelled,
        WeatherWarningEvent weather => weather.IsCancelled ||
            (weather.Items.Count > 0 && weather.Items.All(static item => !item.IsActive)),
        VolcanoEvent volcano => volcano.IsCancelled,
        _ => false,
    };

    private static IEnumerable<string> GetEventAreas(DisasterEvent? disasterEvent) => disasterEvent switch
    {
        QuakeEvent quake => quake.Points.Select(static point => point.Address),
        TsunamiEvent tsunami => tsunami.Areas.Select(static area => area.Name),
        EewEvent eew => eew.Areas.Select(static area => area.Name),
        WeatherWarningEvent weather => weather.Items.Select(static item => item.AreaName),
        VolcanoEvent volcano => volcano.TargetAreas.Select(static area => area.Name),
        _ => [],
    };

    private void RefreshProfiles()
    {
        ProfileNames.Clear();
        if (_services.ProfileStore is null) return;
        foreach (string name in _services.ProfileStore.List()) ProfileNames.Add(name);
    }

    private void RefreshTestLibrary()
    {
        TestLibraryCases.Clear();
        if (_services.TestCaseLibrary is null) return;
        IEnumerable<TestCaseManifest> cases = _services.TestCaseLibrary.List();
        if (!string.IsNullOrWhiteSpace(TestLibrarySearchText))
        {
            string search = TestLibrarySearchText.Trim();
            cases = cases.Where(item => string.Join(" ", item.Name, item.Category, item.Provider,
                item.TelegramType, item.EventId, item.Description, string.Join(" ", item.Tags))
                .Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }
        foreach (TestCaseManifest item in cases) TestLibraryCases.Add(new OperationalTestCaseViewModel(item));
        DeleteAllLibraryCasesCommand?.RaiseCanExecuteChanged();
    }

    private void TrackOperationalEvent(EventIngestionResult result)
    {
        if (result.Event is { } comparedEvent && _services.SourceComparison is { } comparison)
        {
            AudioDecision selectedAudio = new AudioPolicy().Evaluate(comparedEvent, _settings.Audio);
            comparison.ObserveSelectedAudio(comparedEvent,
                selectedAudio.ShouldPlay && selectedAudio.Cue is not null ? selectedAudio.Cue.Value.ToString() : "なし",
                _services.Clock.UtcNow);
        }
        ObserveParsingHealth(result);
    }

    private void ObserveParsingHealth(EventIngestionResult result)
    {
        string eventKey = result.ReceptionSummary?.EventId ?? "unknown";
        string key = "parse-failure-" + eventKey;
        DateTimeOffset now = _services.Clock.UtcNow;
        if (result.Status != EventIngestionStatus.Invalid)
        {
            _services.OperationalAlerts?.Recover(key, "電文解析復旧", $"イベント {eventKey} の解析が復旧しました。", now);
            return;
        }
        if (!_parseFailures.TryGetValue(eventKey, out Queue<DateTimeOffset>? failures))
        {
            failures = new Queue<DateTimeOffset>();
            _parseFailures[eventKey] = failures;
        }
        failures.Enqueue(now);
        while (failures.TryPeek(out DateTimeOffset first) && now - first > TimeSpan.FromMinutes(1)) failures.Dequeue();
        if (failures.Count >= 3)
            _services.OperationalAlerts?.Raise(new OperationalAlert(key, OperationalAlertSeverity.Error,
                "同一イベントの解析失敗", $"イベント {eventKey} の解析が1分間に{failures.Count}回失敗しました。", now));
    }

    private void OnOperationalAlertRaised(OperationalAlert alert) => _dispatcher.Invoke(() =>
    {
        if (string.Equals(alert.Key, "provider-stale", StringComparison.Ordinal)) return;
        OperationalAlerts.Insert(0, alert);
        while (OperationalAlerts.Count > 250) OperationalAlerts.RemoveAt(OperationalAlerts.Count - 1);
        OperatorNotificationRequested?.Invoke(alert);
    });

    private void OnSourceComparisonUpdated(SourceComparisonResult result) => _dispatcher.Invoke(() =>
    {
        SourceComparisonResult? former = SourceComparisons.FirstOrDefault(item => item.CorrelationKey == result.CorrelationKey);
        if (former is not null) SourceComparisons.Remove(former);
        SourceComparisons.Insert(0, result);
        while (SourceComparisons.Count > 1000) SourceComparisons.RemoveAt(SourceComparisons.Count - 1);
    });

    private void OnObsDeliveryReported(ObsDeliveryDiagnostic delivery)
    {
        if (delivery.Stage == ObsDeliveryStage.AudioFailed)
            _services.OperationalAlerts?.Raise(new OperationalAlert("audio-failed", OperationalAlertSeverity.Error,
                "音声再生失敗", $"OBS音声再生に失敗しました: {delivery.ProgramId}", delivery.ReportedAtUtc));
        else if (delivery.Stage == ObsDeliveryStage.AudioCompleted)
            _services.OperationalAlerts?.Recover("audio-failed", "音声再生復旧", "OBS音声再生が完了しました。", delivery.ReportedAtUtc);
    }

    private void ObserveOperationalConnection(ProviderConnectionSnapshot snapshot)
    {
        if (_services.OperationalAlerts is null) return;
        DateTimeOffset now = _services.Clock.UtcNow;
        if (snapshot.State is ProviderConnectionState.Faulted)
            _services.OperationalAlerts.Raise(new OperationalAlert("provider-fault", OperationalAlertSeverity.Error, "受信接続異常", snapshot.Detail ?? string.Empty, now));
        else if (snapshot.State is ProviderConnectionState.Connected or ProviderConnectionState.Stale)
        {
            _services.OperationalAlerts.Recover("provider-fault", "受信接続復旧", "受信接続が復旧しました。", now);
        }
    }

    private async Task MonitorOperationsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_services.OperationalAlerts is null) continue;
            DateTimeOffset now = _services.Clock.UtcNow;
            if (_obsServer?.IsRunning == false)
                _services.OperationalAlerts.Raise(new OperationalAlert("local-view-stopped", OperationalAlertSeverity.Error, "OBS Local View停止", "ローカル配信サーバーが停止しています。", now));
            else _services.OperationalAlerts.Recover("local-view-stopped", "OBS Local View復旧", "ローカル配信サーバーが復旧しました。", now);
            try
            {
                long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_services.DataDirectory))!).AvailableFreeSpace;
                if (free < 1024L * 1024 * 1024)
                    _services.OperationalAlerts.Raise(new OperationalAlert("disk-space", free < 256L * 1024 * 1024 ? OperationalAlertSeverity.Error : OperationalAlertSeverity.Warning, "空き容量不足", $"保存先の空き容量は {free / 1024d / 1024d:N0} MB です。", now));
                else _services.OperationalAlerts.Recover("disk-space", "空き容量回復", "保存先の空き容量が回復しました。", now);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { _services.OperationalAlerts.Raise(new OperationalAlert("disk-check", OperationalAlertSeverity.Warning, "保存先確認失敗", exception.Message, now)); }
            _ = _services.SourceComparison?.GetSnapshot(now);
        }
    }

    private static string DescribeSettingsDifferences(AppSettings current, AppSettings next)
    {
        System.Text.Json.JsonElement left = System.Text.Json.JsonSerializer.SerializeToElement(current);
        System.Text.Json.JsonElement right = System.Text.Json.JsonSerializer.SerializeToElement(next);
        var paths = new List<string>();
        CollectDifferences(left, right, string.Empty, paths);
        if (paths.Count == 0) return "差分はありません。";
        IEnumerable<string> displayed = paths.Take(60).Select(static item => "・" + item);
        string result = string.Join(Environment.NewLine, displayed);
        if (paths.Count > 60) result += Environment.NewLine + $"・ほか {paths.Count - 60} 項目";
        return result;

        static void CollectDifferences(System.Text.Json.JsonElement leftValue,
            System.Text.Json.JsonElement rightValue, string path, List<string> output)
        {
            if (leftValue.ValueKind != rightValue.ValueKind)
            {
                output.Add(string.IsNullOrEmpty(path) ? "設定全体" : path);
                return;
            }
            if (leftValue.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var names = leftValue.EnumerateObject().Select(static item => item.Name)
                    .Concat(rightValue.EnumerateObject().Select(static item => item.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (string name in names)
                {
                    bool hasLeft = leftValue.TryGetProperty(name, out System.Text.Json.JsonElement childLeft);
                    bool hasRight = rightValue.TryGetProperty(name, out System.Text.Json.JsonElement childRight);
                    string childPath = string.IsNullOrEmpty(path) ? name : path + "." + name;
                    if (!hasLeft || !hasRight) output.Add(childPath);
                    else CollectDifferences(childLeft, childRight, childPath, output);
                }
                return;
            }
            if (leftValue.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                if (leftValue.GetRawText() != rightValue.GetRawText()) output.Add(path);
                return;
            }
            if (leftValue.GetRawText() != rightValue.GetRawText()) output.Add(path);
        }
    }
}

public sealed class OperationalTestCaseViewModel : ObservableObject
{
    private string _resultText = "未実行";
    private TestCaseManifest _manifest;
    private string _name;
    private string _category;
    private string _provider;
    private string _telegramType;
    private string _eventId;
    private string _tagsText;
    private string _description;
    private string _expectedKind;
    private string _expectedStatus;
    private string _expectedPageCount;
    private string _expectedBadges;
    private string _expectedText;
    private string _expectedAreas;
    private string _expectedAudioCue;
    private string _expectedSuppressionReason;
    private bool? _expectedCancelled;
    private bool? _expectedUpdate;
    private bool? _expectedReleased;

    public OperationalTestCaseViewModel(TestCaseManifest manifest)
    {
        _manifest = manifest;
        _name = manifest.Name;
        _category = manifest.Category;
        _provider = manifest.Provider;
        _telegramType = manifest.TelegramType;
        _eventId = manifest.EventId;
        _tagsText = string.Join(", ", manifest.Tags);
        _description = manifest.Description;
        _expectedKind = manifest.Expectation.EventKind;
        _expectedStatus = manifest.Expectation.Status;
        _expectedPageCount = manifest.Expectation.PageCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _expectedBadges = string.Join(" | ", manifest.Expectation.RequiredBadges);
        _expectedText = string.Join(" | ", manifest.Expectation.RequiredTextFragments);
        _expectedAreas = string.Join(" | ", manifest.Expectation.RequiredAreas);
        _expectedAudioCue = manifest.Expectation.AudioCue;
        _expectedSuppressionReason = manifest.Expectation.SuppressionReason;
        _expectedCancelled = manifest.Expectation.IsCancelled;
        _expectedUpdate = manifest.Expectation.IsUpdate;
        _expectedReleased = manifest.Expectation.IsReleased;
    }

    public TestCaseManifest Manifest => _manifest;
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(DisplayText)); } }
    public string Category { get => _category; set { if (SetProperty(ref _category, value)) OnPropertyChanged(nameof(DisplayText)); } }
    public string Provider { get => _provider; set => SetProperty(ref _provider, value); }
    public string TelegramType { get => _telegramType; set { if (SetProperty(ref _telegramType, value)) OnPropertyChanged(nameof(DisplayText)); } }
    public string EventId { get => _eventId; set => SetProperty(ref _eventId, value); }
    public string TagsText { get => _tagsText; set => SetProperty(ref _tagsText, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string ExpectedKind { get => _expectedKind; set => SetProperty(ref _expectedKind, value); }
    public string ExpectedStatus { get => _expectedStatus; set => SetProperty(ref _expectedStatus, value); }
    public string ExpectedPageCount { get => _expectedPageCount; set => SetProperty(ref _expectedPageCount, value); }
    public string ExpectedBadges { get => _expectedBadges; set => SetProperty(ref _expectedBadges, value); }
    public string ExpectedText { get => _expectedText; set => SetProperty(ref _expectedText, value); }
    public string ExpectedAreas { get => _expectedAreas; set => SetProperty(ref _expectedAreas, value); }
    public string ExpectedAudioCue { get => _expectedAudioCue; set => SetProperty(ref _expectedAudioCue, value); }
    public string ExpectedSuppressionReason { get => _expectedSuppressionReason; set => SetProperty(ref _expectedSuppressionReason, value); }
    public bool? ExpectedCancelled { get => _expectedCancelled; set => SetProperty(ref _expectedCancelled, value); }
    public bool? ExpectedUpdate { get => _expectedUpdate; set => SetProperty(ref _expectedUpdate, value); }
    public bool? ExpectedReleased { get => _expectedReleased; set => SetProperty(ref _expectedReleased, value); }
    public string DisplayText => $"{Name}  [{Category}]  {TelegramType}";
    public string ResultText { get => _resultText; set => SetProperty(ref _resultText, value); }

    public TestCaseManifest ToManifest() => _manifest with
    {
        Name = Name,
        Category = Category,
        Provider = Provider,
        TelegramType = TelegramType,
        EventId = EventId,
        Tags = Split(TagsText, ','),
        Description = Description,
        Expectation = new TestCaseExpectation(ExpectedKind, ExpectedStatus,
            int.TryParse(ExpectedPageCount, out int pageCount) ? pageCount : null,
            Split(ExpectedBadges, '|'), Split(ExpectedText, '|'), Split(ExpectedAreas, '|'),
            ExpectedAudioCue, ExpectedSuppressionReason)
        {
            IsCancelled = ExpectedCancelled,
            IsUpdate = ExpectedUpdate,
            IsReleased = ExpectedReleased,
        },
    };

    public void ReplaceManifest(TestCaseManifest manifest)
    {
        _manifest = manifest;
        Name = manifest.Name;
        Category = manifest.Category;
        Provider = manifest.Provider;
        TelegramType = manifest.TelegramType;
        EventId = manifest.EventId;
        TagsText = string.Join(", ", manifest.Tags);
        Description = manifest.Description;
        ExpectedKind = manifest.Expectation.EventKind;
        ExpectedStatus = manifest.Expectation.Status;
        ExpectedPageCount = manifest.Expectation.PageCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ExpectedBadges = string.Join(" | ", manifest.Expectation.RequiredBadges);
        ExpectedText = string.Join(" | ", manifest.Expectation.RequiredTextFragments);
        ExpectedAreas = string.Join(" | ", manifest.Expectation.RequiredAreas);
        ExpectedAudioCue = manifest.Expectation.AudioCue;
        ExpectedSuppressionReason = manifest.Expectation.SuppressionReason;
        ExpectedCancelled = manifest.Expectation.IsCancelled;
        ExpectedUpdate = manifest.Expectation.IsUpdate;
        ExpectedReleased = manifest.Expectation.IsReleased;
        OnPropertyChanged(nameof(Manifest));
        OnPropertyChanged(nameof(DisplayText));
    }

    private static string[] Split(string value, char separator) => value
        .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
