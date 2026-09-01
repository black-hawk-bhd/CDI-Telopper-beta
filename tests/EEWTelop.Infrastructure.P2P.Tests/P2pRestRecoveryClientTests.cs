using System.Net;
using System.Text;
using EEWTelop.Infrastructure.P2P.Configuration;
using EEWTelop.Infrastructure.P2P.Recovery;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.P2P.Tests;

[TestClass]
public sealed class P2pRestRecoveryClientTests
{
    [TestMethod]
    public async Task FetchesLatestFiveQuakeAndTsunamiItemsInDescendingOrder()
    {
        var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        var client = new P2pRestRecoveryClient(
            httpClient,
            ProviderOptions.Production,
            new FakeClock());

        var messages = new List<string>();
        await foreach (Application.Events.RawProviderMessage message in
            client.FetchRecentAsync(
                new DateTimeOffset(2026, 7, 31, 11, 59, 0, TimeSpan.Zero),
                CancellationToken.None))
        {
            messages.Add(message.Json);
        }

        Assert.HasCount(4, messages);
        Assert.HasCount(2, handler.RequestUris);
        Assert.IsTrue(handler.RequestUris.All(uri => uri.Query.Contains(
            "limit=5&order=-1",
            StringComparison.Ordinal)));
        Assert.AreEqual("/v2/jma/quake", handler.RequestUris[0].AbsolutePath);
        Assert.AreEqual("/v2/jma/tsunami", handler.RequestUris[1].AbsolutePath);
    }

    [TestMethod]
    public async Task ExcludesTsunamiCancellationIssuedBeforeReconnectCursor()
    {
        var handler = new RecordingHttpHandler
        {
            TsunamiJson =
                "[{\"code\":552,\"id\":\"old-cancel\",\"cancelled\":true," +
                "\"issue\":{\"time\":\"2026/07/31 20:59:00\"}}," +
                "{\"code\":552,\"id\":\"new-cancel\",\"cancelled\":true," +
                "\"issue\":{\"time\":\"2026/07/31 21:01:00\"}}]",
        };
        using var httpClient = new HttpClient(handler);
        var client = new P2pRestRecoveryClient(
            httpClient,
            ProviderOptions.Production,
            new FakeClock());

        var messages = new List<string>();
        await foreach (Application.Events.RawProviderMessage message in
            client.FetchRecentAsync(
                new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
                CancellationToken.None))
        {
            messages.Add(message.Json);
        }

        Assert.HasCount(3, messages);
        Assert.IsFalse(messages.Any(item => item.Contains("old-cancel", StringComparison.Ordinal)));
        Assert.IsTrue(messages.Any(item => item.Contains("new-cancel", StringComparison.Ordinal)));
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        public string? TsunamiJson { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUris.Add(request.RequestUri!);
            string code = request.RequestUri!.AbsolutePath.EndsWith(
                "tsunami",
                StringComparison.Ordinal)
                ? "552"
                : "551";
            string json = code == "552" && TsunamiJson is not null
                ? TsunamiJson
                : $"[{{\"code\":{code},\"id\":\"a\",\"issue\":{{\"time\":\"2026/07/31 21:00:01\"}}}}," +
                  $"{{\"code\":{code},\"id\":\"b\",\"issue\":{{\"time\":\"2026/07/31 21:00:02\"}}}}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
