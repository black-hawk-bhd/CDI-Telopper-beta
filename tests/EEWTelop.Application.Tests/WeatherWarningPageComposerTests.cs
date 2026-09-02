using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class WeatherWarningPageComposerTests
{
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 8, 11, 0, 0, 0, TimeSpan.FromHours(9));

    private static readonly string[] ExpectedReleaseTexts =
    [
        "熊本県熊本市の大雨特別警報は解除されました",
        "熊本県八代市のレベル４土砂災害危険警報は解除されました",
        "熊本県天草市の高潮警報は解除されました",
        "石川県金沢市の雷注意報は解除されました",
    ];

    private static readonly string[] ExpectedRecordRainfallLines =
    [
        "富山市八尾町丸山で１時間に１０９ミリ",
        "富山市山間部西付近で１時間に約１００ミリ",
    ];

    [TestMethod]
    public void WarningReleasesUsePrefectureMunicipalityAndWarningName()
    {
        WeatherWarningEvent weather = CreateWeather(
            [
                Released("熊本市", "4310000", "大雨特別警報", WeatherWarningLevel.SpecialWarning),
                Released("八代市", "4320200", "レベル４土砂災害危険警報", WeatherWarningLevel.Warning),
                Released("天草市", "4321500", "高潮警報", WeatherWarningLevel.Warning),
                Released("金沢市", "1720100", "雷注意報", WeatherWarningLevel.Advisory),
            ],
            isCancelled: true);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        string[] releaseTexts = program.Pages
            .SelectMany(static page => page.Blocks)
            .Where(static block => block.StyleToken == DisplayStyleTokens.WeatherCancel)
            .Select(static block => block.PrimaryText)
            .ToArray();

        CollectionAssert.AreEquivalent(ExpectedReleaseTexts, releaseTexts);
    }

    [TestMethod]
    public void SameReleasedWarningAcrossMunicipalitiesGroupsAreas()
    {
        WeatherWarningEvent weather = CreateWeather(
            [
                Released("A市", "1310100", "雷注意報", WeatherWarningLevel.Advisory),
                Released("B市", "1310200", "雷注意報", WeatherWarningLevel.Advisory),
                Released("C市", "1310300", "雷注意報", WeatherWarningLevel.Advisory),
                Released("D市", "1310400", "雷注意報", WeatherWarningLevel.Advisory),
                Released("E市", "1310500", "雷注意報", WeatherWarningLevel.Advisory),
                Released("F市", "1310600", "雷注意報", WeatherWarningLevel.Advisory),
                Released("G市", "1310700", "雷注意報", WeatherWarningLevel.Advisory),
                Released("H市", "1310800", "雷注意報", WeatherWarningLevel.Advisory),
            ],
            isCancelled: true);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        DisplayBlock[] releaseBlocks = program.Pages
            .SelectMany(static page => page.Blocks)
            .Where(static block => block.StyleToken == DisplayStyleTokens.WeatherCancel)
            .ToArray();

        Assert.HasCount(1, program.Pages);
        Assert.HasCount(2, releaseBlocks);
        Assert.AreEqual(
            "東京都A市、B市、C市、D市、E市、F市の雷注意報は解除されました",
            releaseBlocks[0].PrimaryText);
        Assert.AreEqual(
            "東京都G市、H市の雷注意報は解除されました",
            releaseBlocks[1].PrimaryText);
    }

    [TestMethod]
    public void LargeReleasedWarningAreaListUsesGroupedPages()
    {
        WeatherWarningItem[] items = Enumerable.Range(1, 40)
            .Select(index => Released(
                $"地域{index:00}",
                $"29{index:00000}",
                "雷注意報",
                WeatherWarningLevel.Advisory))
            .ToArray();
        WeatherWarningEvent weather = CreateWeather(items, isCancelled: true);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        DisplayBlock[] releaseBlocks = program.Pages
            .SelectMany(static page => page.Blocks)
            .Where(static block => block.StyleToken == DisplayStyleTokens.WeatherCancel)
            .ToArray();

        Assert.HasCount(4, program.Pages);
        Assert.HasCount(7, releaseBlocks);
        Assert.IsTrue(program.Pages.All(static page =>
            page.Blocks.Count(static block =>
                block.StyleToken == DisplayStyleTokens.WeatherCancel) <= 2));
        Assert.AreEqual(
            "奈良県地域01、地域02、地域03、地域04、地域05、地域06の雷注意報は解除されました",
            releaseBlocks[0].PrimaryText);
        Assert.AreEqual(
            "奈良県地域37、地域38、地域39、地域40の雷注意報は解除されました",
            releaseBlocks[^1].PrimaryText);
    }

    [TestMethod]
    public void TelegramCancellationIsNotDisplayedAsWarningRelease()
    {
        WeatherWarningEvent weather = CreateWeather(
            [],
            isCancelled: true,
            informationType: "取消");

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);

        Assert.HasCount(1, program.Pages);
        Assert.AreEqual("取消", program.Pages[0].Blocks[0].Badge);
        Assert.AreEqual(
            "先ほどの、気象警報・注意報を取り消します",
            program.Pages[0].Blocks[0].PrimaryText);
        Assert.DoesNotContain("解除", program.Pages[0].AccessibleText);
    }

    [TestMethod]
    public void PartialReleaseIsShownAlongsideActiveWarnings()
    {
        WeatherWarningEvent weather = CreateWeather(
            [
                new WeatherWarningItem(
                    "札幌市",
                    "0110000",
                    "暴風警報",
                    "05",
                    WeatherWarningLevel.Warning,
                    "継続",
                    IsActive: true),
                Released("熊本市", "4310000", "大雨警報", WeatherWarningLevel.Warning),
            ],
            isCancelled: false);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        DisplayBlock[] blocks = program.Pages.SelectMany(static page => page.Blocks).ToArray();

        Assert.IsTrue(blocks.Any(static block =>
            block.Badge == "暴風警報" &&
            block.PrimaryText == "北海道　札幌市　継続中" &&
            block.SecondaryText == string.Empty));
        Assert.IsTrue(blocks.Any(static block =>
            block.PrimaryText == "熊本県熊本市の大雨警報は解除されました"));
    }

    [TestMethod]
    public void ActiveWarningKeepsAreaAndStatusOnOneLine()
    {
        WeatherWarningEvent weather = CreateWeather(
            [
                new WeatherWarningItem(
                    "新温泉町",
                    "2858600",
                    "雷注意報",
                    "14",
                    WeatherWarningLevel.Advisory,
                    "継続",
                    IsActive: true),
            ],
            isCancelled: false);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        DisplayBlock block = program.Pages.Single().Blocks.Single(static item =>
            item.StyleToken == DisplayStyleTokens.WeatherAdvisory);

        Assert.AreEqual("兵庫県　新温泉町　継続中", block.PrimaryText);
        Assert.AreEqual(string.Empty, block.SecondaryText);
    }

    [TestMethod]
    public void DifferentWarningsForSameAreaRemainSeparate()
    {
        WeatherWarningEvent weather = CreateWeather(
            [
                Active("御船町", "4344100", "大雨警報", "継続"),
                Active("御船町", "4344100", "洪水警報", "継続"),
            ],
            isCancelled: false);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        DisplayBlock[] blocks = program.Pages
            .SelectMany(static page => page.Blocks)
            .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
            .ToArray();

        Assert.HasCount(2, blocks);
        CollectionAssert.AreEquivalent(
            new List<string> { "大雨警報", "洪水警報" },
            blocks.Select(static block => block.Badge).ToArray());
        Assert.IsTrue(blocks.All(static block =>
            block.PrimaryText == "熊本県　御船町　継続中"));
        Assert.IsTrue(blocks.All(static block =>
            block.StyleToken == DisplayStyleTokens.WeatherWarning));
    }

    [TestMethod]
    public void SameAreaWarningsWithDifferentLevelsRemainSeparate()
    {
        WeatherWarningEvent weather = CreateWeather(
            [
                ActiveWithLevel("御船町", "4344100", "大雨特別警報", WeatherWarningLevel.SpecialWarning),
                ActiveWithLevel("御船町", "4344100", "洪水警報", WeatherWarningLevel.Warning),
            ],
            isCancelled: false);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        DisplayBlock[] blocks = program.Pages
            .SelectMany(static page => page.Blocks)
            .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
            .ToArray();

        Assert.HasCount(2, blocks);
        Assert.AreEqual("大雨特別警報", blocks[0].Badge);
        Assert.AreEqual("洪水警報", blocks[1].Badge);
    }

    [TestMethod]
    public void SameWarningAcrossMunicipalitiesUsesOneBadgeAndGroupedAreaRow()
    {
        WeatherWarningEvent weather = CreateWeather(
            [
                ActiveWithLevel("市原市", "1221900", "レベル５大雨特別警報", WeatherWarningLevel.SpecialWarning),
                ActiveWithLevel("市川市", "1220300", "レベル５大雨特別警報", WeatherWarningLevel.SpecialWarning),
                ActiveWithLevel("松戸市", "1220700", "レベル５大雨特別警報", WeatherWarningLevel.SpecialWarning),
                ActiveWithLevel("柏市", "1221700", "レベル５大雨特別警報", WeatherWarningLevel.SpecialWarning),
                ActiveWithLevel("白井市", "1223200", "レベル５大雨特別警報", WeatherWarningLevel.SpecialWarning),
            ],
            isCancelled: false);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        DisplayBlock[] blocks = program.Pages.Single().Blocks
            .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
            .ToArray();

        Assert.HasCount(2, blocks);
        Assert.AreEqual("レベル５大雨特別警報", blocks[0].Badge);
        Assert.AreEqual(
            "千葉県　市原市　市川市　松戸市　新たに発表",
            blocks[0].PrimaryText);
        Assert.AreEqual(string.Empty, blocks[1].Badge);
        Assert.AreEqual(
            "千葉県　柏市　白井市　新たに発表",
            blocks[1].PrimaryText);
    }

    [TestMethod]
    public void PrefectureLevelWarningDoesNotRepeatPrefectureName()
    {
        WeatherWarningEvent weather = CreateWeather(
            [
                new WeatherWarningItem(
                    "富山県",
                    "160000",
                    "大雨警報",
                    "03",
                    WeatherWarningLevel.Warning,
                    "継続",
                    IsActive: true),
            ],
            isCancelled: false);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);

        Assert.AreEqual("富山県　継続中", program.Pages.Single().Blocks.Single().PrimaryText);
    }

    [TestMethod]
    public void NewlyIssuedAndUpdatedAreasAppearBeforeContinuedAreas()
    {
        WeatherWarningEvent weather = CreateWeather(
            [
                Active("世田谷区", "1311200", "レベル3大雨警報", "継続"),
                Active("日野市", "1321200", "レベル3大雨警報", "発表"),
                Active("昭島市", "1320700", "レベル3大雨警報", "継続"),
                Active("八王子市", "1320100", "レベル3大雨警報", "更新"),
            ],
            isCancelled: false);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        string[] displayed = program.Pages
            .SelectMany(static page => page.Blocks)
            .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
            .Select(static block => block.PrimaryText)
            .ToArray();

        Assert.AreEqual(
            "東京都　日野市　新たに発表\n" +
            "東京都　八王子市　更新\n" +
            "東京都　世田谷区　昭島市　継続中",
            string.Join('\n', displayed));
    }

    [TestMethod]
    public void StatusOrderIsKeptAcrossPageBoundaries()
    {
        WeatherWarningEvent weather = CreateWeather(
            [
                Active("継続市1", "1310100", "大雨警報", "継続"),
                Active("継続市2", "1310200", "大雨警報", "継続"),
                Active("継続市3", "1310300", "大雨警報", "継続"),
                Active("継続市4", "1310600", "大雨警報", "継続"),
                Active("継続市5", "1310700", "大雨警報", "継続"),
                Active("継続市6", "1310800", "大雨警報", "継続"),
                Active("継続市7", "1310900", "大雨警報", "継続"),
                Active("継続市8", "1311000", "大雨警報", "継続"),
                Active("継続市9", "1311100", "大雨警報", "継続"),
                Active("新規市1", "1310400", "大雨警報", "発表"),
                Active("更新市1", "1310500", "大雨警報", "更新"),
            ],
            isCancelled: false);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);

        Assert.HasCount(3, program.Pages);
        Assert.IsTrue(program.Pages[0].Blocks[0].PrimaryText.EndsWith(
            "新たに発表",
            StringComparison.Ordinal));
        Assert.IsTrue(program.Pages[0].Blocks[1].PrimaryText.EndsWith(
            "更新",
            StringComparison.Ordinal));
        Assert.IsTrue(program.Pages.Skip(1)
            .SelectMany(static page => page.Blocks)
            .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
            .All(static block =>
                block.PrimaryText.EndsWith("継続中", StringComparison.Ordinal)));
        Assert.IsTrue(program.Pages.All(static page =>
            page.Blocks.Count(static block =>
                block.StyleToken != DisplayStyleTokens.PageIndicator) <= 2));
    }

    [TestMethod]
    public void WarningLevelsUseLevelFiveBlackLevelFourPurpleLevelThreeRedAndLevelTwoYellow()
    {
        WeatherWarningEvent weather = CreateWeather(
            [
                ActiveWithLevel("特別区", "1310100", "レベル５大雨特別警報", WeatherWarningLevel.SpecialWarning),
                ActiveWithLevel("危険区", "1310200", "レベル４土砂災害危険警報", WeatherWarningLevel.Warning),
                ActiveWithLevel("警報区", "1310300", "レベル３大雨警報", WeatherWarningLevel.Warning),
                ActiveWithLevel("注意区", "1310400", "レベル２大雨注意報", WeatherWarningLevel.Advisory),
            ],
            isCancelled: false);

        DisplayBlock[] blocks = new PageComposer()
            .Compose(weather, AppSettings.CreateDefault().Display)
            .Pages.SelectMany(static page => page.Blocks)
            .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
            .ToArray();

        Assert.AreEqual(DisplayStyleTokens.WeatherSpecialWarning, blocks[0].StyleToken);
        Assert.AreEqual(DisplayStyleTokens.WeatherDangerWarning, blocks[1].StyleToken);
        Assert.AreEqual(DisplayStyleTokens.WeatherWarning, blocks[2].StyleToken);
        Assert.AreEqual(DisplayStyleTokens.WeatherAdvisory, blocks[3].StyleToken);
    }

    [TestMethod]
    public void InformativeJmaHeadlinePrecedesDistrictPagesAndUsesAtMostTwoLines()
    {
        var weather = new WeatherWarningEvent(
            EventId.Create("weather-headline-display-test"),
            "nii-jma-xml",
            IssuedAt,
            IssuedAt,
            "WEATHER-HEADLINE-DISPLAY-TEST",
            SourceMode.HistoryRehearsal,
            new IssueInfo("銚子地方気象台", IssuedAt, "VPWW55", CorrectionType.None),
            "【特別警報（大雨）】北西部、山武・長生にレベル５大雨特別警報を発表しています。" +
            "低い土地の浸水や河川の増水に最大級の警戒をしてください。",
            [
                ActiveWithLevel(
                    "八街市",
                    "1223000",
                    "レベル５大雨特別警報",
                    WeatherWarningLevel.SpecialWarning),
            ],
            isCancelled: false);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        DisplayBlock firstHeadline = program.Pages[0].Blocks
            .Single(static block => block.StyleToken != DisplayStyleTokens.PageIndicator);
        DisplayBlock secondHeadline = program.Pages[1].Blocks
            .Single(static block => block.StyleToken != DisplayStyleTokens.PageIndicator);

        Assert.AreEqual("レベル５大雨特別警報", firstHeadline.Badge);
        Assert.AreEqual(
            "千葉県　北西部、山武・長生に\nレベル５大雨特別警報を発表しています",
            firstHeadline.PrimaryText);
        Assert.AreEqual("最大級の警戒", secondHeadline.Badge);
        Assert.AreEqual(
            "低い土地の浸水や河川の増水に\n最大級の警戒をしてください",
            secondHeadline.PrimaryText);
        Assert.IsTrue(program.Pages.Take(2).All(static page =>
            page.Blocks
                .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
                .All(static block => block.PrimaryText.Split('\n').Length <= 2)));
        Assert.IsTrue(program.Pages.Skip(2)
            .SelectMany(static page => page.Blocks)
            .Any(static block => block.PrimaryText.Contains("八街市", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GenericUpdateHeadlineDoesNotAddAHeadlinePage()
    {
        WeatherWarningEvent weather = CreateWeather(
            [Active("熊本市", "4310000", "大雨警報", "発表")],
            isCancelled: false);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);

        Assert.HasCount(1, program.Pages);
        Assert.AreEqual(
            "熊本県　熊本市　新たに発表",
            program.Pages.Single().Blocks
                .Single(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
                .PrimaryText);
    }

    [TestMethod]
    public void RecordShortDurationHeavyRainUsesOccurrenceRainfallAndWarningPages()
    {
        var weather = new WeatherWarningEvent(
            EventId.Create("record-rain-display-test"),
            "nii-jma-xml",
            IssuedAt,
            IssuedAt,
            "RECORD-RAIN-DISPLAY-TEST",
            SourceMode.HistoryRehearsal,
            new IssueInfo("富山地方気象台", IssuedAt, "VPOA50", CorrectionType.None),
            "１８時４０分、富山県富山市山間部西で記録的短時間大雨。" +
            "富山市八尾町丸山で１時間に１０９ミリ。" +
            "富山市山間部西付近で１時間に約１００ミリ。" +
            "猛烈な雨が降っており、災害発生の危険度が急激に高まっています。",
            [
                new WeatherWarningItem(
                    "富山県",
                    "160000",
                    "記録的短時間大雨情報",
                    string.Empty,
                    WeatherWarningLevel.Warning,
                    "発表",
                    IsActive: true),
            ],
            isCancelled: false,
            WeatherInformationType.RecordShortDurationHeavyRain);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);

        Assert.HasCount(3, program.Pages);
        Assert.AreEqual(
            "１８時４０分　富山県富山市山間部西で記録的短時間大雨",
            WeatherBlocks(program.Pages[0]).Single().PrimaryText);
        CollectionAssert.AreEqual(
            ExpectedRecordRainfallLines,
            WeatherBlocks(program.Pages[1]).Select(static block => block.PrimaryText).ToArray());
        Assert.AreEqual(
            "猛烈な雨が降っており、災害発生の危険度が急激に高まっています",
            WeatherBlocks(program.Pages[2]).Single().PrimaryText);
        Assert.IsTrue(program.Pages.All(static page =>
            WeatherBlocks(page).First().Badge == "記録的短時間大雨情報"));
    }

    [TestMethod]
    public void RecordShortDurationHeavyRainKeepsAtMostThreeRainfallLinesPerPage()
    {
        var weather = new WeatherWarningEvent(
            EventId.Create("record-rain-pagination-test"),
            "nii-jma-xml",
            IssuedAt,
            IssuedAt,
            "RECORD-RAIN-PAGINATION-TEST",
            SourceMode.HistoryRehearsal,
            new IssueInfo("気象庁", IssuedAt, "VPOA50", CorrectionType.None),
            "１２時３０分、東京都で記録的短時間大雨。" +
            "地点１で１時間に１００ミリ。地点２で１時間に１００ミリ。" +
            "地点３で１時間に１００ミリ。地点４で１時間に１００ミリ。" +
            "災害発生の危険度が急激に高まっています。",
            [
                new WeatherWarningItem(
                    "東京都",
                    "130000",
                    "記録的短時間大雨情報",
                    string.Empty,
                    WeatherWarningLevel.Warning,
                    "発表",
                    IsActive: true),
            ],
            isCancelled: false,
            WeatherInformationType.RecordShortDurationHeavyRain);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);

        Assert.HasCount(4, program.Pages);
        Assert.HasCount(3, WeatherBlocks(program.Pages[1]));
        Assert.HasCount(1, WeatherBlocks(program.Pages[2]));
    }

    [TestMethod]
    public void RecordRainWarningAlwaysStartsOnNextPageWithoutXmlSentenceDelimiter()
    {
        const string headline =
            "２０時、千葉県八街市で記録的短時間大雨 " +
            "猛烈な雨が降っており、災害発生の危険度が急激に高まっています";

        DisplayProgram recordRain = ComposeRecordRainBulletin(
            "record-rain-forced-boundary-test",
            "VPOA50",
            WeatherInformationType.RecordShortDurationHeavyRain,
            "記録的短時間大雨情報",
            headline);
        DisplayProgram disasterBulletin = ComposeRecordRainBulletin(
            "disaster-bulletin-forced-boundary-test",
            "VPBS50",
            WeatherInformationType.DisasterPreventionBulletin,
            "千葉県気象防災速報（記録的短時間大雨）",
            headline);

        AssertRecordRainWarningBoundary(recordRain, "記録的短時間大雨情報");
        AssertRecordRainWarningBoundary(disasterBulletin, "気象防災速報");
    }

    [TestMethod]
    public void DisasterPreventionBulletinUsesXmlHeadlineInsteadOfIssueStatus()
    {
        var weather = new WeatherWarningEvent(
            EventId.Create("disaster-prevention-bulletin-display-test"),
            "nii-jma-xml",
            IssuedAt,
            IssuedAt,
            "DISASTER-PREVENTION-BULLETIN-DISPLAY-TEST",
            SourceMode.HistoryRehearsal,
            new IssueInfo("秋田地方気象台", IssuedAt, "VPBS50", CorrectionType.None),
            "１３時４０分、秋田県湯沢市で記録的短時間大雨。" +
            "湯沢市付近で１時間に約１００ミリ。" +
            "猛烈な雨が降っており、災害発生の危険度が急激に高まっています。",
            [
                new WeatherWarningItem(
                    "湯沢市",
                    "0520700",
                    "秋田県気象防災速報（記録的短時間大雨）",
                    string.Empty,
                    WeatherWarningLevel.Warning,
                    "発表",
                    IsActive: true),
            ],
            isCancelled: false,
            WeatherInformationType.DisasterPreventionBulletin);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);

        Assert.HasCount(3, program.Pages);
        Assert.AreEqual(
            "１３時４０分　秋田県湯沢市で記録的短時間大雨",
            WeatherBlocks(program.Pages[0]).Single().PrimaryText);
        Assert.AreEqual(
            "湯沢市付近で１時間に約１００ミリ",
            WeatherBlocks(program.Pages[1]).Single().PrimaryText);
        Assert.AreEqual(
            "猛烈な雨が降っており、災害発生の危険度が急激に高まっています",
            WeatherBlocks(program.Pages[2]).Single().PrimaryText);
        Assert.IsTrue(program.Pages.All(static page =>
            WeatherBlocks(page).First().Badge == "気象防災速報"));
        Assert.IsFalse(program.Pages.Any(static page =>
            page.AccessibleText.Contains("新たに発表", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DisasterPreventionBulletinPlacesEachLongPointOnItsOwnPage()
    {
        var weather = new WeatherWarningEvent(
            EventId.Create("linear-rainband-pagination-test"),
            "axis",
            IssuedAt,
            IssuedAt,
            "LINEAR-RAINBAND-PAGINATION-TEST",
            SourceMode.HistoryRehearsal,
            new IssueInfo("水戸地方気象台", IssuedAt, "VPBS50", CorrectionType.None),
            "茨城県南部では、今後3時間以内に線状降水帯が発生し、非常に激しい雨が同じ場所で降り続く可能性が高まっています。" +
            "命に危険が及ぶ災害発生の危険度が急激に高まるおそれがあります。",
            [
                new WeatherWarningItem(
                    "茨城県南部",
                    "080000",
                    "茨城県気象防災速報（線状降水帯）",
                    string.Empty,
                    WeatherWarningLevel.Warning,
                    "発表",
                    IsActive: true),
            ],
            isCancelled: false,
            WeatherInformationType.DisasterPreventionBulletin);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);

        Assert.HasCount(2, program.Pages);
        Assert.IsTrue(program.Pages.All(static page =>
            WeatherBlocks(page).Length == 1));
        Assert.IsTrue(program.Pages.All(static page =>
            WeatherBlocks(page).Single().Badge == "気象防災速報"));
        StringAssert.Contains(
            WeatherBlocks(program.Pages[0]).Single().PrimaryText,
            "線状降水帯が発生");
        StringAssert.Contains(
            WeatherBlocks(program.Pages[1]).Single().PrimaryText,
            "災害発生の危険度");
    }

    [TestMethod]
    public void TornadoAdvisoryUsesHeadlinePagesAndEndsWithXmlValidTime()
    {
        var weather = new WeatherWarningEvent(
            EventId.Create("tornado-advisory-display-test"),
            "nii-jma-xml",
            IssuedAt,
            IssuedAt,
            "TORNADO-ADVISORY-DISPLAY-TEST",
            SourceMode.HistoryRehearsal,
            new IssueInfo("宇都宮地方気象台", IssuedAt, "VPHW50", CorrectionType.None),
            "栃木県南部、北部は、竜巻などの激しい突風が発生しやすい気象状況になっています。" +
            "空の様子に注意してください。" +
            "雷や急な風の変化など積乱雲が近づく兆しがある場合には、安全確保に努めてください。" +
            "落雷、ひょう、急な強い雨にも注意してください。",
            [
                new WeatherWarningItem(
                    "栃木県",
                    "090000",
                    "竜巻注意情報",
                    "1",
                    WeatherWarningLevel.Advisory,
                    "発表",
                    IsActive: true),
            ],
            isCancelled: false,
            WeatherInformationType.TornadoAdvisory,
            new DateTimeOffset(2026, 8, 9, 18, 10, 0, TimeSpan.FromHours(9)));

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);

        Assert.HasCount(5, program.Pages);
        Assert.IsTrue(program.Pages.Take(4).All(static page =>
            WeatherAdvisoryBlocks(page).Length == 1));
        Assert.IsTrue(program.Pages.All(static page =>
            WeatherAdvisoryBlocks(page).Single().Badge == "栃木県　竜巻注意情報"));
        Assert.AreEqual(
            "栃木県南部、北部は、竜巻などの激しい突風が発生しやすい気象状況になっています",
            WeatherAdvisoryBlocks(program.Pages[0]).Single().PrimaryText);
        Assert.AreEqual(
            "空の様子に注意してください",
            WeatherAdvisoryBlocks(program.Pages[1]).Single().PrimaryText);
        Assert.AreEqual(
            "雷や急な風の変化など積乱雲が近づく兆しがある場合には、安全確保に努めてください",
            WeatherAdvisoryBlocks(program.Pages[2]).Single().PrimaryText);
        Assert.AreEqual(
            "落雷、ひょう、急な強い雨にも注意してください",
            WeatherAdvisoryBlocks(program.Pages[3]).Single().PrimaryText);
        DisplayBlock validTime = WeatherAdvisoryBlocks(program.Pages[4]).Single();
        Assert.AreEqual("栃木県　竜巻注意情報", validTime.Badge);
        Assert.AreEqual("この情報は9日 18時10分まで有効です", validTime.PrimaryText);
    }

    private static DisplayBlock[] WeatherBlocks(DisplayPage page) => page.Blocks
        .Where(static block => block.StyleToken == DisplayStyleTokens.WeatherWarning)
        .ToArray();

    private static DisplayBlock[] WeatherAdvisoryBlocks(DisplayPage page) => page.Blocks
        .Where(static block => block.StyleToken == DisplayStyleTokens.WeatherAdvisory)
        .ToArray();

    private static DisplayProgram ComposeRecordRainBulletin(
        string eventId,
        string rawType,
        WeatherInformationType informationType,
        string kindName,
        string headline)
    {
        var weather = new WeatherWarningEvent(
            EventId.Create(eventId),
            "axis",
            IssuedAt,
            IssuedAt,
            eventId.ToUpperInvariant(),
            SourceMode.HistoryRehearsal,
            new IssueInfo("銚子地方気象台", IssuedAt, rawType, CorrectionType.None),
            headline,
            [
                new WeatherWarningItem(
                    "八街市",
                    "1223000",
                    kindName,
                    string.Empty,
                    WeatherWarningLevel.Warning,
                    "発表",
                    IsActive: true),
            ],
            isCancelled: false,
            informationType);

        return new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
    }

    private static void AssertRecordRainWarningBoundary(
        DisplayProgram program,
        string expectedBadge)
    {
        Assert.HasCount(2, program.Pages);
        DisplayBlock occurrence = WeatherBlocks(program.Pages[0]).Single();
        DisplayBlock warning = WeatherBlocks(program.Pages[1]).Single();
        Assert.AreEqual(expectedBadge, occurrence.Badge);
        Assert.AreEqual(expectedBadge, warning.Badge);
        StringAssert.Contains(occurrence.PrimaryText, "八街市で記録的短時間大雨");
        Assert.DoesNotContain("猛烈な雨", occurrence.PrimaryText);
        StringAssert.StartsWith(warning.PrimaryText, "猛烈な雨が");
        Assert.DoesNotContain("記録的短時間大雨", warning.PrimaryText);
    }

    private static WeatherWarningEvent CreateWeather(
        IReadOnlyList<WeatherWarningItem> items,
        bool isCancelled,
        string informationType = "") => new(
            EventId.Create("weather-release-display-test"),
            "nii-jma-xml",
            IssuedAt,
            IssuedAt,
            "WEATHER-RELEASE-DISPLAY-TEST",
            SourceMode.HistoryRehearsal,
            new IssueInfo(
                "気象庁",
                IssuedAt,
                "VPWW55",
                CorrectionType.None,
                InformationType: informationType),
            "気象警報・注意報を更新しました。",
            items,
            isCancelled);

    private static WeatherWarningItem Released(
        string areaName,
        string areaCode,
        string kindName,
        WeatherWarningLevel level) => new(
            areaName,
            areaCode,
            kindName,
            string.Empty,
            level,
            "解除",
            IsActive: false);

    private static WeatherWarningItem Active(
        string areaName,
        string areaCode,
        string kindName,
        string status) => new(
            areaName,
            areaCode,
            kindName,
            "03",
            WeatherWarningLevel.Warning,
            status,
            IsActive: true);

    private static WeatherWarningItem ActiveWithLevel(
        string areaName,
        string areaCode,
        string kindName,
        WeatherWarningLevel level) => new(
            areaName,
            areaCode,
            kindName,
            string.Empty,
            level,
            "発表",
            IsActive: true);
}
