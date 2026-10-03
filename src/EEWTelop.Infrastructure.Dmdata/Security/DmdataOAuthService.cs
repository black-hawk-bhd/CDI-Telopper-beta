using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Abstractions;

namespace EEWTelop.Infrastructure.Dmdata.Security;

public sealed class DmdataOAuthException(string errorCode, bool requiresAuthorization)
    : Exception(errorCode switch
    {
        "invalid_grant" => "DMDATA OAuth認証の有効期限が切れたか、認可が取り消されました。ブラウザーで再認証してください。",
        "scope_required" => "選択した情報種別の権限がありません。DMDATA OAuthを解除して再認証してください。",
        "client_required" => "DMDATA OAuthをブラウザーで認証してください。独自クライアントを使う場合はIDも確認してください。",
        "authorization_denied" => "DMDATA OAuthの認可が拒否されました。",
        "invalid_client" => "DMDATA OAuthクライアントIDまたは公開クライアントの登録設定を確認してください。",
        "invalid_response" => "DMDATA OAuthサーバーの応答を確認できませんでした。",
        _ => "DMDATA OAuth認証に失敗しました。接続状況とクライアントの登録設定を確認してください。",
    })
{
    public string ErrorCode { get; } = errorCode;
    public bool RequiresAuthorization { get; } = requiresAuthorization;
}

public sealed class DmdataOAuthService : IDmdataOAuthService, IDisposable
{
    public const string RegisteredRedirectUri = "http://127.0.0.1/oauth2/callback";
    private static readonly Uri TokenEndpoint = new("https://manager.dmdata.jp/account/oauth2/v1/token");
    private static readonly Uri RevokeEndpoint = new("https://manager.dmdata.jp/account/oauth2/v1/revoke");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly DmdataOAuthSessionStore _store;
    private readonly IClock _clock;
    private DmdataOAuthTokens? _tokens;

    public DmdataOAuthService(string sessionPath, IClock clock)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(20) }, new DmdataOAuthSessionStore(sessionPath), clock)
    {
    }

    internal DmdataOAuthService(HttpClient httpClient, DmdataOAuthSessionStore store, IClock clock)
    {
        _httpClient = httpClient;
        _store = store;
        _clock = clock;
        _tokens = _store.Load();
    }

    public DmdataOAuthSessionInfo? Session => _tokens is { } tokens
        ? new(tokens.ClientId, tokens.Scope, tokens.ExpiresAtUtc) : null;

    public async Task AuthorizeAsync(string clientId, IReadOnlyList<string> scopes,
        Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        ArgumentNullException.ThrowIfNull(openBrowser);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokens is not null) await RevokeCoreAsync(cancellationToken).ConfigureAwait(false);
            using var listener = new DmdataOAuthCallbackListener();
            string state = Base64Url(RandomNumberGenerator.GetBytes(32));
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            string scope = string.Join(' ', scopes.Distinct(StringComparer.Ordinal));
            Uri authorizationUri = BuildAuthorizationUri(clientId, scope, listener.RedirectUri, state, verifier);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            openBrowser(authorizationUri);
            string code = await listener.WaitForCodeAsync(state, timeout.Token).ConfigureAwait(false);
            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["grant_type"] = "authorization_code",
                ["code"] = code, ["redirect_uri"] = listener.RedirectUri.AbsoluteUri,
                ["code_verifier"] = verifier,
            };
            DmdataOAuthTokens tokens = await RequestTokensAsync(clientId, form, null, scope, timeout.Token).ConfigureAwait(false);
            await SaveAsync(tokens, timeout.Token).ConfigureAwait(false);
            EnsureScopes(tokens, scopes);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> GetAccessTokenAsync(string clientId, IReadOnlyList<string> scopes,
        CancellationToken cancellationToken, string? rejectedAccessToken = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DmdataOAuthTokens tokens = _tokens ?? throw new DmdataOAuthException("client_required", true);
            if (!string.Equals(tokens.ClientId, clientId, StringComparison.Ordinal))
                throw new DmdataOAuthException("client_required", true);
            EnsureScopes(tokens, scopes);
            if (string.IsNullOrWhiteSpace(tokens.AccessToken) || tokens.ExpiresAtUtc <= _clock.UtcNow.AddMinutes(1) ||
                rejectedAccessToken is not null && string.Equals(rejectedAccessToken, tokens.AccessToken, StringComparison.Ordinal))
            {
                try
                {
                    tokens = await RequestTokensAsync(clientId, new Dictionary<string, string>
                    {
                        ["client_id"] = clientId, ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken,
                    }, tokens.RefreshToken, tokens.Scope, cancellationToken).ConfigureAwait(false);
                }
                catch (DmdataOAuthException exception) when (exception.ErrorCode == "invalid_grant")
                {
                    _store.Clear();
                    _tokens = null;
                    throw;
                }
                await SaveAsync(tokens, cancellationToken).ConfigureAwait(false);
                EnsureScopes(tokens, scopes);
            }
            return tokens.AccessToken;
        }
        finally { _gate.Release(); }
    }

    public async Task RevokeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RevokeCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task RevokeCoreAsync(CancellationToken cancellationToken)
    {
        if (_tokens is not { } tokens) return;
        // Keep the session on network failure so revocation can be retried.
        foreach (string token in new[] { tokens.RefreshToken, tokens.AccessToken }.Where(static t => !string.IsNullOrEmpty(t)))
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = tokens.ClientId, ["token"] = token });
            using HttpResponseMessage response = await _httpClient.PostAsync(RevokeEndpoint, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new DmdataOAuthException("revoke_failed", false);
        }
        _store.Clear();
        _tokens = null;
    }

    private async Task<DmdataOAuthTokens> RequestTokensAsync(string clientId, Dictionary<string, string> form,
        string? existingRefreshToken, string requestedScope, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using HttpResponseMessage response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = ParseResponse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new DmdataOAuthException("invalid_response", false);
        if (!response.IsSuccessStatusCode || root.TryGetProperty("error", out _))
        {
            string error = ReadString(root, "error");
            // Never include raw server descriptions, tokens or response bodies in logs/UI.
            bool terminal = (int)response.StatusCode < 500 && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests;
            throw new DmdataOAuthException(error is "invalid_grant" or "invalid_client" or "invalid_scope" ? error : "token_request_failed", terminal);
        }
        string access = ReadString(root, "access_token");
        string refresh = ReadString(root, "refresh_token");
        string scope = ReadString(root, "scope");
        if (string.IsNullOrEmpty(scope)) scope = requestedScope;
        if (string.IsNullOrEmpty(refresh)) refresh = existingRefreshToken ?? string.Empty;
        if (ReadString(root, "token_type") != "Bearer" || string.IsNullOrWhiteSpace(access) ||
            access.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(refresh) ||
            !root.TryGetProperty("expires_in", out JsonElement expires) || !expires.TryGetInt32(out int seconds) ||
            seconds <= 0 || seconds > 86400)
            throw new DmdataOAuthException("invalid_response", false);
        return new(clientId, access, refresh, scope, _clock.UtcNow.AddSeconds(seconds));
    }

    private async Task SaveAsync(DmdataOAuthTokens tokens, CancellationToken cancellationToken)
    {
        await _store.SaveAsync(tokens, cancellationToken).ConfigureAwait(false);
        _tokens = tokens;
    }

    internal static Uri BuildAuthorizationUri(string clientId, string scope, Uri redirectUri, string state, string verifier)
    {
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var values = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = scope, ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
        };
        return new Uri("https://manager.dmdata.jp/account/oauth2/v1/auth?" +
            string.Join('&', values.Select(static pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ValidateClientId(string clientId)
    {
        if (!clientId.StartsWith("CId.", StringComparison.Ordinal) || clientId.Length <= 4 || clientId.Any(char.IsWhiteSpace))
            throw new DmdataOAuthException("client_required", true);
    }

    private static void EnsureScopes(DmdataOAuthTokens tokens, IReadOnlyList<string> scopes)
    {
        HashSet<string> granted = tokens.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (scopes.Any(scope => !granted.Contains(scope))) throw new DmdataOAuthException("scope_required", true);
    }

    private static string ReadString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static JsonDocument ParseResponse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { throw new DmdataOAuthException("invalid_response", false); }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _gate.Dispose();
    }
}
