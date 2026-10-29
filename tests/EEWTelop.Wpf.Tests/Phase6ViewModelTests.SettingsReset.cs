using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Infrastructure.Axis.Security;
using EEWTelop.Infrastructure.Dmdata.Security;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.Services;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

public sealed partial class Phase6ViewModelTests
{
    [TestMethod]
    public async Task ResetAllSettingsDeclinedLeavesSavedAndUnsavedSettingsUntouched()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var services = CreateServices(ProviderConnectionState.Stopped, suppliedSettings: settings);
        var confirmation = new FakeConfirmationService();
        await using var viewModel = new ControlWindowViewModel(services, settings, confirmation, new ImmediateUiDispatcher());
        SettingsEditorViewModel editor = viewModel.Settings;
        editor.PageDurationSeconds = 9;
        int replacements = 0;
        viewModel.SettingsEditorChanged += (_, _) => replacements++;

        await viewModel.ResetAllSettingsAsync();

        Assert.AreEqual(1, confirmation.ResetAllSettingsCallCount);
        Assert.AreSame(editor, viewModel.Settings);
        Assert.AreEqual(9, editor.PageDurationSeconds);
        Assert.AreEqual(settings, await services.SettingsStore.LoadAsync());
        Assert.AreEqual(0, replacements);
        Assert.IsFalse(viewModel.IsResettingAllSettings);
        Assert.IsFalse(services.UiLogs.GetSnapshot().Any(entry => entry.EventName == "AllSettingsReset"));
    }

    [TestMethod]
    public async Task ResetAllSettingsConfirmedRestoresEntireDocumentAndClearsCredentials()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings settings = defaults with
        {
            Provider = defaults.Provider with
            {
                DmdataProtectedCredential = DmdataCredentialProtector.Protect("test-key"),
                AxisProtectedAccessToken = AxisCredentialProtector.Protect("test-token"),
                DmdataOAuthClientId = "CId.custom",
                JmaXmlAutoFallback = false,
            },
            Filter = defaults.Filter with { Quake = false, WeatherAdvisories = true },
            Display = defaults.Display with
            {
                PageDurationSeconds = 9,
                UseVerticalEewWarningLayout = true,
                SubtitlePhraseOverrides = new Dictionary<string, string> { ["test"] = "変更した定型文" },
            },
            Obs = defaults.Obs with
            {
                Enabled = false,
                Port = 12345,
                WebSocketProtectedPassword = ObsCredentialProtector.Protect("test-password"),
            },
            Audio = defaults.Audio with { Muted = true, EewInitialFilePath = "test.wav" },
            History = defaults.History with { Repeat = true, HideModeBanner = true },
            Compatibility = defaults.Compatibility with { EnrichQuakeById = true },
            Log = defaults.Log with { UiMaxEntries = 100 },
            Safety = defaults.Safety with { RestoreRehearsalState = !defaults.Safety.RestoreRehearsalState },
            // These fields have no controls in the editor and must also return to defaults.
            Operations = defaults.Operations with { AlertCoalescingSeconds = 123 },
        };
        var oauth = new FakeDmdataOAuthService();
        var services = CreateServices(ProviderConnectionState.Stopped, suppliedSettings: settings, dmdataOAuthService: oauth);
        var confirmation = new FakeConfirmationService { ResetAllSettingsResult = true };
        await using var viewModel = new ControlWindowViewModel(services, settings, confirmation, new ImmediateUiDispatcher());
        SettingsEditorViewModel oldEditor = viewModel.Settings;
        int replacements = 0;
        viewModel.SettingsEditorChanged += (oldValue, newValue) =>
        {
            Assert.AreSame(oldEditor, oldValue);
            Assert.AreSame(viewModel.Settings, newValue);
            replacements++;
        };

        await viewModel.ResetAllSettingsAsync();

        Assert.AreEqual(JsonSerializer.Serialize(defaults), JsonSerializer.Serialize(await services.SettingsStore.LoadAsync()));
        Assert.AreNotSame(oldEditor, viewModel.Settings);
        Assert.AreEqual(1, replacements);
        Assert.AreEqual(string.Empty, viewModel.Settings.DmdataCredential);
        Assert.AreEqual(string.Empty, viewModel.Settings.AxisAccessToken);
        Assert.AreEqual(string.Empty, viewModel.Settings.ObsWebSocketPassword);
        Assert.AreEqual(defaults.Display.PageDurationSeconds, viewModel.Settings.PageDurationSeconds);
        Assert.IsFalse(oauth.Revoked);
        Assert.IsTrue(services.UiLogs.GetSnapshot().Any(entry => entry.EventName == "AllSettingsReset"));
        Assert.IsFalse(viewModel.IsResettingAllSettings);
    }

    [TestMethod]
    [DataRow("display")]
    [DataRow("reception")]
    [DataRow("audio")]
    public async Task ResetAllSettingsKeepsSectionResetCommandsBoundToTheCurrentEditor(string section)
    {
        AppSettings settings = AppSettings.CreateDefault();
        var services = CreateServices(ProviderConnectionState.Stopped);
        await using var viewModel = new ControlWindowViewModel(services, settings,
            new FakeConfirmationService { ResetAllSettingsResult = true }, new ImmediateUiDispatcher());
        await viewModel.ResetAllSettingsAsync();
        switch (section)
        {
            case "display":
                viewModel.Settings.PageDurationSeconds = 12;
                viewModel.ResetDisplaySettingsCommand.Execute(null);
                Assert.AreEqual(settings.Display.PageDurationSeconds, viewModel.Settings.PageDurationSeconds);
                break;
            case "reception":
                viewModel.Settings.DmdataCredential = "new-test-key";
                viewModel.ResetReceptionSettingsCommand.Execute(null);
                Assert.AreEqual(string.Empty, viewModel.Settings.DmdataCredential);
                break;
            case "audio":
                viewModel.Settings.AudioMuted = true;
                viewModel.ResetAudioSettingsCommand.Execute(null);
                Assert.AreEqual(settings.Audio.Muted, viewModel.Settings.AudioMuted);
                break;
        }
    }

    [TestMethod]
    public async Task ResetAllSettingsIsBlockedWhileOAuthAuthorizationIsPending()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var oauth = new FakeDmdataOAuthService { WaitForCancellation = true };
        var services = CreateServices(ProviderConnectionState.Stopped, suppliedSettings: settings, dmdataOAuthService: oauth);
        var confirmation = new FakeConfirmationService { ResetAllSettingsResult = true };
        await using var viewModel = new ControlWindowViewModel(services, settings, confirmation, new ImmediateUiDispatcher());
        viewModel.Settings.WeatherProvider = ReceptionProvider.Dmdata;
        SettingsEditorViewModel editor = viewModel.Settings;
        Task authorization = viewModel.AuthorizeDmdataOAuthAsync(static _ => { });
        try
        {
            Assert.IsTrue(viewModel.IsDmdataOAuthBusy);
            Assert.IsFalse(viewModel.ResetAllSettingsCommand.CanExecute(null));
            await viewModel.ResetAllSettingsAsync();
            Assert.AreEqual(0, confirmation.ResetAllSettingsCallCount);
            Assert.AreSame(editor, viewModel.Settings);
            Assert.AreEqual(settings, await services.SettingsStore.LoadAsync());
        }
        finally
        {
            viewModel.CancelDmdataOAuth();
            await authorization;
        }
        Assert.IsTrue(viewModel.ResetAllSettingsCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ResetAllSettingsBlocksDuplicateResetAndConflictingActionsWhileSaving()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var store = new ResetTestSettingsStore(settings) { PauseSave = true };
        var services = CreateServices(ProviderConnectionState.Stopped, dmdataOAuthService: new FakeDmdataOAuthService())
            with { SettingsStore = store };
        var confirmation = new FakeConfirmationService { ResetAllSettingsResult = true };
        await using var viewModel = new ControlWindowViewModel(services, settings, confirmation, new ImmediateUiDispatcher());
        Task reset = viewModel.ResetAllSettingsAsync();
        try
        {
            await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(viewModel.IsResettingAllSettings);
            Assert.IsFalse(viewModel.ResetAllSettingsCommand.CanExecute(null));
            Assert.IsFalse(viewModel.SaveSettingsCommand.CanExecute(null));
            Assert.IsFalse(viewModel.ConnectCommand.CanExecute(null));
            Assert.IsFalse(viewModel.CanManageDmdataOAuth);
            await viewModel.ResetAllSettingsAsync();
            await viewModel.SaveSettingsAsync();
            Assert.AreEqual(1, confirmation.ResetAllSettingsCallCount);
            Assert.AreEqual(1, store.SaveCount);
        }
        finally
        {
            store.ReleaseSave.TrySetResult(true);
            await reset;
        }
        Assert.IsFalse(viewModel.IsResettingAllSettings);
        Assert.IsTrue(viewModel.ResetAllSettingsCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ResetAllSettingsWaitsForAnEarlierSaveAndLeavesDefaultsOnDisk()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var store = new ResetTestSettingsStore(settings) { PauseSave = true };
        var services = CreateServices(ProviderConnectionState.Stopped) with { SettingsStore = store };
        await using var viewModel = new ControlWindowViewModel(services, settings,
            new FakeConfirmationService { ResetAllSettingsResult = true }, new ImmediateUiDispatcher());
        viewModel.Settings.PageDurationSeconds = 9;
        Task save = viewModel.SaveSettingsAsync();
        Task reset = Task.CompletedTask;
        try
        {
            await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            reset = viewModel.ResetAllSettingsAsync();
            Assert.IsTrue(viewModel.IsResettingAllSettings);
            Assert.AreEqual(1, store.SaveCount);
        }
        finally
        {
            store.ReleaseSave.TrySetResult(true);
            await Task.WhenAll(save, reset).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(2, store.SaveCount);
        Assert.AreEqual(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(await store.LoadAsync()));
    }

    [TestMethod]
    public async Task ResetAllSettingsReconfiguresAnActiveReceiverWithoutASecondReader()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings settings = defaults with { Provider = defaults.Provider with { Mode = ProviderMode.Sandbox } };
        var source = new FaultedActiveConfigurableEventSource();
        var services = CreateServices(ProviderConnectionState.Stopped, suppliedSettings: settings, suppliedEventSource: source);
        await using var viewModel = new ControlWindowViewModel(services, settings,
            new FakeConfirmationService { ResetAllSettingsResult = true }, new ImmediateUiDispatcher());
        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => source.IsReaderActive);
        Assert.AreEqual(1, source.ReadCount);

        await viewModel.ResetAllSettingsAsync();

        Assert.AreEqual(2, source.ReadCount);
        Assert.IsTrue(source.IsReaderActive);
        Assert.IsGreaterThanOrEqualTo(1, source.StopCount);
        Assert.AreEqual(ProviderMode.Production, (await services.SettingsStore.LoadAsync()).Provider.Mode);
        Assert.IsFalse(services.UiLogs.GetSnapshot().Any(entry => entry.EventName == "AllSettingsResetFailed"));
    }

    [TestMethod]
    public async Task ResetAllSettingsFinishesSavingBeforeTheViewModelIsDisposed()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var store = new ResetTestSettingsStore(settings) { PauseSave = true };
        var services = CreateServices(ProviderConnectionState.Stopped) with { SettingsStore = store };
        await using var viewModel = new ControlWindowViewModel(services, settings,
            new FakeConfirmationService { ResetAllSettingsResult = true }, new ImmediateUiDispatcher());
        Task reset = viewModel.ResetAllSettingsAsync();
        Task dispose = Task.CompletedTask;
        try
        {
            await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dispose = viewModel.DisposeAsync().AsTask();
            Assert.IsFalse(dispose.IsCompleted);
        }
        finally
        {
            store.ReleaseSave.TrySetResult(true);
            await Task.WhenAll(reset, dispose).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(1, store.SaveCount);
        Assert.IsFalse(viewModel.IsResettingAllSettings);
    }

    [TestMethod]
    public async Task ResetAllSettingsSaveFailureReportsFailureAndReenablesControls()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var store = new ResetTestSettingsStore(settings) { FailSave = true };
        var services = CreateServices(ProviderConnectionState.Stopped) with { SettingsStore = store };
        await using var viewModel = new ControlWindowViewModel(services, settings,
            new FakeConfirmationService { ResetAllSettingsResult = true }, new ImmediateUiDispatcher());

        await viewModel.ResetAllSettingsAsync();

        Assert.IsFalse(viewModel.IsResettingAllSettings);
        Assert.IsTrue(viewModel.ResetAllSettingsCommand.CanExecute(null));
        StringAssert.Contains(viewModel.ReceptionStatusText, "完了できませんでした");
        Assert.IsTrue(services.UiLogs.GetSnapshot().Any(entry => entry.EventName == "AllSettingsResetFailed"));
        Assert.IsFalse(services.UiLogs.GetSnapshot().Any(entry => entry.EventName == "AllSettingsReset"));
    }

    private sealed class ResetTestSettingsStore(AppSettings settings) : ISettingsStore
    {
        private AppSettings _settings = settings;
        public bool PauseSave { get; init; }
        public bool FailSave { get; init; }
        public int SaveCount { get; private set; }
        public TaskCompletionSource<bool> SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_settings);

        public async ValueTask SaveAsync(AppSettings value, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            SaveStarted.TrySetResult(true);
            if (PauseSave) await ReleaseSave.Task.WaitAsync(cancellationToken);
            if (FailSave) throw new IOException("Test settings write failure");
            _settings = value;
        }
    }
}
