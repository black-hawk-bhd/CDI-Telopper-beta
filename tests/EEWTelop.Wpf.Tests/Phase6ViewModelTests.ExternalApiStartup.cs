using System.Text.Json;
using System.Text.Json.Nodes;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Wpf.Bootstrap;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.Services;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

public sealed partial class Phase6ViewModelTests
{
    [TestMethod]
    public void ExternalApiStartupDefaultsOffWhenSettingIsMissing()
    {
        ObsSettings defaults = AppSettings.CreateDefault().Obs;
        Assert.IsFalse(defaults.ExternalApiAutoEnableOnStartup);
        JsonObject json = JsonSerializer.SerializeToNode(defaults)!.AsObject();
        json.Remove(nameof(ObsSettings.ExternalApiAutoEnableOnStartup));

        Assert.IsFalse(json.Deserialize<ObsSettings>()!.ExternalApiAutoEnableOnStartup);
    }

    [TestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    public async Task ExternalApiStartupRequiresSavedOptInAndRunningLocalView(
        bool localViewEnabled, bool startupEnabled, bool expectedEnabled)
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults)
        {
            ObsEnabled = localViewEnabled,
            ExternalApiAutoEnableOnStartup = startupEnabled,
        };
        AppSettings saved = editor.ToSettings(defaults);
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!;
        AppServices services = CreateServices(ProviderConnectionState.Stopped, suppliedSettings: settings);
        var server = new FakeObsServer();
        await using var viewModel = new ControlWindowViewModel(
            services, settings, new FakeConfirmationService(), new ImmediateUiDispatcher(),
            new ObsSnapshotStore(settings.Display, services.Clock.UtcNow), server);

        await WaitUntilAsync(() => viewModel.ObsStatusText != "停止中");

        Assert.AreEqual(localViewEnabled, server.IsRunning);
        Assert.AreEqual(startupEnabled, viewModel.Settings.ExternalApiAutoEnableOnStartup);
        Assert.AreEqual(expectedEnabled, viewModel.ExternalApiEnabled);
        Assert.AreEqual(expectedEnabled, viewModel.ExternalApiUrlText.Length > 0);
        Assert.IsFalse(viewModel.RehearsalApiEnabled);
    }

    [TestMethod]
    public async Task SavingExternalApiStartupPreferenceDoesNotChangeCurrentSession()
    {
        AppServices services = CreateServices(ProviderConnectionState.Stopped);
        AppSettings settings = services.InitialSettings;
        await using var viewModel = new ControlWindowViewModel(
            services, settings, new FakeConfirmationService(), new ImmediateUiDispatcher(),
            new ObsSnapshotStore(settings.Display, services.Clock.UtcNow), new FakeObsServer());

        viewModel.Settings.ExternalApiAutoEnableOnStartup = true;
        await viewModel.SaveSettingsAsync();
        Assert.IsFalse(viewModel.ExternalApiEnabled);

        viewModel.ExternalApiEnabled = true;
        viewModel.Settings.ExternalApiAutoEnableOnStartup = false;
        await viewModel.SaveSettingsAsync();
        Assert.IsTrue(viewModel.ExternalApiEnabled);
    }
}
