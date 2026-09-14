using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Dmdata.Transport;

/// <summary>Official minute feeds, manually selected; never an EEW transport.</summary>
public sealed class JmaPullEventSource : IEventSource, IProviderConfigurableEventSource
{
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly Dictionary<string, (string Xml, string? ETag, DateTimeOffset? Modified)> _feeds = new();
    private ProviderRoutingSettings _routing;
    private CancellationTokenSource? _reader;
    private int _active;
    private bool _disposed;
    private bool _pollFailed;
    private DateTimeOffset? _since;
    public JmaPullEventSource(ProviderSettings settings, IClock clock)
        : this(settings, clock, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })) { }

    public JmaPullEventSource(ProviderSettings settings, IClock clock, HttpClient http)
    {
        _routing = settings.Routing;
        _clock = clock;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
        Connection = new(ProviderConnectionState.Stopped, clock.UtcNow);
    }

    public ProviderConnectionSnapshot Connection { get; private set; }
    public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged;
    public void ConfigureProvider(ProviderSettings settings)
    {
        if (Volatile.Read(ref _active) != 0) throw new InvalidOperationException("Stop reception before configuring JMA XML.");
        if (_routing != settings.Routing) _since = null;
        _routing = settings.Routing;
    }

    public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _active, 1) != 0) throw new InvalidOperationException("Only one JMA reader is allowed.");
        using var reader = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _reader = reader;
        _since ??= _clock.UtcNow;
        try
        {
            while (!reader.IsCancellationRequested)
            {
                List<RawProviderMessage> messages = [];
                try
                {
                    SetState(ProviderConnectionState.Connecting, "気象庁XMLを確認中");
                    messages = await PollAsync(_since.Value, reader.Token).ConfigureAwait(false);
                    if (!_pollFailed) SetState(ProviderConnectionState.Connected, "気象庁XML：60秒間隔で巡回（EEW対象外）");
                }
                catch (OperationCanceledException) when (reader.IsCancellationRequested) { yield break; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or OperationCanceledException)
                {
                    SetState(ProviderConnectionState.Reconnecting, "気象庁XML取得失敗：" + ex.Message);
                }
                foreach (var message in messages) yield return message;
                await Task.Delay(TimeSpan.FromSeconds(60), reader.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _reader = null;
            Interlocked.Exchange(ref _active, 0);
            SetState(ProviderConnectionState.Stopped, "停止");
        }
    }

    internal async Task<List<RawProviderMessage>> PollAsync(DateTimeOffset since, CancellationToken token)
    {
        _pollFailed = false;
        var entries = new List<(Uri Uri, DateTimeOffset Updated)>();
        var feeds = new List<string>();
        if (_routing.Weather == ReceptionProvider.JmaXml) feeds.Add("extra");
        if (new[] { _routing.Quake, _routing.Tsunami, _routing.Volcano, _routing.NankaiTrough }.Contains(ReceptionProvider.JmaXml)) feeds.Add("eqvol");
        foreach (string feed in feeds)
        {
            string xml;
            try { xml = await DownloadAsync(new Uri($"https://www.data.jma.go.jp/developer/xml/feed/{feed}.xml"), token); }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                _pollFailed = true;
                SetState(ProviderConnectionState.Reconnecting, "気象庁XML：フィード取得失敗。次回巡回で再試行します。");
                continue;
            }
            using var xmlReader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(xmlReader);
            XNamespace atom = "http://www.w3.org/2005/Atom";
            foreach (var entry in document.Descendants(atom + "entry"))
            {
                if (!DateTimeOffset.TryParse((string?)entry.Element(atom + "updated"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated) || updated < since) continue;
                foreach (var link in entry.Elements(atom + "link"))
                {
                    if (!Uri.TryCreate((string?)link.Attribute("href"), UriKind.Absolute, out var uri) || !IsTelegramUri(uri)) continue;
                    string code = Regex.Match(uri.AbsolutePath, @"_(V[A-Z]{3}\d{2})_", RegexOptions.CultureInvariant).Groups[1].Value;
                    if (Accepts(code, _routing) && !_seen.Contains(uri.AbsoluteUri)) entries.Add((uri, updated));
                }
            }
        }
        var messages = new List<RawProviderMessage>();
        foreach (var entry in entries.DistinctBy(x => x.Uri).OrderBy(x => x.Updated))
        {
            string payload;
            try { payload = await DownloadAsync(entry.Uri, token); }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                _pollFailed = true;
                SetState(ProviderConnectionState.Reconnecting, "気象庁XML：一部電文の取得失敗。次回巡回で再試行します。");
                continue;
            }
            messages.Add(new("jma-xml", payload, SourceMode.Production, _clock.UtcNow) { ContentFormat = RawProviderContentFormat.JmaXml });
            _seen.Add(entry.Uri.AbsoluteUri);
            _seenOrder.Enqueue(entry.Uri.AbsoluteUri);
            while (_seenOrder.Count > 20000) _seen.Remove(_seenOrder.Dequeue());
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
        return messages;
    }

    internal static bool IsTelegramUri(Uri uri) => uri.Scheme == "https" && uri.Host == "www.data.jma.go.jp" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        Regex.IsMatch(uri.AbsolutePath, @"^/developer/xml/data/[A-Za-z0-9_-]+\.xml$", RegexOptions.CultureInvariant);

    internal static bool Accepts(string code, ProviderRoutingSettings routing) => code switch
    {
        "VXSE51" or "VXSE52" or "VXSE53" or "VXSE62" => routing.Quake == ReceptionProvider.JmaXml,
        "VYSE50" or "VYSE60" => routing.NankaiTrough == ReceptionProvider.JmaXml,
        "VTSE41" or "VTSE51" or "VTSE52" => routing.Tsunami == ReceptionProvider.JmaXml,
        "VFVO50" or "VFVO56" => routing.Volcano == ReceptionProvider.JmaXml,
        // Exclude legacy aggregate warnings; retain the reorganized warning telegrams.
        "VPWW53" or "VPWW54" or "VPOA50" => false,
        "VPWW55" or "VPWW56" or "VPWW57" or "VPWW58" or "VPWW59" or "VPWW60" or "VPWW61" or "VPWS50" or "VPBS50" or "VPBS51" or "VPHW50" or "VPHW51" => routing.Weather == ReceptionProvider.JmaXml,
        _ => Regex.IsMatch(code, @"^VXKO[5-8][0-9]$", RegexOptions.CultureInvariant) && routing.Weather == ReceptionProvider.JmaXml,
    };

    private async Task<string> DownloadAsync(Uri uri, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        token = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        bool isFeed = uri.AbsolutePath.Contains("/feed/", StringComparison.Ordinal);
        if (isFeed && _feeds.TryGetValue(uri.AbsoluteUri, out var cached))
        {
            if (cached.ETag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            request.Headers.IfModifiedSince = cached.Modified;
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && _feeds.TryGetValue(uri.AbsoluteUri, out var previous)) return previous.Xml;
        response.EnsureSuccessStatusCode();
        const int limit = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new IOException("XML size limit exceeded.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, token)) != 0)
        {
            if (buffer.Length + count > limit) throw new IOException("XML size limit exceeded.");
            buffer.Write(bytes, 0, count);
        }
        string xml = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        if (isFeed) _feeds[uri.AbsoluteUri] = (xml, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        return xml;
    }

    private void SetState(ProviderConnectionState state, string detail)
    {
        Connection = new(state, _clock.UtcNow, Detail: detail);
        ConnectionChanged?.Invoke(this, Connection);
    }
    public ValueTask StopAsync(CancellationToken cancellationToken = default) { CancelReader(); return ValueTask.CompletedTask; }
    // Resume at the next scheduled poll, never bypass the minimum polling interval.
    public void RequestReconnect(ReconnectReason reason) { }
    public ValueTask DisposeAsync() { _disposed = true; CancelReader(); _http.Dispose(); return ValueTask.CompletedTask; }
    private void CancelReader()
    {
        try { _reader?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
