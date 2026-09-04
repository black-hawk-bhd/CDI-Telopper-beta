using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class EventIngestionPipelineTests
{
    [TestMethod]
    public void LegacyAxisWeatherTelegramIsIgnoredBeforeVersioningAndDisplay()
    {
        DateTimeOffset issuedAt = new(2026, 8, 12, 8, 4, 9, TimeSpan.Zero);
        var legacy = new WeatherWarningEvent(
            EventId.Create("weather-VPWW54-test"),
            "axis",
            issuedAt,
            issuedAt,
            "legacy-signature",
            SourceMode.Production,
            new IssueInfo("JMA", issuedAt, "VPWW54", CorrectionType.None),
            "legacy warning",
            [WeatherItem("Test City", "0000000", "legacy warning", WeatherWarningLevel.Warning)],
            isCancelled: false);
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, DisplayEventFactory.Settings);
        var pipeline = new EventIngestionPipeline(
            new StubNormalizer(legacy),
            new EventVersionCache(),
            new PageComposer(),
            coordinator,
            DisplayEventFactory.Settings);
        var raw = new RawProviderMessage(
            "axis",
            "<Report />",
            SourceMode.Production,
            clock.UtcNow)
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        };

        EventIngestionResult result = pipeline.Process(raw);

        Assert.AreEqual(EventIngestionStatus.Ignored, result.Status);
        Assert.IsNull(result.Program);
        Assert.IsNull(result.Snapshot);
        Assert.IsNull(coordinator.Evaluate().CurrentProgram);
        Assert.AreEqual("AXIS旧形式気象電文", result.DisplaySuppressionReason);
    }

    [TestMethod]
    public void NormalizedEventIsComposedOnceAndDuplicateIsSuppressed()
    {
        QuakeEvent disasterEvent = DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale);
        var clock = new FakeClock();
        var pipeline = new EventIngestionPipeline(
            new StubNormalizer(disasterEvent),
            new EventVersionCache(),
            new PageComposer(),
            new PriorityCoordinator(clock, DisplayEventFactory.Settings),
            DisplayEventFactory.Settings);
        var raw = new RawProviderMessage(
            "p2pquake",
            "{}",
            SourceMode.Production,
            clock.UtcNow);

        EventIngestionResult accepted = pipeline.Process(raw);
        EventIngestionResult duplicate = pipeline.Process(raw);

        Assert.AreEqual(EventIngestionStatus.Accepted, accepted.Status);
        Assert.IsNotNull(accepted.Program);
        Assert.IsNotNull(accepted.Snapshot);
        Assert.IsNotNull(accepted.ReceptionSummary);
        Assert.AreEqual(raw.ReceivedAt, accepted.ReceptionSummary.ReceivedAt);
        Assert.AreEqual(disasterEvent.ProviderCode, accepted.ReceptionSummary.ProviderCode);
        Assert.AreEqual(disasterEvent.Id.Value, accepted.ReceptionSummary.EventId);
        Assert.AreEqual("地震情報", accepted.ReceptionSummary.EventType);
        Assert.AreEqual("採用・表示", accepted.ReceptionSummary.ProcessingResult);
        Assert.AreEqual(EventIngestionStatus.Duplicate, duplicate.Status);
        Assert.IsNull(duplicate.Program);
        Assert.AreEqual("重複", duplicate.ReceptionSummary?.ProcessingResult);
    }

    [TestMethod]
    public void PreDisplayEditingHoldsComposedProgramOutsideCoordinator()
    {
        QuakeEvent disasterEvent = DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale);
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, DisplayEventFactory.Settings);
        var pipeline = new EventIngestionPipeline(
            new StubNormalizer(disasterEvent),
            new EventVersionCache(),
            new PageComposer(),
            coordinator,
            DisplayEventFactory.Settings)
        {
            HoldBeforeDisplay = true,
        };

        EventIngestionResult result = pipeline.Process(CreateRaw(clock, "pre-display"));

        Assert.AreEqual(EventIngestionStatus.Accepted, result.Status);
        Assert.IsTrue(result.AwaitingPreDisplayEdit);
        Assert.IsNotNull(result.Program);
        Assert.IsNull(result.Snapshot);
        Assert.IsNull(coordinator.Evaluate().CurrentProgram);
    }

    [TestMethod]
    public void IgnoredMessageRecordsOnlySafeEnvelopeFields()
    {
        var clock = new FakeClock();
        var pipeline = new EventIngestionPipeline(
            new IgnoringNormalizer(),
            new EventVersionCache(),
            new PageComposer(),
            new PriorityCoordinator(clock, DisplayEventFactory.Settings),
            DisplayEventFactory.Settings);
        const string confidentialPayload = "本文はログへ出さない";
        var raw = new RawProviderMessage(
            "p2pquake",
            $$"""
              {
                "code": 554,
                "id": "event\n554",
                "issue": { "serial": "12" },
                "body": "{{confidentialPayload}}"
              }
              """,
            SourceMode.Sandbox,
            clock.UtcNow);

        EventIngestionResult result = pipeline.Process(raw);

        Assert.AreEqual(EventIngestionStatus.Ignored, result.Status);
        Assert.IsNotNull(result.ReceptionSummary);
        Assert.AreEqual(554, result.ReceptionSummary.ProviderCode);
        Assert.AreEqual("event 554", result.ReceptionSummary.EventId);
        Assert.AreEqual("不明", result.ReceptionSummary.EventType);
        Assert.AreEqual("対象外", result.ReceptionSummary.ProcessingResult);
        Assert.AreEqual("12", result.ReceptionSummary.ReportNumber);
        Assert.DoesNotContain(confidentialPayload, result.ReceptionSummary.ToLogMessage());
    }

    [TestMethod]
    public void MaximumIntensity2IsAcceptedButNotAddedToTheDisplayCoordinatorWhenFiltered()
    {
        QuakeEvent disasterEvent = DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            [DisplayEventFactory.Point(1, JmaScale.Two)]);
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, DisplayEventFactory.Settings);
        var pipeline = new EventIngestionPipeline(
            new StubNormalizer(disasterEvent),
            new EventVersionCache(),
            new PageComposer(),
            coordinator,
            DisplayEventFactory.Settings,
            new FilterSettings(true, true, true, HideQuakeBelowIntensity3: true));
        var raw = new RawProviderMessage(
            "p2pquake",
            "{}",
            SourceMode.Production,
            clock.UtcNow);

        EventIngestionResult result = pipeline.Process(raw);

        Assert.AreEqual(EventIngestionStatus.Accepted, result.Status);
        Assert.IsNotNull(result.Event);
        Assert.IsNull(result.Program);
        Assert.IsNull(result.Snapshot);
        Assert.IsNull(coordinator.Evaluate().CurrentProgram);
        Assert.AreEqual("最大震度フィルター", result.DisplaySuppressionReason);
        Assert.IsNotNull(result.ReceptionSummary);
        Assert.Contains("採用・非表示", result.ReceptionSummary.ProcessingResult);
    }

    [TestMethod]
    public void IntensityFilterShowsThreeAndKeepsUnknownMaximumForSafety()
    {
        var filter = new FilterSettings(true, true, true, HideQuakeBelowIntensity3: true);
        QuakeEvent intensityThree = DisplayEventFactory.CreateQuake(
            QuakeIssueType.DetailScale,
            [DisplayEventFactory.Point(1, JmaScale.Three)]);
        QuakeEvent unknown = DisplayEventFactory.CreateQuake(QuakeIssueType.Foreign);

        Assert.IsTrue(EventDisplayFilter.IsEnabled(filter, intensityThree));
        Assert.IsTrue(EventDisplayFilter.IsEnabled(filter, unknown));
    }

    [TestMethod]
    public void DefaultWeatherFilterKeepsWarningsAndTornadoButDropsOtherAdvisories()
    {
        WeatherWarningEvent weather = CreateWeatherInformation(
            WeatherInformationType.WarningAndAdvisory,
            [
                WeatherItem("熊本市", "4310000", "大雨特別警報", WeatherWarningLevel.SpecialWarning),
                WeatherItem("八代市", "4320200", "暴風警報", WeatherWarningLevel.Warning),
                WeatherItem("天草市", "4321500", "雷注意報", WeatherWarningLevel.Advisory),
                WeatherItem("宇城市", "4321300", "竜巻注意情報", WeatherWarningLevel.Advisory),
            ]);

        WeatherWarningEvent filtered = Assert.IsInstanceOfType<WeatherWarningEvent>(
            EventDisplayFilter.Apply(AppSettings.CreateDefault().Filter, weather));

        Assert.HasCount(3, filtered.Items);
        Assert.IsFalse(filtered.Items.Any(static item => item.KindName == "雷注意報"));
        Assert.IsTrue(filtered.Items.Any(static item => item.KindName == "竜巻注意情報"));
    }

    [TestMethod]
    public void ContinuationOnlyWeatherCanBeHiddenAfterOtherDisplayFilters()
    {
        WeatherWarningEvent weather = CreateWeatherInformation(
            WeatherInformationType.WarningAndAdvisory,
            [
                WeatherItem(
                    "倉敷市",
                    "3320200",
                    "大雨警報",
                    WeatherWarningLevel.Warning,
                    "継続"),
                WeatherItem(
                    "岡山市",
                    "3310000",
                    "雷注意報",
                    WeatherWarningLevel.Advisory,
                    "発表"),
            ]);
        FilterSettings defaults = AppSettings.CreateDefault().Filter;

        Assert.IsNull(EventDisplayFilter.Apply(defaults, weather));
        Assert.AreEqual(
            "継続情報のみ",
            EventDisplayFilter.DescribeSuppression(defaults, weather));

        WeatherWarningEvent shown = Assert.IsInstanceOfType<WeatherWarningEvent>(
            EventDisplayFilter.Apply(
                defaults with { HideWeatherContinuationOnly = false },
                weather));
        Assert.HasCount(1, shown.Items);
        Assert.AreEqual("継続", shown.Items[0].Status);
    }

    [TestMethod]
    public void ContinuationFilterNeverHidesNewAnnouncementsOrReleases()
    {
        WeatherWarningEvent mixed = CreateWeatherInformation(
            WeatherInformationType.WarningAndAdvisory,
            [
                WeatherItem(
                    "倉敷市",
                    "3320200",
                    "大雨警報",
                    WeatherWarningLevel.Warning,
                    "継続"),
                WeatherItem(
                    "岡山市",
                    "3310000",
                    "洪水警報",
                    WeatherWarningLevel.Warning,
                    "発表"),
            ]);
        WeatherWarningEvent release = CreateWeatherInformation(
            WeatherInformationType.WarningAndAdvisory,
            [WeatherItem(
                "倉敷市",
                "3320200",
                "大雨警報",
                WeatherWarningLevel.Warning,
                "解除")]);

        Assert.IsNotNull(EventDisplayFilter.Apply(AppSettings.CreateDefault().Filter, mixed));
        Assert.IsNotNull(EventDisplayFilter.Apply(AppSettings.CreateDefault().Filter, release));
    }

    [TestMethod]
    public void WeatherPrefectureFilterUsesJmaAreaCodePrefix()
    {
        WeatherWarningEvent weather = CreateWeatherInformation(
            WeatherInformationType.WarningAndAdvisory,
            [
                WeatherItem("熊本市", "4310000", "大雨警報", WeatherWarningLevel.Warning),
                WeatherItem("札幌市", "0110000", "暴風警報", WeatherWarningLevel.Warning),
            ]);
        FilterSettings filter = AppSettings.CreateDefault().Filter with
        {
            WeatherPrefectureCode = "43",
        };

        WeatherWarningEvent filtered = Assert.IsInstanceOfType<WeatherWarningEvent>(
            EventDisplayFilter.Apply(filter, weather));

        Assert.HasCount(1, filtered.Items);
        Assert.AreEqual("熊本市", filtered.Items[0].AreaName);
    }

    [TestMethod]
    public void WeatherPrefectureFilterAcceptsMultipleSelectedPrefectures()
    {
        WeatherWarningEvent weather = CreateWeatherInformation(
            WeatherInformationType.WarningAndAdvisory,
            [
                WeatherItem("札幌市", "0110000", "暴風警報", WeatherWarningLevel.Warning),
                WeatherItem("東京都千代田区", "1310100", "大雨警報", WeatherWarningLevel.Warning),
                WeatherItem("熊本市", "4310000", "大雨警報", WeatherWarningLevel.Warning),
            ]);
        FilterSettings filter = AppSettings.CreateDefault().Filter with
        {
            WeatherPrefectureCodes = ["01", "43"],
        };

        WeatherWarningEvent filtered = Assert.IsInstanceOfType<WeatherWarningEvent>(
            EventDisplayFilter.Apply(filter, weather));

        Assert.HasCount(2, filtered.Items);
        Assert.IsTrue(filtered.Items.Any(static item => item.AreaName == "札幌市"));
        Assert.IsTrue(filtered.Items.Any(static item => item.AreaName == "熊本市"));
        Assert.IsFalse(filtered.Items.Any(static item => item.AreaName == "東京都千代田区"));
    }

    [TestMethod]
    public void WeatherBulletinTypesHaveIndependentFilters()
    {
        FilterSettings filter = AppSettings.CreateDefault().Filter with
        {
            WeatherRecordShortRain = false,
            WeatherDisasterPreventionBulletins = true,
        };
        WeatherWarningEvent recordRain = CreateWeatherInformation(
            WeatherInformationType.RecordShortDurationHeavyRain,
            [WeatherItem("熊本県", "430000", "記録的短時間大雨情報", WeatherWarningLevel.Warning)]);
        WeatherWarningEvent bulletin = CreateWeatherInformation(
            WeatherInformationType.DisasterPreventionBulletin,
            [WeatherItem("熊本県", "430000", "気象防災速報", WeatherWarningLevel.Warning)]);

        Assert.IsNull(EventDisplayFilter.Apply(filter, recordRain));
        Assert.IsNotNull(EventDisplayFilter.Apply(filter, bulletin));
    }

    [TestMethod]
    public void UnknownActiveWeatherKindFailsSafeAsWarningInsteadOfDisappearing()
    {
        WeatherWarningEvent weather = CreateWeatherInformation(
            WeatherInformationType.WarningAndAdvisory,
            [WeatherItem("検証市", "9990000", "将来追加された警報", WeatherWarningLevel.Unknown)]);
        FilterSettings filter = AppSettings.CreateDefault().Filter with
        {
            WeatherWarnings = true,
            WeatherAdvisories = false,
        };

        WeatherWarningEvent filtered = Assert.IsInstanceOfType<WeatherWarningEvent>(
            EventDisplayFilter.Apply(filter, weather));

        Assert.HasCount(1, filtered.Items);
        Assert.AreEqual(WeatherWarningLevel.Unknown, filtered.Items[0].Level);
    }

    [TestMethod]
    public void UnchangedFilteredWeatherDisplayIsKeptForReviewButNotDisplayedAgain()
    {
        WeatherWarningEvent first = CreateWeatherInformation(
            WeatherInformationType.WarningAndAdvisory,
            [
                WeatherItem(
                    "倉敷市",
                    "3320200",
                    "レベル３大雨警報",
                    WeatherWarningLevel.Warning,
                    "継続"),
                WeatherItem(
                    "岡山市",
                    "3310000",
                    "雷注意報",
                    WeatherWarningLevel.Advisory,
                    "継続"),
            ]) with
        {
            Signature = "weather-revision-1",
        };
        WeatherWarningEvent advisoryChanged = CreateWeatherInformation(
            WeatherInformationType.WarningAndAdvisory,
            [
                WeatherItem(
                    "倉敷市",
                    "3320200",
                    "レベル３大雨警報",
                    WeatherWarningLevel.Warning,
                    "継続"),
                WeatherItem(
                    "岡山市",
                    "3310000",
                    "雷注意報",
                    WeatherWarningLevel.Advisory,
                    "解除"),
            ]) with
        {
            Signature = "weather-revision-2",
        };
        FilterSettings filter = AppSettings.CreateDefault().Filter with
        {
            WeatherWarnings = true,
            WeatherAdvisories = false,
            HideWeatherContinuationOnly = false,
        };
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, DisplayEventFactory.Settings);
        var pipeline = new EventIngestionPipeline(
            new QueueNormalizer(first, advisoryChanged),
            new EventVersionCache(),
            new PageComposer(),
            coordinator,
            DisplayEventFactory.Settings,
            filter);

        EventIngestionResult displayed = pipeline.Process(CreateRaw(clock, "first"));
        EventIngestionResult unchanged = pipeline.Process(CreateRaw(clock, "advisory-changed"));

        Assert.IsNotNull(displayed.Program);
        Assert.IsNotNull(displayed.Snapshot);
        Assert.IsNull(unchanged.Program);
        Assert.IsNull(unchanged.Snapshot);
        Assert.IsNotNull(unchanged.ReviewProgram);
        Assert.AreEqual("表示対象の変更なし", unchanged.DisplaySuppressionReason);
        Assert.AreEqual(0, unchanged.DisplayedItemCount);
        Assert.IsNotNull(unchanged.ReceptionSummary);
        Assert.Contains(
            "採用・非表示(表示対象の変更なし)",
            unchanged.ReceptionSummary.ProcessingResult);
        Assert.AreEqual(
            displayed.Program.ProgramId,
            coordinator.Evaluate().CurrentProgram?.ProgramId);
    }

    [TestMethod]
    public void ChangedFilteredWeatherDisplayIsDisplayedNormally()
    {
        WeatherWarningEvent first = CreateWeatherInformation(
            WeatherInformationType.WarningAndAdvisory,
            [WeatherItem(
                "倉敷市",
                "3320200",
                "レベル３大雨警報",
                WeatherWarningLevel.Warning,
                "継続")]) with
        {
            Signature = "weather-visible-revision-1",
        };
        WeatherWarningEvent warningChanged = CreateWeatherInformation(
            WeatherInformationType.WarningAndAdvisory,
            [WeatherItem(
                "岡山市",
                "3310000",
                "レベル３大雨警報",
                WeatherWarningLevel.Warning,
                "継続")]) with
        {
            Signature = "weather-visible-revision-2",
        };
        var clock = new FakeClock();
        var pipeline = new EventIngestionPipeline(
            new QueueNormalizer(first, warningChanged),
            new EventVersionCache(),
            new PageComposer(),
            new PriorityCoordinator(clock, DisplayEventFactory.Settings),
            DisplayEventFactory.Settings,
            AppSettings.CreateDefault().Filter with
            {
                HideWeatherContinuationOnly = false,
            });

        pipeline.Process(CreateRaw(clock, "first"));
        EventIngestionResult changed = pipeline.Process(CreateRaw(clock, "warning-changed"));

        Assert.IsNotNull(changed.Program);
        Assert.IsNotNull(changed.Snapshot);
        Assert.IsNull(changed.ReviewProgram);
        Assert.IsNull(changed.DisplaySuppressionReason);
        Assert.IsTrue(changed.Program.Pages.Any(static page =>
            page.AccessibleText.Contains("岡山市", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TwoIndependentEewWarningsAreComposedIntoOneStackedProgram()
    {
        DateTimeOffset firstTime = new(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(9));
        EewEvent first = DisplayEventFactory.CreateEew(
            eventId: "eew-warning-a",
            hypocenterName: "山梨県東部",
            issuedAt: firstTime,
            signature: "EEW-A-1");
        EewEvent second = DisplayEventFactory.CreateEew(
            eventId: "eew-warning-b",
            hypocenterName: "東京湾",
            issuedAt: firstTime.AddSeconds(1),
            signature: "EEW-B-1");
        var clock = new FakeClock();
        var pipeline = new EventIngestionPipeline(
            new QueueNormalizer(first, second),
            new EventVersionCache(),
            new PageComposer(),
            new PriorityCoordinator(clock, DisplayEventFactory.Settings),
            DisplayEventFactory.Settings);

        pipeline.Process(CreateRaw(clock, "first"));
        EventIngestionResult result = pipeline.Process(CreateRaw(clock, "second"));

        Assert.IsNotNull(result.Program);
        Assert.StartsWith("concurrent-eew:", result.Program.ProgramId);
        Assert.HasCount(1, result.Program.Pages);
        DisplayPage page = result.Program.Pages[0];
        Assert.AreEqual(2, page.Blocks.Count(static block =>
            block.StyleToken == DisplayStyleTokens.EewHeader));
        Assert.IsTrue(page.Blocks.Any(static block => block.PrimaryText.Contains("山梨県東部")));
        Assert.IsTrue(page.Blocks.Any(static block => block.PrimaryText.Contains("東京湾")));
        Assert.DoesNotContain("P2P", page.AccessibleText);
        Assert.IsFalse(page.AccessibleText.Contains("dmdata", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ContinuedReportUpdatesItsExistingEewSlotWithoutDuplicatingIt()
    {
        DateTimeOffset firstTime = new(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(9));
        EewEvent first = DisplayEventFactory.CreateEew(
            eventId: "eew-warning-a",
            hypocenterName: "山梨県東部",
            issuedAt: firstTime,
            signature: "EEW-A-1");
        EewEvent continued = DisplayEventFactory.CreateEew(
            eventId: "eew-warning-a",
            hypocenterName: "山梨県東部（更新）",
            issuedAt: firstTime.AddSeconds(1),
            signature: "EEW-A-2");
        var clock = new FakeClock();
        var pipeline = new EventIngestionPipeline(
            new QueueNormalizer(first, continued),
            new EventVersionCache(),
            new PageComposer(),
            new PriorityCoordinator(clock, DisplayEventFactory.Settings),
            DisplayEventFactory.Settings);

        pipeline.Process(CreateRaw(clock, "first"));
        EventIngestionResult result = pipeline.Process(CreateRaw(clock, "continued"));

        Assert.IsNotNull(result.Program);
        DisplayPage page = result.Program.Pages[0];
        Assert.AreEqual(1, page.Blocks.Count(static block =>
            block.StyleToken == DisplayStyleTokens.EewHeader));
        Assert.IsTrue(page.Blocks.Any(static block => block.PrimaryText.Contains("山梨県東部（更新）")));
    }

    [TestMethod]
    public void ClearingDisplayAlsoClearsRememberedConcurrentEewSlots()
    {
        DateTimeOffset firstTime = new(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(9));
        EewEvent first = DisplayEventFactory.CreateEew(
            eventId: "eew-warning-a",
            issuedAt: firstTime,
            signature: "EEW-A-1");
        EewEvent second = DisplayEventFactory.CreateEew(
            eventId: "eew-warning-b",
            issuedAt: firstTime.AddSeconds(1),
            signature: "EEW-B-1");
        var clock = new FakeClock();
        var pipeline = new EventIngestionPipeline(
            new QueueNormalizer(first, second),
            new EventVersionCache(),
            new PageComposer(),
            new PriorityCoordinator(clock, DisplayEventFactory.Settings),
            DisplayEventFactory.Settings);

        pipeline.Process(CreateRaw(clock, "first"));
        pipeline.ClearTransientState();
        EventIngestionResult result = pipeline.Process(CreateRaw(clock, "second"));

        Assert.IsNotNull(result.Program);
        Assert.AreEqual(1, result.Program.Pages[0].Blocks.Count(static block =>
            block.StyleToken == DisplayStyleTokens.EewHeader));
        Assert.IsFalse(result.Program.ProgramId.StartsWith("concurrent-eew:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CancellingOneConcurrentEewReplacesOnlyThatSlotWithCancellation()
    {
        DateTimeOffset firstTime = new(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(9));
        EewEvent first = DisplayEventFactory.CreateEew(
            eventId: "eew-warning-a",
            hypocenterName: "山梨県東部",
            issuedAt: firstTime,
            signature: "EEW-A-1");
        EewEvent second = DisplayEventFactory.CreateEew(
            eventId: "eew-warning-b",
            hypocenterName: "東京湾",
            issuedAt: firstTime.AddSeconds(1),
            signature: "EEW-B-1");
        EewEvent cancellation = DisplayEventFactory.CreateEew(
            cancelled: true,
            eventId: "eew-warning-a",
            issuedAt: firstTime.AddSeconds(2),
            signature: "EEW-A-CANCEL");
        var clock = new FakeClock();
        var pipeline = new EventIngestionPipeline(
            new QueueNormalizer(first, second, cancellation),
            new EventVersionCache(),
            new PageComposer(),
            new PriorityCoordinator(clock, DisplayEventFactory.Settings),
            DisplayEventFactory.Settings);

        pipeline.Process(CreateRaw(clock, "first"));
        pipeline.Process(CreateRaw(clock, "second"));
        EventIngestionResult result = pipeline.Process(CreateRaw(clock, "cancel"));

        Assert.IsNotNull(result.Program);
        Assert.AreEqual(1, result.Program.Pages[0].Blocks.Count(static block =>
            block.StyleToken == DisplayStyleTokens.EewHeader));
        Assert.AreEqual(1, result.Program.Pages[0].Blocks.Count(static block =>
            block.StyleToken == DisplayStyleTokens.EewHeaderCancel));
        Assert.IsTrue(result.Program.Pages[0].Blocks.Any(static block =>
            block.PrimaryText.Contains("東京湾")));
        Assert.IsTrue(result.Program.Pages[0].Blocks.Any(static block =>
            block.PrimaryText.Contains("先ほどの、緊急地震速報を取り消します")));
    }

    [TestMethod]
    public void CancellingBothConcurrentEewsShowsTwoCancellationCards()
    {
        DateTimeOffset firstTime = new(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(9));
        EewEvent first = DisplayEventFactory.CreateEew(
            eventId: "eew-warning-a",
            issuedAt: firstTime,
            signature: "EEW-A-1");
        EewEvent second = DisplayEventFactory.CreateEew(
            eventId: "eew-warning-b",
            issuedAt: firstTime.AddSeconds(1),
            signature: "EEW-B-1");
        EewEvent cancelFirst = DisplayEventFactory.CreateEew(
            cancelled: true,
            eventId: "eew-warning-a",
            issuedAt: firstTime.AddSeconds(2),
            signature: "EEW-A-CANCEL");
        EewEvent cancelSecond = DisplayEventFactory.CreateEew(
            cancelled: true,
            eventId: "eew-warning-b",
            issuedAt: firstTime.AddSeconds(3),
            signature: "EEW-B-CANCEL");
        var clock = new FakeClock();
        var pipeline = new EventIngestionPipeline(
            new QueueNormalizer(first, second, cancelFirst, cancelSecond),
            new EventVersionCache(),
            new PageComposer(),
            new PriorityCoordinator(clock, DisplayEventFactory.Settings),
            DisplayEventFactory.Settings);

        pipeline.Process(CreateRaw(clock, "first"));
        pipeline.Process(CreateRaw(clock, "second"));
        pipeline.Process(CreateRaw(clock, "cancel-first"));
        EventIngestionResult result = pipeline.Process(CreateRaw(clock, "cancel-second"));

        Assert.IsNotNull(result.Program);
        Assert.AreEqual(2, result.Program.Pages[0].Blocks.Count(static block =>
            block.StyleToken == DisplayStyleTokens.EewHeaderCancel));
        Assert.AreEqual(0, result.Program.Pages[0].Blocks.Count(static block =>
            block.StyleToken == DisplayStyleTokens.EewHeader));
    }

    private static RawProviderMessage CreateRaw(FakeClock clock, string payload) => new(
        "p2pquake",
        payload,
        SourceMode.Production,
        clock.UtcNow);

    private static WeatherWarningEvent CreateWeatherInformation(
        WeatherInformationType informationType,
        IReadOnlyList<WeatherWarningItem> items)
    {
        DateTimeOffset issuedAt = new(2026, 8, 10, 12, 0, 0, TimeSpan.FromHours(9));
        return new WeatherWarningEvent(
            EventId.Create($"weather-{informationType}"),
            "axis",
            issuedAt,
            issuedAt,
            $"signature-{informationType}",
            SourceMode.Production,
            new IssueInfo("気象庁", issuedAt, informationType.ToString(), CorrectionType.None),
            "熊本県に関する気象情報",
            items,
            isCancelled: false,
            informationType);
    }

    private static WeatherWarningItem WeatherItem(
        string areaName,
        string areaCode,
        string kindName,
        WeatherWarningLevel level,
        string status = "発表") => new(
        areaName,
        areaCode,
        kindName,
        string.Empty,
        level,
        status,
        IsActive: true);

    private sealed class StubNormalizer(DisasterEvent disasterEvent) : IEventNormalizer
    {
        public NormalizeResult Normalize(RawProviderMessage raw)
        {
            ArgumentNullException.ThrowIfNull(raw);
            return NormalizeResult.Success(disasterEvent);
        }
    }

    private sealed class IgnoringNormalizer : IEventNormalizer
    {
        public NormalizeResult Normalize(RawProviderMessage raw)
        {
            ArgumentNullException.ThrowIfNull(raw);
            return NormalizeResult.Ignored();
        }
    }

    private sealed class QueueNormalizer(params DisasterEvent[] events) : IEventNormalizer
    {
        private readonly Queue<DisasterEvent> _events = new(events);

        public NormalizeResult Normalize(RawProviderMessage raw)
        {
            ArgumentNullException.ThrowIfNull(raw);
            return NormalizeResult.Success(_events.Dequeue());
        }
    }
}
