using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.History;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class ReplayPresentationTests
{
    [TestMethod]
    public void DefaultsLeavePagesLabelsAndBadgesUntouched()
    {
        var settings = AppSettings.CreateDefault();
        DisplayProgram source = CreateProgram();
        DisplayProgram result = ReplayPresentation.Apply(source, settings.History, source.IssuedAt, true, true);
        Assert.AreSame(source.Pages, result.Pages);
        Assert.AreEqual(source.RehearsalLabel, result.RehearsalLabel);
        Assert.IsFalse(result.HideReplayModeBanner);
        Assert.IsFalse(result.HideInformationBadges);
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void BadgeOptionsAreIndependentAndDoNotEraseMetadata(bool banner, bool badges)
    {
        DisplayProgram source = CreateProgram();
        var options = AppSettings.CreateDefault().History with { HideModeBanner = banner, HideInformationBadges = badges };
        DisplayProgram result = ReplayPresentation.Apply(source, options, source.IssuedAt, false, true);
        Assert.AreEqual(banner, result.HideReplayModeBanner);
        Assert.AreEqual(badges, result.HideInformationBadges);
        Assert.AreEqual(SourceMode.HistoryRehearsal, result.SourceMode);
        Assert.AreEqual("過去情報／訓練", result.RehearsalLabel);
        Assert.AreEqual("大雨警報", result.Pages[0].Blocks[0].Badge);
        Assert.AreEqual(source.Pages[0].AccessibleText, result.Pages[0].AccessibleText);
        Assert.IsFalse(source.HideReplayModeBanner);
    }

    [TestMethod]
    public void ReplayNoticeIsSeparateFinalPageWithJstAnnouncementTimeAndUpdatedIndicators()
    {
        DisplayProgram source = CreateProgram();
        var options = AppSettings.CreateDefault().History with { AppendIssuedAtPage = true };
        DateTimeOffset announced = new(2026, 10, 20, 16, 2, 0, TimeSpan.Zero);
        DisplayProgram result = ReplayPresentation.Apply(source, options, announced, true, true);
        Assert.HasCount(2, result.Pages);
        Assert.AreEqual("これは10月21日01時02分発表の情報です", result.Pages[^1].AccessibleText);
        Assert.AreEqual(2, result.Pages[^1].Index);
        Assert.AreEqual("1 / 2", result.Pages[0].Blocks[^1].PrimaryText);
        Assert.AreEqual("2 / 2", result.Pages[1].Blocks[^1].PrimaryText);
        Assert.HasCount(1, source.Pages);
        Assert.AreEqual(source.IssuedAt, result.IssuedAt);
        Assert.AreEqual(source.EventId, result.EventId);
        Assert.AreEqual(source.SourceMode, result.SourceMode);
        Assert.AreEqual(TimeSpan.FromSeconds(8), HistoryReplayTiming.GetItemDuration(result,
            AppSettings.CreateDefault().Display with { PageDurationSeconds = 4 }, 1));
    }

    [TestMethod]
    public void NoticeIsNotAddedToOrdinaryTrainingAndRespectsPageIndicatorOff()
    {
        DisplayProgram source = CreateProgram();
        var options = AppSettings.CreateDefault().History with { AppendIssuedAtPage = true };
        Assert.AreSame(source.Pages, ReplayPresentation.Apply(source, options, source.IssuedAt, false, true).Pages);
        var replay = ReplayPresentation.Apply(source, options, source.IssuedAt, true, false);
        Assert.IsFalse(replay.Pages.SelectMany(static page => page.Blocks)
            .Any(static block => block.StyleToken == DisplayStyleTokens.PageIndicator));
    }

    private static DisplayProgram CreateProgram() => new("history", EventId.Create("test-history"),
        EventKind.WeatherWarning, SourceMode.HistoryRehearsal, DateTimeOffset.UnixEpoch,
        OverlayPriority.WeatherWarning,
        [new DisplayPage(1, [new DisplayBlock("大雨警報", "東京都｜継続中", "渋谷区", DisplayStyleTokens.WeatherWarning)],
            "大雨警報。東京都｜継続中。渋谷区", null)], DateTimeOffset.UnixEpoch, EndPolicy.AutoHide, "過去情報／訓練");
}
