using System.IO.Compression;
using System.Text.Json.Nodes;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Diagnostics;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Persistence;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Diagnostics;
using EEWTelop.Infrastructure.Logging;
using EEWTelop.Infrastructure.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class Phase8PersistenceDiagnosticsTests
{
    private static readonly System.Text.Json.JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };
    private string _directory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"eewtelop-phase8-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SettingsRoundTripUsesSchemaVersionAndLeavesNoTemporaryFile()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings expected = AppSettings.CreateDefault() with
        {
            Provider = AppSettings.CreateDefault().Provider with
            {
                ReceptionProvider = ReceptionProvider.Dmdata,
                Mode = ProviderMode.Custom,
                WebSocketUrl = "wss://custom.invalid/ws",
                RestBaseUrl = "https://custom.invalid/v2",
                DmdataReceiveEarthquakeTelegrams = true,
            },
            Filter = AppSettings.CreateDefault().Filter with
            {
                HideQuakeBelowIntensity3 = true,
                HideWeatherContinuationOnly = false,
                WeatherPrefectureCodes = ["01", "43"],
                WeatherWarnings = false,
                WeatherAdvisories = true,
            },
            Display = AppSettings.CreateDefault().Display with
            {
                PageDurationSeconds = 7.5,
                FontScale = 1.25,
                LetterSpacingEm = 0.2,
                LineSpacing = 1.5,
                BackgroundMode = BackgroundMode.Blue,
                OutputTransform = new OutputTransformSettings(2, 10, 20, 30, 40, 50, 60),
                EewAutoHideSeconds = 18,
                QuakeAutoHideSeconds = 22,
                TsunamiAutoHideSeconds = 35,
                ProductionReplay = ProductionReplaySettings.Default with
                {
                    RotationIntervalSeconds = 12,
                    ResumeDelaySeconds = 3,
                    Tsunami = new ProductionReplayPolicy(true, 10, false),
                    WeatherWarning = new ProductionReplayPolicy(true, 90, true),
                },
            },
            Obs = AppSettings.CreateDefault().Obs with
            {
                SnapshotIntervalMilliseconds = 50,
                BrowserSourceSyncEnabled = true,
                WebSocketProtectedPassword = "protected-value",
                TargetSceneName = "QTelopper Test",
                AudioMonitoringMode = ObsAudioMonitoringMode.MonitorAndOutput,
            },
            Audio = AppSettings.CreateDefault().Audio with
            {
                MinimumQuakeScale = JmaScale.SixLower,
                MinimumTsunamiGrade = TsunamiGrade.Warning,
                WeatherSpecialWarningEnabled = true,
                WeatherWarningEnabled = true,
                WeatherAdvisoryEnabled = true,
                WeatherDisasterPreventionBulletinEnabled = true,
                WeatherSpecialWarningFilePath = "weather-level5.wav",
                WeatherWarningFilePath = "weather-level4-3.mp3",
                WeatherAdvisoryFilePath = "weather-level2.ogg",
                WeatherDisasterPreventionBulletinFilePath = "weather-bulletin.wav",
                TsunamiAdvisoryEnabled = true,
                TsunamiWarningEnabled = true,
                TsunamiMajorWarningEnabled = true,
                TsunamiAdvisoryFilePath = "tsunami-advisory.wav",
                TsunamiWarningFilePath = "tsunami-warning.wav",
                TsunamiMajorWarningFilePath = "tsunami-major.wav",
            },
            Log = AppSettings.CreateDefault().Log with
            {
                SaveRawProviderMessages = true,
                RawMessageRetentionDays = 14,
                RawMessageMaximumTotalMegabytes = 1024,
            },
        };

        await store.SaveAsync(expected);
        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(ReceptionProvider.Dmdata, actual.Provider.ReceptionProvider);
        Assert.AreEqual(ProviderMode.Production, actual.Provider.Mode);
        Assert.AreEqual("wss://api.p2pquake.net/v2/ws", actual.Provider.WebSocketUrl);
        Assert.AreEqual("https://api.p2pquake.net/v2", actual.Provider.RestBaseUrl);
        Assert.AreEqual(7.5, actual.Display.PageDurationSeconds);
        Assert.AreEqual(1, actual.Display.FontScale);
        Assert.AreEqual(0, actual.Display.LetterSpacingEm);
        Assert.AreEqual(1, actual.Display.LineSpacing);
        Assert.AreEqual(BackgroundMode.Transparent, actual.Display.BackgroundMode);
        Assert.AreEqual(OutputTransformSettings.Default, actual.Display.OutputTransform);
        Assert.AreEqual(18, actual.Display.EewAutoHideSeconds);
        Assert.AreEqual(22, actual.Display.QuakeAutoHideSeconds);
        Assert.AreEqual(35, actual.Display.TsunamiAutoHideSeconds);
        Assert.AreEqual(12, actual.Display.ProductionReplay.RotationIntervalSeconds);
        Assert.AreEqual(3, actual.Display.ProductionReplay.ResumeDelaySeconds);
        Assert.IsTrue(actual.Display.ProductionReplay.Tsunami.Enabled);
        Assert.AreEqual(10, actual.Display.ProductionReplay.Tsunami.RepeatCount);
        Assert.IsTrue(actual.Display.ProductionReplay.WeatherWarning.AudioOnEachCycle);
        Assert.IsTrue(actual.Obs.BrowserSourceSyncEnabled);
        Assert.AreEqual(50, actual.Obs.SnapshotIntervalMilliseconds);
        Assert.AreEqual("protected-value", actual.Obs.WebSocketProtectedPassword);
        Assert.AreEqual("QTelopper Test", actual.Obs.TargetSceneName);
        Assert.AreEqual(
            ObsAudioMonitoringMode.Off,
            actual.Obs.AudioMonitoringMode);
        Assert.AreEqual(JmaScale.SixLower, actual.Audio.MinimumQuakeScale);
        Assert.AreEqual(TsunamiGrade.Warning, actual.Audio.MinimumTsunamiGrade);
        Assert.IsTrue(actual.Audio.WeatherSpecialWarningEnabled);
        Assert.IsTrue(actual.Audio.WeatherWarningEnabled);
        Assert.IsTrue(actual.Audio.WeatherAdvisoryEnabled);
        Assert.IsTrue(actual.Audio.WeatherDisasterPreventionBulletinEnabled);
        Assert.AreEqual(
            "weather-level5.wav",
            actual.Audio.WeatherSpecialWarningFilePath);
        Assert.AreEqual("weather-level4-3.mp3", actual.Audio.WeatherWarningFilePath);
        Assert.AreEqual("weather-level2.ogg", actual.Audio.WeatherAdvisoryFilePath);
        Assert.AreEqual(
            "weather-bulletin.wav",
            actual.Audio.WeatherDisasterPreventionBulletinFilePath);
        Assert.IsTrue(actual.Audio.TsunamiAdvisoryEnabled);
        Assert.IsTrue(actual.Audio.TsunamiWarningEnabled);
        Assert.IsTrue(actual.Audio.TsunamiMajorWarningEnabled);
        Assert.AreEqual("tsunami-advisory.wav", actual.Audio.TsunamiAdvisoryFilePath);
        Assert.AreEqual("tsunami-warning.wav", actual.Audio.TsunamiWarningFilePath);
        Assert.AreEqual("tsunami-major.wav", actual.Audio.TsunamiMajorWarningFilePath);
        Assert.IsTrue(actual.Filter.HideQuakeBelowIntensity3);
        Assert.IsFalse(actual.Filter.HideWeatherContinuationOnly);
        Assert.HasCount(2, actual.Filter.WeatherPrefectureCodes);
        Assert.AreEqual("01", actual.Filter.WeatherPrefectureCodes[0]);
        Assert.AreEqual("43", actual.Filter.WeatherPrefectureCodes[1]);
        Assert.IsFalse(actual.Filter.WeatherWarnings);
        Assert.IsTrue(actual.Filter.WeatherAdvisories);
        Assert.IsTrue(actual.Log.SaveRawProviderMessages);
        Assert.AreEqual(14, actual.Log.RawMessageRetentionDays);
        Assert.AreEqual(1024, actual.Log.RawMessageMaximumTotalMegabytes);
        Assert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [TestMethod]
    public async Task AxisSelectionAndProtectedTokenSurviveSettingsRoundTrip()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "axis-settings.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings expected = AppSettings.CreateDefault() with
        {
            Provider = AppSettings.CreateDefault().Provider with
            {
                ReceptionProvider = ReceptionProvider.Axis,
                AxisApiBaseUrl = "https://axis.prioris.jp/api/",
                AxisProtectedAccessToken = "protected-token-placeholder",
                AxisChannel = "jmx-seismology,jmx-meteorology",
            },
        };

        await store.SaveAsync(expected);
        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(ReceptionProvider.Axis, actual.Provider.ReceptionProvider);
        Assert.AreEqual(ProviderMode.Production, actual.Provider.Mode);
        Assert.AreEqual(
            "protected-token-placeholder",
            actual.Provider.AxisProtectedAccessToken);
        Assert.AreEqual(
            "jmx-seismology,jmx-meteorology,jmx-volcanology,eew",
            actual.Provider.AxisChannel);
    }

    [TestMethod]
    public async Task DmdataSelectionAndProtectedCredentialSurviveSettingsRoundTrip()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "dmdata-settings.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings expected = AppSettings.CreateDefault() with
        {
            Provider = AppSettings.CreateDefault().Provider with
            {
                ReceptionProvider = ReceptionProvider.Dmdata,
                DmdataProtectedCredential = "protected-credential-placeholder",
                DmdataCredentialEnvironmentVariable = string.Empty,
                DmdataReceiveEarthquakeTelegrams = true,
            },
        };

        await store.SaveAsync(expected);
        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(ReceptionProvider.Dmdata, actual.Provider.ReceptionProvider);
        Assert.AreEqual(
            "protected-credential-placeholder",
            actual.Provider.DmdataProtectedCredential);
        Assert.AreEqual(string.Empty, actual.Provider.DmdataCredentialEnvironmentVariable);
    }

    [TestMethod]
    public async Task SchemaTwentyOneDmdataSettingsMigrateWithoutDiscardingLegacyEnvironmentName()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "dmdata-schema-21.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings legacy = AppSettings.CreateDefault() with
        {
            SchemaVersion = 21,
            Provider = AppSettings.CreateDefault().Provider with
            {
                ReceptionProvider = ReceptionProvider.Dmdata,
                DmdataCredentialEnvironmentVariable = "QTELOPPER_LEGACY_DMDATA_KEY",
                DmdataReceiveEarthquakeTelegrams = true,
            },
        };
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(
            legacy,
            CamelCaseJson));

        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(
            "QTELOPPER_LEGACY_DMDATA_KEY",
            actual.Provider.DmdataCredentialEnvironmentVariable);
        Assert.AreEqual(string.Empty, actual.Provider.DmdataProtectedCredential);
    }

    [TestMethod]
    public async Task SchemaElevenAxisSettingsEnableAllSupportedJmaChannels()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "axis-schema-11.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings legacy = AppSettings.CreateDefault() with
        {
            SchemaVersion = 11,
            Provider = AppSettings.CreateDefault().Provider with
            {
                ReceptionProvider = ReceptionProvider.Axis,
                AxisChannel = "jmx-seismology",
            },
        };
        await File.WriteAllTextAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(legacy, CamelCaseJson));
        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(
            "jmx-seismology,jmx-meteorology,jmx-volcanology,eew",
            actual.Provider.AxisChannel);
        Assert.IsTrue(logs.GetSnapshot().Any(entry => entry.EventName == "SettingsMigrated"));
    }

    [TestMethod]
    public async Task SchemaTwelveSettingsGainSafeDetailedWeatherFilterDefaults()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "weather-schema-12.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings legacy = AppSettings.CreateDefault() with
        {
            SchemaVersion = 12,
        };
        await File.WriteAllTextAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(legacy, CamelCaseJson));
        JsonNode legacyDocument = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        JsonObject legacyFilter = legacyDocument["filter"]!.AsObject();
        legacyFilter.Remove("weatherPrefectureCode");
        legacyFilter.Remove("weatherSpecialWarnings");
        legacyFilter.Remove("weatherWarnings");
        legacyFilter.Remove("weatherAdvisories");
        legacyFilter.Remove("weatherTornadoAdvisories");
        legacyFilter.Remove("weatherRecordShortRain");
        legacyFilter.Remove("weatherDisasterPreventionBulletins");
        legacyFilter.Remove("hideWeatherContinuationOnly");
        await File.WriteAllTextAsync(path, legacyDocument.ToJsonString());

        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.IsTrue(actual.Filter.WeatherSpecialWarnings);
        Assert.IsTrue(actual.Filter.WeatherWarnings);
        Assert.IsFalse(actual.Filter.WeatherAdvisories);
        Assert.IsTrue(actual.Filter.WeatherTornadoAdvisories);
        Assert.IsTrue(actual.Filter.WeatherRecordShortRain);
        Assert.IsTrue(actual.Filter.WeatherDisasterPreventionBulletins);
        Assert.IsTrue(actual.Filter.HideWeatherContinuationOnly);
    }

    [TestMethod]
    public async Task SchemaFourteenSettingsGainOptInRawArchiveDefaults()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "raw-archive-schema-14.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings legacy = AppSettings.CreateDefault() with { SchemaVersion = 14 };
        await File.WriteAllTextAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(legacy, CamelCaseJson));
        JsonNode legacyDocument = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        JsonObject legacyLog = legacyDocument["log"]!.AsObject();
        legacyLog.Remove("saveRawProviderMessages");
        legacyLog.Remove("rawMessageRetentionDays");
        legacyLog.Remove("rawMessageMaximumTotalMegabytes");
        await File.WriteAllTextAsync(path, legacyDocument.ToJsonString());

        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.IsFalse(actual.Log.SaveRawProviderMessages);
        Assert.AreEqual(7, actual.Log.RawMessageRetentionDays);
        Assert.AreEqual(256, actual.Log.RawMessageMaximumTotalMegabytes);
    }

    [TestMethod]
    public async Task SettingsInspectionUsesTheSameEnumConvertersAsNormalLoading()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "inspect-settings.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings expected = AppSettings.CreateDefault();
        await store.SaveAsync(expected);

        (AppSettings actual, int sourceSchema) =
            JsonSettingsStore.ReadAndNormalizeDocument(path);

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, sourceSchema);
        Assert.AreEqual(expected.Provider.Mode, actual.Provider.Mode);
        Assert.AreEqual(expected.Provider.ReceptionProvider, actual.Provider.ReceptionProvider);
    }

    [TestMethod]
    public async Task SchemaFifteenTsunamiAudioMigratesToSeparateGradeFiles()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "tsunami-audio-schema-15.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings legacy = AppSettings.CreateDefault() with
        {
            SchemaVersion = 15,
            Audio = AudioSettings.Disabled with
            {
                TsunamiEnabled = true,
                TsunamiFilePath = "legacy-tsunami.wav",
                MinimumTsunamiGrade = TsunamiGrade.Warning,
            },
        };
        await File.WriteAllTextAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(legacy, CamelCaseJson));
        JsonNode legacyDocument = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        JsonObject legacyAudio = legacyDocument["audio"]!.AsObject();
        legacyAudio.Remove("tsunamiAdvisoryEnabled");
        legacyAudio.Remove("tsunamiWarningEnabled");
        legacyAudio.Remove("tsunamiMajorWarningEnabled");
        legacyAudio.Remove("tsunamiAdvisoryFilePath");
        legacyAudio.Remove("tsunamiWarningFilePath");
        legacyAudio.Remove("tsunamiMajorWarningFilePath");
        await File.WriteAllTextAsync(path, legacyDocument.ToJsonString());

        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.IsFalse(actual.Audio.TsunamiAdvisoryEnabled);
        Assert.IsTrue(actual.Audio.TsunamiWarningEnabled);
        Assert.IsTrue(actual.Audio.TsunamiMajorWarningEnabled);
        Assert.AreEqual("legacy-tsunami.wav", actual.Audio.TsunamiWarningFilePath);
        Assert.AreEqual("legacy-tsunami.wav", actual.Audio.TsunamiMajorWarningFilePath);
    }

    [TestMethod]
    public async Task InvalidSettingsAreMovedAsideAndDefaultsAreLoadedWithWarning()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{ invalid json");
        var store = new JsonSettingsStore(path, logs);

        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(
            System.Text.Json.JsonSerializer.Serialize(AppSettings.CreateDefault(), CamelCaseJson),
            System.Text.Json.JsonSerializer.Serialize(actual, CamelCaseJson));
        Assert.IsFalse(File.Exists(path));
        Assert.HasCount(1, Directory.GetFiles(_directory, "settings.corrupt-*.json"));
        Assert.IsTrue(logs.GetSnapshot().Any(entry => entry.EventName == "SettingsRecovered"));
    }

    [TestMethod]
    public async Task LegacySettingsMakeEewInheritTheFormerCommonAutoHideValue()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings legacy = AppSettings.CreateDefault() with
        {
            Display = AppSettings.CreateDefault().Display with
            {
                AutoHideSeconds = 27,
            },
        };
        await store.SaveAsync(legacy);
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["display"]!.AsObject().Remove("eewAutoHideSeconds");
        document["display"]!.AsObject().Remove("quakeAutoHideSeconds");
        document["display"]!.AsObject().Remove("tsunamiAutoHideSeconds");
        await File.WriteAllTextAsync(path, document.ToJsonString());

        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(27, actual.Display.AutoHideSeconds);
        Assert.AreEqual(27, actual.Display.EewAutoHideSeconds);
        Assert.AreEqual(27, actual.Display.QuakeAutoHideSeconds);
        Assert.AreEqual(27, actual.Display.TsunamiAutoHideSeconds);
    }

    [TestMethod]
    public async Task SchemaOneSettingsAreMigratedWithoutLosingPreferences()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings legacy = AppSettings.CreateDefault() with
        {
            SchemaVersion = 1,
            Filter = AppSettings.CreateDefault().Filter with
            {
                HideQuakeBelowIntensity3 = true,
            },
            Display = AppSettings.CreateDefault().Display with
            {
                PageDurationSeconds = 8,
            },
        };
        await File.WriteAllTextAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(
                legacy,
                CamelCaseJson));
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["display"]!["topmost"] = true;
        document["obs"]!["mode"] = "WindowCapture";
        document["audio"]!["volume"] = 0.8;
        document["audio"]!["outputDeviceId"] = "legacy-device";
        await File.WriteAllTextAsync(path, document.ToJsonString());

        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(8, actual.Display.PageDurationSeconds);
        Assert.IsTrue(actual.Filter.HideQuakeBelowIntensity3);
        JsonNode persisted = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.AreEqual(AppSettings.CurrentSchemaVersion, persisted["schemaVersion"]!.GetValue<int>());
        Assert.IsTrue(logs.GetSnapshot().Any(entry => entry.EventName == "SettingsMigrated"));
        Assert.IsEmpty(Directory.GetFiles(_directory, "settings.corrupt-*.json"));
    }

    [TestMethod]
    public async Task SchemaTwentyTwoSettingsIgnoreAndRemoveLegacyDesktopOverlay()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings legacy = AppSettings.CreateDefault() with { SchemaVersion = 22 };
        await File.WriteAllTextAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(legacy, CamelCaseJson));
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["display"]!["desktopOverlay"] = new JsonObject
        {
            ["enabled"] = true,
            ["left"] = 120d,
            ["top"] = 80d,
        };
        await File.WriteAllTextAsync(path, document.ToJsonString());

        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        JsonNode persisted = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.IsNull(persisted["display"]!["desktopOverlay"]);
        Assert.IsTrue(logs.GetSnapshot().Any(entry => entry.EventName == "SettingsMigrated"));
    }

    [TestMethod]
    public async Task SchemaTwentyThreeAxisSelectionMigratesToRequestedHybridRoutes()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "axis-schema-23.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings legacy = defaults with
        {
            SchemaVersion = 23,
            Provider = defaults.Provider with
            {
                ReceptionProvider = ReceptionProvider.Axis,
            },
        };
        await File.WriteAllTextAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(legacy, CamelCaseJson));

        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(ReceptionProvider.Axis, actual.Provider.Routing.Eew);
        Assert.AreEqual(ReceptionProvider.P2pQuake, actual.Provider.Routing.Quake);
        Assert.AreEqual(ReceptionProvider.P2pQuake, actual.Provider.Routing.Tsunami);
        Assert.AreEqual(ReceptionProvider.Axis, actual.Provider.Routing.Weather);
        Assert.AreEqual(ReceptionProvider.Axis, actual.Provider.Routing.Volcano);
        Assert.AreEqual(ReceptionProvider.Axis, actual.Provider.Routing.NankaiTrough);
    }

    [TestMethod]
    public async Task SchemaTwentyFourRoutingMigratesAndPreservesDisabledCategories()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "routing-schema-24.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings defaults = AppSettings.CreateDefault();
        ProviderRoutingSettings routing = ProviderRoutingSettings.FromLegacy(
            ReceptionProvider.Disabled) with
        {
            Eew = ReceptionProvider.Axis,
            Quake = ReceptionProvider.P2pQuake,
        };
        AppSettings legacy = defaults with
        {
            SchemaVersion = 24,
            Provider = defaults.Provider with
            {
                ReceptionProvider = ReceptionProvider.Axis,
                Routing = routing,
                DmdataReceiveEewWarnings = true,
                DmdataReceiveEarthquakeTelegrams = true,
                DmdataReceiveWeatherWarnings = true,
                DmdataReceiveVolcanoTelegrams = true,
            },
        };
        await File.WriteAllTextAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(legacy, CamelCaseJson));

        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(ReceptionProvider.Axis, actual.Provider.Routing.Eew);
        Assert.AreEqual(ReceptionProvider.P2pQuake, actual.Provider.Routing.Quake);
        Assert.AreEqual(ReceptionProvider.Disabled, actual.Provider.Routing.Tsunami);
        Assert.AreEqual(ReceptionProvider.Disabled, actual.Provider.Routing.Weather);
        Assert.AreEqual(ReceptionProvider.Disabled, actual.Provider.Routing.Volcano);
        Assert.AreEqual(ReceptionProvider.Disabled, actual.Provider.Routing.NankaiTrough);
        Assert.AreEqual("eew", actual.Provider.AxisChannel);
        Assert.IsFalse(actual.Provider.DmdataReceiveEewWarnings);
        Assert.IsFalse(actual.Provider.DmdataReceiveEarthquakeTelegrams);
        Assert.IsFalse(actual.Provider.DmdataReceiveWeatherWarnings);
        Assert.IsFalse(actual.Provider.DmdataReceiveVolcanoTelegrams);
        Assert.IsTrue(logs.GetSnapshot().Any(entry => entry.EventName == "SettingsMigrated"));
    }

    [TestMethod]
    public void SchemaTwentyFiveMigrationPreservesLegacyForecastContract()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings legacy = defaults with
        {
            SchemaVersion = 25,
            Provider = defaults.Provider with
            {
                DmdataEewContractType = DmdataEewContractType.Warning,
            },
        };

        AppSettings actual = JsonSettingsStore.NormalizeDocument(legacy);

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(DmdataEewContractType.Forecast,
            actual.Provider.DmdataEewContractType);
    }

    [TestMethod]
    public async Task SchemaThreeSettingsGainSafeNiiHistoryDefaults()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path, logs);
        AppSettings legacy = AppSettings.CreateDefault() with { SchemaVersion = 3 };
        await File.WriteAllTextAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(legacy, CamelCaseJson));
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["history"]!.AsObject().Remove("niiDate");
        document["history"]!.AsObject().Remove("niiContent");
        await File.WriteAllTextAsync(path, document.ToJsonString());

        AppSettings actual = await store.LoadAsync();

        Assert.AreEqual(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(DateOnly.FromDateTime(DateTime.Today), actual.History.NiiDate);
        Assert.AreEqual(NiiHistoryContent.QuakeAndTsunami, actual.History.NiiContent);
        Assert.AreEqual(string.Empty, actual.History.NiiReportUrl);
        Assert.AreEqual(string.Empty, actual.History.LocalXmlFilePath);
        Assert.IsTrue(logs.GetSnapshot().Any(entry => entry.EventName == "SettingsMigrated"));
    }

    [TestMethod]
    public async Task InvalidStateIsMovedAsideAndEmptyStateIsLoadedWithWarning()
    {
        var logs = new UiLogBuffer();
        string path = Path.Combine(_directory, "state.json");
        await File.WriteAllTextAsync(path, "{ invalid json");
        var store = new JsonDisplayStateStore(path, logs);

        DisplayStateDocument actual = await store.LoadAsync();

        Assert.AreEqual(DisplayStateDocument.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.IsNull(actual.Current);
        Assert.IsFalse(File.Exists(path));
        Assert.HasCount(1, Directory.GetFiles(_directory, "state.corrupt-*.json"));
        Assert.IsTrue(logs.GetSnapshot().Any(entry => entry.EventName == "StateRecovered"));
    }

    [TestMethod]
    public async Task DiagnosticsZipRedactsCredentialsQueriesAndLogUrls()
    {
        string path = Path.Combine(_directory, "diagnostics.zip");
        AppSettings settings = AppSettings.CreateDefault() with
        {
            Provider = new ProviderSettings(
                ProviderMode.Custom,
                "wss://alice:open-sesame@example.invalid/ws?token=top-secret&room=1",
                "https://example.invalid/api?apikey=top-secret"),
        };
        var snapshot = new DiagnosticsSnapshot(
            DiagnosticsSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            "1.0.0",
            Environment.Version.ToString(),
            Environment.OSVersion.VersionString,
            "Stopped",
            null,
            0,
            "停止中",
            0,
            "EewInitial",
            "Completed",
            DateTimeOffset.UtcNow,
            settings,
            [new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppLogLevel.Warning,
                "CustomUrl",
                "Failed wss://alice:open-sesame@example.invalid/ws?token=top-secret")]);
        var writer = new ZipDiagnosticsBundleWriter();

        await writer.WriteAsync(path, snapshot);

        using ZipArchive archive = ZipFile.OpenRead(path);
        Assert.AreEqual(7, archive.Entries.Count);
        string contents = string.Join("\n", archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain("open-sesame", contents);
        Assert.DoesNotContain("top-secret", contents);
        Assert.Contains("redacted", contents);
        Assert.Contains("lastObsAudioCue", contents);
        Assert.Contains("EewInitial", contents);
        Assert.Contains("lastObsAudioPlaybackResult", contents);
        Assert.Contains("Completed", contents);
        Assert.Contains("settings.redacted.json", string.Join(',', archive.Entries.Select(e => e.Name)));
        Assert.Contains("operations/provider-connections.json", string.Join(',', archive.Entries.Select(e => e.FullName)));
    }

    [TestMethod]
    public async Task FileLogSanitizesUrlCredentialsAndCompleteQuery()
    {
        string logDirectory = Path.Combine(_directory, "logs");
        using var writer = new FileAppLogWriter(logDirectory);
        await writer.WriteAsync(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppLogLevel.Error,
            "ConnectionFailed",
            "wss://user:password@example.invalid/ws?token=secret&value=1"));

        string contents = await File.ReadAllTextAsync(Directory.GetFiles(logDirectory, "*.log")[0]);
        string fileName = Path.GetFileName(Directory.GetFiles(logDirectory, "*.log")[0]);

        Assert.StartsWith("CDI-Telopper-", fileName);
        Assert.DoesNotContain("password", contents);
        Assert.DoesNotContain("token=secret", contents);
        Assert.Contains("redacted", contents);
    }

    [TestMethod]
    public async Task FileLogPreservesExceptionStackAndSanitizesUrls()
    {
        string logDirectory = Path.Combine(_directory, "exception-logs");
        using var writer = new FileAppLogWriter(logDirectory);
        Exception exception;
        try
        {
            ThrowLoggedException();
            throw new InvalidOperationException("The test exception was not thrown.");
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        await writer.WriteAsync(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppLogLevel.Critical,
            "UiUnhandledException",
            "The first UI failure was captured.",
            exception));

        string contents = await File.ReadAllTextAsync(Directory.GetFiles(logDirectory, "*.log")[0]);

        Assert.Contains("ThrowLoggedException", contents);
        Assert.DoesNotContain("top-secret", contents);
        Assert.Contains("redacted", contents);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowLoggedException() =>
        throw new InvalidOperationException(
            "Failure at https://example.invalid/fault?token=top-secret");

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
