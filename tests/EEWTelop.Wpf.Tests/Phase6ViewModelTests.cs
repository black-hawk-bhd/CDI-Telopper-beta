using System.Runtime.CompilerServices;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Audio;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Operations;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Axis.Security;
using EEWTelop.Infrastructure.Dmdata.Security;
using EEWTelop.Infrastructure.Operations;
using EEWTelop.Infrastructure.P2P.Configuration;
using EEWTelop.Infrastructure.Settings;
using EEWTelop.Wpf.Bootstrap;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.Services;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class Phase6ViewModelTests
{
    [TestMethod]
    public void DistributionSettingsExposeEnabledProvidersAndSupportedPresentationOptions()
    {
        var editor = new SettingsEditorViewModel(AppSettings.CreateDefault());

        var expectedProviders = new List<ReceptionProvider> { ReceptionProvider.P2pQuake };
        if (BuildFeatures.DmdataProviderEnabled)
        {
            expectedProviders.Add(ReceptionProvider.Dmdata);
        }
        if (BuildFeatures.AxisProviderEnabled)
        {
            expectedProviders.Add(ReceptionProvider.Axis);
        }
        expectedProviders.Add(ReceptionProvider.Disabled);
        CollectionAssert.AreEqual(expectedProviders, editor.ReceptionProviders.ToArray());
        CollectionAssert.AreEqual(
            new[] { ProviderMode.Production, ProviderMode.Sandbox },
            editor.ProviderModes.ToArray());
        CollectionAssert.AreEqual(
            new[] { BackgroundMode.Transparent },
            editor.BackgroundModes.ToArray());
        Assert.HasCount(1, editor.ObsAudioMonitoringModes);
        Assert.AreEqual(
            ObsAudioMonitoringMode.Off,
            editor.ObsAudioMonitoringModes[0].Value);
        Assert.AreEqual(
            BuildFeatures.ExtendedFeaturesEnabled,
            editor.HistoryApis.Any(option => option.Value == HistoryApi.NiiJmaXml));
        Assert.AreEqual(
            BuildFeatures.ExtendedFeaturesEnabled,
            editor.HistoryApis.Any(option => option.Value == HistoryApi.LocalJmaXml));
        Assert.IsTrue(editor.NiiHistoryContents.Any(option =>
            option.Value == NiiHistoryContent.WeatherWarningsOnly));
        Assert.IsTrue(editor.NiiHistoryContents.Any(option =>
            option.Value == NiiHistoryContent.AllSupported));
        NiiHistoryContent[] individualWeatherContents =
        [
            NiiHistoryContent.WeatherRain,
            NiiHistoryContent.WeatherLandslide,
            NiiHistoryContent.WeatherStormSurge,
            NiiHistoryContent.WeatherStorm,
            NiiHistoryContent.WeatherWave,
            NiiHistoryContent.WeatherHeavySnow,
            NiiHistoryContent.WeatherOtherAdvisories,
        ];
        Assert.IsTrue(individualWeatherContents.All(value =>
            editor.NiiHistoryContents.Any(option => option.Value == value)));
    }

    [TestMethod]
    public void ProviderRoutingOverridesStaleDmdataSubscriptionFlags()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings configured = defaults with
        {
            Provider = defaults.Provider with
            {
                DmdataReceiveEewWarnings = true,
                DmdataReceiveEarthquakeTelegrams = true,
                DmdataReceiveWeatherWarnings = true,
                DmdataReceiveVolcanoTelegrams = true,
                DmdataUseLegacyWeatherWarningTelegrams = true,
            },
        };
        var editor = new SettingsEditorViewModel(configured);

        AppSettings saved = editor.ToSettings(configured);

        Assert.IsFalse(saved.Provider.DmdataReceiveEewWarnings);
        Assert.IsFalse(saved.Provider.DmdataReceiveEarthquakeTelegrams);
        Assert.IsFalse(saved.Provider.DmdataReceiveWeatherWarnings);
        Assert.IsFalse(saved.Provider.DmdataReceiveVolcanoTelegrams);
        Assert.IsFalse(saved.Provider.DmdataUseLegacyWeatherWarningTelegrams);
    }

    [TestMethod]
    public void DmdataCredentialIsProtectedForCurrentWindowsUserWhenSettingsAreSaved()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults)
        {
            DmdataCredential = "direct-api-key",
        };

        AppSettings saved = editor.ToSettings(defaults);

        Assert.AreNotEqual("direct-api-key", saved.Provider.DmdataProtectedCredential);
        Assert.AreEqual(
            "direct-api-key",
            DmdataCredentialProtector.Unprotect(saved.Provider.DmdataProtectedCredential));
        Assert.AreEqual(string.Empty, saved.Provider.DmdataCredentialEnvironmentVariable);
    }

    [TestMethod]
    public void DmdataOAuthCredentialIsNotExposedOrReinterpretedAsAnApiKey()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings legacy = defaults with
        {
            Provider = defaults.Provider with
            {
                DmdataAuthenticationMode = DmdataAuthenticationMode.OAuthAccessToken,
                DmdataProtectedCredential = DmdataCredentialProtector.Protect("legacy-oauth-token"),
            },
        };

        var editor = new SettingsEditorViewModel(legacy);
        AppSettings saved = editor.ToSettings(legacy);

        CollectionAssert.AreEqual(
            new[] { DmdataAuthenticationMode.ApiKey },
            editor.DmdataAuthenticationModes.ToArray());
        Assert.AreEqual(string.Empty, editor.DmdataCredential);
        Assert.AreEqual(DmdataAuthenticationMode.ApiKey, saved.Provider.DmdataAuthenticationMode);
        Assert.AreEqual(string.Empty, saved.Provider.DmdataProtectedCredential);
    }

    [TestMethod]
    public void ReceptionResetLeavesDmdataContractCategoriesUnselected()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults);

        editor.ResetReceptionSettings();

        Assert.IsFalse(editor.DmdataReceiveEewWarnings);
        Assert.IsFalse(editor.DmdataReceiveEarthquakeTelegrams);
        Assert.IsFalse(editor.DmdataReceiveWeatherWarnings);
        Assert.IsFalse(editor.DmdataReceiveVolcanoTelegrams);
        Assert.AreEqual(DmdataEewContractType.Warning, editor.DmdataEewContractType);
    }

    [TestMethod]
    public void DmdataEewContractSelectionRoundTripsThroughEditor()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings configured = defaults with
        {
            Provider = defaults.Provider with
            {
                Routing = defaults.Provider.Routing with
                {
                    Eew = ReceptionProvider.Dmdata,
                },
                ReceptionProvider = ReceptionProvider.Dmdata,
                DmdataEewContractType = DmdataEewContractType.Forecast,
            },
        };
        var editor = new SettingsEditorViewModel(configured);

        Assert.AreEqual(DmdataEewContractType.Forecast, editor.DmdataEewContractType);
        editor.DmdataEewContractType = DmdataEewContractType.Warning;

        AppSettings saved = editor.ToSettings(configured);

        Assert.AreEqual(DmdataEewContractType.Warning,
            saved.Provider.DmdataEewContractType);
        Assert.IsTrue(saved.Provider.DmdataReceiveEewWarnings);
    }

    [TestMethod]
    public void SettingsAreClampedAndProductionConfirmationCannotBeDisabled()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults)
        {
            PageDurationSeconds = 30.4,
            LetterSpacingEm = 1,
            LineSpacing = 0.1,
            FontScale = 4,
            Width = 10,
            Height = 10,
            ObsPort = 99999,
            ObsSnapshotIntervalMilliseconds = 1,
            ObsAudioMonitoringMode = ObsAudioMonitoringMode.MonitorAndOutput,
            HistoryLimit = 999,
            HistoryApi = HistoryApi.LocalJmaXml,
            LocalHistoryXmlFilePath = @" C:\test-data\sample.xml ",
            ConfirmTestInProduction = false,
            HideQuakeBelowIntensity3 = true,
            AutoHideSeconds = -1,
            EewAutoHideSeconds = 9999,
            QuakeAutoHideSeconds = 9999,
            TsunamiAutoHideSeconds = -1,
            ProductionReplayRotationIntervalSeconds = 999,
            ProductionReplayResumeDelaySeconds = -5,
            ProductionReplayTsunamiEnabled = true,
            ProductionReplayTsunamiRepeatCount = 999999,
            ProductionReplayTsunamiAudioEachCycle = true,
            NiiHistoryDate = new DateTime(2010, 1, 1),
            NiiHistoryContent = NiiHistoryContent.TsunamiOnly,
            HistoryRepeat = true,
            OutputScale = 99,
            OutputOffsetX = 9000,
            OutputOffsetY = -9000,
            OutputCropLeft = 1900,
            OutputCropRight = 1900,
        };

        AppSettings result = editor.ToSettings(defaults);

        Assert.AreEqual(30, result.Display.PageDurationSeconds);
        Assert.AreEqual(0, result.Display.LetterSpacingEm);
        Assert.AreEqual(1, result.Display.LineSpacing);
        Assert.AreEqual(1, result.Display.FontScale);
        Assert.AreEqual(0, result.Display.AutoHideSeconds);
        Assert.AreEqual(3600, result.Display.EewAutoHideSeconds);
        Assert.AreEqual(3600, result.Display.QuakeAutoHideSeconds);
        Assert.AreEqual(0, result.Display.TsunamiAutoHideSeconds);
        Assert.AreEqual(300, result.Display.ProductionReplay.RotationIntervalSeconds);
        Assert.AreEqual(0, result.Display.ProductionReplay.ResumeDelaySeconds);
        Assert.IsTrue(result.Display.ProductionReplay.Tsunami.Enabled);
        Assert.AreEqual(100, result.Display.ProductionReplay.Tsunami.RepeatCount);
        Assert.IsTrue(result.Display.ProductionReplay.Tsunami.AudioOnEachCycle);
        Assert.IsFalse(result.Display.ProductionReplay.Eew.Enabled);
        Assert.AreEqual(320, result.Display.Width);
        Assert.AreEqual(180, result.Display.Height);
        Assert.AreEqual(65535, result.Obs.Port);
        Assert.AreEqual(50, result.Obs.SnapshotIntervalMilliseconds);
        Assert.AreEqual(ObsAudioMonitoringMode.Off, result.Obs.AudioMonitoringMode);
        Assert.AreEqual(100, result.History.Limit);
        Assert.AreEqual(HistoryApi.LocalJmaXml, result.History.Api);
        Assert.AreEqual(@"C:\test-data\sample.xml", result.History.LocalXmlFilePath);
        Assert.AreEqual(new DateOnly(2012, 12, 1), result.History.NiiDate);
        Assert.AreEqual(NiiHistoryContent.TsunamiOnly, result.History.NiiContent);
        Assert.IsTrue(result.History.Repeat);
        Assert.AreEqual(OutputTransformSettings.Default, result.Display.OutputTransform);
        Assert.IsTrue(result.Filter.HideQuakeBelowIntensity3);
        Assert.IsTrue(result.Safety.ConfirmTestInProduction);
        Assert.IsFalse(result.Audio.EewEnabled);
        Assert.IsFalse(result.Audio.TsunamiEnabled);
        Assert.IsFalse(result.Audio.QuakeEnabled);
        Assert.IsFalse(result.Audio.Muted);
        Assert.IsTrue(result.Audio.TestUsesProductionSound);
        Assert.IsTrue(result.Audio.QuakeScaleCues.Values.All(static cue =>
            !cue.Enabled && string.IsNullOrEmpty(cue.FilePath)));
        Assert.IsTrue(result.Audio.TsunamiGradeCues.Values.All(static cue =>
            !cue.Enabled && string.IsNullOrEmpty(cue.FilePath)));
    }

    [TestMethod]
    public void SandboxModeUsesDedicatedEndpointsAndRepairsLegacyProductionUrls()
    {
        AppSettings legacy = AppSettings.CreateDefault() with
        {
            Provider = new ProviderSettings(
                ProviderMode.Sandbox,
                ProviderOptions.Production.WebSocketUri.AbsoluteUri,
                ProviderOptions.Production.RestBaseUri.AbsoluteUri),
        };

        var editor = new SettingsEditorViewModel(legacy);

        Assert.AreEqual(ProviderOptions.Sandbox.WebSocketUri.AbsoluteUri, editor.WebSocketUrl);
        Assert.AreEqual(
            ProviderOptions.Sandbox.RestBaseUri.AbsoluteUri.TrimEnd('/'),
            editor.RestBaseUrl);

        editor.ProviderMode = ProviderMode.Production;
        Assert.AreEqual(ProviderOptions.Production.WebSocketUri.AbsoluteUri, editor.WebSocketUrl);
        editor.ProviderMode = ProviderMode.Sandbox;
        Assert.AreEqual(ProviderOptions.Sandbox.WebSocketUri.AbsoluteUri, editor.WebSocketUrl);
    }

    [TestMethod]
    public void UserSelectedAudioSettingsAreSaved()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings configured = defaults with
        {
            Audio = new AudioSettings(
                EewEnabled: true,
                TsunamiEnabled: true,
                TrainingEnabled: false,
                TestUsesProductionSound: true,
                Muted: false,
                QuakeEnabled: true,
                EewInitialEnabled: true,
                EewContinuationEnabled: true,
                EewCancellationEnabled: true,
                QuakeFilePath: "quake.wav",
                TsunamiFilePath: "tsunami.mp3",
                EewInitialFilePath: "initial.wav",
                EewContinuationFilePath: "continue.ogg",
                EewCancellationFilePath: "cancel.wav",
                FileAudioConfigured: true)
            {
                MinimumQuakeScale = JmaScale.FiveUpper,
                MinimumTsunamiGrade = TsunamiGrade.Warning,
                WeatherSpecialWarningEnabled = true,
                WeatherWarningEnabled = true,
                WeatherAdvisoryEnabled = true,
                WeatherSpecialWarningFilePath = "weather-level5.wav",
                WeatherWarningFilePath = "weather-level4-3.mp3",
                WeatherAdvisoryFilePath = "weather-level2.ogg",
                WeatherCoalescingSeconds = 2.75,
                TsunamiAdvisoryEnabled = true,
                TsunamiWarningEnabled = true,
                TsunamiMajorWarningEnabled = true,
                TsunamiAdvisoryFilePath = "tsunami-advisory.wav",
                TsunamiWarningFilePath = "tsunami-warning.wav",
                TsunamiMajorWarningFilePath = "tsunami-major.wav",
            },
        };
        var editor = new SettingsEditorViewModel(configured);

        AppSettings saved = editor.ToSettings(configured);

        Assert.AreEqual(configured.Audio.MinimumQuakeScale, saved.Audio.MinimumQuakeScale);
        Assert.AreEqual(configured.Audio.EewInitialFilePath, saved.Audio.EewInitialFilePath);
        Assert.AreEqual(configured.Audio.WeatherCoalescingSeconds,
            saved.Audio.WeatherCoalescingSeconds);
        Assert.HasCount(9, saved.Audio.QuakeScaleCues);
        Assert.IsFalse(saved.Audio.QuakeScaleCues[JmaScale.Four].Enabled);
        Assert.IsTrue(saved.Audio.QuakeScaleCues[JmaScale.FiveUpper].Enabled);
        Assert.AreEqual("quake.wav",
            saved.Audio.QuakeScaleCues[JmaScale.Seven].FilePath);
        Assert.HasCount(4, saved.Audio.TsunamiGradeCues);
        Assert.IsTrue(saved.Audio.TsunamiGradeCues[TsunamiGrade.Watch].Enabled);
        Assert.AreEqual("tsunami-advisory.wav",
            saved.Audio.TsunamiGradeCues[TsunamiGrade.Watch].FilePath);
    }

    [TestMethod]
    public void WeatherAudioCoalescingSettingDefaultsAndClampsToSupportedRange()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults);

        Assert.AreEqual(
            AudioSettings.DefaultWeatherCoalescingSeconds,
            editor.WeatherAudioCoalescingSeconds);

        editor.WeatherAudioCoalescingSeconds = 9;
        AppSettings maximum = editor.ToSettings(defaults);
        Assert.AreEqual(
            AudioSettings.MaximumWeatherCoalescingSeconds,
            maximum.Audio.WeatherCoalescingSeconds);

        editor.WeatherAudioCoalescingSeconds = -1;
        AppSettings minimum = editor.ToSettings(defaults);
        Assert.AreEqual(0, minimum.Audio.WeatherCoalescingSeconds);
    }

    [TestMethod]
    public void ResetAudioSettingsClearsOnlyTheEditableAudioConfiguration()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings configured = defaults with
        {
            Audio = defaults.Audio with
            {
                Muted = true,
                TestUsesProductionSound = false,
                EewInitialEnabled = true,
                EewInitialFilePath = "eew.wav",
                WeatherSpecialWarningEnabled = true,
                WeatherSpecialWarningFilePath = "weather.wav",
                WeatherCoalescingSeconds = 3,
                QuakeScaleCues = new Dictionary<JmaScale, AudioCueSetting>
                {
                    [JmaScale.Seven] = new(true, "quake7.wav"),
                },
                TsunamiGradeCues = new Dictionary<TsunamiGrade, AudioCueSetting>
                {
                    [TsunamiGrade.MajorWarning] = new(true, "major.wav"),
                },
            },
        };
        var editor = new SettingsEditorViewModel(configured);
        double originalPageDuration = editor.PageDurationSeconds;

        editor.ResetAudioSettings();
        AppSettings reset = editor.ToSettings(configured);

        Assert.IsFalse(reset.Audio.Muted);
        Assert.IsTrue(reset.Audio.TestUsesProductionSound);
        Assert.IsFalse(reset.Audio.EewEnabled);
        Assert.IsFalse(reset.Audio.QuakeEnabled);
        Assert.IsFalse(reset.Audio.TsunamiEnabled);
        Assert.IsFalse(reset.Audio.WeatherSpecialWarningEnabled);
        Assert.IsFalse(reset.Audio.WeatherWarningEnabled);
        Assert.IsFalse(reset.Audio.WeatherAdvisoryEnabled);
        Assert.AreEqual(
            AudioSettings.DefaultWeatherCoalescingSeconds,
            reset.Audio.WeatherCoalescingSeconds);
        Assert.IsTrue(reset.Audio.QuakeScaleCues.Values.All(static cue =>
            !cue.Enabled && string.IsNullOrEmpty(cue.FilePath)));
        Assert.IsTrue(reset.Audio.TsunamiGradeCues.Values.All(static cue =>
            !cue.Enabled && string.IsNullOrEmpty(cue.FilePath)));
        Assert.IsTrue(string.IsNullOrEmpty(reset.Audio.EewInitialFilePath));
        Assert.IsTrue(string.IsNullOrEmpty(reset.Audio.WeatherSpecialWarningFilePath));
        Assert.AreEqual(originalPageDuration, editor.PageDurationSeconds);
    }

    [TestMethod]
    public void EachSettingsSectionCanReturnToItsDefaultValuesIndependently()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults)
        {
            ReceptionProvider = ReceptionProvider.Axis,
            DmdataCredential = "dmdata-secret",
            AxisAccessToken = "secret",
            FilterEew = false,
            FilterWeatherAdvisories = true,
            PageDurationSeconds = 20,
            ShowPageIndicator = false,
            ProductionReplayEewEnabled = true,
            ProductionReplayEewRepeatCount = 99,
            Width = 1280,
            Height = 720,
            ObsEnabled = false,
            ObsPort = 12345,
            ObsBrowserSourceSyncEnabled = true,
            ObsWebSocketPassword = "password",
            HistoryApi = HistoryApi.LocalJmaXml,
            LocalHistoryXmlFilePath = "sample.xml",
            SaveRawProviderMessages = true,
            RawMessageRetentionDays = 90,
            EnrichQuakeById = true,
            ConfirmTestInProduction = false,
        };
        editor.SetWeatherPrefectureCodes(["13", "27"]);
        editor.SetSubtitlePhraseOverrides(new Dictionary<string, string>
        {
            ["tsunami.attention"] = "edited",
        });

        editor.ResetReceptionSettings();
        Assert.AreEqual(defaults.Provider.ReceptionProvider, editor.ReceptionProvider);
        Assert.AreEqual(string.Empty, editor.DmdataCredential);
        Assert.AreEqual(string.Empty, editor.AxisAccessToken);
        Assert.AreEqual(20, editor.PageDurationSeconds);

        editor.ResetFilterSettings();
        Assert.AreEqual(defaults.Filter.Eew, editor.FilterEew);
        Assert.AreEqual(defaults.Filter.WeatherAdvisories, editor.FilterWeatherAdvisories);
        Assert.IsEmpty(editor.WeatherPrefectureCodes);

        editor.ResetCompatibilityAndSafetySettings();
        Assert.AreEqual(defaults.Compatibility.EnrichQuakeById, editor.EnrichQuakeById);
        Assert.AreEqual(defaults.Safety.ConfirmTestInProduction, editor.ConfirmTestInProduction);

        editor.ResetDisplaySettings();
        Assert.AreEqual(defaults.Display.PageDurationSeconds, editor.PageDurationSeconds);
        Assert.AreEqual(defaults.Display.ShowPageIndicator, editor.ShowPageIndicator);
        Assert.IsEmpty(editor.SubtitlePhraseOverrides);

        editor.ResetProductionReplaySettings();
        Assert.AreEqual(
            defaults.Display.ProductionReplay.Eew.Enabled,
            editor.ProductionReplayEewEnabled);
        Assert.AreEqual(
            defaults.Display.ProductionReplay.Eew.RepeatCount,
            editor.ProductionReplayEewRepeatCount);

        editor.ResetCanvasSettings();
        Assert.AreEqual(defaults.Display.Width, editor.Width);
        Assert.AreEqual(defaults.Display.Height, editor.Height);

        editor.ResetObsLocalViewSettings();
        Assert.AreEqual(defaults.Obs.Enabled, editor.ObsEnabled);
        Assert.AreEqual(defaults.Obs.Port, editor.ObsPort);

        editor.ResetObsWebSocketSettings();
        Assert.AreEqual(defaults.Obs.BrowserSourceSyncEnabled, editor.ObsBrowserSourceSyncEnabled);
        Assert.AreEqual(string.Empty, editor.ObsWebSocketPassword);

        editor.ResetHistorySettings();
        Assert.AreEqual(defaults.History.Api, editor.HistoryApi);
        Assert.AreEqual(defaults.History.LocalXmlFilePath, editor.LocalHistoryXmlFilePath);

        editor.ResetLogSettings();
        Assert.AreEqual(defaults.Log.SaveRawProviderMessages, editor.SaveRawProviderMessages);
        Assert.AreEqual(defaults.Log.RawMessageRetentionDays, editor.RawMessageRetentionDays);
    }

    [TestMethod]
    public void InformationProvidersCanBeSelectedAndSavedIndependently()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults)
        {
            EewProvider = ReceptionProvider.Axis,
            QuakeProvider = ReceptionProvider.P2pQuake,
            TsunamiProvider = ReceptionProvider.P2pQuake,
            WeatherProvider = ReceptionProvider.Dmdata,
            VolcanoProvider = ReceptionProvider.Axis,
            NankaiTroughProvider = ReceptionProvider.Dmdata,
            AxisAccessToken = "axis-token",
            DmdataCredential = "dmdata-key",
        };

        Assert.AreEqual("jmx-volcanology,eew", editor.AxisChannel);
        editor.EewProvider = ReceptionProvider.P2pQuake;
        Assert.AreEqual("jmx-volcanology", editor.AxisChannel);
        editor.EewProvider = ReceptionProvider.Axis;
        Assert.AreEqual("jmx-volcanology,eew", editor.AxisChannel);

        AppSettings actual = editor.ToSettings(defaults);

        Assert.AreEqual(ReceptionProvider.Axis, actual.Provider.Routing.Eew);
        Assert.AreEqual(ReceptionProvider.P2pQuake, actual.Provider.Routing.Quake);
        Assert.AreEqual(ReceptionProvider.P2pQuake, actual.Provider.Routing.Tsunami);
        Assert.AreEqual(ReceptionProvider.Dmdata, actual.Provider.Routing.Weather);
        Assert.AreEqual(ReceptionProvider.Axis, actual.Provider.Routing.Volcano);
        Assert.AreEqual(ReceptionProvider.Dmdata, actual.Provider.Routing.NankaiTrough);
        Assert.IsTrue(actual.Provider.DmdataReceiveWeatherWarnings);
        Assert.IsTrue(actual.Provider.DmdataReceiveEarthquakeTelegrams);
    }

    [TestMethod]
    public void DisabledCategoriesAreSavedWithoutEnablingUnusedProviderContracts()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults)
        {
            EewProvider = ReceptionProvider.Disabled,
            QuakeProvider = ReceptionProvider.P2pQuake,
            TsunamiProvider = ReceptionProvider.Disabled,
            WeatherProvider = ReceptionProvider.Disabled,
            VolcanoProvider = ReceptionProvider.Disabled,
            NankaiTroughProvider = ReceptionProvider.Disabled,
        };

        AppSettings actual = editor.ToSettings(defaults);

        Assert.AreEqual(ReceptionProvider.Disabled, actual.Provider.Routing.Eew);
        Assert.AreEqual(ReceptionProvider.P2pQuake, actual.Provider.Routing.Quake);
        Assert.AreEqual(ReceptionProvider.Disabled, actual.Provider.Routing.Tsunami);
        Assert.AreEqual(ReceptionProvider.Disabled, actual.Provider.Routing.Weather);
        Assert.AreEqual(ReceptionProvider.Disabled, actual.Provider.Routing.Volcano);
        Assert.AreEqual(ReceptionProvider.Disabled, actual.Provider.Routing.NankaiTrough);
        Assert.IsFalse(actual.Provider.DmdataReceiveEewWarnings);
        Assert.IsFalse(actual.Provider.DmdataReceiveEarthquakeTelegrams);
        Assert.IsFalse(actual.Provider.DmdataReceiveWeatherWarnings);
        Assert.IsFalse(actual.Provider.DmdataReceiveVolcanoTelegrams);
        Assert.IsTrue(editor.EarthquakeProviderOptions.Any(option =>
            option.Value == ReceptionProvider.Disabled && option.Label == "受信しない"));
    }

    [TestMethod]
    public async Task ManualDisconnectRequiresConfirmationButCancellationKeepsConnection()
    {
        var confirmation = new FakeConfirmationService { DisconnectResult = false };
        AppServices services = CreateServices(ProviderConnectionState.Connected);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            confirmation,
            new ImmediateUiDispatcher());

        viewModel.DisconnectCommand.Execute(null);

        Assert.AreEqual(1, confirmation.DisconnectCallCount);
        Assert.AreEqual(ProviderConnectionState.Connected, viewModel.ConnectionState);

        confirmation.DisconnectResult = true;
        viewModel.DisconnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConnectionState == ProviderConnectionState.Stopped);

        Assert.AreEqual(2, confirmation.DisconnectCallCount);
        Assert.AreEqual(ProviderConnectionState.Stopped, viewModel.ConnectionState);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task SavingProviderSettingsRestartsFaultedButActiveReception()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var source = new FaultedActiveConfigurableEventSource();
        AppServices services = CreateServices(
            ProviderConnectionState.Stopped,
            suppliedSettings: settings,
            suppliedEventSource: source);
        var viewModel = new ControlWindowViewModel(
            services,
            settings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());

        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() =>
            source.IsReaderActive &&
            viewModel.ConnectionState == ProviderConnectionState.Faulted);
        Assert.IsTrue(source.IsReaderActive);

        viewModel.Settings.ProviderMode = ProviderMode.Sandbox;
        await viewModel.SaveSettingsAsync();
        await WaitUntilAsync(() => source.ReadCount == 2 && source.IsReaderActive);

        Assert.AreEqual(1, source.StopCount);
        Assert.AreEqual(2, source.ReadCount);
        Assert.IsTrue(source.IsReaderActive);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task DmdataConnectionIsIdentifiedAsDmdataInsteadOfP2p()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings settings = defaults with
        {
            Provider = defaults.Provider with
            {
                ReceptionProvider = ReceptionProvider.Dmdata,
                Mode = ProviderMode.Production,
            },
        };
        AppServices services = CreateServices(
            ProviderConnectionState.Connected,
            suppliedSettings: settings);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());

        StringAssert.Contains(viewModel.ApiModeText, "DMDATA.JP");
        Assert.DoesNotContain("P2P地震情報", viewModel.ApiModeText);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task DisconnectedTestLibraryRunOutputsToPreviewAndObs()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qtelopper-wpf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var library = new FileTestCaseLibrary(Path.Combine(directory, "library"));
            string xml = Path.Combine(directory, "sample.xml");
            await File.WriteAllTextAsync(xml,
                "<Report><Control><Title>震源・震度に関する情報</Title></Control></Report>");
            await library.ImportFilesAsync("未接続リハーサル", [xml]);
            QuakeEvent quake = CreateHistoryQuake();
            AppServices services = CreateServices(
                ProviderConnectionState.Stopped,
                suppliedNormalizer: new StubNormalizer(quake),
                testCaseLibrary: library);
            var obsStore = new ObsSnapshotStore(services.InitialSettings.Display, services.Clock.UtcNow);
            var viewModel = new ControlWindowViewModel(
                services,
                services.InitialSettings,
                new FakeConfirmationService(),
                new ImmediateUiDispatcher(),
                obsStore);
            bool previewRequested = false;
            viewModel.ShowPreviewRequested += (_, _) => previewRequested = true;
            viewModel.SelectedLibraryCase = viewModel.TestLibraryCases.Single();

            viewModel.RunLibraryCaseCommand.Execute(null);
            await WaitUntilAsync(() =>
                previewRequested && viewModel.Overlay.HasProgram && obsStore.Read().HasProgram);

            Assert.AreEqual(ProviderConnectionState.Stopped, viewModel.ConnectionState);
            Assert.IsTrue(previewRequested);
            Assert.IsTrue(viewModel.Overlay.HasProgram);
            Assert.IsTrue(obsStore.Read().HasProgram);
            Assert.AreEqual("テストライブラリ／訓練", viewModel.Overlay.RehearsalLabel);
            StringAssert.Contains(viewModel.SelectedLibraryCase.ResultText, "合格");
            await viewModel.DisposeAsync();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProductionTestRequiresConfirmationAndAcceptedTestHasBanner()
    {
        var confirmation = new FakeConfirmationService { Result = false };
        AppServices services = CreateServices(ProviderConnectionState.Connected);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            confirmation,
            new ImmediateUiDispatcher());

        viewModel.RunTestCommand.Execute(null);

        Assert.AreEqual(1, confirmation.CallCount);
        Assert.IsFalse(viewModel.Overlay.HasProgram);

        confirmation.Result = true;
        viewModel.RunTestCommand.Execute(null);

        Assert.AreEqual(2, confirmation.CallCount);
        Assert.IsTrue(viewModel.Overlay.HasProgram);
        Assert.AreEqual("操作テスト／訓練", viewModel.Overlay.RehearsalLabel);
        Assert.IsTrue(viewModel.Overlay.Blocks.Count > 0);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task EewComparisonImmediatelyReplacesThePreviousComparison()
    {
        AppServices services = CreateServices(ProviderConnectionState.Stopped);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());

        viewModel.SelectedScenario = viewModel.Scenarios.Single(item => item.Id == "detail-scale");
        viewModel.RunTestCommand.Execute(null);
        Assert.IsFalse(viewModel.Overlay.IsEewProgram);

        viewModel.SelectedScenario = viewModel.Scenarios.Single(item => item.Id == "eew-warning");
        viewModel.RunTestCommand.Execute(null);

        Assert.IsTrue(viewModel.Overlay.IsEewProgram);
        Assert.AreEqual("操作テスト／訓練", viewModel.Overlay.RehearsalLabel);
        StringAssert.Contains(viewModel.Overlay.AccessibleText, "緊急地震速報");
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task EveryEewTrainingStageCanBeSelectedAndDisplayedIndividually()
    {
        AppServices services = CreateServices(ProviderConnectionState.Stopped);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());
        var expectedHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eew-warning"] = "緊急地震速報（気象庁）",
            ["eew-cancel"] = "緊急地震速報（取消）",
        };

        foreach ((string scenarioId, string expectedHeader) in expectedHeaders)
        {
            viewModel.SelectedScenario = viewModel.Scenarios.Single(item => item.Id == scenarioId);
            viewModel.RunTestCommand.Execute(null);

            Assert.IsTrue(viewModel.Overlay.IsEewProgram);
            Assert.AreEqual("操作テスト／訓練", viewModel.Overlay.RehearsalLabel);
            Assert.AreEqual(expectedHeader, viewModel.Overlay.Blocks[0].PrimaryText);
        }

        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task HistoryRehearsalCanBeStartedDisplayedAndStopped()
    {
        var historyLoader = new FakeHistoryRehearsalLoader([CreateHistoryQuake()]);
        AppServices services = CreateServices(
            ProviderConnectionState.Stopped,
            historyLoader: historyLoader);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());

        viewModel.StartHistoryRehearsalCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Overlay.RehearsalLabel == "履歴リハーサル／訓練");

        Assert.IsTrue(viewModel.IsHistoryRehearsalRunning);
        Assert.AreEqual("履歴リハーサル／訓練", viewModel.Overlay.RehearsalLabel);
        Assert.AreEqual(1, historyLoader.LoadCount);
        Assert.AreEqual(1, viewModel.HistoryItems.Count);
        Assert.IsNotNull(viewModel.SelectedHistoryItem);
        viewModel.StopHistoryRehearsalCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsHistoryRehearsalRunning);

        Assert.IsFalse(viewModel.IsHistoryRehearsalRunning);
        Assert.AreEqual("停止処理中…", viewModel.HistoryRehearsalStatusText);
        Assert.IsFalse(viewModel.Overlay.HasProgram);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task PastTelegramsCanBeReviewedWithoutSendingUntilOperatorRedisplaysOne()
    {
        var historyLoader = new FakeHistoryRehearsalLoader([CreateHistoryQuake()]);
        AppServices services = CreateServices(
            ProviderConnectionState.Stopped,
            historyLoader: historyLoader);
        var obsStore = new ObsSnapshotStore(services.InitialSettings.Display, services.Clock.UtcNow);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher(),
            obsStore);

        viewModel.LoadHistoryForReviewCommand.Execute(null);
        await WaitUntilAsync(() =>
            viewModel.ReceivedTelegrams.Count == 1 && !viewModel.IsTelegramHistoryLoading);

        ReceivedTelegramViewModel item = viewModel.ReceivedTelegrams.Single();
        Assert.AreEqual(1, historyLoader.LoadCount);
        Assert.AreEqual("過去電文", item.SourceText);
        Assert.IsNotEmpty(item.Pages);
        Assert.IsFalse(viewModel.Overlay.HasProgram);
        Assert.IsFalse(obsStore.Read().HasProgram);

        viewModel.SelectedReceivedTelegram = item;
        viewModel.RedisplayReceivedTelegramCommand.Execute(null);

        Assert.IsTrue(viewModel.Overlay.HasProgram);
        Assert.IsTrue(obsStore.Read().HasProgram);
        Assert.AreEqual("受信電文の再表示／訓練", viewModel.Overlay.RehearsalLabel);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task HistoryRehearsalRemovesDisabledAdvisoriesFromMixedWarningTelegram()
    {
        DateTimeOffset issuedAt = new(2026, 8, 13, 15, 40, 0, TimeSpan.FromHours(9));
        var mixedWeather = new WeatherWarningEvent(
            EventId.Create("history-mixed-weather"),
            "nii-jma-xml",
            issuedAt,
            issuedAt,
            "HISTORY-MIXED-WEATHER",
            SourceMode.HistoryRehearsal,
            new IssueInfo("熊本地方気象台", issuedAt, "VPWW53", CorrectionType.None),
            "熊本県の警報・注意報を更新しました。",
            [
                new WeatherWarningItem(
                    "御船町", "4344100", "大雨警報", "03",
                    WeatherWarningLevel.Warning, "継続", IsActive: true),
                new WeatherWarningItem(
                    "御船町", "4344100", "雷注意報", "14",
                    WeatherWarningLevel.Advisory, "継続", IsActive: true),
            ],
            isCancelled: false);
        AppSettings settings = AppSettings.CreateDefault() with
        {
            Filter = AppSettings.CreateDefault().Filter with
            {
                WeatherWarnings = true,
                WeatherAdvisories = false,
            },
        };
        AppServices services = CreateServices(
            ProviderConnectionState.Stopped,
            historyLoader: new FakeHistoryRehearsalLoader([mixedWeather]),
            suppliedSettings: settings);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());

        viewModel.StartHistoryRehearsalCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HistoryItems.Count == 1);

        WeatherWarningEvent filtered = Assert.IsInstanceOfType<WeatherWarningEvent>(
            viewModel.HistoryItems.Single().DisasterEvent);
        Assert.HasCount(1, filtered.Items);
        Assert.AreEqual("大雨警報", filtered.Items[0].KindName);
        Assert.IsFalse(viewModel.HistoryItems.Single().Program.Pages.Any(static page =>
            page.AccessibleText.Contains("雷注意報", StringComparison.Ordinal)));
        viewModel.StopHistoryRehearsalCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsHistoryRehearsalRunning);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task HistoryRehearsalIsBlockedWhileProductionIsConnected()
    {
        var historyLoader = new FakeHistoryRehearsalLoader([CreateHistoryQuake()]);
        AppServices services = CreateServices(
            ProviderConnectionState.Connected,
            historyLoader: historyLoader);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());

        viewModel.StartHistoryRehearsalCommand.Execute(null);

        Assert.AreEqual(0, historyLoader.LoadCount);
        Assert.IsFalse(viewModel.IsHistoryRehearsalRunning);
        Assert.AreEqual("本番接続中は開始できません", viewModel.HistoryRehearsalStatusText);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task HistoryRehearsalFailureIsShownAndRecordedInUiLog()
    {
        AppServices services = CreateServices(
            ProviderConnectionState.Stopped,
            historyLoader: new FailingHistoryRehearsalLoader());
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());

        viewModel.StartHistoryRehearsalCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsHistoryRehearsalRunning);

        StringAssert.Contains(viewModel.HistoryRehearsalStatusText, "失敗");
        Assert.IsTrue(viewModel.Logs.Any(entry => entry.EventName == "HistoryRehearsalLoading"));
        UiLogEntryViewModel failure = viewModel.Logs.Single(
            entry => entry.EventName == "HistoryRehearsalFailed");
        Assert.AreEqual(AppLogLevel.Error, failure.Level);
        StringAssert.Contains(failure.Message, "履歴テスト失敗");
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task ViewModelLogCollectionsStayAtFixedLimit()
    {
        AppServices services = CreateServices(ProviderConnectionState.Stopped);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());
        for (int index = 0; index < 300; index++)
        {
            await services.UiLogs.WriteAsync(new AppLogEntry(
                DateTimeOffset.UnixEpoch.AddSeconds(index),
                AppLogLevel.Information,
                $"event-{index}",
                $"message-{index}"));
        }

        Assert.HasCount(250, viewModel.Logs);
        Assert.HasCount(250, viewModel.VisibleLogs);
        Assert.AreEqual("event-50", viewModel.Logs[0].EventName);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task ReceivedMessageAddsSafeDebugSummaryWithoutRawPayload()
    {
        AppServices services = CreateServices(ProviderConnectionState.Stopped);
        var source = (FakeEventSource)services.EventSource;
        const string rawOnlyText = "この本文は記録しない";
        source.Enqueue(new RawProviderMessage(
            "p2pquake",
            $$"""
              {
                "code": 554,
                "id": "ignored-event",
                "issue": { "serial": 8 },
                "body": "{{rawOnlyText}}"
              }
              """,
            SourceMode.Sandbox,
            new DateTimeOffset(2026, 8, 1, 2, 3, 4, 567, TimeSpan.Zero)));
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher())
        {
            MinimumLogLevel = AppLogLevel.Debug,
        };

        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Logs.Any(
            entry => entry.EventName == "ProviderMessageReceived"));

        UiLogEntryViewModel summary = viewModel.Logs.Single(
            entry => entry.EventName == "ProviderMessageReceived");
        Assert.AreEqual(AppLogLevel.Debug, summary.Level);
        StringAssert.Contains(summary.Message, "受信時刻=");
        StringAssert.Contains(summary.Message, "コード=554");
        StringAssert.Contains(summary.Message, "イベントID=ignored-event");
        StringAssert.Contains(summary.Message, "種別=不明");
        StringAssert.Contains(summary.Message, "処理結果=対象外");
        StringAssert.Contains(summary.Message, "報番号=8");
        Assert.DoesNotContain(rawOnlyText, summary.Message);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task ReceivedProductionTelegramCanBeClearedAndRedisplayed()
    {
        QuakeEvent quake = CreateQuake(SourceMode.Production, "received-production-quake");
        AppServices services = CreateServices(
            ProviderConnectionState.Stopped,
            suppliedNormalizer: new StubNormalizer(quake));
        var source = (FakeEventSource)services.EventSource;
        source.Enqueue(new RawProviderMessage(
            "p2pquake",
            "{}",
            SourceMode.Production,
            services.Clock.UtcNow));
        var obsStore = new ObsSnapshotStore(services.InitialSettings.Display, services.Clock.UtcNow);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher(),
            obsStore);

        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ReceivedTelegrams.Count == 1);
        Assert.HasCount(1, viewModel.ReceivedTelegrams);

        viewModel.ClearQuakeDisplayCommand.Execute(null);
        Assert.IsFalse(viewModel.Overlay.HasProgram);
        Assert.IsFalse(obsStore.Read().HasProgram);

        viewModel.SelectedReceivedTelegram = viewModel.ReceivedTelegrams.Single();
        viewModel.RedisplayReceivedTelegramCommand.Execute(null);

        Assert.IsTrue(viewModel.Overlay.HasProgram);
        Assert.IsTrue(obsStore.Read().HasProgram);
        Assert.AreEqual("受信電文の再表示／訓練", viewModel.Overlay.RehearsalLabel);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task CategoryClearRemovesOnlyTheRequestedKind()
    {
        var clock = new FakeClock();
        AppServices services = CreateServices(ProviderConnectionState.Connected, clock);
        DisplayProgram quake = CreateSubtitleEditProgram(
            "clear-quake", EventKind.Quake, OverlayPriority.Quake,
            EndPolicy.LoopUntilReplaced, "地震", clock.UtcNow);
        DisplayProgram eew = CreateSubtitleEditProgram(
            "keep-eew", EventKind.Eew, OverlayPriority.Eew,
            EndPolicy.AutoHide, "緊急地震速報", clock.UtcNow.AddSeconds(1));
        services.DisplayCoordinator.Apply(quake);
        services.DisplayCoordinator.Apply(eew);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());

        viewModel.ClearQuakeDisplayCommand.Execute(null);

        CoordinatorSnapshot snapshot = services.DisplayCoordinator.Evaluate();
        Assert.IsNotNull(snapshot.CurrentProgram);
        Assert.AreEqual(EventKind.Eew, snapshot.CurrentProgram.Kind);
        Assert.IsFalse(snapshot.PendingPrograms.Any(static program => program.Kind == EventKind.Quake));
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task WeatherCategoryClearRemovesWeatherAndKeepsOtherKinds()
    {
        var clock = new FakeClock();
        AppServices services = CreateServices(ProviderConnectionState.Connected, clock);
        DisplayProgram quake = CreateSubtitleEditProgram(
            "keep-quake", EventKind.Quake, OverlayPriority.Quake,
            EndPolicy.LoopUntilReplaced, "地震", clock.UtcNow);
        DisplayProgram weather = CreateSubtitleEditProgram(
            "clear-weather", EventKind.WeatherWarning, OverlayPriority.WeatherWarning,
            EndPolicy.LoopUntilReplaced, "気象", clock.UtcNow.AddSeconds(1));
        services.DisplayCoordinator.Apply(weather);
        services.DisplayCoordinator.Apply(quake);
        var obsStore = new ObsSnapshotStore(services.InitialSettings.Display, clock.UtcNow);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher(),
            obsStore);

        viewModel.ClearWeatherDisplayCommand.Execute(null);

        CoordinatorSnapshot snapshot = services.DisplayCoordinator.Evaluate();
        Assert.IsNotNull(snapshot.CurrentProgram);
        Assert.AreEqual(EventKind.Quake, snapshot.CurrentProgram.Kind);
        Assert.IsFalse(snapshot.PendingPrograms.Any(static program =>
            program.Kind == EventKind.WeatherWarning));
        Assert.AreNotEqual(EventKind.WeatherWarning, obsStore.Read().Kind);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task ObsServerStatusClientCountAndUrlCopyAreBoundToViewModel()
    {
        AppServices services = CreateServices(ProviderConnectionState.Stopped);
        var obsStore = new ObsSnapshotStore(services.InitialSettings.Display, services.Clock.UtcNow);
        var obsServer = new FakeObsServer();
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher(),
            obsStore,
            obsServer);
        string copied = string.Empty;
        viewModel.CopyTextRequested += (_, text) => copied = text;

        await WaitUntilAsync(() => obsServer.StartCount == 1);
        Assert.AreEqual("稼働中 127.0.0.1:18432", viewModel.ObsStatusText);
        Assert.AreEqual(obsServer.OverlayUrl, viewModel.ObsUrlText);
        obsServer.SetClientCount(2);
        Assert.AreEqual(2, viewModel.ObsClientCount);

        viewModel.CopyObsUrlCommand.Execute(null);
        Assert.AreEqual(obsServer.OverlayUrl, copied);

        viewModel.Settings.ObsSnapshotIntervalMilliseconds = 50;
        await viewModel.SaveSettingsAsync();
        Assert.AreEqual(50, obsServer.SnapshotIntervalMilliseconds);

        viewModel.Settings.ObsEnabled = false;
        await viewModel.SaveSettingsAsync();
        Assert.AreEqual(1, obsServer.StopCount);
        Assert.AreEqual("無効", viewModel.ObsStatusText);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task ObsAudioTestCannotBeQueuedWhileProductionIsConnected()
    {
        AppServices services = CreateServices(ProviderConnectionState.Connected);
        var obsStore = new ObsSnapshotStore(
            services.InitialSettings.Display,
            services.Clock.UtcNow);
        var obsServer = new FakeObsServer();
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher(),
            obsStore,
            obsServer);
        await WaitUntilAsync(() => obsServer.StartCount == 1);
        obsServer.SetClientCount(1);
        long audioSequenceBeforeTest = obsStore.Read().AudioSequence;

        Assert.IsFalse(viewModel.TestAudioCommand.CanExecute(AudioCueId.EewInitial));

        // Exercise the method-level safety guard as well. RelayCommand.Execute
        // itself does not enforce CanExecute when called programmatically.
        viewModel.TestAudioCommand.Execute(AudioCueId.EewInitial);
        await WaitUntilAsync(() => viewModel.Logs.Any(
            entry => entry.EventName == "AudioTestBlockedInProduction"));

        Assert.AreEqual(audioSequenceBeforeTest, obsStore.Read().AudioSequence);
        Assert.IsFalse(viewModel.Logs.Any(entry => entry.EventName == "AudioTestQueued"));
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task ClearDisplayCommandClearsWpfObsAndCoordinatorWithoutStoppingReception()
    {
        AppServices services = CreateServices(ProviderConnectionState.Connected);
        TestScenario scenario = TestScenarioCatalog.Create(services.Clock.UtcNow)[0];
        services.DisplayCoordinator.Apply(
            services.PageComposer.Compose(scenario.Event, services.InitialSettings.Display));
        var obsStore = new ObsSnapshotStore(services.InitialSettings.Display, services.Clock.UtcNow);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher(),
            obsStore);
        Assert.IsTrue(viewModel.Overlay.HasProgram);

        viewModel.ClearDisplayCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Logs.Any(entry => entry.EventName == "DisplayCleared"));

        Assert.IsFalse(viewModel.Overlay.HasProgram);
        Assert.IsFalse(obsStore.Read().HasProgram);
        Assert.IsNull(services.DisplayCoordinator.Evaluate().CurrentProgram);
        Assert.AreEqual(ProviderConnectionState.Connected, viewModel.ConnectionState);
        StringAssert.Contains(viewModel.ReceptionStatusText, "受信は継続中");
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task SubtitleEditUpdatesPreviewAndObsWithoutChangingCoordinatorOrReplayingAudio()
    {
        AppServices services = CreateServices(ProviderConnectionState.Connected);
        TestScenario scenario = TestScenarioCatalog.Create(services.Clock.UtcNow)[0];
        services.DisplayCoordinator.Apply(
            services.PageComposer.Compose(scenario.Event, services.InitialSettings.Display));
        var obsStore = new ObsSnapshotStore(services.InitialSettings.Display, services.Clock.UtcNow);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher(),
            obsStore);
        DisplayProgram? source = null;
        DisplayProgram? editable = null;
        viewModel.EditSubtitleRequested += (original, displayed) =>
        {
            source = original;
            editable = displayed;
        };

        viewModel.EditSubtitleCommand.Execute(null);
        Assert.IsNotNull(source);
        Assert.IsNotNull(editable);
        long audioSequence = obsStore.Read().AudioSequence;
        DisplayPage originalPage = editable.Pages[0];
        DisplayBlock originalBlock = originalPage.Blocks[0];
        var editedBlock = originalBlock with { PrimaryText = "手動編集テスト" };
        var editedPage = originalPage with
        {
            Blocks = [editedBlock, .. originalPage.Blocks.Skip(1)],
            AccessibleText = "手動編集テスト",
        };
        DisplayProgram edited = editable with
        {
            Pages = [editedPage, .. editable.Pages.Skip(1)],
        };

        Assert.IsTrue(viewModel.TryApplySubtitleEdit(source, edited));

        Assert.IsTrue(viewModel.Overlay.Blocks.Any(
            block => block.PrimaryText == "手動編集テスト"));
        Assert.IsTrue(obsStore.Read().Blocks.Any(
            block => block.PrimaryText == "手動編集テスト"));
        Assert.IsFalse(services.DisplayCoordinator.Evaluate().CurrentProgram!.Pages
            .SelectMany(static page => page.Blocks)
            .Any(block => block.PrimaryText == "手動編集テスト"));
        Assert.AreEqual(audioSequence, obsStore.Read().AudioSequence);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task PendingSubtitleEditingIsDisabled()
    {
        var clock = new FakeClock();
        AppServices services = CreateServices(ProviderConnectionState.Connected, clock);
        DisplayProgram quake = CreateSubtitleEditProgram(
            "pending-weather",
            EventKind.WeatherWarning,
            OverlayPriority.WeatherWarning,
            EndPolicy.LoopUntilReplaced,
            "待機中の元本文",
            clock.UtcNow);
        DisplayProgram eew = CreateSubtitleEditProgram(
            "active-eew",
            EventKind.Eew,
            OverlayPriority.Eew,
            EndPolicy.AutoHide,
            "表示中の緊急地震速報",
            clock.UtcNow.AddSeconds(1));
        services.DisplayCoordinator.Apply(quake);
        services.DisplayCoordinator.Apply(eew);
        var obsStore = new ObsSnapshotStore(services.InitialSettings.Display, clock.UtcNow);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher(),
            obsStore);
        Assert.IsFalse(viewModel.EditPendingSubtitleCommand.CanExecute(null));
        viewModel.EditPendingSubtitleCommand.Execute(null);
        Assert.IsFalse(viewModel.Overlay.Blocks.Any(
            block => block.PrimaryText == "表示前に編集済み"));
        Assert.IsFalse(obsStore.Read().Blocks.Any(
            block => block.PrimaryText == "表示前に編集済み"));
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task FifteenSecondRuntimeGapRequestsObsWebSocketRecovery()
    {
        var clock = new FakeClock();
        AppServices services = CreateServices(ProviderConnectionState.Connected, clock);
        var viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());

        clock.Advance(TimeSpan.FromSeconds(16));
        await WaitUntilAsync(() =>
            ((FakeEventSource)services.EventSource).LastReconnectReason == ReconnectReason.RuntimeGap);

        Assert.AreEqual(
            ReconnectReason.RuntimeGap,
            ((FakeEventSource)services.EventSource).LastReconnectReason);
        await viewModel.DisposeAsync();
    }

    [TestMethod]
    public async Task AxisTokenRefreshUpdatesEditorAndProtectedSettings()
    {
        const string previousToken = "previous-axis-token";
        const string refreshedToken = "refreshed-axis-token";
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings settings = defaults with
        {
            Provider = defaults.Provider with
            {
                ReceptionProvider = ReceptionProvider.Axis,
                AxisProtectedAccessToken = AxisCredentialProtector.Protect(previousToken),
            },
        };
        var refreshService = new FakeAxisTokenRefreshService(refreshedToken);
        AppServices services = CreateServices(
            ProviderConnectionState.Stopped,
            suppliedSettings: settings,
            axisTokenRefreshService: refreshService);
        var viewModel = new ControlWindowViewModel(
            services,
            settings,
            new FakeConfirmationService(),
            new ImmediateUiDispatcher());

        await WaitUntilAsync(() => viewModel.Settings.AxisAccessToken == refreshedToken);
        AppSettings saved = await services.SettingsStore.LoadAsync();

        Assert.AreEqual(1, refreshService.CallCount);
        Assert.AreEqual(refreshedToken, viewModel.Settings.AxisAccessToken);
        Assert.AreEqual(
            refreshedToken,
            AxisCredentialProtector.Unprotect(saved.Provider.AxisProtectedAccessToken));
        await viewModel.DisposeAsync();
    }

    private static AppServices CreateServices(
        ProviderConnectionState connectionState,
        FakeClock? suppliedClock = null,
        IHistoryRehearsalLoader? historyLoader = null,
        AppSettings? suppliedSettings = null,
        IEventNormalizer? suppliedNormalizer = null,
        ITestCaseLibrary? testCaseLibrary = null,
        IAxisTokenRefreshService? axisTokenRefreshService = null,
        IEventSource? suppliedEventSource = null)
    {
        AppSettings settings = suppliedSettings ?? AppSettings.CreateDefault();
        FakeClock clock = suppliedClock ?? new FakeClock();
        var logs = new UiLogBuffer();
        IEventSource source = suppliedEventSource ??
            new FakeEventSource(clock.UtcNow, connectionState);
        IEventNormalizer normalizer = suppliedNormalizer ?? new IgnoringNormalizer();
        var composer = new PageComposer();
        var coordinator = new PriorityCoordinator(clock, settings.Display);
        var pipeline = new EventIngestionPipeline(
            normalizer,
            new EventVersionCache(),
            composer,
            coordinator,
            settings.Display);
        return new AppServices(
            clock,
            settings,
            new FakeIdGenerator(),
            new InMemorySettingsStore(settings),
            logs,
            logs,
            ProviderOptions.Production,
            normalizer,
            composer,
            coordinator,
            source,
            pipeline,
            new EventReceptionService(source, pipeline),
            HistoryRehearsalLoader: historyLoader,
            TestCaseLibrary: testCaseLibrary,
            AxisTokenRefreshService: axisTokenRefreshService);
    }

    private static DisplayProgram CreateSubtitleEditProgram(
        string id,
        EventKind kind,
        OverlayPriority priority,
        EndPolicy endPolicy,
        string text,
        DateTimeOffset now)
    {
        var block = new DisplayBlock(
            string.Empty,
            text,
            string.Empty,
            kind == EventKind.Eew ? DisplayStyleTokens.EewWarning : DisplayStyleTokens.Summary);
        var page = new DisplayPage(0, [block], text, null);
        return new DisplayProgram(
            id,
            EventId.Create(id),
            kind,
            SourceMode.Production,
            now,
            priority,
            [page],
            now,
            endPolicy,
            string.Empty);
    }

    private static QuakeEvent CreateHistoryQuake()
        => CreateQuake(SourceMode.HistoryRehearsal, "history-quake");

    private static QuakeEvent CreateQuake(SourceMode sourceMode, string eventId)
    {
        QuakeEvent source = (QuakeEvent)TestScenarioCatalog.Create(
            new DateTimeOffset(2026, 8, 1, 3, 0, 0, TimeSpan.Zero))
            .Single(item => item.Id == "detail-scale")
            .Event;
        return new QuakeEvent(
            EventId.Create(eventId),
            source.Provider,
            source.IssuedAt,
            source.ReceivedAt,
            source.Signature,
            sourceMode,
            source.Issue,
            source.IssueType,
            source.Earthquake,
            source.Points,
            source.FreeFormComment);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private sealed class FakeConfirmationService : IConfirmationService
    {
        public bool Result { get; set; } = true;

        public bool DisconnectResult { get; set; } = true;

        public int CallCount { get; private set; }

        public int DisconnectCallCount { get; private set; }

        public bool ConfirmProductionTest()
        {
            CallCount++;
            return Result;
        }

        public bool ConfirmDisconnect()
        {
            DisconnectCallCount++;
            return DisconnectResult;
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

        private long Timestamp { get; set; }

        public long GetTimestamp() => Timestamp;

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            TimeSpan.FromSeconds(Timestamp - startingTimestamp);

        public void Advance(TimeSpan value)
        {
            UtcNow += value;
            Timestamp += (long)value.TotalSeconds;
        }
    }

    private sealed class FakeIdGenerator : IIdGenerator
    {
        private int _value;

        public string NewId() => (++_value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FakeAxisTokenRefreshService(string refreshedToken)
        : IAxisTokenRefreshService
    {
        public int CallCount { get; private set; }

        public ValueTask<AxisTokenRefreshResult> RefreshIfDueAsync(
            Uri apiBaseUri,
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(new AxisTokenRefreshResult(
                AxisTokenRefreshOutcome.Refreshed,
                refreshedToken));
        }
    }

    private sealed class IgnoringNormalizer : IEventNormalizer
    {
        public NormalizeResult Normalize(RawProviderMessage raw)
        {
            ArgumentNullException.ThrowIfNull(raw);
            return NormalizeResult.Ignored();
        }
    }

    private sealed class StubNormalizer(DisasterEvent disasterEvent) : IEventNormalizer
    {
        public NormalizeResult Normalize(RawProviderMessage raw)
        {
            ArgumentNullException.ThrowIfNull(raw);
            return NormalizeResult.Success(disasterEvent);
        }
    }

    private sealed class FakeHistoryRehearsalLoader(IReadOnlyList<DisasterEvent> events)
        : IHistoryRehearsalLoader
    {
        public int LoadCount { get; private set; }

        public Task<HistoryRehearsalLoadResult> LoadAsync(
            HistorySettings history,
            ProviderSettings provider,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult(new HistoryRehearsalLoadResult(events, 0, 0));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingHistoryRehearsalLoader : IHistoryRehearsalLoader
    {
        public Task<HistoryRehearsalLoadResult> LoadAsync(
            HistorySettings history,
            ProviderSettings provider,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("履歴テスト失敗: HTTP 400");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEventSource : IEventSource
    {
        private readonly Queue<RawProviderMessage> _messages = [];

        public FakeEventSource(
            DateTimeOffset timestamp,
            ProviderConnectionState connectionState)
        {
            Connection = new ProviderConnectionSnapshot(connectionState, timestamp);
        }

        public ProviderConnectionSnapshot Connection { get; private set; }

        public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged;

        public ReconnectReason? LastReconnectReason { get; private set; }

        public void Enqueue(RawProviderMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            _messages.Enqueue(message);
        }

        public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            while (_messages.TryDequeue(out RawProviderMessage? message))
            {
                yield return message;
            }
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connection = Connection with { State = ProviderConnectionState.Stopped };
            ConnectionChanged?.Invoke(this, Connection);
            return ValueTask.CompletedTask;
        }

        public void RequestReconnect(ReconnectReason reason)
        {
            LastReconnectReason = reason;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FaultedActiveConfigurableEventSource :
        IEventSource,
        IProviderConfigurableEventSource
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _readerStop;
        private int _readerActive;

        public ProviderConnectionSnapshot Connection { get; private set; } = new(
            ProviderConnectionState.Stopped,
            DateTimeOffset.UtcNow);

        public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged;

        public int ReadCount { get; private set; }

        public int StopCount { get; private set; }

        public bool IsReaderActive => Volatile.Read(ref _readerActive) != 0;

        public void ConfigureProvider(ProviderSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            if (IsReaderActive)
            {
                throw new InvalidOperationException(
                    "Information providers cannot be changed while reception is active.");
            }
        }

        public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _readerActive, 1) != 0)
            {
                throw new InvalidOperationException("Only one test reader is allowed.");
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate)
            {
                _readerStop = stop;
                ReadCount++;
            }

            Transition(ProviderConnectionState.Faulted);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_readerStop, stop))
                    {
                        _readerStop = null;
                    }
                }

                Volatile.Write(ref _readerActive, 0);
                Transition(ProviderConnectionState.Stopped);
            }

            yield break;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationTokenSource? stop;
            lock (_gate)
            {
                StopCount++;
                stop = _readerStop;
            }

            try
            {
                stop?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            return ValueTask.CompletedTask;
        }

        public void RequestReconnect(ReconnectReason reason)
        {
        }

        public ValueTask DisposeAsync() => StopAsync();

        private void Transition(ProviderConnectionState state)
        {
            Connection = new ProviderConnectionSnapshot(state, DateTimeOffset.UtcNow);
            ConnectionChanged?.Invoke(this, Connection);
        }
    }

    private sealed class FakeObsServer : IObsLocalViewServer
    {
        public event Action<int>? ClientCountChanged;

        public bool IsRunning { get; private set; }

        public int Port { get; private set; }

        public int ClientCount { get; private set; }

        public int SnapshotIntervalMilliseconds { get; private set; } =
            ObsSettings.DefaultSnapshotIntervalMilliseconds;

        public string LastAudioCue { get; set; } = string.Empty;

        public string LastAudioPlaybackResult { get; set; } = "None";

        public DateTimeOffset? LastAudioPlaybackAtUtc { get; set; }

        public string OverlayUrl => IsRunning
            ? $"http://127.0.0.1:{Port}/overlay/?token=TEST"
            : string.Empty;

        public string EewUrl => IsRunning
            ? $"http://127.0.0.1:{Port}/eew/?token=TEST"
            : string.Empty;

        public string TsunamiUrl => IsRunning
            ? $"http://127.0.0.1:{Port}/tsunami/?token=TEST"
            : string.Empty;

        public string WeatherUrl => IsRunning
            ? $"http://127.0.0.1:{Port}/weather/?token=TEST"
            : string.Empty;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public void UpdateSnapshotInterval(int milliseconds)
        {
            SnapshotIntervalMilliseconds = milliseconds;
        }

        public Task StartAsync(int port, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = true;
            Port = port == 0 ? 18432 : port;
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRunning)
            {
                StopCount++;
            }

            IsRunning = false;
            Port = 0;
            SetClientCount(0);
            return Task.CompletedTask;
        }

        public void SetClientCount(int count)
        {
            ClientCount = count;
            ClientCountChanged?.Invoke(count);
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
