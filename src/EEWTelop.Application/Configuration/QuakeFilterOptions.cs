using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Configuration;

public enum QuakePrefectureFilterMode
{
    EventAndPoints = 0,
    EventOnly,
    PointsOnly,
}

public static class QuakeFilterOptions
{
    public static IReadOnlyList<JmaScale> ExcludableScales { get; } =
    [
        JmaScale.One, JmaScale.Two, JmaScale.Three, JmaScale.Four,
        JmaScale.FiveLower, JmaScale.FiveUpper, JmaScale.SixLower, JmaScale.SixUpper,
    ];

    public static JmaScale[] ResolveExcludedScales(FilterSettings filter) =>
        (filter.ExcludedQuakeMaximumScales ??
            (filter.HideQuakeBelowIntensity3 ? [JmaScale.One, JmaScale.Two] : []))
        .Where(ExcludableScales.Contains).Distinct().Order().ToArray();

    public static string? FindPrefectureCode(string prefecture, string municipalityCode = "")
    {
        string name = prefecture.Trim();
        WeatherPrefectureOption? option = WeatherPrefectureCatalog.Options
            .FirstOrDefault(item => item.Code.Length > 0 &&
                (item.Name == name ||
                 (item.Name != "北海道" && item.Name[..^1] == name)));
        if (option is not null)
        {
            return option.Code;
        }

        // Municipality codes use prefectural prefixes; seismic-area codes do not.
        string code = municipalityCode.Trim();
        return code.Length >= 5 && WeatherPrefectureCatalog.IsSupported(code[..2])
            ? code[..2]
            : null;
    }
}
