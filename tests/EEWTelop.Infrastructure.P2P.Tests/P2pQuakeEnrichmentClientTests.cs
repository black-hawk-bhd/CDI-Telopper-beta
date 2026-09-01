using System.Net;
using System.Text;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.P2P.Configuration;
using EEWTelop.Infrastructure.P2P.Recovery;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.P2P.Tests;

[TestClass]
public sealed class P2pQuakeEnrichmentClientTests
{
    [TestMethod]
    public async Task OptionalEnrichmentAllowsAtMostOneRequestPerSevenSeconds()
    {
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        var clock = new FakeClock();
        var client = new P2pQuakeEnrichmentClient(
            httpClient,
            ProviderOptions.Production,
            clock);
        EventId eventId = EventId.Create("quake/id");

        Application.Events.RawProviderMessage? first = await client.TryFetchAsync(eventId);
        Application.Events.RawProviderMessage? suppressed = await client.TryFetchAsync(eventId);
        clock.Advance(TimeSpan.FromSeconds(7));
        Application.Events.RawProviderMessage? afterInterval = await client.TryFetchAsync(eventId);

        Assert.IsNotNull(first);
        Assert.IsNull(suppressed);
        Assert.IsNotNull(afterInterval);
        Assert.AreEqual(2, handler.RequestCount);
        Assert.EndsWith("/jma/quake/quake%2Fid", handler.LastRequestUri!.AbsoluteUri);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":551}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
