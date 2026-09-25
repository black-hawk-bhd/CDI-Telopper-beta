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
