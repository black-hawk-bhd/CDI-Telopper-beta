using System.Net;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using EEWTelop.Infrastructure.Dmdata.Transport;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class JmaPullEventSourceTests
{
    [TestMethod]
    public void FiltersEewAndUnselectedCategories()
    {
        var routing = ProviderRoutingSettings.Default with { Weather = ReceptionProvider.JmaXml };
        Assert.IsTrue(JmaPullEventSource.Accepts("VPWW55", routing));
        Assert.IsTrue(JmaPullEventSource.Accepts("VXKO52", routing));
        Assert.IsFalse(JmaPullEventSource.Accepts("VXSE43", routing));
        Assert.IsFalse(JmaPullEventSource.Accepts("VXSE45", routing));
        Assert.IsFalse(JmaPullEventSource.Accepts("VXSE53", routing));
        Assert.IsFalse(JmaPullEventSource.Accepts("VPFD50", routing));
    }

    [TestMethod]
    public void ExcludesOnlyLegacyWarningCodesFromSupportedWeatherTelegrams()
    {
        var routing = ProviderRoutingSettings.Default with { Weather = ReceptionProvider.JmaXml };
        Assert.IsFalse(JmaPullEventSource.Accepts("VPWW53", routing));
        Assert.IsFalse(JmaPullEventSource.Accepts("VPWW54", routing));
        Assert.IsFalse(JmaPullEventSource.Accepts("VPOA50", routing));
        foreach (string code in new[] { "VPWW55", "VPWW56", "VPWW57", "VPWW58", "VPWW59", "VPWW60", "VPWW61", "VPWS50", "VPBS50", "VPBS51", "VPHW50", "VPHW51", "VXKO52", "VXKO72" })
        {
            Assert.IsTrue(JmaPullEventSource.Accepts(code, routing), code);
        }
    }

    [TestMethod]
    public void RestrictsTelegramHostsAndPaths()
    {
        Assert.IsTrue(JmaPullEventSource.IsTelegramUri(new("https://www.data.jma.go.jp/developer/xml/data/20260908152751_0_VPWW53_140000.xml")));
        Assert.IsFalse(JmaPullEventSource.IsTelegramUri(new("http://www.data.jma.go.jp/developer/xml/data/test.xml")));
        Assert.IsFalse(JmaPullEventSource.IsTelegramUri(new("https://example.com/developer/xml/data/test.xml")));
        Assert.IsFalse(JmaPullEventSource.IsTelegramUri(new("https://www.data.jma.go.jp/private.xml")));
    }

    [TestMethod]
    public async Task PollSkipsOldAndDuplicateMessagesAndRetriesFailedXml()
    {
        var clock = new FakeClock();
        var handler = new FakeHandler();
        await using var source = new JmaPullEventSource(AppSettings.CreateDefault().Provider with
        {
            Routing = ProviderRoutingSettings.Default with { Weather = ReceptionProvider.JmaXml },
        }, clock, new HttpClient(handler));
        Assert.AreEqual(0, (await source.PollAsync(clock.UtcNow, default)).Count);
        var received = await source.PollAsync(clock.UtcNow, default);
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual("jma-xml", received[0].Provider);
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        var result = normalizer.Normalize(received[0]);
        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        var weather = Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        var settings = AppSettings.CreateDefault();
        var visible = Assert.IsInstanceOfType<WeatherWarningEvent>(EventDisplayFilter.Apply(settings.Filter, weather));
        string caption = string.Join(" ", new PageComposer().Compose(visible, settings.Display).Pages.Select(p => p.AccessibleText));
        Assert.Contains("大雨", caption);
        Assert.Contains("和歌山市", caption);
        Assert.AreEqual(0, (await source.PollAsync(clock.UtcNow, default)).Count);
        Assert.AreEqual(2, handler.XmlRequests);
    }

    [TestMethod]
    public async Task AutomaticFallbackDoesNotPollHealthySourcesAndStopsAfterRecovery()
    {
        var clock = new FakeClock();
        var handler = new FakeHandler();
        var settings = AppSettings.CreateDefault().Provider with
        {
            Mode = ProviderMode.Production,
            Routing = ProviderRoutingSettings.FromLegacy(ReceptionProvider.Disabled) with { Weather = ReceptionProvider.Axis },
        };
        var fallback = new JmaFallbackRouting(settings, clock);
        await using var source = new JmaPullEventSource(settings, clock, new HttpClient(handler)) { FallbackRouting = fallback };
        await source.PollAsync(clock.UtcNow, default);
        Assert.AreEqual(0, handler.FeedRequests);
        fallback.Observe(ReceptionProvider.Axis, ProviderConnectionState.Faulted);
        clock.UtcNow += TimeSpan.FromSeconds(30);
        await source.PollAsync(clock.UtcNow - TimeSpan.FromSeconds(30), default);
        Assert.AreEqual(1, handler.FeedRequests);
        fallback.Observe(ReceptionProvider.Axis, ProviderConnectionState.Connected);
        await source.PollAsync(clock.UtcNow, default);
        Assert.AreEqual(1, handler.FeedRequests);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int XmlRequests { get; private set; }
        public int FeedRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/feed/"))
            {
                FeedRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    <feed xmlns="http://www.w3.org/2005/Atom">
                    <entry><updated>2026-09-09T00:01:00Z</updated><link href="https://www.data.jma.go.jp/developer/xml/data/20260909000100_0_VPWW55_010000.xml"/></entry>
                    <entry><updated>2026-09-08T23:59:00Z</updated><link href="https://www.data.jma.go.jp/developer/xml/data/20260908235900_0_VPWW55_010000.xml"/></entry>
                    <entry><updated>2026-09-09T00:01:00Z</updated><link href="https://www.data.jma.go.jp/developer/xml/data/20260909000100_0_VXSE43_010000.xml"/></entry>
                    <entry><updated>2026-09-09T00:01:00Z</updated><link href="https://www.data.jma.go.jp/developer/xml/data/20260909000100_0_VPWW53_010000.xml"/></entry>
                    <entry><updated>2026-09-09T00:01:00Z</updated><link href="https://www.data.jma.go.jp/developer/xml/data/20260909000100_0_VPWW54_010000.xml"/></entry>
                    <entry><updated>2026-09-09T00:01:00Z</updated><link href="https://www.data.jma.go.jp/developer/xml/data/20260909000100_0_VPOA50_010000.xml"/></entry>
                    </feed>
                    """) });
            }
            XmlRequests++;
            return Task.FromResult(new HttpResponseMessage(XmlRequests == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent("""
                <Report><Control><Title>気象警報・注意報（Ｒ０６）（大雨）</Title><Status>通常</Status><PublishingOffice>和歌山地方気象台</PublishingOffice></Control>
                <Head><ReportDateTime>2026-09-09T09:01:00+09:00</ReportDateTime><InfoType>発表</InfoType></Head>
                <Body><Warning type="気象警報・注意報（市町村等）">
                <Item><Kind><Status>発表</Status><Name>レベル３大雨警報</Name><Code>03</Code></Kind><Area><Name>和歌山市</Name><Code>3020100</Code></Area></Item>
                </Warning></Body></Report>
                """) });
        }
    }
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
