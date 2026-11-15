using System.Text.RegularExpressions;
using EEWTelop.Application.Configuration;

namespace EEWTelop.Wpf.Controls;

internal sealed record RubySegment(string Text, string? Reading = null);

public sealed record ReviewPlace(string Name, string AreaCode)
{
    internal JmaPlaceCodeTable? CodeTable { get; init; }

    internal string Prefecture { get; init; } = string.Empty;
}

// Review-only decoration. Never write readings back into the display program or speech text.
internal static class PlaceNameReadings
{
    private sealed record Entry(string Prefecture, string Name, string Reading);

    private static readonly Dictionary<string, Entry[]> Entries = Load();
    private static readonly Regex Names = BuildMatcher(Entries.Keys);

    public static IReadOnlyList<RubySegment> Split(string? text, IReadOnlyList<ReviewPlace>? places = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<RubySegment>();
        }

        var placesByName = (places ?? []).Where(place => text.Contains(place.Name, StringComparison.Ordinal))
            .ToLookup(place => place.Name, StringComparer.Ordinal);
        var jmaNames = placesByName.Where(group => group.Any(JmaPlaceReadings.Contains)).Select(group => group.Key).ToArray();
        var result = new List<RubySegment>();
        string? prefecture = null;
        var offset = 0;
        Match[] matches;
        try
        {
            matches = jmaNames.Length == 0
                ? Names.Matches(text).Cast<Match>().ToArray()
                : MatchWithJmaNames(text, BuildMatcher(jmaNames)).ToArray();
        }
        catch (RegexMatchTimeoutException)
        {
            // Decoration must not prevent an unusually large telegram from being reviewed.
            return [new RubySegment(text)];
        }

        foreach (var match in matches)
        {
            if (match.Index > offset)
            {
                result.Add(new RubySegment(text[offset..match.Index]));
            }

            Entries.TryGetValue(match.Value, out var candidates);
            bool isPrefecture = candidates?.Any(entry => entry.Name == entry.Prefecture) == true;
            bool leftJoined = match.Index > 0 && IsKanji(text[match.Index - 1]) &&
                !(match.Index == offset && result.Count > 0 && result[^1].Reading is not null);
            int end = match.Index + match.Length;
            bool rightJoined = end < text.Length && IsKanji(text[end]) && !isPrefecture;
            if (leftJoined || rightJoined)
            {
                result.Add(new RubySegment(match.Value));
                offset = end;
                continue;
            }
            if (isPrefecture)
            {
                prefecture = match.Value;
            }

            var namedPlaces = placesByName[match.Value];
            var jmaPlaces = namedPlaces.Where(JmaPlaceReadings.Contains).ToArray();
            string? reading = jmaPlaces.Length > 0
                ? ResolveJmaReading(jmaPlaces, prefecture)
                : ResolveMunicipalityReading(candidates ?? [], namedPlaces, prefecture);
            result.Add(new RubySegment(match.Value, reading));
            offset = end;
        }

        if (offset < text.Length)
        {
            result.Add(new RubySegment(text[offset..]));
        }

        return result;
    }

    private static Regex BuildMatcher(IEnumerable<string> names) => new(
        string.Join("|", names.OrderByDescending(name => name.Length)
            .ThenBy(name => name, StringComparer.Ordinal).Select(Regex.Escape)),
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static IEnumerable<Match> MatchWithJmaNames(string text, Regex jmaNames)
    {
        // Only places identified in this telegram enter the JMA matcher. An
        // unrelated region name can otherwise consume a prefecture+station prefix.
        Match postal = Names.Match(text);
        Match jma = jmaNames.Match(text);
        while (postal.Success || jma.Success)
        {
            bool firstJma = jma.Success && (!postal.Success || jma.Index < postal.Index ||
                (jma.Index == postal.Index && jma.Length >= postal.Length));
            Match match = firstJma ? jma : postal;
            yield return match;

            int end = match.Index + match.Length;
            if (postal.Success && postal.Index < end)
            {
                postal = Names.Match(text, end);
            }
            if (jma.Success && jma.Index < end)
            {
                jma = jmaNames.Match(text, end);
            }
        }
    }

    private static string? ResolveJmaReading(ReviewPlace[] places, string? prefecture)
    {
        if (places.Length > 1 && prefecture is not null)
        {
            var localPlaces = places.Where(place => place.Prefecture == prefecture).ToArray();
            if (localPlaces.Length > 0)
            {
                places = localPlaces;
            }
        }

        if (places.Any(place => place.AreaCode.Length > 0))
        {
            places = places.Where(place => place.AreaCode.Length > 0).ToArray();
        }

        var readings = places.Select(place => JmaPlaceReadings.Resolve(place, prefecture))
            .Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return readings.Length == 1 ? readings[0] : null;
    }

    private static string? ResolveMunicipalityReading(
        Entry[] candidates, IEnumerable<ReviewPlace> places, string? prefecture)
    {
        // A code identifies the prefecture, while the exact name identifies the dictionary entry.
        // Conflicting multi-prefecture occurrences without local textual context stay undecorated.
        var codePrefectures = places.Where(place => place.CodeTable is null &&
                place.AreaCode.Length is 5 or 7 && place.AreaCode.All(char.IsAsciiDigit))
            .Select(place => WeatherPrefectureCatalog.Find(place.AreaCode[..2])?.Name)
            .Where(name => name is not null).Distinct(StringComparer.Ordinal).ToArray();
        string? resolvedPrefecture = codePrefectures.Length == 1 ? codePrefectures[0] : prefecture;
        bool ambiguousCode = codePrefectures.Length > 1 &&
            (prefecture is null || !codePrefectures.Contains(prefecture));
        ambiguousCode |= codePrefectures.Length == 1 && prefecture is not null && prefecture != resolvedPrefecture;
        var readings = candidates.Where(entry => !ambiguousCode && (resolvedPrefecture is null || entry.Prefecture == resolvedPrefecture))
            .Select(entry => entry.Reading).Distinct(StringComparer.Ordinal).ToArray();
        return readings.Length == 1 ? readings[0] : null;
    }

    private static bool IsKanji(char value) => value is >= '\u3400' and <= '\u9fff' or '々';

    private static Dictionary<string, Entry[]> Load()
    {
        using var stream = typeof(PlaceNameReadings).Assembly.GetManifestResourceStream(
            "EEWTelop.Wpf.Assets.place-readings.tsv")
            ?? throw new InvalidOperationException("Place reading resource is missing.");
        using var reader = new StreamReader(stream);
        var entries = new List<Entry>();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('\t');
            // A municipal suffix alone is not a place name; never decorate generic 市町村.
            if (fields.Length == 3 && fields[1].Length >= 2)
            {
                entries.Add(new Entry(fields[0], fields[1], fields[2]));
            }
        }

        return entries.GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    }
}
