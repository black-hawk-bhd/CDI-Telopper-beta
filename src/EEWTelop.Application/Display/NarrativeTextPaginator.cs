namespace EEWTelop.Application.Display;

/// <summary>
/// OBS/WPFの実表示幅を概算し、説明文が指定行数を超えないようにページ分割する。
/// 地域・観測点などの構造化一覧には使用しない。
/// </summary>
internal static class NarrativeTextPaginator
{
    internal const int EstimatedColumnsPerVisualLine = 24;
    internal const int DefaultMaximumVisualLines = 3;

    public static IReadOnlyList<IReadOnlyList<string>> Paginate(
        IEnumerable<string> source,
        int maximumVisualLines = DefaultMaximumVisualLines)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumVisualLines, 1);

        string[] fragments = source
            .SelectMany(line => SplitForDisplay(line, maximumVisualLines))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        var pages = new List<IReadOnlyList<string>>();
        var pageLines = new List<string>(maximumVisualLines);
        int pageVisualLines = 0;
        foreach (string fragment in fragments)
        {
            int visualLines = EstimateVisualLineCount(fragment);
            if (pageLines.Count > 0 && pageVisualLines + visualLines > maximumVisualLines)
            {
                pages.Add(pageLines.ToArray());
                pageLines.Clear();
                pageVisualLines = 0;
            }

            pageLines.Add(fragment);
            pageVisualLines += visualLines;
        }

        if (pageLines.Count > 0)
        {
            pages.Add(pageLines.ToArray());
        }

        return pages;
    }

    public static int EstimateVisualLineCount(string value)
    {
        string[] explicitLines = value
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n');
        return explicitLines.Sum(line => Math.Max(
            1,
            (int)Math.Ceiling(EstimateDisplayColumns(line) /
                EstimatedColumnsPerVisualLine)));
    }

    private static IEnumerable<string> SplitForDisplay(
        string value,
        int maximumVisualLines)
    {
        value = value.Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        if (EstimateVisualLineCount(value) <= maximumVisualLines)
        {
            yield return value;
            yield break;
        }

        if (value.Contains('\n'))
        {
            foreach (string explicitLine in value.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                foreach (string fragment in SplitForDisplay(explicitLine, maximumVisualLines))
                {
                    yield return fragment;
                }
            }

            yield break;
        }

        double maximumColumns = (double)EstimatedColumnsPerVisualLine * maximumVisualLines;
        int start = 0;
        while (start < value.Length)
        {
            int end = start;
            double columns = 0;
            while (end < value.Length)
            {
                double characterColumns = GetCharacterColumns(value[end]);
                if (columns + characterColumns > maximumColumns)
                {
                    break;
                }

                columns += characterColumns;
                end++;
            }

            if (end < value.Length)
            {
                int limit = end;
                while (end > start && IsTrailingPunctuation(value[end])) end--;
                if (end == start) end = limit;
                if (char.IsSurrogatePair(value, end - 1)) end--;
                end = start + FindPageBreak(value.AsSpan(start, end - start));
            }

            yield return value[start..end].Trim();
            start = end;
        }
    }

    private static int FindPageBreak(ReadOnlySpan<char> text)
    {
        int sentenceEnd = text.LastIndexOf('。');
        if (sentenceEnd >= 0)
        {
            while (sentenceEnd + 1 < text.Length && IsTrailingPunctuation(text[sentenceEnd + 1])) sentenceEnd++;
            return sentenceEnd + 1;
        }

        int clauseEnd = text.LastIndexOfAny("、,，；;");
        if (clauseEnd >= 0)
        {
            return clauseEnd + 1;
        }

        int space = text.LastIndexOfAny(" 　\t");
        return space > 0 ? space + 1 : text.Length;
    }

    private static double EstimateDisplayColumns(string value) =>
        value.Sum(GetCharacterColumns);

    private static double GetCharacterColumns(char character) => character <= 0x7f ? 0.55 : 1;

    private static bool IsTrailingPunctuation(char character) =>
        character is '。' or '、' or '，' or ',' or '；' or ';' or '」' or '』' or '）' or ')' or '】' or ']';
}
