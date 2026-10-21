using System.Net;
using System.Xml;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.History;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class JmaXmlHistoryMessageSourceTests
{
    [TestMethod]
    public async Task FetchesLongFeedsDeduplicatesSelectsNewestAndCachesWithoutLiveMetadata()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var source = new JmaXmlHistoryMessageSource(http, new Clock());
        var request = Request() with { Limit = 2 };
        var first = await source.FetchAsync(request);
        var second = await source.FetchAsync(request);
        Assert.HasCount(2, first);
        Assert.AreEqual(2, handler.FeedCount);
        Assert.AreEqual(2, handler.TelegramCount);
        Assert.IsTrue(first.All(static item => item.SourceMode == SourceMode.HistoryRehearsal &&
            item.Provider == "jma-xml" && item.ContentFormat == RawProviderContentFormat.JmaXml));
        Assert.Contains("VTSE41", first[0].Payload);
        Assert.Contains("VXSE53", first[1].Payload);
        Assert.AreEqual(first[0].Payload, second[0].Payload);
    }

    [TestMethod]
    public async Task CategoryFilterNeverDownloadsEewLegacyOrUntrustedLinks()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var source = new JmaXmlHistoryMessageSource(http, new Clock());
        var messages = await source.FetchAsync(Request() with { JmaContent = JmaHistoryContent.Weather });
        Assert.AreEqual(1, handler.FeedCount);
        Assert.AreEqual(1, handler.TelegramCount);
        Assert.Contains("VPWW55", messages.Single().Payload);
    }

    [TestMethod]
    public async Task DirectOfficialXmlBypassesFeedsAndPreservesHistoryMode()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var source = new JmaXmlHistoryMessageSource(http, new Clock());
        var messages = await source.FetchAsync(Request() with
            { JmaReportUrl = "https://www.data.jma.go.jp/developer/xml/data/20261021010000_0_VXSE53_270000.xml" });
        Assert.AreEqual(0, handler.FeedCount);
        Assert.AreEqual(1, handler.TelegramCount);
        Assert.AreEqual(SourceMode.HistoryRehearsal, messages.Single().SourceMode);
    }

    [TestMethod]
    [DataRow("https://example.com/developer/xml/data/a.xml")]
    [DataRow("http://www.data.jma.go.jp/developer/xml/data/a.xml")]
    [DataRow("https://www.data.jma.go.jp/developer/xml/data/a.xml?redirect=x")]
    [DataRow("https://www.data.jma.go.jp/private.xml")]
    public async Task RejectsNonOfficialDirectUrlsBeforeNetworkAccess(string url)
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var source = new JmaXmlHistoryMessageSource(http, new Clock());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => source.FetchAsync(Request() with { JmaReportUrl = url }));
        Assert.AreEqual(0, handler.FeedCount + handler.TelegramCount);
    }

    [TestMethod]
    public async Task RejectsDtdAndOversizedResponsesAndAllowsRetryAfterFailure()
    {
        var handler = new Handler { Telegram = "<!DOCTYPE Report [<!ENTITY x 'test'>]><Report>&x;</Report>" };
        using var http = new HttpClient(handler);
        await using var source = new JmaXmlHistoryMessageSource(http, new Clock());
        var request = Request() with { JmaContent = JmaHistoryContent.Weather };
        await Assert.ThrowsExactlyAsync<XmlException>(() => source.FetchAsync(request));
        handler.Telegram = "<Report />";
        Assert.HasCount(1, await source.FetchAsync(request));
        handler.Telegram = new string('x', 8 * 1024 * 1024 + 1);
        await Assert.ThrowsExactlyAsync<IOException>(() => source.FetchAsync(Request() with
            { JmaReportUrl = "https://www.data.jma.go.jp/developer/xml/data/oversized.xml" }));
    }

    [TestMethod]
    public async Task CancellationDoesNotDownload()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var source = new JmaXmlHistoryMessageSource(http, new Clock());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.FetchAsync(Request(), stop.Token));
        Assert.AreEqual(0, handler.FeedCount);
    }

    private static HistoryFetchRequest Request() => new(HistoryApi.JmaXml, 100, AppSettings.CreateDefault().Provider);

    private sealed class Handler : HttpMessageHandler
    {
        public int FeedCount { get; private set; }
        public int TelegramCount { get; private set; }
        public string? Telegram { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string xml;
            if (path.Contains("/feed/", StringComparison.Ordinal))
            {
                Assert.IsTrue(path.EndsWith("_l.xml", StringComparison.Ordinal));
                FeedCount++;
                xml = "<feed xmlns='http://www.w3.org/2005/Atom'>" +
                    Entry("VXSE53", "01:00:00") + Entry("VXSE53", "01:00:00") + Entry("VTSE41", "02:00:00") +
                    Entry("VPWW55", "00:00:00") + Entry("VXSE43", "03:00:00") + Entry("VPWW53", "04:00:00") +
                    "<entry><updated>2026-10-21T04:00:00Z</updated><link href='https://example.com/VXSE53.xml'/></entry></feed>";
            }
            else { TelegramCount++; xml = Telegram ?? $"<Report><Title>{path}</Title></Report>"; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) });
        }
        private static string Entry(string code, string time) => $"<entry><updated>2026-10-21T{time}Z</updated>" +
            $"<link href='https://www.data.jma.go.jp/developer/xml/data/20261021010000_0_{code}_270000.xml'/></entry>";
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
