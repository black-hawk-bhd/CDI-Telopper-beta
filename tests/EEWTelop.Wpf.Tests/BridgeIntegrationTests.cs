using System.Net;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Bridge;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class BridgeIntegrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-21T12:00:00+09:00", System.Globalization.CultureInfo.InvariantCulture);
    private const string Quake = """
        {"mode":"live","eventId":"q1","serial":"2","reportTime":"2026-09-21T12:00:00+09:00","originTime":"2026-09-21T11:59:00+09:00",
         "hypocenter":"紀伊水道","depthKm":"0","magnitude":"5.4","maxInt":"5-","lat":34,"lon":135,
         "stations":[{"code":"A","name":"大阪市","intensity":"5-?","region":"大阪府","city":"大阪市"}]}
        """;
    private const string Eew = """
        {"eventId":"e1","serial":3,"time":"2026-09-21T12:00:00+09:00","hypo":"相模湾","mag":5.8,
         "depth":10,"isWarn":true,"isCancel":false,"warnAreas":["神奈川県"]}
        """;
    private const string Forecast = """
        {"mode":"live","Head":{"EventID":"t1","Serial":"1","ReportDateTime":"2026-09-21T12:00:00+09:00"},
         "Body":{"Tsunami":{"Forecast":{"Item":[{"Area":{"Name":"岩手県"},"Category":{"Kind":{"Code":"53"}},
          "FirstHeight":{"Condition":"津波到達中と推測"},"MaxHeight":{"TsunamiHeight":"3.0"}}]}}}}
        """;
    private static RawProviderMessage Raw(string type, string payload, bool snapshot = false, string mode = "live") =>
        new(ObsEarthquakeBridgeSource.ProviderName,
            $$"""{"schema":"cdi.bridge.event.v1","type":"{{type}}","mode":"{{mode}}","payload":{{payload}}} """,
            SourceMode.Production, Now) { IsStateSnapshot = snapshot };
    private static ObsEarthquakeBridgeNormalizer Normalizer() => new(new EventSignatureBuilder());

    private static RawProviderMessage V11(string type, string payload, bool snapshot = false) =>
        Raw(type, payload, snapshot) with { Json = Raw(type, payload).Json.Replace("cdi.bridge.event.v1", "obs-earthquake.bridge.event.v1", StringComparison.Ordinal) };

    [TestMethod]
    public void V11NestedEewAndQuakePreserveHypocenterAndCancellation()
    {
        const string payload = """
            {"mode":"live","eventId":"R8","serial":12,"reportTime":"2026-09-21T12:00:00+09:00",
             "isWarn":true,"isFinal":true,"maxIntensity":"6-",
             "hypocenter":{"name":"紀伊水道","magnitude":6.8,"depthKm":20,"latitude":33.8,"longitude":135.1},
             "warningAreas":[{"name":"大阪府","type":"警報"}]}
            """;
        var n = Normalizer();
        var eew = (EewEvent)n.Normalize(V11("eew", payload)).Event!;
        Assert.AreEqual("紀伊水道", eew.Earthquake!.Hypocenter!.Name);
        Assert.AreEqual(6.8, eew.Earthquake.Hypocenter.Magnitude);
        Assert.AreEqual("大阪府", eew.Areas.Single().Name);
        Assert.IsTrue(eew.IsFinal);
        var quake = (QuakeEvent)n.Normalize(V11("earthquake", payload, snapshot: true)).Event!;
        Assert.AreEqual(JmaScale.SixLower, quake.Earthquake.MaximumScale);
        foreach (string type in new[] { "eew.cancel", "earthquake.cancel" })
        {
            var cancel = n.Normalize(V11(type, """{"mode":"live","eventId":"R8","serial":13,"receivedAt":"2026-09-21T12:01:00+09:00"}"""));
            Assert.AreEqual(NormalizeStatus.Success, cancel.Status);
            Assert.IsTrue(cancel.Event!.IsCancelled);
        }
        Assert.AreEqual(NormalizeStatus.Ignored, n.Normalize(V11("eew", payload)).Status);
        Assert.AreEqual(NormalizeStatus.Ignored, n.Normalize(V11("earthquake", payload, snapshot: true)).Status);
    }

    [TestMethod]
    public void V11TsunamiWrapperExpiryAndCancelDoNotMeanAllClear()
    {
        string wrapped = $$"""
            {"mode":"live","eventId":"t1","serial":5,"available":true,
             "validDateTime":"2026-09-21T11:59:00+09:00","payload":{{Forecast}}}
            """;
        var n = Normalizer();
        var state = new ExternalApiState();
        var tsunami = (TsunamiEvent)n.Normalize(V11("tsunami.forecast", wrapped, snapshot: true)).Event!;
        Assert.AreEqual("5", tsunami.Issue.Serial);
        state.Observe(new(EventIngestionStatus.Accepted, tsunami, null, null, []));
        Assert.AreEqual("expired", state.Read(Now).ForecastState);
        var cancel = n.Normalize(V11("tsunami.forecast.cancel", """
            {"mode":"live","eventId":"t1","serial":6,"available":false,"reason":"cancelled",
             "receivedAt":"2026-09-21T12:01:00+09:00","payload":{}}
            """));
        Assert.AreEqual(NormalizeStatus.Success, cancel.Status);
        state.Observe(new(EventIngestionStatus.Accepted, cancel.Event, null, null, []));
        Assert.AreEqual("telegramCancelled", state.Read(Now).ForecastState);
        Assert.AreEqual(NormalizeStatus.Ignored, n.Normalize(V11("tsunami.forecast", wrapped)).Status);
    }

    [TestMethod]
    public void V11UnknownModeAndUnavailableAreNotPromotedToLive()
    {
        var n = Normalizer();
        Assert.AreEqual(NormalizeStatus.Ignored, n.Normalize(V11("earthquake", Quake.Replace("\"live\"", "\"unknown\"", StringComparison.Ordinal), snapshot: true)).Status);
        Assert.AreEqual(NormalizeStatus.Ignored, n.Normalize(V11("earthquake", """{"mode":"live","available":false,"reason":"upstream_unavailable"}""")).Status);
        Assert.AreEqual(NormalizeStatus.Ignored, n.Normalize(V11("future.weather", "{}")).Status);
    }

    [TestMethod]
    public void DifferentTsunamiEventIdsAreNotCombined()
    {
        var n = Normalizer();
        var state = new ExternalApiState();
        var forecast = (TsunamiEvent)n.Normalize(Raw("tsunami.forecast", Forecast)).Event!;
        state.Observe(new(EventIngestionStatus.Accepted, forecast, null, null, []));
        var observation = new TsunamiEvent(EventId.Create("other"), "obs-earthquake-bridge", Now, Now, "", SourceMode.Production,
            new IssueInfo("Bridge", Now, "VTSE51", CorrectionType.None), [], false, null);
        state.Observe(new(EventIngestionStatus.Accepted, observation, null, null, []));
        Assert.IsNull(state.Read(Now).Observation);
        var unrelatedCancel = new TsunamiEvent(EventId.Create("other"), "obs-earthquake-bridge", Now.AddMinutes(1), Now, "", SourceMode.Production,
            new IssueInfo("Bridge", Now.AddMinutes(1), "VTSE41", CorrectionType.None, InformationType: "取消"), [], true, null);
        state.Observe(new(EventIngestionStatus.Accepted, unrelatedCancel, null, null, []));
        Assert.AreEqual("active", state.Read(Now).ForecastState);
    }

    [TestMethod]
    public void ForecastAndObservationUseIndependentSerialsAndCancellationScopes()
    {
        var pipeline = new EventIngestionPipeline(Normalizer(), new EventVersionCache(), new PageComposer(),
            new PriorityCoordinator(new Clock(), AppSettings.CreateDefault().Display), AppSettings.CreateDefault().Display);
        string forecast = Forecast.Replace("\"Serial\":\"1\"", "\"Serial\":\"5\"", StringComparison.Ordinal);
        Assert.AreEqual(EventIngestionStatus.Accepted, pipeline.Process(V11("tsunami.forecast", forecast)).Status);
        const string observation = """
            {"mode":"live","eventId":"t1","serial":3,"reportTime":"2026-09-21T12:00:01+09:00",
             "payload":{"Body":{"Tsunami":{"Observation":{"Item":[{"Area":{"Name":"岩手県"},
              "Station":[{"Name":"宮古","MaxHeight":{"TsunamiHeight":"0.4"}}]}]}}}}}
            """;
        Assert.AreEqual(EventIngestionStatus.Accepted, pipeline.Process(V11("tsunami.observation", observation)).Status);
        Assert.AreEqual(EventIngestionStatus.Accepted, pipeline.Process(V11("tsunami.observation.cancel", """
            {"mode":"live","eventId":"t1","serial":4,"receivedAt":"2026-09-21T12:00:02+09:00"}
            """)).Status);
        var next = pipeline.Process(V11("tsunami.observation", observation.Replace("\"serial\":3", "\"serial\":5", StringComparison.Ordinal)
            .Replace("12:00:01", "12:00:03", StringComparison.Ordinal)));
        Assert.AreEqual(EventIngestionStatus.Accepted, next.Status);
        Assert.IsTrue(((TsunamiEvent)next.Event!).Areas.Any(a => a.Grade == TsunamiGrade.MajorWarning));
    }

    [TestMethod]
    public void ExpiredFlagSuppressesLiveOutputWithoutCancellingWarning()
    {
        var pipeline = new EventIngestionPipeline(Normalizer(), new EventVersionCache(), new PageComposer(),
            new PriorityCoordinator(new Clock(), AppSettings.CreateDefault().Display), AppSettings.CreateDefault().Display);
        string wrapped = $$"""{"mode":"live","eventId":"t1","serial":1,"expired":true,"payload":{{Forecast}}}""";
        var result = pipeline.Process(V11("tsunami.forecast", wrapped));
        Assert.AreEqual(EventIngestionStatus.Accepted, result.Status);
        Assert.IsNull(result.Program);
        Assert.IsTrue(result.Event!.IsExpired);
        Assert.IsFalse(result.Event.IsCancelled);
    }

    [TestMethod]
    public void QuakeMappingPreservesUnknownTsunamiAndZeroDepth()
    {
        var result = Normalizer().Normalize(Raw("earthquake", Quake));
        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        var quake = (QuakeEvent)result.Event!;
        Assert.AreEqual(0, quake.Earthquake.Hypocenter!.DepthKilometers);
        Assert.AreEqual(DomesticTsunami.Unknown, quake.Earthquake.DomesticTsunami);
        Assert.AreEqual(JmaScale.FiveLowerOrMore, quake.Points.Single().Scale);
        Assert.AreEqual("A", quake.Points.Single().StationCode);
    }

    [TestMethod]
    public void TestAndTrainingNeverBecomeLive()
    {
        var normalizer = Normalizer();
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Raw("eew", Eew, mode: "test")).Status);
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Raw("eew", Eew.Replace("\"isWarn\":true", "\"isTest\":true,\"isWarn\":true", StringComparison.Ordinal), snapshot: true)).Status);
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Raw("eew", Eew.Replace("\"isWarn\":true", "\"isWarn\":false", StringComparison.Ordinal))).Status);
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Raw("earthquake", Quake.Replace("\"mode\":\"live\",", "", StringComparison.Ordinal), snapshot: true)).Status);
    }

    [TestMethod]
    public void InitialSnapshotIsAcceptedWithoutSubtitleAndLiveDuplicateIsSuppressed()
    {
        var clock = new Clock();
        var coordinator = new PriorityCoordinator(clock, AppSettings.CreateDefault().Display);
        var pipeline = new EventIngestionPipeline(Normalizer(), new EventVersionCache(), new PageComposer(), coordinator, AppSettings.CreateDefault().Display);
        EventIngestionResult initial = pipeline.Process(Raw("earthquake", Quake, snapshot: true));
        Assert.AreEqual(EventIngestionStatus.Accepted, initial.Status);
        Assert.IsNull(initial.Program);
        Assert.IsNull(initial.Snapshot);
        Assert.AreEqual(EventIngestionStatus.Duplicate, pipeline.Process(Raw("earthquake", Quake)).Status);
        var repeatedSnapshot = pipeline.Process(Raw("earthquake", Quake, snapshot: true));
        Assert.AreEqual(EventIngestionStatus.Accepted, repeatedSnapshot.Status);
        Assert.IsNull(repeatedSnapshot.Program);
        var live = pipeline.Process(Raw("earthquake", Quake.Replace("\"2\"", "\"3\"", StringComparison.Ordinal)));
        Assert.AreEqual(EventIngestionStatus.Accepted, live.Status);
        Assert.IsNotNull(live.Program);
    }

    [TestMethod]
    public void OlderBufferedUpdateCannotRollBackSnapshot()
    {
        var normalizer = Normalizer();
        Assert.IsTrue(normalizer.Normalize(Raw("earthquake", Quake, snapshot: true)).IsSuccess);
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Raw("earthquake", Quake.Replace("12:00:00", "11:00:00", StringComparison.Ordinal))).Status);
    }

    [TestMethod]
    public void ForecastCode53AndExplicitReleaseAreDistinctFromEmptyPayload()
    {
        var normalizer = Normalizer();
        var warning = (TsunamiEvent)normalizer.Normalize(Raw("tsunami.forecast", Forecast)).Event!;
        Assert.AreEqual(TsunamiGrade.MajorWarning, warning.Areas.Single().Grade);
        Assert.AreEqual(3.0, warning.Areas.Single().MaximumHeight!.ValueMeters);
        var release = (TsunamiEvent)normalizer.Normalize(Raw("tsunami.forecast", Forecast.Replace("\"53\"", "\"50\"", StringComparison.Ordinal))).Event!;
        Assert.IsTrue(release.IsCancelled);
        string empty = """{"Head":{"EventID":"t1","ReportDateTime":"2026-09-21T12:00:00+09:00"},"Body":{"Tsunami":{"Forecast":{"Item":[]}}}}""";
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Raw("tsunami.forecast", empty)).Status);
        Assert.AreEqual(NormalizeStatus.Invalid, normalizer.Normalize(Raw("tsunami.forecast", Forecast.Replace("\"53\"", "\"99\"", StringComparison.Ordinal))).Status);
    }

    [TestMethod]
    public void ForecastAndObservationRemainSeparate()
    {
        const string observation = """
            {"Head":{"EventID":"t1","ReportDateTime":"2026-09-21T12:01:00+09:00"},
             "Body":{"Tsunami":{"Observation":{"Item":[{"Area":{"Name":"岩手県"},"Station":[
              {"Name":"宮古","MaxHeight":{"TsunamiHeight":"0.4","DateTime":"2026-09-21T12:00:00+09:00"}}]}]}}}}
            """;
        var state = new ExternalApiState();
        var normalizer = Normalizer();
        foreach (var message in new[] { Raw("tsunami.forecast", Forecast), Raw("tsunami.observation", observation) })
            state.Observe(new(EventIngestionStatus.Accepted, normalizer.Normalize(message).Event, null, null, []));
        Assert.AreEqual("VTSE41", state.Read(Now).Forecast!.TelegramType);
        Assert.AreEqual("CoastalObservation", state.Read(Now).Observation!.Areas.Single().Role);
    }

    [TestMethod]
    public void PublicEarthquakeApiUsesGenericFieldsAndExcludesTests()
    {
        var state = new ExternalEarthquakeState();
        var normalizer = Normalizer();
        state.Observe(new(EventIngestionStatus.Accepted, normalizer.Normalize(Raw("earthquake", Quake)).Event, null, null, []));
        state.Observe(new(EventIngestionStatus.Accepted, normalizer.Normalize(Raw("eew", Eew)).Event, null, null, []));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(state.ReadQuake()));
        Assert.AreEqual("5-", doc.RootElement.GetProperty("earthquake").GetProperty("earthquake").GetProperty("maximumIntensity").GetString());
        using var eew = JsonDocument.Parse(JsonSerializer.Serialize(state.ReadEew(Now.AddMinutes(11))));
        Assert.IsTrue(eew.RootElement.GetProperty("events")[0].GetProperty("isExpired").GetBoolean());
        Assert.IsFalse(eew.RootElement.GetProperty("events")[0].GetProperty("isCancelled").GetBoolean());
    }

    [TestMethod]
    public void ProviderSelectionIsPerInformationAndOnlyOneBridgeIsSelected()
    {
        var routing = ProviderRoutingSettings.Default with { Eew = ReceptionProvider.ObsEarthquakeBridge, Quake = ReceptionProvider.ObsEarthquakeBridge };
        Assert.AreEqual(1, routing.GetDistinctProviders().Count(p => p == ReceptionProvider.ObsEarthquakeBridge));
        var selected = new ProviderSelectionEventNormalizer(Normalizer(), AppSettings.CreateDefault().Provider with { Routing = routing });
        Assert.AreEqual(NormalizeStatus.Ignored, selected.Normalize(Raw("tsunami.forecast", Forecast)).Status);
        Assert.AreEqual(NormalizeStatus.Success, selected.Normalize(Raw("earthquake", Quake)).Status);
        var settings = new SettingsEditorViewModel(AppSettings.CreateDefault());
        Assert.IsTrue(settings.EarthquakeProviderOptions.Any(p => p.Value == ReceptionProvider.ObsEarthquakeBridge));
        Assert.IsFalse(settings.CommercialProviderOptions.Any(p => p.Value == ReceptionProvider.ObsEarthquakeBridge));
    }

    [TestMethod]
    public async Task TransportSubscribesBeforeSnapshotsAndStopsWithoutCancellationEvent()
    {
        var handler = new FakeBridge();
        await using var source = new ObsEarthquakeBridgeSource(new Clock(), new HttpClient(handler));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<RawProviderMessage>();
        await foreach (var message in source.ReadAllAsync(timeout.Token))
        {
            received.Add(message);
            if (received.Count == 3) break;
        }
        string[] expected = ["/api/v1/events", "/api/v1/status", "/api/v1/eew/current", "/api/v1/earthquake/current", "/api/v1/tsunami/current"];
        CollectionAssert.AreEqual(expected, handler.Paths.ToArray());
        Assert.IsTrue(received.Take(2).All(m => m.IsStateSnapshot));
        Assert.IsFalse(received[2].IsStateSnapshot);
        Assert.AreEqual(ProviderConnectionState.Stopped, source.Connection.State);
    }

    [TestMethod]
    public async Task V11TransportAcceptsSchemaLessSnapshotsAndStaleUpstream()
    {
        await using var source = new ObsEarthquakeBridgeSource(new Clock(), new HttpClient(new FakeBridge(true)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int count = 0;
        var normalizer = Normalizer();
        await foreach (var message in source.ReadAllAsync(timeout.Token))
        {
            Assert.AreEqual(NormalizeStatus.Success, normalizer.Normalize(message).Status);
            Assert.AreEqual(ProviderConnectionState.Stale, source.Connection.State);
            if (++count == 3) break;
        }
        Assert.AreEqual(3, count);
    }

    private sealed class FakeBridge(bool modern = false) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            string text = path switch
            {
                "/api/v1/events" => "data: " + JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(Raw("earthquake", Quake.Replace("\"2\"", "\"3\"", StringComparison.Ordinal)).Payload)) + "\n\n",
                "/api/v1/status" => """{"feeds":{"earthquake":{"connected":true}}}""",
                "/api/v1/eew/current" => """{"schema":"cdi.bridge.snapshot.v1","type":"eew","events":[]}""",
                "/api/v1/earthquake/current" => $$"""{"schema":"cdi.bridge.snapshot.v1","type":"earthquake","event":{{Quake}}}""",
                "/api/v1/tsunami/current" => $$"""{"schema":"cdi.bridge.snapshot.v1","type":"tsunami","forecast":{{Forecast}}}""",
                _ => throw new InvalidOperationException(path),
            };
            if (modern)
            {
                if (path.EndsWith("events", StringComparison.Ordinal)) text = text.Replace("cdi.bridge.event.v1", "obs-earthquake.bridge.event.v1", StringComparison.Ordinal);
                else if (path.EndsWith("status", StringComparison.Ordinal))
                    text = """{"apiVersion":"1.1","upstreamHealthy":false,"upstreamState":"degraded","feeds":{"earthquake":{"configured":true,"connected":true,"stale":true},"eew":{"configured":false,"connected":false}}}""";
                else
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
                    node.Remove("schema");
                    node.Remove("type");
                    text = node.ToJsonString();
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(text, Encoding.UTF8, path.EndsWith("events", StringComparison.Ordinal) ? "text/event-stream" : "application/json") });
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
