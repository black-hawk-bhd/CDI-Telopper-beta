using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Abstractions;

namespace EEWTelop.Infrastructure.Axis.Security;

public sealed class AxisTokenRefreshService : IAxisTokenRefreshService, IDisposable
{
    internal static readonly TimeSpan RefreshWindow = TimeSpan.FromDays(7);

    private readonly HttpClient _httpClient;
    private readonly IClock _clock;
    private bool _disposed;

    public AxisTokenRefreshService(IClock clock)
        : this(clock, new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
    {
    }

    internal AxisTokenRefreshService(IClock clock, HttpClient httpClient)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async ValueTask<AxisTokenRefreshResult> RefreshIfDueAsync(
        Uri apiBaseUri,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(apiBaseUri);
        if (!TryReadExpiration(accessToken, out DateTimeOffset expiresAtUtc))
        {
            return new AxisTokenRefreshResult(
                AxisTokenRefreshOutcome.InvalidToken,
                accessToken);
        }

        TimeSpan remaining = expiresAtUtc - _clock.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return new AxisTokenRefreshResult(
                AxisTokenRefreshOutcome.Expired,
                accessToken,
                expiresAtUtc);
        }

        // AXIS permits refresh only when less than seven days remain.  Keep this
        // comparison strict so the endpoint is never polled before it is due.
        if (remaining >= RefreshWindow)
        {
            return new AxisTokenRefreshResult(
                AxisTokenRefreshOutcome.NotDue,
                accessToken,
                expiresAtUtc);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(EnsureTrailingSlash(apiBaseUri), "token/refresh/"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            return new AxisTokenRefreshResult(
                AxisTokenRefreshOutcome.ContractExpired,
                accessToken,
                expiresAtUtc);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new AxisTokenRefreshResult(
                AxisTokenRefreshOutcome.AuthorizationFailed,
                accessToken,
                expiresAtUtc);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"AXIS token refresh failed ({(int)response.StatusCode}).",
                inner: null,
                response.StatusCode);
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("AXIS returned an invalid token refresh response.");
        }

        string status = root.TryGetProperty("status", out JsonElement statusElement) &&
            statusElement.ValueKind == JsonValueKind.String
            ? statusElement.GetString() ?? string.Empty
            : string.Empty;
        string returnedToken = root.TryGetProperty("token", out JsonElement tokenElement) &&
            tokenElement.ValueKind == JsonValueKind.String
            ? tokenElement.GetString() ?? string.Empty
            : string.Empty;

        if (string.Equals(status, "not due for refresh yet", StringComparison.OrdinalIgnoreCase))
        {
            return new AxisTokenRefreshResult(
                AxisTokenRefreshOutcome.Unchanged,
                accessToken,
                expiresAtUtc);
        }

        if (!string.Equals(status, "generate a new token", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(returnedToken) ||
            !TryReadExpiration(returnedToken, out DateTimeOffset refreshedExpiresAtUtc))
        {
            throw new InvalidDataException("AXIS returned an invalid token refresh response.");
        }

        return new AxisTokenRefreshResult(
            AxisTokenRefreshOutcome.Refreshed,
            returnedToken,
            refreshedExpiresAtUtc);
    }

    internal static bool TryReadExpiration(string? token, out DateTimeOffset expiresAtUtc)
    {
        expiresAtUtc = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string[] segments = token.Split('.');
        if (segments.Length != 3)
        {
            return false;
        }

        try
        {
            string payload = segments[1]
                .Replace('-', '+')
                .Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using JsonDocument document = JsonDocument.Parse(
                Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            if (!document.RootElement.TryGetProperty("exp", out JsonElement expElement))
            {
                return false;
            }

            long seconds;
            if (expElement.ValueKind == JsonValueKind.Number)
            {
                if (!expElement.TryGetInt64(out seconds))
                {
                    return false;
                }
            }
            else if (expElement.ValueKind == JsonValueKind.String &&
                long.TryParse(
                    expElement.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long parsed))
            {
                seconds = parsed;
            }
            else
            {
                return false;
            }

            expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or
            JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/')
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
}
