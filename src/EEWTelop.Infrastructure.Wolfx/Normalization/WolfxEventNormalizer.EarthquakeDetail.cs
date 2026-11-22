using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EEWTelop.Application.Events;
using EEWTelop.Application.Formatting;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Wolfx.Normalization;

public sealed partial class WolfxEventNormalizer
{
    internal sealed class EarthquakeDetailReports(DateTimeOffset startedAt) : IDisposable
    {
        private readonly SemaphoreSlim _publication = new(1);
        private readonly Dictionary<(string Event, string Product), (DateTimeOffset Time, string Report)> _seen = [];
        private readonly Dictionary<string, DateTimeOffset> _published = new(StringComparer.Ordinal);
        private bool _initialized;

        public async ValueTask PublishSummaryAsync(RawProviderMessage raw, ProviderMessageBuffer output, CancellationToken token)
        {
            string id = string.Empty;
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw.Payload);
                id = ReadValue(ReadObject(document.RootElement, "No1"), "EventID").Trim();
            }
            catch (JsonException)
            {
                // Let the existing normalizer report the malformed summary.
            }
            await _publication.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!_published.ContainsKey(id)) await output.WriteAsync(raw, token).ConfigureAwait(false);
            }
            finally { _publication.Release(); }
        }

        public async ValueTask PublishDetailsAsync(RawProviderMessage raw, ProviderMessageBuffer output, CancellationToken token)
        {
            using JsonDocument document = JsonDocument.Parse(raw.Payload);
            JsonElement root = document.RootElement;
            if (!TryGetProperty(root, "events", out JsonElement events) || events.ValueKind != JsonValueKind.Array)
                throw new JsonException("Wolfx earthquake details require an events array.");

            var currentEvents = new HashSet<string>(StringComparer.Ordinal);
            var updates = new List<DetailReport>();
            DetailReport? latestCached = null;
            foreach (JsonElement item in events.EnumerateArray().Take(100))
            {
                string id = ReadString(item, "eventId").Trim();
                if (id.Length == 0) continue;
                currentEvents.Add(id);
                bool allCancelled = ReadString(item, "status") == "cancelled";
                foreach (JsonElement report in ReadArray(item, "reports"))
                {
                    string product = ReadString(report, "product");
                    string status = ReadString(report, "status");
                    string reportId = ReadString(report, "id");
                    if (GetEarthquakeDetailKind(product) == QuakeIssueType.Unknown || reportId.Length == 0 ||
                        status is not ("issued" or "corrected" or "cancelled") || ReadIsoTime(report, "time") is not { } issued)
                        continue;
                    var key = (id, product);
                    string version = reportId + ":" + status;
                    if (_seen.TryGetValue(key, out var previous) && (issued < previous.Time || version == previous.Report))
                        continue;
                    _seen[key] = (issued, version);
                    if (status == "cancelled" && !allCancelled) continue;
                    var detail = new DetailReport(id, issued, item, report);
                    if (issued >= startedAt)
                        updates.Add(detail);
                    else if (!_initialized && (latestCached is null || issued > latestCached.Time))
                        latestCached = detail;
                }
            }

            await _publication.WaitAsync(token).ConfigureAwait(false);
            try
            {
                foreach (string id in _published.Keys.Where(id => !currentEvents.Contains(id)).ToArray())
                    _published.Remove(id);
                if (latestCached is not null)
                    await PublishAsync(latestCached, raw, output, isSnapshot: true, token).ConfigureAwait(false);
                foreach (DetailReport detail in updates.OrderBy(static item => item.Time))
                    await PublishAsync(detail, raw, output, isSnapshot: false, token).ConfigureAwait(false);
                _initialized = true;
                foreach (var key in _seen.Keys.Where(key => !currentEvents.Contains(key.Event)).ToArray())
                    _seen.Remove(key);
            }
            finally { _publication.Release(); }
        }

        private async ValueTask PublishAsync(DetailReport detail, RawProviderMessage raw,
            ProviderMessageBuffer output, bool isSnapshot, CancellationToken token)
        {
            if (_published.TryGetValue(detail.EventId, out DateTimeOffset previous) && detail.Time < previous)
                return;
            object? hypocenters = TryGetProperty(detail.Event, "hypocenters", out var items) ? items : null;
            string payload = JsonSerializer.Serialize(new
            {
                type = "jma_earthquake_detail",
                eventId = detail.EventId,
                hypocenters,
                report = detail.Report,
            });
            await output.WriteAsync(raw with
            {
                Json = payload,
                TransportPayload = detail.Event.GetRawText(),
                TransportContentFormat = RawProviderContentFormat.Json,
                IsStateSnapshot = isSnapshot,
            }, token).ConfigureAwait(false);
            _published[detail.EventId] = detail.Time;
        }

        public void Dispose() => _publication.Dispose();

        private sealed record DetailReport(string EventId, DateTimeOffset Time, JsonElement Event, JsonElement Report);
    }

    private static QuakeIssueType GetEarthquakeDetailKind(string product) => product switch
    {
        "VXSE51" => QuakeIssueType.ScalePrompt,
        "VXSE52" => QuakeIssueType.Destination,
        "VXSE53" => QuakeIssueType.DetailScale,
        "VXSE61" => QuakeIssueType.Other,
        "VXSE62" => QuakeIssueType.LongPeriodObservation,
        _ => QuakeIssueType.Unknown,
    };

    private NormalizeResult NormalizeEarthquakeDetail(JsonElement root, RawProviderMessage raw)
    {
        string eventId = ReadString(root, "eventId").Trim();
        JsonElement report = ReadObject(root, "report");
        string product = ReadString(report, "product");
        QuakeIssueType kind = GetEarthquakeDetailKind(product);
        string status = ReadString(report, "status");
        if (kind == QuakeIssueType.Unknown || status is not ("issued" or "corrected" or "cancelled"))
            return NormalizeResult.Ignored();
        if (eventId.Length == 0 || ReadString(report, "id").Length == 0 ||
            ReadIsoTime(report, "time") is not { } issuedAt)
            return Invalid("report", "Wolfx earthquake detail requires the official event ID and report time.");

        JsonElement observation = ReadObject(report, "observation");
        JsonElement[] prefs = ReadArray(observation, "prefs").ToArray();
        JsonElement[] areas = ReadArray(observation, "areas").ToArray();
        QuakePoint[] points = ReadEarthquakeDetailPoints(observation, prefs, areas);
        JmaScale scale = ReadDetailScale(observation, "maxIntensity");
        if (scale == JmaScale.Unknown && points.Length > 0)
            scale = points.Select(static point => point.Scale).Where(static value => value != JmaScale.FiveLowerOrMore)
                .DefaultIfEmpty(JmaScale.Unknown).Max();

        JsonElement hypocenter = ReadIndex(ReadArray(root, "hypocenters").ToArray(), report, "hypocenter");
        HypocenterInfo? hypocenterInfo = hypocenter.ValueKind == JsonValueKind.Object
            ? new(ReadString(hypocenter, "name"), string.Empty, ReadNullableDouble(hypocenter, "latitude"),
                ReadNullableDouble(hypocenter, "longitude"), ReadNullableInt(hypocenter, "depth"),
                ReadNullableDouble(report, "magnitude"),
                ReadString(hypocenter, "description").Contains("ごく浅い", StringComparison.Ordinal) ? "ごく浅い" : string.Empty,
                ReadString(report, "magnitudeText"))
            : null;
        DateTimeOffset? origin = ReadIsoTime(report, "originTime");
        DateTimeOffset? arrival = ReadIsoTime(report, "arrivalTime");
        JsonElement comments = ReadObject(report, "comments");
        string forecast = ReadString(comments, "forecast");
        var earthquake = new EarthquakeInfo(origin ?? arrival ?? issuedAt, arrival, hypocenterInfo,
            scale, ParseDomesticTsunami(forecast), ForeignTsunami.Unknown)
        {
            OriginTimeIsKnown = origin.HasValue,
        };
        string serial = ReadValue(report, "serial").Trim();
        IssueInfo issue = CreateIssue(product, issuedAt, status) with { Serial = serial.Length > 0 ? serial : null };
        var quake = new QuakeEvent(EventId.Create(eventId), raw.Provider, issuedAt, raw.ReceivedAt,
            string.Empty, raw.SourceMode, issue, kind, earthquake, points,
            JoinText(forecast, ReadString(comments, "warning"), ReadString(comments, "note"),
                ReadString(comments, "text"), ReadString(report, "text")),
            product == "VXSE62" ? ReadDetailLongPeriod(observation, prefs, areas) : null,
            isCancelled: status == "cancelled", headline: ReadString(report, "title"))
        {
            JmaEarthquakeId = kind == QuakeIssueType.ScalePrompt ? EventId.FromJmaEarthquakeId(eventId) : null,
        };
        return SuccessWithSignature(quake);
    }

    private static QuakePoint[] ReadEarthquakeDetailPoints(JsonElement observation, JsonElement[] prefs, JsonElement[] areas)
    {
        var points = new List<QuakePoint>();
        var areasWithCities = new HashSet<int>();
        foreach (JsonElement city in ReadArray(observation, "cities"))
        {
            int? areaIndex = ReadItemIndex(city, "area", areas.Length);
            JsonElement area = areaIndex is int index ? areas[index] : default;
            JsonElement pref = ReadIndex(prefs, area, "pref");
            int before = points.Count;
            foreach (JsonElement station in ReadArray(city, "stations"))
                AddDetailPoint(points, station, pref, area, city, isStation: true);
            if (points.Count == before || ReadPointScale(city) == JmaScale.FiveLowerOrMore &&
                !points.Skip(before).Any(static point => point.Scale == JmaScale.FiveLowerOrMore))
                AddDetailPoint(points, city, pref, area, city);
            if (areaIndex is int cityArea && points.Count > before) areasWithCities.Add(cityArea);
        }
        for (int i = 0; i < areas.Length; i++)
        {
            JsonElement area = areas[i];
            JsonElement pref = ReadIndex(prefs, area, "pref");
            int before = points.Count;
            foreach (JsonElement station in ReadArray(area, "stations"))
                AddDetailPoint(points, station, pref, area, default, isStation: true);
            if (points.Count == before && !areasWithCities.Contains(i) ||
                ReadPointScale(area) == JmaScale.FiveLowerOrMore &&
                !points.Any(point => point.SeismicAreaCode == ReadValue(area, "code") && point.Scale == JmaScale.FiveLowerOrMore))
                AddDetailPoint(points, area, pref, area, default);
        }
        if (points.Count == 0)
        {
            foreach (JsonElement pref in prefs) AddDetailPoint(points, pref, pref, default, default);
        }
        return points.DistinctBy(static point => point.StationCode.Length > 0
            ? (point.StationCode, string.Empty, string.Empty)
            : (string.Empty, point.Prefecture, point.Address)).ToArray();
    }

    private static void AddDetailPoint(List<QuakePoint> points, JsonElement item, JsonElement pref,
        JsonElement area, JsonElement city, bool isStation = false)
    {
        string name = ReadString(item, "name").Trim();
        JmaScale scale = ReadPointScale(item);
        if (name.Length == 0 || scale == JmaScale.Unknown) return;
        string prefecture = ReadString(pref, "name").Trim();
        bool isArea = !isStation && city.ValueKind == JsonValueKind.Undefined;
        points.Add(new QuakePoint(prefecture, name, isArea, scale, PlaceNormalizer.BuildDisplayName(prefecture, name, isArea))
        {
            SeismicAreaCode = ReadValue(area, "code"),
            SeismicAreaName = ReadString(area, "name"),
            MunicipalityCode = ReadValue(city, "code"),
            MunicipalityName = ReadString(city, "name"),
            StationCode = isStation ? ReadValue(item, "code") : string.Empty,
            IsNonJmaIntensityStation = isStation && name.EndsWith('＊'),
        });
    }

    private static JmaScale ReadPointScale(JsonElement item) =>
        ReadDetailScale(item, "condition") == JmaScale.FiveLowerOrMore
            ? JmaScale.FiveLowerOrMore : ReadDetailScale(item, "int");

    private static JmaScale ReadDetailScale(JsonElement item, string name)
    {
        string value = ReadValue(item, name).Trim().Replace("５", "5", StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal).Replace("　", string.Empty, StringComparison.Ordinal);
        return value is "5-?" or "!5-" || value.Contains("5弱以上未入電", StringComparison.Ordinal)
            ? JmaScale.FiveLowerOrMore : ParseScale(value);
    }

    private static LongPeriodIntensityInfo? ReadDetailLongPeriod(JsonElement observation, JsonElement[] prefs, JsonElement[] areas)
    {
        LongPeriodIntensityArea[] observations = areas.Select(area => new LongPeriodIntensityArea(
            ReadString(ReadIndex(prefs, area, "pref"), "name"), ReadString(area, "name"),
            ReadNullableInt(area, "lg") ?? 0))
            .Where(static area => area.Class is >= 1 and <= 4 && area.Area.Length > 0)
            .ToArray();
        int maximum = ReadNullableInt(observation, "maxLongPeriod") ?? 0;
        if (maximum is < 1 or > 4) maximum = observations.Select(static area => area.Class).DefaultIfEmpty(0).Max();
        return maximum == 0 ? null : new LongPeriodIntensityInfo(maximum, observations);
    }

    private static JsonElement ReadIndex(JsonElement[] array, JsonElement item, string key) =>
        ReadItemIndex(item, key, array.Length) is int index ? array[index] : default;

    private static int? ReadItemIndex(JsonElement item, string key, int length) =>
        TryGetProperty(item, key, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int index) && index >= 0 && index < length ? index : null;
}
