using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Axis.Normalization;

/// <summary>
/// Routes AXIS JMA XML-derived messages and the dedicated eew JSON channel to
/// their matching normalizers.
/// </summary>
public sealed class AxisEventNormalizer : IEventNormalizer
{
    private readonly IEventNormalizer _jmaXmlNormalizer;
    private readonly AxisEewEventNormalizer _eewNormalizer;

    public AxisEventNormalizer(
        IEventNormalizer jmaXmlNormalizer,
        IEventSignatureBuilder signatureBuilder)
    {
        ArgumentNullException.ThrowIfNull(jmaXmlNormalizer);
        ArgumentNullException.ThrowIfNull(signatureBuilder);
        _jmaXmlNormalizer = jmaXmlNormalizer;
        _eewNormalizer = new AxisEewEventNormalizer(signatureBuilder);
    }

    public NormalizeResult Normalize(RawProviderMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return raw.ContentFormat switch
        {
            RawProviderContentFormat.JmaXml => _jmaXmlNormalizer.Normalize(raw),
            RawProviderContentFormat.Json => _eewNormalizer.Normalize(raw),
            _ => NormalizeResult.Ignored(new ValidationIssue(
                "contentFormat",
                $"Unsupported AXIS content format {raw.ContentFormat}.",
                ValidationSeverity.Warning)),
        };
    }
}

internal sealed class AxisEewEventNormalizer : IEventNormalizer
{
    private static readonly Regex DepthNumber = new(
        @"\d+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IEventSignatureBuilder _signatureBuilder;

    public AxisEewEventNormalizer(IEventSignatureBuilder signatureBuilder)
    {
        ArgumentNullException.ThrowIfNull(signatureBuilder);
        _signatureBuilder = signatureBuilder;
    }

    public NormalizeResult Normalize(RawProviderMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (string.IsNullOrWhiteSpace(raw.Payload))
        {
            return Invalid("json", "AXIS eew message JSON is empty.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw.Payload);
            JsonElement message = UnwrapMessage(document.RootElement);
            if (message.ValueKind != JsonValueKind.Object)
            {
                return Invalid("message", "AXIS eew message root must be an object.");
            }

            string eventId = ReadString(message, "EventID");
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return Invalid("EventID", "AXIS eew EventID is required.");
            }

            JsonElement flag = ReadObject(message, "Flag");
            bool isCancelled = ReadBoolean(flag, "is_cancel");
            bool isFinal = ReadBoolean(flag, "is_final");
            bool isTraining = ReadBoolean(flag, "is_training");
            string title = ReadString(message, "Title");
            string text = ReadString(message, "Text");
            JmaScale maximumScale = ParseScale(ReadString(message, "Intensity"));
            bool isWarning = ContainsWarningMarker(title) ||
                ContainsWarningMarker(text) ||
                maximumScale >= JmaScale.FiveLower;
            if (!isCancelled && !isWarning)
            {
                return NormalizeResult.Ignored(new ValidationIssue(
                    "Title",
                    "AXIS eew forecast-only message was ignored.",
                    ValidationSeverity.Warning));
            }

            DateTimeOffset issuedAt = ParseDateTime(
                ReadString(message, "ReportDateTime"),
                raw.ReceivedAt);
            DateTimeOffset originTime = ParseDateTime(
                ReadString(message, "OriginDateTime"),
                issuedAt);
            string serial = ReadValueAsString(message, "Serial");
            var issue = new IssueInfo(
                "AXIS",
                issuedAt,
                "AXIS-EEW",
                CorrectionType.None,
                string.IsNullOrWhiteSpace(serial) ? null : serial,
                isCancelled ? "取消" : "発表");
            var earthquake = new EarthquakeInfo(
                originTime,
                ArrivalTime: null,
                NormalizeHypocenter(message),
                maximumScale,
                DomesticTsunami.Unknown,
                ForeignTsunami.Unknown);
            EewArea[] areas = NormalizeAreas(message);

            var disasterEvent = new EewEvent(
                EventId.Create(eventId.Trim()),
                raw.Provider.Trim(),
                issuedAt,
                raw.ReceivedAt,
                signature: string.Empty,
                raw.SourceMode,
                issue,
                earthquake,
                areas,
                isWarning: true,
                isFinal,
                isCancelled,
                isTest: isTraining || raw.SourceMode != SourceMode.Production);
            disasterEvent = disasterEvent with
            {
                Signature = _signatureBuilder.Build(disasterEvent),
            };
            return NormalizeResult.Success(disasterEvent);
        }
        catch (JsonException exception)
        {
            return Invalid("json", $"Malformed AXIS eew JSON: {exception.Message}");
        }
        catch (FormatException exception)
        {
            return Invalid("json", exception.Message);
        }
    }

    private static JsonElement UnwrapMessage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetProperty(root, "channel", out JsonElement channel) &&
            string.Equals(channel.GetString(), "eew", StringComparison.OrdinalIgnoreCase) &&
            TryGetProperty(root, "message", out JsonElement message))
        {
            return message;
        }

        return root;
    }

    private static HypocenterInfo? NormalizeHypocenter(JsonElement message)
    {
        JsonElement hypocenter = ReadObject(message, "Hypocenter");
        if (hypocenter.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        (double? latitude, double? longitude) = ReadCoordinate(hypocenter);
        return new HypocenterInfo(
            ReadString(hypocenter, "Name").Trim(),
            ReducedName: string.Empty,
            latitude,
            longitude,
            ParseDepth(ReadString(hypocenter, "Depth")),
            ParseDouble(ReadValueAsString(message, "Magnitude")),
            ReadString(hypocenter, "Description").Trim());
    }

    private static EewArea[] NormalizeAreas(JsonElement message)
    {
        if (!TryGetProperty(message, "Forecast", out JsonElement forecast) ||
            forecast.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<EewArea>();
        foreach (JsonElement item in forecast.EnumerateArray())
        {
            string name = ReadString(item, "Name").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            JsonElement intensity = ReadObject(item, "Intensity");
            JmaScale scaleFrom = ParseScale(ReadString(intensity, "From"));
            JmaScale scaleTo = ParseScale(ReadString(intensity, "To"));
            if (scaleTo == JmaScale.Unknown)
            {
                scaleTo = scaleFrom;
            }

            if (scaleFrom < JmaScale.Four && scaleTo < JmaScale.Four)
            {
                continue;
            }

            result.Add(new EewArea(
                Prefecture: string.Empty,
                name,
                scaleFrom,
                ScaleTo: (int)scaleTo,
                EewWarningKind.ForecastNotArrived,
                ArrivalTime: null));
        }

        return result.ToArray();
    }

    private static (double? Latitude, double? Longitude) ReadCoordinate(JsonElement hypocenter)
    {
        if (!TryGetProperty(hypocenter, "Coordinate", out JsonElement coordinate) ||
            coordinate.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        double[] values = coordinate.EnumerateArray()
            .Select(ReadDouble)
            .Where(static value => value is not null)
            .Select(static value => value!.Value)
            .Take(2)
            .ToArray();
        if (values.Length < 2)
        {
            return (null, null);
        }

        if (Math.Abs(values[0]) > 90 && Math.Abs(values[1]) <= 90)
        {
            return (values[1], values[0]);
        }

        return (values[0], values[1]);
    }

    private static int? ParseDepth(string value)
    {
        if (value.Contains("ごく浅い", StringComparison.Ordinal))
        {
            return 0;
        }

        Match match = DepthNumber.Match(value);
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
        "5弱以上" => JmaScale.FiveLowerOrMore,
        "5+" or "5強" => JmaScale.FiveUpper,
        "6-" or "6弱" => JmaScale.SixLower,
        "6+" or "6強" => JmaScale.SixUpper,
        "7" => JmaScale.Seven,
        _ => JmaScale.Unknown,
    };

    private static DateTimeOffset ParseDateTime(string value, DateTimeOffset fallback) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset result)
                ? result
                : fallback;

    private static double? ParseDouble(string value) => double.TryParse(
        value,
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out double result) && double.IsFinite(result)
            ? result
            : null;

    private static double? ReadDouble(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out double number) && double.IsFinite(number) =>
            number,
        JsonValueKind.String => ParseDouble(value.GetString() ?? string.Empty),
        _ => null,
    };

    private static bool ContainsWarningMarker(string value) =>
        value.Contains("警報", StringComparison.Ordinal) ||
        value.Contains("強い揺れに警戒", StringComparison.Ordinal);

    private static JsonElement ReadObject(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string ReadString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadValueAsString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out JsonElement value))
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

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(element, propertyName, out JsonElement value))
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

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static NormalizeResult Invalid(string path, string message) =>
        NormalizeResult.Invalid(new ValidationIssue(path, message, ValidationSeverity.Error));
}
