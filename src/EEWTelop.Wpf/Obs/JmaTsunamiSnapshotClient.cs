using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Obs;

/// <summary>One current JMA snapshot, never passed through the subtitle/audio ingestion pipeline.</summary>
public static class JmaTsunamiSnapshotClient
{
    public const string Endpoint = "https://www.data.jma.go.jp/multi/data/VTSE41/jp.json";
    private const int MaximumBytes = 1024 * 1024;

    public static async Task<TsunamiEvent> FetchAsync(CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        return await FetchAsync(http, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<TsunamiEvent> FetchAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.Age > TimeSpan.FromSeconds(60) ||
            response.Content.Headers.ContentLength > MaximumBytes)
            throw new InvalidDataException("Invalid or stale snapshot response.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var body = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (body.Length + read > MaximumBytes) throw new InvalidDataException("Snapshot too large.");
            body.Write(buffer, 0, read);
        }
        return Parse(body.ToArray(), DateTimeOffset.UtcNow);
    }

    internal static TsunamiEvent Parse(byte[] json, DateTimeOffset receivedAt)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("reportDateTime", out var report) || report.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParseExact(report.GetString() + " +09:00", "yyyy/MM/dd HH:mm zzz",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset issuedAt) ||
            issuedAt > receivedAt.AddMinutes(5) ||
            !root.TryGetProperty("item", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unrecognized snapshot format.");

        List<TsunamiArea> areas = [];
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("area", out var area) || area.ValueKind != JsonValueKind.Object || !area.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) ||
                !item.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.Object || !kind.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Unrecognized forecast item.");
            TsunamiGrade grade = code.GetString() switch
            {
                "52" or "53" => TsunamiGrade.MajorWarning,
                "51" => TsunamiGrade.Warning,
                "62" => TsunamiGrade.Watch,
                "71" or "72" or "73" => TsunamiGrade.Forecast,
                "50" or "60" => TsunamiGrade.Unknown,
                _ => throw new InvalidDataException("Unknown tsunami kind code."),
            };
            // Release entries are not active forecast areas. Missing/malformed data is never a release.
            if (grade != TsunamiGrade.Unknown)
                areas.Add(new(grade, false, name.GetString()!, null, null));
        }
        TsunamiArea[] distinct = areas.GroupBy(a => a.Name, StringComparer.Ordinal)
            .Select(g => g.OrderBy(a => (int)a.Grade).First()).ToArray();
        return new(EventId.Create("jma-current-tsunami-" + issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            "jma-current-json", issuedAt, receivedAt, "startup-tsunami", SourceMode.Production,
            new IssueInfo("気象庁", issuedAt, "VTSE41", CorrectionType.None, null, "発表"),
            distinct, distinct.Length == 0, null);
    }
}
