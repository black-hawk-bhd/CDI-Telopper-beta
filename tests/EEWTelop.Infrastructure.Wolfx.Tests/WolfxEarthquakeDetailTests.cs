using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Wolfx.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Wolfx.Tests;

[TestClass]
public sealed class WolfxEarthquakeDetailTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 4, 0, 0, TimeSpan.Zero);
    private readonly WolfxEventNormalizer _normalizer = new(new EventSignatureBuilder());
    private const string Snapshot = """
        {"events":[{"eventId":"20261118125900","status":"reported","hypocenters":[
            {"code":"430","name":"岐阜県飛騨地方","latitude":36.2,"longitude":137.5,"depth":10.0}
          ],"reports":[
            {"id":"detail-1","product":"VXSE53","time":"2026-11-18T04:01:00Z","status":"issued","serial":"3",
             "originTime":"2026-11-18T03:59:00Z","hypocenter":0,"title":"震源・震度情報","magnitude":4.0,
             "observation":{"maxIntensity":"5-",
               "prefs":[{"code":"21","name":"岐阜県","int":"5-"}],
               "areas":[{"code":"430","name":"岐阜県飛騨","int":"5-","pref":0}],
               "cities":[{"code":"2120300","name":"高山市","area":0,"condition":"震度５弱以上　未入電",
                          "stations":[{"code":"2120321","name":"高山市奥飛騨温泉郷栃尾＊","int":"5-"}]}]},
             "comments":{"forecast":"この地震による津波の心配はありません。",
                         "warning":"今後の地震情報に注意してください。",
                         "note":"＊は気象庁以外の観測点です。"}}
          ]}]}
        """;

    [TestMethod]
    public async Task StationDetailKeepsOfficialTimeAndCodesAndUsesExistingIntensityCaptions()
    {
        using var reports = new WolfxEventNormalizer.EarthquakeDetailReports(Now);
        var buffer = new ProviderMessageBuffer();
        await reports.PublishDetailsAsync(Raw(Snapshot), buffer, TestContext.CancellationToken);
        RawProviderMessage raw = (await DrainAsync(buffer)).Single();
        Assert.IsFalse(raw.IsStateSnapshot);
        var quake = Assert.IsInstanceOfType<QuakeEvent>(_normalizer.Normalize(raw).Event);
        Assert.AreEqual("20261118125900", quake.Id.Value);
        Assert.AreEqual(Now.AddMinutes(1), quake.IssuedAt);
        Assert.AreEqual(Now.AddMinutes(-1), quake.Earthquake.OriginTime);
        Assert.AreEqual("岐阜県飛騨地方", quake.Earthquake.Hypocenter!.Name);
        Assert.AreEqual(10, quake.Earthquake.Hypocenter.DepthKilometers);
        Assert.AreEqual(DomesticTsunami.None, quake.Earthquake.DomesticTsunami);
        Assert.AreEqual(
            "この地震による津波の心配はありません。\n今後の地震情報に注意してください。\n＊は気象庁以外の観測点です。",
            quake.FreeFormComment);
        QuakePoint station = quake.Points.Single(static point => point.StationCode.Length > 0);
        Assert.AreEqual("2120321", station.StationCode);
        Assert.AreEqual("430", station.SeismicAreaCode);
        Assert.AreEqual("2120300", station.MunicipalityCode);
        Assert.AreEqual("岐阜県", station.Prefecture);
        Assert.AreEqual("高山市奥飛騨温泉郷栃尾＊", station.Address);
        Assert.IsTrue(station.IsNonJmaIntensityStation);
        Assert.AreEqual(JmaScale.FiveLowerOrMore, quake.Points.Single(point => point.Address == "高山市").Scale);
        DisplayProgram program = new PageComposer().Compose(quake, AppSettings.CreateDefault().Display);
        string captions = string.Join('\n', program.Pages.Select(static page => page.AccessibleText));
        StringAssert.Contains(captions, "岐阜県高山市");
        StringAssert.Contains(captions, "震度5弱以上 未入電");
        Assert.IsTrue(program.Pages.SelectMany(page => page.Blocks).Any(block =>
            block.StyleToken == DisplayStyleTokens.Intensity && block.Badge == "震度5弱"));
        Assert.AreEqual(1, program.Pages.Count(static page =>
            page.AccessibleText.Contains("津波の心配はありません", StringComparison.Ordinal)));
        Assert.AreEqual("今後の地震情報に注意してください。", program.Pages[^1].AccessibleText);
        Assert.DoesNotContain("気象庁以外の観測点", captions);

        DisplaySettings filtered = AppSettings.CreateDefault().Display with { HideNonJmaIntensityStations = true };
        DisplayProgram filteredProgram = new PageComposer().Compose(quake, filtered);
        string filteredCaptions = string.Join('\n', filteredProgram.Pages.Select(page => page.AccessibleText));
        Assert.IsFalse(filteredProgram.Pages.SelectMany(page => page.Blocks).Any(block =>
            block.StyleToken == DisplayStyleTokens.Intensity && block.Badge == "震度5弱"));
        Assert.Contains("岐阜県高山市", filteredCaptions);
        Assert.Contains("震度5弱以上 未入電", filteredCaptions);
    }

    [TestMethod]
    public async Task ArchivedDetailIsReviewOnlyAndRepeatedOrSummaryFramesDoNotReplaceIt()
    {
        using var reports = new WolfxEventNormalizer.EarthquakeDetailReports(Now.AddMinutes(5));
        var buffer = new ProviderMessageBuffer();
        await reports.PublishDetailsAsync(Raw(Snapshot), buffer, TestContext.CancellationToken);
        await reports.PublishDetailsAsync(Raw(Snapshot), buffer, TestContext.CancellationToken);
        await reports.PublishSummaryAsync(Raw("""{"type":"jma_eqlist","No1":{"EventID":"20261118125900"}}"""),
            buffer, TestContext.CancellationToken);
        await reports.PublishSummaryAsync(Raw("""{"type":"jma_eqlist","No1":{"EventID":"different-event"}}"""),
            buffer, TestContext.CancellationToken);
        RawProviderMessage[] pending = await DrainAsync(buffer);
        Assert.HasCount(2, pending);
        Assert.IsTrue(pending[0].IsStateSnapshot);
        StringAssert.Contains(pending[1].Payload, "different-event");
    }

    [TestMethod]
    public async Task LongPeriodReportUsesOwnObservationsAndIndependentReportNumber()
    {
        const string nextReport = """
            {"events":[{"eventId":"20261118125900","status":"reported","reports":[
              {"id":"long-1","product":"VXSE62","time":"2026-11-18T04:02:00Z","status":"issued","serial":"1",
               "observation":{"maxLongPeriod":"2","prefs":[{"name":"岐阜県"}],
                              "areas":[{"name":"岐阜県飛騨","pref":0,"lg":"2"}]}}
            ]}]}
            """;
        using var reports = new WolfxEventNormalizer.EarthquakeDetailReports(Now);
        var buffer = new ProviderMessageBuffer();
        await reports.PublishDetailsAsync(Raw(Snapshot), buffer, TestContext.CancellationToken);
        await reports.PublishDetailsAsync(Raw(nextReport), buffer, TestContext.CancellationToken);
        var cache = new EventVersionCache();
        foreach (RawProviderMessage raw in await DrainAsync(buffer))
        {
            var quake = Assert.IsInstanceOfType<QuakeEvent>(_normalizer.Normalize(raw).Event);
            Assert.IsTrue(cache.TryAccept(quake));
            if (quake.Issue.RawType == "VXSE62")
            {
                Assert.IsEmpty(quake.Points);
                Assert.AreEqual(QuakeIssueType.LongPeriodObservation, quake.IssueType);
                Assert.AreEqual(2, quake.LongPeriodIntensity!.MaximumClass);
                Assert.AreEqual("岐阜県", quake.LongPeriodIntensity.Areas.Single().Prefecture);
            }
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static RawProviderMessage Raw(string json) => new("wolfx", json, SourceMode.Production, Now.AddMinutes(5));

    private static async Task<RawProviderMessage[]> DrainAsync(ProviderMessageBuffer buffer)
    {
        buffer.TryComplete();
        var messages = new List<RawProviderMessage>();
        await foreach (RawProviderMessage raw in buffer.ReadAllAsync()) messages.Add(raw);
        return messages.ToArray();
    }
}
