namespace EEWTelop.Infrastructure.Axis.Transport;

using System.Xml.Linq;
using EEWTelop.Infrastructure.Axis.Configuration;

internal static class AxisWeatherTelegramPolicy
{
    private static readonly HashSet<string> SeismologyTelegrams =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "VXSE43", "VXSE45", "VXSE51", "VXSE52", "VXSE53", "VXSE62",
            "VYSE50", "VYSE60", "VTSE41", "VTSE51", "VTSE52",
        };

    private static readonly HashSet<string> LegacyWarningTelegrams =
        new(StringComparer.OrdinalIgnoreCase) { "VPWW53", "VPWW54" };

    public static bool ShouldAccept(string? telegramType)
    {
        string type = telegramType?.Trim() ?? string.Empty;
        return !LegacyWarningTelegrams.Contains(type);
    }

    public static bool IsAssignedToAxis(string? channel, string? telegramType)
    {
        if (string.Equals(
            channel,
            AxisProviderOptions.EewChannel,
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(
            channel,
            AxisProviderOptions.MeteorologyChannel,
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(
            channel,
            AxisProviderOptions.VolcanologyChannel,
            StringComparison.OrdinalIgnoreCase))
        {
            return telegramType is "VFVO50" or "VFVO56";
        }

        string type = telegramType?.Trim() ?? string.Empty;
        return string.Equals(
                channel,
                AxisProviderOptions.SeismologyChannel,
                StringComparison.OrdinalIgnoreCase) &&
            SeismologyTelegrams.Contains(type);
    }

    public static bool IsAssignedRecoveryTelegram(string? telegramType)
    {
        string type = telegramType?.Trim() ?? string.Empty;
        return SeismologyTelegrams.Contains(type) ||
            type is "VFVO50" or "VFVO56" ||
            type.StartsWith("VP", StringComparison.OrdinalIgnoreCase);
    }

    public static string ReadTelegramType(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return string.Empty;
        }

        XDocument document = XDocument.Parse(xml, LoadOptions.None);
        return ReadTelegramType(document);
    }

    public static string ReadTelegramType(XContainer document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // A JMA telegram code belongs to Control/Type.  AXIS payloads do not
        // always contain that element, while their Body can contain unrelated
        // elements named Type (for example "hazard level").  Returning the
        // first Type element therefore allowed legacy VPWW53/54 telegrams to
        // pass the suppression policy.  Only accept values that actually look
        // like telegram codes and then fall back to the report identifier.
        string? controlType = document.Descendants()
            .Where(static element =>
                element.Name.LocalName.Equals("Type", StringComparison.OrdinalIgnoreCase) &&
                element.Parent?.Name.LocalName.Equals(
                    "Control",
                    StringComparison.OrdinalIgnoreCase) == true)
            .Select(static element => element.Value.Trim())
            .FirstOrDefault(IsTelegramTypeToken);
        if (controlType is not null)
        {
            return controlType;
        }

        IEnumerable<string> identifiers = GetElementsIncludingRoot(document)
            .Where(static element => IsIdentifierName(element.Name.LocalName))
            .Select(static element => element.Value)
            .Concat(GetElementsIncludingRoot(document)
                .Attributes()
                .Where(static attribute => IsIdentifierName(attribute.Name.LocalName))
                .Select(static attribute => attribute.Value));
        foreach (string identifier in identifiers)
        {
            string? identifierType = identifier
                .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(IsTelegramTypeToken);
            if (identifierType is not null)
            {
                return identifierType;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<XElement> GetElementsIncludingRoot(
        XContainer container) => container switch
        {
            XDocument document when document.Root is not null =>
                document.Root.DescendantsAndSelf(),
            XElement element => element.DescendantsAndSelf(),
            _ => container.Descendants(),
        };

    private static bool IsIdentifierName(string localName) =>
        localName.Equals("uuid", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("id", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("identifier", StringComparison.OrdinalIgnoreCase);

    private static bool IsTelegramTypeToken(string value) =>
        value.Length == 6 &&
        value[..4].All(static character => character is >= 'A' and <= 'Z') &&
        value[4..].All(static character => character is >= '0' and <= '9');
}
