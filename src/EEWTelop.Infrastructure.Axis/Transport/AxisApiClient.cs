using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EEWTelop.Infrastructure.Axis.Configuration;

namespace EEWTelop.Infrastructure.Axis.Transport;

internal sealed class AxisApiClient
{
    private readonly HttpClient _httpClient;
    private readonly AxisProviderOptions _options;

    public AxisApiClient(HttpClient httpClient, AxisProviderOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<IReadOnlyList<Uri>> GetServersAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(_options.ApiBaseUri, "server/list/"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _options.AccessToken);
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AxisApiException(
                (int)response.StatusCode,
                $"AXIS server discovery failed ({(int)response.StatusCode}).");
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var servers = new List<Uri>();
        CollectWebSocketUris(document.RootElement, servers);
        if (servers.Count == 0)
        {
            throw new InvalidDataException("AXIS returned no usable WebSocket server.");
        }

        return servers.Distinct().ToArray();
    }

    private static void CollectWebSocketUris(JsonElement element, ICollection<Uri> output)
    {
        if (element.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(element.GetString(), UriKind.Absolute, out Uri? uri) &&
            uri.Scheme is "ws" or "wss")
        {
            output.Add(ToSocketUri(uri));
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                CollectWebSocketUris(property.Value, output);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                CollectWebSocketUris(item, output);
            }
        }
    }

    private static Uri ToSocketUri(Uri server)
    {
        var builder = new UriBuilder(server)
        {
            Path = server.AbsolutePath.TrimEnd('/') + "/socket",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }
}

internal sealed class AxisApiException : Exception
{
    public AxisApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
