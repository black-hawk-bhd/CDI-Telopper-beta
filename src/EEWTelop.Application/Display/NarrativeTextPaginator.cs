using System.Text.RegularExpressions;

namespace EEWTelop.Application.Display;

/// <summary>
/// OBS/WPFの実表示幅を概算し、説明文が指定行数を超えないようにページ分割する。
/// 地域・観測点などの構造化一覧には使用しない。
/// </summary>
internal static partial class NarrativeTextPaginator
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

        double maximumColumns = EstimatedColumnsPerVisualLine * maximumVisualLines;
        string[] clauses = ClauseBoundaryPattern()
            .Split(value)
            .Where(static clause => !string.IsNullOrWhiteSpace(clause))
            .ToArray();
        string current = string.Empty;
        foreach (string clause in clauses)
        {
            if (EstimateDisplayColumns(clause) > maximumColumns)
            {
                if (!string.IsNullOrWhiteSpace(current))
                {
                    yield return current.Trim();
                    current = string.Empty;
                }

                foreach (string fragment in SplitAtDisplayWidth(clause, maximumColumns))
                {
                    yield return fragment;
                }

                continue;
            }

            string candidate = current + clause;
            if (!string.IsNullOrWhiteSpace(current) &&
                EstimateDisplayColumns(candidate) > maximumColumns)
            {
                yield return current.Trim();
                current = clause;
            }
            else
            {
                current = candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            yield return current.Trim();
        }
    }

    private static IEnumerable<string> SplitAtDisplayWidth(string value, double maximumColumns)
    {
        int start = 0;
        double columns = 0;
        for (int index = 0; index < value.Length; index++)
        {
            double characterColumns = GetCharacterColumns(value[index]);
            if (index > start && columns + characterColumns > maximumColumns)
            {
                yield return value[start..index].Trim();
                start = index;
                columns = 0;
            }

            columns += characterColumns;
        }

        if (start < value.Length)
        {
            yield return value[start..].Trim();
        }
    }

    private static double EstimateDisplayColumns(string value) =>
        value.Sum(GetCharacterColumns);

    private static double GetCharacterColumns(char character) => character <= 0x7f ? 0.55 : 1;

    [GeneratedRegex(@"(?<=[、,，；;])", RegexOptions.CultureInvariant)]
    private static partial Regex ClauseBoundaryPattern();
}
