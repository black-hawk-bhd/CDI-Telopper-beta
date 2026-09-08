namespace EEWTelop.Application.Display;

internal static class WeatherTextPagination
{
    internal static double Width(string text) => text.Sum(c => c <= 127 ? .55 : 1);

    // Protected spans may exceed the estimate: preserve their meaning instead of truncating them.
    internal static IEnumerable<string> Split(string text, IEnumerable<string> names, int columns)
    {
        var protectedBoundaries = new bool[text.Length + 1];
        foreach (string name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            for (int start = text.IndexOf(name, StringComparison.Ordinal); start >= 0;
                 start = text.IndexOf(name, start + 1, StringComparison.Ordinal))
                for (int i = start + 1; i < start + name.Length; i++) protectedBoundaries[i] = true;
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if ("（([【「『".Contains(text[i])) depth++;
            if (depth > 0) protectedBoundaries[i + 1] = true;
            if ("）)]】」』".Contains(text[i]) && depth > 0)
            {
                depth--;
                if (depth == 0) protectedBoundaries[i + 1] = false;
            }
        }
        int offset = 0;
        while (offset < text.Length)
        {
            int end = offset;
            double width = 0;
            while (end < text.Length && width + Width(text[end].ToString()) <= columns)
                width += Width(text[end++].ToString());
            if (end == text.Length) { yield return text[offset..].Trim(); yield break; }
            int candidate = end;
            while (candidate > offset && protectedBoundaries[candidate]) candidate--;
            if (candidate == offset)
            {
                candidate = Math.Max(end, offset + 1);
                while (candidate < text.Length && protectedBoundaries[candidate]) candidate++;
            }
            // Prefer punctuation outside protected spans, without dropping any source text.
            for (int i = candidate; i > offset; i--)
                if (!protectedBoundaries[i] && "、。；;，,　 ".Contains(text[i - 1]))
                { candidate = i; break; }
            foreach (string phrase in new[] { "最大級の警戒", "レベル５", "レベル４", "レベル３", "レベル２", "レベル5", "レベル4", "レベル3", "レベル2" })
            {
                int index = text.IndexOf(phrase, offset + 1, StringComparison.Ordinal);
                if (index > offset && index <= end && !protectedBoundaries[index])
                { candidate = index; break; }
            }
            yield return text[offset..candidate].Trim();
            offset = candidate;
        }
    }

    internal static IEnumerable<string> Group(string[] names, int count, Func<string[], string> format)
    {
        var current = new List<string>();
        foreach (string name in names)
        {
            if (current.Count > 0 && (current.Count == count ||
                Width(format(current.Append(name).ToArray())) > 48))
            { yield return format(current.ToArray()); current.Clear(); }
            current.Add(name);
        }
        if (current.Count > 0) yield return format(current.ToArray());
    }
}
