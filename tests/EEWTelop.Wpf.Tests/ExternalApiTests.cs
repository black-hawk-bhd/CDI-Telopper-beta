using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Obs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class ExternalApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void UnreceivedDoesNotMeanAllClearAndTrainingIsExcluded()
    {
        var store = new ExternalApiState();
        Assert.IsFalse(store.Read(Now).HasForecast);
        foreach (SourceMode mode in new[] { SourceMode.ManualTest, SourceMode.HistoryRehearsal, SourceMode.Sandbox })
            store.Observe(Result(Telegram(mode: mode)));
        Assert.IsNull(store.Read(Now).Forecast);
        store.Observe(Result(Telegram(), EventIngestionStatus.Duplicate));
        store.Observe(Result(Telegram(), EventIngestionStatus.Ignored));
        Assert.AreEqual(0L, store.Read(Now).Revision);
    }

    [TestMethod]
    public void LiveXmlTrainingStatusCannotEnterProductionApi()
    {
        const string xml = """
            <Report><Control><Title>津波警報・注意報・予報</Title><Status>訓練</Status></Control>
            <Head><ReportDateTime>2026-09-20T09:00:00+09:00</ReportDateTime><EventID>test-tsunami</EventID><InfoType>発表</InfoType></Head>
            <Body><Tsunami><Forecast><Item><Category><Name>津波警報</Name></Category><Area><Name>岩手県</Name></Area></Item></Forecast></Tsunami></Body></Report>
            """;
        var normalizer = new EEWTelop.Infrastructure.Dmdata.Normalization.JmaXmlEventNormalizer(new EventSignatureBuilder());
        var result = normalizer.Normalize(new("jma-xml", xml, SourceMode.Production, Now)
            { ContentFormat = RawProviderContentFormat.JmaXml });
        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        Assert.AreEqual(SourceMode.Sandbox, result.Event!.SourceMode);
        var store = new ExternalApiState();
        store.Observe(Result((TsunamiEvent)result.Event));
        Assert.IsFalse(store.Read(Now).HasForecast);
    }

    [TestMethod]
    public void ObservationCannotReplaceWarningAndMergedForecastIsStillForecast()
    {
        var store = new ExternalApiState();
        store.Observe(Result(Telegram()));
        store.Observe(Result(Telegram(type: "VTSE51", issued: Now.AddMinutes(1))));
        Assert.AreEqual("VTSE41", store.Read(Now).Forecast!.TelegramType);
        Assert.AreEqual("VTSE51", store.Read(Now).Observation!.TelegramType);
        Assert.AreEqual(0, store.Read(Now).Observation!.Item.Length);
        store.Observe(Result(Telegram(issued: Now.AddMinutes(2))));
        Assert.AreEqual(Now.AddMinutes(2), store.Read(Now).Forecast!.IssuedAt);
        Assert.IsTrue(store.Read(Now).Forecast!.Areas.All(a => a.Role == "ForecastArea"));
    }

    [TestMethod]
    public void ReleaseExpiryCancellationAndOlderTelegramAreDistinct()
    {
        var store = new ExternalApiState();
        store.Observe(Result(Telegram()));
        Assert.AreEqual("52", store.Read(Now).Forecast!.Item.Single().Kind.Code);
        Assert.IsTrue(store.Read(Now.AddHours(2)).Forecast!.IsExpired);
        Assert.AreEqual(0, store.Read(Now.AddHours(2)).Forecast!.Item.Length);
        store.Observe(Result(Telegram(cancelled: true, issued: Now.AddMinutes(2))));
        Assert.IsTrue(store.Read(Now).Forecast!.IsCancelled);
        Assert.IsFalse(store.Read(Now).Forecast!.IsTelegramCancellation);
        Assert.AreEqual(0, store.Read(Now).Forecast!.Item.Length);
        store.Observe(Result(Telegram()));
        Assert.IsTrue(store.Read(Now).Forecast!.IsCancelled);
        store.Observe(Result(Telegram(cancelled: true, issued: Now.AddMinutes(3), infoType: "取消")));
        Assert.IsTrue(store.Read(Now).Forecast!.IsTelegramCancellation);
    }

    [TestMethod]
    public async Task ApiIsOptInAuthenticatedReadOnlyAndSeparateFromObsToken()
    {
        await using var server = CreateServer();
        await server.StartAsync(0);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string root = $"http://127.0.0.1:{server.Port}";
        using var disabled = await http.GetAsync(root + "/api/v1/status");
        Assert.AreEqual(HttpStatusCode.NotFound, disabled.StatusCode);
        server.ExternalApiEnabled = true;
        using var missing = await http.GetAsync(root + "/api/v1/status");
        Assert.AreEqual(HttpStatusCode.Forbidden, missing.StatusCode);
        using var wrongToken = await http.GetAsync(root + "/api/v1/status" + new Uri(server.OverlayUrl).Query);
        Assert.AreEqual(HttpStatusCode.Forbidden, wrongToken.StatusCode);
        string url = server.ExternalApiUrl;
        using var status = JsonDocument.Parse(await http.GetStringAsync(url));
        Assert.AreEqual("1", status.RootElement.GetProperty("apiVersion").GetString());
        Assert.IsTrue(status.RootElement.GetProperty("readOnly").GetBoolean());
        using var post = await http.PostAsync(url, new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        using var tsunami = JsonDocument.Parse(await http.GetStringAsync(url.Replace("/status?", "/tsunami?", StringComparison.Ordinal)));
        Assert.IsFalse(tsunami.RootElement.GetProperty("tsunami").GetProperty("hasForecast").GetBoolean());
        using var earthquake = JsonDocument.Parse(await http.GetStringAsync(url.Replace("/status?", "/earthquake?", StringComparison.Ordinal)));
        Assert.IsFalse(earthquake.RootElement.GetProperty("earthquake").GetProperty("hasInformation").GetBoolean());
        using var eew = JsonDocument.Parse(await http.GetStringAsync(url.Replace("/status?", "/eew?", StringComparison.Ordinal)));
        Assert.AreEqual(0, eew.RootElement.GetProperty("eew").GetProperty("events").GetArrayLength());
        server.ExternalApiEnabled = false;
        server.ExternalApiEnabled = true;
        using var old = await http.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.Forbidden, old.StatusCode);
    }

    [TestMethod]
    public async Task BearerTokenWorksButRemoteOriginAndNullOriginAreRejected()
    {
        await using var server = CreateServer();
        await server.StartAsync(0);
        server.ExternalApiEnabled = true;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var uri = new Uri(server.ExternalApiUrl);
        using var bearer = new HttpRequestMessage(HttpMethod.Get, uri.GetLeftPart(UriPartial.Path));
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uri.Query[7..]);
        using var allowed = await http.SendAsync(bearer);
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        foreach (string origin in new[] { "https://example.com", "null" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("Origin", origin);
            using var response = await http.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [TestMethod]
    public async Task WebSocketSendsInitialSnapshotAndStopsWhenDisabled()
    {
        await using var server = CreateServer();
        await server.StartAsync(0);
        server.ExternalApiEnabled = true;
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var uri = new UriBuilder(server.ExternalApiUrl) { Scheme = "ws", Path = "/api/v1/events" }.Uri;
        await socket.ConnectAsync(uri, timeout.Token);
        byte[] buffer = new byte[8192];
        var received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count));
        Assert.AreEqual("snapshot", json.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(1L, json.RootElement.GetProperty("sequence").GetInt64());
        Assert.IsTrue(json.RootElement.TryGetProperty("earthquake", out _));
        Assert.IsTrue(json.RootElement.TryGetProperty("eew", out _));
        Assert.AreEqual(0, server.ClientCount); // API clients do not masquerade as OBS/audio clients.
        server.ExternalApiEnabled = false;
        try
        {
            var close = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            Assert.AreEqual(WebSocketMessageType.Close, close.MessageType);
        }
        catch (WebSocketException) { }
    }

    private static ObsLocalViewServer CreateServer() => new(
        new ObsSnapshotStore(AppSettings.CreateDefault().Display, Now), new Clock(), new UiLogBuffer());

    [TestMethod]
    public async Task ServerCanStopWithIncompleteApiRequest()
    {
        await using var server = CreateServer();
        await server.StartAsync(0);
        server.ExternalApiEnabled = true;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /api/v1/status HTTP/1.1\r\n"));
        await Task.Delay(50);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await server.StopAsync(timeout.Token);
        Assert.IsFalse(server.IsRunning);
    }

    [TestMethod]
    public async Task ReceptionUpdatesHttpAndWebSocketEvenWhenSubtitleIsFiltered()
    {
        var settings = AppSettings.CreateDefault();
        var clock = new Clock();
        var normalizer = new FixedNormalizer();
        var pipeline = new EventIngestionPipeline(normalizer, new EventVersionCache(),
            new PageComposer(), new PriorityCoordinator(clock, settings.Display), settings.Display,
            new FilterSettings(true, true, false));
        var reception = new EventReceptionService(new IdleSource(), pipeline);
        await using var server = new ObsLocalViewServer(new ObsSnapshotStore(settings.Display, Now),
            clock, new UiLogBuffer(), receptionService: reception);
        await server.StartAsync(0);
        server.ExternalApiEnabled = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new UriBuilder(server.ExternalApiUrl)
            { Scheme = "ws", Path = "/api/v1/events" }.Uri, timeout.Token);
        byte[] buffer = new byte[16384];
        await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        EventIngestionResult result = await reception.ProcessAsync(new("jma-xml", "{}", SourceMode.Production, Now));
        Assert.IsNull(result.Program);
        var update = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, update.Count));
        Assert.AreEqual("update", json.RootElement.GetProperty("type").GetString());
        Assert.IsTrue(json.RootElement.GetProperty("tsunami").GetProperty("hasForecast").GetBoolean());
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var state = JsonDocument.Parse(await http.GetStringAsync(
            server.ExternalApiUrl.Replace("/status?", "/tsunami?", StringComparison.Ordinal)));
        Assert.AreEqual("52", state.RootElement.GetProperty("tsunami").GetProperty("forecast")
            .GetProperty("item")[0].GetProperty("kind").GetProperty("code").GetString());
        socket.Abort();
    }

    [TestMethod]
    public void RehearsalFailureRetainsStateAndResetRejectsOldSession()
    {
        var store = new ExternalRehearsalState();
        string session = store.Begin("simulator");
        store.Observe(Telegram(mode: SourceMode.ManualTest), session);
        store.SetHealth(session, "failed");
        var failed = store.Read(Now);
        Assert.IsTrue(failed["rehearsal"]!["active"]!.GetValue<bool>());
        Assert.IsTrue(failed["tsunami"]!["hasForecast"]!.GetValue<bool>());
        Assert.AreEqual("failed", failed["dataHealth"]!["tsunami"]!["state"]!.GetValue<string>());
        long revision = failed["tsunami"]!["revision"]!.GetValue<long>();
        string next = store.Begin("simulator");
        Assert.AreNotEqual(session, next);
        store.Observe(Telegram(mode: SourceMode.ManualTest), session);
        store.SetHealth(session, "failed");
        store.End(session);
        var reset = store.Read(Now);
        Assert.IsTrue(reset["rehearsal"]!["active"]!.GetValue<bool>());
        Assert.IsFalse(reset["tsunami"]!["hasForecast"]!.GetValue<bool>());
        Assert.IsTrue(reset["tsunami"]!["revision"]!.GetValue<long>() > revision);
        store.End(next);
        Assert.IsFalse(store.Read(Now)["rehearsal"]!["active"]!.GetValue<bool>());
    }

    [TestMethod]
    [DataRow(SourceMode.ManualTest, "manualTest")]
    [DataRow(SourceMode.Sandbox, "sandbox")]
    [DataRow(SourceMode.HistoryRehearsal, "historyRehearsal")]
    public void RehearsalSourcesStayOutOfProduction(SourceMode source, string expected)
    {
        var live = new ExternalApiState();
        var rehearsal = new ExternalRehearsalState();
        var item = Telegram(mode: source);
        live.Observe(Result(item));
        rehearsal.Observe(item);
        Assert.IsFalse(live.Read(Now).HasForecast);
        Assert.AreEqual(expected, rehearsal.Read(Now)["tsunami"]!["forecast"]!["sourceMode"]!.GetValue<string>());
        Assert.AreEqual(source, item.SourceMode);
    }

    [TestMethod]
    public void OnlyExplicitManualReplayCanCopyProductionIntoRehearsal()
    {
        var rehearsal = new ExternalRehearsalState();
        var live = Telegram();
        rehearsal.Observe(live);
        Assert.IsFalse(rehearsal.Read(Now)["tsunami"]!["hasForecast"]!.GetValue<bool>());
        rehearsal.Observe(live, manualReplay: true);
        Assert.AreEqual("manualReplay", rehearsal.Read(Now)["tsunami"]!["forecast"]!["sourceMode"]!.GetValue<string>());
        Assert.AreEqual(SourceMode.Production, live.SourceMode);
        Assert.AreEqual("signature", live.Signature);
    }

    [TestMethod]
    public void SimulatorFullSnapshotRemovesAbsentResourcesWithoutEndingSession()
    {
        var rehearsal = new ExternalRehearsalState();
        string session = rehearsal.Begin("simulator");
        rehearsal.Observe(Telegram(mode: SourceMode.ManualTest), session);
        rehearsal.ReplaceSnapshot(session, []);
        var state = rehearsal.Read(Now);
        Assert.IsFalse(state["tsunami"]!["hasForecast"]!.GetValue<bool>());
        Assert.AreEqual("unknown", state["tsunami"]!["forecastState"]!.GetValue<string>());
        Assert.IsTrue(state["rehearsal"]!["active"]!.GetValue<bool>());
    }

    [TestMethod]
    public void HealthTracksRouteNotDisasterAge()
    {
        var health = new ExternalApiHealth();
        Assert.AreEqual("unknown", health.Read(EventKind.Tsunami, true, null).State);
        Assert.AreEqual("connecting", health.Read(EventKind.Tsunami, true, ProviderConnectionState.Connecting).State);
        health.Observe(Result(Telegram(issued: Now.AddYears(-1))));
        var connected = health.Read(EventKind.Tsunami, true, ProviderConnectionState.Connected);
        Assert.AreEqual("healthy", connected.State);
        Assert.AreEqual(Now.AddYears(-1), connected.LastAcceptedAt);
        Assert.AreEqual("stale", health.Read(EventKind.Tsunami, true, ProviderConnectionState.Reconnecting).State);
        Assert.AreEqual("failed", health.Read(EventKind.Tsunami, true, ProviderConnectionState.Faulted).State);
        Assert.AreEqual("disabled", health.Read(EventKind.Tsunami, false, ProviderConnectionState.Connected).State);
        Assert.IsNull(health.Read(EventKind.Eew, true, ProviderConnectionState.Connected).LastAcceptedAt);
    }

    [TestMethod]
    public async Task RehearsalAuthenticationAndRevocationAreIndependent()
    {
        await using var server = CreateServer();
        await server.StartAsync(0);
        server.ExternalApiEnabled = true;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string root = $"http://127.0.0.1:{server.Port}";
        using var disabled = await http.GetAsync(root + "/api/v1/rehearsal/status");
        Assert.AreEqual(HttpStatusCode.NotFound, disabled.StatusCode);
        server.RehearsalApiEnabled = true;
        string rehearsalUrl = server.RehearsalApiUrl;
        using var wrongLive = await http.GetAsync(root + "/api/v1/status" + new Uri(rehearsalUrl).Query);
        using var wrongTraining = await http.GetAsync(root + "/api/v1/rehearsal/status" + new Uri(server.ExternalApiUrl).Query);
        using var obs = await http.GetAsync(root + "/api/v1/rehearsal/status" + new Uri(server.OverlayUrl).Query);
        Assert.AreEqual(HttpStatusCode.Forbidden, wrongLive.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, wrongTraining.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, obs.StatusCode);
        using var status = JsonDocument.Parse(await http.GetStringAsync(rehearsalUrl));
        Assert.IsTrue(status.RootElement.GetProperty("isTraining").GetBoolean());
        Assert.AreEqual("rehearsal", status.RootElement.GetProperty("channel").GetString());
        server.BeginApiRehearsal("manualTest");
        server.ObserveApiRehearsal(Telegram(mode: SourceMode.ManualTest));
        using var production = JsonDocument.Parse(await http.GetStringAsync(server.ExternalApiUrl.Replace("/status?", "/tsunami?", StringComparison.Ordinal)));
        Assert.IsFalse(production.RootElement.GetProperty("tsunami").GetProperty("hasForecast").GetBoolean());
        server.RehearsalApiEnabled = false;
        using var off = await http.GetAsync(rehearsalUrl);
        Assert.AreEqual(HttpStatusCode.NotFound, off.StatusCode);
        server.RehearsalApiEnabled = true;
        using var revoked = await http.GetAsync(rehearsalUrl);
        Assert.AreEqual(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var liveStillEnabled = await http.GetAsync(server.ExternalApiUrl);
        Assert.AreEqual(HttpStatusCode.OK, liveStillEnabled.StatusCode);
    }

    [TestMethod]
    public async Task RehearsalWebSocketNotifiesFailureAndExplicitEnd()
    {
        await using var server = CreateServer();
        await server.StartAsync(0);
        server.RehearsalApiEnabled = true;
        string session = server.BeginApiRehearsal("simulator")!;
        server.ObserveApiRehearsal(Telegram(mode: SourceMode.ManualTest), session);
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri(server.RehearsalApiUrl.Replace("http:", "ws:", StringComparison.Ordinal)
            .Replace("/status?", "/events?", StringComparison.Ordinal)), timeout.Token);
        async Task<JsonDocument> Receive()
        {
            byte[] bytes = new byte[65536];
            var received = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
            Assert.IsTrue(received.EndOfMessage);
            return JsonDocument.Parse(bytes.AsMemory(0, received.Count));
        }
        using var first = await Receive();
        Assert.AreEqual("snapshot", first.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(session, first.RootElement.GetProperty("rehearsalSessionId").GetString());
        server.SetApiRehearsalHealth(session, "failed");
        using var failed = await Receive();
        Assert.AreEqual("update", failed.RootElement.GetProperty("type").GetString());
        Assert.IsTrue(failed.RootElement.GetProperty("rehearsal").GetProperty("active").GetBoolean());
        Assert.AreEqual("failed", failed.RootElement.GetProperty("status").GetProperty("dataHealth").GetProperty("tsunami").GetProperty("state").GetString());
        server.EndApiRehearsal(session);
        using var ended = await Receive();
        Assert.IsFalse(ended.RootElement.GetProperty("rehearsal").GetProperty("active").GetBoolean());
        Assert.IsTrue(ended.RootElement.GetProperty("sequence").GetInt64() > failed.RootElement.GetProperty("sequence").GetInt64());
        server.RehearsalApiEnabled = false;
        try { await socket.ReceiveAsync(new byte[256].AsMemory(), timeout.Token); }
        catch (WebSocketException) { }
        Assert.IsFalse(timeout.IsCancellationRequested);
    }

    [TestMethod]
    public async Task RehearsalNeverStartsProductionInitialization()
    {
        int calls = 0;
        await using var server = new ObsLocalViewServer(new ObsSnapshotStore(AppSettings.CreateDefault().Display, Now),
            new Clock(), new UiLogBuffer(), initialTsunamiFetcher: _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Telegram());
            });
        server.RehearsalApiEnabled = true;
        await server.StartAsync(0);
        server.BeginApiRehearsal("manualTest");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var state = await http.GetAsync(server.RehearsalApiUrl);
        Assert.AreEqual(HttpStatusCode.OK, state.StatusCode);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task DomainHealthChangeEmitsUpdateWhileAggregateIsUnchanged()
    {
        var settings = AppSettings.CreateDefault();
        var quake = new IdleSource();
        var tsunami = new IdleSource();
        var providers = new Dictionary<ReceptionProvider, IEventSource>
        {
            [ReceptionProvider.P2pQuake] = quake, [ReceptionProvider.Axis] = tsunami,
        };
        await using var routed = new RoutedProviderEventSource(settings.Provider with
        {
            JmaXmlAutoFallback = false,
            Routing = ProviderRoutingSettings.FromLegacy(ReceptionProvider.Disabled) with
                { Quake = ReceptionProvider.P2pQuake, Tsunami = ReceptionProvider.Axis },
        }, providers);
        var clock = new Clock();
        var pipeline = new EventIngestionPipeline(new FixedNormalizer(), new EventVersionCache(),
            new PageComposer(), new PriorityCoordinator(clock, settings.Display), settings.Display);
        var reception = new EventReceptionService(routed, pipeline);
        await using var server = new ObsLocalViewServer(new ObsSnapshotStore(settings.Display, Now), clock,
            new UiLogBuffer(), receptionService: reception);
        await server.StartAsync(0);
        server.ExternalApiEnabled = true;
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await socket.ConnectAsync(new UriBuilder(server.ExternalApiUrl) { Scheme = "ws", Path = "/api/v1/events" }.Uri, timeout.Token);
        byte[] buffer = new byte[32768];
        await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        var aggregate = reception.Connection;
        tsunami.Connection = new(ProviderConnectionState.Faulted, Now);
        Assert.AreEqual(aggregate, reception.Connection);
        var message = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        using var result = JsonDocument.Parse(buffer.AsMemory(0, message.Count));
        Assert.AreEqual("update", result.RootElement.GetProperty("type").GetString());
        var health = result.RootElement.GetProperty("status").GetProperty("dataHealth");
        Assert.AreEqual("failed", health.GetProperty("tsunami").GetProperty("state").GetString());
        Assert.AreEqual("healthy", health.GetProperty("earthquake").GetProperty("state").GetString());
        Assert.AreEqual("disabled", health.GetProperty("eew").GetProperty("state").GetString());
        socket.Abort();
    }

    private sealed class FixedNormalizer : IEventNormalizer
    {
        public NormalizeResult Normalize(RawProviderMessage raw) => NormalizeResult.Success(Telegram());
    }

    private sealed class IdleSource : IEventSource
    {
        public ProviderConnectionSnapshot Connection { get; set; } = new(ProviderConnectionState.Connected, Now);
        public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged { add { } remove { } }
        public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void RequestReconnect(ReconnectReason reason) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static EventIngestionResult Result(TsunamiEvent value,
        EventIngestionStatus status = EventIngestionStatus.Accepted) => new(status, value, null, null, []);

    private static TsunamiEvent Telegram(SourceMode mode = SourceMode.Production,
        string type = "VTSE41", DateTimeOffset? issued = null, bool cancelled = false, string infoType = "発表") => new(
            EventId.Create("tsunami-1"), "jma-xml", issued ?? Now, issued ?? Now, "signature", mode,
            new IssueInfo("気象庁", issued ?? Now, type, CorrectionType.None, null, infoType),
            [new(TsunamiGrade.MajorWarning, false, "岩手県", null, null),
             new(TsunamiGrade.Unknown, false, "宮古", null, new("1m", 1))
             { Role = TsunamiInformationRole.CoastalObservation, ParentAreaName = "岩手県" }],
            cancelled, Now.AddHours(1));

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
