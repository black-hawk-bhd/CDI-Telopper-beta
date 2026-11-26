using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Documents;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Settings;
using EEWTelop.Wpf.Controls;
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
        json.Remove(nameof(DisplaySettings.HighlightEewAreaChanges));
        json.Remove(nameof(DisplaySettings.ShowEewEstimates));
        var legacy = JsonSerializer.Deserialize<DisplaySettings>(json.ToJsonString())!;
        Assert.IsFalse(legacy.UseVerticalEewWarningLayout);
        Assert.IsFalse(legacy.HighlightEewAreaChanges);
        Assert.IsFalse(legacy.ShowEewEstimates);
        var editor = new SettingsEditorViewModel(defaults)
        {
            UseVerticalEewWarningLayout = true,
            HighlightEewAreaChanges = true,
            ShowEewEstimates = true,
        };
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(editor.ToSettings(defaults)))!;
        Assert.IsTrue(JsonSettingsStore.NormalizeDocument(saved).Display.UseVerticalEewWarningLayout);
        Assert.IsTrue(JsonSettingsStore.NormalizeDocument(saved).Display.HighlightEewAreaChanges);
        var reloaded = new SettingsEditorViewModel(saved);
        Assert.IsTrue(reloaded.UseVerticalEewWarningLayout);
        Assert.IsTrue(reloaded.HighlightEewAreaChanges);
        Assert.IsTrue(reloaded.ShowEewEstimates);
        reloaded.ResetDisplaySettings();
        DisplaySettings reset = reloaded.ToSettings(saved).Display;
        Assert.IsFalse(reset.UseVerticalEewWarningLayout);
        Assert.IsFalse(reset.HighlightEewAreaChanges);
        Assert.IsFalse(reset.ShowEewEstimates);
    }

    [TestMethod]
    public void ExistingWarningEstimatesFollowTheCurrentSettingAndDoNotSurviveCancellation()
    {
        var settings = AppSettings.CreateDefault().Display;
        var enabled = settings with { ShowEewEstimates = true };
        var scenarios = TestScenarioCatalog.Create(Now);
        var warning = (EewEvent)scenarios.Single(item => item.Id == "eew-warning").Event;
        var cancellation = (EewEvent)scenarios.Single(item => item.Id == "eew-cancel").Event;
        var composer = new PageComposer();
        var program = composer.Compose(warning, settings);
        var coordinator = new PriorityCoordinator(new Clock(), settings);
        var snapshot = coordinator.Apply(program);
        var store = new ObsSnapshotStore(settings, Now);
        var overlay = new OverlayViewModel();
        store.AddMonitorTelegram(warning, program, settings, "訓練表示");
        overlay.Apply(snapshot, settings);
        Assert.IsNull(overlay.Blocks.Single(block => block.StyleToken == DisplayStyleTokens.EewAreas).EewEstimateText);
        var disabled = store.Publish(snapshot, settings, Now);
        Assert.DoesNotContain("eewEstimateText", JsonSerializer.Serialize(disabled, ObsJsonContext.Default.ObsViewSnapshot));

        const string expected = "予想最大震度 6弱　推定M 6.4";
        Assert.AreEqual(expected, store.PublishSettings(enabled, Now).Blocks
            .Single(block => block.StyleToken == DisplayStyleTokens.EewAreas).EewEstimateText);
        Assert.AreEqual(expected, store.Read(ObsViewChannel.Eew, Now).Blocks
            .Single(block => block.StyleToken == DisplayStyleTokens.EewAreas).EewEstimateText);
        Assert.AreEqual(expected, store.ReadMonitorTelegram(1, Now)![0].Blocks
            .Single(block => block.StyleToken == DisplayStyleTokens.EewAreas).EewEstimateText);
        overlay.Apply(snapshot, enabled);
        Assert.AreEqual(expected, overlay.Blocks.Single(block => block.StyleToken == DisplayStyleTokens.EewAreas).EewEstimateText);

        Assert.IsTrue(store.PublishSettings(settings, Now).Blocks.All(block => block.EewEstimateText is null));
        Assert.IsTrue(store.Read(ObsViewChannel.Eew, Now).Blocks.All(block => block.EewEstimateText is null));
        Assert.IsTrue(store.ReadMonitorTelegram(1, Now)![0].Blocks.All(block => block.EewEstimateText is null));
        var cancelled = store.Publish(
            coordinator.Apply(composer.Compose(cancellation, enabled)), enabled, Now);
        Assert.IsTrue(cancelled.Blocks.Any(block => block.EewCard is { IsCancelled: true }));
        Assert.IsTrue(cancelled.Blocks.All(block => block.EewEstimateText is null));
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

    [STATestMethod]
    public void HighlightedAreasReachObsAndRenderAsOrangeTextInWpf()
    {
        var settings = AppSettings.CreateDefault().Display with { HighlightEewAreaChanges = true };
        var scenario = TestScenarioCatalog.Create(Now).Single(item => item.Id == "eew-expanding-15");
        var first = (EewEvent)scenario.Steps[0].Event;
        var continued = (EewEvent)scenario.Steps[1].Event;
        var composer = new PageComposer();
        var concurrent = new ConcurrentEewProgramComposer();
        concurrent.Compose(first, composer.Compose(first, settings), settings);
        var program = concurrent.Compose(continued, composer.Compose(continued, settings), settings);
        var snapshot = new PriorityCoordinator(new Clock(), settings).Apply(program);
        var obs = new ObsSnapshotStore(settings, Now).Publish(snapshot, settings, Now);
        var serialized = JsonSerializer.Serialize(obs, ObsJsonContext.Default.ObsViewSnapshot);
        using var document = JsonDocument.Parse(serialized);
        var areas = document.RootElement.GetProperty("blocks").EnumerateArray()
            .Single(block => block.GetProperty("styleToken").GetString() == DisplayStyleTokens.EewAreas);
        var changed = areas.GetProperty("eewAreaLabels").EnumerateArray()
            .Where(label => label.GetProperty("isChanged").GetBoolean())
            .Select(label => label.GetProperty("name").GetString()).ToArray();
        Assert.AreEqual("千葉", changed.Single());

        var overlay = new OverlayViewModel();
        overlay.Apply(snapshot, settings);
        var areaBlock = overlay.Blocks.Single(block => block.StyleToken == DisplayStyleTokens.EewAreas);
        var caption = new EewCaptionText { SourceText = areaBlock.PrimaryText, AreaLabels = areaBlock.EewAreaLabels };
        string[] orange = caption.Inlines.OfType<Run>()
            .Where(run => run.Foreground == System.Windows.Media.Brushes.Orange).Select(run => run.Text).ToArray();
        CollectionAssert.AreEqual(changed, orange);
        Assert.AreEqual(areaBlock.PrimaryText, caption.Text);
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
