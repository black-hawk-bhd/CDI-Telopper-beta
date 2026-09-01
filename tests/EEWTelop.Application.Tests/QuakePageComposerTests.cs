using EEWTelop.Application.Display;
using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class QuakePageComposerTests
{
    private readonly PageComposer _composer = new();

    [TestMethod]
    public void ScalePromptUsesSafePageOrderAndCheckingOverride()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.ScalePrompt,
            [DisplayEventFactory.Point(1, JmaScale.Four)],
            DomesticTsunami.Checking,
            hypocenterName: string.Empty,
            depth: null,
            magnitude: null);

        DisplayProgram program = Compose(quake);

        Assert.AreEqual(3, program.Pages.Count);
        DisplayBlock summary = ContentBlocks(program.Pages[0]).Single();
        Assert.AreEqual(
            "12時34分頃　震度３以上の地震がありました",
            summary.PrimaryText);
        Assert.AreEqual(string.Empty, summary.SecondaryText);
        Assert.AreEqual(
            "12時34分頃　震度３以上の地震がありました",
            program.Pages[0].AccessibleText);
        Assert.AreEqual("念のため津波に注意してください。", program.Pages[1].Blocks[0].PrimaryText);
        Assert.DoesNotContain("津波の有無を調査中", program.Pages[1].AccessibleText);
        Assert.AreEqual("震度4", ContentBlocks(program.Pages[2])[0].Badge);
        Assert.DoesNotContain("固定震源", program.Pages[0].AccessibleText);
    }

    [TestMethod]
    public void ScalePromptWithUnknownTsunamiStateStillShowsCaution()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.ScalePrompt,
            [DisplayEventFactory.Point(1, JmaScale.Four)],
            DomesticTsunami.Unknown,
            hypocenterName: string.Empty,
            depth: null,
            magnitude: null,
            rawType: "VXSE51");

        DisplayProgram program = Compose(quake);

        Assert.AreEqual(
            "念のため津波に注意してください。",
            program.Pages[1].Blocks[0].PrimaryText);
    }

    [TestMethod]
    public void CancelledScalePromptUsesOnlyCancellationMessage()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.ScalePrompt,
            isCancelled: true);

        DisplayProgram program = Compose(quake);

        Assert.HasCount(1, program.Pages);
        Assert.AreEqual(
            "先ほどの、震度速報を取り消します",
            program.Pages[0].AccessibleText);
        Assert.DoesNotContain("震度３以上の地震がありました", program.Pages[0].AccessibleText);
        Assert.DoesNotContain("対象地域情報はありません", program.Pages[0].AccessibleText);
    }

    [TestMethod]
    public void CancelledDetailScaleUsesTelegramSpecificCancellationMessage()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            rawType: "VXSE53",
            isCancelled: true);

        DisplayProgram program = Compose(quake);

        Assert.HasCount(1, program.Pages);
        Assert.AreEqual(
            "先ほどの、震源・震度に関する情報を取り消します",
            program.Pages[0].AccessibleText);
        Assert.DoesNotContain("津波", program.Pages[0].AccessibleText);
    }

    [TestMethod]
    public void FixedPhraseOverrideIsAppliedBeforeDisplayWithoutChangingEventData()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.ScalePrompt,
            [DisplayEventFactory.Point(1, JmaScale.Four)],
            DomesticTsunami.Checking);
        DisplaySettings settings = DisplayEventFactory.Settings with
        {
            SubtitlePhraseOverrides = new Dictionary<string, string>
            {
                ["quake.tsunami.caution"] = "海岸や川の近くでは念のため津波に注意してください。",
            },
        };

        DisplayProgram program = _composer.Compose(quake, settings);

        Assert.AreEqual(
            "海岸や川の近くでは念のため津波に注意してください。",
            program.Pages[1].Blocks[0].PrimaryText);
        StringAssert.Contains(
            program.Pages[1].AccessibleText,
            "海岸や川の近くでは念のため津波に注意してください。");
        Assert.AreEqual(quake.Id, program.EventId);
    }

    [TestMethod]
    public void EmptyFixedPhraseOverrideSuppressesOnlyThatPhrase()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            [DisplayEventFactory.Point(1, JmaScale.Four)],
            DomesticTsunami.Checking);
        DisplaySettings settings = DisplayEventFactory.Settings with
        {
            SubtitlePhraseOverrides = new Dictionary<string, string>
            {
                ["quake.tsunami.checking"] = string.Empty,
            },
        };

        DisplayProgram program = _composer.Compose(quake, settings);
        DisplayBlock advisory = ContentBlocks(program.Pages[1])[0];

        Assert.DoesNotContain("津波の有無を調査中です。", advisory.PrimaryText);
        StringAssert.Contains(advisory.PrimaryText, "念のため津波に注意してください。");
    }

    [TestMethod]
    public void DetailScaleUsesSummaryTsunamiThenIntensityAndComment()
    {
        QuakePoint[] points = Enumerable.Range(1, 7)
            .Select(number => DisplayEventFactory.Point(number, JmaScale.FiveLower))
            .ToArray();
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            points,
            DomesticTsunami.None,
            comment: "固定付加文",
            magnitude: 6);

        DisplayProgram program = Compose(quake);

        Assert.AreEqual(5, program.Pages.Count);
        StringAssert.StartsWith(program.Pages[0].AccessibleText, "12時34分頃");
        Assert.DoesNotContain("12:34", program.Pages[0].AccessibleText);
        StringAssert.Contains(program.Pages[0].AccessibleText, "震源の深さは10km");
        StringAssert.Contains(program.Pages[0].AccessibleText, "マグニチュードは6.0と推定されます");
        Assert.AreEqual("この地震による津波の心配はありません", ContentBlocks(program.Pages[1])[0].PrimaryText);
        Assert.AreEqual("固定付加文", ContentBlocks(program.Pages[^1])[0].PrimaryText);

        DisplayPage[] intensityPages = program.Pages
            .Where(page => ContentBlocks(page).Any(block => block.StyleToken == DisplayStyleTokens.Intensity))
            .ToArray();
        Assert.AreEqual(2, intensityPages.Length);
        Assert.IsTrue(intensityPages.All(page => ContentBlocks(page).Count <= 2));
        Assert.IsTrue(intensityPages
            .SelectMany(ContentBlocks)
            .Where(block => block.StyleToken == DisplayStyleTokens.Intensity)
            .All(block => block.PrimaryText.Split('　').Length <= 3));
    }

    [TestMethod]
    public void NonEffectiveTsunamiUsesNoDamageWording()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            [DisplayEventFactory.Point(1, JmaScale.Four)],
            DomesticTsunami.NonEffective);

        DisplayProgram program = Compose(quake);

        Assert.IsTrue(program.Pages
            .SelectMany(ContentBlocks)
            .Any(static block => block.PrimaryText ==
                "日本の沿岸では若干の海面変動があるかもしれませんが、被害の心配はありません"));
        Assert.IsFalse(program.Pages
            .SelectMany(ContentBlocks)
            .Any(static block => block.PrimaryText ==
                "この地震により若干の海面変動が予想されます"));
    }

    [TestMethod]
    public void ActiveTsunamiWarningUsesTsunamiInformationWording()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            [DisplayEventFactory.Point(1, JmaScale.Four)],
            DomesticTsunami.Warning);

        DisplayProgram program = Compose(quake);

        Assert.IsTrue(program.Pages
            .SelectMany(ContentBlocks)
            .Any(static block => block.PrimaryText ==
                "この地震により津波情報を発表しています"));
        Assert.IsFalse(program.Pages
            .SelectMany(ContentBlocks)
            .Any(static block => block.PrimaryText.Contains(
                "津波警報等が発表されています",
                StringComparison.Ordinal)));
    }

    [TestMethod]
    public void EewNoStrongShakingCommentIsDisplayedOnTwoLines()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            points: [],
            DomesticTsunami.None,
            comment: "この地震で緊急地震速報を発表しましたが、強い揺れは観測されませんでした。");

        DisplayProgram program = Compose(quake);

        DisplayBlock comment = ContentBlocks(program.Pages[^1]).Single();
        Assert.AreEqual(
            "この地震で緊急地震速報を発表しましたが\n強い揺れは観測されませんでした",
            comment.PrimaryText);
        Assert.IsFalse(program.Pages
            .SelectMany(ContentBlocks)
            .Any(block => block.StyleToken == DisplayStyleTokens.Empty));
    }

    [TestMethod]
    public void LongFreeFormCommentIsPaginatedToAtMostThreeVisualLines()
    {
        const string comment =
            "この地震について長い補足情報が発表されています、今後の情報に十分注意してください、" +
            "揺れの強かった地域では落石や崖崩れなどに注意してください、" +
            "危険な場所には近づかず自治体からの情報を確認してください";
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            [DisplayEventFactory.Point(1, JmaScale.Four)],
            comment: comment);

        DisplayProgram program = Compose(quake);
        DisplayPage[] commentPages = program.Pages
            .Where(page => ContentBlocks(page).Any(block =>
                block.StyleToken == DisplayStyleTokens.Comment))
            .ToArray();

        Assert.IsGreaterThan(1, commentPages.Length);
        Assert.IsTrue(commentPages.All(page => ContentBlocks(page)
            .Where(block => block.StyleToken == DisplayStyleTokens.Comment)
            .Sum(block => EstimateVisualLines(block.PrimaryText)) <= 3));
        Assert.AreEqual(
            comment,
            string.Concat(commentPages
                .SelectMany(ContentBlocks)
                .Where(block => block.StyleToken == DisplayStyleTokens.Comment)
                .Select(block => block.PrimaryText)));
    }

    private static int EstimateVisualLines(string value) => value
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Split('\n')
        .Sum(line => Math.Max(
            1,
            (int)Math.Ceiling(
                line.Sum(static character => character <= 0x7f ? 0.55 : 1) / 24)));

    [TestMethod]
    public void ContinuedIntensityPageAlwaysRepeatsBadge()
    {
        QuakePoint[] points = Enumerable.Range(1, 13)
            .Select(number => DisplayEventFactory.Point(number, JmaScale.FiveLower))
            .ToArray();
        DisplayProgram program = Compose(DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            points));
        DisplayPage[] intensityPages = program.Pages
            .Where(page => ContentBlocks(page).Any(block => block.StyleToken == DisplayStyleTokens.Intensity))
            .ToArray();

        Assert.AreEqual(3, intensityPages.Length);
        Assert.IsTrue(intensityPages.All(page => ContentBlocks(page)[0].Badge == "震度5弱"));
        Assert.AreEqual(string.Empty, ContentBlocks(intensityPages[0])[1].Badge);
    }

    [TestMethod]
    public void IntensitiesBelowThreeFallBackToObservedMaximum()
    {
        QuakePoint[] points =
        [
            DisplayEventFactory.Point(1, JmaScale.One),
            DisplayEventFactory.Point(2, JmaScale.Two),
            DisplayEventFactory.Point(3, JmaScale.Two),
        ];

        DisplayProgram program = Compose(DisplayEventFactory.CreateQuake(
            QuakeIssueType.ScalePrompt,
            points));
        DisplayBlock intensity = program.Pages
            .SelectMany(ContentBlocks)
            .Single(block => block.StyleToken == DisplayStyleTokens.Intensity);

        Assert.AreEqual("震度2", intensity.Badge);
        StringAssert.Contains(intensity.PrimaryText, "固定市2");
        StringAssert.Contains(intensity.PrimaryText, "固定市3");
        Assert.DoesNotContain("固定市1", intensity.PrimaryText);
    }

    [TestMethod]
    public void ScaleAndDestinationIncludesIntensityButDestinationDoesNot()
    {
        QuakePoint point = DisplayEventFactory.Point(1, JmaScale.Four);
        DisplayProgram combined = Compose(DisplayEventFactory.CreateQuake(
            QuakeIssueType.ScaleAndDestination,
            [point]));
        DisplayProgram destination = Compose(DisplayEventFactory.CreateQuake(
            QuakeIssueType.Destination,
            [point]));

        Assert.IsTrue(combined.Pages.SelectMany(ContentBlocks)
            .Any(block => block.StyleToken == DisplayStyleTokens.Intensity));
        Assert.IsFalse(destination.Pages.SelectMany(ContentBlocks)
            .Any(block => block.StyleToken == DisplayStyleTokens.Intensity));
    }

    [TestMethod]
    public void ForeignUsesCommentBeforeForeignTsunamiTemplate()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.Foreign,
            foreignTsunami: ForeignTsunami.WarningPacific,
            comment: "PTWC固定文",
            hypocenterName: "固定諸島",
            magnitude: 7.4);

        DisplayProgram program = Compose(quake);

        Assert.AreEqual(2, program.Pages.Count);
        StringAssert.StartsWith(program.Pages[0].AccessibleText, "12時34分頃");
        StringAssert.Contains(program.Pages[0].AccessibleText, "規模の大きな地震がありました");
        StringAssert.Contains(program.Pages[0].AccessibleText, "マグニチュード 7.4");
        Assert.AreEqual("PTWC固定文", ContentBlocks(program.Pages[1])[0].PrimaryText);
        Assert.DoesNotContain("太平洋で津波", program.Pages[1].AccessibleText);
    }

    [TestMethod]
    public void OtherUsesSafeKnownFieldsAndUnknownWithoutCommentProducesNoPages()
    {
        DisplayProgram other = Compose(DisplayEventFactory.CreateQuake(
            QuakeIssueType.Other,
            domesticTsunami: DomesticTsunami.None,
            comment: "安全な固定原文",
            hypocenterName: "固定火山"));
        DisplayProgram unknown = Compose(DisplayEventFactory.CreateQuake(
            QuakeIssueType.Unknown,
            comment: string.Empty,
            rawType: "FutureIssue"));

        StringAssert.StartsWith(other.Pages[0].AccessibleText, "12時34分頃");
        StringAssert.Contains(other.Pages[0].AccessibleText, "地震・火山に関する情報が発表されました");
        StringAssert.Contains(other.Pages[0].AccessibleText, "対象：固定火山");
        StringAssert.Contains(other.Pages[0].AccessibleText, "安全な固定原文");
        Assert.IsEmpty(unknown.Pages);
    }

    [TestMethod]
    public void LongPeriodObservationShowsOnlyClassAndObservedAreas()
    {
        const string explanatoryComment =
            "各長周期地震動階級に対する簡易な現象表現 https://www.data.jma.go.jp/eew/data/ltpgm/";
        var observation = new LongPeriodIntensityInfo(
            1,
            [new LongPeriodIntensityArea("青森県", "青森県津軽北部", 1)]);
        DisplayProgram program = Compose(DisplayEventFactory.CreateQuake(
            QuakeIssueType.LongPeriodObservation,
            comment: explanatoryComment,
            rawType: "VXSE62",
            longPeriodIntensity: observation));

        Assert.AreEqual(2, program.Pages.Count);
        StringAssert.Contains(program.Pages[0].AccessibleText, "長周期地震動階級1");
        Assert.AreEqual("長周期階級1", ContentBlocks(program.Pages[1])[0].Badge);
        Assert.AreEqual("青森県津軽北部", ContentBlocks(program.Pages[1])[0].PrimaryText);
        Assert.IsFalse(program.Pages.Any(page =>
            page.AccessibleText.Contains("data.jma.go.jp", StringComparison.Ordinal) ||
            page.AccessibleText.Contains("簡易な現象表現", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SubsequentEarthquakeAdvisoryUsesDedicatedFourPageLayout()
    {
        const string headline =
            "　本日（２０日）１６時５２分に三陸沖を震源とするモーメントマグニチュード（Ｍｗ）７．４の地震が発生しました。" +
            "この地震の発生により、北海道の根室沖から東北地方の三陸沖にかけての巨大地震の想定震源域では、新たな大規模地震の発生可能性が平常時と比べて相対的に高まっていると考えられます。" +
            "今後の政府や自治体などからの呼びかけ等に応じた防災対応をとってください。";
        DisplayProgram program = Compose(DisplayEventFactory.CreateQuake(
            QuakeIssueType.SubsequentEarthquakeAdvisory,
            rawType: "VYSE60",
            headline: headline));

        Assert.AreEqual(4, program.Pages.Count);
        StringAssert.Contains(
            program.Pages[0].AccessibleText,
            "北海道・三陸沖後発地震注意情報");
        StringAssert.Contains(
            program.Pages[1].AccessibleText,
            "本日（20日）16時52分に三陸沖を震源とするモーメントマグニチュード7.4の地震が発生しました");
        StringAssert.Contains(
            program.Pages[1].AccessibleText,
            "北海道の根室沖から東北地方の三陸沖にかけての");
        StringAssert.Contains(
            program.Pages[2].AccessibleText,
            "巨大地震の想定震源域では、新たな大規模地震の発生可能性が");
        StringAssert.Contains(
            program.Pages[2].AccessibleText,
            "平常時と比べて相対的に高まっていると考えられます。");
        StringAssert.Contains(
            program.Pages[3].AccessibleText,
            "今後の政府や自治体などからの呼びかけ等に応じた防災対応をとってください");
        Assert.IsFalse(program.Pages.Any(page =>
            page.AccessibleText.Contains("（Ｍｗ）", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CorrectionAndPageIndicatorAreExplicitBlocks()
    {
        DisplayProgram program = Compose(DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            [DisplayEventFactory.Point(1, JmaScale.Four)],
            correction: CorrectionType.ScaleAndDestination));

        Assert.AreEqual("訂正", ContentBlocks(program.Pages[0])[0].Badge);
        Assert.AreEqual("震度・震源を訂正します", ContentBlocks(program.Pages[0])[0].PrimaryText);
        Assert.IsTrue(program.Pages.All(page =>
            page.Blocks[^1].StyleToken == DisplayStyleTokens.PageIndicator));
        Assert.AreEqual("1 / 3", program.Pages[0].Blocks[^1].PrimaryText);
    }

    [TestMethod]
    public void ShallowDepthUsesEstablishedWording()
    {
        DisplayProgram program = Compose(DisplayEventFactory.CreateQuake(
            QuakeIssueType.Destination,
            depth: 0));

        StringAssert.Contains(program.Pages[0].AccessibleText, "震源はごく浅い");
    }

    [TestMethod]
    public void TenThousandPointsArePagedWithoutExpandingFixedCapacity()
    {
        QuakePoint[] points = Enumerable.Range(1, 10_000)
            .Select(number => DisplayEventFactory.Point(number, JmaScale.Three))
            .ToArray();

        DisplayProgram program = Compose(DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            points));
        DisplayPage[] intensityPages = program.Pages
            .Where(page => ContentBlocks(page).Any(block =>
                block.StyleToken == DisplayStyleTokens.Intensity))
            .ToArray();

        Assert.AreEqual(1_667, intensityPages.Length);
        Assert.IsTrue(intensityPages.All(page => ContentBlocks(page).Count <= 2));
        Assert.IsTrue(intensityPages.All(page => ContentBlocks(page)[0].Badge == "震度3"));
        Assert.IsTrue(intensityPages
            .SelectMany(ContentBlocks)
            .All(block => block.PrimaryText.Split('　').Length <= 3));
    }

    private DisplayProgram Compose(QuakeEvent quake) =>
        _composer.Compose(quake, DisplayEventFactory.Settings);

    private static IReadOnlyList<DisplayBlock> ContentBlocks(DisplayPage page) =>
        page.Blocks.Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator).ToArray();
}
