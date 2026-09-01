using System.Text.RegularExpressions;

namespace EEWTelop.Application.Formatting;

public static partial class PlaceNormalizer
{
    public static string BuildDisplayName(string? prefecture, string? address, bool isArea)
    {
        string pref = prefecture?.Trim() ?? string.Empty;
        string rawAddress = address?.Trim() ?? string.Empty;
        string place = isArea ? rawAddress : ShortMunicipality(rawAddress);

        if (pref.Length > 0 && place.Length > 0)
        {
            return place.StartsWith(pref, StringComparison.Ordinal) ? place : pref + place;
        }

        return pref.Length > 0 ? pref : place.Length > 0 ? place : "地点不明";
    }

    private static string ShortMunicipality(string address)
    {
        if (address.Length == 0)
        {
            return string.Empty;
        }

        Match match = MunicipalityPattern().Match(address);
        return match.Success ? match.Groups[1].Value : address;
    }

    [GeneratedRegex("^(.+?(?:特別区|市|区|町|村|郡))", RegexOptions.CultureInvariant)]
    private static partial Regex MunicipalityPattern();
}
