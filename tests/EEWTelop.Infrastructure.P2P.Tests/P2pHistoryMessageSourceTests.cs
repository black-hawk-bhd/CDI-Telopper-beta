using System.Net;
using System.Text;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.P2P.Configuration;
using EEWTelop.Infrastructure.P2P.Recovery;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.P2P.Tests;

[TestClass]
public sealed class P2pHistoryMessageSourceTests
{
    [TestMethod]
    public async Task JmaQuakeUsesDescendingEndpointAndMarksMessagesAsHistory()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var clock = new FakeClock();
        var source = new P2pHistoryMessageSource(httpClient, clock);

        IReadOnlyList<RawProviderMessage> messages = await source.FetchAsync(
            new HistoryFetchRequest(
                HistoryApi.JmaQuake,
                5,
                AppSettings.CreateDefault().Provider));

        Assert.IsNotNull(handler.RequestUri);
        Assert.AreEqual("/v2/jma/quake", handler.RequestUri.AbsolutePath);
        Assert.AreEqual("?limit=5&order=-1", handler.RequestUri.Query);
        Assert.HasCount(2, messages);
        Assert.IsTrue(messages.All(item => item.SourceMode == SourceMode.HistoryRehearsal));
        Assert.IsTrue(messages.All(item => item.ReceivedAt == clock.UtcNow));
    }

    [TestMethod]
    public async Task HistoryEndpointRequestsAllSupportedDisplayCodes()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var source = new P2pHistoryMessageSource(httpClient, new FakeClock());

        await source.FetchAsync(new HistoryFetchRequest(
            HistoryApi.History,
            8,
            new ProviderSettings(
                ProviderMode.Sandbox,
                ProviderOptions.Sandbox.WebSocketUri.AbsoluteUri,
                ProviderOptions.Sandbox.RestBaseUri.AbsoluteUri)));

        Assert.IsNotNull(handler.RequestUri);
        Assert.AreEqual("/v2/history", handler.RequestUri.AbsolutePath);
        Assert.IsTrue(handler.RequestUri.Query.Contains("codes=551&codes=552&codes=556", StringComparison.Ordinal));
        Assert.IsTrue(handler.RequestUri.Query.Contains("limit=8", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task JmaQuakeUsesProductionEndpointWhenCurrentProviderIsSandbox()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var source = new P2pHistoryMessageSource(httpClient, new FakeClock());

        await source.FetchAsync(new HistoryFetchRequest(
            HistoryApi.JmaQuake,
            3,
            new ProviderSettings(
                ProviderMode.Sandbox,
                ProviderOptions.Sandbox.WebSocketUri.AbsoluteUri,
                ProviderOptions.Sandbox.RestBaseUri.AbsoluteUri)));

        Assert.IsNotNull(handler.RequestUri);
        Assert.AreEqual(ProviderOptions.Production.RestBaseUri.Host, handler.RequestUri.Host);
        Assert.AreEqual("/v2/jma/quake", handler.RequestUri.AbsolutePath);
    }

    [TestMethod]
    public async Task HttpFailureIsRecordedInApplicationLog()
    {
        var logs = new UiLogBuffer();
        var handler = new RecordingHandler(HttpStatusCode.BadRequest);
        using var httpClient = new HttpClient(handler);
        var source = new P2pHistoryMessageSource(httpClient, new FakeClock(), logWriter: logs);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => source.FetchAsync(
            new HistoryFetchRequest(
                HistoryApi.History,
                5,
                AppSettings.CreateDefault().Provider)));

        IReadOnlyList<AppLogEntry> entries = logs.GetSnapshot();
        Assert.IsTrue(entries.Any(entry => entry.EventName == "HistoryFetchStarted"));
        AppLogEntry failed = entries.Single(entry => entry.EventName == "HistoryFetchFailed");
        Assert.AreEqual(AppLogLevel.Error, failed.Level);
        StringAssert.Contains(failed.Message, "HTTP 400");
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    "[{\"code\":551,\"id\":\"a\"},{\"code\":551,\"id\":\"b\"}]",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
