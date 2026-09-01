using System.Globalization;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class VolcanoPageComposerTests
{
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 8, 13, 15, 30, 0, TimeSpan.FromHours(9));

    [TestMethod]
    public void WarningForecastShowsVolcanoAlertAndTargetMunicipality()
    {
        DisplayProgram program = Compose(CreateVolcano(
            VolcanoInformationType.WarningForecast,
            VolcanoAlertLevel.Level3,
            "レベル３（入山規制）",
            eventTime: null));

        Assert.AreEqual(EventKind.Volcano, program.Kind);
        Assert.AreEqual(OverlayPriority.VolcanoWarning, program.Priority);
        Assert.AreEqual("噴火警報", program.Pages[0].Blocks[0].Badge);
        Assert.Contains("桜島", program.Pages[0].AccessibleText);
        Assert.Contains("レベル３", program.Pages[0].AccessibleText);
        Assert.IsTrue(program.Pages.Any(page => page.AccessibleText.Contains("鹿児島市")));
    }

    [TestMethod]
    public void EruptionFlashShowsOccurrenceTimeAndHighestVolcanoPriority()
    {
        DisplayProgram program = Compose(CreateVolcano(
            VolcanoInformationType.EruptionFlash,
            VolcanoAlertLevel.Unknown,
            string.Empty,
            IssuedAt.AddMinutes(-1),
            headline: "＜桜島で噴火が発生＞",
            activity: "桜島で、令和８年８月１３日１５時２９分頃、噴火が発生しました。"));

        Assert.AreEqual(OverlayPriority.EruptionFlash, program.Priority);
        Assert.AreEqual("噴火速報", program.Pages[0].Blocks[0].Badge);
        Assert.Contains("15時29分", program.Pages[0].AccessibleText);
        Assert.Contains("桜島で噴火が発生", program.Pages[0].AccessibleText);
        Assert.AreEqual(
            1,
            program.Pages.SelectMany(page => page.Blocks)
                .Count(block => block.PrimaryText.Contains("噴火が発生", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void EruptionFlashKeepsActivityWhenItAddsSpecificCraterInformation()
    {
        DisplayProgram program = Compose(CreateVolcano(
            VolcanoInformationType.EruptionFlash,
            VolcanoAlertLevel.Unknown,
            string.Empty,
            IssuedAt.AddMinutes(-1),
            headline: "桜島で噴火が発生",
            activity: "南岳山頂火口で噴火が発生しました。"));

        Assert.IsTrue(program.Pages.Any(page =>
            page.AccessibleText.Contains("南岳山頂火口", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void EruptionFlashCancellationDoesNotShowNormalEruptionTextOrOccurrenceTime()
    {
        DisplayProgram program = Compose(CreateVolcano(
            VolcanoInformationType.EruptionFlash,
            VolcanoAlertLevel.Unknown,
            string.Empty,
            eventTime: null,
            headline: "火山名 御嶽山 噴火速報",
            activity: string.Empty,
            isCancelled: true,
            isTelegramCancellation: true,
            bodyText: "先に発表した御嶽山の噴火速報は取り消します。"));

        Assert.AreEqual("噴火速報取消", program.Pages[0].Blocks[0].Badge);
        Assert.AreEqual(
            "先ほどの、噴火速報を取り消します",
            program.Pages[0].Blocks[0].PrimaryText);
        Assert.DoesNotContain("噴火が発生", program.Pages[0].AccessibleText);
        Assert.AreEqual(1, program.Pages.Count);
    }

    [TestMethod]
    public void EruptionFlashUsesApproximateMarkerFromEventDateTime()
    {
        DisplayProgram program = Compose(CreateVolcano(
            VolcanoInformationType.EruptionFlash,
            VolcanoAlertLevel.Unknown,
            string.Empty,
            IssuedAt.AddMinutes(-1),
            headline: "御嶽山で噴火が発生したもよう",
            activity: string.Empty,
            eventTimeIsApproximate: true));

        Assert.Contains("15時29分頃", program.Pages[0].AccessibleText);
    }

    [TestMethod]
    public void NonLevelVolcanoWarningUsesWarningBadgeAndPriority()
    {
        DisplayProgram program = Compose(CreateVolcano(
            VolcanoInformationType.WarningForecast,
            VolcanoAlertLevel.Unknown,
            "周辺海域警戒",
            eventTime: null,
            isWarning: true));

        Assert.AreEqual(OverlayPriority.VolcanoWarning, program.Priority);
        Assert.AreEqual("噴火警報", program.Pages[0].Blocks[0].Badge);
    }

    [TestMethod]
    public void WarningReleaseUsesReleaseBadgeWithoutTelegramCancellation()
    {
        DisplayProgram program = Compose(CreateVolcano(
            VolcanoInformationType.WarningForecast,
            VolcanoAlertLevel.Level1,
            "レベル１（活火山であることに留意）",
            eventTime: null,
            isCancelled: true));

        Assert.AreEqual("解除", program.Pages[0].Blocks[0].Badge);
    }

    [TestMethod]
    public void CorrectedEruptionFlashShowsCorrectionBeforeCorrectedContent()
    {
        DisplayProgram program = Compose(CreateVolcano(
            VolcanoInformationType.EruptionFlash,
            VolcanoAlertLevel.Unknown,
            string.Empty,
            IssuedAt.AddMinutes(-1),
            headline: "御嶽山で噴火が発生したもよう",
            activity: string.Empty,
            correction: CorrectionType.Generic));

        Assert.AreEqual("訂正", program.Pages[0].Blocks[0].Badge);
        Assert.AreEqual("噴火速報", program.Pages[0].Blocks[1].Badge);
    }

    [TestMethod]
    public void WarningForecastPaginatesLongSectionsToAtMostThreeVisualLines()
    {
        DisplayProgram program = Compose(CreateVolcano(
            VolcanoInformationType.WarningForecast,
            VolcanoAlertLevel.Level3,
            "レベル３（入山規制）",
            eventTime: null,
            headline: "＜阿蘇山に火口周辺警報（噴火警戒レベル３、入山規制）を発表＞。",
            activity:
                "阿蘇山では、12日19時頃から火山性微動の振幅が増大し、本日（14日）15時過ぎから中岳西山腹観測点南北動成分の1分間平均振幅が4マイクロメートル毎秒を超えています。" +
                "また、本日実施した現地調査では、火山ガス（二酸化硫黄）の放出量が1日あたり2000トンを超えて急増していることを確認しました（前回6日、1200トン）。" +
                "阿蘇山では火山活動にさらなる高まりがみられていることから、中岳第一火口から概ね2kmの範囲に影響を及ぼす噴火が発生する可能性があります。",
            prevention:
                "中岳第一火口から概ね2kmの範囲では、噴火に伴う弾道を描いて飛散する大きな噴石及び火砕流に警戒してください。" +
                "風下側では、火山灰だけでなく小さな噴石が遠方まで風に流されて降るため注意してください。" +
                "また、火山ガスに注意してください。",
            appendix:
                "＊＊（参考：噴火警戒レベルの説明）＊＊。" +
                "【レベル5（避難）】：危険な居住地域からの避難等が必要。" +
                "【レベル4（高齢者等避難）】：警戒が必要な居住地域での高齢者等の要配慮者の避難、住民の避難の準備等が必要。" +
                "【レベル1（活火山であることに留意）】：状況に応じて火口内への立入規制等。"));

        DisplayPage[] textPages = program.Pages
            .Where(page => page.Blocks.Any(block =>
                block.Badge is "噴火警報" or "警戒事項" or "参考"))
            .Skip(1)
            .ToArray();

        Assert.IsGreaterThan(6, textPages.Length);
        Assert.IsTrue(
            textPages.All(page =>
                page.Blocks
                    .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
                    .Sum(block => EstimateVisualLines(block.PrimaryText)) <= 3),
            string.Join(" | ", textPages.Select(page =>
                $"{page.Index}:{page.Blocks
                    .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
                    .Sum(block => EstimateVisualLines(block.PrimaryText))}:" +
                string.Join("/", page.Blocks.Select(static block => block.PrimaryText)))));
        Assert.IsTrue(program.Pages.Any(page =>
            page.AccessibleText.Contains("火山性微動の振幅が増大", StringComparison.Ordinal)));
        string allPrimaryText = string.Concat(program.Pages
            .SelectMany(static page => page.Blocks)
            .Select(static block => block.PrimaryText));
        Assert.Contains("【レベル1（活火山であることに留意）】", allPrimaryText);
        Assert.Contains("火口内への立入規制等", allPrimaryText);
    }

    [TestMethod]
    public void Vfvo50AlertLevelIncreaseShowsOnlyAnnouncementAndLevelChange()
    {
        DisplayProgram program = Compose(CreateVolcano(
            VolcanoInformationType.WarningForecast,
            VolcanoAlertLevel.Level3,
            "レベル３（入山規制）",
            eventTime: null,
            headline:
                "＜阿蘇山に火口周辺警報（噴火警戒レベル３、入山規制）を発表＞ " +
                "中岳第一火口から概ね２ｋｍの範囲では警戒してください。 " +
                "＜噴火警戒レベルを２（火口周辺規制）から３（入山規制）に引上げ＞",
            activity: "火山性微動の振幅が増大しています。",
            prevention: "大きな噴石及び火砕流に警戒してください。",
            appendix: "噴火警戒レベルの説明。"));

        Assert.AreEqual(1, program.Pages.Count);
        Assert.AreEqual(1, program.Pages[0].Blocks.Count(block =>
            block.StyleToken != DisplayStyleTokens.PageIndicator));
        Assert.AreEqual("噴火警報", program.Pages[0].Blocks[0].Badge);
        Assert.AreEqual(
            "阿蘇山に火口周辺警報（噴火警戒レベル３、入山規制）を発表しました\n" +
            "噴火警戒レベルを２（火口周辺規制）から３（入山規制）に引き上げ",
            program.Pages[0].Blocks[0].PrimaryText);
        Assert.DoesNotContain("火山性微動", program.Pages[0].AccessibleText);
        Assert.DoesNotContain("大きな噴石", program.Pages[0].AccessibleText);
        Assert.DoesNotContain("噴火警戒レベルの説明", program.Pages[0].AccessibleText);
    }

    private static DisplayProgram Compose(VolcanoEvent volcano) =>
        new PageComposer().Compose(volcano, DisplayEventFactory.Settings);

    private static VolcanoEvent CreateVolcano(
        VolcanoInformationType informationType,
        VolcanoAlertLevel level,
        string levelText,
        DateTimeOffset? eventTime,
        string headline = "桜島で火山情報が発表されました。",
        string activity = "活発な噴火活動が続いています。",
        bool? isWarning = null,
        bool isCancelled = false,
        bool isTelegramCancellation = false,
        bool? eventTimeIsApproximate = null,
        string bodyText = "",
        CorrectionType correction = CorrectionType.None,
        string prevention = "大きな噴石に警戒してください。",
        string appendix = "") => new(
            EventId.Create("volcano-display-fixture"),
            "dmdata.jp",
            IssuedAt,
            IssuedAt.AddSeconds(1),
            "VOLCANO-SIGNATURE",
            SourceMode.Production,
            new IssueInfo("気象庁", IssuedAt, informationType ==
                VolcanoInformationType.EruptionFlash ? "VFVO56" : "VFVO50",
                correction),
            informationType,
            "桜島",
            "506",
            level,
            levelText,
            headline,
            activity,
            prevention,
            [new VolcanoTargetArea("鹿児島市", "4620100", "火口周辺警報", "13", "発表")],
            eventTime,
            isCancelled,
            isWarning: isWarning ?? (informationType == VolcanoInformationType.WarningForecast &&
                level >= VolcanoAlertLevel.Level2),
            alertLevelCode: level == VolcanoAlertLevel.Unknown
                ? string.Empty
                : ((int)level + 10).ToString(CultureInfo.InvariantCulture),
            eventTimeIsApproximate: eventTimeIsApproximate ??
                informationType == VolcanoInformationType.EruptionFlash,
            eventTimePrecision: eventTime is null ? string.Empty : "yyyy-mm-ddThh:mm",
            isTelegramCancellation: isTelegramCancellation,
            appendix: appendix,
            bodyText: bodyText);

    private static int EstimateVisualLines(string value)
    {
        double columns = value.Sum(static character => character <= 0x7f ? 0.55 : 1);
        return Math.Max(1, (int)Math.Ceiling(columns / 24));
    }
}
