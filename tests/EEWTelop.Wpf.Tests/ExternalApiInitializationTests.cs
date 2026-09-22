using System.Net;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Obs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class ExternalApiInitializationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 1, 0, 0, TimeSpan.Zero);
    private const string Empty = """{"reportDateTime":"2026/09/20 09:00","item":[]}""";
    private const string Active = """{"reportDateTime":"2026/09/20 09:00","item":[{"area":{"name":"岩手県"},"kind":{"code":"52"}}]}""";
    private static TsunamiEvent Parse(string text) => JmaTsunamiSnapshotClient.Parse(Encoding.UTF8.GetBytes(text), Now);

    [TestMethod]
    public void StartupSnapshotDistinguishesLoadingFailureUnknownAndNoWarning()
    {
        var state = new ExternalApiState();
        Assert.AreEqual("unknown", state.Read(Now).ForecastState);
        var ticket = state.BeginInitialization(Now);
        Assert.AreEqual("loading", state.Read(Now).Initialization.State);
        state.FailInitialization(ticket.Id, Now);
        Assert.AreEqual("failed", state.Read(Now).Initialization.State);
        Assert.IsNull(state.Read(Now).Forecast);
        ticket = state.BeginInitialization(Now);
        state.CompleteInitialization(ticket, Parse(Empty), Now);
        Assert.AreEqual("ready", state.Read(Now).Initialization.State);
        Assert.AreEqual("inactive", state.Read(Now).ForecastState);
        Assert.AreEqual("startupSnapshot", state.Read(Now).ForecastOrigin);
        Assert.IsTrue(state.Read(Now).HasForecast);
    }

    [TestMethod]
    public void LiveForecastArrivingDuringFetchAlwaysWins()
    {
        var state = new ExternalApiState();
        var ticket = state.BeginInitialization(Now);
        TsunamiEvent live = Parse(Active);
        state.Observe(new(EventIngestionStatus.Accepted, live, null, null, []));
        state.CompleteInitialization(ticket, Parse(Empty.Replace("09:00", "10:00", StringComparison.Ordinal)), Now);
        Assert.AreEqual("live", state.Read(Now).ForecastOrigin);
        Assert.AreEqual("active", state.Read(Now).ForecastState);
        Assert.IsFalse(state.Read(Now).Initialization.Applied);
    }

    [TestMethod]
    public void OlderInitialSnapshotAndCancelledAttemptCannotOverwriteState()
    {
        var state = new ExternalApiState();
        state.Observe(new(EventIngestionStatus.Accepted, Parse(Active), null, null, []));
        var ticket = state.BeginInitialization(Now);
        state.CompleteInitialization(ticket, Parse(Empty.Replace("09:00", "08:00", StringComparison.Ordinal)), Now);
        Assert.AreEqual("active", state.Read(Now).ForecastState);
        ticket = state.BeginInitialization(Now);
        state.CancelInitialization();
        state.CompleteInitialization(ticket, Parse(Empty), Now);
        state.FailInitialization(ticket.Id, Now);
        Assert.AreEqual("cancelled", state.Read(Now).Initialization.State);
        Assert.AreEqual("active", state.Read(Now).ForecastState);
    }

    [TestMethod]
    public void InitialSnapshotValidatesInsteadOfTreatingInvalidDataAsAllClear()
    {
        foreach (string invalid in new[] { "{}", "{\"item\":null}", Empty.Replace("09:00", "bad", StringComparison.Ordinal),
            Active.Replace("52", "999", StringComparison.Ordinal), Active.Replace("岩手県", "", StringComparison.Ordinal),
            Empty.Replace("2026/09/20", "2099/09/20", StringComparison.Ordinal) })
            Assert.ThrowsExactly<InvalidDataException>(() => Parse(invalid));
        Assert.AreEqual(TsunamiGrade.Warning, Parse(Active.Replace("52", "51", StringComparison.Ordinal)).Areas.Single().Grade);
        Assert.AreEqual(TsunamiGrade.Watch, Parse(Active.Replace("52", "62", StringComparison.Ordinal)).Areas.Single().Grade);
        Assert.AreEqual(TsunamiGrade.MajorWarning, Parse(Active.Replace("52", "53", StringComparison.Ordinal)).Areas.Single().Grade);
        Assert.IsTrue(Parse(Active.Replace("52", "50", StringComparison.Ordinal)).IsCancelled);
    }

    [TestMethod]
    public async Task FetchUsesNoCacheAndRejectsHttpErrorsAndStaleCache()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        TsunamiEvent value = await JmaTsunamiSnapshotClient.FetchAsync(http, CancellationToken.None);
        Assert.AreEqual("jma-current-json", value.Provider);
        Assert.IsTrue(handler.NoCache);
        handler.Status = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => JmaTsunamiSnapshotClient.FetchAsync(http, CancellationToken.None));
        handler.Status = HttpStatusCode.OK;
        handler.Age = TimeSpan.FromMinutes(5);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => JmaTsunamiSnapshotClient.FetchAsync(http, CancellationToken.None));
    }

    [TestMethod]
    public async Task EnableInitializesOnlyApiWithoutSubtitleOrAudioAndCanRestart()
    {
        var settings = AppSettings.CreateDefault();
        var snapshots = new ObsSnapshotStore(settings.Display, Now);
        var result = new TaskCompletionSource<TsunamiEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using var server = new ObsLocalViewServer(snapshots, new Clock(), new UiLogBuffer(),
            initialTsunamiFetcher: async token => { Interlocked.Increment(ref calls); return await result.Task.WaitAsync(token); });
        await server.StartAsync(0);
        Assert.AreEqual(0, calls);
        server.ExternalApiEnabled = true;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var loading = await Read(http, server);
        Assert.AreEqual("loading", loading.RootElement.GetProperty("tsunami").GetProperty("initialization").GetProperty("state").GetString());
        result.SetResult(Parse(Active));
        await WaitState(http, server, "ready");
        using var ready = await Read(http, server);
        Assert.AreEqual("active", ready.RootElement.GetProperty("tsunami").GetProperty("forecastState").GetString());
        Assert.AreEqual("", server.LastAudioCue);
        using var overlay = JsonDocument.Parse(await http.GetStringAsync(
            new Uri(server.OverlayUrl).GetLeftPart(UriPartial.Authority) + "/state" + new Uri(server.OverlayUrl).Query));
        // The initializer does not have a reception pipeline or coordinator; only the API snapshot changes.
        Assert.IsFalse(overlay.RootElement.GetProperty("hasProgram").GetBoolean());
        Assert.AreEqual(0L, overlay.RootElement.GetProperty("audioSequence").GetInt64());
        server.ExternalApiEnabled = false;
        server.ExternalApiEnabled = true;
        await WaitState(http, server, "ready");
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task FailedInitialFetchIsVisibleWithoutInventingNoWarning()
    {
        await using var server = new ObsLocalViewServer(new ObsSnapshotStore(AppSettings.CreateDefault().Display, Now),
            new Clock(), new UiLogBuffer(), initialTsunamiFetcher: _ => throw new HttpRequestException("test"));
        await server.StartAsync(0);
        server.ExternalApiEnabled = true;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await WaitState(http, server, "failed");
        using var failed = await Read(http, server);
        Assert.IsFalse(failed.RootElement.GetProperty("tsunami").GetProperty("hasForecast").GetBoolean());
    }

    private static async Task<JsonDocument> Read(HttpClient http, ObsLocalViewServer server) =>
        JsonDocument.Parse(await http.GetStringAsync(server.ExternalApiUrl.Replace("/status?", "/tsunami?", StringComparison.Ordinal)));

    private static async Task WaitState(HttpClient http, ObsLocalViewServer server, string expected)
    {
        for (int i = 0; i < 100; i++)
        {
            using var value = await Read(http, server);
            if (value.RootElement.GetProperty("tsunami").GetProperty("initialization").GetProperty("state").GetString() == expected) return;
            await Task.Delay(20);
        }
        Assert.Fail("Initialization did not reach " + expected);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public TimeSpan? Age { get; set; }
        public bool NoCache { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            NoCache = request.Headers.CacheControl?.NoCache == true;
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Active) };
            response.Headers.Age = Age;
            return Task.FromResult(response);
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
