using System.Text.Json;
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
public sealed class ReplaySettingsTests
{
    [TestMethod]
    public void LegacySettingsDefaultOffAndAllChoicesRoundTripAndReset()
    {
        var history = JsonSerializer.Deserialize<HistorySettings>("{\"Api\":0,\"Limit\":10,\"IntervalSeconds\":4}")!;
        Assert.IsFalse(history.HideModeBanner);
        Assert.IsFalse(history.HideInformationBadges);
        Assert.IsFalse(history.AppendIssuedAtPage);
        var defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults)
        {
            HistoryApi = HistoryApi.JmaXml, JmaHistoryContent = JmaHistoryContent.Tsunami,
            JmaHistoryReportUrl = " https://www.data.jma.go.jp/developer/xml/data/sample.xml ",
            HideReplayModeBanner = true, HideReplayInformationBadges = true, AppendReplayIssuedAtPage = true,
        };
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(editor.ToSettings(defaults)))!;
        var loaded = new SettingsEditorViewModel(saved);
        Assert.AreEqual(HistoryApi.JmaXml, loaded.HistoryApi);
        Assert.AreEqual(JmaHistoryContent.Tsunami, loaded.JmaHistoryContent);
        Assert.IsTrue(loaded.HideReplayModeBanner && loaded.HideReplayInformationBadges && loaded.AppendReplayIssuedAtPage);
        Assert.AreEqual("https://www.data.jma.go.jp/developer/xml/data/sample.xml", loaded.JmaHistoryReportUrl);
        loaded.ResetHistorySettings();
        Assert.IsFalse(loaded.HideReplayModeBanner || loaded.HideReplayInformationBadges || loaded.AppendReplayIssuedAtPage);
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ObsAndWpfRenderChoicesConsistentlyWithoutLosingWeatherHeading(bool hideBanner, bool hideBadge)
    {
        var settings = AppSettings.CreateDefault();
        var source = new DisplayProgram("history", EventId.Create("history"), EventKind.WeatherWarning,
            SourceMode.HistoryRehearsal, DateTimeOffset.UnixEpoch, OverlayPriority.WeatherWarning,
            [new DisplayPage(1, [new DisplayBlock("大雨警報", "渋谷区", "", DisplayStyleTokens.WeatherWarning)
                { WeatherHeading = "東京都｜継続中" }], "大雨警報 東京都｜継続中 渋谷区", null)],
            DateTimeOffset.UnixEpoch, EndPolicy.AutoHide, "過去情報");
        var program = ReplayPresentation.Apply(source, settings.History with
            { HideModeBanner = hideBanner, HideInformationBadges = hideBadge }, source.IssuedAt, true, true);
        var coordinator = new PriorityCoordinator(new Clock(), settings.Display);
        var snapshot = coordinator.Apply(program);
        var store = new ObsSnapshotStore(settings.Display, DateTimeOffset.UnixEpoch);
        ObsViewSnapshot obs = store.Publish(snapshot, settings.Display, DateTimeOffset.UnixEpoch);
        var overlay = new OverlayViewModel();
        overlay.Apply(snapshot, settings.Display);
        Assert.AreEqual(hideBanner ? string.Empty : "過去情報", obs.RehearsalLabel);
        Assert.AreEqual(obs.RehearsalLabel, overlay.RehearsalLabel);
        Assert.AreEqual(hideBadge, obs.Blocks[0].HideBadge);
        Assert.AreEqual(!hideBadge, overlay.Blocks[0].IsBadgeVisible);
        Assert.IsTrue(overlay.Blocks[0].HasWeatherContext);
        Assert.AreEqual(hideBadge ? "大雨警報　東京都｜継続中" : "東京都｜継続中", overlay.Blocks[0].WeatherContext);
        Assert.AreEqual(overlay.Blocks[0].WeatherContext, obs.Blocks[0].WeatherHeading);
        Assert.AreEqual("東京都｜継続中", source.Pages[0].Blocks[0].WeatherHeading);
        Assert.AreEqual("大雨警報", obs.Blocks[0].Badge);
        Assert.AreEqual(SourceMode.HistoryRehearsal, obs.SourceMode);
    }

    [TestMethod]
    public void HistoryCleanupCannotEraseReplacementLiveOutput()
    {
        var settings = AppSettings.CreateDefault().Display;
        var now = DateTimeOffset.UnixEpoch;
        var store = new ObsSnapshotStore(settings, now);
        var old = new DisplayProgram("history-old", EventId.Create("old"), EventKind.Quake,
            SourceMode.HistoryRehearsal, now, OverlayPriority.Quake,
            [new DisplayPage(1, [new DisplayBlock("震度3", "東京都", "", DisplayStyleTokens.Intensity)], "東京都", null)],
            now, EndPolicy.AutoHide, "過去情報");
        var live = old with { ProgramId = "live-new", SourceMode = SourceMode.Production, RehearsalLabel = string.Empty };
        var coordinator = new PriorityCoordinator(new Clock(), settings);
        store.Publish(coordinator.Apply(live), settings, now);
        store.ClearProgram(EventKind.Quake, now, old.ProgramId);
        Assert.AreEqual("live-new", store.Read(ObsViewChannel.General, now).ProgramId);
        store.ClearProgram(EventKind.Quake, now, live.ProgramId);
        Assert.IsFalse(store.Read(ObsViewChannel.General, now).HasProgram);
    }

    [TestMethod]
    [DataRow(SourceMode.HistoryRehearsal, false)]
    [DataRow(SourceMode.ManualTest, false)]
    [DataRow(SourceMode.Sandbox, false)]
    [DataRow(SourceMode.Production, true)]
    [DataRow(SourceMode.Production, false)]
    public void EveryReplayTrainingAndRehearsalOutputKeepsSeverityAndCanSwitchBack(SourceMode mode, bool replay)
    {
        var settings = AppSettings.CreateDefault();
        var now = DateTimeOffset.UnixEpoch;
        var first = new DisplayBlock("津波注意報", "地域A", "1m", DisplayStyleTokens.Tsunami);
        var continuation = new DisplayBlock("", "地域B", "1m", DisplayStyleTokens.Tsunami);
        var page = new DisplayPage(1, [first, continuation], "津波注意報 地域A 地域B", null);
        var source = new DisplayProgram("all-mode-subtitles", EventId.Create("all-mode-subtitles"), EventKind.Tsunami,
            mode, now, OverlayPriority.TsunamiWatch, [page], now, EndPolicy.AutoHide,
            mode == SourceMode.Production && replay ? "" : "操作テスト／訓練");
        var program = ReplayPresentation.Apply(source, settings.History with
            { HideInformationBadges = true }, source.IssuedAt, replay, true);
        var coordinator = new PriorityCoordinator(new Clock(), settings.Display);
        var snapshot = coordinator.Apply(program);
        var store = new ObsSnapshotStore(settings.Display, now);
        var published = store.Publish(snapshot, settings.Display, now);
        var obs = store.Read(ObsViewChannel.Tsunami, now);
        var overlay = new OverlayViewModel();
        overlay.Apply(snapshot, settings.Display);
        Assert.IsTrue(obs.Blocks[0].HideBadge);
        Assert.AreEqual("津波注意報　地域A", obs.Blocks[0].PrimaryText);
        Assert.AreEqual(obs.Blocks[0].PrimaryText, published.Blocks[0].PrimaryText);
        Assert.AreEqual(obs.Blocks[0].PrimaryText, overlay.Blocks[0].SubtitlePrimaryText);
        Assert.IsFalse(overlay.Blocks[0].IsBadgeVisible);
        Assert.AreEqual("地域B", obs.Blocks[1].PrimaryText);
        Assert.AreEqual("1m", obs.Blocks[0].SecondaryText);
        Assert.AreEqual(source.RehearsalLabel, obs.RehearsalLabel);
        Assert.AreEqual(source.SourceMode, obs.SourceMode);
        Assert.AreEqual(1, obs.PageCount);
        Assert.AreEqual(page.AccessibleText, obs.AccessibleText);
        Assert.AreEqual("地域A", first.PrimaryText);
        var badgeProgram = program with { HideInformationBadges = false };
        var badgeCoordinator = new PriorityCoordinator(new Clock(), settings.Display);
        var badgeSnapshot = badgeCoordinator.Apply(badgeProgram);
        var badgeOutput = store.Publish(badgeSnapshot, settings.Display, now);
        overlay.Apply(badgeSnapshot, settings.Display);
        Assert.IsFalse(badgeOutput.Blocks[0].HideBadge);
        Assert.AreEqual("地域A", badgeOutput.Blocks[0].PrimaryText);
        Assert.IsTrue(overlay.Blocks[0].IsBadgeVisible);
        Assert.AreEqual(source.RehearsalLabel, overlay.RehearsalLabel);
    }

    private sealed class Clock : EEWTelop.Application.Abstractions.IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
