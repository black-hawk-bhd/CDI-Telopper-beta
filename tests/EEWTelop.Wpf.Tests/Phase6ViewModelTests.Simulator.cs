using System.Text.Json;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.Services;
using EEWTelop.Wpf.Testing;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

public sealed partial class Phase6ViewModelTests
{
    private const string SimulatorQuakeId = "20240101161010";
    private const string SimulatorTsunamiCaution = "念のため津波に注意してください。";

    [TestMethod]
    [DataRow(SimulatorQuakeId, "Warning", false, false, true)]
    [DataRow(SimulatorQuakeId, "Warning", false, true, true)]
    [DataRow("20240101160608 20240101161010", "Watch", false, false, true)]
    [DataRow("20240101160608", "MajorWarning", false, false, false)]
    [DataRow(SimulatorQuakeId, "Forecast", false, false, false)]
    [DataRow(SimulatorQuakeId, "Warning", true, false, false)]
    [DataRow("practice-quake", "Warning", false, false, true)]
    public async Task SimulatorTsunamiUpdateClearsOnlyLinkedScalePrompts(
        string warningId, string grade, bool released, bool queued, bool clearsQuake)
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings settings = defaults with
        {
            Display = defaults.Display with
            {
                OmitScalePromptCautionWhenTsunamiWarningActive = true,
                SuppressScalePromptWhenTsunamiWarningActive = true,
            },
        };
        var services = CreateServices(ProviderConnectionState.Stopped, suppliedSettings: settings);
        DateTimeOffset now = services.Clock.UtcNow;
        var obs = new ObsSnapshotStore(settings.Display, now);
        await using var vm = new ControlWindowViewModel(services, settings, new FakeConfirmationService(),
            new ImmediateUiDispatcher(), obs, new FakeObsServer());
        var coordinator = new PriorityCoordinator(services.Clock, settings.Display);
        var eewComposer = new ConcurrentEewProgramComposer();
        string quakeId = warningId == "practice-quake" ? warningId : SimulatorQuakeId;
        QuakeEvent quake = DecodeSimulatorScalePrompt(now, quakeId);
        TsunamiEvent warning = DecodeSimulatorForecast(now, warningId, grade, released);
        if (queued)
        {
            coordinator.Apply(CreateSubtitleEditProgram("simulator-eew", EventKind.Eew, OverlayPriority.Eew,
                EndPolicy.AutoHide, "緊急地震速報", now) with { SourceMode = SourceMode.ManualTest });
        }

        vm.ApplySimulatorUpdates([quake], null, coordinator, eewComposer);
        Assert.IsTrue(obs.Read(ObsViewChannel.General, now).HasProgram);
        Assert.AreEqual(queued, coordinator.Evaluate().PendingPrograms.Any(program => program.Kind == EventKind.Quake));
        vm.ApplySimulatorUpdates([warning], warning, coordinator, eewComposer);

        Assert.AreEqual(!clearsQuake, obs.Read(ObsViewChannel.General, now).HasProgram);
        if (clearsQuake)
        {
            CoordinatorSnapshot current = coordinator.Evaluate();
            Assert.AreEqual(EventKind.Tsunami, current.CurrentProgram?.Kind);
            Assert.IsFalse(current.PendingPrograms.Any(program => program.Kind == EventKind.Quake));
            Assert.AreEqual(SourceMode.ManualTest, obs.Read(ObsViewChannel.Tsunami, now).SourceMode);
        }
    }

    [TestMethod]
    [DataRow(true, true, true, false, false)]
    [DataRow(false, true, true, true, false)]
    [DataRow(false, false, true, true, true)]
    [DataRow(true, true, false, true, true)]
    public async Task SimulatorAppliesBothSettingsWhenTheForecastIsAlreadyInTheFrame(
        bool suppress, bool omitCaution, bool tsunamiEnabled, bool showsQuake, bool showsCaution)
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings settings = defaults with
        {
            Display = defaults.Display with
            {
                SuppressScalePromptWhenTsunamiWarningActive = suppress,
                OmitScalePromptCautionWhenTsunamiWarningActive = omitCaution,
            },
            Filter = defaults.Filter with { Tsunami = tsunamiEnabled },
        };
        var services = CreateServices(ProviderConnectionState.Stopped, suppliedSettings: settings);
        DateTimeOffset now = services.Clock.UtcNow;
        var obs = new ObsSnapshotStore(settings.Display, now);
        await using var vm = new ControlWindowViewModel(services, settings, new FakeConfirmationService(),
            new ImmediateUiDispatcher(), obs, new FakeObsServer());
        var coordinator = new PriorityCoordinator(services.Clock, settings.Display);
        TsunamiEvent warning = DecodeSimulatorForecast(now, SimulatorQuakeId, "Warning", released: false);
        QuakeEvent quake = DecodeSimulatorScalePrompt(now, SimulatorQuakeId);

        vm.ApplySimulatorUpdates([quake, warning], warning, coordinator, new ConcurrentEewProgramComposer());

        ObsViewSnapshot snapshot = obs.Read(ObsViewChannel.General, now);
        Assert.AreEqual(showsQuake, snapshot.HasProgram);
        string secondPage = obs.Read(ObsViewChannel.General, now.AddSeconds(settings.Display.PageDurationSeconds + 0.1)).AccessibleText;
        Assert.AreEqual(showsCaution, secondPage.Contains(SimulatorTsunamiCaution, StringComparison.Ordinal));
    }

    private static QuakeEvent DecodeSimulatorScalePrompt(DateTimeOffset now, string identifier)
    {
        using var json = JsonDocument.Parse($$"""
            {"eventId":"{{identifier}}","informationType":"ScalePrompt","issuedAt":"2024-01-01T07:12:27Z",
             "earthquake":{"maximumIntensity":"4"},"points":[{"prefecture":"石川県","name":"能登","intensity":"4"}]}
            """);
        return (QuakeEvent)DisasterSimulatorDecoder.Decode("earthquake", json.RootElement, now);
    }

    private static TsunamiEvent DecodeSimulatorForecast(DateTimeOffset now, string identifier, string grade, bool released)
    {
        using var json = JsonDocument.Parse($$"""
            {"eventId":"{{identifier}}","telegramType":"VTSE41","issuedAt":"2024-01-01T07:12:54Z","expiresAt":"2024-01-01T08:00:00Z",
             "isCancelled":{{(released ? "true" : "false")}},"areas":[{"role":"ForecastArea","name":"石川県能登","grade":"{{grade}}"}]}
            """);
        return (TsunamiEvent)DisasterSimulatorDecoder.Decode("tsunami", json.RootElement, now);
    }
}
