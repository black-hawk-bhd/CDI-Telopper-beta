using EEWTelop.Application.Display;
using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class EewAndTsunamiPageComposerTests
{
    private static readonly string[] ExpectedTsunamiGradeLabels =
    [
        "大津波警報",
        "津波警報",
        "津波注意報",
        "津波予報（若干の海面変動）",
    ];

    private static readonly string[] ExpectedKantoDistrictLabels =
        ["茨城", "栃木", "群馬", "埼玉", "千葉", "東京", "神奈川"];

    private static readonly string[] ExpectedKantoKoshinAggregatedLabels =
        ["関東", "甲信", "静岡", "岐阜"];

    private static readonly string[] ExpectedWideAreaAggregatedLabels =
        ["関東", "東北", "東海", "大阪", "高知"];

    private static readonly string[] ExpectedTokyoIslandLabels =
        ["関東", "伊豆諸島"];

    private static readonly string[] ExpectedHokkaidoDistrictLabels =
        ["北海道道央", "北海道道南", "北海道道北", "北海道道東"];

    private readonly PageComposer _composer = new();

    [TestMethod]
    public void EewUsesOnlyDeclaredAreasAndDoesNotInferFromHypocenter()
    {
        EewArea[] areas =
        [
            new EewArea("固定県A", "地域1", JmaScale.FiveLower, 50,
                EewWarningKind.ForecastNotArrived, null),
            new EewArea("固定県A", "地域2", JmaScale.Four, 45,
                EewWarningKind.ForecastArrived, null),
            new EewArea("固定県B", "地域3", JmaScale.Four, 45,
                EewWarningKind.Plum, null),
        ];

        DisplayProgram program = Compose(DisplayEventFactory.CreateEew(areas));
        string areaText = program.Pages[0].Blocks
            .Single(block => block.StyleToken == DisplayStyleTokens.EewAreas)
            .PrimaryText;

        Assert.AreEqual(OverlayPriority.Eew, program.Priority);
        Assert.AreEqual("固定県A　固定県B", areaText);
        Assert.DoesNotContain("固定震源", areaText);
        Assert.DoesNotContain("P2P", program.Pages[0].AccessibleText);
        Assert.IsFalse(program.Pages[0].AccessibleText.Contains("dmdata", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void EewNeverExposesProviderAttributionInViewerFacingText()
    {
        DisplayProgram p2p = Compose(DisplayEventFactory.CreateEew(provider: "P2PQuake"));
        DisplayProgram dmdata = Compose(DisplayEventFactory.CreateEew(provider: "dmdata.jp"));

        foreach (string text in new[] { p2p.Pages[0].AccessibleText, dmdata.Pages[0].AccessibleText })
        {
            Assert.IsFalse(text.Contains("P2P", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(text.Contains("dmdata", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("参考情報", text);
        }
    }

    [TestMethod]
    public void EewKeepsPrefecturalForecastDistrictsWhenFewerThanEightAreAnnounced()
    {
        EewArea[] areas =
        [
            Area("茨城県"),
            Area("栃木県"),
            Area("群馬県"),
            Area("埼玉県"),
            Area("千葉県"),
            Area("東京都", "東京都多摩東部"),
            Area("神奈川県"),
        ];

        IReadOnlyList<string> labels = EewAreaLabelFormatter.Format(areas);

        CollectionAssert.AreEqual(
            ExpectedKantoDistrictLabels,
            labels.ToArray());
    }

    [TestMethod]
    public void EewCountsEachPrefecturalForecastDistrictOnlyOnce()
    {
        EewArea[] areas =
        [
            Area("茨城県", "茨城県北部"),
            Area("茨城県", "茨城県南部"),
            Area("栃木県"),
            Area("群馬県"),
            Area("埼玉県"),
            Area("千葉県"),
            Area("東京都", "東京都２３区"),
            Area("神奈川県"),
        ];

        IReadOnlyList<string> labels = EewAreaLabelFormatter.Format(areas);

        CollectionAssert.AreEqual(
            ExpectedKantoDistrictLabels,
            labels.ToArray());
    }

    [TestMethod]
    public void EewAggregatesThreeDistrictsAndCompleteSmallRegionsFromEightDistricts()
    {
        EewArea[] areas =
        [
            Area("埼玉県"),
            Area("千葉県"),
            Area("東京都", "東京都２３区"),
            Area("神奈川県"),
            Area("山梨県"),
            Area("長野県"),
            Area("静岡県"),
            Area("岐阜県"),
        ];

        IReadOnlyList<string> labels = EewAreaLabelFormatter.Format(areas);

        CollectionAssert.AreEqual(
            ExpectedKantoKoshinAggregatedLabels,
            labels.ToArray());
    }

    [TestMethod]
    public void EewWideAreaRuleAggregatesEveryRegionWithTwoOrMoreDistricts()
    {
        EewArea[] areas =
        [
            Area("茨城県"),
            Area("栃木県"),
            Area("青森県"),
            Area("岩手県"),
            Area("静岡県"),
            Area("岐阜県"),
            Area("大阪府"),
            Area("高知県"),
        ];

        IReadOnlyList<string> labels = EewAreaLabelFormatter.Format(areas);

        CollectionAssert.AreEqual(
            ExpectedWideAreaAggregatedLabels,
            labels.ToArray());
    }

    [TestMethod]
    public void EewKeepsTokyoIslandsSeparateFromMainlandTokyo()
    {
        EewArea[] areas =
        [
            Area("東京都", "東京都２３区"),
            Area("神奈川県"),
            Area("千葉県"),
            Area("東京都", "伊豆大島"),
            Area("東京都", "新島"),
            Area("東京都", "神津島"),
            Area("東京都", "三宅島"),
            Area("東京都", "八丈島"),
        ];

        IReadOnlyList<string> labels = EewAreaLabelFormatter.Format(areas);

        CollectionAssert.AreEqual(
            ExpectedTokyoIslandLabels,
            labels.ToArray());
    }

    [TestMethod]
    public void EewNormalizesDetailedHokkaidoAreasToFourForecastDistricts()
    {
        EewArea[] areas =
        [
            Area(string.Empty, "石狩地方北部"),
            Area(string.Empty, "胆振地方中東部"),
            Area(string.Empty, "宗谷地方南部"),
            Area(string.Empty, "根室地方北部"),
        ];

        IReadOnlyList<string> labels = EewAreaLabelFormatter.Format(areas);

        CollectionAssert.AreEqual(
            ExpectedHokkaidoDistrictLabels,
            labels.ToArray());
    }

    [TestMethod]
    public void EewCancellationHasNoWarningAreas()
    {
        DisplayProgram program = Compose(DisplayEventFactory.CreateEew(cancelled: true));

        Assert.AreEqual("緊急地震速報（取消）", program.Pages[0].Blocks[0].PrimaryText);
        StringAssert.Contains(program.Pages[0].AccessibleText, "先ほどの、緊急地震速報を取り消します");
        Assert.IsFalse(program.Pages[0].Blocks.Any(block => block.StyleToken == DisplayStyleTokens.EewAreas));
    }

    [TestMethod]
    public void TestEewUsesSeparateSafetyBannerAndAgencyHeader()
    {
        EewEvent eew = DisplayEventFactory.CreateEew(isTest: true);

        DisplayProgram program = Compose(eew);

        Assert.AreEqual("操作テスト／訓練", program.RehearsalLabel);
        Assert.AreEqual("緊急地震速報（気象庁）", program.Pages[0].Blocks[0].PrimaryText);
        Assert.AreEqual(DisplayStyleTokens.EewHeader, program.Pages[0].Blocks[0].StyleToken);
        Assert.IsFalse(eew.IsFinal);
    }

    [TestMethod]
    public void EewForecastWarningAndFinalUseSharedAgencyHeader()
    {
        DisplayProgram forecast = Compose(DisplayEventFactory.CreateEew(
            isWarning: false,
            isFinal: false));
        DisplayProgram warning = Compose(DisplayEventFactory.CreateEew(
            isWarning: true,
            isFinal: false));
        DisplayProgram final = Compose(DisplayEventFactory.CreateEew(
            isWarning: true,
            isFinal: true));

        Assert.AreEqual("緊急地震速報（気象庁）", forecast.Pages[0].Blocks[0].PrimaryText);
        Assert.AreEqual("緊急地震速報（気象庁）", warning.Pages[0].Blocks[0].PrimaryText);
        Assert.AreEqual("緊急地震速報（気象庁）", final.Pages[0].Blocks[0].PrimaryText);
        StringAssert.Contains(forecast.Pages[0].AccessibleText, "今後の情報に注意");
        StringAssert.Contains(warning.Pages[0].AccessibleText, "強い揺れに警戒");
    }

    [TestMethod]
    public void ThirteenTsunamiAreasUseThreePerPageAndRepeatContinuedBadge()
    {
        TsunamiArea[] areas = Enumerable.Range(1, 13)
            .Select(number => DisplayEventFactory.TsunamiArea(number, TsunamiGrade.Warning))
            .ToArray();

        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(areas));

        Assert.AreEqual(5, program.Pages.Count);
        Assert.IsTrue(program.Pages.All(page => ContentBlocks(page).Count <= 3));
        Assert.IsTrue(program.Pages.All(page => ContentBlocks(page)[0].Badge == "津波警報"));
        Assert.AreEqual(string.Empty, ContentBlocks(program.Pages[0])[1].Badge);
        Assert.AreEqual(EndPolicy.LoopUntilReplaced, program.EndPolicy);
        Assert.AreEqual(OverlayPriority.TsunamiWarning, program.Priority);
        Assert.AreEqual("〔12時35分 １ｍ〕", ContentBlocks(program.Pages[0])[0].SecondaryText);
    }

    [TestMethod]
    public void TsunamiGradesAreOrderedAndUnknownUsesSafeLabel()
    {
        TsunamiArea[] areas =
        [
            DisplayEventFactory.TsunamiArea(1, TsunamiGrade.Unknown),
            DisplayEventFactory.TsunamiArea(2, TsunamiGrade.Watch),
            DisplayEventFactory.TsunamiArea(3, TsunamiGrade.MajorWarning),
            DisplayEventFactory.TsunamiArea(4, TsunamiGrade.Warning),
        ];

        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(areas));
        DisplayBlock[] blocks = program.Pages.SelectMany(ContentBlocks).ToArray();

        Assert.AreEqual("大津波警報", blocks[0].Badge);
        Assert.AreEqual("津波警報", blocks[1].Badge);
        Assert.AreEqual("津波注意報", blocks[2].Badge);
        Assert.AreEqual("津波情報", blocks[3].Badge);
    }

    [TestMethod]
    public void OffshoreObservationUsesDedicatedBadgeAndShowsOnlyAvailableObservationTime()
    {
        DateTimeOffset observedAt = new(2026, 8, 11, 1, 8, 0, TimeSpan.FromHours(9));
        TsunamiArea[] areas =
        [
            new TsunamiArea(
                TsunamiGrade.Unknown,
                Immediate: false,
                "静岡御前崎沖",
                new TsunamiFirstHeight(observedAt.AddMinutes(-6), "押し"),
                new TsunamiMaximumHeight("１．８ｍ", 1.8, observedAt, "上昇中"))
            {
                Role = TsunamiInformationRole.OffshoreObservation,
            },
            new TsunamiArea(
                TsunamiGrade.Unknown,
                Immediate: false,
                "三重尾鷲沖",
                new TsunamiFirstHeight(observedAt.AddMinutes(-5), "引き"),
                new TsunamiMaximumHeight("１．２ｍ", 1.2))
            {
                Role = TsunamiInformationRole.OffshoreObservation,
            },
        ];

        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(
            areas,
            sourceMode: SourceMode.HistoryRehearsal,
            rawType: "VTSE52"));
        IReadOnlyList<DisplayBlock> blocks = ContentBlocks(program.Pages[0]);

        Assert.AreEqual("津波観測情報", blocks[0].Badge);
        Assert.AreEqual("〔01時08分 押し １．８ｍ 上昇中〕", blocks[0].SecondaryText);
        Assert.AreEqual("〔引き １．２ｍ〕", blocks[1].SecondaryText);
    }

    [TestMethod]
    public void Vtse51CreatesSeparateForecastPredictionAndObservationPages()
    {
        DateTimeOffset issuedAt = new(2026, 8, 11, 1, 10, 0, TimeSpan.FromHours(9));
        TsunamiArea[] areas =
        [
            new TsunamiArea(
                TsunamiGrade.Warning,
                Immediate: false,
                "静岡県",
                new TsunamiFirstHeight(issuedAt.AddMinutes(20), string.Empty),
                new TsunamiMaximumHeight("３ｍ", 3))
            {
                Role = TsunamiInformationRole.ForecastArea,
            },
            new TsunamiArea(
                TsunamiGrade.Warning,
                Immediate: false,
                "御前崎",
                new TsunamiFirstHeight(issuedAt.AddMinutes(25), string.Empty),
                MaximumHeight: null)
            {
                Role = TsunamiInformationRole.StationForecast,
                ParentAreaName = "静岡県",
                HighTideAt = issuedAt.AddMinutes(65),
            },
            new TsunamiArea(
                TsunamiGrade.Unknown,
                Immediate: false,
                "御前崎",
                new TsunamiFirstHeight(issuedAt.AddMinutes(22), "押し"),
                new TsunamiMaximumHeight("１．２ｍ", 1.2, issuedAt.AddMinutes(32)))
            {
                Role = TsunamiInformationRole.CoastalObservation,
                ParentAreaName = "静岡県",
            },
        ];

        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(
            areas,
            sourceMode: SourceMode.HistoryRehearsal,
            rawType: "VTSE51",
            observationAsOf: issuedAt.AddMinutes(-1)));

        Assert.HasCount(4, program.Pages);
        Assert.AreEqual("津波観測情報", ContentBlocks(program.Pages[0])[0].Badge);
        Assert.AreEqual(
            "01時09分現在の津波観測値をお知らせします",
            ContentBlocks(program.Pages[0])[0].PrimaryText);
        Assert.AreEqual("津波警報", ContentBlocks(program.Pages[0])[1].Badge);
        Assert.AreEqual("現在発表中", ContentBlocks(program.Pages[0])[1].PrimaryText);
        Assert.AreEqual("津波観測情報", ContentBlocks(program.Pages[1])[0].Badge);
        Assert.AreEqual(
            "〔01時42分 押し １．２ｍ〕",
            ContentBlocks(program.Pages[1])[0].SecondaryText);
        Assert.AreEqual("津波到達予想", ContentBlocks(program.Pages[2])[0].Badge);
        Assert.AreEqual(
            "〔到達 01時35分 満潮 02時15分〕",
            ContentBlocks(program.Pages[2])[0].SecondaryText);
        Assert.AreEqual("津波警報", ContentBlocks(program.Pages[3])[0].Badge);
    }

    [TestMethod]
    public void Vtse51OmitsArrivalPredictionsExplicitlyConfirmedByJma()
    {
        DateTimeOffset observationAsOf = new(2026, 8, 11, 1, 40, 0, TimeSpan.FromHours(9));
        TsunamiArea[] areas =
        [
            new TsunamiArea(
                TsunamiGrade.Warning,
                Immediate: false,
                "到達済み観測点",
                new TsunamiFirstHeight(observationAsOf.AddMinutes(-5), "第１波の到達を確認"),
                MaximumHeight: null)
            {
                Role = TsunamiInformationRole.StationForecast,
            },
            new TsunamiArea(
                TsunamiGrade.Warning,
                Immediate: false,
                "未到達観測点",
                new TsunamiFirstHeight(observationAsOf.AddMinutes(5), string.Empty),
                MaximumHeight: null)
            {
                Role = TsunamiInformationRole.StationForecast,
            },
        ];

        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(
            areas,
            rawType: "VTSE51",
            observationAsOf: observationAsOf));

        DisplayBlock[] blocks = program.Pages.SelectMany(ContentBlocks).ToArray();
        Assert.IsFalse(blocks.Any(static block => block.PrimaryText == "到達済み観測点"));
        Assert.IsTrue(blocks.Any(static block => block.PrimaryText == "未到達観測点"));
    }

    [TestMethod]
    public void Vtse51ShowsActiveMajorWarningAsLargeBadgeRow()
    {
        DateTimeOffset observationAsOf = new(2026, 8, 24, 16, 44, 0, TimeSpan.FromHours(9));
        TsunamiArea[] areas =
        [
            new TsunamiArea(
                TsunamiGrade.MajorWarning,
                Immediate: false,
                "石川県能登",
                FirstHeight: null,
                MaximumHeight: null)
            {
                Role = TsunamiInformationRole.ForecastArea,
            },
            new TsunamiArea(
                TsunamiGrade.Unknown,
                Immediate: false,
                "佐渡市鷲崎",
                new TsunamiFirstHeight(observationAsOf.AddMinutes(-34), "押し"),
                new TsunamiMaximumHeight("観測中", null))
            {
                Role = TsunamiInformationRole.CoastalObservation,
            },
        ];

        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(
            areas,
            rawType: "VTSE51",
            observationAsOf: observationAsOf));

        IReadOnlyList<DisplayBlock> summary = ContentBlocks(program.Pages[0]);
        Assert.AreEqual(
            "16時44分現在の津波観測値をお知らせします",
            summary[0].PrimaryText);
        Assert.AreEqual("大津波警報", summary[1].Badge);
        Assert.AreEqual("現在発表中", summary[1].PrimaryText);
        Assert.AreEqual(
            "〔押し 観測中〕",
            ContentBlocks(program.Pages[1])[0].SecondaryText);
    }

    [TestMethod]
    public void Vtse41UpdateKeepsWarningBeforeAccumulatedObservationDetails()
    {
        DateTimeOffset issuedAt = new(2026, 8, 11, 1, 10, 0, TimeSpan.FromHours(9));
        TsunamiArea[] areas =
        [
            new TsunamiArea(
                TsunamiGrade.Warning,
                Immediate: false,
                "静岡県",
                new TsunamiFirstHeight(issuedAt.AddMinutes(5), string.Empty),
                new TsunamiMaximumHeight("３ｍ", 3)),
            new TsunamiArea(
                TsunamiGrade.Unknown,
                Immediate: false,
                "御前崎",
                new TsunamiFirstHeight(issuedAt.AddMinutes(-1), "押し"),
                new TsunamiMaximumHeight("１．２ｍ", 1.2, issuedAt))
            {
                Role = TsunamiInformationRole.CoastalObservation,
            },
        ];

        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(
            areas,
            rawType: "VTSE41",
            observationAsOf: issuedAt));

        Assert.AreEqual("津波情報", ContentBlocks(program.Pages[0])[0].Badge);
        Assert.AreEqual(
            "津波警報・津波注意報は次の通りです",
            ContentBlocks(program.Pages[0])[0].PrimaryText);
        Assert.AreEqual("津波警報", ContentBlocks(program.Pages[1])[0].Badge);
        Assert.AreEqual("津波観測情報", ContentBlocks(program.Pages[2])[0].Badge);
    }

    [TestMethod]
    [DataRow(TsunamiGrade.MajorWarning, "大津波警報・津波警報・津波注意報は次の通りです")]
    [DataRow(TsunamiGrade.Warning, "津波警報・津波注意報は次の通りです")]
    [DataRow(TsunamiGrade.Watch, "津波注意報は次の通りです")]
    public void Vtse41StartsWithAnnouncementForHighestPublishedGrade(
        TsunamiGrade highestGrade,
        string expected)
    {
        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(
            [DisplayEventFactory.TsunamiArea(1, highestGrade)],
            rawType: "VTSE41"));

        Assert.AreEqual("津波情報", ContentBlocks(program.Pages[0])[0].Badge);
        Assert.AreEqual(expected, ContentBlocks(program.Pages[0])[0].PrimaryText);
        Assert.AreEqual(
            GetExpectedTsunamiBadge(highestGrade),
            ContentBlocks(program.Pages[1])[0].Badge);
    }

    [TestMethod]
    public void ChangedVtse41StartsWithWarningChangeAnnouncement()
    {
        TsunamiEvent tsunami = DisplayEventFactory.CreateTsunami(
            [DisplayEventFactory.TsunamiArea(1, TsunamiGrade.MajorWarning)],
            rawType: "VTSE41") with
        {
            WarningStateChanged = true,
        };

        DisplayProgram program = Compose(tsunami);

        Assert.AreEqual("津波情報", ContentBlocks(program.Pages[0])[0].Badge);
        Assert.AreEqual(
            "津波情報が変更されました",
            ContentBlocks(program.Pages[0])[0].PrimaryText);
        Assert.AreEqual(
            "大津波警報・津波警報・津波注意報は次の通りです",
            ContentBlocks(program.Pages[1])[0].PrimaryText);
        Assert.AreEqual("大津波警報", ContentBlocks(program.Pages[2])[0].Badge);
    }

    [TestMethod]
    public void TsunamiForecastIsHiddenByDefaultAndDoesNotCreateDisplayPage()
    {
        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(
            [DisplayEventFactory.TsunamiArea(1, TsunamiGrade.Forecast)]));

        Assert.IsEmpty(program.Pages);
    }

    [TestMethod]
    public void EnabledTsunamiForecastUsesSeaLevelChangeLabelAfterWarnings()
    {
        TsunamiArea[] areas =
        [
            DisplayEventFactory.TsunamiArea(1, TsunamiGrade.Forecast),
            DisplayEventFactory.TsunamiArea(2, TsunamiGrade.Watch),
            DisplayEventFactory.TsunamiArea(3, TsunamiGrade.Warning),
            DisplayEventFactory.TsunamiArea(4, TsunamiGrade.MajorWarning),
        ];
        DisplaySettings settings = DisplayEventFactory.Settings with
        {
            ShowTsunamiForecast = true,
        };

        DisplayProgram program = _composer.Compose(
            DisplayEventFactory.CreateTsunami(areas),
            settings);
        DisplayBlock[] firstBlocks = program.Pages
            .Select(page => ContentBlocks(page)[0])
            .ToArray();

        CollectionAssert.AreEqual(
            ExpectedTsunamiGradeLabels,
            firstBlocks.Select(block => block.Badge).ToArray());
    }

    [TestMethod]
    public void TsunamiCancellationUsesTwentySecondOverride()
    {
        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(
            [],
            cancelled: true));

        Assert.AreEqual(OverlayPriority.TsunamiCancel, program.Priority);
        Assert.AreEqual(EndPolicy.AutoHide, program.EndPolicy);
        Assert.AreEqual(TimeSpan.FromSeconds(20), program.Pages[0].DurationOverride);
        Assert.AreEqual(
            "津波注意報・津波警報・大津波警報はすべて解除されました",
            program.Pages[0].Blocks[0].PrimaryText);
    }

    [TestMethod]
    public void TsunamiTelegramCancellationIsNotDisplayedAsWarningRelease()
    {
        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(
            [],
            cancelled: true,
            rawType: "VTSE41",
            informationType: "取消"));

        Assert.AreEqual("取消", program.Pages[0].Blocks[0].Badge);
        Assert.AreEqual(
            "先ほどの、津波警報・注意報・予報を取り消します",
            program.Pages[0].Blocks[0].PrimaryText);
        Assert.DoesNotContain("解除", program.Pages[0].AccessibleText);
    }

    [TestMethod]
    public void SandboxTsunamiIsLabeledAndDoesNotLoopForever()
    {
        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(
            [DisplayEventFactory.TsunamiArea(1, TsunamiGrade.Watch)],
            sourceMode: SourceMode.Sandbox));

        Assert.AreEqual("サンドボックス／訓練", program.RehearsalLabel);
        Assert.AreEqual(EndPolicy.AutoHide, program.EndPolicy);
        Assert.AreEqual(OverlayPriority.TsunamiWatch, program.Priority);
    }

    [TestMethod]
    public void SinglePageDoesNotShowPageIndicator()
    {
        DisplayProgram program = Compose(DisplayEventFactory.CreateTsunami(
            [DisplayEventFactory.TsunamiArea(1, TsunamiGrade.Watch)]));

        Assert.IsFalse(program.Pages[0].Blocks.Any(block =>
            block.StyleToken == DisplayStyleTokens.PageIndicator));
    }

    [TestMethod]
    public void ComposerIsDeterministicAndUsesReceivedTimeAsStart()
    {
        TsunamiEvent tsunami = DisplayEventFactory.CreateTsunami(
            [DisplayEventFactory.TsunamiArea(1, TsunamiGrade.Watch)]);

        DisplayProgram first = Compose(tsunami);
        DisplayProgram second = Compose(tsunami);

        Assert.AreEqual(first.ProgramId, second.ProgramId);
        Assert.AreEqual(first.StartedAtUtc, tsunami.ReceivedAt.ToUniversalTime());
        CollectionAssert.AreEqual(
            first.Pages.Select(static page => page.AccessibleText).ToArray(),
            second.Pages.Select(static page => page.AccessibleText).ToArray());
    }

    [TestMethod]
    public void PageComposerHasNoRuntimeServiceDependencies()
    {
        FieldInfo[] instanceFields = typeof(PageComposer).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.IsEmpty(instanceFields);
        Assert.HasCount(1, typeof(PageComposer).GetConstructors());
        Assert.IsEmpty(typeof(PageComposer).GetConstructors()[0].GetParameters());
    }

    private DisplayProgram Compose(DisasterEvent disasterEvent) =>
        _composer.Compose(disasterEvent, DisplayEventFactory.Settings);

    private static EewArea Area(string prefecture, string? name = null) => new(
        prefecture,
        name ?? $"{prefecture}対象地域",
        JmaScale.Four,
        45,
        EewWarningKind.ForecastNotArrived,
        ArrivalTime: null);

    private static IReadOnlyList<DisplayBlock> ContentBlocks(DisplayPage page) =>
        page.Blocks.Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator).ToArray();

    private static string GetExpectedTsunamiBadge(TsunamiGrade grade) => grade switch
    {
        TsunamiGrade.MajorWarning => "大津波警報",
        TsunamiGrade.Warning => "津波警報",
        TsunamiGrade.Watch => "津波注意報",
        _ => "津波情報",
    };
}
