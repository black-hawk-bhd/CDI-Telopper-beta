using System.Net;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Dmdata.Configuration;
using EEWTelop.Infrastructure.Dmdata.Security;
using EEWTelop.Infrastructure.Dmdata.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class DmdataSocketApiClientTests
{
    private static readonly string[] ExpectedClassifications =
    [
        "telegram.weather",
    ];

    [TestMethod]
    public async Task StartUsesOfficialV2RawXmlRequestAndBasicAuthentication()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK, """
                {
                  "status": "ok",
                  "websocket": {
                    "id": 42,
                    "url": "wss://ws001.api.dmdata.jp/v2/websocket?ticket=one-use-ticket",
                    "protocol": ["dmdata.v2"]
                  }
                }
                """);
        }));
        var client = CreateClient(httpClient, DmdataAuthenticationMode.ApiKey, "api-key");

        DmdataSocketTicket ticket = await client.StartAsync(CancellationToken.None);

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Post, captured.Method);
        Assert.AreEqual("https://api.dmdata.jp/v2/socket", captured.RequestUri!.AbsoluteUri);
        Assert.AreEqual("Basic", captured.Headers.Authorization!.Scheme);
        Assert.AreEqual(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("api-key:")),
            captured.Headers.Authorization.Parameter);
        using JsonDocument json = JsonDocument.Parse(body!);
        JsonElement root = json.RootElement;
        CollectionAssert.AreEquivalent(
            ExpectedClassifications,
            root.GetProperty("classifications").EnumerateArray()
                .Select(item => item.GetString()).ToArray());
        CollectionAssert.Contains(
            root.GetProperty("types").EnumerateArray()
                .Select(item => item.GetString()).ToArray(),
            "VPWW55");
        CollectionAssert.Contains(
            root.GetProperty("types").EnumerateArray()
                .Select(item => item.GetString()).ToArray(),
            "VPWW61");
        CollectionAssert.Contains(
            root.GetProperty("types").EnumerateArray()
                .Select(item => item.GetString()).ToArray(),
            "VPBS50");
        CollectionAssert.Contains(
            root.GetProperty("types").EnumerateArray()
                .Select(item => item.GetString()).ToArray(),
            "VPBS51");
        CollectionAssert.Contains(
            root.GetProperty("types").EnumerateArray()
                .Select(item => item.GetString()).ToArray(),
            "VPOA50");
        CollectionAssert.Contains(
            root.GetProperty("types").EnumerateArray()
                .Select(item => item.GetString()).ToArray(),
            "VPHW51");
        CollectionAssert.DoesNotContain(
            root.GetProperty("types").EnumerateArray()
                .Select(item => item.GetString()).ToArray(),
            "VPWW54");
        CollectionAssert.DoesNotContain(
            root.GetProperty("types").EnumerateArray()
                .Select(item => item.GetString()).ToArray(),
            "VXSE43");
        Assert.AreEqual("raw", root.GetProperty("formatMode").GetString());
        Assert.AreEqual("including", root.GetProperty("test").GetString());
        Assert.AreEqual("CDI-Telopper", root.GetProperty("appName").GetString());
        Assert.AreEqual("42", ticket.SocketId);
        Assert.AreEqual("dmdata.v2", ticket.Protocol);
    }

    [TestMethod]
    public async Task CloseUsesOfficialV2SocketIdEndpointAndBearerAuthentication()
    {
        HttpRequestMessage? captured = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        var client = CreateClient(
            httpClient,
            DmdataAuthenticationMode.OAuthAccessToken,
            "oauth-token");

        await client.CloseAsync("42", CancellationToken.None);

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Delete, captured.Method);
        Assert.AreEqual("https://api.dmdata.jp/v2/socket/42", captured.RequestUri!.AbsoluteUri);
        Assert.AreEqual("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.AreEqual("oauth-token", captured.Headers.Authorization.Parameter);
    }

    [TestMethod]
    public void OriginRestricted403IsReportedAsAnActionableProviderConfigurationError()
    {
        var exception = new DmdataApiException(
            403,
            "Does not match the configured IP or Request Origin.");

        string detail = DmdataEventSource.DescribeAuthorizationFailure(exception);

        StringAssert.Contains(detail, "接続元IP");
        StringAssert.Contains(detail, "Request Origin");
        StringAssert.Contains(detail, "契約者ページ");
    }

    private static DmdataSocketApiClient CreateClient(
        HttpClient httpClient,
        DmdataAuthenticationMode mode,
        string secret) => new(
            httpClient,
            new DmdataProviderOptions(
                new Uri("https://api.dmdata.jp/v2/"),
                "unused",
                mode,
                IncludeTestTelegrams: true)
            {
                ReceiveWeatherWarnings = true,
            },
            new FixedCredentialProvider(mode, secret));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class FixedCredentialProvider(
        DmdataAuthenticationMode mode,
        string secret) : IDmdataCredentialProvider
    {
        public DmdataCredential GetCredential() => new(mode, secret);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
