using System.Text.Json;
using System.Windows;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Controls;

internal static class TrialMunicipalityPoints
{
    internal sealed record Location(string Code, string Prefecture, string Name, Point Position);
    private static readonly Location[] Locations = Load();

    internal static Location? Resolve(QuakePoint point)
    {
        // Exact municipality names only: never turn a station-name prefix into a city observation.
        string address = point.Address.Trim();
        string prefecture = point.Prefecture.Trim();
        if (prefecture.Length == 0) return null;
        if (address.StartsWith(prefecture, StringComparison.Ordinal)) address = address[prefecture.Length..];
        string shortPrefecture = prefecture == "北海道" ? prefecture : prefecture[..^1];
        var matches = Locations.Where(p => p.Prefecture == prefecture &&
            (p.Name == address || shortPrefecture + p.Name == address)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static Location[] Load()
    {
        using var stream = typeof(TrialMunicipalityPoints).Assembly.GetManifestResourceStream(
            "EEWTelop.Wpf.Assets.trial-municipality-points.json") ?? throw new InvalidOperationException("Municipality map data missing.");
        using var document = JsonDocument.Parse(stream);
        var locations = document.RootElement.GetProperty("points").EnumerateArray().Select(p => new Location(
            "city:" + p.GetProperty("code").GetString(), p.GetProperty("prefecture").GetString()!,
            p.GetProperty("name").GetString()!, new Point(p.GetProperty("longitude").GetDouble(),
                p.GetProperty("latitude").GetDouble()))).Where(p => TrialQuakeMap.InBounds(p.Position.X, p.Position.Y)).ToArray();
        // Some official city entries are split into subareas. Combine only a shared municipality
        // code and identical city-name prefix, never unrelated names or received station prefixes.
        var combined = locations.GroupBy(p => p.Code[..10]).Select(group =>
        {
            string name = group.First().Name;
            int end = name.IndexOf('市');
            string city = end >= 0 ? name[..(end + 1)] : string.Empty;
            if (group.Count() < 2 || city.Length == 0 ||
                !group.All(p => p.Name.StartsWith(city, StringComparison.Ordinal)) ||
                locations.Any(p => p.Prefecture == group.First().Prefecture && p.Name == city)) return null;
            return new Location(group.Key + "00", group.First().Prefecture, city,
                new Point(group.Average(p => p.Position.X), group.Average(p => p.Position.Y)));
        }).OfType<Location>();
        return locations.Concat(combined).ToArray();
    }
}
