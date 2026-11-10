using System.Globalization;
using System.Text.Json;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Ydits;

public sealed class YditsEventNormalizer(IEventSignatureBuilder signatureBuilder) : IEventNormalizer
{
    public const string ProviderName = "ydits";
    public const string TestProviderName = "ydits-test";

    public NormalizeResult Normalize(RawProviderMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        bool isTest = string.Equals(raw.Provider, TestProviderName, StringComparison.OrdinalIgnoreCase);
        if (raw.SourceMode != (isTest ? SourceMode.Sandbox : SourceMode.Production)) return NormalizeResult.Ignored();
        try
        {
            if (raw.ContentFormat != RawProviderContentFormat.Json)
                throw new JsonException("YDITS VXSE43 requires JSON.");
            if (ReadResponse(raw.Payload, raw.ReceivedAt, isTest).Report is not { } report)
                return NormalizeResult.Ignored();

            var issue = new IssueInfo(isTest ? "YDITS（テスト電文）" : "YDITS / 気象庁",
                report.IssuedAt, "VXSE43", CorrectionType.None,
                InformationType: report.IsCancelled ? "取消" : report.IsFinal ? "最終報" : "発表");
            var item = new EewEvent(report.Id, raw.Provider, report.IssuedAt, raw.ReceivedAt,
                string.Empty, raw.SourceMode, issue, report.Earthquake, [], isWarning: !report.IsCancelled,
                isFinal: report.IsFinal, isCancelled: report.IsCancelled, isTest: isTest)
            {
                SourceMessageTime = report.IssuedAt,
            };
            return NormalizeResult.Success(item with { Signature = signatureBuilder.Build(item) }) with
            {
                PreviewOnly = isTest,
            };
        }
        catch (JsonException)
        {
            return NormalizeResult.Invalid(
                new ValidationIssue("ydits.vxse43", "YDITSのVXSE43応答の形式が不正です。", ValidationSeverity.Error));
        }
    }

    internal static (bool IsActive, WarningReport? Report) ReadResponse(string json, DateTimeOffset receivedAt, bool isTest)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        JsonElement root = document.RootElement;
        if (ReadString(root, "status") != "OK") throw new JsonException("Invalid YDITS response status.");
        bool isActive = ReadBoolean(root, "isEew");
        if (!root.TryGetProperty("report", out JsonElement report) || report.ValueKind == JsonValueKind.Null)
        {
            if (isActive) throw new JsonException("Active YDITS response has no report.");
            return (false, null);
        }
        if (!isActive && report.ValueKind == JsonValueKind.Object && !report.TryGetProperty("isLast", out _))
            return (false, null);

        switch (ReadString(report, "type"))
        {
            case "drill":
            case "test":
                if (!isTest) return (isActive, null);
                break;
            case "normal":
                break;
            default:
                throw new JsonException("Unknown YDITS report type.");
        }

        bool isLast = ReadBoolean(report, "isLast");
        bool isFinal = ReadBoolean(report, "isFinal");
        bool isCancelled = ReadBoolean(report, "isCanceled");
        bool isWarning = ReadBoolean(report, "isWarning");
        if ((isFinal || isCancelled) != isLast || (isCancelled && (isFinal || isWarning)))
            throw new JsonException("Inconsistent YDITS report flags.");
        if ((!isTest && !isActive && !isLast) || (!isCancelled && !isWarning)) return (isActive, null);

        DateTimeOffset issuedAt = ReadTime(report, "time");
        // Mirrors can retain a previous report. Admit only recent publications.
        if (!isTest && (issuedAt < receivedAt.AddMinutes(-2) || issuedAt > receivedAt.AddSeconds(10)))
            return (isActive, null);

        if (!report.TryGetProperty("earthquake", out JsonElement quake) || quake.ValueKind == JsonValueKind.Null)
        {
            // The published cancellation schema has no correlation key when the
            // earthquake object is omitted. Never apply it to the last warning by guesswork.
            if (isCancelled) return (isActive, null);
            throw new JsonException("YDITS warning has no earthquake.");
        }

        DateTimeOffset detectedAt = ReadTime(quake, "arrivalTime");
        // The public schema has no event ID. Use detection time because origin time can be revised.
        var id = EventId.Create("ydits-" + detectedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        EarthquakeInfo? earthquake = isCancelled ? null : ReadEarthquake(quake, detectedAt);
        return (isActive, new WarningReport(id, issuedAt, earthquake, isFinal, isCancelled));
    }

    private static EarthquakeInfo ReadEarthquake(JsonElement quake, DateTimeOffset detectedAt)
    {
        bool provisional = ReadBoolean(quake, "condition");
        int? depth = quake.TryGetProperty("depth", out var depthValue) &&
            depthValue.ValueKind == JsonValueKind.Number && depthValue.TryGetInt32(out int km) && km >= 0
            ? km : null;
        double? magnitude = quake.TryGetProperty("magnitude", out var magnitudeValue) &&
            magnitudeValue.ValueKind == JsonValueKind.Number && magnitudeValue.TryGetDouble(out double mj) && double.IsFinite(mj)
            ? mj : null;
        var hypocenter = new HypocenterInfo(ReadString(quake, "hypocenterName").Trim(), string.Empty, null, null,
            provisional ? null : depth, provisional ? null : magnitude, provisional ? "仮定震源要素" : string.Empty);
        return new EarthquakeInfo(ReadTime(quake, "originTime"), detectedAt, hypocenter,
            ReadString(quake, "intensity") switch
            {
                "0" => JmaScale.Zero,
                "1" => JmaScale.One,
                "2" => JmaScale.Two,
                "3" => JmaScale.Three,
                "4" => JmaScale.Four,
                "5-" => JmaScale.FiveLower,
                "5+" => JmaScale.FiveUpper,
                "6-" => JmaScale.SixLower,
                "6+" => JmaScale.SixUpper,
                "7" => JmaScale.Seven,
                _ => JmaScale.Unknown,
            }, DomesticTsunami.Unknown, ForeignTsunami.Unknown);
    }

    private static string ReadString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException($"Missing YDITS {name}.");

    private static bool ReadBoolean(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new JsonException($"Missing YDITS {name}.");

    private static DateTimeOffset ReadTime(JsonElement item, string name)
    {
        string text = ReadString(item, name);
        if ((text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10) &&
            item.GetProperty(name).TryGetDateTimeOffset(out var time))
            return time;
        throw new JsonException($"Invalid YDITS {name}.");
    }

    internal sealed record WarningReport(EventId Id, DateTimeOffset IssuedAt, EarthquakeInfo? Earthquake,
        bool IsFinal, bool IsCancelled);
}
