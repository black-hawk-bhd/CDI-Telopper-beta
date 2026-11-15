using EEWTelop.Application.Configuration;

namespace EEWTelop.Wpf.Controls;

internal enum JmaPlaceCodeTable
{
    AreaForecastLocalE,
    AreaTsunami,
    PointSeismicIntensity,
}

internal static class JmaPlaceReadings
{
    private sealed record Entry(string Code, string? Prefecture, string Reading);

    private static readonly Dictionary<(JmaPlaceCodeTable Table, string Name), Entry[]> Entries = Load();

    public static bool Contains(ReviewPlace place) =>
        place.CodeTable is { } table && Entries.ContainsKey((table, place.Name));

    public static string? Resolve(ReviewPlace place, string? textPrefecture = null)
    {
        if (place.CodeTable is not { } table || !Entries.TryGetValue((table, place.Name), out var entries))
        {
            return null;
        }

        // Verify the name as well as the code. Historical telegrams can use
        // names that differ from the names in the bundled edition of the tables.
        string? prefecture = place.Prefecture.Length == 0 ? textPrefecture : place.Prefecture;
        var candidates = place.AreaCode.Length > 0
            ? entries.Where(entry => entry.Code == place.AreaCode)
            : entries.Where(entry => prefecture is null || entry.Prefecture is null || entry.Prefecture == prefecture);
        var readings = candidates.Select(entry => entry.Reading).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return readings.Length == 1 ? readings[0] : null;
    }

    private static Dictionary<(JmaPlaceCodeTable Table, string Name), Entry[]> Load()
    {
        using var stream = typeof(JmaPlaceReadings).Assembly.GetManifestResourceStream(
            "EEWTelop.Wpf.Assets.jma-place-readings.tsv")
            ?? throw new InvalidOperationException("JMA place reading resource is missing.");
        using var reader = new StreamReader(stream);
        var entries = new List<((JmaPlaceCodeTable Table, string Name) Key, Entry Entry)>();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length == 5 && Enum.TryParse(fields[0], out JmaPlaceCodeTable table))
            {
                string? prefecture = fields[2].Length == 0 ? null : WeatherPrefectureCatalog.Find(fields[2])?.Name;
                entries.Add(((table, fields[3]), new Entry(fields[1], prefecture, fields[4])));
            }
        }

        return entries.GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Entry).ToArray());
    }
}
