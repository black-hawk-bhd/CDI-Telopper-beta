using System.Text.Json;
using System.Text.Json.Nodes;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Settings;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class EewVerticalSettingsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [TestMethod]
    public void LegacySettingsStayHorizontalAndEditorSavesLoadsAndResetsChoice()
    {
        var defaults = AppSettings.CreateDefault();
        var json = JsonNode.Parse(JsonSerializer.Serialize(defaults.Display))!.AsObject();
        json.Remove(nameof(DisplaySettings.UseVerticalEewWarningLayout));
        Assert.IsFalse(JsonSerializer.Deserialize<DisplaySettings>(json.ToJsonString())!.UseVerticalEewWarningLayout);
        var editor = new SettingsEditorViewModel(defaults) { UseVerticalEewWarningLayout = true };
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(editor.ToSettings(defaults)))!;
        Assert.IsTrue(JsonSettingsStore.NormalizeDocument(saved).Display.UseVerticalEewWarningLayout);
        var reloaded = new SettingsEditorViewModel(saved);
        Assert.IsTrue(reloaded.UseVerticalEewWarningLayout);
        reloaded.ResetDisplaySettings();
        Assert.IsFalse(reloaded.ToSettings(saved).Display.UseVerticalEewWarningLayout);
    }

    [TestMethod]
    public void SharedObsAndMonitorSnapshotsCarryCardsAndPresentationWithoutChangingMetadataOrAudio()
    {
        var settings = AppSettings.CreateDefault().Display with { UseVerticalEewWarningLayout = true };
        var item = TestScenarioCatalog.Create(Now).Select(s => s.Event).OfType<EewEvent>()
            .First(e => e.IsWarning && !e.IsCancelled);
        var program = new PageComposer().Compose(item, settings);
        var store = new ObsSnapshotStore(settings, Now);
        var coordinator = new PriorityCoordinator(new Clock(), settings);
        var obs = store.Publish(coordinator.Apply(program), settings, Now);
        Assert.IsTrue(obs.UseVerticalEewWarningLayout);
        Assert.IsNotNull(obs.Blocks[0].EewCard);
        Assert.AreEqual(item.SourceMode, obs.SourceMode);
        Assert.AreEqual(program.RehearsalLabel, obs.RehearsalLabel);
        var category = store.Read(ObsViewChannel.Eew, Now);
        Assert.IsTrue(category.UseVerticalEewWarningLayout);
        Assert.AreEqual(obs.Blocks[0].EewCard, category.Blocks[0].EewCard);
        store.AddMonitorTelegram(item, program, settings, "訓練表示");
        var review = store.ReadMonitorTelegram(1, Now)![0];
        Assert.IsTrue(review.UseVerticalEewWarningLayout);
        Assert.AreEqual(category.Blocks[0].EewCard, review.Blocks[0].EewCard);
        Assert.AreEqual(0L, review.AudioSequence);
        var serialized = JsonSerializer.Serialize(obs, ObsJsonContext.Default.ObsViewSnapshot);
        using var document = JsonDocument.Parse(serialized);
        Assert.IsTrue(document.RootElement.GetProperty("useVerticalEewWarningLayout").GetBoolean());
        Assert.IsTrue(document.RootElement.GetProperty("blocks")[0].GetProperty("eewCard").GetProperty("areas").GetArrayLength() > 0);
    }

    [TestMethod]
    public void ExistingWarningCanSwitchStylesAndEmptySnapshotsKeepSetting()
    {
        var settings = AppSettings.CreateDefault().Display;
        var item = TestScenarioCatalog.Create(Now).Select(s => s.Event).OfType<EewEvent>()
            .First(e => e.IsWarning && !e.IsCancelled);
        var program = new PageComposer().Compose(item, settings);
        var store = new ObsSnapshotStore(settings, Now);
        store.PublishProgram(item, program, settings, Now);
        Assert.IsFalse(store.Read(ObsViewChannel.Eew, Now).UseVerticalEewWarningLayout);
        var vertical = settings with { UseVerticalEewWarningLayout = true };
        Assert.IsTrue(store.PublishSettings(vertical, Now).UseVerticalEewWarningLayout);
        var snapshot = store.Read(ObsViewChannel.Eew, Now);
        Assert.IsTrue(snapshot.UseVerticalEewWarningLayout);
        Assert.AreEqual(program.ProgramId, snapshot.ProgramId);
        Assert.IsTrue(store.Read(ObsViewChannel.Weather, Now).UseVerticalEewWarningLayout);
        store.PublishSettings(settings, Now);
        Assert.IsFalse(store.Read(ObsViewChannel.Eew, Now).UseVerticalEewWarningLayout);
        store.ClearPrograms(Now);
        Assert.IsFalse(store.Read(ObsViewChannel.Eew, Now).HasProgram);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
