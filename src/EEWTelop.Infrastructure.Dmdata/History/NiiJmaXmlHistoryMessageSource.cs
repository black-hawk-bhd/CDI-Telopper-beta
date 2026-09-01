using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Dmdata.History;

public sealed partial class NiiJmaXmlHistoryMessageSource : IHistoryMessageSource
{
    public const string ProviderName = "nii-jma-xml";
    public const int MaximumFetchCount = 20;

    // A daily archive index can exceed 2 MiB on high-volume weather days.
    // Keep a bounded, host-restricted read while allowing realistic daily pages.
    private const int MaximumIndexBytes = 16 * 1024 * 1024;
    private const int MaximumTelegramPageBytes = 2 * 1024 * 1024;
    private const int MaximumXmlBytes = 1024 * 1024;
    private static readonly Uri DatabaseBaseUri = new("https://agora.ex.nii.ac.jp/");
    private static readonly DateOnly FirstArchiveDate = new(2012, 12, 1);
    private static readonly HashSet<string> QuakeCodes =
        new(StringComparer.Ordinal)
        {
            "VXSE51", "VXSE52", "VXSE53", "VXSE62", "VYSE50", "VYSE60",
        };
    private static readonly HashSet<string> TsunamiCodes =
        new(StringComparer.Ordinal) { "VTSE41", "VTSE51", "VTSE52" };
    private static readonly HashSet<string> WeatherWarningCodes =
        new(StringComparer.Ordinal)
        {
            "VPWW55", "VPWW56", "VPWW57", "VPWW58", "VPWW59", "VPWW60", "VPWW61",
        };

    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly IClock _clock;
    private readonly IAppLogWriter? _logWriter;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestInterval;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _lastNetworkRequestAt = DateTimeOffset.MinValue;

    public NiiJmaXmlHistoryMessageSource(
        string cacheDirectory,
        IClock clock,
        IAppLogWriter? logWriter = null)
        : this(CreateHttpClient(), cacheDirectory, clock, ownsHttpClient: true, logWriter: logWriter)
    {
    }

    public NiiJmaXmlHistoryMessageSource(
        HttpClient httpClient,
        string cacheDirectory,
        IClock clock,
        bool ownsHttpClient = false,
        IAppLogWriter? logWriter = null,
        TimeSpan? requestInterval = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(clock);
        _httpClient = httpClient;
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _clock = clock;
        _ownsHttpClient = ownsHttpClient;
        _logWriter = logWriter;
        _requestInterval = requestInterval ?? TimeSpan.FromSeconds(1);
        if (_requestInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestInterval));
        }
    }

    public async Task<IReadOnlyList<RawProviderMessage>> FetchAsync(
        HistoryFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Api != HistoryApi.NiiJmaXml)
        {
            throw new ArgumentException("The NII source only handles NiiJmaXml history requests.", nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(request.NiiReportUrl))
        {
            string reportId = ExtractReportIdFromUrl(request.NiiReportUrl);
            Directory.CreateDirectory(_cacheDirectory);
            await LogAsync(
                AppLogLevel.Information,
                "NiiDirectTelegramFetchStarted",
                $"NII個別電文URLからXMLを取得します。id={reportId}",
                cancellationToken).ConfigureAwait(false);

            try
            {
                (string xml, bool cacheHit) = await FetchTelegramXmlAsync(
                    reportId,
                    cancellationToken).ConfigureAwait(false);
                await LogAsync(
                    AppLogLevel.Information,
                    "NiiDirectTelegramFetchCompleted",
                    $"NII個別電文を1件取得しました（キャッシュ{(cacheHit ? 1 : 0)}件）。データ提供：気象庁防災情報XMLデータベース（国立情報学研究所）",
                    cancellationToken).ConfigureAwait(false);
                return
                [
                    new RawProviderMessage(
                        ProviderName,
                        xml,
                        SourceMode.HistoryRehearsal,
                        ReadArchiveReceivedAt(reportId) ?? _clock.UtcNow)
                    {
                        ContentFormat = RawProviderContentFormat.JmaXml,
                    },
                ];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                await LogAsync(
                    AppLogLevel.Error,
                    "NiiDirectTelegramFetchFailed",
                    $"NII個別電文の取得に失敗しました: {exception.Message}",
                    CancellationToken.None,
                    exception).ConfigureAwait(false);
                throw;
            }
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        if (request.NiiDate < FirstArchiveDate || request.NiiDate > today)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"NII archive date must be between {FirstArchiveDate:yyyy-MM-dd} and {today:yyyy-MM-dd}.");
        }

        int limit = Math.Clamp(request.Limit, 1, MaximumFetchCount);
        HashSet<string> allowedCodes = GetAllowedCodes(request.NiiContent);
        Directory.CreateDirectory(_cacheDirectory);
        await LogAsync(
            AppLogLevel.Information,
            "NiiHistoryFetchStarted",
            $"NII気象庁XML履歴の取得を開始します。date={request.NiiDate:yyyy-MM-dd} content={request.NiiContent} limit={limit}",
            cancellationToken).ConfigureAwait(false);

        try
        {
            Uri indexUri = BuildDailyIndexUri(request.NiiDate);
            string indexHtml = await GetStringAsync(
                indexUri,
                MaximumIndexBytes,
                cancellationToken).ConfigureAwait(false);
            string[] reportIds = ExtractReportIds(indexHtml, allowedCodes)
                .OrderByDescending(static id => id, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
            await LogAsync(
                AppLogLevel.Debug,
                "NiiHistoryIndexFetched",
                $"NII日付別一覧から対象電文を{reportIds.Length}件選択しました。date={request.NiiDate:yyyy-MM-dd}",
                cancellationToken).ConfigureAwait(false);

            var messages = new List<RawProviderMessage>(reportIds.Length);
            int cacheHits = 0;
            foreach (string reportId in reportIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string xml, bool cacheHit) = await FetchTelegramXmlAsync(
                    reportId,
                    cancellationToken).ConfigureAwait(false);
                if (cacheHit)
                {
                    cacheHits++;
                }

                messages.Add(new RawProviderMessage(
                    ProviderName,
                    xml,
                    SourceMode.HistoryRehearsal,
                    ReadArchiveReceivedAt(reportId) ?? _clock.UtcNow)
                {
                    ContentFormat = RawProviderContentFormat.JmaXml,
                });
            }

            await LogAsync(
                AppLogLevel.Information,
                "NiiHistoryFetchCompleted",
                $"NII気象庁XML履歴を{messages.Count}件取得しました（キャッシュ{cacheHits}件）。データ提供：気象庁防災情報XMLデータベース（国立情報学研究所）",
                cancellationToken).ConfigureAwait(false);
            return messages;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            await LogAsync(
                AppLogLevel.Error,
                "NiiHistoryFetchFailed",
                $"NII気象庁XML履歴の取得に失敗しました: {exception.Message}",
                CancellationToken.None,
                exception).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _requestGate.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    internal static IReadOnlyList<string> ExtractReportIds(
        string html,
        IReadOnlySet<string> allowedCodes)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(allowedCodes);
        return ReportIdPattern().Matches(html)
            .Select(static match => new
            {
                Id = match.Groups["id"].Value,
                Code = match.Groups["code"].Value,
            })
            .Where(item => allowedCodes.Contains(item.Code))
            .Select(static item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static string ExtractXml(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        Match match = XmlPrePattern().Match(html);
        if (!match.Success)
        {
            throw new InvalidDataException("NII telegram page did not contain an XML block.");
        }

        string xml = WebUtility.HtmlDecode(match.Groups["xml"].Value).Trim();
        if (Encoding.UTF8.GetByteCount(xml) > MaximumXmlBytes ||
            (!xml.StartsWith("<Report", StringComparison.Ordinal) &&
             !xml.StartsWith("<?xml", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("NII telegram XML was empty, oversized, or had an unexpected root.");
        }

        return xml;
    }

    internal static string ExtractReportIdFromUrl(string reportUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportUrl);
        if (reportUrl.Length > 2048)
        {
            throw new InvalidDataException("NII個別電文URLが長すぎます。");
        }

        if (!Uri.TryCreate(reportUrl.Trim(), UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(DatabaseBaseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.AbsolutePath != "/cgi-bin/cps/report_each.pl" &&
             uri.AbsolutePath != "/cgi-bin/cps/report_xml.pl"))
        {
            throw new InvalidDataException(
                "NII個別電文URLはagora.ex.nii.ac.jpのHTTPS個別ページまたはXML表示ページを指定してください。");
        }

        string query = uri.Query;
        if (query.Length <= 1)
        {
            throw new InvalidDataException("NII個別電文URLに電文IDがありません。");
        }

        string? reportId = null;
        foreach (string component in query[1..].Split('&', StringSplitOptions.None))
        {
            int separator = component.IndexOf('=');
            if (separator <= 0 || separator == component.Length - 1)
            {
                throw new InvalidDataException("NII個別電文URLのクエリ形式が正しくありません。");
            }

            string name = Uri.UnescapeDataString(component[..separator]);
            string value = Uri.UnescapeDataString(component[(separator + 1)..]);
            if (!name.Equals("id", StringComparison.Ordinal) || reportId is not null)
            {
                throw new InvalidDataException("NII個別電文URLには電文ID以外のクエリを指定できません。");
            }

            reportId = value;
        }

        if (reportId is null || !ReportIdOnlyPattern().IsMatch(reportId))
        {
            throw new InvalidDataException("NII個別電文URLの電文ID形式が正しくありません。");
        }

        return reportId;
    }

    internal static DateTimeOffset? ReadArchiveReceivedAt(string reportId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        string[] parts = reportId.Split('_');
        if (parts.Length < 2 ||
            !DateTimeOffset.TryParseExact(
                parts[0],
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestamp))
        {
            return null;
        }

        // The second component distinguishes archive entries received during
        // the same second. A tick preserves that order without changing the
        // displayed wall-clock time.
        return long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long sequence) &&
            sequence >= 0 &&
            sequence < TimeSpan.TicksPerSecond
                ? timestamp.AddTicks(sequence)
                : timestamp;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CDI-Telopper/2.0 (manual-history-rehearsal)");
        return client;
    }

    private static HashSet<string> GetAllowedCodes(NiiHistoryContent content) => content switch
    {
        NiiHistoryContent.QuakeAndTsunami => new HashSet<string>(
            QuakeCodes.Concat(TsunamiCodes),
            StringComparer.Ordinal),
        NiiHistoryContent.QuakeOnly => new HashSet<string>(QuakeCodes, StringComparer.Ordinal),
        NiiHistoryContent.TsunamiOnly => new HashSet<string>(TsunamiCodes, StringComparer.Ordinal),
        NiiHistoryContent.WeatherWarningsOnly => new HashSet<string>(
            WeatherWarningCodes,
            StringComparer.Ordinal),
        NiiHistoryContent.AllSupported => new HashSet<string>(
            QuakeCodes.Concat(TsunamiCodes).Concat(WeatherWarningCodes),
            StringComparer.Ordinal),
        NiiHistoryContent.WeatherRain => SingleCode("VPWW55"),
        NiiHistoryContent.WeatherLandslide => SingleCode("VPWW56"),
        NiiHistoryContent.WeatherStormSurge => SingleCode("VPWW57"),
        NiiHistoryContent.WeatherStorm => SingleCode("VPWW58"),
        NiiHistoryContent.WeatherWave => SingleCode("VPWW59"),
        NiiHistoryContent.WeatherHeavySnow => SingleCode("VPWW60"),
        NiiHistoryContent.WeatherOtherAdvisories => SingleCode("VPWW61"),
        _ => throw new ArgumentOutOfRangeException(nameof(content), content, "Unknown NII content filter."),
    };

    private static HashSet<string> SingleCode(string code) =>
        new(StringComparer.Ordinal) { code };

    private static Uri BuildDailyIndexUri(DateOnly date) => new(
        DatabaseBaseUri,
        $"cgi-bin/cps/report_day.pl?date={date:yyyyMMdd}");

    private static Uri BuildTelegramUri(string reportId)
    {
        if (!ReportIdOnlyPattern().IsMatch(reportId))
        {
            throw new InvalidDataException("NII report identifier had an unexpected format.");
        }

        return new Uri(DatabaseBaseUri, $"cgi-bin/cps/report_xml.pl?id={reportId}");
    }

    private async Task<(string Xml, bool CacheHit)> FetchTelegramXmlAsync(
        string reportId,
        CancellationToken cancellationToken)
    {
        string? xml = await TryReadCacheAsync(reportId, cancellationToken).ConfigureAwait(false);
        if (xml is not null)
        {
            return (xml, true);
        }

        Uri telegramUri = BuildTelegramUri(reportId);
        string telegramHtml = await GetStringAsync(
            telegramUri,
            MaximumTelegramPageBytes,
            cancellationToken).ConfigureAwait(false);
        xml = ExtractXml(telegramHtml);
        await WriteCacheAsync(reportId, xml, cancellationToken).ConfigureAwait(false);
        await LogAsync(
            AppLogLevel.Debug,
            "NiiHistoryTelegramFetched",
            $"NIIからXML電文を取得しました。id={reportId}",
            cancellationToken).ConfigureAwait(false);
        return (xml, false);
    }

    private async Task<string> GetStringAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(DatabaseBaseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NII history requests are restricted to the configured HTTPS host.");
        }

        await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"NII history server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                inner: null,
                response.StatusCode);
        }

        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength > maximumBytes)
        {
            throw CreateResponseTooLargeException(uri, contentLength.Value, maximumBytes);
        }

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream(contentLength > 0 && contentLength <= maximumBytes
            ? (int)contentLength.Value
            : 0);
        var buffer = new byte[32 * 1024];
        int total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw CreateResponseTooLargeException(uri, total, maximumBytes);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, total);
    }

    private static InvalidDataException CreateResponseTooLargeException(
        Uri uri,
        long observedBytes,
        int maximumBytes) => new(
            $"NII history response exceeded the configured size limit. " +
            $"resource={uri.AbsolutePath} bytes={observedBytes} limit={maximumBytes}");

    private async Task WaitForRequestSlotAsync(CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan elapsed = _clock.UtcNow - _lastNetworkRequestAt;
            TimeSpan delay = _requestInterval - elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            _lastNetworkRequestAt = _clock.UtcNow;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<string?> TryReadCacheAsync(
        string reportId,
        CancellationToken cancellationToken)
    {
        string path = GetCachePath(reportId);
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumXmlBytes)
        {
            return null;
        }

        string xml = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        return xml.StartsWith("<Report", StringComparison.Ordinal) ||
            xml.StartsWith("<?xml", StringComparison.Ordinal)
                ? xml
                : null;
    }

    private async Task WriteCacheAsync(
        string reportId,
        string xml,
        CancellationToken cancellationToken)
    {
        string path = GetCachePath(reportId);
        string temporary = Path.Combine(
            _cacheDirectory,
            $".{reportId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                xml,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string GetCachePath(string reportId)
    {
        if (!ReportIdOnlyPattern().IsMatch(reportId))
        {
            throw new InvalidDataException("NII report identifier had an unexpected format.");
        }

        string path = Path.GetFullPath(Path.Combine(_cacheDirectory, $"{reportId}.xml"));
        string prefix = _cacheDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NII cache path escaped the configured cache directory.");
        }

        return path;
    }

    private ValueTask LogAsync(
        AppLogLevel level,
        string eventName,
        string message,
        CancellationToken cancellationToken,
        Exception? exception = null) => _logWriter?.WriteAsync(
            new AppLogEntry(_clock.UtcNow, level, eventName, message, exception),
            cancellationToken) ?? ValueTask.CompletedTask;

    [GeneratedRegex(
        @"report_each\.pl\?id=(?<id>\d{14}_\d+_(?<code>[A-Z0-9]+)_\d+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReportIdPattern();

    [GeneratedRegex(
        @"^\d{14}_\d+_[A-Z0-9]+_\d+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReportIdOnlyPattern();

    [GeneratedRegex(
        @"<pre[^>]*>(?<xml>.*?)</pre>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex XmlPrePattern();
}
