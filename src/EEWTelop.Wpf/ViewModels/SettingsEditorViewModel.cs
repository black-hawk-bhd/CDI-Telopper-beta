using EEWTelop.Application.Audio;
using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.P2P.Configuration;
#if QTELOPPER_DMDATA_PROVIDER
using EEWTelop.Infrastructure.Dmdata.Security;
#endif
#if QTELOPPER_AXIS_PROVIDER
using EEWTelop.Infrastructure.Axis.Configuration;
using EEWTelop.Infrastructure.Axis.Security;
#endif
using EEWTelop.Wpf.Mvvm;
using EEWTelop.Wpf.Obs;
using System.Collections.ObjectModel;

namespace EEWTelop.Wpf.ViewModels;

public sealed class SettingsEditorViewModel : ObservableObject
{
    private ReceptionProvider _receptionProvider;
    private ReceptionProvider _eewProvider;
    private ReceptionProvider _quakeProvider;
    private ReceptionProvider _tsunamiProvider;
    private ReceptionProvider _weatherProvider;
    private ReceptionProvider _volcanoProvider;
    private ReceptionProvider _nankaiTroughProvider;
    private ProviderMode _providerMode;
    private string _webSocketUrl;
    private string _restBaseUrl;
    private string _dmdataApiBaseUrl;
    private string _dmdataCredential;
    private DmdataAuthenticationMode _dmdataAuthenticationMode;
    private DmdataEewContractType _dmdataEewContractType;
    private bool _dmdataIncludeTestTelegrams;
    private bool _dmdataReceiveEewWarnings;
    private bool _dmdataReceiveEarthquakeTelegrams;
    private bool _dmdataReceiveWeatherWarnings;
    private bool _dmdataReceiveVolcanoTelegrams;
    private bool _dmdataUseLegacyWeatherWarningTelegrams;
    private string _axisApiBaseUrl;
    private string _axisAccessToken;
    private string _axisChannel;
    private bool _filterEew;
    private bool _filterQuake;
    private bool _filterTsunami;
    private bool _filterWeatherWarning;
    private bool _filterVolcano;
    private string[] _weatherPrefectureCodes;
    private bool _filterWeatherSpecialWarnings;
    private bool _filterWeatherWarnings;
    private bool _filterWeatherAdvisories;
    private bool _filterWeatherTornadoAdvisories;
    private bool _filterWeatherRecordShortRain;
    private bool _filterWeatherDisasterPreventionBulletins;
    private bool _hideQuakeBelowIntensity3;
    private double _pageDurationSeconds;
    private bool _showPageIndicator;
    private bool _showTsunamiForecast;
    private double _outputScale;
    private double _outputOffsetX;
    private double _outputOffsetY;
    private double _outputCropLeft;
    private double _outputCropTop;
    private double _outputCropRight;
    private double _outputCropBottom;
    private double _letterSpacingEm;
    private double _lineSpacing;
    private double _fontScale;
    private int _autoHideSeconds;
    private int _eewAutoHideSeconds;
    private int _quakeAutoHideSeconds;
    private int _tsunamiAutoHideSeconds;
    private int _weatherWarningAutoHideSeconds;
    private int _productionReplayRotationIntervalSeconds;
    private int _productionReplayResumeDelaySeconds;
    private bool _productionReplayEewEnabled;
    private int _productionReplayEewRepeatCount;
    private bool _productionReplayEewAudioEachCycle;
    private bool _productionReplayQuakeEnabled;
    private int _productionReplayQuakeRepeatCount;
    private bool _productionReplayQuakeAudioEachCycle;
    private bool _productionReplayTsunamiEnabled;
    private int _productionReplayTsunamiRepeatCount;
    private bool _productionReplayTsunamiAudioEachCycle;
    private bool _productionReplayWeatherEnabled;
    private int _productionReplayWeatherRepeatCount;
    private bool _productionReplayWeatherAudioEachCycle;
    private bool _productionReplayVolcanoEnabled;
    private int _productionReplayVolcanoRepeatCount;
    private bool _productionReplayVolcanoAudioEachCycle;
    private BackgroundMode _backgroundMode;
    private int _width;
    private int _height;
    private bool _obsEnabled;
    private int _obsPort;
    private bool _obsRuntimeRecovery;
    private int _obsSnapshotIntervalMilliseconds;
    private bool _obsBrowserSourceSyncEnabled;
    private string _obsWebSocketUrl;
    private string _obsWebSocketPassword;
    private string _obsTargetSceneName;
    private ObsAudioMonitoringMode _obsAudioMonitoringMode;
    private bool _audioMuted;
    private bool _audioInRehearsal;
    private bool _quakeAudioEnabled;
    private bool _tsunamiAdvisoryAudioEnabled;
    private bool _tsunamiWarningAudioEnabled;
    private bool _tsunamiMajorWarningAudioEnabled;
    private JmaScale _minimumQuakeScale;
    private bool _eewInitialAudioEnabled;
    private bool _eewContinuationAudioEnabled;
    private bool _eewCancellationAudioEnabled;
    private bool _weatherSpecialWarningAudioEnabled;
    private bool _weatherWarningAudioEnabled;
    private bool _weatherAdvisoryAudioEnabled;
    private double _weatherAudioCoalescingSeconds;
    private string _quakeAudioFilePath;
    private string _tsunamiAdvisoryAudioFilePath;
    private string _tsunamiWarningAudioFilePath;
    private string _tsunamiMajorWarningAudioFilePath;
    private string _eewInitialAudioFilePath;
    private string _eewContinuationAudioFilePath;
    private string _eewCancellationAudioFilePath;
    private string _weatherSpecialWarningAudioFilePath;
    private string _weatherWarningAudioFilePath;
    private string _weatherAdvisoryAudioFilePath;
    private HistoryApi _historyApi;
    private int _historyLimit;
    private int _historyIntervalSeconds;
    private DateTime _niiHistoryDate;
    private NiiHistoryContent _niiHistoryContent;
    private string _niiHistoryReportUrl;
    private string _localHistoryXmlFilePath;
    private bool _historyRepeat;
    private bool _enrichQuakeById;
    private bool _confirmTestInProduction;
    private readonly int _uiLogMaxEntries;
    private bool _saveRawProviderMessages;
    private int _rawMessageRetentionDays;
    private int _rawMessageMaximumTotalMegabytes;
    private Dictionary<string, string> _subtitlePhraseOverrides;

    public SettingsEditorViewModel(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _receptionProvider = NormalizeReceptionProvider(
            settings.Provider.ReceptionProvider);
        ProviderRoutingSettings initialRouting = settings.Provider.Routing;
        if (settings.Provider.ReceptionProvider != initialRouting.GetCompatibilityProvider())
        {
            initialRouting = ProviderRoutingSettings.FromLegacy(
                settings.Provider.ReceptionProvider);
        }
        _eewProvider = NormalizeReceptionProvider(initialRouting.Eew);
        _quakeProvider = NormalizeReceptionProvider(initialRouting.Quake);
        _tsunamiProvider = NormalizeReceptionProvider(initialRouting.Tsunami);
        _weatherProvider = NormalizeReceptionProvider(initialRouting.Weather);
        _volcanoProvider = NormalizeReceptionProvider(initialRouting.Volcano);
        _nankaiTroughProvider = NormalizeReceptionProvider(
            initialRouting.NankaiTrough);
        _providerMode = settings.Provider.Mode == ProviderMode.Sandbox
            ? ProviderMode.Sandbox
            : ProviderMode.Production;
        ProviderOptions provider = _providerMode == ProviderMode.Sandbox
            ? ProviderOptions.Sandbox
            : ProviderOptions.Production;
        _webSocketUrl = provider.WebSocketUri.AbsoluteUri;
        _restBaseUrl = provider.RestBaseUri.AbsoluteUri.TrimEnd('/');
        _dmdataApiBaseUrl = settings.Provider.DmdataApiBaseUrl;
        // OAuth remains readable for legacy migration, but it is no longer an
        // operator-selectable connection method.  Do not reinterpret an old
        // OAuth access token as an API key.
        _dmdataCredential = settings.Provider.DmdataAuthenticationMode ==
            EEWTelop.Application.Configuration.DmdataAuthenticationMode.ApiKey
                ? ResolveDmdataCredential(settings.Provider)
                : string.Empty;
        _dmdataAuthenticationMode = EEWTelop.Application.Configuration.DmdataAuthenticationMode.ApiKey;
        _dmdataEewContractType = settings.Provider.DmdataEewContractType;
        _dmdataIncludeTestTelegrams = settings.Provider.DmdataIncludeTestTelegrams;
        _dmdataReceiveEewWarnings = settings.Provider.DmdataReceiveEewWarnings;
        _dmdataReceiveEarthquakeTelegrams = settings.Provider.DmdataReceiveEarthquakeTelegrams;
        _dmdataReceiveWeatherWarnings = BuildFeatures.ExtendedFeaturesEnabled &&
            settings.Provider.DmdataReceiveWeatherWarnings;
        _dmdataReceiveVolcanoTelegrams = BuildFeatures.ExtendedFeaturesEnabled &&
            settings.Provider.DmdataReceiveVolcanoTelegrams;
        _dmdataUseLegacyWeatherWarningTelegrams =
            settings.Provider.DmdataUseLegacyWeatherWarningTelegrams;
        _axisApiBaseUrl = settings.Provider.AxisApiBaseUrl;
        _axisAccessToken = UnprotectAxisToken(settings.Provider.AxisProtectedAccessToken);
        _axisChannel = GetAxisChannel(initialRouting);
        _filterEew = settings.Filter.Eew;
        _filterQuake = settings.Filter.Quake;
        _filterTsunami = settings.Filter.Tsunami;
        _filterWeatherWarning = settings.Filter.WeatherWarning;
        _filterVolcano = settings.Filter.Volcano;
        _weatherPrefectureCodes = WeatherPrefectureCatalog.ResolveCodes(settings.Filter);
        _filterWeatherSpecialWarnings = settings.Filter.WeatherSpecialWarnings;
        _filterWeatherWarnings = settings.Filter.WeatherWarnings;
        _filterWeatherAdvisories = settings.Filter.WeatherAdvisories;
        _filterWeatherTornadoAdvisories = settings.Filter.WeatherTornadoAdvisories;
        _filterWeatherRecordShortRain = settings.Filter.WeatherRecordShortRain;
        _filterWeatherDisasterPreventionBulletins =
            settings.Filter.WeatherDisasterPreventionBulletins;
        _hideQuakeBelowIntensity3 = settings.Filter.HideQuakeBelowIntensity3;
        _pageDurationSeconds = settings.Display.PageDurationSeconds;
        _showPageIndicator = settings.Display.ShowPageIndicator;
        _showTsunamiForecast = settings.Display.ShowTsunamiForecast;
        _outputScale = OutputTransformSettings.Default.Scale;
        _outputOffsetX = OutputTransformSettings.Default.OffsetX;
        _outputOffsetY = OutputTransformSettings.Default.OffsetY;
        _outputCropLeft = OutputTransformSettings.Default.CropLeft;
        _outputCropTop = OutputTransformSettings.Default.CropTop;
        _outputCropRight = OutputTransformSettings.Default.CropRight;
        _outputCropBottom = OutputTransformSettings.Default.CropBottom;
        _letterSpacingEm = 0;
        _lineSpacing = 1;
        _fontScale = 1;
        _autoHideSeconds = settings.Display.AutoHideSeconds;
        _eewAutoHideSeconds = settings.Display.EffectiveEewAutoHideSeconds;
        _quakeAutoHideSeconds = settings.Display.EffectiveQuakeAutoHideSeconds;
        _tsunamiAutoHideSeconds = settings.Display.EffectiveTsunamiAutoHideSeconds;
        _weatherWarningAutoHideSeconds =
            settings.Display.EffectiveWeatherWarningAutoHideSeconds;
        _subtitlePhraseOverrides = new Dictionary<string, string>(
            settings.Display.SubtitlePhraseOverrides ?? [],
            StringComparer.Ordinal);
        ProductionReplaySettings productionReplay = settings.Display.ProductionReplay;
        _productionReplayRotationIntervalSeconds = productionReplay.RotationIntervalSeconds;
        _productionReplayResumeDelaySeconds = productionReplay.ResumeDelaySeconds;
        _productionReplayEewEnabled = productionReplay.Eew.Enabled;
        _productionReplayEewRepeatCount = productionReplay.Eew.RepeatCount;
        _productionReplayEewAudioEachCycle = productionReplay.Eew.AudioOnEachCycle;
        _productionReplayQuakeEnabled = productionReplay.Quake.Enabled;
        _productionReplayQuakeRepeatCount = productionReplay.Quake.RepeatCount;
        _productionReplayQuakeAudioEachCycle = productionReplay.Quake.AudioOnEachCycle;
        _productionReplayTsunamiEnabled = productionReplay.Tsunami.Enabled;
        _productionReplayTsunamiRepeatCount = productionReplay.Tsunami.RepeatCount;
        _productionReplayTsunamiAudioEachCycle = productionReplay.Tsunami.AudioOnEachCycle;
        _productionReplayWeatherEnabled = productionReplay.WeatherWarning.Enabled;
        _productionReplayWeatherRepeatCount = productionReplay.WeatherWarning.RepeatCount;
        _productionReplayWeatherAudioEachCycle = productionReplay.WeatherWarning.AudioOnEachCycle;
        _productionReplayVolcanoEnabled = productionReplay.Volcano.Enabled;
        _productionReplayVolcanoRepeatCount = productionReplay.Volcano.RepeatCount;
        _productionReplayVolcanoAudioEachCycle = productionReplay.Volcano.AudioOnEachCycle;
        _backgroundMode = BackgroundMode.Transparent;
        _width = settings.Display.Width;
        _height = settings.Display.Height;
        _obsEnabled = settings.Obs.Enabled;
        _obsPort = settings.Obs.Port;
        _obsRuntimeRecovery = settings.Obs.RuntimeRecovery;
        _obsSnapshotIntervalMilliseconds = settings.Obs.SnapshotIntervalMilliseconds;
        _obsBrowserSourceSyncEnabled = settings.Obs.BrowserSourceSyncEnabled;
        _obsWebSocketUrl = settings.Obs.WebSocketUrl;
        _obsWebSocketPassword = ObsCredentialProtector.Unprotect(
            settings.Obs.WebSocketProtectedPassword);
        _obsTargetSceneName = settings.Obs.TargetSceneName;
        _obsAudioMonitoringMode = NormalizeObsAudioMonitoringMode(
            settings.Obs.AudioMonitoringMode);
        bool legacyDisabledAudio = !settings.Audio.FileAudioConfigured;
        _audioMuted = legacyDisabledAudio ? false : settings.Audio.Muted;
        _audioInRehearsal = legacyDisabledAudio || settings.Audio.TestUsesProductionSound;
        _quakeAudioEnabled = settings.Audio.QuakeEnabled;
        _minimumQuakeScale = NormalizeMinimumQuakeScale(settings.Audio.MinimumQuakeScale);
        bool splitTsunamiAudioConfigured = settings.Audio.TsunamiAdvisoryEnabled ||
            settings.Audio.TsunamiWarningEnabled ||
            settings.Audio.TsunamiMajorWarningEnabled ||
            !string.IsNullOrWhiteSpace(settings.Audio.TsunamiAdvisoryFilePath) ||
            !string.IsNullOrWhiteSpace(settings.Audio.TsunamiWarningFilePath) ||
            !string.IsNullOrWhiteSpace(settings.Audio.TsunamiMajorWarningFilePath);
        _tsunamiAdvisoryAudioEnabled = splitTsunamiAudioConfigured
            ? settings.Audio.TsunamiAdvisoryEnabled
            : settings.Audio.TsunamiEnabled &&
              settings.Audio.MinimumTsunamiGrade == TsunamiGrade.Watch;
        _tsunamiWarningAudioEnabled = splitTsunamiAudioConfigured
            ? settings.Audio.TsunamiWarningEnabled
            : settings.Audio.TsunamiEnabled &&
              settings.Audio.MinimumTsunamiGrade is TsunamiGrade.Watch or TsunamiGrade.Warning;
        _tsunamiMajorWarningAudioEnabled = splitTsunamiAudioConfigured
            ? settings.Audio.TsunamiMajorWarningEnabled
            : settings.Audio.TsunamiEnabled;
        _eewInitialAudioEnabled = settings.Audio.EewInitialEnabled;
        _eewContinuationAudioEnabled = settings.Audio.EewContinuationEnabled;
        _eewCancellationAudioEnabled = settings.Audio.EewCancellationEnabled;
        _weatherSpecialWarningAudioEnabled =
            settings.Audio.WeatherSpecialWarningEnabled;
        _weatherWarningAudioEnabled = settings.Audio.WeatherWarningEnabled;
        _weatherAdvisoryAudioEnabled = settings.Audio.WeatherAdvisoryEnabled;
        _weatherAudioCoalescingSeconds =
            settings.Audio.EffectiveWeatherCoalescingSeconds;
        _quakeAudioFilePath = settings.Audio.QuakeFilePath ?? string.Empty;
        _tsunamiAdvisoryAudioFilePath = splitTsunamiAudioConfigured
            ? settings.Audio.TsunamiAdvisoryFilePath ?? string.Empty
            : settings.Audio.TsunamiFilePath ?? string.Empty;
        _tsunamiWarningAudioFilePath = splitTsunamiAudioConfigured
            ? settings.Audio.TsunamiWarningFilePath ?? string.Empty
            : settings.Audio.TsunamiFilePath ?? string.Empty;
        _tsunamiMajorWarningAudioFilePath = splitTsunamiAudioConfigured
            ? settings.Audio.TsunamiMajorWarningFilePath ?? string.Empty
            : settings.Audio.TsunamiFilePath ?? string.Empty;
        _eewInitialAudioFilePath = settings.Audio.EewInitialFilePath ?? string.Empty;
        _eewContinuationAudioFilePath = settings.Audio.EewContinuationFilePath ?? string.Empty;
        _eewCancellationAudioFilePath = settings.Audio.EewCancellationFilePath ?? string.Empty;
        _weatherSpecialWarningAudioFilePath =
            settings.Audio.WeatherSpecialWarningFilePath ?? string.Empty;
        _weatherWarningAudioFilePath =
            settings.Audio.WeatherWarningFilePath ?? string.Empty;
        _weatherAdvisoryAudioFilePath =
            settings.Audio.WeatherAdvisoryFilePath ?? string.Empty;
        QuakeAudioCues = CreateQuakeAudioCues(settings.Audio);
        TsunamiAudioCues = CreateTsunamiAudioCues(settings.Audio);
        _historyApi = settings.History.Api;
        _historyLimit = settings.History.Limit;
        _historyIntervalSeconds = settings.History.IntervalSeconds;
        _niiHistoryDate = settings.History.NiiDate.ToDateTime(TimeOnly.MinValue);
        _niiHistoryContent = settings.History.NiiContent;
        _niiHistoryReportUrl = settings.History.NiiReportUrl ?? string.Empty;
        _localHistoryXmlFilePath = settings.History.LocalXmlFilePath ?? string.Empty;
        _historyRepeat = settings.History.Repeat;
        _enrichQuakeById = settings.Compatibility.EnrichQuakeById;
        _confirmTestInProduction = settings.Safety.ConfirmTestInProduction;
        _uiLogMaxEntries = settings.Log.UiMaxEntries;
        _saveRawProviderMessages = settings.Log.SaveRawProviderMessages;
        _rawMessageRetentionDays = settings.Log.RawMessageRetentionDays;
        _rawMessageMaximumTotalMegabytes = settings.Log.RawMessageMaximumTotalMegabytes;
    }

    public IReadOnlyList<ProviderMode> ProviderModes { get; } =
        [ProviderMode.Production, ProviderMode.Sandbox];

    public IReadOnlyList<ReceptionProvider> ReceptionProviders { get; } =
        Enum.GetValues<ReceptionProvider>()
            .Where(static provider => provider switch
            {
                ReceptionProvider.Disabled => true,
                ReceptionProvider.P2pQuake => true,
                ReceptionProvider.Dmdata => BuildFeatures.DmdataProviderEnabled,
                ReceptionProvider.Axis => BuildFeatures.AxisProviderEnabled,
                _ => false,
            })
            .ToArray();

    public IReadOnlyList<ReceptionProviderOption> EarthquakeProviderOptions { get; } =
        CreateReceptionProviderOptions(includeP2p: true);

    public IReadOnlyList<ReceptionProviderOption> CommercialProviderOptions { get; } =
        CreateReceptionProviderOptions(includeP2p: false);

    public bool HasCommercialProviderOptions => CommercialProviderOptions.Any(
        static option => option.Value != ReceptionProvider.Disabled);

    public IReadOnlyList<DmdataAuthenticationMode> DmdataAuthenticationModes { get; } =
        [EEWTelop.Application.Configuration.DmdataAuthenticationMode.ApiKey];

    public IReadOnlyList<DmdataEewContractTypeOption> DmdataEewContractTypes { get; } =
    [
        new(DmdataEewContractType.Warning, "警報契約（eew.warning / VXSE43）"),
        new(DmdataEewContractType.Forecast, "予報契約（eew.forecast / VXSE45）"),
    ];

    public IReadOnlyList<BackgroundMode> BackgroundModes { get; } =
        [BackgroundMode.Transparent];

    public IReadOnlyList<ObsAudioMonitoringModeOption> ObsAudioMonitoringModes { get; } =
    [
        new(ObsAudioMonitoringMode.Off, "OBS出力のみ（固定）"),
    ];

    public IReadOnlyList<QuakeAudioThresholdOption> QuakeAudioThresholds { get; } =
    [
        new(JmaScale.Three, "震度3以上"),
        new(JmaScale.Four, "震度4以上"),
        new(JmaScale.FiveLower, "震度5弱以上"),
        new(JmaScale.FiveUpper, "震度5強以上"),
        new(JmaScale.SixLower, "震度6弱以上"),
        new(JmaScale.SixUpper, "震度6強以上"),
        new(JmaScale.Seven, "震度7"),
    ];

    public ObservableCollection<AudioCueOptionViewModel> QuakeAudioCues { get; }

    public ObservableCollection<AudioCueOptionViewModel> TsunamiAudioCues { get; }

    public IReadOnlyList<HistoryApiOption> HistoryApis { get; } =
        BuildFeatures.ExtendedFeaturesEnabled
            ?
            [
                new(HistoryApi.JmaQuake, "P2P地震情報（jma/quake）"),
                new(HistoryApi.History, "P2P履歴（地震・津波・EEW）"),
                new(HistoryApi.NiiJmaXml, "気象庁防災情報XML DB（NII）"),
                new(HistoryApi.LocalJmaXml, "外部JMA XMLファイル"),
            ]
            :
            [
                new(HistoryApi.JmaQuake, "P2P地震情報（jma/quake）"),
                new(HistoryApi.History, "P2P履歴（地震・津波・EEW）"),
            ];

    public IReadOnlyList<NiiHistoryContentOption> NiiHistoryContents { get; } =
    [
        new(NiiHistoryContent.QuakeAndTsunami, "地震情報・津波情報"),
        new(NiiHistoryContent.QuakeOnly, "地震情報のみ"),
        new(NiiHistoryContent.TsunamiOnly, "津波情報のみ"),
        new(NiiHistoryContent.WeatherWarningsOnly, "気象注警報のみ（VPWW55～61）"),
        new(NiiHistoryContent.WeatherRain, "VPWW55：大雨（レベル5～2）"),
        new(NiiHistoryContent.WeatherLandslide, "VPWW56：土砂災害（レベル5～2）"),
        new(NiiHistoryContent.WeatherStormSurge, "VPWW57：高潮（レベル5～2）"),
        new(NiiHistoryContent.WeatherStorm, "VPWW58：暴風・暴風雪"),
        new(NiiHistoryContent.WeatherWave, "VPWW59：波浪"),
        new(NiiHistoryContent.WeatherHeavySnow, "VPWW60：大雪"),
        new(NiiHistoryContent.WeatherOtherAdvisories, "VPWW61：その他の注意報"),
        new(NiiHistoryContent.AllSupported, "地震・津波・気象注警報"),
    ];

    public int UiLogMaxEntries => _uiLogMaxEntries;

    public bool SaveRawProviderMessages
    {
        get => _saveRawProviderMessages;
        set => SetProperty(ref _saveRawProviderMessages, value);
    }

    public int RawMessageRetentionDays
    {
        get => _rawMessageRetentionDays;
        set => SetProperty(ref _rawMessageRetentionDays, value);
    }

    public int RawMessageMaximumTotalMegabytes
    {
        get => _rawMessageMaximumTotalMegabytes;
        set => SetProperty(ref _rawMessageMaximumTotalMegabytes, value);
    }

    public bool IsP2pProvider => CurrentRouting.Uses(ReceptionProvider.P2pQuake);

    public bool IsDmdataProvider => CurrentRouting.Uses(ReceptionProvider.Dmdata);

    public bool IsAxisProvider => CurrentRouting.Uses(ReceptionProvider.Axis);

    public bool IsCustomProvider => IsP2pProvider && ProviderMode == ProviderMode.Custom;

    public ReceptionProvider ReceptionProvider
    {
        get => CurrentRouting.GetCompatibilityProvider();
        set
        {
            ReceptionProvider normalized = NormalizeReceptionProvider(value);
            SetProperty(ref _receptionProvider, normalized);
            ApplyRouting(ProviderRoutingSettings.FromLegacy(normalized));
        }
    }

    public ReceptionProvider EewProvider
    {
        get => _eewProvider;
        set => SetRouteProvider(ref _eewProvider, value, nameof(EewProvider));
    }

    public ReceptionProvider QuakeProvider
    {
        get => _quakeProvider;
        set => SetRouteProvider(ref _quakeProvider, value, nameof(QuakeProvider));
    }

    public ReceptionProvider TsunamiProvider
    {
        get => _tsunamiProvider;
        set => SetRouteProvider(ref _tsunamiProvider, value, nameof(TsunamiProvider));
    }

    public ReceptionProvider WeatherProvider
    {
        get => _weatherProvider;
        set => SetRouteProvider(ref _weatherProvider, value, nameof(WeatherProvider));
    }

    public ReceptionProvider VolcanoProvider
    {
        get => _volcanoProvider;
        set => SetRouteProvider(ref _volcanoProvider, value, nameof(VolcanoProvider));
    }

    public ReceptionProvider NankaiTroughProvider
    {
        get => _nankaiTroughProvider;
        set => SetRouteProvider(
            ref _nankaiTroughProvider,
            value,
            nameof(NankaiTroughProvider));
    }

    public ProviderMode ProviderMode
    {
        get => _providerMode;
        set
        {
            ProviderMode normalized = value == ProviderMode.Sandbox
                ? ProviderMode.Sandbox
                : ProviderMode.Production;
            if (SetProperty(ref _providerMode, normalized))
            {
                OnPropertyChanged(nameof(IsCustomProvider));
                ProviderOptions preset = normalized == ProviderMode.Production
                    ? ProviderOptions.Production
                    : ProviderOptions.Sandbox;
                WebSocketUrl = preset.WebSocketUri.AbsoluteUri;
                RestBaseUrl = preset.RestBaseUri.AbsoluteUri.TrimEnd('/');
                if (normalized == ProviderMode.Production)
                {
                    ConfirmTestInProduction = true;
                }
            }
        }
    }

    public string WebSocketUrl { get => _webSocketUrl; set => SetProperty(ref _webSocketUrl, value); }
    public string RestBaseUrl { get => _restBaseUrl; set => SetProperty(ref _restBaseUrl, value); }
    public string DmdataApiBaseUrl { get => _dmdataApiBaseUrl; set => SetProperty(ref _dmdataApiBaseUrl, value); }
    public string DmdataCredential { get => _dmdataCredential; set => SetProperty(ref _dmdataCredential, value); }
    public DmdataAuthenticationMode DmdataAuthenticationMode { get => _dmdataAuthenticationMode; set => SetProperty(ref _dmdataAuthenticationMode, value); }
    public DmdataEewContractType DmdataEewContractType { get => _dmdataEewContractType; set => SetProperty(ref _dmdataEewContractType, value); }
    public bool DmdataIncludeTestTelegrams { get => _dmdataIncludeTestTelegrams; set => SetProperty(ref _dmdataIncludeTestTelegrams, value); }
    public bool DmdataReceiveEewWarnings { get => _dmdataReceiveEewWarnings; set => SetProperty(ref _dmdataReceiveEewWarnings, value); }
    public bool DmdataReceiveEarthquakeTelegrams { get => _dmdataReceiveEarthquakeTelegrams; set => SetProperty(ref _dmdataReceiveEarthquakeTelegrams, value); }
    public bool DmdataReceiveWeatherWarnings { get => _dmdataReceiveWeatherWarnings; set => SetProperty(ref _dmdataReceiveWeatherWarnings, value); }
    public bool DmdataReceiveVolcanoTelegrams { get => _dmdataReceiveVolcanoTelegrams; set => SetProperty(ref _dmdataReceiveVolcanoTelegrams, value); }
    public bool DmdataUseLegacyWeatherWarningTelegrams { get => _dmdataUseLegacyWeatherWarningTelegrams; set => SetProperty(ref _dmdataUseLegacyWeatherWarningTelegrams, value); }
    public string AxisApiBaseUrl { get => _axisApiBaseUrl; set => SetProperty(ref _axisApiBaseUrl, value); }
    public string AxisAccessToken { get => _axisAccessToken; set => SetProperty(ref _axisAccessToken, value); }
    public string AxisChannel { get => _axisChannel; set => SetProperty(ref _axisChannel, value); }
    public bool FilterEew { get => _filterEew; set => SetProperty(ref _filterEew, value); }
    public bool FilterQuake { get => _filterQuake; set => SetProperty(ref _filterQuake, value); }
    public bool FilterTsunami { get => _filterTsunami; set => SetProperty(ref _filterTsunami, value); }
    public bool FilterWeatherWarning { get => _filterWeatherWarning; set => SetProperty(ref _filterWeatherWarning, value); }
    public bool FilterVolcano { get => _filterVolcano; set => SetProperty(ref _filterVolcano, value); }
    public IReadOnlyList<string> WeatherPrefectureCodes => _weatherPrefectureCodes;

    public string WeatherPrefectureSelectionSummary
    {
        get
        {
            if (_weatherPrefectureCodes.Length == 0)
            {
                return "全国";
            }

            string[] names = _weatherPrefectureCodes
                .Select(WeatherPrefectureCatalog.Find)
                .Where(static option => option is not null)
                .Select(static option => option!.Name)
                .ToArray();
            return names.Length <= 3
                ? string.Join("、", names)
                : $"{string.Join("、", names.Take(3))} ほか{names.Length - 3}都道府県";
        }
    }

    public void SetWeatherPrefectureCodes(IEnumerable<string>? codes)
    {
        string[] normalized = WeatherPrefectureCatalog.NormalizeCodes(codes);
        if (_weatherPrefectureCodes.SequenceEqual(normalized, StringComparer.Ordinal))
        {
            return;
        }

        _weatherPrefectureCodes = normalized;
        OnPropertyChanged(nameof(WeatherPrefectureCodes));
        OnPropertyChanged(nameof(WeatherPrefectureSelectionSummary));
    }
    public bool FilterWeatherSpecialWarnings { get => _filterWeatherSpecialWarnings; set => SetProperty(ref _filterWeatherSpecialWarnings, value); }
    public bool FilterWeatherWarnings { get => _filterWeatherWarnings; set => SetProperty(ref _filterWeatherWarnings, value); }
    public bool FilterWeatherAdvisories { get => _filterWeatherAdvisories; set => SetProperty(ref _filterWeatherAdvisories, value); }
    public bool FilterWeatherTornadoAdvisories { get => _filterWeatherTornadoAdvisories; set => SetProperty(ref _filterWeatherTornadoAdvisories, value); }
    public bool FilterWeatherRecordShortRain { get => _filterWeatherRecordShortRain; set => SetProperty(ref _filterWeatherRecordShortRain, value); }
    public bool FilterWeatherDisasterPreventionBulletins { get => _filterWeatherDisasterPreventionBulletins; set => SetProperty(ref _filterWeatherDisasterPreventionBulletins, value); }
    public bool HideQuakeBelowIntensity3 { get => _hideQuakeBelowIntensity3; set => SetProperty(ref _hideQuakeBelowIntensity3, value); }
    public double PageDurationSeconds { get => _pageDurationSeconds; set => SetProperty(ref _pageDurationSeconds, value); }
    public bool ShowPageIndicator { get => _showPageIndicator; set => SetProperty(ref _showPageIndicator, value); }
    public bool ShowTsunamiForecast { get => _showTsunamiForecast; set => SetProperty(ref _showTsunamiForecast, value); }
    public double OutputScale { get => _outputScale; set => SetProperty(ref _outputScale, OutputTransformSettings.Default.Scale); }
    public double OutputOffsetX { get => _outputOffsetX; set => SetProperty(ref _outputOffsetX, OutputTransformSettings.Default.OffsetX); }
    public double OutputOffsetY { get => _outputOffsetY; set => SetProperty(ref _outputOffsetY, OutputTransformSettings.Default.OffsetY); }
    public double OutputCropLeft { get => _outputCropLeft; set => SetProperty(ref _outputCropLeft, OutputTransformSettings.Default.CropLeft); }
    public double OutputCropTop { get => _outputCropTop; set => SetProperty(ref _outputCropTop, OutputTransformSettings.Default.CropTop); }
    public double OutputCropRight { get => _outputCropRight; set => SetProperty(ref _outputCropRight, OutputTransformSettings.Default.CropRight); }
    public double OutputCropBottom { get => _outputCropBottom; set => SetProperty(ref _outputCropBottom, OutputTransformSettings.Default.CropBottom); }
    public double LetterSpacingEm { get => _letterSpacingEm; set => SetProperty(ref _letterSpacingEm, 0); }
    public double LineSpacing { get => _lineSpacing; set => SetProperty(ref _lineSpacing, 1); }
    public double FontScale { get => _fontScale; set => SetProperty(ref _fontScale, 1); }
    public int AutoHideSeconds { get => _autoHideSeconds; set => SetProperty(ref _autoHideSeconds, value); }
    public int EewAutoHideSeconds { get => _eewAutoHideSeconds; set => SetProperty(ref _eewAutoHideSeconds, value); }
    public int QuakeAutoHideSeconds { get => _quakeAutoHideSeconds; set => SetProperty(ref _quakeAutoHideSeconds, value); }
    public int TsunamiAutoHideSeconds { get => _tsunamiAutoHideSeconds; set => SetProperty(ref _tsunamiAutoHideSeconds, value); }
    public int WeatherWarningAutoHideSeconds { get => _weatherWarningAutoHideSeconds; set => SetProperty(ref _weatherWarningAutoHideSeconds, value); }
    public IReadOnlyDictionary<string, string> SubtitlePhraseOverrides =>
        _subtitlePhraseOverrides;

    public void SetSubtitlePhraseOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        _subtitlePhraseOverrides = overrides is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(overrides, StringComparer.Ordinal);
        OnPropertyChanged(nameof(SubtitlePhraseOverrides));
    }
    public int ProductionReplayRotationIntervalSeconds { get => _productionReplayRotationIntervalSeconds; set => SetProperty(ref _productionReplayRotationIntervalSeconds, value); }
    public int ProductionReplayResumeDelaySeconds { get => _productionReplayResumeDelaySeconds; set => SetProperty(ref _productionReplayResumeDelaySeconds, value); }
    public bool ProductionReplayEewEnabled { get => _productionReplayEewEnabled; set => SetProperty(ref _productionReplayEewEnabled, value); }
    public int ProductionReplayEewRepeatCount { get => _productionReplayEewRepeatCount; set => SetProperty(ref _productionReplayEewRepeatCount, value); }
    public bool ProductionReplayEewAudioEachCycle { get => _productionReplayEewAudioEachCycle; set => SetProperty(ref _productionReplayEewAudioEachCycle, value); }
    public bool ProductionReplayQuakeEnabled { get => _productionReplayQuakeEnabled; set => SetProperty(ref _productionReplayQuakeEnabled, value); }
    public int ProductionReplayQuakeRepeatCount { get => _productionReplayQuakeRepeatCount; set => SetProperty(ref _productionReplayQuakeRepeatCount, value); }
    public bool ProductionReplayQuakeAudioEachCycle { get => _productionReplayQuakeAudioEachCycle; set => SetProperty(ref _productionReplayQuakeAudioEachCycle, value); }
    public bool ProductionReplayTsunamiEnabled { get => _productionReplayTsunamiEnabled; set => SetProperty(ref _productionReplayTsunamiEnabled, value); }
    public int ProductionReplayTsunamiRepeatCount { get => _productionReplayTsunamiRepeatCount; set => SetProperty(ref _productionReplayTsunamiRepeatCount, value); }
    public bool ProductionReplayTsunamiAudioEachCycle { get => _productionReplayTsunamiAudioEachCycle; set => SetProperty(ref _productionReplayTsunamiAudioEachCycle, value); }
    public bool ProductionReplayWeatherEnabled { get => _productionReplayWeatherEnabled; set => SetProperty(ref _productionReplayWeatherEnabled, value); }
    public int ProductionReplayWeatherRepeatCount { get => _productionReplayWeatherRepeatCount; set => SetProperty(ref _productionReplayWeatherRepeatCount, value); }
    public bool ProductionReplayWeatherAudioEachCycle { get => _productionReplayWeatherAudioEachCycle; set => SetProperty(ref _productionReplayWeatherAudioEachCycle, value); }
    public bool ProductionReplayVolcanoEnabled { get => _productionReplayVolcanoEnabled; set => SetProperty(ref _productionReplayVolcanoEnabled, value); }
    public int ProductionReplayVolcanoRepeatCount { get => _productionReplayVolcanoRepeatCount; set => SetProperty(ref _productionReplayVolcanoRepeatCount, value); }
    public bool ProductionReplayVolcanoAudioEachCycle { get => _productionReplayVolcanoAudioEachCycle; set => SetProperty(ref _productionReplayVolcanoAudioEachCycle, value); }
    public BackgroundMode BackgroundMode
    {
        get => _backgroundMode;
        set => SetProperty(ref _backgroundMode, BackgroundMode.Transparent);
    }
    public int Width { get => _width; set => SetProperty(ref _width, value); }
    public int Height { get => _height; set => SetProperty(ref _height, value); }
    public bool ObsEnabled { get => _obsEnabled; set => SetProperty(ref _obsEnabled, value); }
    public int ObsPort { get => _obsPort; set => SetProperty(ref _obsPort, value); }
    public bool ObsRuntimeRecovery { get => _obsRuntimeRecovery; set => SetProperty(ref _obsRuntimeRecovery, value); }
    public int ObsSnapshotIntervalMilliseconds
    {
        get => _obsSnapshotIntervalMilliseconds;
        set => SetProperty(ref _obsSnapshotIntervalMilliseconds, value);
    }
    public bool ObsBrowserSourceSyncEnabled { get => _obsBrowserSourceSyncEnabled; set => SetProperty(ref _obsBrowserSourceSyncEnabled, value); }
    public string ObsWebSocketUrl { get => _obsWebSocketUrl; set => SetProperty(ref _obsWebSocketUrl, value); }
    public string ObsWebSocketPassword { get => _obsWebSocketPassword; set => SetProperty(ref _obsWebSocketPassword, value); }
    public string ObsTargetSceneName { get => _obsTargetSceneName; set => SetProperty(ref _obsTargetSceneName, value); }
    public ObsAudioMonitoringMode ObsAudioMonitoringMode
    {
        get => _obsAudioMonitoringMode;
        set => SetProperty(
            ref _obsAudioMonitoringMode,
            NormalizeObsAudioMonitoringMode(value));
    }
    public bool AudioMuted { get => _audioMuted; set => SetProperty(ref _audioMuted, value); }
    public bool AudioInRehearsal { get => _audioInRehearsal; set => SetProperty(ref _audioInRehearsal, value); }
    public bool QuakeAudioEnabled { get => _quakeAudioEnabled; set => SetProperty(ref _quakeAudioEnabled, value); }
    public bool TsunamiAdvisoryAudioEnabled { get => _tsunamiAdvisoryAudioEnabled; set => SetProperty(ref _tsunamiAdvisoryAudioEnabled, value); }
    public bool TsunamiWarningAudioEnabled { get => _tsunamiWarningAudioEnabled; set => SetProperty(ref _tsunamiWarningAudioEnabled, value); }
    public bool TsunamiMajorWarningAudioEnabled { get => _tsunamiMajorWarningAudioEnabled; set => SetProperty(ref _tsunamiMajorWarningAudioEnabled, value); }
    public JmaScale MinimumQuakeScale
    {
        get => _minimumQuakeScale;
        set => SetProperty(ref _minimumQuakeScale, NormalizeMinimumQuakeScale(value));
    }

    public bool EewInitialAudioEnabled { get => _eewInitialAudioEnabled; set => SetProperty(ref _eewInitialAudioEnabled, value); }
    public bool EewContinuationAudioEnabled { get => _eewContinuationAudioEnabled; set => SetProperty(ref _eewContinuationAudioEnabled, value); }
    public bool EewCancellationAudioEnabled { get => _eewCancellationAudioEnabled; set => SetProperty(ref _eewCancellationAudioEnabled, value); }
    public bool WeatherSpecialWarningAudioEnabled { get => _weatherSpecialWarningAudioEnabled; set => SetProperty(ref _weatherSpecialWarningAudioEnabled, value); }
    public bool WeatherWarningAudioEnabled { get => _weatherWarningAudioEnabled; set => SetProperty(ref _weatherWarningAudioEnabled, value); }
    public bool WeatherAdvisoryAudioEnabled { get => _weatherAdvisoryAudioEnabled; set => SetProperty(ref _weatherAdvisoryAudioEnabled, value); }
    public double WeatherAudioCoalescingSeconds { get => _weatherAudioCoalescingSeconds; set => SetProperty(ref _weatherAudioCoalescingSeconds, value); }
    public string QuakeAudioFilePath { get => _quakeAudioFilePath; set => SetProperty(ref _quakeAudioFilePath, value); }
    public string TsunamiAdvisoryAudioFilePath { get => _tsunamiAdvisoryAudioFilePath; set => SetProperty(ref _tsunamiAdvisoryAudioFilePath, value); }
    public string TsunamiWarningAudioFilePath { get => _tsunamiWarningAudioFilePath; set => SetProperty(ref _tsunamiWarningAudioFilePath, value); }
    public string TsunamiMajorWarningAudioFilePath { get => _tsunamiMajorWarningAudioFilePath; set => SetProperty(ref _tsunamiMajorWarningAudioFilePath, value); }
    public string EewInitialAudioFilePath { get => _eewInitialAudioFilePath; set => SetProperty(ref _eewInitialAudioFilePath, value); }
    public string EewContinuationAudioFilePath { get => _eewContinuationAudioFilePath; set => SetProperty(ref _eewContinuationAudioFilePath, value); }
    public string EewCancellationAudioFilePath { get => _eewCancellationAudioFilePath; set => SetProperty(ref _eewCancellationAudioFilePath, value); }
    public string WeatherSpecialWarningAudioFilePath { get => _weatherSpecialWarningAudioFilePath; set => SetProperty(ref _weatherSpecialWarningAudioFilePath, value); }
    public string WeatherWarningAudioFilePath { get => _weatherWarningAudioFilePath; set => SetProperty(ref _weatherWarningAudioFilePath, value); }
    public string WeatherAdvisoryAudioFilePath { get => _weatherAdvisoryAudioFilePath; set => SetProperty(ref _weatherAdvisoryAudioFilePath, value); }
    public HistoryApi HistoryApi
    {
        get => _historyApi;
        set
        {
            if (SetProperty(ref _historyApi, value))
            {
                OnPropertyChanged(nameof(IsNiiHistoryApi));
            }
        }
    }

    public bool IsNiiHistoryApi => HistoryApi == HistoryApi.NiiJmaXml;

    public int HistoryLimit { get => _historyLimit; set => SetProperty(ref _historyLimit, value); }
    public int HistoryIntervalSeconds { get => _historyIntervalSeconds; set => SetProperty(ref _historyIntervalSeconds, value); }
    public DateTime NiiHistoryDate { get => _niiHistoryDate; set => SetProperty(ref _niiHistoryDate, value); }
    public NiiHistoryContent NiiHistoryContent { get => _niiHistoryContent; set => SetProperty(ref _niiHistoryContent, value); }
    public string NiiHistoryReportUrl { get => _niiHistoryReportUrl; set => SetProperty(ref _niiHistoryReportUrl, value); }
    public string LocalHistoryXmlFilePath { get => _localHistoryXmlFilePath; set => SetProperty(ref _localHistoryXmlFilePath, value); }
    public bool HistoryRepeat { get => _historyRepeat; set => SetProperty(ref _historyRepeat, value); }
    public bool EnrichQuakeById { get => _enrichQuakeById; set => SetProperty(ref _enrichQuakeById, value); }
    public bool ConfirmTestInProduction { get => _confirmTestInProduction; set => SetProperty(ref _confirmTestInProduction, value); }

    public AppSettings ToSettings(AppSettings baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ProviderRoutingSettings routing = CurrentRouting;
        double pageDuration = Math.Round(
            Math.Clamp(PageDurationSeconds, 1, 30) * 2,
            MidpointRounding.AwayFromZero) / 2;
        ProviderMode providerMode = routing.Uses(ReceptionProvider.Axis) ||
            routing.Uses(ReceptionProvider.Dmdata)
            ? ProviderMode.Production
            : ProviderMode == ProviderMode.Sandbox
            ? ProviderMode.Sandbox
            : ProviderMode.Production;
        ProviderOptions provider = providerMode == ProviderMode.Sandbox
            ? ProviderOptions.Sandbox
            : ProviderOptions.Production;
        const BackgroundMode backgroundMode = BackgroundMode.Transparent;
        return baseline with
        {
            Provider = baseline.Provider with
            {
                Mode = providerMode,
                WebSocketUrl = provider.WebSocketUri.AbsoluteUri,
                RestBaseUrl = provider.RestBaseUri.AbsoluteUri.TrimEnd('/'),
                ReceptionProvider = routing.GetCompatibilityProvider(),
                Routing = routing,
                DmdataApiBaseUrl = DmdataApiBaseUrl.Trim(),
                DmdataCredentialEnvironmentVariable = string.Empty,
                DmdataProtectedCredential = ProtectDmdataCredential(DmdataCredential),
                DmdataAuthenticationMode = EEWTelop.Application.Configuration.DmdataAuthenticationMode.ApiKey,
                DmdataEewContractType = DmdataEewContractType,
                DmdataIncludeTestTelegrams = DmdataIncludeTestTelegrams,
                DmdataReceiveEewWarnings = routing.Eew == ReceptionProvider.Dmdata,
                DmdataReceiveEarthquakeTelegrams =
                    routing.Quake == ReceptionProvider.Dmdata ||
                    routing.Tsunami == ReceptionProvider.Dmdata ||
                    routing.NankaiTrough == ReceptionProvider.Dmdata,
                DmdataReceiveWeatherWarnings = BuildFeatures.ExtendedFeaturesEnabled &&
                    routing.Weather == ReceptionProvider.Dmdata,
                DmdataReceiveVolcanoTelegrams = BuildFeatures.ExtendedFeaturesEnabled &&
                    routing.Volcano == ReceptionProvider.Dmdata,
                DmdataUseLegacyWeatherWarningTelegrams =
                    BuildFeatures.ExtendedFeaturesEnabled &&
                    routing.Weather == ReceptionProvider.Dmdata &&
                    DmdataUseLegacyWeatherWarningTelegrams,
                AxisApiBaseUrl = AxisApiBaseUrl.Trim(),
                AxisProtectedAccessToken = ProtectAxisToken(AxisAccessToken),
                AxisChannel = GetAxisChannel(routing),
            },
            Filter = new FilterSettings(
                FilterEew,
                FilterQuake,
                FilterTsunami,
                HideQuakeBelowIntensity3)
            {
                WeatherWarning = FilterWeatherWarning,
                WeatherPrefectureCodes = _weatherPrefectureCodes.ToArray(),
                WeatherPrefectureCode = _weatherPrefectureCodes.Length == 1
                    ? _weatherPrefectureCodes[0]
                    : string.Empty,
                WeatherSpecialWarnings = FilterWeatherSpecialWarnings,
                WeatherWarnings = FilterWeatherWarnings,
                WeatherAdvisories = FilterWeatherAdvisories,
                WeatherTornadoAdvisories = FilterWeatherTornadoAdvisories,
                WeatherRecordShortRain = FilterWeatherRecordShortRain,
                WeatherDisasterPreventionBulletins =
                    FilterWeatherDisasterPreventionBulletins,
                Volcano = FilterVolcano,
            },
            Display = new DisplaySettings(
                pageDuration,
                ShowPageIndicator,
                0,
                1,
                1,
                Math.Clamp(AutoHideSeconds, 0, 3600),
                backgroundMode,
                Math.Clamp(Width, 320, 7680),
                Math.Clamp(Height, 180, 4320))
            {
                EewAutoHideSeconds = Math.Clamp(EewAutoHideSeconds, 0, 3600),
                QuakeAutoHideSeconds = Math.Clamp(QuakeAutoHideSeconds, 0, 3600),
                TsunamiAutoHideSeconds = Math.Clamp(TsunamiAutoHideSeconds, 0, 3600),
                WeatherWarningAutoHideSeconds = Math.Clamp(
                    WeatherWarningAutoHideSeconds,
                    0,
                    3600),
                ShowTsunamiForecast = ShowTsunamiForecast,
                SubtitlePhraseOverrides = new Dictionary<string, string>(
                    _subtitlePhraseOverrides,
                    StringComparer.Ordinal),
                OutputTransform = OutputTransformSettings.Default,
                ProductionReplay = new ProductionReplaySettings(
                    RotationIntervalSeconds: Math.Clamp(
                        ProductionReplayRotationIntervalSeconds,
                        1,
                        300),
                    ResumeDelaySeconds: Math.Clamp(
                        ProductionReplayResumeDelaySeconds,
                        0,
                        300),
                    Eew: ProductionReplayPolicy.Disabled(
                        Math.Clamp(ProductionReplayEewRepeatCount, 1, 100)),
                    Quake: new ProductionReplayPolicy(
                        ProductionReplayQuakeEnabled,
                        Math.Clamp(ProductionReplayQuakeRepeatCount, 1, 100),
                        ProductionReplayQuakeAudioEachCycle),
                    Tsunami: new ProductionReplayPolicy(
                        ProductionReplayTsunamiEnabled,
                        Math.Clamp(ProductionReplayTsunamiRepeatCount, 1, 100),
                        ProductionReplayTsunamiAudioEachCycle),
                    WeatherWarning: new ProductionReplayPolicy(
                        ProductionReplayWeatherEnabled,
                        Math.Clamp(ProductionReplayWeatherRepeatCount, 1, 100),
                        ProductionReplayWeatherAudioEachCycle),
                    Volcano: new ProductionReplayPolicy(
                        ProductionReplayVolcanoEnabled,
                        Math.Clamp(ProductionReplayVolcanoRepeatCount, 1, 100),
                        ProductionReplayVolcanoAudioEachCycle)),
            },
            Obs = new ObsSettings(
                ObsEnabled,
                Math.Clamp(ObsPort, 0, 65535),
                ObsRuntimeRecovery)
            {
                SnapshotIntervalMilliseconds = Math.Clamp(
                    ObsSnapshotIntervalMilliseconds,
                    ObsSettings.MinimumSnapshotIntervalMilliseconds,
                    ObsSettings.MaximumSnapshotIntervalMilliseconds),
                BrowserSourceSyncEnabled = ObsBrowserSourceSyncEnabled,
                WebSocketUrl = ObsWebSocketUrl.Trim(),
                WebSocketProtectedPassword = ObsCredentialProtector.Protect(ObsWebSocketPassword),
                TargetSceneName = ObsTargetSceneName.Trim(),
                AudioMonitoringMode = NormalizeObsAudioMonitoringMode(ObsAudioMonitoringMode),
            },
            Audio = new AudioSettings(
                EewEnabled: EewInitialAudioEnabled || EewContinuationAudioEnabled ||
                    EewCancellationAudioEnabled,
                TsunamiEnabled: TsunamiAudioCues.Any(static item => item.Enabled),
                TrainingEnabled: false,
                TestUsesProductionSound: AudioInRehearsal,
                Muted: AudioMuted,
                QuakeEnabled: QuakeAudioCues.Any(static item => item.Enabled),
                EewInitialEnabled: EewInitialAudioEnabled,
                EewContinuationEnabled: EewContinuationAudioEnabled,
                EewCancellationEnabled: EewCancellationAudioEnabled,
                QuakeFilePath: QuakeAudioCues.FirstOrDefault(
                    static item => item.Enabled)?.FilePath.Trim() ?? string.Empty,
                TsunamiFilePath: baseline.Audio.TsunamiFilePath?.Trim() ?? string.Empty,
                EewInitialFilePath: EewInitialAudioFilePath.Trim(),
                EewContinuationFilePath: EewContinuationAudioFilePath.Trim(),
                EewCancellationFilePath: EewCancellationAudioFilePath.Trim(),
                FileAudioConfigured: true)
            {
                MinimumQuakeScale = NormalizeMinimumQuakeScale(MinimumQuakeScale),
                // 旧版へ設定を戻す場合に備え、廃止前の閾値とファイル設定も保持する。
                MinimumTsunamiGrade = baseline.Audio.MinimumTsunamiGrade,
                TsunamiAdvisoryEnabled = TsunamiAudioCues.Single(
                    static item => item.TsunamiGrade == TsunamiGrade.Watch).Enabled,
                TsunamiWarningEnabled = TsunamiAudioCues.Single(
                    static item => item.TsunamiGrade == TsunamiGrade.Warning).Enabled,
                TsunamiMajorWarningEnabled = TsunamiAudioCues.Single(
                    static item => item.TsunamiGrade == TsunamiGrade.MajorWarning).Enabled,
                TsunamiAdvisoryFilePath = TsunamiAudioCues.Single(
                    static item => item.TsunamiGrade == TsunamiGrade.Watch).FilePath.Trim(),
                TsunamiWarningFilePath = TsunamiAudioCues.Single(
                    static item => item.TsunamiGrade == TsunamiGrade.Warning).FilePath.Trim(),
                TsunamiMajorWarningFilePath = TsunamiAudioCues.Single(
                    static item => item.TsunamiGrade == TsunamiGrade.MajorWarning).FilePath.Trim(),
                WeatherSpecialWarningEnabled = WeatherSpecialWarningAudioEnabled,
                WeatherWarningEnabled = WeatherWarningAudioEnabled,
                WeatherAdvisoryEnabled = WeatherAdvisoryAudioEnabled,
                WeatherSpecialWarningFilePath =
                    WeatherSpecialWarningAudioFilePath.Trim(),
                WeatherWarningFilePath = WeatherWarningAudioFilePath.Trim(),
                WeatherAdvisoryFilePath = WeatherAdvisoryAudioFilePath.Trim(),
                WeatherCoalescingSeconds = double.IsFinite(WeatherAudioCoalescingSeconds)
                    ? Math.Clamp(
                        WeatherAudioCoalescingSeconds,
                        0,
                        AudioSettings.MaximumWeatherCoalescingSeconds)
                    : AudioSettings.DefaultWeatherCoalescingSeconds,
                QuakeScaleCues = QuakeAudioCues.ToDictionary(
                    static item => item.QuakeScale!.Value,
                    static item => new AudioCueSetting(
                        item.Enabled,
                        item.FilePath.Trim())),
                TsunamiGradeCues = TsunamiAudioCues.ToDictionary(
                    static item => item.TsunamiGrade!.Value,
                    static item => new AudioCueSetting(
                        item.Enabled,
                        item.FilePath.Trim())),
            },
            History = new HistorySettings(
                HistoryApi,
                Math.Clamp(HistoryLimit, 1, 100),
                Math.Max(1, HistoryIntervalSeconds))
            {
                NiiDate = DateOnly.FromDateTime(
                    NiiHistoryDate.Date < new DateTime(2012, 12, 1)
                        ? new DateTime(2012, 12, 1)
                        : NiiHistoryDate.Date > DateTime.Today
                            ? DateTime.Today
                            : NiiHistoryDate.Date),
                NiiContent = NiiHistoryContent,
                NiiReportUrl = NiiHistoryReportUrl.Trim(),
                LocalXmlFilePath = LocalHistoryXmlFilePath.Trim(),
                Repeat = HistoryRepeat,
            },
            Compatibility = new CompatibilitySettings(EnrichQuakeById),
            Log = new LogSettings(UiLogMaxEntries)
            {
                SaveRawProviderMessages = SaveRawProviderMessages,
                RawMessageRetentionDays = Math.Clamp(RawMessageRetentionDays, 1, 90),
                RawMessageMaximumTotalMegabytes = Math.Clamp(
                    RawMessageMaximumTotalMegabytes,
                    32,
                    4096),
            },
            Safety = baseline.Safety with
            {
                ConfirmTestInProduction =
                    ProviderMode == ProviderMode.Production || ConfirmTestInProduction,
            },
        };
    }

    private static string ProtectAxisToken(string token)
    {
#if QTELOPPER_AXIS_PROVIDER
        return AxisCredentialProtector.Protect(token);
#else
        return string.Empty;
#endif
    }

    private static string ProtectDmdataCredential(string credential)
    {
#if QTELOPPER_DMDATA_PROVIDER
        return DmdataCredentialProtector.Protect(credential);
#else
        return string.Empty;
#endif
    }

    private static string UnprotectDmdataCredential(string protectedCredential)
    {
#if QTELOPPER_DMDATA_PROVIDER
        return DmdataCredentialProtector.Unprotect(protectedCredential);
#else
        return string.Empty;
#endif
    }

    private static string ResolveDmdataCredential(ProviderSettings provider)
    {
        string credential = UnprotectDmdataCredential(provider.DmdataProtectedCredential);
        if (!string.IsNullOrWhiteSpace(credential) ||
            string.IsNullOrWhiteSpace(provider.DmdataCredentialEnvironmentVariable))
        {
            return credential;
        }

        // One-time UI migration path for pre-schema-22 settings. Saving the
        // settings moves this value into the CurrentUser DPAPI payload.
        return Environment.GetEnvironmentVariable(
            provider.DmdataCredentialEnvironmentVariable.Trim()) ?? string.Empty;
    }

    private static string UnprotectAxisToken(string protectedToken)
    {
#if QTELOPPER_AXIS_PROVIDER
        return AxisCredentialProtector.Unprotect(protectedToken);
#else
        return string.Empty;
#endif
    }

    private static string GetAxisChannel(ProviderRoutingSettings routing)
    {
#if QTELOPPER_AXIS_PROVIDER
        return AxisProviderOptions.BuildSelectedChannels(routing);
#else
        return string.Empty;
#endif
    }

    public string GetAudioFilePath(AudioCueId cue) => cue switch
    {
        AudioCueId.QuakeIntensity1 or AudioCueId.QuakeIntensity2 or
        AudioCueId.QuakeIntensity3 or AudioCueId.QuakeIntensity4 or
        AudioCueId.QuakeIntensity5Lower or AudioCueId.QuakeIntensity5Upper or
        AudioCueId.QuakeIntensity6Lower or AudioCueId.QuakeIntensity6Upper or
        AudioCueId.QuakeIntensity7 => FindAudioCue(QuakeAudioCues, cue)?.FilePath ?? string.Empty,
        AudioCueId.TsunamiForecast or AudioCueId.TsunamiAdvisory or
        AudioCueId.TsunamiWarning or AudioCueId.TsunamiMajorWarning =>
            FindAudioCue(TsunamiAudioCues, cue)?.FilePath ?? string.Empty,
        AudioCueId.QuakeIntensity3OrMore => QuakeAudioFilePath,
        AudioCueId.Tsunami => TsunamiWarningAudioFilePath,
        AudioCueId.EewInitial => EewInitialAudioFilePath,
        AudioCueId.EewContinuation => EewContinuationAudioFilePath,
        AudioCueId.EewCancellation => EewCancellationAudioFilePath,
        AudioCueId.WeatherSpecialWarning => WeatherSpecialWarningAudioFilePath,
        AudioCueId.WeatherWarning => WeatherWarningAudioFilePath,
        AudioCueId.WeatherAdvisory => WeatherAdvisoryAudioFilePath,
        _ => string.Empty,
    };

    public void SetAudioFilePath(AudioCueId cue, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        AudioCueOptionViewModel? individual =
            FindAudioCue(QuakeAudioCues, cue) ?? FindAudioCue(TsunamiAudioCues, cue);
        if (individual is not null)
        {
            individual.FilePath = filePath;
            individual.Enabled = true;
            return;
        }

        switch (cue)
        {
            case AudioCueId.QuakeIntensity3OrMore:
                QuakeAudioFilePath = filePath;
                QuakeAudioEnabled = true;
                break;
            case AudioCueId.Tsunami:
            case AudioCueId.TsunamiWarning:
                TsunamiWarningAudioFilePath = filePath;
                TsunamiWarningAudioEnabled = true;
                break;
            case AudioCueId.TsunamiAdvisory:
                TsunamiAdvisoryAudioFilePath = filePath;
                TsunamiAdvisoryAudioEnabled = true;
                break;
            case AudioCueId.TsunamiMajorWarning:
                TsunamiMajorWarningAudioFilePath = filePath;
                TsunamiMajorWarningAudioEnabled = true;
                break;
            case AudioCueId.EewInitial:
                EewInitialAudioFilePath = filePath;
                EewInitialAudioEnabled = true;
                break;
            case AudioCueId.EewContinuation:
                EewContinuationAudioFilePath = filePath;
                EewContinuationAudioEnabled = true;
                break;
            case AudioCueId.EewCancellation:
                EewCancellationAudioFilePath = filePath;
                EewCancellationAudioEnabled = true;
                break;
            case AudioCueId.WeatherSpecialWarning:
                WeatherSpecialWarningAudioFilePath = filePath;
                WeatherSpecialWarningAudioEnabled = true;
                break;
            case AudioCueId.WeatherWarning:
                WeatherWarningAudioFilePath = filePath;
                WeatherWarningAudioEnabled = true;
                break;
            case AudioCueId.WeatherAdvisory:
                WeatherAdvisoryAudioFilePath = filePath;
                WeatherAdvisoryAudioEnabled = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cue), cue, null);
        }
    }

    public void ResetAudioSettings()
    {
        AudioSettings defaults = AudioSettings.Disabled;

        AudioMuted = defaults.Muted;
        AudioInRehearsal = defaults.TestUsesProductionSound;
        QuakeAudioEnabled = defaults.QuakeEnabled;
        MinimumQuakeScale = defaults.MinimumQuakeScale;

        foreach (AudioCueOptionViewModel cue in QuakeAudioCues)
        {
            cue.Enabled = false;
            cue.FilePath = string.Empty;
        }

        TsunamiAdvisoryAudioEnabled = defaults.TsunamiAdvisoryEnabled;
        TsunamiWarningAudioEnabled = defaults.TsunamiWarningEnabled;
        TsunamiMajorWarningAudioEnabled = defaults.TsunamiMajorWarningEnabled;
        TsunamiAdvisoryAudioFilePath = defaults.TsunamiAdvisoryFilePath;
        TsunamiWarningAudioFilePath = defaults.TsunamiWarningFilePath;
        TsunamiMajorWarningAudioFilePath = defaults.TsunamiMajorWarningFilePath;

        foreach (AudioCueOptionViewModel cue in TsunamiAudioCues)
        {
            cue.Enabled = false;
            cue.FilePath = string.Empty;
        }

        EewInitialAudioEnabled = defaults.EewInitialEnabled;
        EewContinuationAudioEnabled = defaults.EewContinuationEnabled;
        EewCancellationAudioEnabled = defaults.EewCancellationEnabled;
        EewInitialAudioFilePath = defaults.EewInitialFilePath;
        EewContinuationAudioFilePath = defaults.EewContinuationFilePath;
        EewCancellationAudioFilePath = defaults.EewCancellationFilePath;

        WeatherSpecialWarningAudioEnabled = defaults.WeatherSpecialWarningEnabled;
        WeatherWarningAudioEnabled = defaults.WeatherWarningEnabled;
        WeatherAdvisoryAudioEnabled = defaults.WeatherAdvisoryEnabled;
        WeatherSpecialWarningAudioFilePath = defaults.WeatherSpecialWarningFilePath;
        WeatherWarningAudioFilePath = defaults.WeatherWarningFilePath;
        WeatherAdvisoryAudioFilePath = defaults.WeatherAdvisoryFilePath;
        WeatherAudioCoalescingSeconds = defaults.EffectiveWeatherCoalescingSeconds;

        // These legacy values are not exposed by the current UI, but clearing them
        // prevents an older version from reviving a previously selected file.
        QuakeAudioFilePath = defaults.QuakeFilePath;
    }

    public void ResetReceptionSettings()
    {
        ProviderSettings defaults = AppSettings.CreateDefault().Provider;
        ApplyRouting(defaults.Routing);
        ProviderMode = defaults.Mode;
        WebSocketUrl = defaults.WebSocketUrl;
        RestBaseUrl = defaults.RestBaseUrl;
        DmdataApiBaseUrl = defaults.DmdataApiBaseUrl;
        DmdataCredential = string.Empty;
        DmdataAuthenticationMode = EEWTelop.Application.Configuration.DmdataAuthenticationMode.ApiKey;
        DmdataEewContractType = defaults.DmdataEewContractType;
        DmdataIncludeTestTelegrams = defaults.DmdataIncludeTestTelegrams;
        // Contract categories are opt-in. Never select a category merely because
        // it is available in this edition: dmdata rejects categories outside the
        // API key's active contract/scopes.
        DmdataReceiveEewWarnings = defaults.DmdataReceiveEewWarnings;
        DmdataReceiveEarthquakeTelegrams = defaults.DmdataReceiveEarthquakeTelegrams;
        DmdataReceiveWeatherWarnings = BuildFeatures.ExtendedFeaturesEnabled &&
            defaults.DmdataReceiveWeatherWarnings;
        DmdataReceiveVolcanoTelegrams = BuildFeatures.ExtendedFeaturesEnabled &&
            defaults.DmdataReceiveVolcanoTelegrams;
        DmdataUseLegacyWeatherWarningTelegrams =
            defaults.DmdataUseLegacyWeatherWarningTelegrams;
        AxisApiBaseUrl = defaults.AxisApiBaseUrl;
        AxisAccessToken = string.Empty;
        AxisChannel = defaults.AxisChannel;
    }

    public void ResetFilterSettings()
    {
        FilterSettings defaults = AppSettings.CreateDefault().Filter;
        FilterEew = defaults.Eew;
        FilterQuake = defaults.Quake;
        FilterTsunami = defaults.Tsunami;
        FilterWeatherWarning = defaults.WeatherWarning;
        FilterVolcano = defaults.Volcano;
        HideQuakeBelowIntensity3 = defaults.HideQuakeBelowIntensity3;
        SetWeatherPrefectureCodes(defaults.WeatherPrefectureCodes);
        FilterWeatherSpecialWarnings = defaults.WeatherSpecialWarnings;
        FilterWeatherWarnings = defaults.WeatherWarnings;
        FilterWeatherAdvisories = defaults.WeatherAdvisories;
        FilterWeatherTornadoAdvisories = defaults.WeatherTornadoAdvisories;
        FilterWeatherRecordShortRain = defaults.WeatherRecordShortRain;
        FilterWeatherDisasterPreventionBulletins =
            defaults.WeatherDisasterPreventionBulletins;
    }

    public void ResetCompatibilityAndSafetySettings()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        EnrichQuakeById = defaults.Compatibility.EnrichQuakeById;
        ConfirmTestInProduction = defaults.Safety.ConfirmTestInProduction;
    }

    public void ResetDisplaySettings()
    {
        DisplaySettings defaults = AppSettings.CreateDefault().Display;
        PageDurationSeconds = defaults.PageDurationSeconds;
        ShowPageIndicator = defaults.ShowPageIndicator;
        ShowTsunamiForecast = defaults.ShowTsunamiForecast;
        LetterSpacingEm = defaults.LetterSpacingEm;
        LineSpacing = defaults.LineSpacing;
        FontScale = defaults.FontScale;
        AutoHideSeconds = defaults.AutoHideSeconds;
        EewAutoHideSeconds = defaults.EffectiveEewAutoHideSeconds;
        QuakeAutoHideSeconds = defaults.EffectiveQuakeAutoHideSeconds;
        TsunamiAutoHideSeconds = defaults.EffectiveTsunamiAutoHideSeconds;
        WeatherWarningAutoHideSeconds = defaults.EffectiveWeatherWarningAutoHideSeconds;
        SetSubtitlePhraseOverrides(defaults.SubtitlePhraseOverrides);
    }

    public void ResetProductionReplaySettings()
    {
        ProductionReplaySettings defaults = ProductionReplaySettings.Default;
        ProductionReplayRotationIntervalSeconds = defaults.RotationIntervalSeconds;
        ProductionReplayResumeDelaySeconds = defaults.ResumeDelaySeconds;
        ApplyProductionReplayPolicy(defaults.Eew, EventKind.Eew);
        ApplyProductionReplayPolicy(defaults.Quake, EventKind.Quake);
        ApplyProductionReplayPolicy(defaults.Tsunami, EventKind.Tsunami);
        ApplyProductionReplayPolicy(defaults.WeatherWarning, EventKind.WeatherWarning);
        ApplyProductionReplayPolicy(defaults.Volcano, EventKind.Volcano);
    }

    public void ResetCanvasSettings()
    {
        DisplaySettings defaults = AppSettings.CreateDefault().Display;
        BackgroundMode = defaults.BackgroundMode;
        Width = defaults.Width;
        Height = defaults.Height;
        OutputScale = defaults.OutputTransform.Scale;
        OutputOffsetX = defaults.OutputTransform.OffsetX;
        OutputOffsetY = defaults.OutputTransform.OffsetY;
        OutputCropLeft = defaults.OutputTransform.CropLeft;
        OutputCropTop = defaults.OutputTransform.CropTop;
        OutputCropRight = defaults.OutputTransform.CropRight;
        OutputCropBottom = defaults.OutputTransform.CropBottom;
    }

    public void ResetObsLocalViewSettings()
    {
        ObsSettings defaults = AppSettings.CreateDefault().Obs;
        ObsEnabled = defaults.Enabled;
        ObsPort = defaults.Port;
        ObsSnapshotIntervalMilliseconds = defaults.SnapshotIntervalMilliseconds;
        ObsRuntimeRecovery = defaults.RuntimeRecovery;
    }

    public void ResetObsWebSocketSettings()
    {
        ObsSettings defaults = AppSettings.CreateDefault().Obs;
        ObsBrowserSourceSyncEnabled = defaults.BrowserSourceSyncEnabled;
        ObsWebSocketUrl = defaults.WebSocketUrl;
        ObsWebSocketPassword = string.Empty;
        ObsTargetSceneName = defaults.TargetSceneName;
        ObsAudioMonitoringMode = defaults.AudioMonitoringMode;
    }

    public void ResetHistorySettings()
    {
        HistorySettings defaults = AppSettings.CreateDefault().History;
        HistoryApi = defaults.Api;
        HistoryLimit = defaults.Limit;
        HistoryIntervalSeconds = defaults.IntervalSeconds;
        NiiHistoryDate = defaults.NiiDate.ToDateTime(TimeOnly.MinValue);
        NiiHistoryContent = defaults.NiiContent;
        NiiHistoryReportUrl = defaults.NiiReportUrl;
        LocalHistoryXmlFilePath = defaults.LocalXmlFilePath;
        HistoryRepeat = defaults.Repeat;
    }

    public void ResetLogSettings()
    {
        LogSettings defaults = AppSettings.CreateDefault().Log;
        SaveRawProviderMessages = defaults.SaveRawProviderMessages;
        RawMessageRetentionDays = defaults.RawMessageRetentionDays;
        RawMessageMaximumTotalMegabytes = defaults.RawMessageMaximumTotalMegabytes;
    }

    private void ApplyProductionReplayPolicy(ProductionReplayPolicy policy, EventKind kind)
    {
        switch (kind)
        {
            case EventKind.Eew:
                ProductionReplayEewEnabled = policy.Enabled;
                ProductionReplayEewRepeatCount = policy.RepeatCount;
                ProductionReplayEewAudioEachCycle = policy.AudioOnEachCycle;
                break;
            case EventKind.Quake:
                ProductionReplayQuakeEnabled = policy.Enabled;
                ProductionReplayQuakeRepeatCount = policy.RepeatCount;
                ProductionReplayQuakeAudioEachCycle = policy.AudioOnEachCycle;
                break;
            case EventKind.Tsunami:
                ProductionReplayTsunamiEnabled = policy.Enabled;
                ProductionReplayTsunamiRepeatCount = policy.RepeatCount;
                ProductionReplayTsunamiAudioEachCycle = policy.AudioOnEachCycle;
                break;
            case EventKind.WeatherWarning:
                ProductionReplayWeatherEnabled = policy.Enabled;
                ProductionReplayWeatherRepeatCount = policy.RepeatCount;
                ProductionReplayWeatherAudioEachCycle = policy.AudioOnEachCycle;
                break;
            case EventKind.Volcano:
                ProductionReplayVolcanoEnabled = policy.Enabled;
                ProductionReplayVolcanoRepeatCount = policy.RepeatCount;
                ProductionReplayVolcanoAudioEachCycle = policy.AudioOnEachCycle;
                break;
        }
    }

    private static AudioCueOptionViewModel? FindAudioCue(
        IEnumerable<AudioCueOptionViewModel> items,
        AudioCueId cue) => items.FirstOrDefault(item => item.Cue == cue);

    private static ObservableCollection<AudioCueOptionViewModel> CreateQuakeAudioCues(
        AudioSettings audio)
    {
        (JmaScale Scale, AudioCueId Cue, string Label)[] definitions =
        [
            (JmaScale.One, AudioCueId.QuakeIntensity1, "震度1"),
            (JmaScale.Two, AudioCueId.QuakeIntensity2, "震度2"),
            (JmaScale.Three, AudioCueId.QuakeIntensity3, "震度3"),
            (JmaScale.Four, AudioCueId.QuakeIntensity4, "震度4"),
            (JmaScale.FiveLower, AudioCueId.QuakeIntensity5Lower, "震度5弱"),
            (JmaScale.FiveUpper, AudioCueId.QuakeIntensity5Upper, "震度5強"),
            (JmaScale.SixLower, AudioCueId.QuakeIntensity6Lower, "震度6弱"),
            (JmaScale.SixUpper, AudioCueId.QuakeIntensity6Upper, "震度6強"),
            (JmaScale.Seven, AudioCueId.QuakeIntensity7, "震度7"),
        ];
        var result = new ObservableCollection<AudioCueOptionViewModel>();
        foreach ((JmaScale scale, AudioCueId cue, string label) in definitions)
        {
            AudioCueSetting? configured = null;
            bool hasIndividual = audio.QuakeScaleCues is { } quakeCues &&
                quakeCues.TryGetValue(scale, out configured);
            bool enabled = hasIndividual
                ? configured!.Enabled
                : audio.QuakeEnabled && (int)scale >= (int)audio.MinimumQuakeScale;
            string path = hasIndividual
                ? configured!.FilePath ?? string.Empty
                : audio.QuakeFilePath ?? string.Empty;
            result.Add(new AudioCueOptionViewModel(
                cue, label, enabled, path, scale, null));
        }

        return result;
    }

    private static ObservableCollection<AudioCueOptionViewModel> CreateTsunamiAudioCues(
        AudioSettings audio)
    {
        (TsunamiGrade Grade, AudioCueId Cue, string Label)[] definitions =
        [
            (TsunamiGrade.Forecast, AudioCueId.TsunamiForecast, "津波予報（若干の海面変動）"),
            (TsunamiGrade.Watch, AudioCueId.TsunamiAdvisory, "津波注意報"),
            (TsunamiGrade.Warning, AudioCueId.TsunamiWarning, "津波警報"),
            (TsunamiGrade.MajorWarning, AudioCueId.TsunamiMajorWarning, "大津波警報"),
        ];
        var result = new ObservableCollection<AudioCueOptionViewModel>();
        foreach ((TsunamiGrade grade, AudioCueId cue, string label) in definitions)
        {
            AudioCueSetting? configured = null;
            bool hasIndividual = audio.TsunamiGradeCues is { } tsunamiCues &&
                tsunamiCues.TryGetValue(grade, out configured);
            bool enabled = hasIndividual
                ? configured!.Enabled
                : grade switch
                {
                    TsunamiGrade.Watch => audio.TsunamiAdvisoryEnabled,
                    TsunamiGrade.Warning => audio.TsunamiWarningEnabled,
                    TsunamiGrade.MajorWarning => audio.TsunamiMajorWarningEnabled,
                    _ => false,
                };
            string path = hasIndividual
                ? configured!.FilePath ?? string.Empty
                : grade switch
                {
                    TsunamiGrade.Watch => audio.TsunamiAdvisoryFilePath,
                    TsunamiGrade.Warning => audio.TsunamiWarningFilePath,
                    TsunamiGrade.MajorWarning => audio.TsunamiMajorWarningFilePath,
                    _ => audio.TsunamiFilePath,
                } ?? string.Empty;
            result.Add(new AudioCueOptionViewModel(
                cue, label, enabled, path, null, grade));
        }

        return result;
    }

    private static JmaScale NormalizeMinimumQuakeScale(JmaScale scale) => scale is
        JmaScale.Three or
        JmaScale.Four or
        JmaScale.FiveLower or
        JmaScale.FiveUpper or
        JmaScale.SixLower or
        JmaScale.SixUpper or
        JmaScale.Seven
            ? scale
            : JmaScale.Three;

    private ProviderRoutingSettings CurrentRouting => new(
        EewProvider,
        QuakeProvider,
        TsunamiProvider,
        WeatherProvider,
        VolcanoProvider,
        NankaiTroughProvider);

    private void SetRouteProvider(
        ref ReceptionProvider field,
        ReceptionProvider value,
        string propertyName)
    {
        ReceptionProvider normalized = NormalizeReceptionProvider(value);
        if (!SetProperty(ref field, normalized, propertyName))
        {
            return;
        }

        _receptionProvider = CurrentRouting.GetCompatibilityProvider();
        RaiseProviderSelectionProperties();
    }

    private void ApplyRouting(ProviderRoutingSettings routing)
    {
        ArgumentNullException.ThrowIfNull(routing);
        EewProvider = routing.Eew;
        QuakeProvider = routing.Quake;
        TsunamiProvider = routing.Tsunami;
        WeatherProvider = routing.Weather;
        VolcanoProvider = routing.Volcano;
        NankaiTroughProvider = routing.NankaiTrough;
        _receptionProvider = CurrentRouting.GetCompatibilityProvider();
        RaiseProviderSelectionProperties();
    }

    private void RaiseProviderSelectionProperties()
    {
        AxisChannel = GetAxisChannel(CurrentRouting);
        OnPropertyChanged(nameof(ReceptionProvider));
        OnPropertyChanged(nameof(IsP2pProvider));
        OnPropertyChanged(nameof(IsDmdataProvider));
        OnPropertyChanged(nameof(IsAxisProvider));
        OnPropertyChanged(nameof(IsCustomProvider));
    }

    private static List<ReceptionProviderOption> CreateReceptionProviderOptions(
        bool includeP2p)
    {
        var options = new List<ReceptionProviderOption>(4)
        {
            new(ReceptionProvider.Disabled, "受信しない"),
        };
        if (includeP2p)
        {
            options.Add(new ReceptionProviderOption(ReceptionProvider.P2pQuake, "P2P"));
        }

        if (BuildFeatures.AxisProviderEnabled)
        {
            options.Add(new ReceptionProviderOption(ReceptionProvider.Axis, "AXIS"));
        }

        if (BuildFeatures.DmdataProviderEnabled)
        {
            options.Add(new ReceptionProviderOption(ReceptionProvider.Dmdata, "DMDATA.JP"));
        }

        return options;
    }

    private static ReceptionProvider NormalizeReceptionProvider(ReceptionProvider provider) =>
        provider switch
        {
            ReceptionProvider.Disabled => ReceptionProvider.Disabled,
            ReceptionProvider.Dmdata when BuildFeatures.DmdataProviderEnabled =>
                ReceptionProvider.Dmdata,
            ReceptionProvider.Axis when BuildFeatures.AxisProviderEnabled =>
                ReceptionProvider.Axis,
            _ => ReceptionProvider.P2pQuake,
        };

    private static ObsAudioMonitoringMode NormalizeObsAudioMonitoringMode(
        ObsAudioMonitoringMode _) => ObsAudioMonitoringMode.Off;
}

public sealed record HistoryApiOption(HistoryApi Value, string Label);

public sealed record NiiHistoryContentOption(NiiHistoryContent Value, string Label);

public sealed record DmdataEewContractTypeOption(
    DmdataEewContractType Value,
    string Label);

public sealed record QuakeAudioThresholdOption(JmaScale Value, string Label);

public sealed record ObsAudioMonitoringModeOption(
    ObsAudioMonitoringMode Value,
    string Label);

public sealed record ReceptionProviderOption(
    ReceptionProvider Value,
    string Label);
