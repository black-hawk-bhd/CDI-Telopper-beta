using System.Globalization;
using System.Text.Json;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class DisasterSimulatorTests
{
    [TestMethod]
    [DataRow("http://example.com:64270/")]
    [DataRow("http://127.0.0.1.example.com/")]
    [DataRow("http://192.168.0.2/")]
    [DataRow("https://127.0.0.1/")]
    [DataRow("http://127.0.0.1/?token=secret")]
    [DataRow("http://user:secret@127.0.0.1/")]
    public void RejectNonLocalOrCredentialUrl(string address) =>
        Assert.Throws<ArgumentException>(() => DisasterSimulatorClient.ValidateEndpoint(address));

    [TestMethod]
    public void AcceptLiteralLoopback() => Assert.AreEqual(64270,
        DisasterSimulatorClient.ValidateEndpoint("http://127.0.0.1:64270/").Port);

    [TestMethod]
    [DataRow("earthquake")]
    [DataRow("eew")]
    [DataRow("tsunami")]
    public void UpstreamLiveNeverBecomesProduction(string kind)
    {
        using var json = JsonDocument.Parse("""
            {"eventId":"example","serial":1,"sourceMode":"live","isTraining":false,
             "informationType":"DetailScale","telegramType":"VTSE41","isWarning":true,
             "earthquake":{"originTime":"2026-09-25T00:00:00Z","maximumIntensity":"5弱",
               "hypocenter":{"name":"訓練震源","latitude":35,"longitude":139,"magnitude":6.1}},
             "points":[{"name":"訓練市","prefecture":"訓練県","municipalityName":"訓練市","areaCode":"123","intensity":"5-"}],
             "areas":[{"name":"訓練地域","grade":"MajorWarning","role":"ForecastArea","intensity":"6強",
                       "maximumHeight":{"valueMeters":5,"description":"5m"}}]}
            """);
        var item = DisasterSimulatorDecoder.Decode(kind, json.RootElement, DateTimeOffset.UtcNow);
        Assert.AreEqual(SourceMode.ManualTest, item.SourceMode);
        Assert.IsTrue(item.Id.Value.StartsWith("simulator:", StringComparison.Ordinal));
        if (item is QuakeEvent quake)
        {
            Assert.AreEqual(JmaScale.FiveLower, quake.Earthquake.MaximumScale);
            Assert.AreEqual("123", quake.Points.Single().SeismicAreaCode);
            Assert.AreEqual(6.1, quake.Earthquake.Hypocenter!.Magnitude);
        }
        if (item is EewEvent eew) { Assert.IsTrue(eew.IsTest); Assert.AreEqual(JmaScale.SixUpper, eew.Areas.Single().ScaleFrom); }
        if (item is TsunamiEvent tsunami) Assert.AreEqual(TsunamiGrade.MajorWarning, tsunami.Areas.Single().Grade);
    }


    [TestMethod]
    public void LongPeriodObservationPreservesSimulatorClasses()
    {
        using var json = JsonDocument.Parse("""
            {
              "eventId":"lp-1", "serial":1, "informationType":"LongPeriodObservation",
              "issuedAt":"2026-09-25T20:00:00+09:00",
              "earthquake":{"originTime":"2026-09-25T19:58:00+09:00"},
              "longPeriodIntensity":{
                "maximumClass":4,
                "areas":[
                  {"prefecture":"石川県","area":"石川県能登","class":4},
                  {"prefecture":"石川県","area":"石川県加賀","class":3},
                  {"prefecture":"新潟県","area":"新潟県上越","class":2}
                ]
              }
            }
            """);
        var quake = (QuakeEvent)DisasterSimulatorDecoder.Decode(
            "earthquake", json.RootElement, DateTimeOffset.UtcNow);
        Assert.AreEqual(QuakeIssueType.LongPeriodObservation, quake.IssueType);
        Assert.IsNotNull(quake.LongPeriodIntensity);
        Assert.AreEqual(4, quake.LongPeriodIntensity.MaximumClass);
        Assert.HasCount(3, quake.LongPeriodIntensity.Areas);
        Assert.AreEqual("石川県能登", quake.LongPeriodIntensity.Areas[0].Area);
        Assert.AreEqual(4, quake.LongPeriodIntensity.Areas[0].Class);
        Assert.AreEqual(SourceMode.ManualTest, quake.SourceMode);
        var program = new EEWTelop.Application.Display.PageComposer().Compose(quake,
            EEWTelop.Application.Configuration.AppSettings.CreateDefault().Display);
        string text = string.Join("\n", program.Pages.Select(p => p.AccessibleText));
        StringAssert.Contains(text, "石川県能登");
        StringAssert.Contains(text, "4");
    }

    [TestMethod]
    public void LongPeriodRejectsInvalidClassesAndDeduplicatesAreas()
    {
        using var json = JsonDocument.Parse("""
            {"eventId":"lp","informationType":"LongPeriodObservation","longPeriodIntensity":{
              "areas":[{"prefecture":"石川県","area":"能登","class":2},
                       {"prefecture":"石川県","area":"能登","class":"4"},
                       {"prefecture":"石川県","area":"加賀","class":5},
                       {"prefecture":"石川県","area":"","class":3}]}}
            """);
        var quake = (QuakeEvent)DisasterSimulatorDecoder.Decode("earthquake", json.RootElement, DateTimeOffset.UtcNow);
        Assert.IsNotNull(quake.LongPeriodIntensity);
        Assert.AreEqual(4, quake.LongPeriodIntensity.MaximumClass);
        Assert.HasCount(1, quake.LongPeriodIntensity.Areas);
        Assert.AreEqual(4, quake.LongPeriodIntensity.Areas[0].Class);
    }

    [TestMethod]
    public void LongPeriodAbsentOrInvalidDoesNotInventInformation()
    {
        foreach (string payload in new[] { "{}", "{\"maximumClass\":9,\"areas\":[]}" })
        {
            using var json = JsonDocument.Parse("{\"eventId\":\"lp\",\"longPeriodIntensity\":" + payload + "}");
            var quake = (QuakeEvent)DisasterSimulatorDecoder.Decode("earthquake", json.RootElement, DateTimeOffset.UtcNow);
            Assert.IsNull(quake.LongPeriodIntensity);
        }
    }

    [TestMethod]
    public void InitialHeartbeatAndUnchangedSectionsDoNotReplay()
    {
        var tracker = new SimulatorUpdateTracker();
        Assert.AreEqual(0, Read(tracker, "snapshot", "one", 1, "1").Events.Count);
        Assert.AreEqual(0, Read(tracker, "heartbeat", "one", 2, "1").Events.Count);
        Assert.AreEqual(1, Read(tracker, "update", "one", 3, "2").Events.Count);
        Assert.AreEqual(0, Read(tracker, "update", "one", 4, "2").Events.Count);
        Assert.AreEqual(0, Read(tracker, "update", "one", 3, "1").Events.Count);
        var reset = Read(tracker, "update", "two", 1, "1");
        Assert.IsTrue(reset.Reset);
        Assert.AreEqual(1, reset.Events.Count);
    }

    [TestMethod]
    public void CancellationAndExpirationArePreserved()
    {
        using var json = JsonDocument.Parse("""
            {"eventId":"a","telegramType":"VTSE51","isTelegramCancellation":true,"isExpired":true}
            """);
        var item = (TsunamiEvent)DisasterSimulatorDecoder.Decode("tsunami", json.RootElement, DateTimeOffset.UtcNow);
        Assert.IsTrue(item.IsCancelled);
        Assert.IsTrue(item.IsExpired);
        Assert.AreEqual("取消", item.Issue.InformationType);
        Assert.AreEqual("VTSE51", item.Issue.RawType);
    }


    [TestMethod]
    public void Phase2CanonicalQuakeAndSeismicAreaFieldsArePreferred()
    {
        using var json = JsonDocument.Parse("""
            {
              "eventId":"phase2-quake", "serial":2, "informationType":"DetailScale",
              "issuedAt":"2026-09-26T01:00:00+09:00",
              "earthquake":{
                "originTime":"2026-09-26T00:59:00+09:00", "magnitude":9.9,
                "hypocenter":{"name":"大阪府北部","magnitude":6.2}
              },
              "points":[{
                "name":"大阪市北区","prefecture":"大阪府","municipalityName":"大阪市北区",
                "seismicAreaName":"大阪府北部","seismicAreaCode":"350","areaCode":"legacy",
                "stationCode":"2712700","intensity":"5+"
              }]
            }
            """);
        var quake = (QuakeEvent)DisasterSimulatorDecoder.Decode(
            "earthquake", json.RootElement, DateTimeOffset.UtcNow);
        Assert.AreEqual(6.2, quake.Earthquake.Hypocenter!.Magnitude);
        Assert.AreEqual("350", quake.Points.Single().SeismicAreaCode);
    }

    [TestMethod]
    public void Phase2EewUsesRangeAndArrivalWithLegacyFallback()
    {
        using var json = JsonDocument.Parse("""
            {
              "eventId":"phase2-eew", "serial":3, "informationType":"Forecast",
              "issuedAt":"2026-09-26T01:00:00+09:00", "isWarning":false,
              "earthquake":{"originTime":"2026-09-26T00:59:50+09:00"},
              "areas":[
                {"prefecture":"大阪府","name":"大阪府北部","intensityFrom":"5-","intensityTo":"6-","arrivalTime":"2026-09-26T01:00:20+09:00"},
                {"prefecture":"兵庫県","name":"兵庫県南東部","intensity":"4"}
              ]
            }
            """);
        var eew = (EewEvent)DisasterSimulatorDecoder.Decode(
            "eew", json.RootElement, DateTimeOffset.UtcNow);
        Assert.HasCount(2, eew.Areas);
        Assert.AreEqual(JmaScale.FiveLower, eew.Areas[0].ScaleFrom);
        Assert.AreEqual((int)JmaScale.SixLower, eew.Areas[0].ScaleTo);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-26T01:00:20+09:00", CultureInfo.InvariantCulture), eew.Areas[0].ArrivalTime);
        Assert.AreEqual(JmaScale.Four, eew.Areas[1].ScaleFrom);
        Assert.AreEqual((int)JmaScale.Four, eew.Areas[1].ScaleTo);
    }

    [TestMethod]
    public void Phase2TsunamiRestoresInitialImmediateOffshoreAndObservationAsOf()
    {
        using var json = JsonDocument.Parse("""
            {
              "eventId":"phase2-tsunami", "serial":4, "telegramType":"VTSE52",
              "issuedAt":"2026-08-11T01:10:00+09:00",
              "observationAsOf":"2026-08-11T01:09:00+09:00",
              "areas":[{
                "name":"岩手沖GPS","parentAreaName":"岩手県","role":"OffshoreObservation",
                "grade":"Unknown","immediate":true,
                "firstHeight":{"arrivalTime":"2026-08-11T01:02:00+09:00","initial":"押し"},
                "maximumHeight":{"observedAt":"2026-08-11T01:05:00+09:00","valueMeters":0.4}
              }]
            }
            """);
        var tsunami = (TsunamiEvent)DisasterSimulatorDecoder.Decode(
            "tsunami", json.RootElement, DateTimeOffset.UtcNow);
        TsunamiArea area = tsunami.Areas.Single();
        Assert.AreEqual(TsunamiInformationRole.OffshoreObservation, area.Role);
        Assert.IsTrue(area.Immediate);
        Assert.AreEqual("押し", area.FirstHeight!.Condition);
        Assert.AreEqual(DateTimeOffset.Parse("2026-08-11T01:09:00+09:00", CultureInfo.InvariantCulture), tsunami.ObservationAsOf);
    }

    [TestMethod]
    public void Phase2TsunamiImmediateCanBeDerivedForOlderPayloads()
    {
        using var json = JsonDocument.Parse("""
            {
              "eventId":"phase2-tsunami-legacy", "telegramType":"VTSE51",
              "areas":[{
                "name":"宮古","role":"CoastalObservation","grade":"Unknown",
                "firstHeight":{"initial":"既に津波到達と推測"}
              }]
            }
            """);
        var tsunami = (TsunamiEvent)DisasterSimulatorDecoder.Decode(
            "tsunami", json.RootElement, DateTimeOffset.UtcNow);
        Assert.IsTrue(tsunami.Areas.Single().Immediate);
        Assert.AreEqual("既に津波到達と推測", tsunami.Areas.Single().FirstHeight!.Condition);
    }

    private static (IReadOnlyList<DisasterEvent> Events, bool Reset) Read(
        SimulatorUpdateTracker tracker, string type, string session, int seq, string serial)
    {
        using var doc = JsonDocument.Parse($$$"""
            {"apiVersion":"1","type":"{{{type}}}","sessionId":"{{{session}}}","sequence":{{{seq}}},
             "earthquake":{"hasInformation":true,"earthquake":{"eventId":"q","serial":"{{{serial}}}"}},
             "eew":{"hasInformation":false,"events":[]},"tsunami":{"hasInformation":false}}
            """);
        return tracker.Read(doc.RootElement, DateTimeOffset.UtcNow);
    }
}
