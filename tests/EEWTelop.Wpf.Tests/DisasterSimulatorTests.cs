using System.Globalization;
using System.Text.Json;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
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
    public void NotoWarningFollowUpHighlightsTheFiveAddedPrefectures()
    {
        using var firstPayload = JsonDocument.Parse("""
            {
              "eventId":"20240101161010","serial":1,"isWarning":true,"issuedAt":"2024-01-01T16:10:16+09:00",
              "areas":[{"name":"石川","prefecture":"石川","intensity":"5-"}]
            }
            """);
        using var nextPayload = JsonDocument.Parse("""
            {
              "eventId":"20240101161010","serial":2,"isWarning":true,"issuedAt":"2024-01-01T16:10:43+09:00",
              "areas":[{"name":"石川","prefecture":"石川","intensity":"6-"},
                       {"name":"富山","prefecture":"富山","intensity":"5-"},
                       {"name":"新潟","prefecture":"新潟","intensity":"4"},
                       {"name":"長野","prefecture":"長野","intensity":"4"},
                       {"name":"岐阜","prefecture":"岐阜","intensity":"4"},
                       {"name":"群馬","prefecture":"群馬","intensity":"3"}]
            }
            """);
        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
        var first = (EewEvent)DisasterSimulatorDecoder.Decode("eew", firstPayload.RootElement, receivedAt);
        var next = (EewEvent)DisasterSimulatorDecoder.Decode("eew", nextPayload.RootElement, receivedAt.AddSeconds(27));
        var settings = AppSettings.CreateDefault().Display with { HighlightEewAreaChanges = true };
        var composer = new PageComposer();
        var concurrent = new ConcurrentEewProgramComposer();
        concurrent.Compose(first, composer.Compose(first, settings), settings);

        var program = concurrent.Compose(next, composer.Compose(next, settings), settings);
        var caption = program.Pages[0].Blocks.Single(block => block.StyleToken == DisplayStyleTokens.EewAreas);
        Assert.AreEqual("石川　富山　新潟　長野　岐阜　群馬", caption.PrimaryText);
        Assert.IsNotNull(caption.EewAreaLabels);
        Assert.AreEqual(new EewAreaLabel("石川", false), caption.EewAreaLabels[0]);
        Assert.AreEqual("富山　新潟　長野　岐阜　群馬",
            string.Join('　', caption.EewAreaLabels.Where(area => area.IsChanged).Select(area => area.Name)));
        Assert.AreEqual(SourceMode.ManualTest, program.SourceMode);
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
        Assert.IsTrue(tracker.AcceptedFrame);
        Assert.IsTrue(tracker.SnapshotChanged);
        Assert.HasCount(1, tracker.Snapshot);
        Assert.AreEqual(0, Read(tracker, "heartbeat", "one", 2, "1").Events.Count);
        Assert.IsTrue(tracker.AcceptedFrame);
        Assert.IsFalse(tracker.SnapshotChanged);
        Assert.AreEqual(1, Read(tracker, "update", "one", 3, "2").Events.Count);
        Assert.AreEqual(0, Read(tracker, "update", "one", 4, "2").Events.Count);
        Assert.AreEqual(0, Read(tracker, "update", "one", 3, "1").Events.Count);
        Assert.IsFalse(tracker.AcceptedFrame);
        var reset = Read(tracker, "update", "two", 1, "1");
        Assert.IsTrue(reset.Reset);
        Assert.AreEqual(1, reset.Events.Count);
    }

    [TestMethod]
    [DataRow("same-event", "test-session", "", true)]
    [DataRow("other-event", "test-session", ",\"previousGrade\":\"MajorWarning\"", true)]
    [DataRow("other-event", "test-session", "", false)]
    [DataRow("same-event", "reset-session", "", false)]
    public void SimulatorWarningDowngradeUsesTheReportedGradeOrAnEarlierForecastInTheSameSession(
        string previousId, string session, string previousGrade, bool expectedSwitch)
    {
        var tracker = new SimulatorUpdateTracker();
        DateTimeOffset receivedAt = DateTimeOffset.UnixEpoch;
        var initial = ReadForecast("snapshot", "test-session", 1, previousId, "MajorWarning", "");
        Assert.IsEmpty(initial.Events);

        var changed = ReadForecast("update", session, 2, "same-event", "Warning", previousGrade);
        var forecast = Assert.IsInstanceOfType<TsunamiEvent>(changed.Events.Single());
        Assert.AreEqual(SourceMode.ManualTest, forecast.SourceMode);
        var display = AppSettings.CreateDefault().Display;
        var program = new PageComposer().Compose(forecast, display);
        DisplayPage first = program.Pages[0];
        Assert.AreEqual(expectedSwitch
            ? "津波警報・注意報が切り替えられました"
            : "津波警報・津波注意報は次の通りです", first.Blocks[0].PrimaryText);
        if (expectedSwitch)
        {
            Assert.AreEqual("津波警報", first.Blocks[1].Badge);
            Assert.AreEqual("石川県能登で発表中", first.Blocks[1].PrimaryText);
            Assert.AreEqual("大津波警報から切り替えられました。", first.Blocks[1].SecondaryText);
        }

        Assert.IsEmpty(ReadForecast("heartbeat", session, 3, "same-event", "Warning", previousGrade, issuedAt: 2).Events);

        (IReadOnlyList<DisasterEvent> Events, bool Reset) ReadForecast(
            string type, string sessionId, int sequence, string id, string grade, string prior, int? issuedAt = null)
        {
            using var frame = JsonDocument.Parse($$$"""
              {"apiVersion":"1","type":"{{{type}}}","sessionId":"{{{sessionId}}}","sequence":{{{sequence}}},
               "tsunami":{"hasInformation":true,
                 "forecast":{"eventId":"{{{id}}}","telegramType":"VTSE41","issuedAt":"2024-01-01T07:12:0{{{issuedAt ?? sequence}}}Z",
                    "areas":[{"role":"ForecastArea","code":"371","name":"石川県能登","grade":"{{{grade}}}"{{{prior}}}}]}
               }}
              """);
            return tracker.Read(frame.RootElement, receivedAt);
        }
    }

    [TestMethod]
    public void SimulatorTracksTheForecastFromTheFullSnapshotAndClearsItOnReset()
    {
        var tracker = new SimulatorUpdateTracker();
        using var snapshot = JsonDocument.Parse("""
            {"apiVersion":"1","type":"snapshot","sessionId":"one","sequence":1,
             "tsunami":{"hasInformation":true,"forecast":{"eventId":"warning"},"observation":{"eventId":"observation"}}}
            """);
        using var reset = JsonDocument.Parse("""
            {"apiVersion":"1","type":"update","sessionId":"two","sequence":1,
             "tsunami":{"hasInformation":true,"observation":{"eventId":"observation"}}}
            """);

        Assert.IsEmpty(tracker.Read(snapshot.RootElement, DateTimeOffset.UtcNow).Events);
        Assert.AreEqual("simulator:warning", tracker.TsunamiForecast?.Id.Value);
        Assert.IsTrue(tracker.Read(reset.RootElement, DateTimeOffset.UtcNow).Reset);
        Assert.IsNull(tracker.TsunamiForecast);
        Assert.HasCount(1, tracker.Snapshot);
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
    [DataRow("""{"from":"5+","to":"5+"}""", false, "予想最大震度 5強　推定マグニチュード 5.7")]
    [DataRow("""{"from":"5-","to":"6-"}""", false, "予想最大震度 5弱〜6弱　推定マグニチュード 5.7")]
    [DataRow("""{"from":"6+","to":"over"}""", false, "予想最大震度 6強以上　推定マグニチュード 5.7")]
    [DataRow("""{"from":"5-"}""", false, "予想最大震度 下限 5弱　推定マグニチュード 5.7")]
    [DataRow("""{"to":"5+"}""", false, "予想最大震度 上限 5強　推定マグニチュード 5.7")]
    [DataRow("""{"from":"6+","to":"5-"}""", false, "推定マグニチュード 5.7")]
    [DataRow("""{"from":"5-?","to":"over"}""", false, "推定マグニチュード 5.7")]
    [DataRow("null", false, "推定マグニチュード 5.7")]
    [DataRow("""{"from":"5+","to":"5+"}""", true, null)]
    public void SimulatorEewDisplaysOnlyTheProvidedMaximumForecast(
        string forecast, bool cancelled, string? expectedEstimate)
    {
        using var json = JsonDocument.Parse($$$"""
            {
              "eventId":"20240101160608","isWarning":true,"isCancelled":{{{(cancelled ? "true" : "false")}}},
              "earthquake":{"hypocenter":{"name":"石川県能登地方","magnitude":5.7}},
              "areas":[{"prefecture":"石川","name":"石川","intensity":"5+"}],
              "maximumForecastIntensity":{{{forecast}}}
            }
            """);

        var eew = (EewEvent)DisasterSimulatorDecoder.Decode("eew", json.RootElement, DateTimeOffset.UtcNow);
        var program = new PageComposer().Compose(eew, AppSettings.CreateDefault().Display);

        Assert.AreEqual(SourceMode.ManualTest, program.SourceMode);
        Assert.AreEqual(expectedEstimate, program.Pages[0].Blocks
            .SingleOrDefault(block => block.StyleToken == DisplayStyleTokens.EewAreas)?.EewEstimateText);
        if (cancelled)
            Assert.IsNull(eew.MaximumForecastIntensity);
    }

    [TestMethod]
    [DataRow("仮定震源要素", "予想最大震度 5強以上")]
    [DataRow("", "予想最大震度 5強以上　推定マグニチュード 1.0")]
    public void SimulatorEewUsesTheHypocenterConditionToIgnoreAPlaceholderMagnitude(
        string condition, string expectedEstimate)
    {
        using var json = JsonDocument.Parse($$$"""
            {
              "eventId":"20240101165652","isWarning":true,
              "earthquake":{"magnitude":1.0,"hypocenter":{"name":"能登半島沖","condition":"{{{condition}}}"}},
              "maximumForecastIntensity":{"from":"5+","to":"over"}
            }
            """);
        var eew = (EewEvent)DisasterSimulatorDecoder.Decode("eew", json.RootElement, DateTimeOffset.UtcNow);
        var program = new PageComposer().Compose(eew, AppSettings.CreateDefault().Display);

        Assert.AreEqual(condition, eew.Earthquake?.Hypocenter?.Condition);
        Assert.AreEqual(condition.Length > 0 ? null : 1.0, eew.Earthquake?.Hypocenter?.Magnitude);
        Assert.AreEqual(SourceMode.ManualTest, program.SourceMode);
        Assert.AreEqual(expectedEstimate, program.Pages[0].Blocks
            .Single(block => block.StyleToken == DisplayStyleTokens.EewAreas).EewEstimateText);
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
