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
    [DataRow(false)]
    [DataRow(true)]
    public void SharedObsAndMonitorSnapshotsCarryCardsAndPresentationWithoutChangingMetadataOrAudio(bool left)
    {
        var settings = AppSettings.CreateDefault().Display with
            { UseVerticalEewWarningLayout = true, AlignVerticalEewWarningsLeft = left };
        var item = TestScenarioCatalog.Create(Now).Select(s => s.Event).OfType<EewEvent>()
            .First(e => e.IsWarning && !e.IsCancelled);
        var program = new PageComposer().Compose(item, settings);
        var store = new ObsSnapshotStore(settings, Now);
        var coordinator = new PriorityCoordinator(new Clock(), settings);
        var obs = store.Publish(coordinator.Apply(program), settings, Now);
        Assert.IsTrue(obs.UseVerticalEewWarningLayout);
        Assert.AreEqual(left, obs.AlignVerticalEewWarningsLeft);
        Assert.IsNotNull(obs.Blocks[0].EewCard);
        Assert.AreEqual(item.SourceMode, obs.SourceMode);
        Assert.AreEqual(program.RehearsalLabel, obs.RehearsalLabel);
        var category = store.Read(ObsViewChannel.Eew, Now);
        Assert.IsTrue(category.UseVerticalEewWarningLayout);
        Assert.AreEqual(left, category.AlignVerticalEewWarningsLeft);
        Assert.AreEqual(obs.Blocks[0].EewCard, category.Blocks[0].EewCard);
        store.AddMonitorTelegram(item, program, settings, "訓練表示");
        var review = store.ReadMonitorTelegram(1, Now)![0];
        Assert.IsTrue(review.UseVerticalEewWarningLayout);
        Assert.AreEqual(left, review.AlignVerticalEewWarningsLeft);
        Assert.AreEqual(category.Blocks[0].EewCard, review.Blocks[0].EewCard);
        Assert.AreEqual(0L, review.AudioSequence);
        var serialized = JsonSerializer.Serialize(obs, ObsJsonContext.Default.ObsViewSnapshot);
        using var document = JsonDocument.Parse(serialized);
        Assert.IsTrue(document.RootElement.GetProperty("useVerticalEewWarningLayout").GetBoolean());
        Assert.AreEqual(left, document.RootElement.GetProperty("alignVerticalEewWarningsLeft").GetBoolean());
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AlignmentDefaultsRightAndEditorPersistsAndResetsChoice(bool left)
    {
        var defaults = AppSettings.CreateDefault();
        var json = JsonNode.Parse(JsonSerializer.Serialize(defaults.Display))!.AsObject();
        json.Remove(nameof(DisplaySettings.AlignVerticalEewWarningsLeft));
        Assert.IsFalse(JsonSerializer.Deserialize<DisplaySettings>(json.ToJsonString())!.AlignVerticalEewWarningsLeft);
        var editor = new SettingsEditorViewModel(defaults)
        {
            UseVerticalEewWarningLayout = true,
            AlignVerticalEewWarningsLeft = left,
        };
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(editor.ToSettings(defaults)))!;
        Assert.AreEqual(left, JsonSettingsStore.NormalizeDocument(saved).Display.AlignVerticalEewWarningsLeft);
        var loaded = new SettingsEditorViewModel(saved);
        Assert.AreEqual(left, loaded.AlignVerticalEewWarningsLeft);
        loaded.UseVerticalEewWarningLayout = false;
        Assert.AreEqual(left, loaded.ToSettings(saved).Display.AlignVerticalEewWarningsLeft);
        loaded.ResetDisplaySettings();
        Assert.IsFalse(loaded.ToSettings(saved).Display.AlignVerticalEewWarningsLeft);
    }

    [TestMethod]
    public void ActiveWarningCanChangeAlignmentWithoutRestartingProgram()
    {
        var right = AppSettings.CreateDefault().Display with { UseVerticalEewWarningLayout = true };
        var item = TestScenarioCatalog.Create(Now).Select(s => s.Event).OfType<EewEvent>()
            .First(e => e.IsWarning && !e.IsCancelled);
        var program = new PageComposer().Compose(item, right);
        var store = new ObsSnapshotStore(right, Now);
        var before = store.Publish(new PriorityCoordinator(new Clock(), right).Apply(program), right, Now);
        var left = right with { AlignVerticalEewWarningsLeft = true };
        var after = store.PublishSettings(left, Now);
        Assert.IsTrue(after.AlignVerticalEewWarningsLeft);
        Assert.AreEqual(before.ProgramId, after.ProgramId);
        Assert.AreEqual(before.AudioSequence, after.AudioSequence);
        Assert.AreEqual(before.SourceMode, after.SourceMode);
        Assert.AreEqual(before.RehearsalLabel, after.RehearsalLabel);
        Assert.AreEqual(before.Blocks[0].EewCard, after.Blocks[0].EewCard);
        Assert.IsTrue(store.Read(ObsViewChannel.Eew, Now).AlignVerticalEewWarningsLeft);
        Assert.IsTrue(store.Read(ObsViewChannel.Weather, Now).AlignVerticalEewWarningsLeft);
        store.ClearPrograms(Now);
        Assert.IsTrue(store.Read().AlignVerticalEewWarningsLeft);
        Assert.IsTrue(store.Read(ObsViewChannel.Eew, Now).AlignVerticalEewWarningsLeft);
        store.PublishSettings(right, Now);
        Assert.IsFalse(store.Read().AlignVerticalEewWarningsLeft);
        Assert.IsFalse(store.Read(ObsViewChannel.Eew, Now).AlignVerticalEewWarningsLeft);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
