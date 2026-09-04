using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Audio;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Diagnostics;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Operations;
using EEWTelop.Application.Persistence;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
#if QTELOPPER_AXIS_PROVIDER
using EEWTelop.Infrastructure.Axis.Configuration;
using EEWTelop.Infrastructure.Axis.Security;
#endif
#if QTELOPPER_DMDATA_PROVIDER
using EEWTelop.Infrastructure.Dmdata.Configuration;
#endif
using EEWTelop.Infrastructure.P2P.Configuration;
using EEWTelop.Infrastructure.Wolfx.Configuration;
using EEWTelop.Wpf.Bootstrap;
using EEWTelop.Wpf.Mvvm;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.Services;

namespace EEWTelop.Wpf.ViewModels;

internal enum TelegramReplayMode
{
    ProductionReplay,
    PastInformation,
    Training,
}

public sealed partial class ControlWindowViewModel : ObservableObject, IAsyncDisposable
{
    public bool IsExtendedFeaturesEnabled =>
        _services is not null && BuildFeatures.ExtendedFeaturesEnabled;

    private static readonly TimeSpan RuntimeGapNoticeThreshold = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ObsRuntimeRecoveryThreshold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AxisTokenRefreshCheckInterval = TimeSpan.FromDays(1);
    private readonly string _obsBrowserSourceDescription =
        "地震字幕・全ての音声・EEW・津波字幕・気象情報の4ソースを登録します。音声ミキサーには「地震字幕・全ての音声」だけを追加します。OBS側でWebSocketサーバーを有効にしてください。";
    private readonly AppServices _services;
    private readonly IConfirmationService _confirmationService;
    private readonly IUiDispatcher _dispatcher;
    private PriorityCoordinator _previewCoordinator;
    private readonly ObsSnapshotStore _obsSnapshotStore;
    private readonly IObsLocalViewServer? _obsServer;
    private readonly IObsBrowserSourceSynchronizer? _obsBrowserSourceSynchronizer;
    private readonly CancellationTokenSource _renderLoopStop = new();
    private readonly CancellationTokenSource _axisTokenRefreshStop = new();
    private readonly SemaphoreSlim _axisTokenRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _obsLifecycle = new(1, 1);
    private readonly object _stateQueueGate = new();
    private readonly object _weatherAudioGate = new();
    private readonly EewAudioPriorityGate _eewAudioPriority = new();
    private readonly ProductionReplayCatalog _productionReplayCatalog = new();
    private readonly bool _isE2ETestMode;
    private readonly string _applicationVersionText = BuildApplicationVersionText();
    private IDisplayCoordinator _activeCoordinator;
    private AppSettings _settings;
    private TestScenario? _selectedScenario;
    private ReceivedTelegramViewModel? _selectedReceivedTelegram;
    private TelegramReplayMode _telegramReplayMode = TelegramReplayMode.Training;
    private bool _isTelegramHistoryLoading;
    private string _telegramReviewStatusText = "本番受信待ち。過去電文は［過去電文を取得］から読み込めます。";
    private AppLogLevel _minimumLogLevel;
    private ProviderConnectionState _connectionState;
    private string _lastReceivedText = "—";
    private string _retryDelayText = "—";
    private int _reconnectCount;
    private int _obsClientCount;
    private string _obsStatusText = "停止中";
    private string _obsBrowserSyncStatusText = "無効";
    private string _receptionStatusText = "表示対象の受信待ち";
    private long _lastRuntimeTimestamp;
    private Task? _receptionTask;
    private Task _renderLoopTask = Task.CompletedTask;
    private Task _axisTokenRefreshTask = Task.CompletedTask;
    private Task _obsConfigurationTask = Task.CompletedTask;
    private Task _stateSaveTask = Task.CompletedTask;
    private Task _testScenarioTask = Task.CompletedTask;
    private Task _historyReplayTask = Task.CompletedTask;
    private Task _weatherAudioTask = Task.CompletedTask;
    private DisplayStateDocument? _pendingState;
    private bool _stateSaveRunning;
    private string _lastStateSignature = string.Empty;
    private ProviderConnectionSnapshot _lastConnectionSnapshot;
    private CancellationTokenSource? _historyReplayCancellation;
    private CancellationTokenSource? _testScenarioCancellation;
    private CancellationTokenSource? _weatherAudioCancellation;
    private PendingAudioPlayback? _pendingWeatherAudio;
    private IDisplayCoordinator? _historyCoordinator;
    private PriorityCoordinator? _productionReplayCoordinator;
    private DateTimeOffset _productionReplayResumeAfterUtc = DateTimeOffset.MaxValue;
    private DateTimeOffset _productionReplayNextSwitchUtc = DateTimeOffset.MaxValue;
    private bool _isHistoryRehearsalRunning;
    private int _e2eAcceptedTelopRevision;
    private HistoryReplayItemViewModel? _selectedHistoryItem;
    private string _historyRehearsalStatusText;
    private string _historyCancellationStatus = "停止しました";
    private CoordinatorSnapshot? _lastCoordinatorSnapshot;
    private readonly Dictionary<DisplayProgram, DisplayProgram> _manualSubtitleEdits =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<PreDisplaySubtitleDraft> _preDisplaySubtitleDrafts = [];
    private bool _preDisplayEditingEnabled;
    private bool _disposed;

    public ControlWindowViewModel(
        AppServices services,
        AppSettings settings,
        IConfirmationService confirmationService,
        IUiDispatcher dispatcher,
        ObsSnapshotStore? obsSnapshotStore = null,
        IObsLocalViewServer? obsServer = null,
        IObsBrowserSourceSynchronizer? obsBrowserSourceSynchronizer = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(confirmationService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (settings.Provider.ReceptionProvider !=
            settings.Provider.Routing.GetCompatibilityProvider())
        {
            ProviderRoutingSettings migratedRouting = ProviderRoutingSettings.FromLegacy(
                settings.Provider.ReceptionProvider);
            settings = settings with
            {
                Provider = settings.Provider with { Routing = migratedRouting },
            };
        }
        _services = services;
        _isE2ETestMode = string.Equals(
            Environment.GetEnvironmentVariable("QT_E2E"),
            "1",
            StringComparison.Ordinal);
        _settings = settings;
        _confirmationService = confirmationService;
        _dispatcher = dispatcher;
        _previewCoordinator = new PriorityCoordinator(services.Clock, settings.Display);
        _activeCoordinator = services.DisplayCoordinator;
        _obsSnapshotStore = obsSnapshotStore ?? new ObsSnapshotStore(settings.Display, services.Clock.UtcNow);
        _obsServer = obsServer;
        _obsBrowserSourceSynchronizer = obsBrowserSourceSynchronizer;
        _obsBrowserSyncStatusText = obsBrowserSourceSynchronizer?.Status ?? "利用不可";
        _lastRuntimeTimestamp = services.Clock.GetTimestamp();
        _historyRehearsalStatusText = services.HistoryRehearsalLoader is null
            ? "履歴取得機能を利用できません"
            : "停止中";
        Settings = new SettingsEditorViewModel(settings);
        Overlay = new OverlayViewModel();
        Overlay.ApplySettings(settings.Display);
        _obsSnapshotStore.PublishSettings(settings.Display, services.Clock.UtcNow);
        Scenarios = TestScenarioCatalog.Create(services.Clock.UtcNow);
        _selectedScenario = Scenarios[0];
        _connectionState = services.EventSource.Connection.State;
        _lastConnectionSnapshot = services.EventSource.Connection;
        services.IngestionPipeline.HoldBeforeDisplay = false;

        ConnectCommand = new RelayCommand(StartConnection, () => !IsConnectedOrConnecting);
        DisconnectCommand = new RelayCommand(
            RequestManualDisconnect,
            () => IsConnectedOrConnecting);
        SaveSettingsCommand = new RelayCommand(() => _ = SaveSettingsFromCommandAsync());
        ResetAudioSettingsCommand = new RelayCommand(Settings.ResetAudioSettings);
        ResetReceptionSettingsCommand = new RelayCommand(Settings.ResetReceptionSettings);
        ResetFilterSettingsCommand = new RelayCommand(Settings.ResetFilterSettings);
        ResetCompatibilityAndSafetySettingsCommand = new RelayCommand(
            Settings.ResetCompatibilityAndSafetySettings);
        ResetDisplaySettingsCommand = new RelayCommand(Settings.ResetDisplaySettings);
        ResetProductionReplaySettingsCommand = new RelayCommand(
            Settings.ResetProductionReplaySettings);
        ResetCanvasSettingsCommand = new RelayCommand(Settings.ResetCanvasSettings);
        ResetObsLocalViewSettingsCommand = new RelayCommand(Settings.ResetObsLocalViewSettings);
        ResetObsWebSocketSettingsCommand = new RelayCommand(Settings.ResetObsWebSocketSettings);
        ResetHistorySettingsCommand = new RelayCommand(Settings.ResetHistorySettings);
        ResetLogSettingsCommand = new RelayCommand(() =>
        {
            Settings.ResetLogSettings();
            MinimumLogLevel = AppLogLevel.Debug;
        });
        RunTestCommand = new RelayCommand(RunSelectedTest, () => SelectedScenario is not null);
        StartHistoryRehearsalCommand = new RelayCommand(
            StartHistoryRehearsal,
            () => _services.HistoryRehearsalLoader is not null && !IsHistoryRehearsalRunning);
        StopHistoryRehearsalCommand = new RelayCommand(
            () => CancelHistoryRehearsal("停止処理中…"),
            () => IsHistoryRehearsalRunning);
        PlaySelectedHistoryCommand = new RelayCommand(
            StartSelectedHistoryRehearsal,
            () => SelectedHistoryItem is not null && !IsHistoryRehearsalRunning);
        ShowPreviewCommand = new RelayCommand(() => ShowPreviewRequested?.Invoke(this, EventArgs.Empty));
        EditSubtitleCommand = new RelayCommand(
            RequestSubtitleEdit,
            () => _lastCoordinatorSnapshot?.CurrentProgram is not null);
        EditPendingSubtitleCommand = new RelayCommand(
            static () => { },
            static () => false);
        EditPreDisplaySubtitleCommand = new RelayCommand(
            static () => { },
            static () => false);
        EditSubtitlePhraseTemplatesCommand = new RelayCommand(() =>
            EditSubtitlePhraseTemplatesRequested?.Invoke());
        ClearDisplayCommand = new RelayCommand(ClearDisplay);
        ClearQuakeDisplayCommand = new RelayCommand(() => ClearDisplay(EventKind.Quake));
        ClearTsunamiDisplayCommand = new RelayCommand(() => ClearDisplay(EventKind.Tsunami));
        ClearEewDisplayCommand = new RelayCommand(() => ClearDisplay(EventKind.Eew));
        ClearWeatherDisplayCommand = new RelayCommand(() => ClearDisplay(EventKind.WeatherWarning));
        RedisplayReceivedTelegramCommand = new RelayCommand(
            RedisplaySelectedTelegram,
            () => SelectedReceivedTelegram is not null);
        ShowTelegramReviewCommand = new RelayCommand(() => ShowTelegramReviewRequested?.Invoke());
        LoadHistoryForReviewCommand = new RelayCommand(
            () => _ = LoadHistoryForReviewAsync(),
            () => _services.HistoryRehearsalLoader is not null &&
                !IsTelegramHistoryLoading &&
                !IsHistoryRehearsalRunning);
        ClearLogsCommand = new RelayCommand(() =>
        {
            Logs.Clear();
            VisibleLogs.Clear();
        });
        CopyLogsCommand = new RelayCommand(() => CopyTextRequested?.Invoke(
            this,
            string.Join(Environment.NewLine, VisibleLogs.Select(static entry => entry.DisplayText))));
        CopyObsUrlCommand = new RelayCommand(
            () => CopyTextRequested?.Invoke(this, ObsUrlText),
            () => !string.IsNullOrWhiteSpace(ObsUrlText) && _obsServer?.IsRunning == true);
        CopyObsOutputUrlCommand = new RelayCommand(parameter =>
        {
            if (parameter is string url && !string.IsNullOrWhiteSpace(url))
            {
                CopyTextRequested?.Invoke(this, url);
            }
        });
        SyncObsBrowserSourcesCommand = new RelayCommand(
            RequestObsBrowserSourceSynchronization,
            () => _obsBrowserSourceSynchronizer is not null && _obsServer?.IsRunning == true);
        ExportDiagnosticsCommand = new RelayCommand(() =>
            ExportDiagnosticsRequested?.Invoke(this, EventArgs.Empty));
        BrowseAudioFileCommand = new RelayCommand(parameter =>
        {
            if (parameter is AudioCueId cue)
            {
                BrowseAudioFileRequested?.Invoke(cue);
            }
        });
        BrowseHistoryXmlFileCommand = new RelayCommand(() => BrowseHistoryXmlFileRequested?.Invoke());
        TestAudioCommand = new RelayCommand(parameter =>
        {
            if (parameter is AudioCueId cue)
            {
                _ = TestAudioAsync(cue);
            }
        }, _ => !IsProductionConnectionActive);
        StopAudioCommand = new RelayCommand(() => _ = StopAudioAsync());

        foreach (AppLogEntry entry in services.UiLogs.GetSnapshot())
        {
            AddLogToCollections(entry);
        }

        services.UiLogs.EntryAdded += OnLogEntryAdded;
        services.EventSource.ConnectionChanged += OnConnectionChanged;
        services.ReceptionService.EventProcessed += OnEventProcessed;
        if (_obsServer is not null)
        {
            _obsServer.ClientCountChanged += OnObsClientCountChanged;
        }
        if (_obsBrowserSourceSynchronizer is not null)
        {
            _obsBrowserSourceSynchronizer.StatusChanged += OnObsBrowserSyncStatusChanged;
        }

        InitializeOperationalFeatures();

        UpdateConnection(services.EventSource.Connection);
        ApplyDisplaySnapshot(services.DisplayCoordinator.Evaluate(resynchronizeFromUtc: true));
        _renderLoopTask = RunRenderLoopAsync(_renderLoopStop.Token);
        _axisTokenRefreshTask = RunAxisTokenRefreshLoopAsync(_axisTokenRefreshStop.Token);
        _obsConfigurationTask = ConfigureObsServerAsync(settings.Obs);
        FireAndForgetLog(AppLogLevel.Information, "ApplicationReady", "永続化・ファイルログ・診断ZIP・任意音声ファイル再生を初期化しました。接続は手動開始です。");
    }

    public event EventHandler? ShowPreviewRequested;

    public event Action? ShowTelegramReviewRequested;

    public event Action<DisplayProgram, DisplayProgram>? EditSubtitleRequested;

    public event Action<IReadOnlyList<DisplayProgram>>? EditPendingSubtitleRequested;

    public event Action<IReadOnlyList<DisplayProgram>>? EditPreDisplaySubtitleRequested;

    public event Action? EditSubtitlePhraseTemplatesRequested;

    public event EventHandler<string>? CopyTextRequested;

    public event EventHandler? ExportDiagnosticsRequested;

    public event Action<AudioCueId>? BrowseAudioFileRequested;

    public event Action? BrowseHistoryXmlFileRequested;

    public SettingsEditorViewModel Settings { get; private set; }

    public OverlayViewModel Overlay { get; }

    public bool IsE2ETestMode => _isE2ETestMode;

    public int E2EAcceptedTelopRevision
    {
        get => _e2eAcceptedTelopRevision;
        private set => SetProperty(ref _e2eAcceptedTelopRevision, value);
    }

    public IReadOnlyList<TestScenario> Scenarios { get; }

    public IReadOnlyList<AppLogLevel> LogLevels { get; } = Enum.GetValues<AppLogLevel>();

    public ObservableCollection<UiLogEntryViewModel> Logs { get; } = [];

    public ObservableCollection<UiLogEntryViewModel> VisibleLogs { get; } = [];

    public ObservableCollection<HistoryReplayItemViewModel> HistoryItems { get; } = [];

    public ObservableCollection<ReceivedTelegramViewModel> ReceivedTelegrams { get; } = [];

    public ReceivedTelegramViewModel? SelectedReceivedTelegram
    {
        get => _selectedReceivedTelegram;
        set
        {
            if (SetProperty(ref _selectedReceivedTelegram, value))
            {
                OnPropertyChanged(nameof(CanUseProductionReplayMode));
                SetTelegramReplayMode(value?.Event.SourceMode switch
                {
                    SourceMode.Production => TelegramReplayMode.ProductionReplay,
                    SourceMode.HistoryRehearsal => TelegramReplayMode.PastInformation,
                    _ => TelegramReplayMode.Training,
                });
                RedisplayReceivedTelegramCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanUseProductionReplayMode =>
        SelectedReceivedTelegram?.Event.SourceMode == SourceMode.Production;

    public bool IsProductionReplayMode
    {
        get => _telegramReplayMode == TelegramReplayMode.ProductionReplay;
        set
        {
            if (value && CanUseProductionReplayMode)
            {
                SetTelegramReplayMode(TelegramReplayMode.ProductionReplay);
            }
        }
    }

    public bool IsPastInformationReplayMode
    {
        get => _telegramReplayMode == TelegramReplayMode.PastInformation;
        set
        {
            if (value)
            {
                SetTelegramReplayMode(TelegramReplayMode.PastInformation);
            }
        }
    }

    public bool IsTrainingReplayMode
    {
        get => _telegramReplayMode == TelegramReplayMode.Training;
        set
        {
            if (value)
            {
                SetTelegramReplayMode(TelegramReplayMode.Training);
            }
        }
    }

    public RelayCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand SaveSettingsCommand { get; }

    public RelayCommand ResetAudioSettingsCommand { get; }

    public RelayCommand ResetReceptionSettingsCommand { get; }

    public RelayCommand ResetFilterSettingsCommand { get; }

    public RelayCommand ResetCompatibilityAndSafetySettingsCommand { get; }

    public RelayCommand ResetDisplaySettingsCommand { get; }

    public RelayCommand ResetProductionReplaySettingsCommand { get; }

    public RelayCommand ResetCanvasSettingsCommand { get; }

    public RelayCommand ResetObsLocalViewSettingsCommand { get; }

    public RelayCommand ResetObsWebSocketSettingsCommand { get; }

    public RelayCommand ResetHistorySettingsCommand { get; }

    public RelayCommand ResetLogSettingsCommand { get; }

    public RelayCommand RunTestCommand { get; }

    public RelayCommand StartHistoryRehearsalCommand { get; }

    public RelayCommand StopHistoryRehearsalCommand { get; }

    public RelayCommand PlaySelectedHistoryCommand { get; }

    public RelayCommand ShowPreviewCommand { get; }

    public RelayCommand EditSubtitleCommand { get; }

    public RelayCommand EditPendingSubtitleCommand { get; }

    public RelayCommand EditPreDisplaySubtitleCommand { get; }

    public RelayCommand EditSubtitlePhraseTemplatesCommand { get; }

    public RelayCommand ClearDisplayCommand { get; }

    public RelayCommand ClearQuakeDisplayCommand { get; }

    public RelayCommand ClearTsunamiDisplayCommand { get; }

    public RelayCommand ClearEewDisplayCommand { get; }

    public RelayCommand ClearWeatherDisplayCommand { get; }

    public RelayCommand RedisplayReceivedTelegramCommand { get; }

    public RelayCommand ShowTelegramReviewCommand { get; }

    public RelayCommand LoadHistoryForReviewCommand { get; }

    public RelayCommand ClearLogsCommand { get; }

    public RelayCommand BrowseAudioFileCommand { get; }

    public RelayCommand BrowseHistoryXmlFileCommand { get; }

    public RelayCommand TestAudioCommand { get; }

    public RelayCommand StopAudioCommand { get; }

    public RelayCommand CopyLogsCommand { get; }

    public RelayCommand CopyObsUrlCommand { get; }

    public RelayCommand CopyObsOutputUrlCommand { get; }

    public RelayCommand SyncObsBrowserSourcesCommand { get; }

    public RelayCommand ExportDiagnosticsCommand { get; }

    public TestScenario? SelectedScenario
    {
        get => _selectedScenario;
        set
        {
            if (SetProperty(ref _selectedScenario, value))
            {
                RunTestCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AppLogLevel MinimumLogLevel
    {
        get => _minimumLogLevel;
        set
        {
            if (SetProperty(ref _minimumLogLevel, value))
            {
                RebuildVisibleLogs();
            }
        }
    }

    public ProviderConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (SetProperty(ref _connectionState, value))
            {
                OnPropertyChanged(nameof(ConnectionStateText));
                OnPropertyChanged(nameof(IsConnectedOrConnecting));
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                TestAudioCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ConnectionStateText => ConnectionState switch
    {
        ProviderConnectionState.Stopped => "切断中（手動）",
        ProviderConnectionState.Connecting => "接続中…",
        ProviderConnectionState.Connected => "接続済み",
        ProviderConnectionState.Stale => "接続済み",
        ProviderConnectionState.Reconnecting => "再接続待機中",
        ProviderConnectionState.Faulted => "要確認",
        _ => ConnectionState.ToString(),
    };

    public bool IsConnectedOrConnecting => ConnectionState is not ProviderConnectionState.Stopped and
        not ProviderConnectionState.Faulted;

    public string LastReceivedText { get => _lastReceivedText; private set => SetProperty(ref _lastReceivedText, value); }

    public string RetryDelayText { get => _retryDelayText; private set => SetProperty(ref _retryDelayText, value); }

    public int ReconnectCount { get => _reconnectCount; private set => SetProperty(ref _reconnectCount, value); }

    public int ObsClientCount { get => _obsClientCount; private set => SetProperty(ref _obsClientCount, value); }

    public string ObsStatusText { get => _obsStatusText; private set => SetProperty(ref _obsStatusText, value); }

    public string ObsBrowserSyncStatusText
    {
        get => _obsBrowserSyncStatusText;
        private set => SetProperty(ref _obsBrowserSyncStatusText, value);
    }

    public string ReceptionStatusText { get => _receptionStatusText; private set => SetProperty(ref _receptionStatusText, value); }

    public bool PreDisplayEditingEnabled
    {
        get => false;
        set
        {
            if (_preDisplayEditingEnabled)
            {
                _preDisplayEditingEnabled = false;
                OnPropertyChanged();
            }

            _services.IngestionPipeline.HoldBeforeDisplay = false;
        }
    }

    public string PreDisplayDraftText =>
        $"作画前の字幕を編集・送出（{_preDisplaySubtitleDrafts.Count}件）";

    public string ApiModeText =>
        $"EEW {ProviderLabel(Settings.EewProvider)} / " +
        $"地震 {ProviderLabel(Settings.QuakeProvider)} / " +
        $"津波 {ProviderLabel(Settings.TsunamiProvider)} / " +
        $"気象 {ProviderLabel(Settings.WeatherProvider)} / " +
        $"火山 {ProviderLabel(Settings.VolcanoProvider)} / " +
        $"南海トラフ {ProviderLabel(Settings.NankaiTroughProvider)}";

    public string ApplicationVersionText => _applicationVersionText;

    public string ObsBrowserSourceDescription => _obsBrowserSourceDescription;

    public string ObsUrlText => _obsServer?.OverlayUrl ?? string.Empty;

    public string EewObsUrlText => _obsServer?.EewUrl ?? string.Empty;

    public string TsunamiObsUrlText => _obsServer?.TsunamiUrl ?? string.Empty;

    public string WeatherObsUrlText => _obsServer?.WeatherUrl ?? string.Empty;

    public bool IsHistoryRehearsalRunning
    {
        get => _isHistoryRehearsalRunning;
        private set
        {
            if (SetProperty(ref _isHistoryRehearsalRunning, value))
            {
                StartHistoryRehearsalCommand.RaiseCanExecuteChanged();
                StopHistoryRehearsalCommand.RaiseCanExecuteChanged();
                PlaySelectedHistoryCommand.RaiseCanExecuteChanged();
                LoadHistoryForReviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsTelegramHistoryLoading
    {
        get => _isTelegramHistoryLoading;
        private set
        {
            if (SetProperty(ref _isTelegramHistoryLoading, value))
            {
                LoadHistoryForReviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TelegramReviewStatusText
    {
        get => _telegramReviewStatusText;
        private set => SetProperty(ref _telegramReviewStatusText, value);
    }

    public HistoryReplayItemViewModel? SelectedHistoryItem
    {
        get => _selectedHistoryItem;
        set
        {
            if (SetProperty(ref _selectedHistoryItem, value))
            {
                PlaySelectedHistoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string HistoryRehearsalStatusText
    {
        get => _historyRehearsalStatusText;
        private set => SetProperty(ref _historyRehearsalStatusText, value);
    }

    public async Task SaveSettingsAsync()
    {
        AppSettings updated = Settings.ToSettings(_settings);
        if (updated.Provider.Routing.Uses(ReceptionProvider.Axis))
        {
#if QTELOPPER_AXIS_PROVIDER
            IReadOnlyList<string> axisErrors = AxisProviderOptions
                .FromSettings(updated.Provider)
                .Validate();
            if (axisErrors.Count > 0)
            {
                ReceptionStatusText = "AXIS設定エラー（トークンと接続先を確認）";
                await WriteLogAsync(
                    AppLogLevel.Error,
                    "AxisConfigurationRejected",
                    string.Join(" ", axisErrors)).ConfigureAwait(false);
                return;
            }
#else
            ReceptionStatusText = "この版ではAXISを利用できません";
            return;
#endif
        }

        bool providerSettingsChanged = updated.Provider != _settings.Provider;
        bool receptionConfigurationChanged = HasReceptionConfigurationChanged(
            _settings.Provider,
            updated.Provider);
        bool obsChanged = updated.Obs != _settings.Obs;
        // A routed source can report Faulted when only one provider branch has
        // failed while the other branches and the aggregate reader are still
        // running. The UI state alone therefore cannot determine whether it is
        // safe to reconfigure providers.
        bool restartReception = receptionConfigurationChanged && IsReceptionRunning;
        if (restartReception)
        {
            await StopConnectionAsync().ConfigureAwait(false);
        }

        if (receptionConfigurationChanged &&
            _services.EventSource is IProviderConfigurableEventSource configurable)
        {
            configurable.ConfigureProvider(updated.Provider);
        }
        if (providerSettingsChanged &&
            _services.EventNormalizer is IProviderSelectionConfigurable selectionConfigurable)
        {
            selectionConfigurable.UpdateProviderSelection(updated.Provider);
        }

        _settings = updated;
        _productionReplayCatalog.Prune(
            updated.Display.ProductionReplay,
            _services.Clock.UtcNow);
        _productionReplayNextSwitchUtc = _services.Clock.UtcNow;
        _services.RawMessageArchive?.Configure(updated.Log);
        _services.IngestionPipeline.UpdateSettings(updated.Display, updated.Filter);
        _previewCoordinator.UpdateSettings(updated.Display);
        Overlay.ApplySettings(updated.Display);
        _obsSnapshotStore.PublishSettings(updated.Display, _services.Clock.UtcNow);
        try
        {
            await _services.SettingsStore.SaveAsync(updated).ConfigureAwait(false);
            _services.OperationalAlerts?.Recover("settings-write", "設定保存復旧",
                "設定ファイルを保存できる状態へ復旧しました。", _services.Clock.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _services.OperationalAlerts?.Raise(new OperationalAlert("settings-write", OperationalAlertSeverity.Error,
                "設定保存失敗", exception.Message, _services.Clock.UtcNow));
            throw;
        }
        if (obsChanged)
        {
            await ConfigureObsServerAsync(updated.Obs).ConfigureAwait(false);
        }

        await WriteLogAsync(
            AppLogLevel.Information,
            "SettingsSaved",
            receptionConfigurationChanged
                ? "設定を保存し、受信先を次の接続へ反映しました。"
                : "表示・出力・音・履歴・安全設定を保存し、表示設定を即時反映しました。").ConfigureAwait(false);
        _dispatcher.Invoke(() => OnPropertyChanged(nameof(ApiModeText)));
        if (restartReception)
        {
            _dispatcher.Invoke(StartConnection);
        }
    }

    private static bool HasReceptionConfigurationChanged(
        ProviderSettings current,
        ProviderSettings updated)
    {
        if (current.Routing != updated.Routing)
        {
            return true;
        }

        IReadOnlyList<ReceptionProvider> activeProviders = current.Routing
            .GetDistinctProviders();
        if (activeProviders.Contains(ReceptionProvider.P2pQuake) &&
            ProviderOptions.FromSettings(current) != ProviderOptions.FromSettings(updated))
        {
            return true;
        }

#if QTELOPPER_DMDATA_PROVIDER
        if (activeProviders.Contains(ReceptionProvider.Dmdata) &&
            DmdataProviderOptions.FromSettings(current) !=
                DmdataProviderOptions.FromSettings(updated))
        {
            return true;
        }
#endif

#if QTELOPPER_AXIS_PROVIDER
        if (activeProviders.Contains(ReceptionProvider.Axis) &&
            AxisProviderOptions.FromSettings(current) !=
                AxisProviderOptions.FromSettings(updated))
        {
            return true;
        }
#endif

        if (activeProviders.Contains(ReceptionProvider.Wolfx))
        {
            WolfxProviderOptions currentWolfx = WolfxProviderOptions.FromSettings(current);
            WolfxProviderOptions updatedWolfx = WolfxProviderOptions.FromSettings(updated);
            if ((currentWolfx.ReceiveEew &&
                 currentWolfx.EewWebSocketUri != updatedWolfx.EewWebSocketUri) ||
                (currentWolfx.ReceiveQuake &&
                 currentWolfx.QuakeWebSocketUri != updatedWolfx.QuakeWebSocketUri))
            {
                return true;
            }
        }

        return false;
    }

    private async Task SaveSettingsFromCommandAsync()
    {
        try
        {
            await SaveSettingsAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException and
            not OutOfMemoryException)
        {
            await WriteLogAsync(
                AppLogLevel.Error,
                "SettingsSaveFailed",
                "設定の保存または受信元の再接続に失敗しました。アプリは受信を停止した状態で継続します。",
                exception).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(
                () => ReceptionStatusText = "設定保存・再接続エラー（ログを確認）")
                .ConfigureAwait(false);
        }
    }

    public async Task ExportDiagnosticsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (_services.DiagnosticsWriter is null)
        {
            throw new InvalidOperationException("A diagnostics bundle writer is not configured.");
        }

        var snapshot = new DiagnosticsSnapshot(
            DiagnosticsSnapshot.CurrentSchemaVersion,
            _services.Clock.UtcNow,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            Environment.Version.ToString(),
            Environment.OSVersion.VersionString,
            ConnectionState.ToString(),
            _lastConnectionSnapshot.LastReceivedAt,
            ReconnectCount,
            ObsStatusText,
            ObsClientCount,
            _obsServer?.LastAudioCue ?? string.Empty,
            _obsServer?.LastAudioPlaybackResult ?? "None",
            _obsServer?.LastAudioPlaybackAtUtc,
            _settings,
            _services.UiLogs.GetSnapshot())
        {
            OperationalAlerts = _services.OperationalAlerts?.GetSnapshot() ?? [],
            SourceComparisons = _services.SourceComparison?.GetSnapshot(_services.Clock.UtcNow) ?? [],
            ProviderConnections = _services.ReceptionService.GetProviderConnections(),
            ObsRouteConnections = _obsServer?.RouteClientCounts ?? new Dictionary<string, int>(),
        };
        await _services.DiagnosticsWriter.WriteAsync(path, snapshot, cancellationToken)
            .ConfigureAwait(false);
        await WriteLogAsync(
            AppLogLevel.Information,
            "DiagnosticsExported",
            "秘匿化した診断ZIPを保存しました。").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _axisTokenRefreshStop.Cancel();
        try
        {
            await _axisTokenRefreshTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        CancelTestScenario();
        CancelHistoryRehearsal("終了しました");
        try
        {
            await _testScenarioTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        Task? historyReplayTask = _historyReplayTask;
        if (historyReplayTask is not null)
        {
            try
            {
                await historyReplayTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _services.UiLogs.EntryAdded -= OnLogEntryAdded;
        _services.EventSource.ConnectionChanged -= OnConnectionChanged;
        _services.ReceptionService.EventProcessed -= OnEventProcessed;
        if (_obsServer is not null)
        {
            _obsServer.ClientCountChanged -= OnObsClientCountChanged;
        }
        if (_obsBrowserSourceSynchronizer is not null)
        {
            _obsBrowserSourceSynchronizer.StatusChanged -= OnObsBrowserSyncStatusChanged;
        }
        DisposeOperationalFeatures();

        await CancelPendingWeatherAudioAsync().ConfigureAwait(false);

        _renderLoopStop.Cancel();
        try
        {
            await _renderLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _obsConfigurationTask.ConfigureAwait(false);
        if (_obsServer is not null)
        {
            await _obsServer.DisposeAsync().ConfigureAwait(false);
        }
        if (_obsBrowserSourceSynchronizer is not null)
        {
            await _obsBrowserSourceSynchronizer.DisposeAsync().ConfigureAwait(false);
        }

        if (_services.StateStore is not null)
        {
            CoordinatorSnapshot finalSnapshot = _services.DisplayCoordinator.Evaluate(
                resynchronizeFromUtc: true);
            QueueStateSave(finalSnapshot, lastShutdownWasClean: true, force: true);
            await GetStateSaveTask().ConfigureAwait(false);
        }

        await StopConnectionAsync().ConfigureAwait(false);
        await _services.DisposeAsync().ConfigureAwait(false);
        _renderLoopStop.Dispose();
        _axisTokenRefreshStop.Dispose();
        _axisTokenRefreshGate.Dispose();
        _obsLifecycle.Dispose();
    }

    private void StartConnection()
    {
        if (_receptionTask is { IsCompleted: false })
        {
            return;
        }

        if (_settings.Provider.Routing.GetDistinctProviders().Count == 0)
        {
            ReceptionStatusText = "すべての情報を受信しない設定です（API未接続）";
            return;
        }

        try
        {
            if (_services.EventSource is IProviderConfigurableEventSource configurable)
            {
                configurable.ConfigureProvider(_settings.Provider);
            }

            ReceptionStatusText = _settings.Provider.Mode == ProviderMode.Sandbox
                ? "Sandbox接続中・表示対象の受信待ち"
                : "表示対象の受信待ち";
            _receptionTask = RunReceptionAsync();
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            ReceptionStatusText = "接続設定エラー（ログを確認）";
            FireAndForgetLog(
                AppLogLevel.Error,
                "ProviderConfigurationFailed",
                "受信先設定を適用できませんでした。",
                exception);
        }
    }

    private bool IsReceptionRunning => _receptionTask is { IsCompleted: false };

    private async Task RunAxisTokenRefreshLoopAsync(CancellationToken cancellationToken)
    {
        if (_services.AxisTokenRefreshService is null)
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(AxisTokenRefreshCheckInterval);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAxisTokenIfDueAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is not StackOverflowException and
                    not OutOfMemoryException)
                {
                    await WriteLogAsync(
                        AppLogLevel.Error,
                        "AxisTokenRefreshTaskFailed",
                        "AXISトークンの定期確認で予期しないエラーが発生しました。次回確認時に再試行します。",
                        exception).ConfigureAwait(false);
                }

                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAxisTokenIfDueAsync(CancellationToken cancellationToken)
    {
        IAxisTokenRefreshService? refreshService = _services.AxisTokenRefreshService;
        if (refreshService is null)
        {
            return;
        }

        await _axisTokenRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProviderSettings provider = _settings.Provider;
            string accessToken = Settings.AxisAccessToken;
            if (!provider.Routing.Uses(ReceptionProvider.Axis) ||
                string.IsNullOrWhiteSpace(accessToken) ||
                !Uri.TryCreate(provider.AxisApiBaseUrl, UriKind.Absolute, out Uri? apiBaseUri))
            {
                return;
            }

            AxisTokenRefreshResult result;
            try
            {
                result = await refreshService.RefreshIfDueAsync(
                    apiBaseUri,
                    accessToken,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or
                IOException or InvalidDataException or JsonException)
            {
                await WriteLogAsync(
                    AppLogLevel.Warning,
                    "AxisTokenRefreshFailed",
                    "AXISトークンの自動更新に失敗しました。現在のトークンを維持し、次回の定期確認で再試行します。",
                    exception).ConfigureAwait(false);
                return;
            }

            switch (result.Outcome)
            {
                case AxisTokenRefreshOutcome.NotDue:
                case AxisTokenRefreshOutcome.Unchanged:
                    return;
                case AxisTokenRefreshOutcome.InvalidToken:
                    await WriteLogAsync(
                        AppLogLevel.Warning,
                        "AxisTokenRefreshSkipped",
                        "AXISトークンから有効期限を確認できないため、自動更新を行いませんでした。設定したJWTを確認してください。").ConfigureAwait(false);
                    return;
                case AxisTokenRefreshOutcome.Expired:
                    await WriteLogAsync(
                        AppLogLevel.Error,
                        "AxisTokenExpired",
                        "AXISトークンは期限切れのため自動更新できません。AXISで新しいトークンを発行して設定してください。").ConfigureAwait(false);
                    return;
                case AxisTokenRefreshOutcome.ContractExpired:
                    await WriteLogAsync(
                        AppLogLevel.Error,
                        "AxisContractExpired",
                        "AXIS契約が期限切れのためトークンを更新できません。契約状態を確認してください。").ConfigureAwait(false);
                    return;
                case AxisTokenRefreshOutcome.AuthorizationFailed:
                    await WriteLogAsync(
                        AppLogLevel.Error,
                        "AxisTokenRefreshAuthorizationFailed",
                        "AXISトークンの認証に失敗しました。設定したトークンを確認してください。").ConfigureAwait(false);
                    return;
                case AxisTokenRefreshOutcome.Refreshed:
                    await ApplyRefreshedAxisTokenAsync(
                        accessToken,
                        result.AccessToken,
                        cancellationToken).ConfigureAwait(false);
                    return;
                default:
                    throw new InvalidOperationException("Unknown AXIS token refresh result.");
            }
        }
        finally
        {
            _axisTokenRefreshGate.Release();
        }
    }

#if QTELOPPER_AXIS_PROVIDER
    private async Task ApplyRefreshedAxisTokenAsync(
        string previousToken,
        string refreshedToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshedToken) ||
            !string.Equals(Settings.AxisAccessToken, previousToken, StringComparison.Ordinal) ||
            !_settings.Provider.Routing.Uses(ReceptionProvider.Axis))
        {
            return;
        }

        string protectedToken = AxisCredentialProtector.Protect(refreshedToken);
        if (string.IsNullOrWhiteSpace(protectedToken))
        {
            await WriteLogAsync(
                AppLogLevel.Error,
                "AxisTokenRefreshPersistenceFailed",
                "更新したAXISトークンをWindows DPAPIで保護できなかったため、現在のトークンを維持します。").ConfigureAwait(false);
            return;
        }

        bool restartReception = IsReceptionRunning;
        if (restartReception)
        {
            await StopConnectionAsync().ConfigureAwait(false);
        }

        await _dispatcher.InvokeAsync(
            () => Settings.AxisAccessToken = refreshedToken,
            cancellationToken).ConfigureAwait(false);
        AppSettings updated = _settings with
        {
            Provider = _settings.Provider with
            {
                AxisProtectedAccessToken = protectedToken,
            },
        };
        _settings = updated;
        try
        {
            await _services.SettingsStore.SaveAsync(updated, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            await WriteLogAsync(
                AppLogLevel.Error,
                "AxisTokenRefreshPersistenceFailed",
                "更新したAXISトークンを設定ファイルへ保存できませんでした。アプリ終了前に設定を保存してください。",
                exception).ConfigureAwait(false);
        }

        if (_services.EventSource is IProviderConfigurableEventSource configurable)
        {
            configurable.ConfigureProvider(updated.Provider);
        }

        await WriteLogAsync(
            AppLogLevel.Information,
            "AxisTokenRefreshed",
            "AXISトークンを自動更新し、Windows DPAPIで保護しました。").ConfigureAwait(false);
        if (restartReception && !cancellationToken.IsCancellationRequested)
        {
            await _dispatcher.InvokeAsync(StartConnection, cancellationToken).ConfigureAwait(false);
        }
    }
#else
    private static Task ApplyRefreshedAxisTokenAsync(
        string previousToken,
        string refreshedToken,
        CancellationToken cancellationToken) => Task.CompletedTask;
#endif

    private async Task RunReceptionAsync()
    {
        try
        {
            await _services.ReceptionService.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            await WriteLogAsync(
                AppLogLevel.Error,
                "ReceptionStopped",
                "受信処理が停止しました。",
                exception).ConfigureAwait(false);
        }
    }

    private async Task StopConnectionAsync()
    {
        await _services.ReceptionService.StopAsync().ConfigureAwait(false);
        Task? receptionTask = _receptionTask;
        if (receptionTask is not null)
        {
            await receptionTask.ConfigureAwait(false);
        }

        _receptionTask = null;
    }

    private void RequestManualDisconnect()
    {
        if (!IsConnectedOrConnecting)
        {
            return;
        }

        if (!_confirmationService.ConfirmDisconnect())
        {
            FireAndForgetLog(
                AppLogLevel.Information,
                "DisconnectCancelled",
                "手動切断を取り消しました。");
            return;
        }

        _ = StopConnectionAsync();
    }

    private void RunSelectedTest()
    {
        TestScenario? scenario = SelectedScenario;
        if (scenario is null)
        {
            return;
        }

        bool productionConnected = Settings.ProviderMode == ProviderMode.Production &&
            ConnectionState is ProviderConnectionState.Connected or ProviderConnectionState.Stale;
        if (productionConnected && !_confirmationService.ConfirmProductionTest())
        {
            FireAndForgetLog(AppLogLevel.Warning, "TestCancelled", "本番接続中のテストを取り消しました。");
            return;
        }

        CancelTestScenario();
        var cancellation = new CancellationTokenSource();
        _testScenarioCancellation = cancellation;
        _testScenarioTask = RunTestScenarioAsync(scenario, cancellation);
    }

    private async Task RunTestScenarioAsync(
        TestScenario scenario,
        CancellationTokenSource cancellation)
    {
        var concurrentEewComposer = new ConcurrentEewProgramComposer();
        var previewCoordinator = new PriorityCoordinator(_services.Clock, _settings.Display);
        try
        {
            for (int index = 0; index < scenario.Steps.Count; index++)
            {
                TestScenarioStep step = scenario.Steps[index];
                if (step.DelayAfterPrevious > TimeSpan.Zero)
                {
                    await Task.Delay(step.DelayAfterPrevious, cancellation.Token).ConfigureAwait(false);
                }

                int stepNumber = index + 1;
                await _dispatcher.InvokeAsync(
                    () => DisplayTestScenarioStep(
                        scenario,
                        step,
                        stepNumber,
                        concurrentEewComposer,
                        previewCoordinator),
                    cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _testScenarioCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void DisplayTestScenarioStep(
        TestScenario scenario,
        TestScenarioStep step,
        int stepNumber,
        ConcurrentEewProgramComposer concurrentEewComposer,
        PriorityCoordinator previewCoordinator)
    {
        DisasterEvent disasterEvent = step.Event;
        DisplayProgram program = _services.PageComposer.Compose(disasterEvent, _settings.Display) with
        {
            ProgramId = $"{disasterEvent.Id.Value}:{_services.IdGenerator.NewId()}",
            StartedAtUtc = _services.Clock.UtcNow,
        };
        if (disasterEvent is EewEvent eew)
        {
            program = concurrentEewComposer.Compose(eew, program, _settings.Display);
        }

        CoordinatorSnapshot snapshot = previewCoordinator.Apply(program);
        _obsSnapshotStore.PublishProgram(
            disasterEvent,
            program,
            _settings.Display,
            _services.Clock.UtcNow);
        _previewCoordinator = previewCoordinator;
        Volatile.Write(ref _activeCoordinator, previewCoordinator);
        ApplyDisplaySnapshot(snapshot);
        PlayEventAudio(disasterEvent, Settings.ToSettings(_settings).Audio);
        ShowPreviewRequested?.Invoke(this, EventArgs.Empty);
        FireAndForgetLog(
            AppLogLevel.Information,
            "PreviewTest",
            scenario.Steps.Count == 1
                ? $"テスト表示: {scenario.Label}"
                : $"テスト表示: {scenario.Label}（{stepNumber}/{scenario.Steps.Count}）");
    }

    private void CancelTestScenario()
    {
        try
        {
            Volatile.Read(ref _testScenarioCancellation)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The final scenario step completed between the read and cancellation.
        }
    }

    private void ClearDisplay()
    {
        CancelTestScenario();
        CancelHistoryRehearsal("表示消去により停止しました");
        _services.IngestionPipeline.ClearTransientState();
        Volatile.Write(ref _historyCoordinator, (IDisplayCoordinator?)null);
        _productionReplayCatalog.Clear();
        _productionReplayCoordinator = null;
        _productionReplayResumeAfterUtc = DateTimeOffset.MaxValue;
        _productionReplayNextSwitchUtc = DateTimeOffset.MaxValue;
        _previewCoordinator = new PriorityCoordinator(_services.Clock, _settings.Display);
        _manualSubtitleEdits.Clear();
        CoordinatorSnapshot snapshot = _services.DisplayCoordinator.Clear();
        _obsSnapshotStore.ClearPrograms(_services.Clock.UtcNow);
        Volatile.Write(ref _activeCoordinator, _services.DisplayCoordinator);
        ApplyDisplaySnapshot(snapshot);
        ReceptionStatusText = "表示を手動消去しました（受信は継続中）";
        _ = StopAudioAsync();
        FireAndForgetLog(
            AppLogLevel.Information,
            "DisplayCleared",
            "現在表示・待機表示・継続中の津波表示を手動消去しました。受信は継続します。");
    }

    private void ClearDisplay(EventKind kind)
    {
        CancelTestScenario();
        CancelHistoryRehearsal("種別別の表示消去により停止しました");
        _services.IngestionPipeline.ClearTransientState(kind);
        _productionReplayCatalog.RemoveKind(kind);
        foreach (DisplayProgram program in _manualSubtitleEdits.Keys
                     .Where(program => program.Kind == kind)
                     .ToArray())
        {
            _manualSubtitleEdits.Remove(program);
        }

        DateTimeOffset nowUtc = _services.Clock.UtcNow;
        CoordinatorSnapshot mainSnapshot = _services.DisplayCoordinator is PriorityCoordinator mainCoordinator
            ? mainCoordinator.Clear(kind)
            : _services.DisplayCoordinator.Clear();
        IDisplayCoordinator active = Volatile.Read(ref _activeCoordinator);
        CoordinatorSnapshot displaySnapshot;
        if (ReferenceEquals(active, _services.DisplayCoordinator))
        {
            displaySnapshot = mainSnapshot;
        }
        else if (active is PriorityCoordinator temporary)
        {
            displaySnapshot = temporary.Clear(kind);
            if (displaySnapshot.CurrentProgram is null)
            {
                Volatile.Write(ref _activeCoordinator, _services.DisplayCoordinator);
                displaySnapshot = mainSnapshot;
            }
        }
        else
        {
            Volatile.Write(ref _activeCoordinator, _services.DisplayCoordinator);
            displaySnapshot = mainSnapshot;
        }

        _obsSnapshotStore.ClearProgram(kind, nowUtc);
        ApplyDisplaySnapshot(displaySnapshot);
        ReceptionStatusText = $"{GetEventKindText(kind)}の表示を消去しました（受信は継続中）";
        FireAndForgetLog(
            AppLogLevel.Information,
            "DisplayKindCleared",
            $"種別別に表示を手動消去しました。種別={kind} 受信は継続します。");
    }

    private void RedisplaySelectedTelegram()
    {
        if (SelectedReceivedTelegram is { } item)
        {
            if (_telegramReplayMode == TelegramReplayMode.ProductionReplay &&
                item.Event.SourceMode != SourceMode.Production)
            {
                TelegramReviewStatusText =
                    "本番情報として再掲できるのは、本番で受信した電文だけです。";
                return;
            }

            string label = BuildTelegramReplayLabel(
                _telegramReplayMode,
                item.Event.IssuedAt,
                _services.Clock.UtcNow);
            DisplayRehearsal(item.Event, item.Program, label);
            TelegramReviewStatusText = $"{GetTelegramReplayModeText(_telegramReplayMode)}として再表示しました。";
        }
    }

    private void SetTelegramReplayMode(TelegramReplayMode mode)
    {
        if (_telegramReplayMode == mode)
        {
            return;
        }

        _telegramReplayMode = mode;
        OnPropertyChanged(nameof(IsProductionReplayMode));
        OnPropertyChanged(nameof(IsPastInformationReplayMode));
        OnPropertyChanged(nameof(IsTrainingReplayMode));
    }

    private static string BuildTelegramReplayLabel(
        TelegramReplayMode mode,
        DateTimeOffset issuedAt,
        DateTimeOffset nowUtc)
    {
        DateTimeOffset localIssuedAt = issuedAt.ToLocalTime();
        DateTimeOffset localNow = nowUtc.ToLocalTime();
        string issuedAtText = localIssuedAt.Year == localNow.Year
            ? localIssuedAt.ToString("M月d日HH時mm分発表", CultureInfo.InvariantCulture)
            : localIssuedAt.ToString("yyyy年M月d日HH時mm分発表", CultureInfo.InvariantCulture);
        return mode == TelegramReplayMode.ProductionReplay
            ? issuedAtText
            : $"{GetTelegramReplayModeText(mode)}｜{issuedAtText}";
    }

    private static string GetTelegramReplayModeText(TelegramReplayMode mode) => mode switch
    {
        TelegramReplayMode.ProductionReplay => "本番情報・再掲",
        TelegramReplayMode.PastInformation => "過去情報",
        _ => "訓練",
    };

    private void AddTelegramForReview(
        DisasterEvent disasterEvent,
        DisplayProgram program,
        string displayResult = "表示済み")
    {
        ReceivedTelegramViewModel? duplicate = ReceivedTelegrams.FirstOrDefault(item =>
            item.Event.SourceMode == disasterEvent.SourceMode &&
            string.Equals(item.Event.Provider, disasterEvent.Provider, StringComparison.Ordinal) &&
            string.Equals(item.Event.Id.Value, disasterEvent.Id.Value, StringComparison.Ordinal) &&
            item.Event.IssuedAt == disasterEvent.IssuedAt);
        if (duplicate is not null)
        {
            ReceivedTelegrams.Remove(duplicate);
        }

        var item = new ReceivedTelegramViewModel(disasterEvent, program, displayResult);
        ReceivedTelegrams.Insert(0, item);
        while (ReceivedTelegrams.Count > 500)
        {
            ReceivedTelegrams.RemoveAt(ReceivedTelegrams.Count - 1);
        }

        SelectedReceivedTelegram = item;
    }

    private void DisplayRehearsal(
        DisasterEvent disasterEvent,
        DisplayProgram sourceProgram,
        string label)
    {
        CancelTestScenario();
        var coordinator = new PriorityCoordinator(_services.Clock, _settings.Display);
        DateTimeOffset nowUtc = _services.Clock.UtcNow;
        DisplayProgram program = sourceProgram with
        {
            ProgramId = $"{sourceProgram.ProgramId}:rehearsal:{_services.IdGenerator.NewId()}",
            SourceMode = SourceMode.HistoryRehearsal,
            StartedAtUtc = nowUtc,
            RehearsalLabel = label,
        };
        CoordinatorSnapshot snapshot = coordinator.Apply(program);
        _obsSnapshotStore.PublishProgram(disasterEvent, program, _settings.Display, nowUtc);
        _previewCoordinator = coordinator;
        Volatile.Write(ref _activeCoordinator, coordinator);
        ApplyDisplaySnapshot(snapshot);
        ShowPreviewRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestSubtitleEdit()
    {
        DisplayProgram? sourceProgram = _lastCoordinatorSnapshot?.CurrentProgram;
        if (sourceProgram is not null)
        {
            EditSubtitleRequested?.Invoke(
                sourceProgram,
                GetEditedProgram(sourceProgram));
        }
    }

    private void RequestPendingSubtitleEdit()
    {
        IReadOnlyList<DisplayProgram> programs = GetWaitingSubtitlePrograms(_lastCoordinatorSnapshot);
        if (programs.Count > 0)
        {
            EditPendingSubtitleRequested?.Invoke(programs);
        }
    }

    private static IReadOnlyList<DisplayProgram> GetWaitingSubtitlePrograms(
        CoordinatorSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return Array.Empty<DisplayProgram>();
        }

        var programs = snapshot.PendingPrograms.ToList();
        if (snapshot.PersistentTsunami is { } persistentTsunami &&
            !ReferenceEquals(persistentTsunami, snapshot.CurrentProgram) &&
            !programs.Any(program => ReferenceEquals(program, persistentTsunami)))
        {
            programs.Add(persistentTsunami);
        }

        return programs;
    }

    private void RequestPreDisplaySubtitleEdit()
    {
        DisplayProgram[] programs = _preDisplaySubtitleDrafts
            .Select(static draft => draft.Program)
            .ToArray();
        if (programs.Length > 0)
        {
            EditPreDisplaySubtitleRequested?.Invoke(programs);
        }
    }

    public bool TryReleasePreDisplaySubtitle(
        DisplayProgram sourceProgram,
        DisplayProgram editedProgram)
    {
        ArgumentNullException.ThrowIfNull(sourceProgram);
        ArgumentNullException.ThrowIfNull(editedProgram);
        int index = _preDisplaySubtitleDrafts.FindIndex(
            draft => ReferenceEquals(draft.Program, sourceProgram));
        if (index < 0 ||
            !string.Equals(sourceProgram.ProgramId, editedProgram.ProgramId, StringComparison.Ordinal))
        {
            ReceptionStatusText = "作画前情報が更新されたため、編集内容を送出できませんでした";
            return false;
        }

        PreDisplaySubtitleDraft draft = _preDisplaySubtitleDrafts[index];
        _preDisplaySubtitleDrafts.RemoveAt(index);
        UpdatePreDisplayDraftState();

        DisplayProgram releasedProgram = editedProgram with
        {
            StartedAtUtc = _services.Clock.UtcNow,
            EndPolicy = sourceProgram.EndPolicy,
        };
        CoordinatorSnapshot snapshot = _services.DisplayCoordinator.Apply(releasedProgram);
        if (draft.Event.SourceMode == SourceMode.Production)
        {
            DateTimeOffset nowUtc = _services.Clock.UtcNow;
            _productionReplayCatalog.Update(
                draft.Event,
                releasedProgram,
                _settings.Display.ProductionReplay,
                nowUtc);
            _productionReplayCoordinator = null;
            _productionReplayResumeAfterUtc = nowUtc.AddSeconds(
                _settings.Display.ProductionReplay.ResumeDelaySeconds);
            _productionReplayNextSwitchUtc = _productionReplayResumeAfterUtc;
        }

        if (EventDisplayFilter.Apply(_settings.Filter, draft.Event) is DisasterEvent audibleEvent)
        {
            PlayEventAudio(audibleEvent, _settings.Audio);
        }
        _obsSnapshotStore.PublishProgram(
            draft.Event,
            releasedProgram,
            _settings.Display,
            _services.Clock.UtcNow);
        Volatile.Write(ref _activeCoordinator, _services.DisplayCoordinator);
        ApplyDisplaySnapshot(snapshot);
        ReceptionStatusText = $"作画前編集した字幕を送出: {GetEventKindText(draft.Event.Kind)}";
        FireAndForgetLog(
            AppLogLevel.Information,
            "PreDisplaySubtitleReleased",
            $"作画前編集した字幕を送出しました。種別={draft.Event.Kind} イベントID={draft.Event.Id}");
        return true;
    }

    private void QueuePreDisplaySubtitleDraft(DisasterEvent disasterEvent, DisplayProgram program)
    {
        _preDisplaySubtitleDrafts.RemoveAll(draft =>
            draft.Event.Kind == disasterEvent.Kind &&
            draft.Event.Id == disasterEvent.Id);
        _preDisplaySubtitleDrafts.Add(new PreDisplaySubtitleDraft(disasterEvent, program));
        if (_preDisplaySubtitleDrafts.Count > 100)
        {
            _preDisplaySubtitleDrafts.RemoveAt(0);
        }
        UpdatePreDisplayDraftState();
        ReceptionStatusText =
            $"作画前編集待ち: {GetEventKindText(disasterEvent.Kind)}（{_preDisplaySubtitleDrafts.Count}件）";
        FireAndForgetLog(
            AppLogLevel.Information,
            "PreDisplaySubtitleHeld",
            $"字幕を作画前編集のため保留しました。種別={disasterEvent.Kind} イベントID={disasterEvent.Id}");
    }

    private void UpdatePreDisplayDraftState()
    {
        OnPropertyChanged(nameof(PreDisplayDraftText));
        EditPreDisplaySubtitleCommand.RaiseCanExecuteChanged();
    }

    public DisplayProgram GetEditedProgram(DisplayProgram sourceProgram)
    {
        ArgumentNullException.ThrowIfNull(sourceProgram);
        return _manualSubtitleEdits.TryGetValue(sourceProgram, out DisplayProgram? edited)
            ? edited
            : sourceProgram;
    }

    public bool TryApplySubtitleEdit(
        DisplayProgram sourceProgram,
        DisplayProgram editedProgram)
    {
        ArgumentNullException.ThrowIfNull(sourceProgram);
        ArgumentNullException.ThrowIfNull(editedProgram);
        CoordinatorSnapshot? snapshot = _lastCoordinatorSnapshot;
        bool isCurrent = snapshot?.CurrentProgram is { } current &&
            ReferenceEquals(current, sourceProgram);
        bool isPending = snapshot?.PendingPrograms.Any(program =>
            ReferenceEquals(program, sourceProgram)) == true;
        bool isPersistent = snapshot?.PersistentTsunami is { } persistent &&
            ReferenceEquals(persistent, sourceProgram);
        if (snapshot is null || (!isCurrent && !isPending && !isPersistent) ||
            !string.Equals(sourceProgram.ProgramId, editedProgram.ProgramId, StringComparison.Ordinal))
        {
            ReceptionStatusText = "表示が更新されたため、字幕編集を反映できませんでした";
            FireAndForgetLog(
                AppLogLevel.Warning,
                "SubtitleEditRejected",
                "字幕編集中に表示対象が更新されたため、手動編集を反映しませんでした。");
            return false;
        }

        _manualSubtitleEdits[sourceProgram] = editedProgram with
        {
            StartedAtUtc = sourceProgram.StartedAtUtc,
            EndPolicy = sourceProgram.EndPolicy,
        };
        if (isCurrent)
        {
            ApplyDisplaySnapshot(snapshot);
            ReceptionStatusText = "手動編集した字幕を表示しています（次の受信更新で解除）";
        }
        else
        {
            ReceptionStatusText = "表示待ちの字幕を編集しました（表示順が来ると反映）";
        }
        FireAndForgetLog(
            AppLogLevel.Information,
            "SubtitleEdited",
            $"字幕を手動編集しました。状態={(isCurrent ? "表示中" : "表示待ち")} ProgramId={sourceProgram.ProgramId} ページ数={editedProgram.Pages.Count}。受信データと履歴は変更していません。");
        return true;
    }

    private async Task LoadHistoryForReviewAsync()
    {
        if (_services.HistoryRehearsalLoader is null ||
            IsTelegramHistoryLoading ||
            IsHistoryRehearsalRunning)
        {
            return;
        }

        IsTelegramHistoryLoading = true;
        TelegramReviewStatusText = "過去電文を取得中…";
        AppSettings requested = Settings.ToSettings(_settings);
        try
        {
            HistoryRehearsalLoadResult loaded = await _services.HistoryRehearsalLoader.LoadAsync(
                requested.History,
                requested.Provider,
                _renderLoopStop.Token).ConfigureAwait(false);
            HistoryReplayItemViewModel[] items = loaded.Events
                .Select(disasterEvent => CreateHistoryReplayItem(disasterEvent, requested.Display))
                .Where(static item => item.Program.Pages.Count > 0)
                .ToArray();

            await _dispatcher.InvokeAsync(() =>
            {
                foreach (HistoryReplayItemViewModel item in items.Reverse())
                {
                    AddTelegramForReview(item.DisasterEvent, item.Program);
                }

                SelectedReceivedTelegram = ReceivedTelegrams.FirstOrDefault();
                TelegramReviewStatusText = items.Length == 0
                    ? "確認できる過去電文がありませんでした。履歴設定とログを確認してください。"
                    : $"過去電文を{items.Length}件取得しました（対象外 {loaded.IgnoredCount}件、不正 {loaded.InvalidCount}件）。";
            }, _renderLoopStop.Token).ConfigureAwait(false);

            await WriteLogAsync(
                AppLogLevel.Information,
                "TelegramReviewHistoryLoaded",
                $"確認用の過去電文を{items.Length}件取得しました。対象外={loaded.IgnoredCount} 不正={loaded.InvalidCount}")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_renderLoopStop.IsCancellationRequested)
        {
            // アプリ終了に伴うキャンセルです。
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                TelegramReviewStatusText = "過去電文の取得に失敗しました（ログを確認してください）。";
            }).ConfigureAwait(false);
            await WriteLogAsync(
                AppLogLevel.Error,
                "TelegramReviewHistoryLoadFailed",
                $"確認用の過去電文を取得できませんでした: {exception.Message}",
                exception).ConfigureAwait(false);
        }
        finally
        {
            if (!_renderLoopStop.IsCancellationRequested)
            {
                await _dispatcher.InvokeAsync(() => IsTelegramHistoryLoading = false)
                    .ConfigureAwait(false);
            }
        }
    }

    private void StartHistoryRehearsal()
    {
        if (_services.HistoryRehearsalLoader is null || IsHistoryRehearsalRunning)
        {
            return;
        }

        bool productionConnected = Settings.ProviderMode == ProviderMode.Production &&
            ConnectionState is ProviderConnectionState.Connected or ProviderConnectionState.Stale;
        if (productionConnected)
        {
            HistoryRehearsalStatusText = "本番接続中は開始できません";
            FireAndForgetLog(
                AppLogLevel.Warning,
                "HistoryRehearsalBlocked",
                "本番接続中のため履歴リハーサルを開始しませんでした。");
            return;
        }

        AppSettings requested = Settings.ToSettings(_settings);
        var cancellation = new CancellationTokenSource();
        _historyReplayCancellation = cancellation;
        _historyCancellationStatus = "停止しました";
        IsHistoryRehearsalRunning = true;
        HistoryRehearsalStatusText = "履歴を取得中…";
        _historyReplayTask = RunHistoryRehearsalAsync(requested, cancellation);
    }

    private void StartSelectedHistoryRehearsal()
    {
        HistoryReplayItemViewModel? selected = SelectedHistoryItem;
        if (selected is null || IsHistoryRehearsalRunning)
        {
            return;
        }

        bool productionConnected = Settings.ProviderMode == ProviderMode.Production &&
            ConnectionState is ProviderConnectionState.Connected or ProviderConnectionState.Stale;
        if (productionConnected)
        {
            HistoryRehearsalStatusText = "本番接続中は開始できません";
            return;
        }

        AppSettings requested = Settings.ToSettings(_settings);
        var cancellation = new CancellationTokenSource();
        _historyReplayCancellation = cancellation;
        _historyCancellationStatus = "停止しました";
        IsHistoryRehearsalRunning = true;
        HistoryRehearsalStatusText = "選択した履歴を再生します…";
        _historyReplayTask = RunSelectedHistoryRehearsalAsync(selected, requested, cancellation);
    }

    private void CancelHistoryRehearsal(string status)
    {
        CancellationTokenSource? cancellation = _historyReplayCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        _historyCancellationStatus = status;
        HistoryRehearsalStatusText = status;
        cancellation.Cancel();
    }

    private async Task RunHistoryRehearsalAsync(
        AppSettings requested,
        CancellationTokenSource cancellation)
    {
        string finalStatus = "停止しました";
        try
        {
            IHistoryRehearsalLoader loader = _services.HistoryRehearsalLoader!;
            await WriteLogAsync(
                AppLogLevel.Information,
                "HistoryRehearsalLoading",
                $"履歴リハーサル用データを取得します。api={requested.History.Api} limit={requested.History.Limit} niiDate={requested.History.NiiDate:yyyy-MM-dd} niiContent={requested.History.NiiContent} niiDirectUrl={!string.IsNullOrWhiteSpace(requested.History.NiiReportUrl)} localXmlFile={!string.IsNullOrWhiteSpace(requested.History.LocalXmlFilePath)}")
                .ConfigureAwait(false);
            HistoryRehearsalLoadResult loaded = await loader.LoadAsync(
                requested.History,
                requested.Provider,
                cancellation.Token).ConfigureAwait(false);
            DisasterEvent[] events = loaded.Events
                .Select(disasterEvent => EventDisplayFilter.Apply(requested.Filter, disasterEvent))
                .OfType<DisasterEvent>()
                .ToArray();
            if (loaded.IgnoredCount > 0 || loaded.InvalidCount > 0)
            {
                await WriteLogAsync(
                    AppLogLevel.Warning,
                    "HistoryItemsSkipped",
                    $"履歴のうち対象外 {loaded.IgnoredCount}件、不正 {loaded.InvalidCount}件を除外しました。")
                    .ConfigureAwait(false);
            }

            if (events.Length == 0)
            {
                finalStatus = "再生可能な履歴がありません";
                await WriteLogAsync(
                    AppLogLevel.Warning,
                    "HistoryRehearsalEmpty",
                    "履歴APIまたは外部XMLから再生可能な対応防災情報を取得できませんでした。")
                    .ConfigureAwait(false);
                return;
            }

            HistoryReplayItemViewModel[] historyItems = events
                .Select(disasterEvent => CreateHistoryReplayItem(disasterEvent, requested.Display))
                .ToArray();
            await _dispatcher.InvokeAsync(() =>
            {
                HistoryItems.Clear();
                foreach (HistoryReplayItemViewModel item in historyItems)
                {
                    HistoryItems.Add(item);
                    AddTelegramForReview(item.DisasterEvent, item.Program);
                }

                SelectedHistoryItem = HistoryItems.FirstOrDefault();
            }, cancellation.Token).ConfigureAwait(false);

            await WriteLogAsync(
                AppLogLevel.Information,
                "HistoryRehearsalStarted",
                $"履歴リハーサルを開始しました（{historyItems.Length}件、古い順、繰り返し={requested.History.Repeat}）。")
                .ConfigureAwait(false);
            await ReplayHistoryItemsAsync(historyItems, requested, cancellation)
                .ConfigureAwait(false);

            finalStatus = $"再生完了（{historyItems.Length}件）";
            await WriteLogAsync(
                AppLogLevel.Information,
                "HistoryRehearsalCompleted",
                finalStatus).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            finalStatus = _historyCancellationStatus;
            await WriteLogAsync(
                AppLogLevel.Information,
                "HistoryRehearsalStopped",
                finalStatus).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            finalStatus = "取得・再生に失敗しました（ログを確認）";
            await WriteLogAsync(
                AppLogLevel.Error,
                "HistoryRehearsalFailed",
                $"履歴リハーサルの取得または再生に失敗しました: {exception.Message}",
                exception).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                IDisplayCoordinator? historyCoordinator = Volatile.Read(ref _historyCoordinator);
                if (historyCoordinator is not null &&
                    ReferenceEquals(Volatile.Read(ref _activeCoordinator), historyCoordinator))
                {
                    Volatile.Write(ref _activeCoordinator, _services.DisplayCoordinator);
                    ApplyDisplaySnapshot(_services.DisplayCoordinator.Evaluate(resynchronizeFromUtc: true));
                }

                Volatile.Write(ref _historyCoordinator, (IDisplayCoordinator?)null);
                if (ReferenceEquals(_historyReplayCancellation, cancellation))
                {
                    _historyReplayCancellation = null;
                }

                HistoryRehearsalStatusText = finalStatus;
                IsHistoryRehearsalRunning = false;
            }).ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    private async Task RunSelectedHistoryRehearsalAsync(
        HistoryReplayItemViewModel selected,
        AppSettings requested,
        CancellationTokenSource cancellation)
    {
        string finalStatus = "停止しました";
        try
        {
            await ReplayHistoryItemsAsync([selected], requested, cancellation)
                .ConfigureAwait(false);
            finalStatus = "選択した履歴の再生が完了しました";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            finalStatus = _historyCancellationStatus;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            finalStatus = "履歴再生に失敗しました（ログを確認）";
            await WriteLogAsync(
                AppLogLevel.Error,
                "SelectedHistoryReplayFailed",
                $"選択した履歴を再生できませんでした: {exception.Message}",
                exception).ConfigureAwait(false);
        }
        finally
        {
            await FinishHistoryReplayAsync(finalStatus, cancellation).ConfigureAwait(false);
        }
    }

    private async Task ReplayHistoryItemsAsync(
        HistoryReplayItemViewModel[] items,
        AppSettings requested,
        CancellationTokenSource cancellation)
    {
        bool firstItem = true;
        do
        {
            for (int index = 0; index < items.Length; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                HistoryReplayItemViewModel item = items[index];
                DisasterEvent disasterEvent = item.DisasterEvent;
                DisplayProgram program = item.Program with
                {
                    ProgramId = $"history:{disasterEvent.Id.Value}:{_services.IdGenerator.NewId()}",
                };
                TimeSpan itemDuration = HistoryReplayTiming.GetItemDuration(
                    program,
                    requested.Display,
                    requested.History.IntervalSeconds);
                var coordinator = new PriorityCoordinator(_services.Clock, requested.Display);
                CoordinatorSnapshot snapshot = coordinator.Apply(program);
                int itemNumber = index + 1;
                await _dispatcher.InvokeAsync(() =>
                {
                    SelectedHistoryItem = item;
                    Volatile.Write(ref _historyCoordinator, coordinator);
                    Volatile.Write(ref _activeCoordinator, coordinator);
                    _obsSnapshotStore.PublishProgram(
                        disasterEvent,
                        program,
                        requested.Display,
                        _services.Clock.UtcNow);
                    ApplyDisplaySnapshot(snapshot);
                    HistoryRehearsalStatusText = requested.History.Repeat
                        ? $"繰り返し再生中 {itemNumber} / {items.Length}"
                        : $"再生中 {itemNumber} / {items.Length}";
                    if (firstItem)
                    {
                        ShowPreviewRequested?.Invoke(this, EventArgs.Empty);
                        firstItem = false;
                    }
                }, cancellation.Token).ConfigureAwait(false);
                await WriteLogAsync(
                    AppLogLevel.Information,
                    "HistoryRehearsalItem",
                    $"履歴再生 {itemNumber}/{items.Length}: code={disasterEvent.ProviderCode} type={GetHistoryItemType(disasterEvent)} id={disasterEvent.Id.Value} pages={program.Pages.Count} hold={itemDuration.TotalSeconds:0.0}s")
                    .ConfigureAwait(false);
                PlayEventAudio(disasterEvent, requested.Audio);
                await Task.Delay(itemDuration, cancellation.Token).ConfigureAwait(false);
            }
        }
        while (requested.History.Repeat);
    }

    private HistoryReplayItemViewModel CreateHistoryReplayItem(
        DisasterEvent disasterEvent,
        DisplaySettings displaySettings)
    {
        DisplayProgram program = _services.PageComposer.Compose(disasterEvent, displaySettings);
        string kind = disasterEvent.Kind switch
        {
            EventKind.Quake => "地震",
            EventKind.Tsunami => "津波",
            EventKind.Eew => "緊急地震速報",
            EventKind.WeatherWarning => "気象警報・注意報",
            EventKind.Volcano => "火山情報",
            _ => disasterEvent.Kind.ToString(),
        };
        string correction = disasterEvent.IsCorrection ? " 訂正" : string.Empty;
        string cancellation = disasterEvent.IsCancelled ? " 解除・取消" : string.Empty;
        string displayText = $"{disasterEvent.IssuedAt.ToLocalTime():MM/dd HH:mm:ss}  {kind}{correction}{cancellation}  {GetHistoryItemType(disasterEvent)}";
        return new HistoryReplayItemViewModel(displayText, disasterEvent, program);
    }

    private Task FinishHistoryReplayAsync(
        string finalStatus,
        CancellationTokenSource cancellation) => _dispatcher.InvokeAsync(() =>
        {
            IDisplayCoordinator? historyCoordinator = Volatile.Read(ref _historyCoordinator);
            if (historyCoordinator is not null &&
                ReferenceEquals(Volatile.Read(ref _activeCoordinator), historyCoordinator))
            {
                Volatile.Write(ref _activeCoordinator, _services.DisplayCoordinator);
                ApplyDisplaySnapshot(_services.DisplayCoordinator.Evaluate(resynchronizeFromUtc: true));
            }

            Volatile.Write(ref _historyCoordinator, (IDisplayCoordinator?)null);
            if (ReferenceEquals(_historyReplayCancellation, cancellation))
            {
                _historyReplayCancellation = null;
            }

            HistoryRehearsalStatusText = finalStatus;
            IsHistoryRehearsalRunning = false;
            cancellation.Dispose();
        });

    private static string GetHistoryItemType(DisasterEvent disasterEvent) => disasterEvent switch
    {
        QuakeEvent quake => quake.Issue.RawType,
        TsunamiEvent tsunami => tsunami.Issue.RawType,
        EewEvent eew => eew.Issue.RawType,
        WeatherWarningEvent weather => weather.Issue.RawType,
        VolcanoEvent volcano => volcano.Issue.RawType,
        _ => disasterEvent.Kind.ToString(),
    };

    private void OnConnectionChanged(object? sender, ProviderConnectionSnapshot snapshot) =>
        _dispatcher.Invoke(() =>
        {
            UpdateConnection(snapshot);
        });

    private void UpdateConnection(ProviderConnectionSnapshot snapshot)
    {
        _lastConnectionSnapshot = snapshot;
        ProviderConnectionState previous = ConnectionState;
        ConnectionState = snapshot.State;
        LastReceivedText = snapshot.LastReceivedAt?.ToLocalTime().ToString(
            "HH:mm:ss",
            CultureInfo.CurrentCulture) ?? "—";
        RetryDelayText = snapshot.RetryDelay is TimeSpan delay
            ? $"{delay.TotalSeconds:F1} 秒"
            : "—";
        if (snapshot.State == ProviderConnectionState.Reconnecting &&
            previous != ProviderConnectionState.Reconnecting)
        {
            ReconnectCount++;
        }
    }

    private void OnEventProcessed(object? sender, EventIngestionResult result) =>
        _dispatcher.Invoke(() =>
        {
            if (result.ReceptionSummary is not null)
            {
                FireAndForgetLog(
                    AppLogLevel.Debug,
                    "ProviderMessageReceived",
                    result.ReceptionSummary.ToLogMessage());
            }

            ReceptionStatusText = result.Status switch
            {
                EventIngestionStatus.Accepted when result.AwaitingPreDisplayEdit &&
                    result.Event is not null =>
                    $"作画前編集待ち: {GetEventKindText(result.Event.Kind)}",
                EventIngestionStatus.Accepted when result.Event is not null &&
                    result.Program is not null =>
                    $"表示対象を受信: {GetEventKindText(result.Event.Kind)}",
                EventIngestionStatus.Accepted =>
                    $"受信済み（非表示: {result.DisplaySuppressionReason ?? "理由不明"}）",
                EventIngestionStatus.Duplicate => "受信済み（重複情報）",
                EventIngestionStatus.Ignored => "受信済み（表示対象外情報）",
                EventIngestionStatus.Invalid => "受信データ不正（ログを確認）",
                _ => "受信済み",
            };

            if (result.Status is EventIngestionStatus.Ignored or EventIngestionStatus.Invalid)
            {
                string detail = result.Issues.Count == 0
                    ? "詳細なし"
                    : string.Join(" ", result.Issues.Select(static issue => issue.Message));
                FireAndForgetLog(
                    result.Status == EventIngestionStatus.Invalid
                        ? AppLogLevel.Warning
                        : AppLogLevel.Information,
                    result.Status == EventIngestionStatus.Invalid
                        ? "ProviderMessageInvalid"
                        : "ProviderMessageIgnored",
                    detail);
            }

            if (result.Status == EventIngestionStatus.Accepted && result.Issues.Count > 0)
            {
                string detail = string.Join(
                    " ",
                    result.Issues.Select(static issue => issue.Message));
                FireAndForgetLog(
                    AppLogLevel.Warning,
                    "ProviderMessageValidationIssue",
                    detail);
            }

            if (result.Status == EventIngestionStatus.Accepted &&
                result.AwaitingPreDisplayEdit &&
                result.Event is not null &&
                result.Program is not null)
            {
                QueuePreDisplaySubtitleDraft(result.Event, result.Program);
                return;
            }

            if (result.Status == EventIngestionStatus.Accepted &&
                result.Event?.SourceMode == SourceMode.Production &&
                IsHistoryRehearsalRunning)
            {
                CancelHistoryRehearsal("本番受信により停止しました");
            }

            if (result.Status == EventIngestionStatus.Accepted &&
                result.Event?.SourceMode == SourceMode.Production)
            {
                CancelTestScenario();
            }

            DisplayProgram? reviewProgram = result.Program ?? result.ReviewProgram;
            if (result.Status == EventIngestionStatus.Accepted &&
                result.Event?.SourceMode == SourceMode.Production &&
                reviewProgram is not null)
            {
                string displayResult = result.Program is null
                    ? result.DisplaySuppressionReason ?? "非表示"
                    : "表示済み";
                AddTelegramForReview(result.Event, reviewProgram, displayResult);
                TelegramReviewStatusText = result.Program is null
                    ? $"本番電文を受信しました（{displayResult}、保存中 {ReceivedTelegrams.Count}件）。"
                    : $"本番電文を受信しました（保存中 {ReceivedTelegrams.Count}件）。";
            }

            if (result.Status == EventIngestionStatus.Accepted &&
                result.Event?.SourceMode == SourceMode.Production &&
                (result.Event.IsCancelled ||
                 result.Program is not null && IsEnabled(result.Event)))
            {
                DateTimeOffset nowUtc = _services.Clock.UtcNow;
                _productionReplayCatalog.Update(
                    result.Event,
                    result.Program is not null && IsEnabled(result.Event)
                        ? result.Program
                        : null,
                    _settings.Display.ProductionReplay,
                    nowUtc);
                PriorityCoordinator? activeReplay = _productionReplayCoordinator;
                if (activeReplay is not null &&
                    ReferenceEquals(Volatile.Read(ref _activeCoordinator), activeReplay))
                {
                    Volatile.Write(ref _activeCoordinator, _services.DisplayCoordinator);
                }
                _productionReplayCoordinator = null;
                _productionReplayResumeAfterUtc = nowUtc.AddSeconds(
                    _settings.Display.ProductionReplay.ResumeDelaySeconds);
                _productionReplayNextSwitchUtc = _productionReplayResumeAfterUtc;
            }

            if (result.Status == EventIngestionStatus.Accepted &&
                result.Program is not null &&
                result.Event is not null &&
                EventDisplayFilter.Apply(_settings.Filter, result.Event) is
                    DisasterEvent audibleEvent)
            {
                // Weather events can contain multiple prefectures and levels.
                // Use the same filtered event that is eligible for display so
                // an alert outside the selected prefecture cannot choose audio.
                PlayEventAudio(audibleEvent, _settings.Audio, result.TraceId);
            }

            if (result.Status == EventIngestionStatus.Accepted &&
                result.Program is not null &&
                result.Event is not null &&
                IsEnabled(result.Event))
            {
                if (IsE2ETestMode)
                {
                    E2EAcceptedTelopRevision++;
                }
                _obsSnapshotStore.PublishProgram(
                    result.Event,
                    result.Program,
                    _settings.Display,
                    _services.Clock.UtcNow);
            }

            if (result.Status == EventIngestionStatus.Accepted &&
                result.Snapshot is not null &&
                result.Event is not null &&
                IsEnabled(result.Event) &&
                (!IsTemporaryCoordinator(Volatile.Read(ref _activeCoordinator)) ||
                 result.Event.SourceMode == SourceMode.Production))
            {
                Volatile.Write(ref _activeCoordinator, _services.DisplayCoordinator);
                ApplyDisplaySnapshot(result.Snapshot);
            }
        });

    public void SetAudioFilePath(AudioCueId cue, string filePath) =>
        Settings.SetAudioFilePath(cue, filePath);

    private async Task TestAudioAsync(AudioCueId cue)
    {
        // ICommand normally prevents the button from executing, but keep this
        // guard here as well so a programmatic invocation or a connection-state
        // race can never enqueue test audio during live reception.
        if (IsProductionConnectionActive)
        {
            await WriteLogAsync(
                AppLogLevel.Warning,
                "AudioTestBlockedInProduction",
                $"本番接続中のためOBS音声試聴を送信しませんでした。区分={cue}")
                .ConfigureAwait(false);
            return;
        }

        AppSettings pending = Settings.ToSettings(_settings);
        string filePath = Settings.GetAudioFilePath(cue).Trim();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            await WriteLogAsync(
                AppLogLevel.Warning,
                "AudioFileNotSelected",
                $"試聴する音声ファイルが選択されていません。区分={cue}").ConfigureAwait(false);
            return;
        }

        if (pending.Audio.Muted)
        {
            await WriteLogAsync(
                AppLogLevel.Warning,
                "AudioMuted",
                "ミュート中のため試聴しませんでした。").ConfigureAwait(false);
            return;
        }

        try
        {
            bool queued = await PublishObsAudioAsync(cue, filePath)
                .ConfigureAwait(false);
            if (queued)
            {
                await WriteLogAsync(
                    AppLogLevel.Information,
                    "AudioTestQueued",
                    $"OBSブラウザーソースへ音声試聴を送信しました。実再生結果は後続ログに記録します。区分={cue}")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and
            not StackOverflowException)
        {
            await WriteLogAsync(
                AppLogLevel.Error,
                "ObsAudioTestFailed",
                $"OBSブラウザーソースで音声ファイルを試聴できませんでした。区分={cue}。",
                exception).ConfigureAwait(false);
        }
    }

    private bool IsProductionConnectionActive =>
        IsConnectedOrConnecting &&
        _settings.Provider.Mode == ProviderMode.Production;

    private async Task StopAudioAsync()
    {
        _eewAudioPriority.Reset();
        await CancelPendingWeatherAudioAsync().ConfigureAwait(false);
        _obsSnapshotStore.PublishAudioStop(_services.Clock.UtcNow);
    }

    internal void PlayEventAudio(
        DisasterEvent disasterEvent,
        AudioSettings settings,
        string traceId = "")
    {
        if (_services.AudioPolicy is null)
        {
            return;
        }

        long? eewGeneration = null;
        if (disasterEvent is EewEvent)
        {
            eewGeneration = _eewAudioPriority.BeginEew();
            _ = CancelPendingWeatherAudioAsync();
            _obsSnapshotStore.PublishAudioStop(_services.Clock.UtcNow);
        }

        AudioDecision decision = _services.AudioPolicy.Evaluate(disasterEvent, settings);
        if (!decision.ShouldPlay || decision.Cue is null)
        {
            if (eewGeneration is { } silentEewGeneration)
            {
                _eewAudioPriority.CompleteEew(silentEewGeneration);
            }
            return;
        }

        var pending = new PendingAudioPlayback(decision, traceId);
        if (eewGeneration is { } audibleEewGeneration)
        {
            _ = PlayEventAudioAsync(pending, audibleEewGeneration);
            return;
        }

        if (IsEewAudioPriorityActive())
        {
            LogAudioSuppressedByEew(decision.Cue.Value);
            return;
        }

        if (disasterEvent is WeatherWarningEvent && IsWeatherAudioCue(decision.Cue.Value))
        {
            QueueWeatherAudio(pending,
                settings.EffectiveWeatherCoalescingSeconds);
            return;
        }

        _ = PlayEventAudioAsync(pending);
    }

    private void QueueWeatherAudio(PendingAudioPlayback pending, double coalescingSeconds)
    {
        PendingAudioPlayback? playImmediately = null;
        lock (_weatherAudioGate)
        {
            _pendingWeatherAudio = SelectHigherWeatherAudio(
                _pendingWeatherAudio,
                pending);

            if (coalescingSeconds <= 0)
            {
                playImmediately = _pendingWeatherAudio;
                _pendingWeatherAudio = null;
                _weatherAudioCancellation?.Cancel();
                _weatherAudioCancellation = null;
                _weatherAudioTask = Task.CompletedTask;
            }
            else if (_weatherAudioCancellation is null)
            {
                var cancellation = new CancellationTokenSource();
                _weatherAudioCancellation = cancellation;
                _weatherAudioTask = FlushWeatherAudioAfterDelayAsync(
                    TimeSpan.FromSeconds(coalescingSeconds),
                    cancellation);
            }
        }

        if (playImmediately is not null)
        {
            _ = PlayEventAudioAsync(playImmediately);
        }
    }

    private async Task FlushWeatherAudioAfterDelayAsync(
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            PendingAudioPlayback? pending;
            lock (_weatherAudioGate)
            {
                if (!ReferenceEquals(_weatherAudioCancellation, cancellation))
                {
                    return;
                }

                pending = _pendingWeatherAudio;
                _pendingWeatherAudio = null;
                _weatherAudioCancellation = null;
            }

            if (pending is not null)
            {
                await PlayEventAudioAsync(pending).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task CancelPendingWeatherAudioAsync()
    {
        Task pendingTask;
        lock (_weatherAudioGate)
        {
            _pendingWeatherAudio = null;
            _weatherAudioCancellation?.Cancel();
            pendingTask = _weatherAudioTask;
            _weatherAudioCancellation = null;
            _weatherAudioTask = Task.CompletedTask;
        }

        try
        {
            await pendingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static PendingAudioPlayback SelectHigherWeatherAudio(
        PendingAudioPlayback? current,
        PendingAudioPlayback incoming)
    {
        if (current is null)
        {
            return incoming;
        }

        return GetWeatherAudioPriority(incoming.Decision.Cue) >=
            GetWeatherAudioPriority(current.Decision.Cue)
                ? incoming
                : current;
    }

    private static bool IsWeatherAudioCue(AudioCueId cue) => cue is
        AudioCueId.WeatherSpecialWarning or
        AudioCueId.WeatherDisasterPreventionBulletin or
        AudioCueId.WeatherWarning or
        AudioCueId.WeatherAdvisory;

    private static int GetWeatherAudioPriority(AudioCueId? cue) => cue switch
    {
        AudioCueId.WeatherSpecialWarning => 4,
        AudioCueId.WeatherDisasterPreventionBulletin => 3,
        AudioCueId.WeatherWarning => 2,
        AudioCueId.WeatherAdvisory => 1,
        _ => 0,
    };

    private async Task PlayEventAudioAsync(
        PendingAudioPlayback pending,
        long? eewGeneration = null)
    {
        AudioDecision decision = pending.Decision;
        bool isEew = eewGeneration.HasValue;
        if (isEew && !_eewAudioPriority.IsCurrent(eewGeneration!.Value))
        {
            return;
        }

        if (!isEew && IsEewAudioPriorityActive())
        {
            LogAudioSuppressedByEew(decision.Cue!.Value);
            return;
        }

        try
        {
            bool queued = await PublishObsAudioAsync(
                decision.Cue!.Value,
                decision.FilePath,
                pending.TraceId).ConfigureAwait(false);
            if (queued)
            {
                await WriteLogAsync(
                    AppLogLevel.Information,
                    "AudioQueued",
                    $"OBSブラウザーソースへ設定音声を送信しました。実再生結果は後続ログに記録します。区分={decision.Cue.Value}")
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            await WriteLogAsync(
                AppLogLevel.Error,
                "ObsAudioPlaybackFailed",
                "OBSブラウザーソースへ設定音声を送信できませんでした。表示処理は継続します。",
                exception).ConfigureAwait(false);
        }
        finally
        {
            if (eewGeneration is { } completedEewGeneration)
            {
                _eewAudioPriority.CompleteEew(completedEewGeneration);
            }
        }
    }

    private bool IsEewAudioPriorityActive() => _eewAudioPriority.IsActive(
        _obsSnapshotStore.ReadAudioDiagnostics(),
        _services.Clock.UtcNow,
        _obsServer?.ClientCount > 0);

    private void LogAudioSuppressedByEew(AudioCueId cue) => FireAndForgetLog(
        AppLogLevel.Information,
        "AudioSuppressedByEew",
        $"緊急地震速報の音声を優先しているため、ほかの音声を送信しませんでした。区分={cue}");

    private async Task<bool> PublishObsAudioAsync(
        AudioCueId cue,
        string filePath,
        string traceId = "")
    {
        if (_obsServer?.IsRunning != true)
        {
            await WriteLogAsync(
                AppLogLevel.Warning,
                "ObsAudioUnavailable",
                $"OBS Local Viewが停止中のため音声を送信しませんでした。区分={cue}")
                .ConfigureAwait(false);
            return false;
        }

        if (_obsServer.ClientCount <= 0)
        {
            await WriteLogAsync(
                AppLogLevel.Warning,
                "ObsAudioNoClient",
                $"OBSブラウザーソースが接続されていないため音声を送信しませんでした。区分={cue}")
                .ConfigureAwait(false);
            return false;
        }

        try
        {
            _obsSnapshotStore.PublishAudio(
                cue.ToString(),
                filePath,
                now: _services.Clock.UtcNow);
            await WriteLogAsync(
                AppLogLevel.Information,
                "ObsAudioQueued",
                $"OBSブラウザーソースへ音声再生を通知しました。区分={cue}")
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and
            not StackOverflowException)
        {
            await WriteLogAsync(
                AppLogLevel.Error,
                "ObsAudioFailed",
                $"OBSブラウザーソースへ音声を送信できませんでした。区分={cue}。",
                exception).ConfigureAwait(false);
            return false;
        }
    }

    private bool IsTemporaryCoordinator(IDisplayCoordinator coordinator) =>
        ReferenceEquals(coordinator, _previewCoordinator) ||
        ReferenceEquals(coordinator, Volatile.Read(ref _historyCoordinator)) ||
        ReferenceEquals(coordinator, Volatile.Read(ref _productionReplayCoordinator));

    private sealed record PendingAudioPlayback(AudioDecision Decision, string TraceId);

    private static string GetEventKindText(EventKind kind) => kind switch
    {
        EventKind.Eew => "緊急地震速報",
        EventKind.Quake => "地震情報",
        EventKind.Tsunami => "津波情報",
        EventKind.WeatherWarning => "気象警報・注意報",
        EventKind.Volcano => "火山情報",
        _ => kind.ToString(),
    };

    private static string BuildApplicationVersionText()
    {
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
        int metadataSeparator = version.IndexOf('+', StringComparison.Ordinal);
        if (metadataSeparator >= 0)
        {
            version = version[..metadataSeparator];
        }

        return $"Version {version} — OBS配信用災害字幕スーパー送出ソフトウェア";
    }

    private async Task RunRenderLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            long currentTimestamp = _services.Clock.GetTimestamp();
            TimeSpan runtimeGap = _services.Clock.GetElapsedTime(_lastRuntimeTimestamp);
            _lastRuntimeTimestamp = currentTimestamp;
            bool resynchronizeFromUtc = runtimeGap >= RuntimeGapNoticeThreshold;
            if (resynchronizeFromUtc)
            {
                FireAndForgetLog(
                    AppLogLevel.Warning,
                    "RuntimeGap",
                    $"実行停止または遅延を検出しました（{runtimeGap.TotalSeconds:F1}秒）。表示時刻を再同期します。");
            }

            if (runtimeGap >= ObsRuntimeRecoveryThreshold &&
                _settings.Obs.Enabled &&
                _settings.Obs.RuntimeRecovery &&
                IsConnectedOrConnecting)
            {
                _services.EventSource.RequestReconnect(ReconnectReason.RuntimeGap);
            }

            await _dispatcher.InvokeAsync(
                () => TryAdvanceProductionReplay(_services.Clock.UtcNow),
                cancellationToken).ConfigureAwait(false);

            IDisplayCoordinator coordinator = Volatile.Read(ref _activeCoordinator);
            CoordinatorSnapshot snapshot = coordinator.Evaluate(resynchronizeFromUtc);
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(coordinator, _activeCoordinator))
                {
                    if (ReferenceEquals(coordinator, _previewCoordinator) && snapshot.CurrentProgram is null)
                    {
                        Volatile.Write(ref _activeCoordinator, _services.DisplayCoordinator);
                        ApplyDisplaySnapshot(_services.DisplayCoordinator.Evaluate(resynchronizeFromUtc: true));
                    }
                    else
                    {
                        ApplyDisplaySnapshot(snapshot);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private void TryAdvanceProductionReplay(DateTimeOffset nowUtc)
    {
        IDisplayCoordinator active = Volatile.Read(ref _activeCoordinator);
        bool replayIsActive = ReferenceEquals(active, _productionReplayCoordinator);
        if (!ReferenceEquals(active, _services.DisplayCoordinator) && !replayIsActive)
        {
            return;
        }

        if (nowUtc < _productionReplayResumeAfterUtc ||
            nowUtc < _productionReplayNextSwitchUtc)
        {
            return;
        }

        ProductionReplaySettings replaySettings = _settings.Display.ProductionReplay;
        ProductionReplaySelection? selection = _productionReplayCatalog.SelectNext(
            replaySettings,
            nowUtc);
        if (selection is null)
        {
            if (replayIsActive)
            {
                _productionReplayCoordinator = null;
                Volatile.Write(ref _activeCoordinator, _services.DisplayCoordinator);
            }

            _productionReplayNextSwitchUtc = DateTimeOffset.MaxValue;
            return;
        }

        // The regular coordinator may still retain the same production item,
        // especially a tsunami program whose normal end policy is "until
        // cancellation". Remove that source copy before rotating it. Otherwise
        // it becomes visible again after the configured replay count is exhausted
        // and makes a finite repeat setting behave like permanent display.
        CoordinatorSnapshot sourceSnapshot =
            _services.DisplayCoordinator is PriorityCoordinator mainCoordinator
                ? mainCoordinator.Clear(selection.Event.Kind)
                : _services.DisplayCoordinator.Clear();
        QueueStateSave(sourceSnapshot, lastShutdownWasClean: false);

        var replayProgram = selection.Program with
        {
            ProgramId = $"{selection.Program.ProgramId}:live-repeat:{nowUtc.UtcTicks}",
            StartedAtUtc = nowUtc,
            EndPolicy = EndPolicy.LoopUntilReplaced,
        };
        var coordinator = new PriorityCoordinator(_services.Clock, _settings.Display);
        coordinator.Apply(replayProgram);
        _productionReplayCoordinator = coordinator;
        Volatile.Write(ref _activeCoordinator, coordinator);

        double fullPageCycleSeconds = Math.Max(
            _settings.Display.PageDurationSeconds,
            _settings.Display.PageDurationSeconds * replayProgram.Pages.Count);
        double dwellSeconds = Math.Max(
            replaySettings.RotationIntervalSeconds,
            fullPageCycleSeconds);
        _productionReplayNextSwitchUtc = nowUtc.AddSeconds(dwellSeconds);

        _obsSnapshotStore.PublishProgram(
            selection.Event,
            replayProgram,
            _settings.Display,
            nowUtc);
        if (selection.PlayAudio)
        {
            PlayEventAudio(selection.Event, _settings.Audio);
        }

        FireAndForgetLog(
            AppLogLevel.Debug,
            "ProductionReplayAdvanced",
            $"本番情報の繰り返し表示を更新しました。種別={selection.Event.Kind} 有効件数={selection.ActiveItemCount} 音声={selection.PlayAudio}");
    }

    private void ApplyDisplaySnapshot(CoordinatorSnapshot snapshot)
    {
        _lastCoordinatorSnapshot = snapshot;
        CoordinatorSnapshot displaySnapshot = ApplyManualSubtitleEdit(snapshot);
        RemoveObsoleteSubtitleEdits(snapshot);
        Overlay.Apply(displaySnapshot, _settings.Display);
        _obsSnapshotStore.Publish(displaySnapshot, _settings.Display, _services.Clock.UtcNow);
        EditSubtitleCommand.RaiseCanExecuteChanged();
        EditPendingSubtitleCommand.RaiseCanExecuteChanged();
        if (ReferenceEquals(_activeCoordinator, _services.DisplayCoordinator))
        {
            QueueStateSave(snapshot, lastShutdownWasClean: false);
        }
    }

    private CoordinatorSnapshot ApplyManualSubtitleEdit(CoordinatorSnapshot snapshot)
    {
        if (snapshot.CurrentProgram is null || snapshot.CurrentPage is null)
        {
            return snapshot;
        }

        DisplayProgram sourceProgram = snapshot.CurrentProgram;
        if (!_manualSubtitleEdits.TryGetValue(sourceProgram, out DisplayProgram? editedProgram))
        {
            DisplayProgram? queuedSource = _manualSubtitleEdits.Keys.FirstOrDefault(
                program => IsSameProgramRevision(program, sourceProgram));
            if (queuedSource is null ||
                !_manualSubtitleEdits.Remove(queuedSource, out editedProgram))
            {
                return snapshot;
            }

            editedProgram = editedProgram with
            {
                StartedAtUtc = sourceProgram.StartedAtUtc,
                EndPolicy = sourceProgram.EndPolicy,
            };
            _manualSubtitleEdits[sourceProgram] = editedProgram;
        }

        int pageIndex = snapshot.CurrentPageIndex;
        if (pageIndex < 0 || pageIndex >= editedProgram.Pages.Count)
        {
            return snapshot;
        }

        return snapshot with
        {
            CurrentProgram = editedProgram,
            CurrentPage = editedProgram.Pages[pageIndex],
        };
    }

    private static bool IsSameProgramRevision(DisplayProgram queued, DisplayProgram current) =>
        queued with { StartedAtUtc = current.StartedAtUtc } == current;

    private void RemoveObsoleteSubtitleEdits(CoordinatorSnapshot snapshot)
    {
        if (_manualSubtitleEdits.Count == 0)
        {
            return;
        }

        var activePrograms = new HashSet<DisplayProgram>(ReferenceEqualityComparer.Instance);
        if (snapshot.CurrentProgram is not null)
        {
            activePrograms.Add(snapshot.CurrentProgram);
        }
        if (snapshot.PersistentTsunami is not null)
        {
            activePrograms.Add(snapshot.PersistentTsunami);
        }
        foreach (DisplayProgram program in snapshot.PendingPrograms)
        {
            activePrograms.Add(program);
        }

        foreach (DisplayProgram obsolete in _manualSubtitleEdits.Keys
            .Where(program => !activePrograms.Contains(program))
            .ToArray())
        {
            _manualSubtitleEdits.Remove(obsolete);
        }
    }

    private void QueueStateSave(
        CoordinatorSnapshot snapshot,
        bool lastShutdownWasClean,
        bool force = false)
    {
        if (_services.StateStore is null)
        {
            return;
        }

        string signature = string.Join(
            '|',
            snapshot.CurrentProgram?.ProgramId ?? string.Empty,
            snapshot.ProgramStartedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            snapshot.PersistentTsunami?.ProgramId ?? string.Empty,
            string.Join(',', snapshot.PendingPrograms.Select(static item => item.ProgramId)),
            lastShutdownWasClean);
        if (!force && string.Equals(signature, _lastStateSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastStateSignature = signature;
        DisplayStateDocument state = DisplayStateDocument.FromSnapshot(
            snapshot,
            _services.Clock.UtcNow,
            lastShutdownWasClean,
            _lastConnectionSnapshot.LastReceivedAt,
            Settings.ProviderMode.ToString(),
            _services.VersionCache?.GetSnapshot());
        lock (_stateQueueGate)
        {
            _pendingState = state;
            if (!_stateSaveRunning)
            {
                _stateSaveRunning = true;
                _stateSaveTask = DrainStateSavesAsync();
            }
        }
    }

    private async Task DrainStateSavesAsync()
    {
        while (true)
        {
            DisplayStateDocument? state;
            lock (_stateQueueGate)
            {
                state = _pendingState;
                _pendingState = null;
                if (state is null)
                {
                    _stateSaveRunning = false;
                }
            }

            if (state is null)
            {
                return;
            }

            try
            {
                await _services.StateStore!.SaveAsync(state).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                await WriteLogAsync(
                    AppLogLevel.Error,
                    "StateSaveFailed",
                    "表示状態をstate.jsonへ保存できませんでした。",
                    exception).ConfigureAwait(false);
            }
        }
    }

    private Task GetStateSaveTask()
    {
        lock (_stateQueueGate)
        {
            return _stateSaveTask;
        }
    }

    private async Task ConfigureObsServerAsync(ObsSettings settings)
    {
        if (_obsServer is null)
        {
            return;
        }

        await _obsLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _obsServer.UpdateSnapshotInterval(settings.SnapshotIntervalMilliseconds);
            bool localViewEnabled = settings.Enabled;
            bool correctRunningPort = _obsServer.IsRunning &&
                (settings.Port == 0 || settings.Port == _obsServer.Port);
            if (!localViewEnabled)
            {
                if (_obsServer.IsRunning)
                {
                    await _obsServer.StopAsync().ConfigureAwait(false);
                }

                await UpdateObsStatusAsync("無効").ConfigureAwait(false);
                ConfigureObsBrowserSourceSynchronizer(
                    settings with { BrowserSourceSyncEnabled = false });
                return;
            }

            if (correctRunningPort)
            {
                await UpdateObsStatusAsync($"稼働中 127.0.0.1:{_obsServer.Port}")
                    .ConfigureAwait(false);
                ConfigureObsBrowserSourceSynchronizer(settings);
                return;
            }

            if (_obsServer.IsRunning)
            {
                await _obsServer.StopAsync().ConfigureAwait(false);
            }

            await _obsServer.StartAsync(settings.Port).ConfigureAwait(false);
            await UpdateObsStatusAsync($"稼働中 127.0.0.1:{_obsServer.Port}")
                .ConfigureAwait(false);
            ConfigureObsBrowserSourceSynchronizer(settings);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            await UpdateObsStatusAsync("起動失敗").ConfigureAwait(false);
            ConfigureObsBrowserSourceSynchronizer(
                settings with { BrowserSourceSyncEnabled = false });
            await WriteLogAsync(
                AppLogLevel.Error,
                "ObsLocalViewFailed",
                "OBS Local Viewの起動または再設定に失敗しました。",
                exception).ConfigureAwait(false);
        }
        finally
        {
            _obsLifecycle.Release();
        }
    }

    private Task UpdateObsStatusAsync(string status) => _dispatcher.InvokeAsync(() =>
        {
            ObsStatusText = status;
            OnPropertyChanged(nameof(ObsUrlText));
            OnPropertyChanged(nameof(EewObsUrlText));
            OnPropertyChanged(nameof(TsunamiObsUrlText));
            OnPropertyChanged(nameof(WeatherObsUrlText));
            CopyObsUrlCommand.RaiseCanExecuteChanged();
            SyncObsBrowserSourcesCommand.RaiseCanExecuteChanged();
        });

    private void ConfigureObsBrowserSourceSynchronizer(ObsSettings settings)
    {
        if (_obsBrowserSourceSynchronizer is null || _obsServer is null)
        {
            return;
        }

        _obsBrowserSourceSynchronizer.Configure(settings, new ObsBrowserSourceUrls(
            _obsServer.OverlayUrl,
            _obsServer.EewUrl,
            _obsServer.TsunamiUrl,
            _obsServer.WeatherUrl));
    }

    private void RequestObsBrowserSourceSynchronization()
    {
        AppSettings requested = Settings.ToSettings(_settings);
        ConfigureObsBrowserSourceSynchronizer(requested.Obs);
        _obsBrowserSourceSynchronizer?.RequestSynchronization();
        ObsBrowserSyncStatusText = "同期要求済み";
    }

    private void OnObsBrowserSyncStatusChanged(string status) =>
        _dispatcher.Invoke(() => ObsBrowserSyncStatusText = status);

    private void OnObsClientCountChanged(int count) => _dispatcher.Invoke(() => ObsClientCount = count);

    private bool IsEnabled(DisasterEvent disasterEvent) =>
        EventDisplayFilter.IsEnabled(_settings.Filter, disasterEvent);

    private void OnLogEntryAdded(object? sender, AppLogEntry entry) =>
        _dispatcher.Invoke(() => AddLogToCollections(entry));

    private void AddLogToCollections(AppLogEntry entry)
    {
        var item = new UiLogEntryViewModel(entry);
        Logs.Add(item);
        while (Logs.Count > UiLogBuffer.MaximumCapacity)
        {
            Logs.RemoveAt(0);
        }

        if (entry.Level >= MinimumLogLevel)
        {
            VisibleLogs.Add(item);
            while (VisibleLogs.Count > UiLogBuffer.MaximumCapacity)
            {
                VisibleLogs.RemoveAt(0);
            }
        }
    }

    private void RebuildVisibleLogs()
    {
        VisibleLogs.Clear();
        foreach (UiLogEntryViewModel entry in Logs.Where(entry => entry.Level >= MinimumLogLevel))
        {
            VisibleLogs.Add(entry);
        }
    }

    private async ValueTask WriteLogAsync(
        AppLogLevel level,
        string eventName,
        string message,
        Exception? exception = null)
    {
        try
        {
            await _services.LogWriter.WriteAsync(
                new AppLogEntry(_services.Clock.UtcNow, level, eventName, message, exception)).ConfigureAwait(false);
            _services.OperationalAlerts?.Recover("log-write", "ログ保存復旧",
                "ログ保存が復旧しました。", _services.Clock.UtcNow);
        }
        catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _services.OperationalAlerts?.Raise(new OperationalAlert("log-write", OperationalAlertSeverity.Error,
                "ログ保存失敗", writeException.Message, _services.Clock.UtcNow));
        }
    }

    private void FireAndForgetLog(
        AppLogLevel level,
        string eventName,
        string message,
        Exception? exception = null)
    {
        _ = WriteLogAsync(level, eventName, message, exception).AsTask();
    }

    private static string ProviderLabel(ReceptionProvider provider) => provider switch
    {
        ReceptionProvider.Disabled => "受信しない",
        ReceptionProvider.P2pQuake => "P2P",
        ReceptionProvider.Dmdata => "DMDATA.JP",
        ReceptionProvider.Axis => "AXIS",
        _ => provider.ToString(),
    };

}
internal sealed record PreDisplaySubtitleDraft(
    DisasterEvent Event,
    DisplayProgram Program);

public sealed record HistoryReplayItemViewModel(
    string DisplayText,
    DisasterEvent DisasterEvent,
    DisplayProgram Program);

public sealed record UiLogEntryViewModel(
    DateTimeOffset Timestamp,
    AppLogLevel Level,
    string EventName,
    string Message)
{
    public UiLogEntryViewModel(AppLogEntry entry)
        : this(entry.Timestamp, entry.Level, entry.EventName, entry.Message)
    {
    }

    public string DisplayText => $"{Timestamp.ToLocalTime():HH:mm:ss} [{Level}] {Message}";
}
