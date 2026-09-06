using System.Text.RegularExpressions;

namespace EEWTelop.Wpf.Controls;

internal sealed record RubySegment(string Text, string? Reading = null);

// Review-only decoration. Never write readings back into the display program or speech text.
internal static class PlaceNameReadings
{
    private sealed record Entry(string Prefecture, string Name, string Reading);

    private static readonly Dictionary<string, Entry[]> Entries = Load();
    private static readonly Regex Names = new(
        string.Join("|", Entries.Keys.OrderByDescending(name => name.Length)
            .ThenBy(name => name, StringComparer.Ordinal).Select(Regex.Escape)),
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<RubySegment> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<RubySegment>();
        }

        var result = new List<RubySegment>();
        string? prefecture = null;
        var offset = 0;
        Match[] matches;
        try
        {
            matches = Names.Matches(text).Cast<Match>().ToArray();
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

            var candidates = Entries[match.Value];
            if (candidates.Any(entry => entry.Name == entry.Prefecture))
            {
                prefecture = match.Value;
            }

            // A stated prefecture takes precedence; ambiguous names without context stay plain.
            var readings = candidates.Where(entry => prefecture is null || entry.Prefecture == prefecture)
                .Select(entry => entry.Reading).Distinct(StringComparer.Ordinal).ToArray();
            result.Add(new RubySegment(match.Value, readings.Length == 1 ? readings[0] : null));
            offset = match.Index + match.Length;
        }

        if (offset < text.Length)
        {
            result.Add(new RubySegment(text[offset..]));
        }

        return result;
    }

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
            if (fields.Length == 3)
            {
                entries.Add(new Entry(fields[0], fields[1], fields[2]));
            }
        }

        return entries.GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    }
}
