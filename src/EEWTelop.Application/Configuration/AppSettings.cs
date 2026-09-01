using System.Text.Json.Serialization;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Configuration;

public sealed record AppSettings(
    int SchemaVersion,
    ProviderSettings Provider,
    FilterSettings Filter,
    DisplaySettings Display,
    ObsSettings Obs,
    AudioSettings Audio,
    HistorySettings History,
    CompatibilitySettings Compatibility,
    LogSettings Log,
    SafetySettings Safety)
{
    public const int CurrentSchemaVersion = 26;

    public OperationalSettings Operations { get; init; } = OperationalSettings.Default;

    public static AppSettings CreateDefault() => new(
        SchemaVersion: CurrentSchemaVersion,
        Provider: new ProviderSettings(
            Mode: ProviderMode.Production,
            WebSocketUrl: "wss://api.p2pquake.net/v2/ws",
            RestBaseUrl: "https://api.p2pquake.net/v2")
        {
            ReceptionProvider = ReceptionProvider.P2pQuake,
        },
        Filter: new FilterSettings(
            Eew: true,
            Quake: true,
            Tsunami: true,
            HideQuakeBelowIntensity3: false)
        {
            WeatherWarning = true,
            WeatherSpecialWarnings = true,
            WeatherWarnings = true,
            WeatherAdvisories = false,
            WeatherTornadoAdvisories = true,
            WeatherRecordShortRain = true,
            WeatherDisasterPreventionBulletins = true,
            Volcano = true,
        },
        Display: new DisplaySettings(
            PageDurationSeconds: 4.0,
            ShowPageIndicator: true,
            LetterSpacingEm: 0.0,
            LineSpacing: 1.0,
            FontScale: 1.0,
            AutoHideSeconds: 45,
            BackgroundMode: BackgroundMode.Transparent,
            Width: 1920,
            Height: 1080)
        {
            EewAutoHideSeconds = 45,
            QuakeAutoHideSeconds = 45,
            TsunamiAutoHideSeconds = 45,
            WeatherWarningAutoHideSeconds = 45,
            ShowTsunamiForecast = false,
            OutputTransform = OutputTransformSettings.Default,
            ProductionReplay = ProductionReplaySettings.Default,
        },
        Obs: new ObsSettings(
            Enabled: true,
            Port: 0,
            RuntimeRecovery: true)
        {
            BrowserSourceSyncEnabled = false,
            WebSocketUrl = "ws://127.0.0.1:4455",
        },
        Audio: AudioSettings.Disabled,
        History: new HistorySettings(
            Api: HistoryApi.JmaQuake,
            Limit: 5,
            IntervalSeconds: 3)
        {
            NiiDate = DateOnly.FromDateTime(DateTime.Today),
            NiiContent = NiiHistoryContent.QuakeAndTsunami,
        },
        Compatibility: new CompatibilitySettings(
            EnrichQuakeById: false),
        Log: new LogSettings(
            UiMaxEntries: 250)
        {
            SaveRawProviderMessages = false,
            RawMessageRetentionDays = 7,
            RawMessageMaximumTotalMegabytes = 256,
        },
        Safety: new SafetySettings(
            ConfirmTestInProduction: true,
            RestoreRehearsalState: false));
}

public sealed record ProviderSettings(
    ProviderMode Mode,
    string WebSocketUrl,
    string RestBaseUrl)
{
    public ReceptionProvider ReceptionProvider { get; init; } = ReceptionProvider.P2pQuake;

    public ProviderRoutingSettings Routing { get; init; } = ProviderRoutingSettings.Default;

    public string DmdataApiBaseUrl { get; init; } = "https://api.dmdata.jp/v2";

    // Migration fallback only. New credentials are entered in the UI and stored
    // in DmdataProtectedCredential using Windows DPAPI for CurrentUser.
    public string DmdataCredentialEnvironmentVariable { get; init; } =
        "QTELOPPER_DMDATA_API_KEY";

    // The clear credential is never serialized. This value is a DPAPI-protected
    // payload that can be decrypted only by the Windows user who saved it.
    public string DmdataProtectedCredential { get; init; } = string.Empty;

    public DmdataAuthenticationMode DmdataAuthenticationMode { get; init; } =
        DmdataAuthenticationMode.ApiKey;

    public bool DmdataIncludeTestTelegrams { get; init; } = true;

    // DMDATA.JP sells the EEW warning and forecast classifications separately.
    // New installations use the public warning classification by default.
    public DmdataEewContractType DmdataEewContractType { get; init; } =
        DmdataEewContractType.Warning;

    // DMDATA.JP contract categories are independent. Keep uncontracted categories
    // out of Socket Start requests so a weather-only contract can connect.
    public bool DmdataReceiveEewWarnings { get; init; }

    public bool DmdataReceiveEarthquakeTelegrams { get; init; }

    public bool DmdataReceiveWeatherWarnings { get; init; }

    public bool DmdataReceiveVolcanoTelegrams { get; init; }

    // Do not request the legacy and reorganized warning telegrams together;
    // they describe overlapping states during the transition period.
    public bool DmdataUseLegacyWeatherWarningTelegrams { get; init; }

    public string AxisApiBaseUrl { get; init; } = "https://axis.prioris.jp/api/";

    // The AXIS JWT is encrypted with Windows DPAPI before it is serialized.
    public string AxisProtectedAccessToken { get; init; } = string.Empty;

    public string AxisChannel { get; init; } =
        "jmx-seismology,jmx-meteorology,jmx-volcanology,eew";

    // AXIS jmx-meteorology can deliver both the legacy aggregate warning
    // telegrams and the reorganized telegrams. Never consume both families.
    public bool AxisUseLegacyWeatherWarningTelegrams { get; init; }
}

public sealed record ProviderRoutingSettings(
    ReceptionProvider Eew,
    ReceptionProvider Quake,
    ReceptionProvider Tsunami,
    ReceptionProvider Weather,
    ReceptionProvider Volcano,
    ReceptionProvider NankaiTrough)
{
    public static ProviderRoutingSettings Default { get; } = new(
        Eew: ReceptionProvider.P2pQuake,
        Quake: ReceptionProvider.P2pQuake,
        Tsunami: ReceptionProvider.P2pQuake,
        Weather: ReceptionProvider.P2pQuake,
        Volcano: ReceptionProvider.P2pQuake,
        NankaiTrough: ReceptionProvider.P2pQuake);

    public static ProviderRoutingSettings AxisHybrid { get; } = new(
        Eew: ReceptionProvider.Axis,
        Quake: ReceptionProvider.P2pQuake,
        Tsunami: ReceptionProvider.P2pQuake,
        Weather: ReceptionProvider.Axis,
        Volcano: ReceptionProvider.Axis,
        NankaiTrough: ReceptionProvider.Axis);

    public static ProviderRoutingSettings FromLegacy(ReceptionProvider provider) =>
        provider switch
        {
            ReceptionProvider.Disabled => new ProviderRoutingSettings(
                ReceptionProvider.Disabled,
                ReceptionProvider.Disabled,
                ReceptionProvider.Disabled,
                ReceptionProvider.Disabled,
                ReceptionProvider.Disabled,
                ReceptionProvider.Disabled),
            ReceptionProvider.Axis => AxisHybrid,
            ReceptionProvider.Dmdata => new ProviderRoutingSettings(
                ReceptionProvider.Dmdata,
                ReceptionProvider.Dmdata,
                ReceptionProvider.Dmdata,
                ReceptionProvider.Dmdata,
                ReceptionProvider.Dmdata,
                ReceptionProvider.Dmdata),
            _ => new ProviderRoutingSettings(
                ReceptionProvider.P2pQuake,
                ReceptionProvider.P2pQuake,
                ReceptionProvider.P2pQuake,
                ReceptionProvider.P2pQuake,
                ReceptionProvider.P2pQuake,
                ReceptionProvider.P2pQuake),
        };

    public bool Uses(ReceptionProvider provider) =>
        Eew == provider || Quake == provider || Tsunami == provider ||
        Weather == provider || Volcano == provider || NankaiTrough == provider;

    public IReadOnlyList<ReceptionProvider> GetDistinctProviders() =>
        new[] { Eew, Quake, Tsunami, Weather, Volcano, NankaiTrough }
            .Where(static provider => provider != ReceptionProvider.Disabled)
            .Distinct()
            .ToArray();

    public ReceptionProvider GetCompatibilityProvider() =>
        Uses(ReceptionProvider.Axis)
            ? ReceptionProvider.Axis
            : Uses(ReceptionProvider.Dmdata)
                ? ReceptionProvider.Dmdata
                : Uses(ReceptionProvider.P2pQuake)
                    ? ReceptionProvider.P2pQuake
                    : ReceptionProvider.Disabled;

    public ReceptionProvider GetProvider(EventKind kind, bool isNankaiTrough = false) =>
        isNankaiTrough
            ? NankaiTrough
            : kind switch
            {
                EventKind.Eew => Eew,
                EventKind.Quake => Quake,
                EventKind.Tsunami => Tsunami,
                EventKind.WeatherWarning => Weather,
                EventKind.Volcano => Volcano,
                _ => Quake,
            };
}

public sealed record FilterSettings(
    bool Eew,
    bool Quake,
    bool Tsunami,
    bool HideQuakeBelowIntensity3 = false)
{
    public bool WeatherWarning { get; init; } = true;

    public string WeatherPrefectureCode { get; init; } = string.Empty;

    // Empty means nationwide. WeatherPrefectureCode is retained for migration
    // from settings written by QTelopper 2.0.0-beta.4 and earlier.
    public string[] WeatherPrefectureCodes { get; init; } = [];

    public bool WeatherSpecialWarnings { get; init; } = true;

    public bool WeatherWarnings { get; init; } = true;

    public bool WeatherAdvisories { get; init; }

    public bool WeatherTornadoAdvisories { get; init; } = true;

    public bool WeatherRecordShortRain { get; init; } = true;

    public bool WeatherDisasterPreventionBulletins { get; init; } = true;

    public bool Volcano { get; init; } = true;
}

public sealed record DisplaySettings(
    double PageDurationSeconds,
    bool ShowPageIndicator,
    double LetterSpacingEm,
    double LineSpacing,
    double FontScale,
    int AutoHideSeconds,
    BackgroundMode BackgroundMode,
    int Width,
    int Height)
{
    // Fixed phrases can be customized before telegrams are received. Keys are
    // stable catalog IDs; unknown keys are ignored by the display composer.
    public Dictionary<string, string> SubtitlePhraseOverrides { get; init; } = [];

    // -1 marks settings saved before the EEW-specific timer was introduced.
    // In that case EEW inherits the former common auto-hide value.
    public int EewAutoHideSeconds { get; init; } = -1;

    // -1 keeps settings from older releases compatible with the former common timer.
    public int QuakeAutoHideSeconds { get; init; } = -1;

    public int TsunamiAutoHideSeconds { get; init; } = -1;

    public int WeatherWarningAutoHideSeconds { get; init; } = -1;

    // The 0.2 m class is normally omitted so warnings and watches remain prominent.
    public bool ShowTsunamiForecast { get; init; }

    public OutputTransformSettings OutputTransform { get; init; } =
        OutputTransformSettings.Default;

    // Live information rotation is deliberately opt-in. Enabling it changes
    // what remains on-air after the first immediate display.
    public ProductionReplaySettings ProductionReplay { get; init; } =
        ProductionReplaySettings.Default;

    [JsonIgnore]
    public int EffectiveEewAutoHideSeconds =>
        EewAutoHideSeconds >= 0 ? EewAutoHideSeconds : AutoHideSeconds;

    [JsonIgnore]
    public int EffectiveQuakeAutoHideSeconds =>
        QuakeAutoHideSeconds >= 0 ? QuakeAutoHideSeconds : AutoHideSeconds;

    [JsonIgnore]
    public int EffectiveTsunamiAutoHideSeconds =>
        TsunamiAutoHideSeconds >= 0 ? TsunamiAutoHideSeconds : AutoHideSeconds;

    [JsonIgnore]
    public int EffectiveWeatherWarningAutoHideSeconds =>
        WeatherWarningAutoHideSeconds >= 0
            ? WeatherWarningAutoHideSeconds
            : AutoHideSeconds;
}

public sealed record ProductionReplayPolicy(
    bool Enabled,
    int RepeatCount,
    bool AudioOnEachCycle)
{
    // Schema 18 compatibility only. New settings never write this property.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationSeconds { get; init; }

    public static ProductionReplayPolicy Disabled(int repeatCount) =>
        new(false, repeatCount, false);
}

public sealed record ProductionReplaySettings(
    int RotationIntervalSeconds,
    int ResumeDelaySeconds,
    ProductionReplayPolicy Eew,
    ProductionReplayPolicy Quake,
    ProductionReplayPolicy Tsunami,
    ProductionReplayPolicy WeatherWarning,
    ProductionReplayPolicy Volcano)
{
    public static ProductionReplaySettings Default { get; } = new(
        RotationIntervalSeconds: 10,
        ResumeDelaySeconds: 5,
        Eew: ProductionReplayPolicy.Disabled(3),
        Quake: ProductionReplayPolicy.Disabled(3),
        Tsunami: ProductionReplayPolicy.Disabled(10),
        WeatherWarning: ProductionReplayPolicy.Disabled(5),
        Volcano: ProductionReplayPolicy.Disabled(5));

    public ProductionReplayPolicy GetPolicy(EventKind kind) => kind switch
    {
        // Old settings may still contain Enabled=true. EEW is intentionally
        // excluded from production rotation regardless of persisted values.
        EventKind.Eew => ProductionReplayPolicy.Disabled(Eew.RepeatCount),
        EventKind.Quake => Quake,
        EventKind.Tsunami => Tsunami,
        EventKind.WeatherWarning => WeatherWarning,
        EventKind.Volcano => Volcano,
        _ => ProductionReplayPolicy.Disabled(0),
    };
}

public sealed record OutputTransformSettings(
    double Scale,
    double OffsetX,
    double OffsetY,
    double CropLeft,
    double CropTop,
    double CropRight,
    double CropBottom)
{
    public static OutputTransformSettings Default { get; } = new(
        Scale: 1,
        OffsetX: 0,
        OffsetY: 0,
        CropLeft: 0,
        CropTop: 0,
        CropRight: 0,
        CropBottom: 0);
}

public sealed record ObsSettings(
    bool Enabled,
    int Port,
    bool RuntimeRecovery)
{
    public const int MinimumSnapshotIntervalMilliseconds = 50;

    public const int MaximumSnapshotIntervalMilliseconds = 1000;

    public const int DefaultSnapshotIntervalMilliseconds = 1000;

    public int SnapshotIntervalMilliseconds { get; init; } =
        DefaultSnapshotIntervalMilliseconds;

    public bool BrowserSourceSyncEnabled { get; init; }

    public string WebSocketUrl { get; init; } = "ws://127.0.0.1:4455";

    // The OBS password is encrypted for the current Windows user before serialization.
    public string WebSocketProtectedPassword { get; init; } = string.Empty;

    // Empty means the scene currently active in OBS when missing sources are created.
    public string TargetSceneName { get; init; } = string.Empty;

    // Browser audio is sent to OBS only. The legacy enum is retained so that
    // settings files written by older releases can still be deserialized.
    public ObsAudioMonitoringMode AudioMonitoringMode { get; init; } =
        ObsAudioMonitoringMode.Off;
}

public enum ObsAudioMonitoringMode
{
    Off = 0,
    MonitorOnly = 1,
    MonitorAndOutput = 2,
}

public sealed record AudioSettings(
    bool EewEnabled,
    bool TsunamiEnabled,
    bool TrainingEnabled,
    bool TestUsesProductionSound,
    bool Muted,
    bool QuakeEnabled = false,
    bool EewInitialEnabled = false,
    bool EewContinuationEnabled = false,
    bool EewCancellationEnabled = false,
    string QuakeFilePath = "",
    string TsunamiFilePath = "",
    string EewInitialFilePath = "",
    string EewContinuationFilePath = "",
    string EewCancellationFilePath = "",
    bool FileAudioConfigured = false)
{
    public const double DefaultWeatherCoalescingSeconds = 1.5;

    public const double MaximumWeatherCoalescingSeconds = 3.0;

    public JmaScale MinimumQuakeScale { get; init; } = JmaScale.Three;

    public TsunamiGrade MinimumTsunamiGrade { get; init; } = TsunamiGrade.Watch;

    public bool WeatherSpecialWarningEnabled { get; init; }

    public bool WeatherWarningEnabled { get; init; }

    public bool WeatherAdvisoryEnabled { get; init; }

    public string WeatherSpecialWarningFilePath { get; init; } = string.Empty;

    public string WeatherWarningFilePath { get; init; } = string.Empty;

    public string WeatherAdvisoryFilePath { get; init; } = string.Empty;

    // Weather telegrams for several warning levels can arrive within a fraction
    // of a second. Hold them briefly and play only the highest eligible cue.
    public double WeatherCoalescingSeconds { get; init; } =
        DefaultWeatherCoalescingSeconds;

    public double EffectiveWeatherCoalescingSeconds =>
        double.IsFinite(WeatherCoalescingSeconds)
            ? Math.Clamp(WeatherCoalescingSeconds, 0, MaximumWeatherCoalescingSeconds)
            : DefaultWeatherCoalescingSeconds;

    public bool TsunamiAdvisoryEnabled { get; init; }

    public bool TsunamiWarningEnabled { get; init; }

    public bool TsunamiMajorWarningEnabled { get; init; }

    public string TsunamiAdvisoryFilePath { get; init; } = string.Empty;

    public string TsunamiWarningFilePath { get; init; } = string.Empty;

    public string TsunamiMajorWarningFilePath { get; init; } = string.Empty;

    // Individual cue settings supersede the legacy minimum-threshold settings.
    // The legacy members above remain readable so existing settings can be migrated.
    public Dictionary<JmaScale, AudioCueSetting> QuakeScaleCues { get; init; } = [];

    public Dictionary<TsunamiGrade, AudioCueSetting> TsunamiGradeCues { get; init; } = [];

    public static AudioSettings Disabled { get; } = new(
        EewEnabled: false,
        TsunamiEnabled: false,
        TrainingEnabled: false,
        TestUsesProductionSound: true,
        Muted: false,
        FileAudioConfigured: true);
}

public sealed record AudioCueSetting(bool Enabled, string FilePath);

public sealed record HistorySettings(
    HistoryApi Api,
    int Limit,
    int IntervalSeconds)
{
    public DateOnly NiiDate { get; init; } = DateOnly.FromDateTime(DateTime.Today);

    public NiiHistoryContent NiiContent { get; init; } = NiiHistoryContent.QuakeAndTsunami;

    public string NiiReportUrl { get; init; } = string.Empty;

    public string LocalXmlFilePath { get; init; } = string.Empty;

    public bool Repeat { get; init; }
}

public sealed record CompatibilitySettings(
    bool EnrichQuakeById);

public sealed record LogSettings(
    int UiMaxEntries)
{
    // Raw provider data can contain operational details and can grow quickly.
    // It is therefore opt-in and always written under the application data directory.
    public bool SaveRawProviderMessages { get; init; }

    public int RawMessageRetentionDays { get; init; } = 7;

    public int RawMessageMaximumTotalMegabytes { get; init; } = 256;
}

public sealed record SafetySettings(
    bool ConfirmTestInProduction,
    bool RestoreRehearsalState);

public sealed record OperationalSettings(
    bool Enabled,
    int TimelineRetentionDays,
    int TimelineMaximumTotalMegabytes,
    int TimelineUiMaximumEntries,
    int AlertCoalescingSeconds,
    int SourceComparisonWaitSeconds)
{
    public static OperationalSettings Default { get; } = new(
        Enabled: true,
        TimelineRetentionDays: 7,
        TimelineMaximumTotalMegabytes: 256,
        TimelineUiMaximumEntries: 1000,
        AlertCoalescingSeconds: 60,
        SourceComparisonWaitSeconds: 600);
}

public enum ProviderMode
{
    Production = 0,
    Sandbox = 1,
    Custom = 2,
}

public enum ReceptionProvider
{
    P2pQuake = 0,
    Dmdata = 1,
    Axis = 2,
    Disabled = 3,
}

public enum DmdataAuthenticationMode
{
    ApiKey = 0,
    OAuthAccessToken = 1,
}

public enum DmdataEewContractType
{
    Warning = 0,
    Forecast = 1,
}

public enum BackgroundMode
{
    Transparent = 0,
    Green = 1,
    Blue = 2,
}

public enum HistoryApi
{
    JmaQuake = 0,
    History = 1,
    NiiJmaXml = 2,
    LocalJmaXml = 3,
}

public enum NiiHistoryContent
{
    QuakeAndTsunami = 0,
    QuakeOnly = 1,
    TsunamiOnly = 2,
    WeatherWarningsOnly = 3,
    AllSupported = 4,
    WeatherRain = 5,
    WeatherLandslide = 6,
    WeatherStormSurge = 7,
    WeatherStorm = 8,
    WeatherWave = 9,
    WeatherHeavySnow = 10,
    WeatherOtherAdvisories = 11,
}
