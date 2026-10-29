using System.Net;
using EEWTelop.Application.Logging;

namespace EEWTelop.Infrastructure.Dmdata.History;

public sealed partial class NiiJmaXmlHistoryMessageSource
{
    private const int MaximumRequestAttempts = 2;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    private async Task<string> GetStringAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(DatabaseBaseUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("NII history requests are restricted to the configured HTTPS host.");

        string resource = uri.AbsolutePath.EndsWith("report_day.pl", StringComparison.Ordinal)
            ? "日付別一覧" : "個別電文";
        for (int attempt = 1; attempt <= MaximumRequestAttempts; attempt++)
        {
            await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan delay = _retryDelay;
            bool mayRetry = attempt < MaximumRequestAttempts;
            Exception retryError;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_requestTimeout);
                using HttpResponseMessage response = await _httpClient.GetAsync(
                    uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
                    if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
                        retryAfter = date - _clock.UtcNow;
                    if (retryAfter > MaximumRetryDelay)
                        mayRetry = false; // Do not retry earlier than the server requested.
                    else if (retryAfter > delay)
                        delay = retryAfter.Value;
                    throw new HttpRequestException(
                        $"NIIの{resource}取得に失敗しました。HTTP {(int)response.StatusCode} ({response.ReasonPhrase})。時間をおいて再取得してください。",
                        inner: null, response.StatusCode);
                }

                return await ReadResponseAsync(response, uri, maximumBytes, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                var failure = new TimeoutException(
                    $"NIIの{resource}取得がタイムアウトしました（試行{attempt}/{MaximumRequestAttempts}）。時間をおいて再取得してください。取得済みのXMLキャッシュは保持しています。",
                    exception);
                if (!mayRetry) throw failure;
                retryError = failure;
            }
            catch (HttpRequestException exception) when (
                mayRetry && !cancellationToken.IsCancellationRequested && IsTransientStatus(exception.StatusCode))
            {
                retryError = exception;
            }
            catch (IOException exception) when (mayRetry && !cancellationToken.IsCancellationRequested)
            {
                retryError = exception;
            }

            await LogAsync(AppLogLevel.Warning, "NiiHistoryRequestRetry",
                $"NIIの{resource}取得を{delay.TotalSeconds:0.##}秒後に再試行します（{attempt + 1}/{MaximumRequestAttempts}）。理由: {retryError.Message}",
                cancellationToken, retryError).ConfigureAwait(false);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("NII request attempts exhausted.");
    }

    private static bool IsTransientStatus(HttpStatusCode? status) => status is null or
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
