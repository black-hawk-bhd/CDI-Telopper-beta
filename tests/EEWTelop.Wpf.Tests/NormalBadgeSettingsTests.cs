using System.Text.Json;
using System.Text.Json.Nodes;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.History;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class NormalBadgeSettingsTests
{
    [TestMethod]
    public void LegacyDefaultsVisibleAndEditorSavesAndResetsOnlyNormalChoices()
    {
        var defaults = AppSettings.CreateDefault();
        var json = JsonNode.Parse(JsonSerializer.Serialize(defaults.Display))!.AsObject();
        json.Remove(nameof(DisplaySettings.HideNormalModeBanner));
        json.Remove(nameof(DisplaySettings.HideNormalInformationBadges));
        var legacy = JsonSerializer.Deserialize<DisplaySettings>(json.ToJsonString())!;
        Assert.IsFalse(legacy.HideNormalModeBanner || legacy.HideNormalInformationBadges);
        var editor = new SettingsEditorViewModel(defaults)
        {
            HideNormalModeBanner = true, HideNormalInformationBadges = true,
            HideReplayModeBanner = true, HideReplayInformationBadges = true,
        };
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(editor.ToSettings(defaults)))!;
        var reloaded = new SettingsEditorViewModel(saved);
        Assert.IsTrue(reloaded.HideNormalModeBanner && reloaded.HideNormalInformationBadges);
        reloaded.ResetDisplaySettings();
        Assert.IsFalse(reloaded.HideNormalModeBanner || reloaded.HideNormalInformationBadges);
        Assert.IsTrue(reloaded.HideReplayModeBanner && reloaded.HideReplayInformationBadges);
        reloaded.HideNormalInformationBadges = true;
        reloaded.ResetHistorySettings();
        Assert.IsTrue(reloaded.HideNormalInformationBadges);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NormalBadgesRenderConsistentlyWithoutChangingTextOrMetadata(bool hidden)
    {
        DisplaySettings display = AppSettings.CreateDefault().Display with { HideNormalInformationBadges = hidden };
        DisplayProgram program = Program();
        var coordinator = new PriorityCoordinator(new Clock(), display);
        var snapshot = coordinator.Apply(program);
        var store = new ObsSnapshotStore(display, DateTimeOffset.UnixEpoch);
        var obs = store.Publish(snapshot, display, DateTimeOffset.UnixEpoch);
        var overlay = new OverlayViewModel();
        overlay.Apply(snapshot, display);
        Assert.AreEqual(hidden, obs.Blocks.Single().HideBadge);
        Assert.AreEqual(!hidden, overlay.Blocks.Single().IsBadgeVisible);
        Assert.AreEqual(!hidden, overlay.Blocks.Single().HasBadgeColumn);
        Assert.IsTrue(overlay.Blocks.Single().HasWeatherContext);
        Assert.AreEqual(hidden ? "大雨警報　東京都｜継続中" : "東京都｜継続中", overlay.Blocks.Single().WeatherContext);
        Assert.AreEqual(overlay.Blocks.Single().WeatherContext, obs.Blocks.Single().WeatherHeading);
        Assert.AreEqual("渋谷区", overlay.Blocks.Single().SubtitlePrimaryText);
        Assert.AreEqual("大雨警報", obs.Blocks.Single().Badge);
        Assert.AreEqual(program.Pages[0].AccessibleText, obs.AccessibleText);
        Assert.AreEqual(SourceMode.Production, obs.SourceMode);
        Assert.IsFalse(program.HideInformationBadges);
    }

    [TestMethod]
    public void SettingsApplyToAlreadyPublishedCategoryWithoutRecomposingTelegram()
    {
        var display = AppSettings.CreateDefault().Display;
        var store = new ObsSnapshotStore(display, DateTimeOffset.UnixEpoch);
        var coordinator = new PriorityCoordinator(new Clock(), display);
        var program = Program();
        store.Publish(coordinator.Apply(program), display, DateTimeOffset.UnixEpoch);
        Assert.IsFalse(store.Read(ObsViewChannel.Weather, DateTimeOffset.UnixEpoch).Blocks[0].HideBadge);
        store.PublishSettings(display with { HideNormalInformationBadges = true }, DateTimeOffset.UnixEpoch);
        var hidden = store.Read(ObsViewChannel.Weather, DateTimeOffset.UnixEpoch);
        Assert.IsTrue(hidden.Blocks[0].HideBadge);
        Assert.AreEqual(program.ProgramId, hidden.ProgramId);
        store.PublishSettings(display, DateTimeOffset.UnixEpoch);
        Assert.IsFalse(store.Read(ObsViewChannel.Weather, DateTimeOffset.UnixEpoch).Blocks[0].HideBadge);
    }

    [TestMethod]
    public void AutomaticReplayRemainsIndependentOfNormalSettingsInBothOutputs()
    {
        var defaults = AppSettings.CreateDefault();
        var display = defaults.Display with { HideNormalInformationBadges = true, HideNormalModeBanner = true };
        var source = Program();
        var replay = ReplayPresentation.Apply(source, defaults.History, source.IssuedAt, true, true);
        var coordinator = new PriorityCoordinator(new Clock(), display);
        var snapshot = coordinator.Apply(replay);
        var store = new ObsSnapshotStore(display, DateTimeOffset.UnixEpoch);
        var obs = store.Publish(snapshot, display, DateTimeOffset.UnixEpoch);
        var overlay = new OverlayViewModel();
        overlay.Apply(snapshot, display);
        Assert.IsFalse(obs.Blocks.Single().HideBadge);
        Assert.IsTrue(overlay.Blocks.Single().IsBadgeVisible);
        Assert.AreEqual(SourceMode.Production, obs.SourceMode);
    }

    [TestMethod]
    [DataRow(EventKind.Quake, "震度3", DisplayStyleTokens.Intensity, ObsViewChannel.General)]
    [DataRow(EventKind.Quake, "震度5弱以上 未入電", DisplayStyleTokens.Intensity, ObsViewChannel.General)]
    [DataRow(EventKind.Tsunami, "大津波警報", DisplayStyleTokens.Tsunami, ObsViewChannel.Tsunami)]
    [DataRow(EventKind.Volcano, "噴火警報", DisplayStyleTokens.VolcanoWarning, ObsViewChannel.General)]
    public void NormalSubtitleModeShowsTypeOnBothOutputsAndKeepsContinuationsAndPages(
        EventKind kind, string label, string style, ObsViewChannel channel)
    {
        var display = AppSettings.CreateDefault().Display with { HideNormalInformationBadges = true };
        var first = new DisplayBlock(label, "地域A", "補足", style);
        var second = new DisplayBlock("", "地域B", "", style);
        var page = new DisplayPage(1, [first, second], $"{label} 地域A 補足 地域B", null);
        var program = Program() with { Kind = kind, Pages = [page] };
        var coordinator = new PriorityCoordinator(new Clock(), display);
        var snapshot = coordinator.Apply(program);
        var store = new ObsSnapshotStore(display, DateTimeOffset.UnixEpoch);
        store.Publish(snapshot, display, DateTimeOffset.UnixEpoch);
        var obs = store.Read(channel, DateTimeOffset.UnixEpoch);
        var overlay = new OverlayViewModel();
        overlay.Apply(snapshot, display);
        Assert.AreEqual($"{label}　地域A", obs.Blocks[0].PrimaryText);
        Assert.AreEqual(obs.Blocks[0].PrimaryText, overlay.Blocks[0].SubtitlePrimaryText);
        Assert.AreEqual("地域B", obs.Blocks[1].PrimaryText);
        Assert.AreEqual("地域B", overlay.Blocks[1].SubtitlePrimaryText);
        Assert.AreEqual("補足", obs.Blocks[0].SecondaryText);
        Assert.IsTrue(obs.Blocks.All(static block => block.HideBadge));
        Assert.IsTrue(overlay.Blocks.All(static block => !block.IsBadgeVisible));
        Assert.AreEqual(1, obs.PageCount);
        Assert.AreEqual(page.AccessibleText, obs.AccessibleText);
        Assert.AreSame(first, program.Pages[0].Blocks[0]);
        Assert.AreEqual("地域A", first.PrimaryText);
        store.PublishSettings(display with { HideNormalInformationBadges = false }, DateTimeOffset.UnixEpoch);
        var badgeMode = store.Read(channel, DateTimeOffset.UnixEpoch);
        Assert.IsFalse(badgeMode.Blocks[0].HideBadge);
        Assert.AreEqual("地域A", badgeMode.Blocks[0].PrimaryText);
    }

    private static DisplayProgram Program() => new("normal-badge", EventId.Create("normal-badge"),
        EventKind.WeatherWarning, SourceMode.Production, DateTimeOffset.UnixEpoch, OverlayPriority.WeatherWarning,
        [new DisplayPage(1, [new DisplayBlock("大雨警報", "渋谷区", "", DisplayStyleTokens.WeatherWarning)
            { WeatherHeading = "東京都｜継続中" }], "大雨警報 東京都｜継続中 渋谷区", null)],
        DateTimeOffset.UnixEpoch, EndPolicy.AutoHide, string.Empty);

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
