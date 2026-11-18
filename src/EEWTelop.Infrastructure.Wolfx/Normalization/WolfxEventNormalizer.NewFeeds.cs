using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Wolfx.Normalization;

public sealed partial class WolfxEventNormalizer
{
    internal sealed class SpecialEarthquakeReports
    {
        private readonly Dictionary<string, (DateTimeOffset IssuedAt, string Payload)> _latest = new(StringComparer.Ordinal);

        public IEnumerable<RawProviderMessage> Read(RawProviderMessage raw, bool initialSnapshot, DateTimeOffset connectedAt)
        {
            using JsonDocument document = JsonDocument.Parse(raw.Payload);
            JsonElement root = document.RootElement;
            foreach (JsonElement report in ReadArray(root, "reports").Take(4).OrderBy(item => ReadIsoTime(item, "time")))
            {
                string product = ReadString(report, "product");
                if (product is not ("VYSE50" or "VYSE51" or "VYSE52" or "VYSE60")) continue;
                DateTimeOffset? issuedAt = ReadIsoTime(report, "time");
                string appendix = ReadAppendix(root, report);
                string payload = JsonSerializer.Serialize(new { type = "jma_special_eq", report, appendix });
                if (issuedAt is { } time)
                {
                    // Each push repeats the retained reports for all products.
                    if (_latest.TryGetValue(product, out var previous) &&
                        (time < previous.IssuedAt || payload == previous.Payload)) continue;
                    _latest[product] = (time, payload);
                }

                yield return raw with
                {
                    Json = payload,
                    TransportPayload = raw.Payload,
                    TransportContentFormat = RawProviderContentFormat.Json,
                    IsStateSnapshot = initialSnapshot || issuedAt < connectedAt,
                };
            }
        }
    }

    private NormalizeResult NormalizeSpecialEarthquake(JsonElement root, RawProviderMessage raw)
    {
        JsonElement report = ReadObject(root, "report");
        if (report.ValueKind == JsonValueKind.Undefined)
        {
            JsonElement[] reports = ReadArray(root, "reports").Take(2).ToArray();
            if (reports.Length != 1) return NormalizeResult.Ignored();
            report = reports[0];
        }

        string product = ReadString(report, "product");
        (QuakeIssueType kind, string title) = product switch
        {
            "VYSE50" => (QuakeIssueType.NankaiTroughTemporaryInformation, "南海トラフ地震臨時情報"),
            "VYSE51" => (QuakeIssueType.NankaiTroughCommentary, "南海トラフ地震臨時情報に関する解説情報"),
            "VYSE52" => (QuakeIssueType.NankaiTroughCommentary, "南海トラフ地震関連解説情報"),
            "VYSE60" => (QuakeIssueType.SubsequentEarthquakeAdvisory, "北海道・三陸沖後発地震注意情報"),
            _ => (QuakeIssueType.Unknown, string.Empty),
        };
        if (kind == QuakeIssueType.Unknown) return NormalizeResult.Ignored();

        string id = ReadString(report, "id").Trim();
        string status = ReadString(report, "status");
        if (id.Length == 0 || status is not ("issued" or "corrected" or "cancelled") ||
            ReadIsoTime(report, "time") is not { } issuedAt)
        {
            return Invalid("report", "Wolfx special earthquake report has invalid identity, status or time.");
        }

        string label = ReadString(report, "label").Trim();
        string heading = label.Length > 0 ? $"{title}（{label}）" : title;
        string narrative = JoinText(ReadString(report, "text"), ReadString(report, "notice"), ReadString(report, "next"));
        string appendix = ReadString(root, "appendix");
        if (appendix.Length == 0) appendix = ReadAppendix(root, report);
        var quake = new QuakeEvent(EventId.Create($"{product}-{id}"), raw.Provider, issuedAt, raw.ReceivedAt,
            string.Empty, raw.SourceMode, CreateIssue(product, issuedAt, status), kind,
            new EarthquakeInfo(issuedAt, null, null, JmaScale.Unknown, DomesticTsunami.Unknown, ForeignTsunami.Unknown)
            {
                OriginTimeIsKnown = false,
            },
            [], JoinText(narrative, appendix), isCancelled: status == "cancelled",
            headline: product == "VYSE60" ? narrative : JoinText(heading, narrative))
        {
            IsRoutineNankaiCommentary = product == "VYSE52" &&
                (label == "定例解説" || (label.Length == 0 && ReadValue(report, "code").Trim() == "200")),
        };
        return SuccessWithSignature(quake);
    }

    private NormalizeResult NormalizeTsunamiSnapshot(JsonElement root, RawProviderMessage raw)
    {
        string status = ReadString(root, "status");
        if (status is not ("uninitialized" or "unknown" or "active" or "forecast_only" or
            "cleared" or "expired" or "none_reported"))
        {
            return Invalid("status", "Unknown Wolfx tsunami snapshot status.");
        }

        JsonElement report = ReadObject(root, "report");
        DateTimeOffset? officialTime = ReadIsoTime(report, "time");
        string reportId = ReadString(report, "id").Trim();
        string reportStatus = ReadString(report, "status");
        DateTimeOffset? trustUntil = ReadIsoTime(root, "validUntil");
        DateTimeOffset? officialUntil = ReadIsoTime(report, "validUntil");
        DateTimeOffset? expiresAt = officialUntil < trustUntil ? officialUntil : trustUntil;
        bool expired = status == "expired" || expiresAt <= raw.ReceivedAt;
        bool trusted = TryGetProperty(root, "trusted", out JsonElement trust) && trust.ValueKind == JsonValueKind.True &&
            trustUntil > raw.ReceivedAt && !expired;
        bool hasReport = reportId.Length > 0 && officialTime.HasValue &&
            ReadString(report, "product") == "VTSE41" && reportStatus is "issued" or "corrected" or "cancelled";
        if (trusted && (status is "active" or "forecast_only" or "cleared") && !hasReport)
        {
            return Invalid("report", "Trusted Wolfx tsunami state requires a VTSE41 report.");
        }

        DateTimeOffset issuedAt = officialTime ?? DateTimeOffset.UnixEpoch;
        string eventId = ReadArray(report, "eventIds")
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .FirstOrDefault(static id => !string.IsNullOrWhiteSpace(id)) ?? "wolfx-tsunami";
        var id = EventId.Create(eventId);
        bool telegramCancellation = trusted && hasReport && reportStatus == "cancelled";
        bool cleared = trusted && hasReport && status == "cleared" && !telegramCancellation;
        var areas = new List<TsunamiArea>();
        var observations = new List<TsunamiEvent>();
        if (trusted && hasReport && !telegramCancellation && (status is "active" or "forecast_only"))
        {
            ReadForecastAreas(root, status, areas);
            foreach (JsonElement observation in ReadArray(root, "observations"))
            {
                if (NormalizeTsunamiObservation(observation, raw, id, expiresAt) is not { } item) continue;
                observations.Add(item);
                if (!item.IsCancelled) areas.AddRange(item.Areas);
            }
        }

        JsonElement comments = ReadObject(root, "comments");
        var tsunami = new TsunamiEvent(id, raw.Provider, issuedAt, raw.ReceivedAt, string.Empty,
            raw.SourceMode, CreateIssue("VTSE41", issuedAt, telegramCancellation ? "cancelled" :
                reportStatus == "corrected" ? "corrected" : "issued"),
            areas, cleared || telegramCancellation, expiresAt, headline: ReadString(report, "title"),
            comment: JoinText(ReadString(root, "text"), ReadString(comments, "warning"), ReadString(comments, "forecast"),
                ReadString(comments, "note"), ReadString(comments, "text")))
        {
            IsExpired = expired,
            SnapshotObservations = observations,
            SourceReportId = reportId,
        };
        return SuccessWithSignature(tsunami);
    }

    private static void ReadForecastAreas(JsonElement root, string status, List<TsunamiArea> areas)
    {
        foreach (JsonElement area in ReadArray(root, "areas"))
        {
            TsunamiGrade grade = ReadString(area, "level") switch
            {
                "major_warning" => TsunamiGrade.MajorWarning,
                "warning" => TsunamiGrade.Warning,
                "advisory" => TsunamiGrade.Watch,
                "forecast" => TsunamiGrade.Forecast,
                _ => TsunamiGrade.Unknown,
            };
            if (grade == TsunamiGrade.Unknown || status == "forecast_only" && grade != TsunamiGrade.Forecast) continue;
            if (ReadTsunamiArea(area, grade, TsunamiInformationRole.ForecastArea) is not { } forecast) continue;
            areas.Add(forecast);
            AddStations(area, forecast, TsunamiInformationRole.StationForecast, areas);
        }
    }

    private TsunamiEvent? NormalizeTsunamiObservation(JsonElement report, RawProviderMessage raw,
        EventId eventId, DateTimeOffset? expiresAt)
    {
        string product = ReadString(report, "product");
        string status = ReadString(report, "status");
        string reportId = ReadString(report, "id").Trim();
        if (product is not ("VTSE51" or "VTSE52") || status is not ("issued" or "corrected" or "cancelled") ||
            reportId.Length == 0 || ReadIsoTime(report, "time") is not { } issuedAt) return null;

        var role = product == "VTSE51" ? TsunamiInformationRole.CoastalObservation : TsunamiInformationRole.OffshoreObservation;
        var areas = new List<TsunamiArea>();
        if (status != "cancelled")
        {
            AddObservationAreas(report, "observed", role, areas);
            AddObservationAreas(report, "estimated", role, areas);
        }
        var observation = new TsunamiEvent(eventId, raw.Provider, issuedAt, raw.ReceivedAt, string.Empty,
            raw.SourceMode, CreateIssue(product, issuedAt, status), areas, status == "cancelled", expiresAt,
            ReadIsoTime(report, "observationAsOf"), ReadString(report, "title"), ReadString(report, "text"))
        {
            SourceReportId = reportId,
        };
        return observation with { Signature = _signatureBuilder.Build(observation) };
    }

    private static void AddObservationAreas(JsonElement report, string section,
        TsunamiInformationRole role, List<TsunamiArea> areas)
    {
        bool estimated = section == "estimated";
        foreach (JsonElement area in ReadArray(report, section))
        {
            if (ReadTsunamiArea(area, TsunamiGrade.Unknown, role, estimated) is not { } item) continue;
            if (item.FirstHeight is not null || item.MaximumHeight is not null) areas.Add(item);
            AddStations(area, item, role, areas, estimated);
        }
    }

    private static void AddStations(JsonElement parent, TsunamiArea area,
        TsunamiInformationRole role, List<TsunamiArea> areas, bool estimated = false)
    {
        foreach (JsonElement station in ReadArray(parent, "stations"))
        {
            if (ReadTsunamiArea(station, area.Grade, role, estimated) is { } item)
                areas.Add(item with { ParentAreaName = area.Name, ParentAreaCode = area.Code });
        }
    }

    private static TsunamiArea? ReadTsunamiArea(JsonElement item, TsunamiGrade grade,
        TsunamiInformationRole role, bool estimated = false)
    {
        string name = ReadString(item, "name").Trim();
        if (name.Length == 0) return null;
        JsonElement first = ReadObject(item, "firstWave");
        JsonElement max = ReadObject(item, "maxWave");
        string condition = JoinText(ReadValue(first, "condition"), ReadString(first, "text"));
        string heightText = ReadString(max, "text");
        string heightCondition = ReadValue(max, "condition");
        bool qualitativeHeight = IsQualitativeHeight(heightText) || IsQualitativeHeight(heightCondition);
        if (estimated)
        {
            if (max.ValueKind == JsonValueKind.Object) heightCondition = JoinText("沿岸推定", heightCondition);
            else condition = JoinText("沿岸推定", condition);
        }
        return new TsunamiArea(grade, condition.Contains("ただちに", StringComparison.Ordinal) ||
            condition.Contains("直ちに", StringComparison.Ordinal), name,
            first.ValueKind == JsonValueKind.Object ? new(ReadIsoTime(first, "time"), condition) : null,
            max.ValueKind == JsonValueKind.Object ? new(heightText,
                qualitativeHeight ? null : ReadNullableDouble(max, "height"), ReadIsoTime(max, "time"), heightCondition) : null)
        {
            Role = role,
            Code = ReadValue(item, "code").Trim(),
            HighTideAt = ReadIsoTime(item, "highTide"),
        };
    }

    private static bool IsQualitativeHeight(string value) =>
        value.Contains('超') || value.Contains("以上", StringComparison.Ordinal) ||
        value.Contains("巨大", StringComparison.Ordinal) || value.Contains("高い", StringComparison.Ordinal);

    private static IssueInfo CreateIssue(string product, DateTimeOffset time, string status) =>
        new("Wolfx / 気象庁", time, product, status == "corrected" ? CorrectionType.Generic : CorrectionType.None,
            InformationType: status switch { "cancelled" => "取消", "corrected" => "訂正", _ => "発表" });

    private static JsonElement ReadObject(JsonElement root, string name) =>
        TryGetProperty(root, name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static JsonElement.ArrayEnumerator ReadArray(JsonElement root, string name) =>
        TryGetProperty(root, name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray() : default;

    private static DateTimeOffset? ReadIsoTime(JsonElement item, string name)
    {
        string text = ReadString(item, name);
        return (text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10) &&
            TryGetProperty(item, name, out JsonElement value) && value.TryGetDateTimeOffset(out var time) ? time : null;
    }

    private static string ReadAppendix(JsonElement root, JsonElement report)
    {
        if (!TryGetProperty(report, "appendix", out JsonElement index) || index.ValueKind != JsonValueKind.Number ||
            !index.TryGetInt32(out int position) || position < 0) return string.Empty;
        JsonElement paragraph = ReadArray(root, "appendices").ElementAtOrDefault(position);
        return paragraph.ValueKind == JsonValueKind.String ? paragraph.GetString() ?? string.Empty : string.Empty;
    }

    private static string JoinText(params string[] values) =>
        string.Join('\n', values.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
}
