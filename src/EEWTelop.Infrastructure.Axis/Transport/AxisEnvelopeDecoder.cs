using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using EEWTelop.Application.Events;
using EEWTelop.Infrastructure.Axis.Configuration;

namespace EEWTelop.Infrastructure.Axis.Transport;

internal enum AxisFrameKind
{
    Hello,
    Heartbeat,
    Data,
    Ignored,
}

internal sealed record AxisDecodedFrame(
    AxisFrameKind Kind,
    string? Channel = null,
    string? Xml = null,
    bool IsTest = false,
    string? TelegramType = null,
    string? Json = null)
{
    public string? ProviderPayload => Json ?? Xml;

    public RawProviderContentFormat ContentFormat => Json is null
        ? RawProviderContentFormat.JmaXml
        : RawProviderContentFormat.Json;
}

internal static class AxisEnvelopeDecoder
{
    public static AxisDecodedFrame Decode(string payload, string expectedChannel)
    {
        string trimmed = payload.Trim();
        if (string.Equals(trimmed, "hello", StringComparison.OrdinalIgnoreCase))
        {
            return new AxisDecodedFrame(AxisFrameKind.Hello);
        }

        if (string.Equals(trimmed, "hb", StringComparison.OrdinalIgnoreCase))
        {
            return new AxisDecodedFrame(AxisFrameKind.Heartbeat);
        }

        using JsonDocument document = JsonDocument.Parse(trimmed);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("channel", out JsonElement channelElement) ||
            !root.TryGetProperty("message", out JsonElement message))
        {
            throw new InvalidDataException("AXIS frame did not contain channel and message.");
        }

        string channel = channelElement.GetString() ?? string.Empty;
        if (!AxisProviderOptions.ParseChannels(expectedChannel)
            .Contains(channel, StringComparer.OrdinalIgnoreCase))
        {
            return new AxisDecodedFrame(AxisFrameKind.Ignored, channel);
        }

        if (string.Equals(
            channel,
            AxisProviderOptions.EewChannel,
            StringComparison.OrdinalIgnoreCase))
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("AXIS eew message root must be a JSON object.");
            }

            bool isTraining = message.TryGetProperty("Flag", out JsonElement flag) &&
                flag.ValueKind == JsonValueKind.Object &&
                flag.TryGetProperty("is_training", out JsonElement training) &&
                training.ValueKind == JsonValueKind.True;
            return new AxisDecodedFrame(
                AxisFrameKind.Data,
                channel,
                IsTest: isTraining,
                TelegramType: "AXIS-EEW",
                Json: message.GetRawText());
        }

        string xml;
        if (message.ValueKind == JsonValueKind.String &&
            (message.GetString() ?? string.Empty).TrimStart().StartsWith('<'))
        {
            xml = message.GetString()!;
        }
        else
        {
            XElement report = ConvertRoot(message);
            xml = new XDocument(report).ToString(SaveOptions.DisableFormatting);
        }

        XDocument parsed = XDocument.Parse(xml, LoadOptions.None);
        if (!parsed.Descendants().Any(item => item.Name.LocalName == "Control") ||
            !parsed.Descendants().Any(item => item.Name.LocalName == "Head"))
        {
            throw new InvalidDataException("AXIS JMA message was missing Control or Head.");
        }

        string status = parsed.Descendants()
            .FirstOrDefault(item => item.Name.LocalName == "Status")?.Value ?? string.Empty;
        bool isTest = status.Contains("訓練", StringComparison.Ordinal) ||
            status.Contains("試験", StringComparison.Ordinal);
        string telegramType = AxisWeatherTelegramPolicy.ReadTelegramType(parsed);
        return new AxisDecodedFrame(
            AxisFrameKind.Data,
            channel,
            xml,
            isTest,
            telegramType);
    }

    private static XElement ConvertRoot(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("AXIS message root must be a JSON object.");
        }

        JsonProperty[] properties = message.EnumerateObject().ToArray();
        if (properties.Length == 1 && properties[0].Name is "Report" or "jmx:Report")
        {
            return ConvertElement("Report", properties[0].Value);
        }

        return ConvertElement("Report", message);
    }

    private static XElement ConvertElement(string rawName, JsonElement value)
    {
        var element = new XElement(SafeName(rawName));
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.NameEquals("valueOf_"))
                {
                    element.Add(ReadPrimitive(property.Value));
                }
                else if (property.NameEquals("description"))
                {
                    // AXIS' JMA XML-to-JSON representation keeps the
                    // element-basis `description` attribute without the
                    // trailing underscore used by attributes such as type_.
                    // Treating it as a child element makes XElement.Value
                    // concatenate the schema value and its display label.
                    element.SetAttributeValue("description", ReadPrimitive(property.Value));
                }
                else if (property.Name.EndsWith('_') &&
                    !property.Name.StartsWith("xmlns", StringComparison.OrdinalIgnoreCase))
                {
                    element.SetAttributeValue(
                        SafeName(property.Name.TrimEnd('_')),
                        ReadPrimitive(property.Value));
                }
                else if (property.Name.StartsWith("xmlns", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                else if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in property.Value.EnumerateArray())
                    {
                        element.Add(ConvertElement(property.Name, item));
                    }
                }
                else
                {
                    element.Add(ConvertElement(property.Name, property.Value));
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                element.Add(ConvertElement("Item", item));
            }
        }
        else
        {
            element.Value = ReadPrimitive(value);
        }

        return element;
    }

    private static string SafeName(string name)
    {
        int colon = name.LastIndexOf(':');
        string local = colon >= 0 ? name[(colon + 1)..] : name;
        return XmlConvert.EncodeLocalName(local.TrimEnd('_'));
    }

    private static string ReadPrimitive(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => value.GetRawText(),
    };
}
