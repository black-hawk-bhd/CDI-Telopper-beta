using EEWTelop.Application.Display;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class Phase6ApplicationTests
{
    private static readonly string[] WeatherTrainingScenarioIds =
    [
        "weather-special-warning",
        "weather-warning",
        "weather-advisory",
        "weather-level5",
        "weather-level4",
        "weather-level3",
        "weather-level2",
        "weather-warning-cancel",
    ];

    private static readonly string[] EewTrainingScenarioIds =
    [
        "eew-warning",
        "eew-cancel",
        "eew-expanding-15",
        "eew-concurrent-two",
        "eew-concurrent-one-cancel",
        "eew-concurrent-two-cancel",
    ];

    private static readonly string[] EewTrainingHeaders =
    [
        "緊急地震速報（気象庁）",
        "緊急地震速報（取消）",
        "緊急地震速報（気象庁）",
        "緊急地震速報（気象庁）",
        "緊急地震速報（気象庁）",
        "緊急地震速報（気象庁）",
    ];

    [TestMethod]
    public async Task UiLogBufferNeverRetainsMoreThanTwoHundredFiftyEntries()
    {
        var buffer = new UiLogBuffer();
        DateTimeOffset start = DateTimeOffset.UnixEpoch;
        for (int index = 0; index < 300; index++)
        {
            await buffer.WriteAsync(new AppLogEntry(
                start.AddSeconds(index),
                AppLogLevel.Information,
                $"event-{index}",
                $"message-{index}"));
        }

        IReadOnlyList<AppLogEntry> snapshot = buffer.GetSnapshot();

        Assert.HasCount(UiLogBuffer.MaximumCapacity, snapshot);
        Assert.AreEqual("event-50", snapshot[0].EventName);
        Assert.AreEqual("event-299", snapshot[^1].EventName);
    }

    [TestMethod]
    public void EveryPhaseSixScenarioComposesAsIdentifiedTrainingContent()
    {
        IReadOnlyList<TestScenario> scenarios = TestScenarioCatalog.Create(
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
        var composer = new PageComposer();

        DisplayProgram[] programs = scenarios
            .Select(scenario => composer.Compose(scenario.Event, DisplayEventFactory.Settings))
            .ToArray();

        Assert.HasCount(33, scenarios);
        Assert.IsTrue(scenarios.Any(static scenario =>
            scenario.Id == "weather-special-warning"));
        Assert.IsTrue(scenarios.Any(static scenario =>
            scenario.Id == "weather-warning"));
        Assert.IsTrue(scenarios.Any(static scenario =>
            scenario.Id == "weather-advisory"));
        Assert.IsTrue(scenarios.Any(static scenario =>
            scenario.Id == "weather-level5"));
        Assert.IsTrue(scenarios.Any(static scenario =>
            scenario.Id == "weather-level4"));
        Assert.IsTrue(scenarios.Any(static scenario =>
            scenario.Id == "weather-level3"));
        Assert.IsTrue(scenarios.Any(static scenario =>
            scenario.Id == "weather-level2"));
        Assert.IsTrue(scenarios.Any(static scenario =>
            scenario.Id == "weather-warning-cancel"));
        Assert.IsTrue(programs.All(program => program.Pages.Count > 0));
        Assert.IsTrue(programs.All(program => program.SourceMode == SourceMode.ManualTest));
        Assert.IsTrue(programs.All(program => !string.IsNullOrWhiteSpace(program.RehearsalLabel)));
        Assert.IsTrue(programs
            .Where(program => program.EventId.Value.StartsWith("test-large", StringComparison.Ordinal))
            .All(program => program.Pages.Count > 10));
    }

    [TestMethod]
    public void WeatherTrainingScenariosSeparateConventionalAndLevelBasedWarnings()
    {
        TestScenario[] scenarios = TestScenarioCatalog.Create(
                new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.FromHours(9)))
            .Where(static scenario => scenario.Id.StartsWith("weather-", StringComparison.Ordinal))
            .ToArray();

        CollectionAssert.AreEqual(
            WeatherTrainingScenarioIds,
            scenarios.Select(static scenario => scenario.Id).ToArray());

        (string Id, string RawType, string KindName, WeatherWarningLevel Level)[] expected =
        [
            ("weather-special-warning", "VPWW58", "暴風特別警報", WeatherWarningLevel.SpecialWarning),
            ("weather-warning", "VPWW59", "波浪警報", WeatherWarningLevel.Warning),
            ("weather-advisory", "VPWW61", "雷注意報", WeatherWarningLevel.Advisory),
            ("weather-level5", "VPWW55", "レベル５大雨特別警報", WeatherWarningLevel.SpecialWarning),
            ("weather-level4", "VPWW56", "レベル４土砂災害危険警報", WeatherWarningLevel.Warning),
            ("weather-level3", "VPWW57", "レベル３高潮警報", WeatherWarningLevel.Warning),
            ("weather-level2", "VPWW55", "レベル２大雨注意報", WeatherWarningLevel.Advisory),
        ];

        foreach ((string id, string rawType, string kindName, WeatherWarningLevel level) in expected)
        {
            WeatherWarningEvent weather = Assert.IsInstanceOfType<WeatherWarningEvent>(
                scenarios.Single(scenario => scenario.Id == id).Event);
            Assert.AreEqual(rawType, weather.Issue.RawType);
            Assert.AreEqual(kindName, weather.Items.Single().KindName);
            Assert.AreEqual(level, weather.MaximumLevel);
            Assert.IsFalse(weather.IsCancelled);
        }
    }

    [TestMethod]
    public void EewTrainingStagesAreIndividuallySelectableAndUseReferenceHeaders()
    {
        IReadOnlyList<TestScenario> scenarios = TestScenarioCatalog.Create(
            new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero));
        TestScenario[] eewScenarios = scenarios
            .Where(static scenario => scenario.Id.StartsWith("eew-", StringComparison.Ordinal))
            .ToArray();
        CollectionAssert.AreEqual(
            EewTrainingScenarioIds,
            eewScenarios.Select(static scenario => scenario.Id).ToArray());

        var composer = new PageComposer();
        string[] headers = eewScenarios
            .Select(scenario => composer.Compose(scenario.Event, DisplayEventFactory.Settings)
                .Pages[0].Blocks[0].PrimaryText)
            .ToArray();
        CollectionAssert.AreEqual(
            EewTrainingHeaders,
            headers);

        EewEvent warning = Assert.IsInstanceOfType<EewEvent>(eewScenarios[0].Event);
        EewEvent cancel = Assert.IsInstanceOfType<EewEvent>(eewScenarios[1].Event);
        Assert.IsTrue(warning.IsWarning);
        Assert.IsFalse(warning.IsFinal);
        Assert.IsTrue(cancel.IsCancelled);
    }

    [TestMethod]
    public void ConcurrentEewTrainingTimelinesCoverWarningAndCancellationCombinations()
    {
        IReadOnlyList<TestScenario> scenarios = TestScenarioCatalog.Create(
            new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(9)));
        TestScenario twoWarnings = scenarios.Single(static scenario =>
            scenario.Id == "eew-concurrent-two");
        TestScenario oneCancellation = scenarios.Single(static scenario =>
            scenario.Id == "eew-concurrent-one-cancel");
        TestScenario twoCancellations = scenarios.Single(static scenario =>
            scenario.Id == "eew-concurrent-two-cancel");

        Assert.HasCount(2, twoWarnings.Steps);
        Assert.HasCount(3, oneCancellation.Steps);
        Assert.HasCount(4, twoCancellations.Steps);
        Assert.AreEqual(TimeSpan.Zero, twoWarnings.Steps[0].DelayAfterPrevious);
        Assert.AreEqual(TimeSpan.FromSeconds(5), twoWarnings.Steps[1].DelayAfterPrevious);
        Assert.IsTrue(twoWarnings.Steps.All(static step =>
            step.Event is EewEvent { IsWarning: true, IsCancelled: false }));
        Assert.AreEqual(1, oneCancellation.Steps.Count(static step => step.Event.IsCancelled));
        Assert.AreEqual(2, twoCancellations.Steps.Count(static step => step.Event.IsCancelled));

        string firstId = twoCancellations.Steps[0].Event.Id.Value;
        string secondId = twoCancellations.Steps[1].Event.Id.Value;
        Assert.AreEqual(firstId, twoCancellations.Steps[2].Event.Id.Value);
        Assert.AreEqual(secondId, twoCancellations.Steps[3].Event.Id.Value);
    }

    [TestMethod]
    public void ExpandingEewTrainingAdvancesFromReportOneThroughFifteen()
    {
        TestScenario scenario = TestScenarioCatalog.Create(
                new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(9)))
            .Single(static item => item.Id == "eew-expanding-15");

        Assert.HasCount(15, scenario.Steps);
        string eventId = scenario.Steps[0].Event.Id.Value;
        DateTimeOffset originTime = Assert.IsInstanceOfType<EewEvent>(scenario.Steps[0].Event)
            .Earthquake!
            .OriginTime;
        for (int index = 0; index < scenario.Steps.Count; index++)
        {
            TestScenarioStep step = scenario.Steps[index];
            EewEvent report = Assert.IsInstanceOfType<EewEvent>(step.Event);
            int reportNumber = index + 1;

            Assert.AreEqual(eventId, report.Id.Value);
            Assert.AreEqual(
                reportNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                report.Issue.Serial);
            Assert.HasCount(reportNumber, report.Areas);
            Assert.AreEqual(
                reportNumber == 1 ? TimeSpan.Zero : TimeSpan.FromSeconds(2),
                step.DelayAfterPrevious);
            Assert.IsTrue(report.IsWarning);
            Assert.IsFalse(report.IsFinal);
            Assert.IsFalse(report.IsCancelled);

            DateTimeOffset currentOriginTime = report.Earthquake!.OriginTime;
            Assert.AreEqual(originTime, currentOriginTime);
        }
    }

    [TestMethod]
    public void ExpandingEewTrainingExercisesRegionalAggregationBoundaries()
    {
        TestScenario scenario = TestScenarioCatalog.Create(
                new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(9)))
            .Single(static item => item.Id == "eew-expanding-15");
        var composer = new PageComposer();

        string[] areaTexts = scenario.Steps
            .Select(step => composer.Compose(step.Event, DisplayEventFactory.Settings))
            .Select(static program => program.Pages[0].Blocks
                .Single(block => block.StyleToken == DisplayStyleTokens.EewAreas)
                .PrimaryText)
            .ToArray();

        Assert.AreEqual("千葉　東京　神奈川　埼玉　山梨　長野　静岡", areaTexts[6]);
        Assert.AreEqual("関東　甲信　静岡　岐阜", areaTexts[7]);
        Assert.AreEqual("関東　甲信　東海", areaTexts[8]);
        Assert.AreEqual("関東　甲信　東海　福島", areaTexts[9]);
        Assert.AreEqual("関東　甲信　東海　東北", areaTexts[10]);
        Assert.AreEqual("関東　甲信　東海　東北", areaTexts[14]);
    }

    [TestMethod]
    public void CorrectionEventOmitsUnrelatedTsunamiCheckingPage()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            [DisplayEventFactory.Point(1, JmaScale.Four)],
            DomesticTsunami.Unknown,
            correction: CorrectionType.ScaleAndDestination);
        var composer = new PageComposer();

        DisplayProgram program = composer.Compose(quake, DisplayEventFactory.Settings);

        Assert.AreEqual(DomesticTsunami.Unknown, quake.Earthquake.DomesticTsunami);
        Assert.IsTrue(program.Pages.SelectMany(static page => page.Blocks)
            .Any(static block => block.StyleToken == DisplayStyleTokens.Correction));
        Assert.IsFalse(program.Pages.Any(static page =>
            page.AccessibleText.Contains("津波の有無を調査中", StringComparison.Ordinal)));
        Assert.IsFalse(program.Pages.Any(static page =>
            page.AccessibleText.Contains("念のため津波に注意", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DetailedQuakeTrainingScenariosShowNoTsunamiConcern()
    {
        IReadOnlyList<TestScenario> scenarios = TestScenarioCatalog.Create(
            new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero));
        var composer = new PageComposer();
        string[] scenarioIds =
        [
            "detail-scale",
            "large",
            "large-6-upper",
            "large-6-lower",
            "large-5-upper",
            "large-5-lower",
            "large-4",
            "large-3",
        ];

        foreach (string scenarioId in scenarioIds)
        {
            TestScenario scenario = scenarios.Single(item => item.Id == scenarioId);
            QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(scenario.Event);
            DisplayProgram program = composer.Compose(quake, DisplayEventFactory.Settings);

            Assert.AreEqual(DomesticTsunami.None, quake.Earthquake.DomesticTsunami);
            Assert.IsTrue(program.Pages.Any(static page =>
                page.AccessibleText.Contains(
                    "この地震による津波の心配はありません",
                    StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public void LargeLocationTrainingCoversMaximumIntensitySevenThroughThree()
    {
        IReadOnlyList<TestScenario> scenarios = TestScenarioCatalog.Create(
            new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero));
        var expected = new Dictionary<string, JmaScale>(StringComparer.Ordinal)
        {
            ["large"] = JmaScale.Seven,
            ["large-6-upper"] = JmaScale.SixUpper,
            ["large-6-lower"] = JmaScale.SixLower,
            ["large-5-upper"] = JmaScale.FiveUpper,
            ["large-5-lower"] = JmaScale.FiveLower,
            ["large-4"] = JmaScale.Four,
            ["large-3"] = JmaScale.Three,
        };
        var composer = new PageComposer();

        foreach ((string scenarioId, JmaScale maximumScale) in expected)
        {
            QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(
                scenarios.Single(item => item.Id == scenarioId).Event);
            DisplayProgram program = composer.Compose(quake, DisplayEventFactory.Settings);

            Assert.HasCount(120, quake.Points);
            Assert.AreEqual(maximumScale, quake.Earthquake.MaximumScale);
            Assert.AreEqual(maximumScale, quake.Points.Max(static point => point.Scale));
            Assert.IsTrue(program.Pages.Count > 10);
        }
    }

    [TestMethod]
    public void TsunamiWarningQuakeTrainingShowsAnnouncementSubtitle()
    {
        TestScenario scenario = TestScenarioCatalog.Create(
                new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero))
            .Single(item => item.Id == "tsunami-warning-quake");
        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(scenario.Event);
        var composer = new PageComposer();

        DisplayProgram program = composer.Compose(quake, DisplayEventFactory.Settings);

        Assert.AreEqual(DomesticTsunami.Warning, quake.Earthquake.DomesticTsunami);
        Assert.IsTrue(program.Pages.Any(static page =>
            page.AccessibleText.Contains(
                "この地震により津波情報を発表しています",
                StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TsunamiTrainingCoversEveryAlertGradeWithRealForecastAreas()
    {
        IReadOnlyList<TestScenario> scenarios = TestScenarioCatalog.Create(
            new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero));
        var expected = new Dictionary<string, (TsunamiGrade Grade, string Badge)>(StringComparer.Ordinal)
        {
            ["tsunami-major-warning"] = (TsunamiGrade.MajorWarning, "大津波警報"),
            ["tsunami-warning"] = (TsunamiGrade.Warning, "津波警報"),
            ["tsunami-watch"] = (TsunamiGrade.Watch, "津波注意報"),
        };
        var composer = new PageComposer();

        foreach ((string scenarioId, (TsunamiGrade grade, string badge)) in expected)
        {
            TestScenario scenario = scenarios.Single(item => item.Id == scenarioId);
            TsunamiEvent tsunami = Assert.IsInstanceOfType<TsunamiEvent>(scenario.Event);
            DisplayProgram program = composer.Compose(tsunami, DisplayEventFactory.Settings);

            Assert.HasCount(13, tsunami.Areas);
            Assert.IsTrue(tsunami.Areas.All(area => area.Grade == grade));
            Assert.IsTrue(program.Pages.SelectMany(static page => page.Blocks)
                .Any(block => block.Badge == badge));
            Assert.IsTrue(tsunami.Areas.All(static area =>
                !string.IsNullOrWhiteSpace(area.Name)));
        }

        TsunamiEvent combined = Assert.IsInstanceOfType<TsunamiEvent>(
            scenarios.Single(item => item.Id == "tsunami-13").Event);
        TsunamiGrade[] combinedGrades = combined.Areas
            .Select(static area => area.Grade)
            .Distinct()
            .ToArray();
        CollectionAssert.Contains(combinedGrades, TsunamiGrade.MajorWarning);
        CollectionAssert.Contains(combinedGrades, TsunamiGrade.Warning);
        CollectionAssert.Contains(combinedGrades, TsunamiGrade.Watch);
    }

    [TestMethod]
    public void OffshoreTsunamiObservationTrainingMatchesVtse52StationDisplayData()
    {
        TestScenario scenario = TestScenarioCatalog.Create(
                new DateTimeOffset(2026, 8, 11, 1, 10, 0, TimeSpan.FromHours(9)))
            .Single(static item => item.Id == "tsunami-offshore-observation");
        TsunamiEvent tsunami = Assert.IsInstanceOfType<TsunamiEvent>(scenario.Event);

        Assert.AreEqual("nii-jma-xml", tsunami.Provider);
        Assert.AreEqual("VTSE52", tsunami.Issue.RawType);
        Assert.AreEqual(SourceMode.ManualTest, tsunami.SourceMode);
        Assert.IsFalse(tsunami.IsCancelled);
        Assert.HasCount(8, tsunami.Areas);
        Assert.IsTrue(tsunami.Areas.All(static area => area.Grade == TsunamiGrade.Unknown));
        Assert.AreEqual("静岡御前崎沖", tsunami.Areas[0].Name);
        Assert.AreEqual("押し", tsunami.Areas[0].FirstHeight?.Condition);
        Assert.AreEqual("１．８ｍ", tsunami.Areas[0].MaximumHeight?.Description);
        Assert.AreEqual(1.8, tsunami.Areas[0].MaximumHeight?.ValueMeters);
        Assert.AreEqual("高知足摺岬沖", tsunami.Areas[^1].Name);
        Assert.AreEqual(1.7, tsunami.Areas[^1].MaximumHeight?.ValueMeters);

        DisplayProgram program = new PageComposer().Compose(
            tsunami,
            DisplayEventFactory.Settings);
        Assert.HasCount(3, program.Pages);
        Assert.IsTrue(program.Pages.SelectMany(static page => page.Blocks)
            .Any(static block => block.Badge == "津波観測情報"));
        string renderedText = string.Join(
            '\n',
            program.Pages.Select(static page => page.AccessibleText));
        StringAssert.Contains(renderedText, "静岡御前崎沖");
        StringAssert.Contains(renderedText, "高知足摺岬沖");
        StringAssert.Contains(renderedText, "01時08分 押し １．８ｍ");
        StringAssert.Contains(renderedText, "２．０ｍ");
    }

    [TestMethod]
    public void EveryTrainingScenarioUsesRealLocationsAndExercisesTheirRendering()
    {
        IReadOnlyList<TestScenario> scenarios = TestScenarioCatalog.Create(
            new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero));
        var composer = new PageComposer();
        DisplaySettings settings = DisplayEventFactory.Settings with
        {
            ShowTsunamiForecast = true,
        };

        foreach (TestScenario scenario in scenarios)
        {
            DisplayProgram program = composer.Compose(scenario.Event, settings);
            string renderedText = string.Join(
                '\n',
                program.Pages.Select(static page => page.AccessibleText));

            switch (scenario.Event)
            {
                case EewEvent eew:
                    EarthquakeInfo? earthquake = eew.Earthquake;
                    Assert.IsNotNull(earthquake);
                    HypocenterInfo? hypocenter = earthquake.Hypocenter;
                    Assert.IsNotNull(hypocenter);
                    Assert.IsFalse(IsPlaceholderLocation(hypocenter.Name));
                    Assert.IsTrue(eew.Areas.All(static area =>
                        !IsPlaceholderLocation(area.Prefecture) &&
                        !IsPlaceholderLocation(area.Name)));
                    if (eew.IsCancelled)
                    {
                        Assert.IsFalse(renderedText.Contains(hypocenter.Name, StringComparison.Ordinal));
                    }
                    else
                    {
                        StringAssert.Contains(renderedText, hypocenter.Name);
                        foreach (string areaLabel in EewAreaLabelFormatter.Format(eew.Areas))
                        {
                            StringAssert.Contains(renderedText, areaLabel);
                        }
                    }

                    break;

                case QuakeEvent quake when quake.IssueType == QuakeIssueType.Foreign:
                    Assert.AreEqual("チリ中部沿岸", quake.Earthquake.Hypocenter?.Name);
                    StringAssert.Contains(renderedText, "チリ中部沿岸");
                    break;

                case QuakeEvent quake:
                    Assert.IsNotEmpty(quake.Points);
                    Assert.HasCount(
                        quake.Points.Count,
                        quake.Points.Select(static point => point.DisplayName)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray());
                    Assert.IsTrue(quake.Points.All(static point =>
                        !IsPlaceholderLocation(point.Prefecture) &&
                        !IsPlaceholderLocation(point.Address) &&
                        !IsPlaceholderLocation(point.DisplayName)));
                    foreach (QuakePoint point in quake.Points)
                    {
                        StringAssert.Contains(renderedText, point.DisplayName);
                    }

                    break;

                case TsunamiEvent tsunami:
                    Assert.IsNotEmpty(tsunami.Areas);
                    if (scenario.Id != "tsunami-offshore-observation")
                    {
                        Assert.HasCount(13, tsunami.Areas);
                    }
                    Assert.HasCount(
                        tsunami.Areas.Count,
                        tsunami.Areas.Select(static area => area.Name)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray());
                    Assert.IsTrue(tsunami.Areas.All(static area =>
                        !IsPlaceholderLocation(area.Name)));
                    if (tsunami.IsCancelled)
                    {
                        Assert.IsFalse(tsunami.Areas.Any(area =>
                            renderedText.Contains(area.Name, StringComparison.Ordinal)));
                    }
                    else
                    {
                        foreach (TsunamiArea area in tsunami.Areas)
                        {
                            StringAssert.Contains(renderedText, area.Name);
                        }
                    }

                    break;
            }
        }
    }

    private static bool IsPlaceholderLocation(string value) =>
        value.Contains("都道府県", StringComparison.Ordinal) ||
        value.Contains("自治体", StringComparison.Ordinal) ||
        value.StartsWith("予報区", StringComparison.Ordinal);
}
