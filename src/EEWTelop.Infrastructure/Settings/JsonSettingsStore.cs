using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Infrastructure.Persistence;

namespace EEWTelop.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    public static AppSettings NormalizeDocument(AppSettings settings) =>
        Validate(Migrate(settings));

    public static (AppSettings Settings, int SourceSchemaVersion) ReadAndNormalizeDocument(
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        AppSettings? source = JsonSerializer.Deserialize<AppSettings>(
            stream,
            JsonFileOptions.Create());
        int sourceSchemaVersion = source?.SchemaVersion ?? 0;
        return (Validate(Migrate(source)), sourceSchemaVersion);
    }

    private const string DefaultAxisChannels =
        "jmx-seismology,jmx-meteorology,jmx-volcanology,eew";
    private readonly string _path;
    private readonly IAppLogWriter _log;
    private readonly JsonSerializerOptions _json = JsonFileOptions.Create();

    public JsonSettingsStore(string path, IAppLogWriter log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);
        _path = Path.GetFullPath(path);
        _log = log;
    }

    public async ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return AppSettings.CreateDefault();
        }

        try
        {
            AppSettings? settings;
            await using (FileStream stream = File.OpenRead(_path))
            {
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream,
                    _json,
                    cancellationToken).ConfigureAwait(false);
            }

            int sourceSchemaVersion = settings?.SchemaVersion ?? 0;
            AppSettings migrated = Migrate(settings);
            AppSettings validated = Validate(migrated);
            if (sourceSchemaVersion != AppSettings.CurrentSchemaVersion)
            {
                try
                {
                    await AtomicFileWriter.WriteAsync(
                        _path,
                        (output, token) => JsonSerializer.SerializeAsync(
                            output,
                            validated,
                            _json,
                            token),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    await _log.WriteAsync(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppLogLevel.Warning,
                        "SettingsMigrationSaveFailed",
                        "移行した設定を保存できませんでした。今回の起動では移行後の設定を使用します。",
                        exception), cancellationToken).ConfigureAwait(false);
                }

                await _log.WriteAsync(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppLogLevel.Information,
                    "SettingsMigrated",
                    $"設定ファイルをスキーマ {sourceSchemaVersion} から {AppSettings.CurrentSchemaVersion} へ移行しました。"),
                    cancellationToken).ConfigureAwait(false);
            }

            return validated;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            string backup = AtomicFileWriter.MoveAsideCorruptFile(_path, DateTimeOffset.Now);
            await _log.WriteAsync(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppLogLevel.Warning,
                "SettingsRecovered",
                $"設定JSONが不正なため既定値で起動しました。退避先: {Path.GetFileName(backup)}",
                exception), cancellationToken).ConfigureAwait(false);
            return AppSettings.CreateDefault();
        }
    }

    public ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings validated = Validate(settings);
        return new ValueTask(AtomicFileWriter.WriteAsync(
            _path,
            (stream, token) => JsonSerializer.SerializeAsync(
                stream,
                validated,
                _json,
                token),
            cancellationToken));
    }

    private static AppSettings Validate(AppSettings? settings)
    {
        if (settings is null || settings.SchemaVersion != AppSettings.CurrentSchemaVersion ||
            settings.Provider is null || settings.Filter is null || settings.Display is null ||
            settings.Provider.Routing is null ||
            settings.Obs is null || settings.Audio is null || settings.History is null ||
            settings.Compatibility is null || settings.Log is null || settings.Safety is null ||
            settings.Operations is null ||
            settings.Display.PageDurationSeconds is < 1 or > 30 ||
            settings.Display.AutoHideSeconds is < 0 or > 3600 ||
            settings.Display.EewAutoHideSeconds is < -1 or > 3600 ||
            settings.Display.QuakeAutoHideSeconds is < -1 or > 3600 ||
            settings.Display.TsunamiAutoHideSeconds is < -1 or > 3600 ||
            settings.Display.WeatherWarningAutoHideSeconds is < -1 or > 3600 ||
            settings.Display.ProductionReplay is null ||
            settings.Display.ProductionReplay.RotationIntervalSeconds is < 1 or > 300 ||
            settings.Display.ProductionReplay.ResumeDelaySeconds is < 0 or > 300 ||
            !IsValidProductionReplayPolicy(settings.Display.ProductionReplay.Eew) ||
            !IsValidProductionReplayPolicy(settings.Display.ProductionReplay.Quake) ||
            !IsValidProductionReplayPolicy(settings.Display.ProductionReplay.Tsunami) ||
            !IsValidProductionReplayPolicy(settings.Display.ProductionReplay.WeatherWarning) ||
            !IsValidProductionReplayPolicy(settings.Display.ProductionReplay.Volcano) ||
            settings.Display.Width is < 320 or > 7680 ||
            settings.Display.Height is < 180 or > 4320 ||
            settings.Obs.Port is < 0 or > 65535 ||
            settings.Obs.SnapshotIntervalMilliseconds is
                < ObsSettings.MinimumSnapshotIntervalMilliseconds or
                > ObsSettings.MaximumSnapshotIntervalMilliseconds ||
            !Uri.TryCreate(settings.Obs.WebSocketUrl, UriKind.Absolute, out Uri? obsWebSocket) ||
            obsWebSocket.Scheme is not ("ws" or "wss") ||
            settings.History.Limit is < 1 or > 100 ||
            settings.History.IntervalSeconds < 1 ||
            settings.History.NiiDate < new DateOnly(2012, 12, 1) ||
            settings.History.NiiDate > DateOnly.FromDateTime(DateTime.Today) ||
            !Enum.IsDefined(settings.History.Api) ||
            !Enum.IsDefined(settings.History.NiiContent) ||
            !Enum.IsDefined(settings.Provider.DmdataEewContractType) ||
            settings.Log.RawMessageRetentionDays is < 1 or > 90 ||
            settings.Log.RawMessageMaximumTotalMegabytes is < 32 or > 4096 ||
            settings.Operations.TimelineRetentionDays is < 1 or > 90 ||
            settings.Operations.TimelineMaximumTotalMegabytes is < 32 or > 4096 ||
            settings.Operations.TimelineUiMaximumEntries is < 100 or > 10000 ||
            settings.Operations.AlertCoalescingSeconds is < 10 or > 3600 ||
            settings.Operations.SourceComparisonWaitSeconds is < 10 or > 3600)
        {
            throw new InvalidDataException("The settings document failed schema or range validation.");
        }

        DisplaySettings display = settings.Display with
        {
            LetterSpacingEm = 0,
            LineSpacing = 1,
            FontScale = 1,
            BackgroundMode = BackgroundMode.Transparent,
            OutputTransform = OutputTransformSettings.Default,
            EewAutoHideSeconds = settings.Display.EewAutoHideSeconds < 0
                ? settings.Display.AutoHideSeconds
                : settings.Display.EewAutoHideSeconds,
            QuakeAutoHideSeconds = settings.Display.QuakeAutoHideSeconds < 0
                ? settings.Display.AutoHideSeconds
                : settings.Display.QuakeAutoHideSeconds,
            TsunamiAutoHideSeconds = settings.Display.TsunamiAutoHideSeconds < 0
                ? settings.Display.AutoHideSeconds
                : settings.Display.TsunamiAutoHideSeconds,
            WeatherWarningAutoHideSeconds = settings.Display.WeatherWarningAutoHideSeconds < 0
                ? settings.Display.AutoHideSeconds
                : settings.Display.WeatherWarningAutoHideSeconds,
            ProductionReplay = settings.Display.ProductionReplay,
            SubtitlePhraseOverrides = NormalizeSubtitlePhraseOverrides(
                settings.Display.SubtitlePhraseOverrides),
        };
        bool useSandbox = settings.Provider.Mode == ProviderMode.Sandbox;
        ProviderRoutingSettings routing = settings.Provider.Routing;
        if (settings.Provider.ReceptionProvider != routing.GetCompatibilityProvider())
        {
            // Compatibility for callers and hand-edited documents that still
            // set only the pre-schema-24 single-provider property.
            routing = ProviderRoutingSettings.FromLegacy(
                settings.Provider.ReceptionProvider);
        }
        if (!IsValidProviderRouting(routing))
        {
            throw new InvalidDataException("The information provider routing is invalid.");
        }

        if (routing.Uses(ReceptionProvider.Axis) &&
            (!Uri.TryCreate(settings.Provider.AxisApiBaseUrl, UriKind.Absolute, out Uri? axisApi) ||
             axisApi.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException("The AXIS provider settings are invalid.");
        }
        if (routing.Uses(ReceptionProvider.Dmdata) &&
            (!Uri.TryCreate(settings.Provider.DmdataApiBaseUrl, UriKind.Absolute, out Uri? dmdataApi) ||
             dmdataApi.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException("The DMDATA.JP provider settings are invalid.");
        }
        if (!IsSecureWebSocket(settings.Provider.WolfxEewWebSocketUrl) ||
            !IsSecureWebSocket(settings.Provider.WolfxQuakeWebSocketUrl))
        {
            throw new InvalidDataException("The Wolfx provider settings are invalid.");
        }
        string selectedAxisChannels = BuildAxisChannels(routing);
        ProviderSettings provider = settings.Provider with
        {
            Routing = routing,
            ReceptionProvider = routing.GetCompatibilityProvider(),
            Mode = routing.Uses(ReceptionProvider.Axis) ||
                routing.Uses(ReceptionProvider.Dmdata)
                ? ProviderMode.Production
                : useSandbox ? ProviderMode.Sandbox : ProviderMode.Production,
            WebSocketUrl = useSandbox
                ? "wss://api-realtime-sandbox.p2pquake.net/v2/ws"
                : "wss://api.p2pquake.net/v2/ws",
            RestBaseUrl = useSandbox
                ? "https://api-v2-sandbox.p2pquake.net/v2"
                : "https://api.p2pquake.net/v2",
            DmdataReceiveEewWarnings = routing.Eew == ReceptionProvider.Dmdata,
            DmdataReceiveEarthquakeTelegrams =
                routing.Quake == ReceptionProvider.Dmdata ||
                routing.Tsunami == ReceptionProvider.Dmdata ||
                routing.NankaiTrough == ReceptionProvider.Dmdata,
            DmdataReceiveWeatherWarnings =
                routing.Weather == ReceptionProvider.Dmdata,
            DmdataReceiveVolcanoTelegrams =
                routing.Volcano == ReceptionProvider.Dmdata,
            DmdataUseLegacyWeatherWarningTelegrams =
                routing.Weather == ReceptionProvider.Dmdata &&
                settings.Provider.DmdataUseLegacyWeatherWarningTelegrams,
            AxisChannel = string.IsNullOrWhiteSpace(selectedAxisChannels)
                ? NormalizeAxisChannels(settings.Provider.AxisChannel)
                : selectedAxisChannels,
        };
        AudioSettings audio = NormalizeIndividualAudioCues(MigrateTsunamiAudio(settings.Audio)) with
        {
            MinimumQuakeScale = IsSupportedQuakeAudioThreshold(
                settings.Audio.MinimumQuakeScale)
                    ? settings.Audio.MinimumQuakeScale
                    : JmaScale.Three,
            MinimumTsunamiGrade = IsSupportedTsunamiAudioThreshold(
                settings.Audio.MinimumTsunamiGrade)
                    ? settings.Audio.MinimumTsunamiGrade
                    : TsunamiGrade.Watch,
        };
        ObsSettings obs = settings.Obs with
        {
            // PC側へ音声を返さないよう、旧設定もOBS出力のみに移行する。
            AudioMonitoringMode = ObsAudioMonitoringMode.Off,
        };
        string[] weatherPrefectureCodes =
            WeatherPrefectureCatalog.NormalizeCodes(settings.Filter.WeatherPrefectureCodes);
        if (weatherPrefectureCodes.Length == 0 &&
            !string.IsNullOrWhiteSpace(settings.Filter.WeatherPrefectureCode) &&
            WeatherPrefectureCatalog.IsSupported(settings.Filter.WeatherPrefectureCode))
        {
            weatherPrefectureCodes = [settings.Filter.WeatherPrefectureCode];
        }

        FilterSettings filter = settings.Filter with
        {
            WeatherPrefectureCodes = weatherPrefectureCodes,
            WeatherPrefectureCode = weatherPrefectureCodes.Length == 1
                ? weatherPrefectureCodes[0]
                : string.Empty,
        };
        return settings with
        {
            Provider = provider,
            Filter = filter,
            Display = display,
            Obs = obs,
            Audio = audio,
        };
    }

    private static bool IsSupportedQuakeAudioThreshold(JmaScale scale) => scale is
        JmaScale.Three or
        JmaScale.Four or
        JmaScale.FiveLower or
        JmaScale.FiveUpper or
        JmaScale.SixLower or
        JmaScale.SixUpper or
        JmaScale.Seven;

    private static bool IsSecureWebSocket(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == Uri.UriSchemeWss;

    private static Dictionary<string, string> NormalizeSubtitlePhraseOverrides(
        Dictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return [];
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SubtitlePhraseDefinition definition in SubtitlePhraseCatalog.All)
        {
            if (overrides.TryGetValue(definition.Id, out string? value))
            {
                normalized[definition.Id] = (value ?? string.Empty)[..Math.Min(
                    value?.Length ?? 0,
                    500)];
            }
        }

        return normalized;
    }

    private static bool IsSupportedTsunamiAudioThreshold(TsunamiGrade grade) => grade is
        TsunamiGrade.Watch or TsunamiGrade.Warning or TsunamiGrade.MajorWarning;

    private static AudioSettings MigrateTsunamiAudio(AudioSettings audio)
    {
        bool splitConfigured = audio.TsunamiAdvisoryEnabled ||
            audio.TsunamiWarningEnabled ||
            audio.TsunamiMajorWarningEnabled ||
            !string.IsNullOrWhiteSpace(audio.TsunamiAdvisoryFilePath) ||
            !string.IsNullOrWhiteSpace(audio.TsunamiWarningFilePath) ||
            !string.IsNullOrWhiteSpace(audio.TsunamiMajorWarningFilePath);
        if (splitConfigured || !audio.TsunamiEnabled)
        {
            return audio;
        }

        TsunamiGrade minimum = IsSupportedTsunamiAudioThreshold(audio.MinimumTsunamiGrade)
            ? audio.MinimumTsunamiGrade
            : TsunamiGrade.Watch;
        return audio with
        {
            TsunamiAdvisoryEnabled = minimum == TsunamiGrade.Watch,
            TsunamiWarningEnabled = minimum is TsunamiGrade.Watch or TsunamiGrade.Warning,
            TsunamiMajorWarningEnabled = true,
            TsunamiAdvisoryFilePath = audio.TsunamiFilePath,
            TsunamiWarningFilePath = audio.TsunamiFilePath,
            TsunamiMajorWarningFilePath = audio.TsunamiFilePath,
        };
    }

    private static AudioSettings NormalizeIndividualAudioCues(AudioSettings audio)
    {
        Dictionary<JmaScale, AudioCueSetting> quake = NormalizeQuakeCues(
            audio.QuakeScaleCues);
        if (quake.Count == 0 && audio.QuakeEnabled)
        {
            JmaScale minimum = IsSupportedQuakeAudioThreshold(audio.MinimumQuakeScale)
                ? audio.MinimumQuakeScale
                : JmaScale.Three;
            foreach (JmaScale scale in SupportedQuakeCueScales)
            {
                quake[scale] = new AudioCueSetting(
                    (int)scale >= (int)minimum,
                    audio.QuakeFilePath ?? string.Empty);
            }
        }

        Dictionary<TsunamiGrade, AudioCueSetting> tsunami = NormalizeTsunamiCues(
            audio.TsunamiGradeCues);
        if (tsunami.Count == 0 &&
            (audio.TsunamiEnabled || audio.TsunamiAdvisoryEnabled ||
             audio.TsunamiWarningEnabled || audio.TsunamiMajorWarningEnabled))
        {
            tsunami[TsunamiGrade.Forecast] = new AudioCueSetting(
                false,
                audio.TsunamiFilePath ?? string.Empty);
            tsunami[TsunamiGrade.Watch] = new AudioCueSetting(
                audio.TsunamiAdvisoryEnabled,
                audio.TsunamiAdvisoryFilePath ?? string.Empty);
            tsunami[TsunamiGrade.Warning] = new AudioCueSetting(
                audio.TsunamiWarningEnabled,
                audio.TsunamiWarningFilePath ?? string.Empty);
            tsunami[TsunamiGrade.MajorWarning] = new AudioCueSetting(
                audio.TsunamiMajorWarningEnabled,
                audio.TsunamiMajorWarningFilePath ?? string.Empty);
        }

        return audio with
        {
            QuakeScaleCues = quake,
            TsunamiGradeCues = tsunami,
        };
    }

    private static Dictionary<JmaScale, AudioCueSetting> NormalizeQuakeCues(
        Dictionary<JmaScale, AudioCueSetting>? source)
    {
        var result = new Dictionary<JmaScale, AudioCueSetting>();
        if (source is null)
        {
            return result;
        }

        foreach (JmaScale scale in SupportedQuakeCueScales)
        {
            if (source.TryGetValue(scale, out AudioCueSetting? cue) && cue is not null)
            {
                result[scale] = new AudioCueSetting(
                    cue.Enabled,
                    (cue.FilePath ?? string.Empty).Trim());
            }
        }

        return result;
    }

    private static Dictionary<TsunamiGrade, AudioCueSetting> NormalizeTsunamiCues(
        Dictionary<TsunamiGrade, AudioCueSetting>? source)
    {
        var result = new Dictionary<TsunamiGrade, AudioCueSetting>();
        if (source is null)
        {
            return result;
        }

        foreach (TsunamiGrade grade in SupportedTsunamiCueGrades)
        {
            if (source.TryGetValue(grade, out AudioCueSetting? cue) && cue is not null)
            {
                result[grade] = new AudioCueSetting(
                    cue.Enabled,
                    (cue.FilePath ?? string.Empty).Trim());
            }
        }

        return result;
    }

    private static readonly JmaScale[] SupportedQuakeCueScales =
    [
        JmaScale.One, JmaScale.Two, JmaScale.Three, JmaScale.Four,
        JmaScale.FiveLower, JmaScale.FiveUpper, JmaScale.SixLower,
        JmaScale.SixUpper, JmaScale.Seven,
    ];

    private static readonly TsunamiGrade[] SupportedTsunamiCueGrades =
    [
        TsunamiGrade.Forecast, TsunamiGrade.Watch, TsunamiGrade.Warning,
        TsunamiGrade.MajorWarning,
    ];

    private static AppSettings Migrate(AppSettings? settings)
    {
        if (settings is null)
        {
            throw new InvalidDataException("The settings document is empty.");
        }

        AppSettings migrated = settings.SchemaVersion switch
        {
            AppSettings.CurrentSchemaVersion => settings,
            25 => settings with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
            },
            24 => settings with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
            },
            23 => settings with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Provider = settings.Provider with
                {
                    Routing = ProviderRoutingSettings.FromLegacy(
                        settings.Provider.ReceptionProvider),
                },
            },
            22 => settings with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Provider = settings.Provider with
                {
                    Routing = ProviderRoutingSettings.FromLegacy(
                        settings.Provider.ReceptionProvider),
                },
            },
            21 => settings with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Provider = settings.Provider with
                {
                    Routing = ProviderRoutingSettings.FromLegacy(
                        settings.Provider.ReceptionProvider),
                },
            },
            20 => settings with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Provider = settings.Provider with
                {
                    Routing = ProviderRoutingSettings.FromLegacy(
                        settings.Provider.ReceptionProvider),
                },
                Operations = OperationalSettings.Default,
            },
            19 => settings with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Provider = settings.Provider with
                {
                    Routing = ProviderRoutingSettings.FromLegacy(
                        settings.Provider.ReceptionProvider),
                },
                Operations = OperationalSettings.Default,
            },
            18 => settings with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Provider = settings.Provider with
                {
                    Routing = ProviderRoutingSettings.FromLegacy(
                        settings.Provider.ReceptionProvider),
                },
                Display = settings.Display with
                {
                    ProductionReplay = MigrateProductionReplay(
                        settings.Display.ProductionReplay),
                },
                Operations = OperationalSettings.Default,
            },
            1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 => settings with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Provider = settings.Provider with
                {
                    AxisChannel = DefaultAxisChannels,
                    Routing = ProviderRoutingSettings.FromLegacy(
                        settings.Provider.ReceptionProvider),
                },
                Display = settings.Display with
                {
                    ProductionReplay = ProductionReplaySettings.Default,
                },
                Operations = OperationalSettings.Default,
            },
            _ => throw new InvalidDataException(
                $"Unsupported settings schema version: {settings.SchemaVersion}."),
        };

        // Schema 25 and earlier always requested eew.forecast/VXSE45. Preserve
        // that contract choice during migration; only new installations default
        // to eew.warning/VXSE43.
        return settings.SchemaVersion < AppSettings.CurrentSchemaVersion
            ? migrated with
            {
                Provider = migrated.Provider with
                {
                    DmdataEewContractType = DmdataEewContractType.Forecast,
                },
            }
            : migrated;
    }

    private static string NormalizeAxisChannels(string? channels)
    {
        string[] normalized = (string.IsNullOrWhiteSpace(channels)
                ? DefaultAxisChannels
                : channels)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Contains("eew", StringComparer.OrdinalIgnoreCase)
            ? string.Join(",", normalized)
            : string.Join(",", normalized.Append("eew"));
    }

    private static string BuildAxisChannels(ProviderRoutingSettings routing)
    {
        var channels = new List<string>(4);
        if (routing.Quake == ReceptionProvider.Axis ||
            routing.Tsunami == ReceptionProvider.Axis ||
            routing.NankaiTrough == ReceptionProvider.Axis)
        {
            channels.Add("jmx-seismology");
        }
        if (routing.Weather == ReceptionProvider.Axis)
        {
            channels.Add("jmx-meteorology");
        }
        if (routing.Volcano == ReceptionProvider.Axis)
        {
            channels.Add("jmx-volcanology");
        }
        if (routing.Eew == ReceptionProvider.Axis)
        {
            channels.Add("eew");
        }

        return string.Join(",", channels);
    }

    private static bool IsValidProviderRouting(ProviderRoutingSettings routing) =>
        Enum.IsDefined(routing.Eew) &&
        Enum.IsDefined(routing.Quake) &&
        Enum.IsDefined(routing.Tsunami) &&
        Enum.IsDefined(routing.Weather) &&
        Enum.IsDefined(routing.Volcano) &&
        Enum.IsDefined(routing.NankaiTrough) &&
        routing.Tsunami != ReceptionProvider.Wolfx &&
        routing.Weather != ReceptionProvider.Wolfx &&
        routing.Volcano != ReceptionProvider.Wolfx &&
        routing.NankaiTrough != ReceptionProvider.Wolfx;

    private static bool IsValidProductionReplayPolicy(ProductionReplayPolicy? policy) =>
        policy is not null && policy.RepeatCount is >= 1 and <= 100;

    private static ProductionReplaySettings MigrateProductionReplay(
        ProductionReplaySettings? settings)
    {
        if (settings is null)
        {
            return ProductionReplaySettings.Default;
        }

        int interval = Math.Clamp(settings.RotationIntervalSeconds, 1, 300);
        return settings with
        {
            Eew = MigrateProductionReplayPolicy(settings.Eew, interval, 3),
            Quake = MigrateProductionReplayPolicy(settings.Quake, interval, 3),
            Tsunami = MigrateProductionReplayPolicy(settings.Tsunami, interval, 10),
            WeatherWarning = MigrateProductionReplayPolicy(settings.WeatherWarning, interval, 5),
            Volcano = MigrateProductionReplayPolicy(settings.Volcano, interval, 5),
        };
    }

    private static ProductionReplayPolicy MigrateProductionReplayPolicy(
        ProductionReplayPolicy? policy,
        int intervalSeconds,
        int fallbackCount)
    {
        if (policy is null)
        {
            return ProductionReplayPolicy.Disabled(fallbackCount);
        }

        int repeatCount = policy.DurationSeconds switch
        {
            > 0 => (int)Math.Ceiling(policy.DurationSeconds.Value / (double)intervalSeconds),
            0 when policy.Enabled => fallbackCount,
            _ when policy.RepeatCount > 0 => policy.RepeatCount,
            _ => fallbackCount,
        };
        return new ProductionReplayPolicy(
            policy.Enabled,
            Math.Clamp(repeatCount, 1, 100),
            policy.AudioOnEachCycle);
    }
}
