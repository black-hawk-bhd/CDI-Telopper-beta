using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Dmdata.Configuration;
using EEWTelop.Infrastructure.Dmdata.Security;

namespace EEWTelop.Infrastructure.Dmdata.Transport;

internal sealed class DmdataSocketApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _httpClient;
    private readonly DmdataProviderOptions _options;
    private readonly IDmdataCredentialProvider _credentialProvider;

    public DmdataSocketApiClient(
        HttpClient httpClient,
        DmdataProviderOptions options,
        IDmdataCredentialProvider credentialProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialProvider);
        _httpClient = httpClient;
        _options = options;
        _credentialProvider = credentialProvider;
    }

    public async Task<DmdataSocketTicket> StartAsync(CancellationToken cancellationToken)
    {
        var requestBody = new DmdataSocketStartRequest(
            _options.Classifications.ToArray(),
            _options.TelegramTypes.ToArray(),
            _options.IncludeTestTelegrams ? "including" : "no",
            "CDI-Telopper",
            "raw");
        using HttpResponseMessage response = await SendAuthorizedAsync(() => new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_options.ApiBaseUri, "socket"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        }, cancellationToken).ConfigureAwait(false);
        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        DmdataSocketStartResponse? payload = JsonSerializer.Deserialize<DmdataSocketStartResponse>(
            responseJson,
            JsonOptions);
        if (!response.IsSuccessStatusCode || payload?.Status != "ok" ||
            payload.Websocket is null || string.IsNullOrWhiteSpace(payload.Websocket.Url))
        {
            string detail = payload?.Error?.Message ?? response.ReasonPhrase ?? "Unknown API error";
            throw new DmdataApiException((int)response.StatusCode, detail);
        }

        return new DmdataSocketTicket(
            ReadSocketId(payload.Websocket.Id),
            new Uri(payload.Websocket.Url, UriKind.Absolute),
            payload.Websocket.Protocol is { Length: > 0 }
                ? payload.Websocket.Protocol[0]
                : "dmdata.v2");
    }

    public async Task CloseAsync(string socketId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(socketId))
        {
            return;
        }

        using HttpResponseMessage response = await SendAuthorizedAsync(() => new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(_options.ApiBaseUri, $"socket/{Uri.EscapeDataString(socketId)}")), cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            throw new DmdataApiException((int)response.StatusCode, detail);
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = createRequest();
            DmdataCredential credential = await _credentialProvider.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
            AddAuthorization(request, credential);
            HttpResponseMessage response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized || attempt != 0 ||
                credential.AuthenticationMode != DmdataAuthenticationMode.OAuthAccessToken)
                return response;
            bool refreshed;
            try
            {
                refreshed = await _credentialProvider.RefreshAfterUnauthorizedAsync(
                    credential.Secret, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                response.Dispose();
                throw;
            }
            if (!refreshed) return response;
            response.Dispose();
        }
    }

    private static void AddAuthorization(HttpRequestMessage request, DmdataCredential credential)
    {
        request.Headers.Authorization = credential.AuthenticationMode switch
        {
            DmdataAuthenticationMode.ApiKey => new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.Secret + ":"))),
            DmdataAuthenticationMode.OAuthAccessToken => new AuthenticationHeaderValue(
                "Bearer",
                credential.Secret),
            _ => throw new InvalidOperationException("Unsupported DMDATA.JP authentication mode."),
        };
    }

    private static string ReadSocketId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => id.GetString() ?? string.Empty,
        JsonValueKind.Number => id.GetRawText(),
        _ => string.Empty,
    };

    private sealed record DmdataSocketStartRequest(
        [property: JsonPropertyName("classifications")] string[] Classifications,
        [property: JsonPropertyName("types")] string[] Types,
        [property: JsonPropertyName("test")] string Test,
        [property: JsonPropertyName("appName")] string AppName,
        [property: JsonPropertyName("formatMode")] string FormatMode);

    private sealed record DmdataSocketStartResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("websocket")] DmdataWebSocketResponse? Websocket,
        [property: JsonPropertyName("error")] DmdataErrorResponse? Error);

    private sealed record DmdataWebSocketResponse(
        [property: JsonPropertyName("id")] JsonElement Id,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("protocol")] string[]? Protocol);

    private sealed record DmdataErrorResponse(
        [property: JsonPropertyName("message")] string? Message);
}

internal sealed record DmdataSocketTicket(
    string SocketId,
    Uri WebSocketUri,
    string Protocol);

internal sealed class DmdataApiException : Exception
{
    public DmdataApiException(int statusCode, string message)
        : base($"DMDATA.JP API returned {statusCode}: {message}")
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
