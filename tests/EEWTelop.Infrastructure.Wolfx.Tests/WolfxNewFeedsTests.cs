using System;
using System.Linq;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Wolfx.Configuration;
using EEWTelop.Infrastructure.Wolfx.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Wolfx.Tests;

[TestClass]
public sealed class WolfxNewFeedsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 4, 0, 0, TimeSpan.Zero);
    private readonly WolfxEventNormalizer _normalizer = new(new EventSignatureBuilder());

    private const string Tsunami = """
        {
          "type":"jma_tsunami","status":"active","trusted":true,"validUntil":"2026-11-18T04:02:00Z",
          "report":{"id":"warning-1","product":"VTSE41","time":"2026-11-18T04:00:00Z","status":"issued",
                    "eventIds":["20261118130000","20261118125900"]},
          "areas":[{"code":"200","name":"岩手県","level":"warning","kind":"51",
                    "firstWave":{"text":"ただちに津波来襲と予測"},"maxWave":{"height":10,"text":"10m超"},
                    "stations":[{"code":"201","name":"宮古","highTide":"2026-11-18T06:00:00Z"}]}],
          "observations":[
            {"id":"observations-1","product":"VTSE51","time":"2026-11-18T04:01:00Z","status":"issued",
             "observed":[{"code":"200","name":"岩手県","stations":[{"code":"201","name":"宮古","maxWave":{"height":0.8}}]}]},
            {"id":"offshore-cancel","product":"VTSE52","time":"2026-11-18T04:01:01Z","status":"cancelled",
             "observed":[{"name":"沖合","maxWave":{"height":2}}]}
          ],
          "lastKnown":{"areas":[{"name":"旧区域","level":"major_warning"}]}
        }
        """;

    [TestMethod]
    public void TsunamiKeepsWarningAreasSeparateFromObservationsAndQualitativeHeights()
    {
        var item = Assert.IsInstanceOfType<TsunamiEvent>(_normalizer.Normalize(Raw(Tsunami)).Event);
        Assert.AreEqual("20261118130000", item.Id.Value);
        Assert.AreEqual("20261118130000,20261118125900", string.Join(',', item.JmaEarthquakeIds));
        Assert.AreEqual(Now.AddMinutes(2), item.ExpireAt);
        TsunamiArea forecast = item.Areas.Single(static area => area.Role == TsunamiInformationRole.ForecastArea);
        Assert.AreEqual(TsunamiGrade.Warning, forecast.Grade);
        Assert.IsTrue(forecast.Immediate);
        Assert.IsNull(forecast.MaximumHeight!.ValueMeters);
        Assert.AreEqual("10m超", forecast.MaximumHeight.Description);
        Assert.AreEqual(Now.AddHours(2), item.Areas.Single(static area =>
            area.Role == TsunamiInformationRole.StationForecast).HighTideAt);
        Assert.AreEqual(0.8, item.Areas.Single(static area =>
            area.Role == TsunamiInformationRole.CoastalObservation).MaximumHeight!.ValueMeters);
        Assert.HasCount(2, item.SnapshotObservations!);
        Assert.IsTrue(item.SnapshotObservations!.Single(static report => report.Issue.RawType == "VTSE52").IsCancelled);
        Assert.IsFalse(item.IsCancelled);
    }

    [TestMethod]
    public void TsunamiSnapshotLinksLaterEarthquakeInItsIdListAndClearsThatLinkWhenUntrusted()
    {
        const string detail = """
            {"type":"jma_earthquake_detail","eventId":"20261118125900",
             "report":{"id":"intensity-1","product":"VXSE51","time":"2026-11-18T04:00:10Z","status":"issued",
                       "observation":{"maxIntensity":"4","prefs":[{"name":"岩手県","int":"4"}]}}}
            """;
        var clock = new TestClock();
        DisplaySettings settings = AppSettings.CreateDefault().Display with { OmitScalePromptCautionWhenTsunamiWarningActive = true };
        var pipeline = new EventIngestionPipeline(_normalizer, new EventVersionCache(), new PageComposer(),
            new PriorityCoordinator(clock, settings), settings);
        Assert.IsNotNull(pipeline.Process(Raw(Tsunami)).Program);

        clock.UtcNow = Now.AddSeconds(10);
        EventIngestionResult first = pipeline.Process(Raw(detail, clock.UtcNow));
        Assert.IsNotNull(first.Program);
        Assert.IsFalse(first.Program.Pages.Any(page => page.AccessibleText.Contains("念のため津波に注意", StringComparison.Ordinal)));

        clock.UtcNow = Now.AddSeconds(20);
        var untrusted = Tsunami.Replace("\"trusted\":true", "\"trusted\":false", StringComparison.Ordinal);
        Assert.IsTrue(pipeline.Process(Raw(untrusted, clock.UtcNow)).ClearTsunamiDisplay);
        string followUp = detail.Replace("intensity-1", "intensity-2", StringComparison.Ordinal)
            .Replace("04:00:10Z", "04:00:20Z", StringComparison.Ordinal);
        EventIngestionResult second = pipeline.Process(Raw(followUp, clock.UtcNow));
        Assert.IsNotNull(second.Program);
        Assert.IsTrue(second.Program.Pages.Any(page => page.AccessibleText.Contains("念のため津波に注意", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TsunamiTrustRenewalKeepsTheSameReportAndEnforcesTheNewDeadline()
    {
        var clock = new TestClock();
        DisplaySettings display = AppSettings.CreateDefault().Display;
        var coordinator = new PriorityCoordinator(clock, display);
        var pipeline = new EventIngestionPipeline(_normalizer, new EventVersionCache(), new PageComposer(), coordinator, display);
        EventIngestionResult first = pipeline.Process(Raw(Tsunami, clock.UtcNow));
        Assert.IsNotNull(first.Program);
        clock.UtcNow = Now.AddMinutes(1);
        EventIngestionResult renewed = pipeline.Process(Raw(
            Tsunami.Replace("04:02:00Z", "04:03:00Z", StringComparison.Ordinal), clock.UtcNow));
        Assert.IsTrue(renewed.IsTsunamiStateRefresh);
        Assert.AreEqual(first.Program.ProgramId, renewed.Program!.ProgramId);
        Assert.AreEqual(Now, renewed.Snapshot!.ProgramStartedAtUtc);
        clock.UtcNow = Now.AddMinutes(2).AddSeconds(1);
        Assert.IsNotNull(coordinator.Evaluate().CurrentProgram);
        clock.UtcNow = Now.AddMinutes(3);
        Assert.IsNull(coordinator.Evaluate().CurrentProgram);
    }

    [TestMethod]
    public void UntrustedTsunamiClearsTheDisplayWithoutClaimingTheWarningWasLifted()
    {
        var clock = new TestClock();
        DisplaySettings display = AppSettings.CreateDefault().Display;
        var coordinator = new PriorityCoordinator(clock, display);
        var pipeline = new EventIngestionPipeline(_normalizer, new EventVersionCache(), new PageComposer(), coordinator, display);
        Assert.IsNotNull(pipeline.Process(Raw(Tsunami)).Program);
        EventIngestionResult result = pipeline.Process(Raw(Tsunami.Replace("\"trusted\":true", "\"trusted\":false", StringComparison.Ordinal)));
        var item = Assert.IsInstanceOfType<TsunamiEvent>(result.Event);
        Assert.IsEmpty(item.Areas);
        Assert.IsFalse(item.IsCancelled);
        Assert.IsTrue(result.ClearTsunamiDisplay);
        Assert.IsNull(coordinator.Evaluate().CurrentProgram);
    }

    [TestMethod]
    [DataRow("cleared", "issued", true, false)]
    [DataRow("active", "cancelled", true, true)]
    [DataRow("expired", "cancelled", false, false)]
    [DataRow("none_reported", "issued", false, false)]
    public void TsunamiDistinguishesLiftingFromTelegramCancellationAndExpiry(
        string status, string reportStatus, bool endsWarning, bool cancelsTelegram)
    {
        string json = $$"""
            {"status":"{{status}}","trusted":true,"validUntil":"2026-11-18T04:02:00Z",
             "report":{"id":"report-1","product":"VTSE41","time":"2026-11-18T04:00:00Z","status":"{{reportStatus}}"},
             "areas":[],"observations":[]}
            """;
        var item = Assert.IsInstanceOfType<TsunamiEvent>(_normalizer.Normalize(Raw(json)).Event);
        Assert.AreEqual(endsWarning, item.IsCancelled);
        Assert.AreEqual(cancelsTelegram, item.Issue.InformationType == "取消");
        Assert.AreEqual(status == "expired", item.IsExpired);
    }

    [TestMethod]
    [DataRow("VYSE52", "定例解説", "null", true)]
    [DataRow("VYSE52", "", "\"200\"", true)]
    [DataRow("VYSE52", "", "200", true)]
    [DataRow("VYSE52", "", "null", false)]
    [DataRow("VYSE51", "臨時解説", "200", false)]
    [DataRow("VYSE50", "調査中", "null", false)]
    [DataRow("VYSE60", "", "null", false)]
    public void SpecialEarthquakeIdentifiesRoutineCommentaryFromLabelOrCode(
        string product, string label, string code, bool isRoutine)
    {
        string json = $$"""
            {"type":"jma_special_eq","report":{"id":"special","product":"{{product}}",
             "time":"2026-11-18T04:00:00Z","status":"issued","label":"{{label}}","code":{{code}}
            } }
            """;
        var item = Assert.IsInstanceOfType<QuakeEvent>(_normalizer.Normalize(Raw(json)).Event);
        Assert.AreEqual(isRoutine, item.IsRoutineNankaiCommentary);
    }

    [TestMethod]
    public void SpecialEarthquakeBatchPreservesAllProductsAndRoutesOnlyToTheSpecialSource()
    {
        const string json = """
            {
              "type":"jma_special_eq","appendices":["参考資料"],
              "reports":[
                {"id":"four","product":"VYSE60","time":"2026-11-18T03:59:01Z","status":"issued","text":"後発地震への備えを確認してください。"},
                {"id":"three","product":"VYSE52","time":"2026-11-18T03:59:00Z","status":"issued","label":"定例解説","text":"定例の本文。","appendix":0},
                {"id":"two","product":"VYSE51","time":"2026-11-18T03:58:05Z","status":"cancelled","notice":"取消の理由。"},
                {"id":"one","product":"VYSE50","time":"2026-11-18T03:58:00Z","status":"corrected","label":"調査中","text":"訂正した本文。"}
              ]
            }
            """;
        var special = new WolfxEventNormalizer.SpecialEarthquakeReports();
        RawProviderMessage[] batch = special.Read(Raw(json), initialSnapshot: true, connectedAt: Now).ToArray();
        Assert.HasCount(4, batch);
        Assert.IsTrue(batch.All(item => item.IsStateSnapshot && item.TransportPayload == json));
        Assert.IsEmpty(special.Read(Raw(json), initialSnapshot: false, connectedAt: Now).ToArray());
        string update = json.Replace("03:59:01Z", "04:00:01Z", StringComparison.Ordinal);
        RawProviderMessage newReport = special.Read(Raw(update), initialSnapshot: false, connectedAt: Now).Single();
        Assert.IsFalse(newReport.IsStateSnapshot);
        AppSettings defaults = AppSettings.CreateDefault();
        var provider = defaults.Provider with
        {
            Routing = defaults.Provider.Routing with { Quake = ReceptionProvider.Wolfx, NankaiTrough = ReceptionProvider.Disabled },
        };
        var selection = new ProviderSelectionEventNormalizer(_normalizer, provider);
        Assert.IsTrue(batch.All(item => selection.Normalize(item).Status == NormalizeStatus.Ignored));
        provider = provider with { Routing = provider.Routing with { Quake = ReceptionProvider.Disabled, NankaiTrough = ReceptionProvider.Wolfx, Tsunami = ReceptionProvider.Wolfx } };
        selection.UpdateProviderSelection(provider);
        WolfxProviderOptions options = WolfxProviderOptions.FromSettings(provider);
        Assert.IsTrue(options.ReceiveTsunami && options.ReceiveSpecialEarthquake && !options.ReceiveQuake);
        Assert.IsEmpty(options.Validate());

        QuakeEvent[] reports = batch.Select(item => Assert.IsInstanceOfType<QuakeEvent>(selection.Normalize(item).Event)).ToArray();
        string[] products = ["VYSE50", "VYSE51", "VYSE52", "VYSE60"];
        CollectionAssert.AreEqual(products, reports.Select(item => item.Issue.RawType).ToArray());
        Assert.AreEqual(CorrectionType.Generic, reports[0].Issue.Correction);
        Assert.IsTrue(reports[1].IsCancelled);
        StringAssert.Contains(reports[2].FreeFormComment, "参考資料");
        Assert.AreEqual(QuakeIssueType.SubsequentEarthquakeAdvisory, reports[3].IssueType);
        var pipeline = new EventIngestionPipeline(selection, new EventVersionCache(), new PageComposer(),
            new PriorityCoordinator(new TestClock(), defaults.Display), defaults.Display);
        EventIngestionResult cached = pipeline.Process(batch[2]);
        Assert.IsNull(cached.Program);
        StringAssert.Contains(cached.ReviewProgram!.Pages[0].AccessibleText, "南海トラフ地震関連解説情報（定例解説）");
        Assert.IsFalse(cached.ReviewProgram.Pages[0].AccessibleText.Contains("臨時情報", StringComparison.Ordinal));
    }

    private static RawProviderMessage Raw(string json, DateTimeOffset? receivedAt = null) =>
        new(WolfxProviderOptions.ProviderName, json, SourceMode.Production, receivedAt ?? Now);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public long GetTimestamp() => UtcNow.UtcTicks;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.FromTicks(UtcNow.UtcTicks - startingTimestamp);
    }
}
