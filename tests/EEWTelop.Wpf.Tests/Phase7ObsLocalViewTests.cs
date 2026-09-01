using System.Net;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Obs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class Phase7ObsLocalViewTests
{
    [TestMethod]
    public void ObsMixerControlsOnlyTheGeneralAudioSource()
    {
        Assert.IsTrue(ObsBrowserSourceSynchronizer.ShouldControlAudio(
            "CDI-Telopper 地震字幕・全ての音声"));
        Assert.IsFalse(ObsBrowserSourceSynchronizer.ShouldControlAudio(
            "CDI-Telopper 緊急地震速報"));
        Assert.IsFalse(ObsBrowserSourceSynchronizer.ShouldControlAudio(
            "CDI-Telopper 津波字幕"));
        Assert.IsFalse(ObsBrowserSourceSynchronizer.ShouldControlAudio(
            "CDI-Telopper 気象情報"));
        Assert.IsFalse(ObsBrowserSourceSynchronizer.ShouldControlAudio(
            "QTelopper 地震字幕・全ての音声"));
    }

    [TestMethod]
    public async Task ObsSnapshotIntervalCanBeChangedWithinSupportedRange()
    {
        AppSettings settings = AppSettings.CreateDefault();
        await using var server = new ObsLocalViewServer(
            new ObsSnapshotStore(settings.Display, DateTimeOffset.UtcNow),
            new TestClock(),
            new UiLogBuffer());

        Assert.AreEqual(1000, server.SnapshotIntervalMilliseconds);

        server.UpdateSnapshotInterval(50);

        Assert.AreEqual(50, server.SnapshotIntervalMilliseconds);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            server.UpdateSnapshotInterval(49));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            server.UpdateSnapshotInterval(1001));
    }

    [TestMethod]
    public void ObsWebSocketAuthenticationUsesProtocolV5HashSequence()
    {
        string authentication = ObsWebSocketAuthentication.Create(
            "password",
            "salt",
            "challenge");

        Assert.AreEqual("zTM5ki6L2vVvBQiTG9ckH1Lh64AbnCf6XZ226UmnkIA=", authentication);
    }

    [TestMethod]
    public async Task DedicatedObsUrlsServeIndependentTextViews()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var clock = new TestClock();
        var store = new ObsSnapshotStore(settings.Display, clock.UtcNow);
        var coordinator = new PriorityCoordinator(clock, settings.Display);
        var eewProgram = new DisplayProgram(
            "eew-route", EventId.Create("eew-route-event"), EventKind.Eew,
            SourceMode.Production, clock.UtcNow, OverlayPriority.Eew,
            [new DisplayPage(0, [new DisplayBlock("警報", "緊急地震速報", "", DisplayStyleTokens.EewWarning)], "", null)],
            clock.UtcNow, EndPolicy.Manual, string.Empty);
        store.Publish(coordinator.Apply(eewProgram), settings.Display, clock.UtcNow);
        var weatherEvent = new WeatherWarningEvent(
            EventId.Create("weather-route-event"),
            "axis",
            clock.UtcNow,
            clock.UtcNow,
            "weather-route-signature",
            SourceMode.Production,
            new IssueInfo("熊本地方気象台", clock.UtcNow, "VPWW55", CorrectionType.None, null),
            "熊本県では大雨に警戒してください。",
            [new WeatherWarningItem(
                "熊本市",
                "4310000",
                "大雨警報",
                "03",
                WeatherWarningLevel.Warning,
                "発表",
                true)],
            false);
        var weatherProgram = new DisplayProgram(
            "weather-route", weatherEvent.Id, EventKind.WeatherWarning,
            SourceMode.Production, clock.UtcNow, OverlayPriority.WeatherWarning,
            [new DisplayPage(0, [new DisplayBlock("大雨警報", "熊本市", "新たに発表", DisplayStyleTokens.WeatherWarning)], "", null)],
            clock.UtcNow, EndPolicy.AutoHide, string.Empty);
        store.PublishProgram(weatherEvent, weatherProgram, settings.Display, clock.UtcNow);

        await using var server = new ObsLocalViewServer(store, clock, new UiLogBuffer());
        await server.StartAsync(0);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        Assert.AreEqual("/eew/", new Uri(server.EewUrl).AbsolutePath);
        Assert.AreEqual("/tsunami/", new Uri(server.TsunamiUrl).AbsolutePath);
        Assert.AreEqual("/weather/", new Uri(server.WeatherUrl).AbsolutePath);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(server.EewUrl)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(server.TsunamiUrl)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(server.WeatherUrl)).StatusCode);

        string tokenQuery = new Uri(server.OverlayUrl).Query;
        using JsonDocument general = JsonDocument.Parse(await client.GetStringAsync(
            $"http://127.0.0.1:{server.Port}/state{tokenQuery}&view=general"));
        using JsonDocument eew = JsonDocument.Parse(await client.GetStringAsync(
            $"http://127.0.0.1:{server.Port}/state{tokenQuery}&view=eew"));
        using JsonDocument weather = JsonDocument.Parse(await client.GetStringAsync(
            $"http://127.0.0.1:{server.Port}/state{tokenQuery}&view=weather"));
        Assert.IsFalse(general.RootElement.GetProperty("hasProgram").GetBoolean());
        Assert.IsTrue(eew.RootElement.GetProperty("hasProgram").GetBoolean());
        Assert.AreEqual("Eew", eew.RootElement.GetProperty("kind").GetString());
        Assert.IsTrue(weather.RootElement.GetProperty("hasProgram").GetBoolean());
        Assert.AreEqual("WeatherWarning", weather.RootElement.GetProperty("kind").GetString());

    }

    [TestMethod]
    public async Task ServerRequiresTokenValidatesHostAndEmitsSecurityPolicy()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var clock = new TestClock();
        var store = new ObsSnapshotStore(settings.Display, clock.UtcNow);
        await using var server = new ObsLocalViewServer(store, clock, new UiLogBuffer());
        await server.StartAsync(0);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var unauthorized = await client.GetAsync($"http://127.0.0.1:{server.Port}/overlay/");
        Assert.AreEqual(HttpStatusCode.Forbidden, unauthorized.StatusCode);

        using var wrongHostRequest = new HttpRequestMessage(HttpMethod.Get, server.OverlayUrl);
        wrongHostRequest.Headers.Host = "example.com";
        var wrongHost = await client.SendAsync(wrongHostRequest);
        Assert.AreEqual(HttpStatusCode.Forbidden, wrongHost.StatusCode);

        var overlay = await client.GetAsync(server.OverlayUrl);
        string html = await overlay.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, overlay.StatusCode);
        Assert.IsTrue(overlay.Headers.TryGetValues("Content-Security-Policy", out IEnumerable<string>? policies));
        string policy = string.Join(" ", policies ?? []);
        StringAssert.Contains(policy, "default-src 'none'");
        StringAssert.Contains(policy, "script-src 'self'");
        StringAssert.Contains(policy, "connect-src 'self'");
        StringAssert.Contains(policy, "media-src 'self'");
        StringAssert.Contains(html, "background: transparent");
        StringAssert.Contains(html, "white-space: nowrap");
        StringAssert.Contains(html, "<div id=\"pageIndicator\" hidden>");
        StringAssert.Contains(html, "<audio id=\"alertAudio\"");

        string script = await client.GetStringAsync($"http://127.0.0.1:{server.Port}/assets/overlay.js");
        StringAssert.Contains(script, "textContent");
        StringAssert.Contains(script, "pageIndicator.hidden = !indicatorText");
        StringAssert.Contains(script, "new EventSource");
        StringAssert.Contains(script, "alertAudio.play()");
        StringAssert.Contains(script, "const handlesAudio = view === \"general\"");
        StringAssert.Contains(script, "if (handlesAudio) applyAudioCommand(state)");
        StringAssert.Contains(script, "alertAudio.volume = 1");
        StringAssert.Contains(script, "`/audio/${sequence}?token=");
        StringAssert.Contains(script, "/audio-status?");
        StringAssert.Contains(script, "reportAudioResult(sequence, \"Completed\")");
        StringAssert.Contains(script, "style === \"correction\"");
        StringAssert.Contains(script, "[\"#08050a\", \"#fff\"]");
        StringAssert.Contains(script, "[\"#8f1aa6\", \"#fff\"]");
        StringAssert.Contains(script, "badge.style.outline = \"2px solid #d9d9d9\"");
        Assert.IsFalse(script.Contains("innerHTML", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("eval(", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AudioPlaybackResultIsAuthenticatedRecordedAndLogged()
    {
        string audioPath = Path.Combine(Path.GetTempPath(), $"qtelopper-obs-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(audioPath, [0x49, 0x44, 0x33]);
        try
        {
            var clock = new TestClock();
            var store = new ObsSnapshotStore(AppSettings.CreateDefault().Display, clock.UtcNow);
            ObsViewSnapshot audio = store.PublishAudio("EewInitial", audioPath, clock.UtcNow);
            var logs = new UiLogBuffer();
            await using var server = new ObsLocalViewServer(store, clock, logs);
            await server.StartAsync(0);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            using HttpResponseMessage unauthorized = await client.PostAsync(
                $"http://127.0.0.1:{server.Port}/audio-status?sequence={audio.AudioSequence}&result=Started",
                content: null);
            Assert.AreEqual(HttpStatusCode.Forbidden, unauthorized.StatusCode);

            string tokenQuery = new Uri(server.OverlayUrl).Query;
            using HttpResponseMessage accepted = await client.PostAsync(
                $"http://127.0.0.1:{server.Port}/audio-status{tokenQuery}&sequence={audio.AudioSequence}&result=Completed",
                content: null);

            Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
            Assert.AreEqual("EewInitial", server.LastAudioCue);
            Assert.AreEqual("Completed", server.LastAudioPlaybackResult);
            Assert.IsNotNull(server.LastAudioPlaybackAtUtc);
            Assert.IsTrue(logs.GetSnapshot().Any(entry => entry.EventName == "ObsAudioCompleted"));
        }
        finally
        {
            File.Delete(audioPath);
        }
    }

    [TestMethod]
    public async Task EveryAudioCueIsDeliveredOnlyToTheGeneralAudioSource()
    {
        string audioPath = Path.Combine(
            Path.GetTempPath(),
            $"qtelopper-obs-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(audioPath, [0x49, 0x44, 0x33]);
        try
        {
            var clock = new TestClock();
            var store = new ObsSnapshotStore(
                AppSettings.CreateDefault().Display,
                clock.UtcNow);

            ObsViewSnapshot eewAudio = store.PublishAudio(
                "EewInitial",
                audioPath,
                clock.UtcNow);

            ObsViewSnapshot general = store.Read(ObsViewChannel.General, clock.UtcNow);
            ObsViewSnapshot eew = store.Read(ObsViewChannel.Eew, clock.UtcNow);
            Assert.AreEqual(eewAudio.AudioSequence, general.AudioSequence);
            Assert.AreEqual("play", general.AudioAction);
            Assert.AreEqual(0, eew.AudioSequence);
            Assert.AreEqual(string.Empty, eew.AudioAction);

            ObsViewSnapshot tsunamiAudio = store.PublishAudio(
                "TsunamiMajorWarning",
                audioPath,
                clock.UtcNow);

            general = store.Read(ObsViewChannel.General, clock.UtcNow);
            ObsViewSnapshot tsunami = store.Read(ObsViewChannel.Tsunami, clock.UtcNow);
            Assert.AreEqual(tsunamiAudio.AudioSequence, general.AudioSequence);
            Assert.AreEqual("play", general.AudioAction);
            Assert.AreEqual(0, tsunami.AudioSequence);
            Assert.AreEqual(string.Empty, tsunami.AudioAction);
        }
        finally
        {
            File.Delete(audioPath);
        }
    }

    [TestMethod]
    public void UnsupportedAudioFormatIsRejectedBeforePublishing()
    {
        string audioPath = Path.Combine(Path.GetTempPath(), $"qtelopper-obs-{Guid.NewGuid():N}.wma");
        File.WriteAllBytes(audioPath, [0x00]);
        try
        {
            var clock = new TestClock();
            var store = new ObsSnapshotStore(AppSettings.CreateDefault().Display, clock.UtcNow);

            Assert.ThrowsExactly<NotSupportedException>(() =>
                store.PublishAudio("EewInitial", audioPath, clock.UtcNow));
        }
        finally
        {
            File.Delete(audioPath);
        }
    }

    [TestMethod]
    public async Task AudioReferenceExpiresAfterRetentionPeriod()
    {
        string audioPath = Path.Combine(Path.GetTempPath(), $"qtelopper-obs-{Guid.NewGuid():N}.ogg");
        await File.WriteAllBytesAsync(audioPath, [0x4f, 0x67, 0x67, 0x53]);
        try
        {
            var clock = new TestClock();
            var store = new ObsSnapshotStore(AppSettings.CreateDefault().Display, clock.UtcNow);
            ObsViewSnapshot audio = store.PublishAudio("Tsunami", audioPath, clock.UtcNow);
            await using var server = new ObsLocalViewServer(store, clock, new UiLogBuffer());
            await server.StartAsync(0);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string tokenQuery = new Uri(server.OverlayUrl).Query;

            clock.UtcNow += TimeSpan.FromSeconds(61);
            using HttpResponseMessage response = await client.GetAsync(
                $"http://127.0.0.1:{server.Port}/audio/{audio.AudioSequence}{tokenQuery}");

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            File.Delete(audioPath);
        }
    }

    [TestMethod]
    public async Task AudioEndpointRequiresTokenSupportsRangesAndDoesNotExposeFilePath()
    {
        string audioPath = Path.Combine(Path.GetTempPath(), $"qtelopper-obs-{Guid.NewGuid():N}.mp3");
        byte[] expected = [0x49, 0x44, 0x33, 0x04, 0x00, 0x01, 0x02, 0x03];
        await File.WriteAllBytesAsync(audioPath, expected);
        try
        {
            AppSettings settings = AppSettings.CreateDefault();
            var clock = new TestClock();
            var store = new ObsSnapshotStore(settings.Display, clock.UtcNow);
            ObsViewSnapshot audioSnapshot = store.PublishAudio(
                "EewInitial",
                audioPath,
                clock.UtcNow);
            await using var server = new ObsLocalViewServer(store, clock, new UiLogBuffer());
            await server.StartAsync(0);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            using HttpResponseMessage unauthorized = await client.GetAsync(
                $"http://127.0.0.1:{server.Port}/audio/{audioSnapshot.AudioSequence}");
            Assert.AreEqual(HttpStatusCode.Forbidden, unauthorized.StatusCode);

            string tokenQuery = new Uri(server.OverlayUrl).Query;
            string audioUrl =
                $"http://127.0.0.1:{server.Port}/audio/{audioSnapshot.AudioSequence}{tokenQuery}";
            byte[] actual = await client.GetByteArrayAsync(audioUrl);
            CollectionAssert.AreEqual(expected, actual);

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, audioUrl);
            rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
            using HttpResponseMessage ranged = await client.SendAsync(rangeRequest);
            Assert.AreEqual(HttpStatusCode.PartialContent, ranged.StatusCode);
            CollectionAssert.AreEqual(expected[2..6], await ranged.Content.ReadAsByteArrayAsync());

            string stateJson = await client.GetStringAsync(
                $"http://127.0.0.1:{server.Port}/state{tokenQuery}&view=general");
            Assert.IsFalse(stateJson.Contains(audioPath, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(stateJson.Contains("\"audioVolume\"", StringComparison.Ordinal));
            StringAssert.Contains(stateJson, "\"audioAction\":\"play\"");

            string eewStateJson = await client.GetStringAsync(
                $"http://127.0.0.1:{server.Port}/state{tokenQuery}&view=eew");
            StringAssert.Contains(eewStateJson, "\"audioAction\":\"\"");
        }
        finally
        {
            File.Delete(audioPath);
        }
    }

    [TestMethod]
    public async Task StateEndpointReturnsCompleteLatestSnapshotAndEscapesUntrustedText()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var clock = new TestClock();
        var store = new ObsSnapshotStore(settings.Display, clock.UtcNow);
        var coordinator = new PriorityCoordinator(clock, settings.Display);
        var program = new DisplayProgram(
            ProgramId: "obs-test-1",
            EventId: EventId.Create("obs-event-1"),
            Kind: EventKind.Eew,
            SourceMode: SourceMode.ManualTest,
            IssuedAt: clock.UtcNow,
            Priority: OverlayPriority.Eew,
            Pages:
            [
                new DisplayPage(
                    0,
                    [new DisplayBlock("警報", "<img src=x onerror=alert(1)>", "安全な文字列", DisplayStyleTokens.EewWarning)],
                    "テスト読み上げ",
                    null),
            ],
            StartedAtUtc: clock.UtcNow,
            EndPolicy: EndPolicy.Manual,
            RehearsalLabel: string.Empty);
        store.Publish(coordinator.Apply(program), settings.Display, clock.UtcNow);

        await using var server = new ObsLocalViewServer(store, clock, new UiLogBuffer());
        await server.StartAsync(0);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string tokenQuery = new Uri(server.OverlayUrl).Query;
        string json = await client.GetStringAsync(
            $"http://127.0.0.1:{server.Port}/state{tokenQuery}&view=eew");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.AreEqual(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("obs-test-1", root.GetProperty("programId").GetString());
        Assert.AreEqual("ManualTest", root.GetProperty("sourceMode").GetString());
        Assert.AreEqual("操作テスト／訓練", root.GetProperty("rehearsalLabel").GetString());
        Assert.AreEqual(1920, root.GetProperty("width").GetInt32());
        Assert.AreEqual(1080, root.GetProperty("height").GetInt32());
        Assert.AreEqual("<img src=x onerror=alert(1)>",
            root.GetProperty("blocks")[0].GetProperty("primaryText").GetString());
        Assert.IsFalse(json.Contains("<img", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EventStreamImmediatelySendsSnapshotAndTracksConnectedClient()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var clock = new TestClock();
        var store = new ObsSnapshotStore(settings.Display, clock.UtcNow);
        await using var server = new ObsLocalViewServer(store, clock, new UiLogBuffer());
        await server.StartAsync(0);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string tokenQuery = new Uri(server.OverlayUrl).Query;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://127.0.0.1:{server.Port}/events{tokenQuery}");
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        string? idLine = await reader.ReadLineAsync();
        string? dataLine = await reader.ReadLineAsync();
        Assert.IsNotNull(idLine);
        Assert.IsTrue(idLine.StartsWith("id: ", StringComparison.Ordinal));
        Assert.IsNotNull(dataLine);
        Assert.IsTrue(dataLine.StartsWith("data: ", StringComparison.Ordinal));
        StringAssert.Contains(dataLine, "\"schemaVersion\":1");
        Assert.AreEqual(1, server.ClientCount);

        response.Dispose();
        await WaitUntilAsync(() => server.ClientCount == 0, TimeSpan.FromSeconds(4));
        Assert.AreEqual(0, server.ClientCount);
    }

    [TestMethod]
    public async Task RetiredBrowserRenderAcknowledgementEndpointIsNotExposed()
    {
        AppSettings settings = AppSettings.CreateDefault();
        var clock = new TestClock();
        var store = new ObsSnapshotStore(settings.Display, clock.UtcNow);
        await using var server = new ObsLocalViewServer(store, clock, new UiLogBuffer());
        await server.StartAsync(0);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string tokenQuery = new Uri(server.OverlayUrl).Query;
        string url = $"http://127.0.0.1:{server.Port}/render-status{tokenQuery}" +
            "&view=weather&sequence=42&program=weather-program&page=2";

        using HttpResponseMessage response = await client.PostAsync(url, new StringContent(string.Empty));

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } =
            new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
