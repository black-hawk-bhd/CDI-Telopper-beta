using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.Transport;

namespace EEWTelop.Infrastructure.Dmdata.History;

/// <summary>Manual, bounded history reads from official long feeds. No live ingestion or EEW.</summary>
public sealed class JmaXmlHistoryMessageSource : IHistoryMessageSource
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, string Xml)> _feeds = new();
    private readonly Dictionary<string, string> _telegrams = new();
    private int _cachedCharacters;

    public JmaXmlHistoryMessageSource(IClock clock)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(30) }, clock, ownsHttp: true) { }

    public JmaXmlHistoryMessageSource(HttpClient http, IClock clock, bool ownsHttp = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(clock);
        _http = http;
        _clock = clock;
        _ownsHttp = ownsHttp;
    }

    public async Task<IReadOnlyList<RawProviderMessage>> FetchAsync(
        HistoryFetchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Api != HistoryApi.JmaXml) throw new ArgumentException("JmaXml history required.", nameof(request));
        if (!Enum.IsDefined(request.JmaContent)) throw new ArgumentOutOfRangeException(nameof(request));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(request.JmaReportUrl))
            {
                if (!Uri.TryCreate(request.JmaReportUrl.Trim(), UriKind.Absolute, out Uri? direct) ||
                    !JmaPullEventSource.IsTelegramUri(direct))
                    throw new ArgumentException("気象庁の公開XML電文URLを指定してください。", nameof(request));
                return [ToMessage(await ReadTelegramAsync(direct, cancellationToken).ConfigureAwait(false))];
            }

            string[] feeds = request.JmaContent switch
            {
                JmaHistoryContent.Weather => ["extra_l"],
                JmaHistoryContent.AllSupported => ["eqvol_l", "extra_l"],
                _ => ["eqvol_l"],
            };
            var entries = new List<(Uri Uri, DateTimeOffset Updated)>();
            foreach (string feed in feeds)
            {
                string xml;
                if (_feeds.TryGetValue(feed, out var cached) && _clock.UtcNow - cached.At < TimeSpan.FromMinutes(1))
                    xml = cached.Xml;
                else
                {
                    xml = await DownloadAsync(new Uri($"https://www.data.jma.go.jp/developer/xml/feed/{feed}.xml"), cancellationToken)
                        .ConfigureAwait(false);
                    // Validate before caching, so malformed responses can be retried.
                    ReadDocument(xml);
                    _feeds[feed] = (_clock.UtcNow, xml);
                }
                XNamespace atom = "http://www.w3.org/2005/Atom";
                foreach (XElement entry in ReadDocument(xml).Descendants(atom + "entry"))
                {
                    if (!DateTimeOffset.TryParse((string?)entry.Element(atom + "updated"),
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset updated)) continue;
                    foreach (XElement link in entry.Elements(atom + "link"))
                    {
                        if (!Uri.TryCreate((string?)link.Attribute("href"), UriKind.Absolute, out Uri? uri) ||
                            !JmaPullEventSource.IsTelegramUri(uri)) continue;
                        string code = Regex.Match(uri.AbsolutePath, @"_(V[A-Z]{3}\d{2})_", RegexOptions.CultureInvariant).Groups[1].Value;
                        if (Accepts(code, request.JmaContent)) entries.Add((uri, updated));
                    }
                }
            }
            var messages = new List<RawProviderMessage>();
            foreach (var entry in entries.DistinctBy(static item => item.Uri)
                .OrderByDescending(static item => item.Updated).Take(Math.Clamp(request.Limit, 1, 100)))
            {
                messages.Add(ToMessage(await ReadTelegramAsync(entry.Uri, cancellationToken).ConfigureAwait(false)));
            }
            return messages;
        }
        finally { _gate.Release(); }
    }

    private RawProviderMessage ToMessage(string xml) => new("jma-xml", xml, SourceMode.HistoryRehearsal, _clock.UtcNow)
        { ContentFormat = RawProviderContentFormat.JmaXml };

    private static bool Accepts(string code, JmaHistoryContent content)
    {
        var routing = ProviderRoutingSettings.FromLegacy(ReceptionProvider.Disabled) with
        {
            Quake = content is JmaHistoryContent.AllSupported or JmaHistoryContent.Quake ? ReceptionProvider.JmaXml : ReceptionProvider.Disabled,
            NankaiTrough = content is JmaHistoryContent.AllSupported or JmaHistoryContent.Quake ? ReceptionProvider.JmaXml : ReceptionProvider.Disabled,
            Tsunami = content is JmaHistoryContent.AllSupported or JmaHistoryContent.Tsunami ? ReceptionProvider.JmaXml : ReceptionProvider.Disabled,
            Weather = content is JmaHistoryContent.AllSupported or JmaHistoryContent.Weather ? ReceptionProvider.JmaXml : ReceptionProvider.Disabled,
            Volcano = content is JmaHistoryContent.AllSupported or JmaHistoryContent.Volcano ? ReceptionProvider.JmaXml : ReceptionProvider.Disabled,
        };
        return JmaPullEventSource.Accepts(code, routing);
    }

    private async Task<string> ReadTelegramAsync(Uri uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_telegrams.TryGetValue(uri.AbsoluteUri, out string? cached)) return cached;
        // Pace even distinct manual requests; cached XML never needs downloading again in this session.
        await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
        string xml = await DownloadAsync(uri, token).ConfigureAwait(false);
        if (ReadDocument(xml).Root?.Name.LocalName != "Report") throw new InvalidDataException("JMA Report XML required.");
        while (_telegrams.Count > 0 && (_telegrams.Count >= 100 || _cachedCharacters + xml.Length > MaximumBytes))
        {
            string oldest = _telegrams.Keys.First();
            _cachedCharacters -= _telegrams[oldest].Length;
            _telegrams.Remove(oldest);
        }
        _telegrams[uri.AbsoluteUri] = xml;
        _cachedCharacters += xml.Length;
        return xml;
    }

    private async Task<string> DownloadAsync(Uri uri, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new IOException("XML size limit exceeded.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new IOException("XML size limit exceeded.");
            buffer.Write(bytes, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }

    private static XDocument ReadDocument(string xml)
    {
        using var text = new StringReader(xml);
        using XmlReader reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes,
        });
        return XDocument.Load(reader);
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        if (_ownsHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
