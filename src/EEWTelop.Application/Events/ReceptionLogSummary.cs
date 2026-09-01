using System.Globalization;
using System.Text;
using System.Text.Json;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

public sealed record ReceptionLogSummary(
    DateTimeOffset ReceivedAt,
    int? ProviderCode,
    string EventId,
    string EventType,
    string ProcessingResult,
    string ReportNumber)
{
    private const int MaximumFieldLength = 128;
    private const string MissingValue = "—";

    public static ReceptionLogSummary Create(
        RawProviderMessage raw,
        DisasterEvent? disasterEvent,
        EventIngestionStatus status,
        bool displayed = false,
        string? suppressionReason = null,
        int normalizedItemCount = 0,
        int displayedItemCount = 0,
        int unknownWeatherItemCount = 0)
    {
        ArgumentNullException.ThrowIfNull(raw);

        SafeEnvelope envelope = raw.ContentFormat == RawProviderContentFormat.Json
            ? ReadSafeEnvelope(raw.Payload)
            : new SafeEnvelope();
        int? providerCode = disasterEvent?.ProviderCode ?? envelope.ProviderCode;
        string eventId = Clean(disasterEvent?.Id.Value) ?? envelope.EventId ?? MissingValue;
        string reportNumber = Clean(GetReportNumber(disasterEvent)) ??
            envelope.ReportNumber ??
            MissingValue;

        return new ReceptionLogSummary(
            raw.ReceivedAt,
            providerCode,
            eventId,
            GetEventType(disasterEvent?.Kind, providerCode),
            GetProcessingResult(status, displayed, suppressionReason),
            reportNumber)
        {
            NormalizedItemCount = normalizedItemCount,
            DisplayedItemCount = displayedItemCount,
            UnknownWeatherItemCount = unknownWeatherItemCount,
        };
    }

    public int NormalizedItemCount { get; init; }

    public int DisplayedItemCount { get; init; }

    public int UnknownWeatherItemCount { get; init; }

    public string ToLogMessage()
    {
        DateTimeOffset localReceivedAt = ReceivedAt.ToLocalTime();
        return string.Create(CultureInfo.InvariantCulture, $"受信時刻={localReceivedAt:yyyy-MM-dd HH:mm:ss.fff zzz} コード={ProviderCode?.ToString(CultureInfo.InvariantCulture) ?? MissingValue} イベントID={EventId} 種別={EventType} 処理結果={ProcessingResult} 報番号={ReportNumber} 正規化項目数={NormalizedItemCount} 表示項目数={DisplayedItemCount} 未知警報項目数={UnknownWeatherItemCount}");
    }

    private static SafeEnvelope ReadSafeEnvelope(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SafeEnvelope();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SafeEnvelope();
            }

            int? providerCode = TryReadInt32(root, "code");
            string? eventId = TryReadText(root, "eventId") ??
                TryReadText(root, "id") ??
                TryReadText(root, "_id");
            string? reportNumber = TryReadText(root, "serial");
            if (root.TryGetProperty("issue", out JsonElement issue) &&
                issue.ValueKind == JsonValueKind.Object)
            {
                eventId = TryReadText(issue, "eventId") ?? eventId;
                reportNumber = TryReadText(issue, "serial") ?? reportNumber;
            }

            return new SafeEnvelope(providerCode, eventId, reportNumber);
        }
        catch (JsonException)
        {
            return new SafeEnvelope();
        }
    }

    private static int? TryReadInt32(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static string? TryReadText(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => Clean(value.GetString()),
            JsonValueKind.Number => Clean(value.GetRawText()),
            _ => null,
        };
    }

    private static string? GetReportNumber(DisasterEvent? disasterEvent) => disasterEvent switch
    {
        QuakeEvent quake => quake.Issue.Serial,
        TsunamiEvent tsunami => tsunami.Issue.Serial,
        EewEvent eew => eew.Issue.Serial,
        WeatherWarningEvent weather => weather.Issue.Serial,
        VolcanoEvent volcano => volcano.Issue.Serial,
        _ => null,
    };

    private static string GetEventType(EventKind? kind, int? providerCode) => kind switch
    {
        EventKind.Eew => "緊急地震速報",
        EventKind.Quake => "地震情報",
        EventKind.Tsunami => "津波情報",
        EventKind.WeatherWarning => "気象警報・注意報",
        EventKind.Volcano => "火山情報",
        _ => providerCode switch
        {
            551 => "地震情報",
            552 => "津波情報",
            556 => "緊急地震速報",
            WeatherWarningEvent.InternalProviderCode => "気象警報・注意報",
            VolcanoEvent.InternalProviderCode => "火山情報",
            _ => "不明",
        },
    };

    private static string GetProcessingResult(
        EventIngestionStatus status,
        bool displayed,
        string? suppressionReason) => status switch
    {
        EventIngestionStatus.Accepted when displayed => "採用・表示",
        EventIngestionStatus.Accepted =>
            $"採用・非表示({Clean(suppressionReason) ?? "理由不明"})",
        EventIngestionStatus.Duplicate => "重複",
        EventIngestionStatus.Ignored => "対象外",
        EventIngestionStatus.Invalid => "不正",
        _ => status.ToString(),
    };

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaximumFieldLength));
        foreach (char character in value.Trim())
        {
            if (builder.Length >= MaximumFieldLength)
            {
                break;
            }

            builder.Append(char.IsControl(character) || char.IsWhiteSpace(character) ? ' ' : character);
        }

        string cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private sealed record SafeEnvelope(
        int? ProviderCode = null,
        string? EventId = null,
        string? ReportNumber = null);
}
