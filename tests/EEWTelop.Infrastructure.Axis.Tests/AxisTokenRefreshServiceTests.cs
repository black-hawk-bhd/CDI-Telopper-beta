using System.Net;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Infrastructure.Axis.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Axis.Tests;

[TestClass]
public sealed class AxisTokenRefreshServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TokenWithSevenDaysRemainingDoesNotCallRefreshEndpoint()
    {
        int requestCount = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            throw new AssertFailedException("Refresh endpoint must not be called early.");
        }));
        using var service = new AxisTokenRefreshService(new FixedClock(Now), http);
        string token = CreateToken(Now.AddDays(7));

        AxisTokenRefreshResult result = await service.RefreshIfDueAsync(
            new Uri("https://axis.prioris.jp/api/"),
            token);

        Assert.AreEqual(AxisTokenRefreshOutcome.NotDue, result.Outcome);
        Assert.AreEqual(0, requestCount);
    }

    [TestMethod]
    public async Task DueTokenIsRefreshedWithBearerAuthentication()
    {
        string oldToken = CreateToken(Now.AddDays(6));
        string newToken = CreateToken(Now.AddDays(37));
        Uri? requestedUri = null;
        string? authorization = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            return Json(HttpStatusCode.OK, new
            {
                status = "generate a new token",
                token = newToken,
            });
        }));
        using var service = new AxisTokenRefreshService(new FixedClock(Now), http);

        AxisTokenRefreshResult result = await service.RefreshIfDueAsync(
            new Uri("https://axis.prioris.jp/api"),
            oldToken);

        Assert.AreEqual(AxisTokenRefreshOutcome.Refreshed, result.Outcome);
        Assert.AreEqual(newToken, result.AccessToken);
        Assert.AreEqual("https://axis.prioris.jp/api/token/refresh/", requestedUri?.AbsoluteUri);
        Assert.AreEqual("Bearer " + oldToken, authorization);
        Assert.AreEqual(Now.AddDays(37), result.ExpiresAtUtc);
    }

    [TestMethod]
    public async Task NotDueResponseKeepsCurrentToken()
    {
        string token = CreateToken(Now.AddDays(1));
        using var http = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, new
        {
            status = "not due for refresh yet",
            token,
        })));
        using var service = new AxisTokenRefreshService(new FixedClock(Now), http);

        AxisTokenRefreshResult result = await service.RefreshIfDueAsync(
            new Uri("https://axis.prioris.jp/api/"),
            token);

        Assert.AreEqual(AxisTokenRefreshOutcome.Unchanged, result.Outcome);
        Assert.AreEqual(token, result.AccessToken);
    }

    [TestMethod]
    public async Task ContractExpiryIsReportedWithoutReplacingToken()
    {
        string token = CreateToken(Now.AddDays(1));
        using var http = new HttpClient(new StubHandler(_ =>
            Json(HttpStatusCode.PaymentRequired, new { status = "contract has expired" })));
        using var service = new AxisTokenRefreshService(new FixedClock(Now), http);

        AxisTokenRefreshResult result = await service.RefreshIfDueAsync(
            new Uri("https://axis.prioris.jp/api/"),
            token);

        Assert.AreEqual(AxisTokenRefreshOutcome.ContractExpired, result.Outcome);
        Assert.AreEqual(token, result.AccessToken);
    }

    [TestMethod]
    public async Task ExpiredOrMalformedTokenNeverCallsRefreshEndpoint()
    {
        int requestCount = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return Json(HttpStatusCode.OK, new { });
        }));
        using var service = new AxisTokenRefreshService(new FixedClock(Now), http);

        AxisTokenRefreshResult expired = await service.RefreshIfDueAsync(
            new Uri("https://axis.prioris.jp/api/"),
            CreateToken(Now.AddSeconds(-1)));
        AxisTokenRefreshResult malformed = await service.RefreshIfDueAsync(
            new Uri("https://axis.prioris.jp/api/"),
            "not-a-jwt");

        Assert.AreEqual(AxisTokenRefreshOutcome.Expired, expired.Outcome);
        Assert.AreEqual(AxisTokenRefreshOutcome.InvalidToken, malformed.Outcome);
        Assert.AreEqual(0, requestCount);
    }

    private static string CreateToken(DateTimeOffset expiresAtUtc)
    {
        string header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none" }));
        string payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            exp = expiresAtUtc.ToUnixTimeSeconds(),
        }));
        return header + "." + payload + ".signature";
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static HttpResponseMessage Json(HttpStatusCode statusCode, object value) =>
        new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
