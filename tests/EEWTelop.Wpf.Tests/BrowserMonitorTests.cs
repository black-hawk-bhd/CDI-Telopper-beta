using System.Net;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Obs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class BrowserMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly DisplaySettings Settings = AppSettings.CreateDefault().Display;

    [TestMethod]
    public async Task MonitorIsAuthenticatedReadOnlyAndNotAnAudioClient()
    {
        var store = new ObsSnapshotStore(Settings, Now);
        var catalog = TestScenarioCatalog.Create(Now);
        foreach (var kind in new[] { EventKind.Quake, EventKind.Eew, EventKind.Tsunami, EventKind.WeatherWarning })
        {
            var item = catalog.First(scenario => scenario.Event.Kind == kind).Event;
            var program = new PageComposer().Compose(item, Settings) with { EndPolicy = EndPolicy.Manual };
            store.PublishProgram(item, program, Settings, Now);
            store.AddMonitorTelegram(item, program, Settings, "訓練表示");
        }
        store.PublishAudioStop(Now);
        long audioSequence = store.Read().AudioSequence;
        await using var server = new ObsLocalViewServer(store, new Clock(), new UiLogBuffer());
        await server.StartAsync(0);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string root = $"http://127.0.0.1:{server.Port}";
        string query = new Uri(server.MonitorUrl).Query;
        using var missing = await client.GetAsync(root + "/monitor/data");
        Assert.AreEqual(HttpStatusCode.Forbidden, missing.StatusCode);
        using var html = await client.GetAsync(server.MonitorUrl);
        Assert.AreEqual(HttpStatusCode.OK, html.StatusCode);
        Assert.IsTrue(html.Headers.CacheControl!.NoStore);
        StringAssert.Contains(html.Headers.GetValues("Content-Security-Policy").Single(), "frame-src 'self'");
        using var frame = await client.GetAsync(root + "/monitor/view" + query);
        Assert.AreEqual(HttpStatusCode.OK, frame.StatusCode);
        using var data = JsonDocument.Parse(await client.GetStringAsync(root + "/monitor/data" + query));
        foreach (var channel in data.RootElement.GetProperty("channels").EnumerateObject())
        {
            Assert.IsTrue(channel.Value.GetProperty("hasProgram").GetBoolean());
            Assert.AreEqual(0, channel.Value.GetProperty("audioSequence").GetInt64());
            Assert.AreEqual(string.Empty, channel.Value.GetProperty("audioAction").GetString());
        }
        var entries = data.RootElement.GetProperty("telegrams");
        Assert.AreEqual(4, entries.GetArrayLength());
        string id = entries[0].GetProperty("id").GetString()!;
        using var detail = JsonDocument.Parse(await client.GetStringAsync(root + "/monitor/item" + query + "&id=" + id));
        Assert.IsTrue(detail.RootElement.GetArrayLength() > 0);
        Assert.AreEqual("ManualTest", detail.RootElement[0].GetProperty("sourceMode").GetString());
        Assert.AreEqual(audioSequence, store.Read().AudioSequence);
        Assert.AreEqual(0, server.ClientCount);
        Assert.AreEqual(0, server.RouteClientCounts.Values.Sum());
        using var post = await client.PostAsync(root + "/monitor/data" + query, new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        using var unknown = await client.GetAsync(root + "/monitor/item" + query + "&id=99999");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [TestMethod]
    public void HistoryReviewDoesNotRepublishOrAdvanceTheLiveProgram()
    {
        var store = new ObsSnapshotStore(Settings, Now);
        var items = TestScenarioCatalog.Create(Now).Where(s => s.Event.Kind == EventKind.Quake).Take(2).ToArray();
        Assert.HasCount(2, items);
        foreach (var item in items)
        {
            var program = new PageComposer().Compose(item.Event, Settings) with { EndPolicy = EndPolicy.Manual };
            store.PublishProgram(item.Event, program, Settings, Now);
            store.AddMonitorTelegram(item.Event, program, Settings, "訓練表示");
        }
        string live = store.Read(ObsViewChannel.General, Now).ProgramId;
        var review = store.ReadMonitorTelegram(1, Now.AddDays(1));
        Assert.IsNotNull(review);
        Assert.AreEqual(SourceMode.ManualTest, review[0].SourceMode);
        Assert.AreEqual(live, store.Read(ObsViewChannel.General, Now).ProgramId);
        Assert.AreEqual(0L, review[0].AudioSequence);
        store.ClearPrograms(Now);
        Assert.IsFalse(store.Read(ObsViewChannel.General, Now).HasProgram);
        Assert.IsNotNull(store.ReadMonitorTelegram(1, Now));
    }

    [TestMethod]
    public void HistoryIsBoundedAndDuplicateTelegramIsReplaced()
    {
        var store = new ObsSnapshotStore(Settings, Now);
        for (int index = 0; index < 501; index++)
        {
            var item = TestScenarioCatalog.Create(Now.AddSeconds(index))[0].Event;
            var program = new PageComposer().Compose(item, Settings);
            store.AddMonitorTelegram(item, program, Settings, "訓練表示");
        }
        Assert.IsNull(store.ReadMonitorTelegram(1, Now));
        Assert.IsNotNull(store.ReadMonitorTelegram(501, Now));
        var last = TestScenarioCatalog.Create(Now.AddSeconds(500))[0].Event;
        store.AddMonitorTelegram(last, new PageComposer().Compose(last, Settings), Settings, "更新");
        Assert.IsNull(store.ReadMonitorTelegram(501, Now));
        Assert.IsNotNull(store.ReadMonitorTelegram(502, Now));
        Assert.IsNotNull(store.ReadMonitorTelegram(2, Now));
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
