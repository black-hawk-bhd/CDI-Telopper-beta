using System.Globalization;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Dmdata.Configuration;
using EEWTelop.Infrastructure.Dmdata.Security;

namespace EEWTelop.Infrastructure.Dmdata.Transport;

public sealed class DmdataContractException(int statusCode) : Exception(statusCode switch
{
    401 => "契約情報を取得できませんでした。APIキーまたはOAuth認証を確認してください。",
    403 => "契約情報の閲覧権限（contract.list）または接続元の制限を確認してください。OAuthは「契約情報も認可」から再認証できます。",
    429 => "契約情報の取得回数が制限されています。時間をおいて更新してください。",
    _ => "契約情報を取得できませんでした。接続環境とDMDATA.JPのサービス状況を確認してください。",
})
{
    public int StatusCode { get; } = statusCode;
}

public sealed class DmdataContractService : IDmdataContractService, IDisposable
{
    private static readonly Uri ContractEndpoint = new("https://api.dmdata.jp/v2/contract");
    private static readonly string[] ContractScopes = ["contract.list"];
    private readonly HttpClient _httpClient;
    private readonly IDmdataOAuthService? _oauthService;
    private readonly IClock _clock;

    public DmdataContractService(IDmdataOAuthService? oauthService, IClock clock)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(20) }, oauthService, clock)
    {
    }

    internal DmdataContractService(HttpClient httpClient, IDmdataOAuthService? oauthService, IClock clock)
    {
        _httpClient = httpClient;
        _oauthService = oauthService;
        _clock = clock;
    }

    public async Task<DmdataContractSnapshot> GetAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // This account-data request always goes to the official API, never an editable base URL.
        DmdataProviderOptions options = DmdataProviderOptions.FromSettings(settings);
        IDmdataCredentialProvider credentials = options.AuthenticationMode switch
        {
            DmdataAuthenticationMode.ApiKey when !string.IsNullOrWhiteSpace(options.Credential) =>
                new FixedDmdataCredentialProvider(options.Credential, DmdataAuthenticationMode.ApiKey),
            DmdataAuthenticationMode.OAuthAccessToken when _oauthService is not null =>
                new OAuthDmdataCredentialProvider(_oauthService, options.OAuthClientId, ContractScopes),
            _ => throw new DmdataContractException(401),
        };
        var items = new List<DmdataContractInfo>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        Uri uri = ContractEndpoint;
        for (int page = 0; page < 20; page++)
        {
            using HttpResponseMessage response = await DmdataAuthorizedHttpClient.SendAsync(
                _httpClient, credentials, () => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new DmdataContractException((int)response.StatusCode);
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = ParseResponse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new DmdataContractException(0);
            if (ReadString(root, "status") != "ok")
            {
                int code = root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("code", out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int errorCode)
                    ? errorCode : 0;
                // Never expose error.message, credentials or raw response bodies in UI/logs.
                throw new DmdataContractException(code);
            }
            if (!root.TryGetProperty("items", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
                throw new DmdataContractException(0);
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                items.Add(ReadItem(entry));
                if (items.Count > 10000) throw new DmdataContractException(0);
            }
            if (root.TryGetProperty("nextToken", out JsonElement nextToken) &&
                nextToken.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new DmdataContractException(0);
            string cursor = ReadString(root, "nextToken");
            if (cursor.Length == 0) return new(items.AsReadOnly(), _clock.UtcNow);
            if (cursor.Length > 2048 || !cursors.Add(cursor)) throw new DmdataContractException(0);
            uri = new Uri(ContractEndpoint.AbsoluteUri + "?cursorToken=" + Uri.EscapeDataString(cursor));
        }
        throw new DmdataContractException(0);
    }

    private static DmdataContractInfo ReadItem(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out JsonElement id) ||
            !entry.TryGetProperty("isValid", out JsonElement valid) ||
            valid.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !entry.TryGetProperty("price", out JsonElement price) || price.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("start", out JsonElement start))
            throw new DmdataContractException(0);
        string? contractId = id.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when id.TryGetInt64(out long number) && number >= 0 =>
                number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String when id.GetString() is { Length: > 0 } text && text.All(char.IsAsciiDigit) => text,
            _ => throw new DmdataContractException(0),
        };
        DateTimeOffset? startedAt = start.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when start.TryGetDateTimeOffset(out DateTimeOffset date) => date,
            _ => throw new DmdataContractException(0),
        };
        if (contractId is null && valid.GetBoolean()) throw new DmdataContractException(0);
        string planName = ReadString(entry, "planName");
        string classification = ReadString(entry, "classification");
        if (string.IsNullOrWhiteSpace(planName) || string.IsNullOrWhiteSpace(classification))
            throw new DmdataContractException(0);
        return new(contractId, ReadNonNegativeInt(entry, "planId"), planName, classification,
            ReadNonNegativeInt(price, "day"), ReadNonNegativeInt(price, "month"), startedAt,
            valid.GetBoolean(), ReadNonNegativeInt(entry, "connectionCounts"));
    }

    private static int ReadNonNegativeInt(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number) && number >= 0 ? number : throw new DmdataContractException(0);

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static JsonDocument ParseResponse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { throw new DmdataContractException(0); }
    }

    public void Dispose() => _httpClient.Dispose();
}
