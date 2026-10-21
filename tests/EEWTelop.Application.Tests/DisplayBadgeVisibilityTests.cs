using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.History;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class DisplayBadgeVisibilityTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void NormalOptionsAreIndependentAndDoNotMutateProgram(bool banner, bool badges)
    {
        var display = AppSettings.CreateDefault().Display with
            { HideNormalModeBanner = banner, HideNormalInformationBadges = badges };
        DisplayProgram program = Program(SourceMode.Production);
        Assert.AreEqual(banner, DisplayBadgeVisibility.HideModeBanner(program, display));
        Assert.AreEqual(badges, DisplayBadgeVisibility.HideInformationBadges(program, display));
        Assert.AreEqual(SourceMode.Production, program.SourceMode);
        Assert.IsFalse(program.HideReplayModeBanner || program.HideInformationBadges);
        Assert.AreEqual("大雨警報", program.Pages[0].Blocks[0].Badge);
    }

    [TestMethod]
    [DataRow(SourceMode.ManualTest)]
    [DataRow(SourceMode.HistoryRehearsal)]
    [DataRow(SourceMode.Sandbox)]
    public void NormalOptionsNeverOverrideTrainingOptions(SourceMode mode)
    {
        var display = AppSettings.CreateDefault().Display with
            { HideNormalModeBanner = true, HideNormalInformationBadges = true };
        DisplayProgram program = Program(mode);
        Assert.IsFalse(DisplayBadgeVisibility.HideModeBanner(program, display));
        Assert.IsFalse(DisplayBadgeVisibility.HideInformationBadges(program, display));
        program = program with { HideReplayModeBanner = true, HideInformationBadges = true };
        Assert.IsTrue(DisplayBadgeVisibility.HideModeBanner(program, AppSettings.CreateDefault().Display));
        Assert.IsTrue(DisplayBadgeVisibility.HideInformationBadges(program, AppSettings.CreateDefault().Display));
    }

    [TestMethod]
    public void AutomaticProductionReplayUsesSeparateChoicesEvenWithProductionSourceMetadata()
    {
        var settings = AppSettings.CreateDefault();
        var source = Program(SourceMode.Production);
        var replay = ReplayPresentation.Apply(source, settings.History, source.IssuedAt, true, true);
        Assert.IsTrue(replay.IsReplayPresentation);
        Assert.IsFalse(DisplayBadgeVisibility.HideInformationBadges(replay, settings.Display with
            { HideNormalInformationBadges = true }));
        var hiddenReplay = ReplayPresentation.Apply(source, settings.History with
            { HideInformationBadges = true, HideModeBanner = true }, source.IssuedAt, true, true);
        Assert.IsTrue(DisplayBadgeVisibility.HideInformationBadges(hiddenReplay, settings.Display));
        Assert.IsTrue(DisplayBadgeVisibility.HideModeBanner(hiddenReplay, settings.Display));
        Assert.AreEqual(SourceMode.Production, hiddenReplay.SourceMode);
    }

    [TestMethod]
    public void ProductionSourceTestLabelCannotBeHiddenByNormalOptions()
    {
        var display = AppSettings.CreateDefault().Display with
            { HideNormalModeBanner = true, HideNormalInformationBadges = true };
        var program = Program(SourceMode.Production) with { RehearsalLabel = "操作テスト／訓練" };
        Assert.IsFalse(DisplayBadgeVisibility.HideModeBanner(program, display));
        Assert.IsFalse(DisplayBadgeVisibility.HideInformationBadges(program, display));
    }

    [TestMethod]
    [DataRow("震度3", DisplayStyleTokens.Intensity)]
    [DataRow("震度5弱以上 未入電", DisplayStyleTokens.Intensity)]
    [DataRow("大津波警報", DisplayStyleTokens.Tsunami)]
    [DataRow("津波注意報", DisplayStyleTokens.Tsunami)]
    [DataRow("噴火警報", DisplayStyleTokens.VolcanoWarning)]
    [DataRow("訂正", DisplayStyleTokens.Correction)]
    public void SubtitleModePreservesTypeAndSeverityTextInsteadOfRemovingIt(string label, string style)
    {
        var display = AppSettings.CreateDefault().Display with { HideNormalInformationBadges = true };
        var source = new DisplayBlock(label, "対象地域", "補足", style);
        var output = DisplayBadgeVisibility.FormatBlockForOutput(source, Program(SourceMode.Production), display);
        Assert.AreEqual($"{label}　対象地域", output.PrimaryText);
        Assert.AreEqual("補足", output.SecondaryText);
        Assert.AreEqual(label, output.Badge);
        Assert.AreEqual("対象地域", source.PrimaryText);
    }

    [TestMethod]
    public void WeatherTypeJoinsHeadingWhileAreaRowsAndLegacyLineCountStayUnchanged()
    {
        var display = AppSettings.CreateDefault().Display with { HideNormalInformationBadges = true };
        var program = Program(SourceMode.Production);
        const string rows = "いわき市　三島町　三春町\n下郷町　中島村　二本松市";
        var source = new DisplayBlock("大雨警報", rows, "", DisplayStyleTokens.WeatherWarning)
            { WeatherHeading = "福島県｜継続中" };
        var output = DisplayBadgeVisibility.FormatBlockForOutput(source, program, display);
        Assert.AreEqual("大雨警報　福島県｜継続中", output.WeatherHeading);
        Assert.AreEqual(rows, output.PrimaryText);
        Assert.AreEqual("福島県｜継続中", source.WeatherHeading);
        var legacy = source with { WeatherHeading = "", PrimaryText = "福島県｜継続中\n" + rows };
        var legacyOutput = DisplayBadgeVisibility.FormatBlockForOutput(legacy, program, display);
        Assert.AreEqual("大雨警報　" + legacy.PrimaryText, legacyOutput.PrimaryText);
        Assert.AreEqual(legacy.PrimaryText.Count(static value => value == '\n'),
            legacyOutput.PrimaryText.Count(static value => value == '\n'));
    }

    [TestMethod]
    public void SubtitleModeDoesNotRepeatLabelsOrAffectBadgeModeOrTraining()
    {
        var defaults = AppSettings.CreateDefault();
        var display = defaults.Display with { HideNormalInformationBadges = true };
        var program = Program(SourceMode.Production);
        var source = new DisplayBlock("大雨警報", "大雨警報は継続中です", "", DisplayStyleTokens.WeatherWarning);
        Assert.AreEqual(source.PrimaryText, DisplayBadgeVisibility.FormatBlockForOutput(source, program, display).PrimaryText);
        var continuation = source with { Badge = "", PrimaryText = "次の地域" };
        Assert.AreSame(continuation, DisplayBadgeVisibility.FormatBlockForOutput(continuation, program, display));
        Assert.AreSame(source, DisplayBadgeVisibility.FormatBlockForOutput(source, program, defaults.Display));
        Assert.AreSame(source, DisplayBadgeVisibility.FormatBlockForOutput(source, Program(SourceMode.ManualTest), display));
        var replay = ReplayPresentation.Apply(program, defaults.History, program.IssuedAt, true, true);
        Assert.AreSame(source, DisplayBadgeVisibility.FormatBlockForOutput(source, replay, display));
    }

    [TestMethod]
    [DataRow(SourceMode.ManualTest, false)]
    [DataRow(SourceMode.ManualTest, true)]
    [DataRow(SourceMode.HistoryRehearsal, false)]
    [DataRow(SourceMode.HistoryRehearsal, true)]
    [DataRow(SourceMode.Sandbox, false)]
    [DataRow(SourceMode.Production, true)]
    [DataRow(SourceMode.Production, false)]
    public void EveryNonNormalModeKeepsTypeAndUnreportedIntensityAsSubtitle(SourceMode mode, bool replay)
    {
        var settings = AppSettings.CreateDefault().Display;
        var program = Program(mode) with
        {
            IsReplayPresentation = replay,
            HideInformationBadges = true,
            RehearsalLabel = mode == SourceMode.Production && replay ? "" : "訓練・過去情報",
        };
        const string rows = "地域A　地域B\n地域C";
        var warning = new DisplayBlock("大雨警報", rows, "", DisplayStyleTokens.WeatherWarning)
            { WeatherHeading = "東京都｜継続中" };
        var output = DisplayBadgeVisibility.FormatBlockForOutput(warning, program, settings);
        Assert.AreEqual("大雨警報　東京都｜継続中", output.WeatherHeading);
        Assert.AreEqual(rows, output.PrimaryText);
        Assert.AreEqual("東京都｜継続中", warning.WeatherHeading);
        var unreported = new DisplayBlock("震度5弱以上 未入電", "地域A", "", DisplayStyleTokens.Intensity);
        Assert.AreEqual("震度5弱以上 未入電　地域A",
            DisplayBadgeVisibility.FormatBlockForOutput(unreported, program, settings).PrimaryText);
        Assert.AreEqual(mode, program.SourceMode);
        Assert.AreEqual(replay, program.IsReplayPresentation);
        Assert.IsFalse(DisplayBadgeVisibility.HideModeBanner(program, settings));
        var badgeMode = program with { HideInformationBadges = false };
        Assert.AreSame(warning, DisplayBadgeVisibility.FormatBlockForOutput(warning, badgeMode,
            settings with { HideNormalInformationBadges = true }));
    }

    private static DisplayProgram Program(SourceMode mode) => new("badge-test", EventId.Create("badge-test"),
        EventKind.WeatherWarning, mode, DateTimeOffset.UnixEpoch, OverlayPriority.WeatherWarning,
        [new DisplayPage(1, [new DisplayBlock("大雨警報", "渋谷区", "", DisplayStyleTokens.WeatherWarning)], "大雨警報 渋谷区", null)],
        DateTimeOffset.UnixEpoch, EndPolicy.AutoHide, mode == SourceMode.Production ? "" : "訓練");
}
