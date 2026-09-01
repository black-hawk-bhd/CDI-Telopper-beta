using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.P2P.Configuration;
using EEWTelop.Infrastructure.P2P.Normalization;

namespace EEWTelop.Infrastructure.P2P.Recovery;

internal interface IP2pRestRecoveryClient
{
    IAsyncEnumerable<RawProviderMessage> FetchRecentAsync(
        DateTimeOffset issuedAfter,
        CancellationToken cancellationToken);
}

public sealed class P2pRestRecoveryClient : IP2pRestRecoveryClient
{
    private const int RecentLimit = 5;
    private readonly HttpClient _httpClient;
    private readonly ProviderOptions _options;
    private readonly IClock _clock;

    public P2pRestRecoveryClient(
        HttpClient httpClient,
        ProviderOptions options,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _httpClient = httpClient;
        _options = options;
        _clock = clock;
    }

    public async IAsyncEnumerable<RawProviderMessage> FetchRecentAsync(
        DateTimeOffset issuedAfter,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        await foreach (RawProviderMessage message in FetchEndpointAsync(
            "jma/quake",
            issuedAfter,
            cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }

        await foreach (RawProviderMessage message in FetchEndpointAsync(
            "jma/tsunami",
            issuedAfter,
            cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    private async IAsyncEnumerable<RawProviderMessage> FetchEndpointAsync(
        string relativePath,
        DateTimeOffset issuedAfter,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(
            $"{_options.RestBaseUri.ToString().TrimEnd('/')}/{relativePath}?limit={RecentLimit}&order=-1",
            UriKind.Absolute);
        using HttpResponseMessage response = await _httpClient.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        SourceMode sourceMode = _options.Mode == EEWTelop.Application.Configuration.ProviderMode.Production
            ? SourceMode.Production
            : SourceMode.Sandbox;
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (!TryReadIssueTime(element, out DateTimeOffset issuedAt) ||
                issuedAt <= issuedAfter)
            {
                continue;
            }

            yield return new RawProviderMessage(
                "p2pquake",
                element.GetRawText(),
                sourceMode,
                _clock.UtcNow);
        }
    }

    private static bool TryReadIssueTime(
        JsonElement element,
        out DateTimeOffset issuedAt)
    {
        issuedAt = default;
        if (!element.TryGetProperty("issue", out JsonElement issue) ||
            issue.ValueKind != JsonValueKind.Object ||
            !issue.TryGetProperty("time", out JsonElement time) ||
            time.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return P2pDateTimeParser.TryParse(time.GetString(), out issuedAt);
    }
}
