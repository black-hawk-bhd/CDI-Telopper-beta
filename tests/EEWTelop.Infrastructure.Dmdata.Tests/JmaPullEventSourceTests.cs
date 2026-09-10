using System.Net;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
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
        Assert.AreEqual(1, (await source.PollAsync(clock.UtcNow, default)).Count);
        Assert.AreEqual(0, (await source.PollAsync(clock.UtcNow, default)).Count);
        Assert.AreEqual(2, handler.XmlRequests);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int XmlRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/feed/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    <feed xmlns="http://www.w3.org/2005/Atom">
                    <entry><updated>2026-09-09T00:01:00Z</updated><link href="https://www.data.jma.go.jp/developer/xml/data/20260909000100_0_VPWW55_010000.xml"/></entry>
                    <entry><updated>2026-09-08T23:59:00Z</updated><link href="https://www.data.jma.go.jp/developer/xml/data/20260908235900_0_VPWW55_010000.xml"/></entry>
                    <entry><updated>2026-09-09T00:01:00Z</updated><link href="https://www.data.jma.go.jp/developer/xml/data/20260909000100_0_VXSE43_010000.xml"/></entry>
                    </feed>
                    """) });
            XmlRequests++;
            return Task.FromResult(new HttpResponseMessage(XmlRequests == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent("<Report/>") });
        }
    }
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
