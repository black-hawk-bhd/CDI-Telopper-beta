using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Wolfx.Normalization;

public sealed partial class WolfxEventNormalizer : IEventNormalizer
{
    private static readonly TimeSpan JapanOffset = TimeSpan.FromHours(9);
    private readonly IEventSignatureBuilder _signatureBuilder;

    public WolfxEventNormalizer(IEventSignatureBuilder signatureBuilder)
    {
        _signatureBuilder = signatureBuilder ??
            throw new ArgumentNullException(nameof(signatureBuilder));
    }

    public NormalizeResult Normalize(RawProviderMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.ContentFormat != RawProviderContentFormat.Json ||
            string.IsNullOrWhiteSpace(raw.Payload))
        {
            return Invalid("json", "Wolfx JSON is empty or has an unsupported format.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw.Payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("json", "Wolfx JSON root must be an object.");
            }

            string type = ReadString(root, "type");
            if (type.Equals("heartbeat", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("pong", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeResult.Ignored();
            }

            if (type.Equals("jma_eew", StringComparison.OrdinalIgnoreCase) ||
                HasProperty(root, "WarnArea"))
            {
                return NormalizeEew(root, raw);
            }

            if (type.Equals("jma_eqlist", StringComparison.OrdinalIgnoreCase) ||
                HasProperty(root, "No1"))
            {
                return NormalizeQuakeList(root, raw);
            }

            return NormalizeResult.Ignored(new ValidationIssue(
                "type",
                $"Unsupported Wolfx message type '{type}'.",
                ValidationSeverity.Warning));
        }
        catch (JsonException exception)
        {
            return Invalid("json", $"Malformed Wolfx JSON: {exception.Message}");
        }
    }

    private NormalizeResult NormalizeEew(JsonElement root, RawProviderMessage raw)
    {
        string eventId = ReadValue(root, "EventID").Trim();
        if (eventId.Length == 0)
        {
            return Invalid("EventID", "Wolfx EEW EventID is required.");
        }

        bool isCancelled = ReadBoolean(root, "isCancel");
        bool isWarning = ReadBoolean(root, "isWarn");
        if (!isCancelled && !isWarning)
        {
            return NormalizeResult.Ignored(new ValidationIssue(
                "isWarn",
                "Wolfx forecast-only EEW was ignored because CDI-Telopper displays warnings.",
                ValidationSeverity.Warning));
        }

        DateTimeOffset issuedAt = ParseJapanTime(
            ReadString(root, "AnnouncedTime"), raw.ReceivedAt);
        DateTimeOffset originTime = ParseJapanTime(
            ReadString(root, "OriginTime"), issuedAt);
        string serial = ReadValue(root, "Serial").Trim();
        string title = ReadString(root, "Title").Trim();
        var issue = new IssueInfo(
            "Wolfx / 気象庁",
            issuedAt,
            "WOLFX-JMA-EEW",
            CorrectionType.None,
            serial.Length == 0 ? null : serial,
            title);
        var hypocenter = new HypocenterInfo(
            ReadString(root, "Hypocenter").Trim(),
            string.Empty,
            ReadNullableDouble(root, "Latitude"),
            ReadNullableDouble(root, "Longitude"),
            ReadNullableInt(root, "Depth"),
            ReadNullableDouble(root, "Magunitude") ??
                ReadNullableDouble(root, "Magnitude"),
            string.Empty);
        var earthquake = new EarthquakeInfo(
            originTime,
            null,
            hypocenter,
            ParseScale(ReadValue(root, "MaxIntensity")),
            DomesticTsunami.Unknown,
            ForeignTsunami.Unknown);
        EewArea[] areas = NormalizeAreas(root);
        bool isTraining = ReadBoolean(root, "isTraining") ||
            ReadBoolean(root, "isAssumption");
        var disasterEvent = new EewEvent(
            EventId.Create(eventId),
            raw.Provider,
            issuedAt,
            raw.ReceivedAt,
            string.Empty,
            raw.SourceMode,
            issue,
            earthquake,
            areas,
            isWarning: true,
            ReadBoolean(root, "isFinal"),
            isCancelled,
            isTraining || raw.SourceMode != SourceMode.Production);
        return SuccessWithSignature(disasterEvent);
    }

    private NormalizeResult NormalizeQuakeList(JsonElement root, RawProviderMessage raw)
    {
        if (!TryGetProperty(root, "No1", out JsonElement entry) ||
            entry.ValueKind != JsonValueKind.Object)
        {
            return Invalid("No1", "Wolfx earthquake list has no latest item.");
        }

        string eventId = ReadValue(entry, "EventID").Trim();
        string location = ReadString(entry, "location").Trim();
        DateTimeOffset eventTime = ParseJapanTime(
            ReadString(entry, "time_full"),
            ParseJapanTime(ReadString(entry, "time"), raw.ReceivedAt));
        if (eventId.Length == 0)
        {
            eventId = $"wolfx-{eventTime:yyyyMMddHHmmss}-{location}";
        }

        string title = ReadString(entry, "Title").Trim();
        string info = ReadString(entry, "info").Trim();
        var issue = new IssueInfo(
            "Wolfx / 気象庁",
            eventTime,
            "WOLFX-JMA-EQLIST",
            CorrectionType.None,
            InformationType: title);
        var hypocenter = new HypocenterInfo(
            location,
            string.Empty,
            ParseNullableDouble(ReadValue(entry, "latitude")),
            ParseNullableDouble(ReadValue(entry, "longitude")),
            ParseDepth(ReadValue(entry, "depth")),
            ParseNullableDouble(ReadValue(entry, "magnitude")),
            string.Empty);
        var earthquake = new EarthquakeInfo(
            eventTime,
            null,
            hypocenter,
            ParseScale(ReadValue(entry, "shindo")),
            ParseDomesticTsunami(info),
            ForeignTsunami.Unknown);
        var disasterEvent = new QuakeEvent(
            EventId.Create(eventId),
            raw.Provider,
            eventTime,
            raw.ReceivedAt,
            string.Empty,
            raw.SourceMode,
            issue,
            QuakeIssueType.DetailScale,
            earthquake,
            [],
            info,
            headline: title);
        return SuccessWithSignature(disasterEvent);
    }

    private static EewArea[] NormalizeAreas(JsonElement root)
    {
        if (!TryGetProperty(root, "WarnArea", out JsonElement areas) ||
            areas.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<EewArea>();
        foreach (JsonElement area in areas.EnumerateArray())
        {
            string name = ReadString(area, "Chiiki").Trim();
            if (name.Length == 0)
            {
                continue;
            }

            JmaScale from = ParseScale(ReadValue(area, "Shindo1"));
            JmaScale to = ParseScale(ReadValue(area, "Shindo2"));
            if (to == JmaScale.Unknown)
            {
                to = from;
            }

            result.Add(new EewArea(
                string.Empty,
                name,
                from,
                (int)to,
                ReadBoolean(area, "Arrive")
                    ? EewWarningKind.ForecastArrived
                    : EewWarningKind.ForecastNotArrived,
                null));
        }

        return result.ToArray();
    }

    private NormalizeResult SuccessWithSignature(DisasterEvent disasterEvent)
    {
        disasterEvent = disasterEvent with
        {
            Signature = _signatureBuilder.Build(disasterEvent),
        };
        return NormalizeResult.Success(disasterEvent);
    }

    private static DomesticTsunami ParseDomesticTsunami(string info)
    {
        if (info.Contains("津波の心配はありません", StringComparison.Ordinal))
        {
            return DomesticTsunami.None;
        }

        if (info.Contains("若干の海面変動", StringComparison.Ordinal))
        {
            return DomesticTsunami.NonEffective;
        }

        if (info.Contains("津波に注意", StringComparison.Ordinal))
        {
            return DomesticTsunami.Watch;
        }

        return DomesticTsunami.Unknown;
    }

    private static int? ParseDepth(string value)
    {
        if (value.Contains("ごく浅い", StringComparison.Ordinal))
        {
            return 0;
        }

        Match match = DepthNumber().Match(value);
        return match.Success && int.TryParse(
            match.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int depth)
                ? depth
                : null;
    }

    private static JmaScale ParseScale(string value) => value.Trim() switch
    {
        "0" => JmaScale.Zero,
        "1" => JmaScale.One,
        "2" => JmaScale.Two,
        "3" => JmaScale.Three,
        "4" => JmaScale.Four,
        "5-" or "5弱" => JmaScale.FiveLower,
        "5+" or "5強" => JmaScale.FiveUpper,
        "6-" or "6弱" => JmaScale.SixLower,
        "6+" or "6強" => JmaScale.SixUpper,
        "7" => JmaScale.Seven,
        _ => JmaScale.Unknown,
    };

    private static DateTimeOffset ParseJapanTime(
        string value,
        DateTimeOffset fallback)
    {
        string[] formats = ["yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm"];
        if (DateTime.TryParseExact(
            value.Trim(),
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out DateTime result))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(result, DateTimeKind.Unspecified), JapanOffset);
        }

        return fallback;
    }

    private static double? ReadNullableDouble(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) &&
        double.IsFinite(number)
            ? number
            : ParseNullableDouble(ReadValue(element, name));

    private static int? ReadNullableInt(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : ParseDepth(ReadValue(element, name));

    private static double? ParseNullableDouble(string value) => double.TryParse(
        value,
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out double number) && double.IsFinite(number)
            ? number
            : null;

    private static bool ReadBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool result) && result,
            JsonValueKind.Number => value.TryGetInt32(out int number) && number != 0,
            _ => false,
        };
    }

    private static string ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadValue(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static bool HasProperty(JsonElement element, string name) =>
        TryGetProperty(element, name, out _);

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static NormalizeResult Invalid(string path, string message) =>
        NormalizeResult.Invalid(new ValidationIssue(
            path,
            message,
            ValidationSeverity.Error));

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DepthNumber();
}
