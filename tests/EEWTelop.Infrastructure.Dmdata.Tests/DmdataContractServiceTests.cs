using System.Net;
using System.Text;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Dmdata.Security;
using EEWTelop.Infrastructure.Dmdata.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class DmdataContractServiceTests
{
    private static readonly string[] ContractScopes = ["contract.list"];
    private const string ValidItems = """
        {"status":"ok","items":[
          {"id":92,"planId":1,"planName":"地震・津波関連","classification":"telegram.earthquake",
           "price":{"day":15,"month":350},"start":"2026-10-01T01:01:00Z","isValid":true,"connectionCounts":1},
          {"id":null,"planId":2,"planName":"緊急地震（予報）","classification":"eew.forecast",
           "price":{"day":75,"month":1650},"start":null,"isValid":false,"connectionCounts":0},
          {"id":"123","planId":3,"planName":"緊急地震（警報）","classification":"eew.warning",
           "price":{"day":20,"month":440},"start":"2026-09-01T00:00:00Z","isValid":false,"connectionCounts":1}
        ]}
        """;

    [TestMethod]
    public async Task ApiKeyUsesOfficialReadOnlyEndpointAndKeepsAllContractStates()
    {
        int calls = 0;
        using var service = CreateService(request =>
        {
            calls++;
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://api.dmdata.jp/v2/contract", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Basic", request.Headers.Authorization!.Scheme);
            Assert.AreEqual(Convert.ToBase64String(Encoding.UTF8.GetBytes("test-api-key:")), request.Headers.Authorization.Parameter);
            Assert.IsNull(request.Content);
            return Response(HttpStatusCode.OK, ValidItems);
        });
        DmdataContractSnapshot snapshot = await service.GetAsync(ApiKeySettings() with
            { DmdataApiBaseUrl = "https://example.invalid/v2" }, CancellationToken.None);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(3, snapshot.Items.Count);
        Assert.AreEqual("92", snapshot.Items[0].ContractId);
        Assert.IsTrue(snapshot.Items[0].IsValid);
        Assert.AreEqual(350, snapshot.Items[0].MonthlyMaximumPriceYen);
        Assert.IsNull(snapshot.Items[1].ContractId);
        Assert.AreEqual("123", snapshot.Items[2].ContractId);
        Assert.IsFalse(snapshot.Items[2].IsValid);
        Assert.AreEqual(new TestClock().UtcNow, snapshot.RetrievedAtUtc);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OAuthUsesOnlyContractScopeAndRetries401Once(bool rejectRefreshedToken)
    {
        var oauth = new FakeOAuthService();
        int calls = 0;
        using var service = CreateService(request =>
        {
            calls++;
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            Assert.AreEqual(calls == 1 ? "old-token" : "new-token", request.Headers.Authorization.Parameter);
            return Response(calls == 1 || rejectRefreshedToken ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, ValidItems);
        }, oauth);
        if (rejectRefreshedToken)
            await Assert.ThrowsExactlyAsync<DmdataContractException>(() => service.GetAsync(OAuthSettings(), CancellationToken.None));
        else
            Assert.AreEqual(3, (await service.GetAsync(OAuthSettings(), CancellationToken.None)).Items.Count);
        Assert.AreEqual(2, calls);
        Assert.AreEqual(1, oauth.RefreshCount);
        CollectionAssert.AreEqual(ContractScopes, oauth.LastScopes.ToArray());
        Assert.AreEqual(DmdataOAuthDefaults.ClientId, oauth.LastClientId);
    }

    [TestMethod]
    public async Task ApiKeyUnauthorizedDoesNotRefreshOrRetry()
    {
        int calls = 0;
        using var service = CreateService(_ =>
        {
            calls++;
            return Response(HttpStatusCode.Unauthorized, "secret error body");
        });
        DmdataContractException error = await Assert.ThrowsExactlyAsync<DmdataContractException>(
            () => service.GetAsync(ApiKeySettings(), CancellationToken.None));
        Assert.AreEqual(401, error.StatusCode);
        Assert.AreEqual(1, calls);
        Assert.IsFalse(error.Message.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(403)]
    [DataRow(429)]
    [DataRow(500)]
    public async Task HttpErrorsNeverExposeTheServerBodyOrReportNoContract(int statusCode)
    {
        using var service = CreateService(_ => Response((HttpStatusCode)statusCode, "access-token-and-personal-data"));
        DmdataContractException error = await Assert.ThrowsExactlyAsync<DmdataContractException>(
            () => service.GetAsync(ApiKeySettings(), CancellationToken.None));
        Assert.AreEqual(statusCode, error.StatusCode);
        Assert.IsFalse(error.Message.Contains("access-token", StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains("未契約", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task JsonErrorWithHttp200StillFailsWithSafeMessage()
    {
        using var service = CreateService(_ => Response(HttpStatusCode.OK,
            "{\"status\":\"error\",\"error\":{\"code\":403,\"message\":\"secret-token\"}}"));
        DmdataContractException error = await Assert.ThrowsExactlyAsync<DmdataContractException>(
            () => service.GetAsync(ApiKeySettings(), CancellationToken.None));
        Assert.AreEqual(403, error.StatusCode);
        StringAssert.Contains(error.Message, "contract.list");
        Assert.IsFalse(error.Message.Contains("secret-token", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("not JSON")]
    [DataRow("[]")]
    [DataRow("{\"status\":\"ok\"}")]
    [DataRow("{\"status\":\"ok\",\"items\":null}")]
    [DataRow("{\"status\":\"ok\",\"items\":[{}]}")]
    [DataRow("{\"status\":\"ok\",\"items\":[],\"nextToken\":123}")]
    public async Task IncompleteOrMalformedResponseIsNotTreatedAsUncontracted(string json)
    {
        using var service = CreateService(_ => Response(HttpStatusCode.OK, json));
        await Assert.ThrowsExactlyAsync<DmdataContractException>(() => service.GetAsync(ApiKeySettings(), CancellationToken.None));
    }

    [TestMethod]
    public async Task MissingValidityIsNotTreatedAsInactive()
    {
        using var service = CreateService(_ => Response(HttpStatusCode.OK,
            ValidItems.Replace("\"isValid\":true,", string.Empty, StringComparison.Ordinal)));
        await Assert.ThrowsExactlyAsync<DmdataContractException>(() => service.GetAsync(ApiKeySettings(), CancellationToken.None));
    }

    [TestMethod]
    public async Task MissingCredentialDoesNotMakeARequest()
    {
        using var service = CreateService(_ => throw new AssertFailedException("Unexpected request"));
        await Assert.ThrowsExactlyAsync<DmdataContractException>(() => service.GetAsync(
            AppSettings.CreateDefault().Provider with { DmdataCredentialEnvironmentVariable = string.Empty }, CancellationToken.None));
    }

    [TestMethod]
    public async Task PaginationIsCollectedButRepeatedCursorFailsWithoutPartialResult()
    {
        int calls = 0;
        using var service = CreateService(request =>
        {
            calls++;
            if (calls > 1) Assert.AreEqual("?cursorToken=page%2B2", request.RequestUri!.Query);
            return Response(HttpStatusCode.OK, calls == 1
                ? ValidItems[..^1] + ",\"nextToken\":\"page+2\"}"
                : "{\"status\":\"ok\",\"items\":[]}");
        });
        Assert.AreEqual(3, (await service.GetAsync(ApiKeySettings(), CancellationToken.None)).Items.Count);
        Assert.AreEqual(2, calls);
        using var repeating = CreateService(_ => Response(HttpStatusCode.OK,
            "{\"status\":\"ok\",\"items\":[],\"nextToken\":\"same\"}"));
        await Assert.ThrowsExactlyAsync<DmdataContractException>(() => repeating.GetAsync(ApiKeySettings(), CancellationToken.None));
    }

    private static ProviderSettings ApiKeySettings() => AppSettings.CreateDefault().Provider with
    {
        DmdataProtectedCredential = DmdataCredentialProtector.Protect("test-api-key"),
        DmdataCredentialEnvironmentVariable = string.Empty,
    };

    private static ProviderSettings OAuthSettings() => AppSettings.CreateDefault().Provider with
    { DmdataAuthenticationMode = DmdataAuthenticationMode.OAuthAccessToken };

    private static DmdataContractService CreateService(Func<HttpRequestMessage, HttpResponseMessage> send,
        IDmdataOAuthService? oauth = null) => new(new HttpClient(new StubHandler(send)), oauth, new TestClock());

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string json) => new(statusCode)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class FakeOAuthService : IDmdataOAuthService
    {
        public int RefreshCount { get; private set; }
        public string LastClientId { get; private set; } = string.Empty;
        public IReadOnlyList<string> LastScopes { get; private set; } = [];
        public DmdataOAuthSessionInfo? Session => null;
        public Task AuthorizeAsync(string clientId, IReadOnlyList<string> scopes, Action<Uri> openBrowser,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RevokeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetAccessTokenAsync(string clientId, IReadOnlyList<string> scopes,
            CancellationToken cancellationToken, string? rejectedAccessToken = null)
        {
            LastClientId = clientId;
            LastScopes = scopes;
            if (rejectedAccessToken is not null) RefreshCount++;
            return Task.FromResult(RefreshCount == 0 ? "old-token" : "new-token");
        }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
