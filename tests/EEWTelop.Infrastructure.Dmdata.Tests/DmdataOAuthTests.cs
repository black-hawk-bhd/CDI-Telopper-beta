using System.Net;
using System.Text;
using EEWTelop.Application.Abstractions;
using EEWTelop.Infrastructure.Dmdata.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class DmdataOAuthTests
{
    private const string ClientId = "CId.test-client";
    private static readonly string[] Scopes = ["socket.start", "socket.close", "telegram.get.weather"];
    private static readonly string[] RevokedTokens = ["ARh.refresh", "ATn.old"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CDI-OAuth-Tests", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public void AuthorizationRequestUsesS256AndNoClientSecret()
    {
        Uri uri = DmdataOAuthService.BuildAuthorizationUri(ClientId, string.Join(' ', Scopes),
            new Uri("http://127.0.0.1:43210/oauth2/callback"), "state-test",
            "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");
        Dictionary<string, string> query = DmdataOAuthCallbackListener.ParseQuery(uri.Query)!;
        Assert.AreEqual("https", uri.Scheme);
        Assert.AreEqual("manager.dmdata.jp", uri.Host);
        Assert.AreEqual("S256", query["code_challenge_method"]);
        Assert.AreEqual("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", query["code_challenge"]);
        Assert.IsFalse(query.ContainsKey("client_secret"));
        Assert.IsFalse(query.ContainsKey("code_verifier"));
    }

    [TestMethod]
    public async Task BrowserFlowIgnoresWrongStateThenExchangesCodeAndProtectsTokens()
    {
        using var callbackClient = new HttpClient();
        Task? callbacks = null;
        using var service = CreateService(async request =>
        {
            string form = await request.Content!.ReadAsStringAsync();
            Dictionary<string, string> values = DmdataOAuthCallbackListener.ParseQuery(form)!;
            Assert.AreEqual("authorization_code", values["grant_type"]);
            Assert.AreEqual("ACe.test", values["code"]);
            Assert.IsTrue(values["redirect_uri"].StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            Assert.IsTrue(values["code_verifier"].Length >= 43);
            Assert.IsFalse(values.ContainsKey("client_secret"));
            return TokenResponse(includeRefresh: true);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.AuthorizeAsync(ClientId, Scopes, uri =>
        {
            Dictionary<string, string> query = DmdataOAuthCallbackListener.ParseQuery(uri.Query)!;
            callbacks = SendCallbacksAsync(query["redirect_uri"], query["state"]);
        }, timeout.Token);
        await callbacks!;
        Assert.AreEqual("ATn.new", await service.GetAccessTokenAsync(ClientId, Scopes, timeout.Token));
        string stored = await File.ReadAllTextAsync(SessionPath);
        Assert.IsFalse(stored.Contains("ATn.new", StringComparison.Ordinal));
        Assert.IsFalse(stored.Contains("ARh.refresh", StringComparison.Ordinal));
        Assert.AreEqual(ClientId, service.Session!.ClientId);

        async Task SendCallbacksAsync(string redirect, string state)
        {
            using HttpResponseMessage wrong = await callbackClient.GetAsync(redirect + "?code=wrong&state=wrong", timeout.Token);
            Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);
            using HttpResponseMessage good = await callbackClient.GetAsync(redirect + "?code=ACe.test&state=" + state, timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, good.StatusCode);
        }
    }

    [TestMethod]
    public async Task ExpiredTokenRefreshesOnceForConcurrentRequestsAndRetainsRefreshToken()
    {
        await SeedAsync(expired: true);
        int calls = 0;
        using var service = CreateService(async request =>
        {
            Interlocked.Increment(ref calls);
            string form = await request.Content!.ReadAsStringAsync();
            StringAssert.Contains(form, "grant_type=refresh_token");
            StringAssert.Contains(form, "refresh_token=ARh.refresh");
            return TokenResponse(includeRefresh: false);
        });
        string[] tokens = await Task.WhenAll(
            service.GetAccessTokenAsync(ClientId, Scopes, CancellationToken.None),
            service.GetAccessTokenAsync(ClientId, Scopes, CancellationToken.None));
        Assert.AreEqual(1, calls);
        Assert.IsTrue(tokens.All(static token => token == "ATn.new"));
        var reloaded = new DmdataOAuthSessionStore(SessionPath).Load();
        Assert.AreEqual("ARh.refresh", reloaded!.RefreshToken);
        Assert.AreEqual("ATn.new", reloaded.AccessToken);
    }

    [TestMethod]
    public async Task ConcurrentUnauthorizedResponsesDoNotRefreshAlreadyReplacedToken()
    {
        await SeedAsync(expired: false);
        int calls = 0;
        using var service = CreateService(_ =>
        {
            calls++;
            return Task.FromResult(TokenResponse(includeRefresh: false));
        });
        await service.GetAccessTokenAsync(ClientId, Scopes, CancellationToken.None, "ATn.old");
        await service.GetAccessTokenAsync(ClientId, Scopes, CancellationToken.None, "ATn.old");
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task InvalidGrantRequiresReauthorizationAndRemovesExpiredSession()
    {
        await SeedAsync(expired: true);
        using var service = CreateService(_ => Task.FromResult(Response(HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\",\"error_description\":\"ARh.secret-must-not-leak\"}")));
        DmdataOAuthException error = await Assert.ThrowsExactlyAsync<DmdataOAuthException>(
            () => service.GetAccessTokenAsync(ClientId, Scopes, CancellationToken.None));
        Assert.IsTrue(error.RequiresAuthorization);
        Assert.IsFalse(error.ToString().Contains("ARh.secret-must-not-leak", StringComparison.Ordinal));
        Assert.IsNull(service.Session);
        Assert.IsFalse(File.Exists(SessionPath));
    }

    [TestMethod]
    public async Task TransientRefreshFailureKeepsSessionForRetry()
    {
        await SeedAsync(expired: true);
        using var service = CreateService(_ => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable,
            "{\"error\":\"server_error\"}")));
        DmdataOAuthException error = await Assert.ThrowsExactlyAsync<DmdataOAuthException>(
            () => service.GetAccessTokenAsync(ClientId, Scopes, CancellationToken.None));
        Assert.IsFalse(error.RequiresAuthorization);
        Assert.IsNotNull(service.Session);
        Assert.IsTrue(File.Exists(SessionPath));
    }

    [TestMethod]
    public async Task MissingScopeAndWrongClientFailBeforeSendingTokens()
    {
        await SeedAsync(expired: false);
        using var service = CreateService(_ => throw new AssertFailedException("No HTTP request expected."));
        DmdataOAuthException scopeError = await Assert.ThrowsExactlyAsync<DmdataOAuthException>(
            () => service.GetAccessTokenAsync(ClientId, ["eew.get.forecast"], CancellationToken.None));
        Assert.AreEqual("scope_required", scopeError.ErrorCode);
        DmdataOAuthException clientError = await Assert.ThrowsExactlyAsync<DmdataOAuthException>(
            () => service.GetAccessTokenAsync("CId.other", Scopes, CancellationToken.None));
        Assert.AreEqual("client_required", clientError.ErrorCode);
    }

    [TestMethod]
    public async Task RevokeSendsBothTokensAndClearsSessionOnlyOnSuccess()
    {
        await SeedAsync(expired: false);
        var sent = new List<string>();
        using var service = CreateService(async request =>
        {
            Assert.AreEqual("/account/oauth2/v1/revoke", request.RequestUri!.AbsolutePath);
            Dictionary<string, string> form = DmdataOAuthCallbackListener.ParseQuery(await request.Content!.ReadAsStringAsync())!;
            sent.Add(form["token"]);
            return Response(HttpStatusCode.OK, string.Empty);
        });
        await service.RevokeAsync(CancellationToken.None);
        CollectionAssert.AreEquivalent(RevokedTokens, sent);
        Assert.IsNull(service.Session);
        Assert.IsFalse(File.Exists(SessionPath));
    }

    [TestMethod]
    public async Task CancelledBrowserFlowDoesNotSendTokenRequest()
    {
        using var service = CreateService(_ => throw new AssertFailedException("No token request expected."));
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.AuthorizeAsync(ClientId, Scopes,
            _ => stop.Cancel(), stop.Token));
        Assert.IsNull(service.Session);
    }

    [TestMethod]
    public async Task FailedSessionSaveDoesNotReportAuthorizationSuccess()
    {
        // A directory at the session-file path reliably prevents the atomic file move.
        Directory.CreateDirectory(SessionPath);
        using var callbackClient = new HttpClient();
        Task<HttpResponseMessage>? callback = null;
        using var service = CreateService(_ => Task.FromResult(TokenResponse(includeRefresh: true)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.AuthorizeAsync(ClientId, Scopes, uri =>
        {
            Dictionary<string, string> query = DmdataOAuthCallbackListener.ParseQuery(uri.Query)!;
            callback = callbackClient.GetAsync(query["redirect_uri"] + "?code=ACe.test&state=" + query["state"], timeout.Token);
        }, timeout.Token));
        using HttpResponseMessage response = await callback!;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNull(service.Session);
        await Assert.ThrowsExactlyAsync<DmdataOAuthException>(
            () => service.GetAccessTokenAsync(ClientId, Scopes, timeout.Token));
    }

    [TestMethod]
    public void DuplicateCallbackParametersAreRejected()
    {
        Assert.IsNull(DmdataOAuthCallbackListener.ParseQuery("?state=a&state=b&code=x"));
    }

    private string SessionPath => Path.Combine(_directory, "dmdata-oauth.json");

    private Task SeedAsync(bool expired) => new DmdataOAuthSessionStore(SessionPath).SaveAsync(
        new(ClientId, "ATn.old", "ARh.refresh", string.Join(' ', Scopes),
            expired ? _clock.UtcNow.AddSeconds(-1) : _clock.UtcNow.AddHours(6)), CancellationToken.None);

    private DmdataOAuthService CreateService(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new StubHandler(send)), new DmdataOAuthSessionStore(SessionPath), _clock);

    private static HttpResponseMessage TokenResponse(bool includeRefresh) => Response(HttpStatusCode.OK,
        "{\"access_token\":\"ATn.new\",\"token_type\":\"Bearer\",\"expires_in\":21600," +
        (includeRefresh ? "\"refresh_token\":\"ARh.refresh\"," : string.Empty) +
        "\"scope\":\"socket.start socket.close telegram.get.weather\"}");

    private static HttpResponseMessage Response(HttpStatusCode status, string json) => new(status)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
