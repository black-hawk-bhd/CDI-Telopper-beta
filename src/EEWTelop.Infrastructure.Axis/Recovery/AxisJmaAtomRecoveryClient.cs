using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Axis.Configuration;
using EEWTelop.Infrastructure.Axis.Transport;

namespace EEWTelop.Infrastructure.Axis.Recovery;

internal interface IAxisRecoveryClient
{
    IAsyncEnumerable<RawProviderMessage> FetchRecentAsync(
        DateTimeOffset since,
        AxisProviderOptions options,
        CancellationToken cancellationToken = default);
}

internal sealed class AxisJmaAtomRecoveryClient : IAxisRecoveryClient
{
    private const int MaximumFeedBytes = 4 * 1024 * 1024;
    private const int MaximumTelegramBytes = 24 * 1024 * 1024;
    private const int MaximumEntriesPerFeed = 40;
    private static readonly Uri SeismologyFeed =
        new("https://www.data.jma.go.jp/developer/xml/feed/eqvol.xml");
    private static readonly Uri MeteorologyFeed =
        new("https://www.data.jma.go.jp/developer/xml/feed/extra.xml");
    private static readonly Uri VolcanologyFeed =
        new("https://www.data.jma.go.jp/developer/xml/feed/eqvol.xml");
    private readonly HttpClient _httpClient;
    private readonly IClock _clock;
    private readonly Dictionary<Uri, EntityTagHeaderValue> _entityTags = [];

    public AxisJmaAtomRecoveryClient(
        HttpClient httpClient,
        IClock clock,
        IAppLogWriter logWriter)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(logWriter);
    }

    public async IAsyncEnumerable<RawProviderMessage> FetchRecentAsync(
        DateTimeOffset since,
        AxisProviderOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Uri[] feeds = GetFeeds(options).ToArray();
        foreach (Uri feed in feeds)
        {
            IReadOnlyList<AtomEntry> entries = await ReadFeedAsync(feed, since, cancellationToken)
                .ConfigureAwait(false);
            bool completed = false;
            try
            {
                foreach (AtomEntry entry in entries)
                {
                    string xml = await ReadTelegramAsync(entry.Link, cancellationToken)
                        .ConfigureAwait(false);
                    string telegramType = AxisWeatherTelegramPolicy.ReadTelegramType(xml);
                    string channel = AxisWeatherTelegramPolicy.ReadTelegramType(xml)
                            is "VFVO50" or "VFVO56"
                        ? AxisProviderOptions.VolcanologyChannel
                        : feed == SeismologyFeed
                            ? AxisProviderOptions.SeismologyChannel
                            : AxisProviderOptions.MeteorologyChannel;
                    if (!AxisWeatherTelegramPolicy.IsAssignedToAxis(channel, telegramType))
                    {
                        continue;
                    }

                    string status = ReadElement(xml, "Status");
                    bool isTest = status.Contains("訓練", StringComparison.Ordinal) ||
                        status.Contains("試験", StringComparison.Ordinal);
                    yield return new RawProviderMessage(
                        AxisProviderOptions.ProviderName,
                        xml,
                        isTest ? SourceMode.Sandbox : SourceMode.Production,
                        _clock.UtcNow)
                    {
                        ContentFormat = RawProviderContentFormat.JmaXml,
                    };
                }

                completed = true;
            }
            finally
            {
                // If enumeration is interrupted before every linked telegram is
                // downloaded, force the next reconnect to re-read this feed.
                // This prevents a failed download from being hidden by a 304.
                if (entries.Count > 0 && !completed)
                {
                    _entityTags.Remove(feed);
                }
            }
        }
    }

    private static IEnumerable<Uri> GetFeeds(AxisProviderOptions options)
    {
        if (options.AcceptsChannel(AxisProviderOptions.SeismologyChannel))
        {
            yield return SeismologyFeed;
        }

        if (options.AcceptsChannel(AxisProviderOptions.MeteorologyChannel))
        {
            yield return MeteorologyFeed;
        }


        // Volcano and earthquake telegrams share JMA's eqvol feed. Avoid
        // downloading it twice when both AXIS channels are enabled.
        if (options.AcceptsChannel(AxisProviderOptions.VolcanologyChannel) &&
            !options.AcceptsChannel(AxisProviderOptions.SeismologyChannel))
        {
            yield return VolcanologyFeed;
        }
    }

    private async Task<IReadOnlyList<AtomEntry>> ReadFeedAsync(
        Uri feed,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, feed);
        request.Headers.UserAgent.ParseAdd("CDI-Telopper/2 AXIS-gap-recovery");
        if (_entityTags.TryGetValue(feed, out EntityTagHeaderValue? tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > MaximumFeedBytes)
        {
            throw new InvalidDataException("JMA Atom recovery feed exceeded the safety limit.");
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (bytes.Length > MaximumFeedBytes)
        {
            throw new InvalidDataException("JMA Atom recovery feed exceeded the safety limit.");
        }

        if (response.Headers.ETag is EntityTagHeaderValue entityTag)
        {
            _entityTags[feed] = entityTag;
        }

        XDocument document = XDocument.Parse(
            new UTF8Encoding(false, true).GetString(bytes),
            LoadOptions.None);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        return document.Descendants(atom + "entry")
            .Select(entry => TryReadEntry(entry, atom))
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .Where(entry => entry.Updated >= since)
            .OrderBy(static entry => entry.Updated)
            .TakeLast(MaximumEntriesPerFeed)
            .ToArray();
    }

    private async Task<string> ReadTelegramAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "www.data.jma.go.jp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("JMA Atom recovery link used an unexpected origin.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("CDI-Telopper/2 AXIS-gap-recovery");
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length &&
            length > MaximumTelegramBytes)
        {
            throw new InvalidDataException("JMA recovery telegram exceeded the safety limit.");
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (bytes.Length > MaximumTelegramBytes)
        {
            throw new InvalidDataException("JMA recovery telegram exceeded the safety limit.");
        }

        string xml = new UTF8Encoding(false, true).GetString(bytes);
        XDocument parsed = XDocument.Parse(xml, LoadOptions.None);
        if (parsed.Root?.Name.LocalName != "Report")
        {
            throw new InvalidDataException("JMA recovery document was not a Report telegram.");
        }

        return xml;
    }

    private static AtomEntry? TryReadEntry(XElement entry, XNamespace atom)
    {
        if (!DateTimeOffset.TryParse(entry.Element(atom + "updated")?.Value, out DateTimeOffset updated))
        {
            return null;
        }

        string? href = entry.Elements(atom + "link")
            .Select(static link => (string?)link.Attribute("href"))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        return Uri.TryCreate(href, UriKind.Absolute, out Uri? link)
            ? new AtomEntry(updated, link)
            : null;
    }

    private static string ReadElement(string xml, string localName)
    {
        XDocument document = XDocument.Parse(xml, LoadOptions.None);
        return document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName)?.Value ??
            string.Empty;
    }

    private sealed record AtomEntry(DateTimeOffset Updated, Uri Link);
}
