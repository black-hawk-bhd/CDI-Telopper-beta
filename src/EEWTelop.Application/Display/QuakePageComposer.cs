using System.Text;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Formatting;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

internal static class QuakePageComposer
{
    private const int IntensityRowsPerPage = 2;
    private const int NamesPerRow = 3;

    public static DisplayProgram Compose(QuakeEvent quake, DisplaySettings settings)
    {
        if (quake.IsCancelled)
        {
            string cancellationText = PageComposerSupport.GetCancellationText(
                PageComposerSupport.GetQuakeCancellationSubject(quake));
            return PageComposerSupport.CreateProgram(
                quake,
                settings,
                OverlayPriority.Quake,
                EndPolicy.AutoHide,
                [new PageDraft(
                [
                    new DisplayBlock(
                        string.Empty,
                        cancellationText,
                        string.Empty,
                        DisplayStyleTokens.Summary),
                ])]);
        }

        IReadOnlyList<IntensityRow> intensityRows = BuildIntensityRows(quake.Points);
        IReadOnlyList<PageDraft> pages = quake.IssueType switch
        {
            QuakeIssueType.ScalePrompt => ComposeScalePrompt(quake, intensityRows),
            QuakeIssueType.DetailScale => ComposeDetailed(quake, intensityRows, includeIntensity: true),
            QuakeIssueType.ScaleAndDestination =>
                ComposeDetailed(quake, intensityRows, includeIntensity: true),
            QuakeIssueType.Destination =>
                ComposeDetailed(quake, intensityRows, includeIntensity: false),
            QuakeIssueType.Foreign => ComposeForeign(quake),
            QuakeIssueType.Other => ComposeOther(quake),
            QuakeIssueType.LongPeriodObservation => ComposeLongPeriodObservation(quake),
            QuakeIssueType.NankaiTroughTemporaryInformation =>
                ComposeNankaiTroughTemporaryInformation(quake),
            QuakeIssueType.SubsequentEarthquakeAdvisory =>
                ComposeSubsequentEarthquakeAdvisory(quake),
            _ => ComposeUnknown(quake),
        };

        return PageComposerSupport.CreateProgram(
            quake,
            settings,
            OverlayPriority.Quake,
            EndPolicy.AutoHide,
            pages);
    }

    private static List<PageDraft> ComposeScalePrompt(
        QuakeEvent quake,
        IReadOnlyList<IntensityRow> intensityRows)
    {
        string time = PageComposerSupport.FormatJapanTime(quake.Earthquake.OriginTime);
        var pages = new List<PageDraft>
        {
            CreateSummaryPage(
                quake,
                $"{time}頃　震度３以上の地震がありました",
                string.Empty),
        };

        // 震度速報（VXSE51）には震源・津波判定がまだ含まれず、DMDATAの
        // JMA XMLではDomesticTsunamiがUnknownになる。判定前も注意文を省略しない。
        string tsunamiText = quake.Earthquake.DomesticTsunami is
            DomesticTsunami.Checking or DomesticTsunami.Unknown
            ? "念のため津波に注意してください。"
            : PageComposerSupport.GetDomesticTsunamiText(quake.Earthquake.DomesticTsunami);
        AddAdvisoryPage(pages, tsunamiText);
        AddIntensityPages(
            pages,
            intensityRows,
            "震度速報の対象地域情報はありません");
        AddCommentPage(pages, quake.FreeFormComment);
        return pages;
    }

    private static List<PageDraft> ComposeDetailed(
        QuakeEvent quake,
        IReadOnlyList<IntensityRow> intensityRows,
        bool includeIntensity)
    {
        (string primary, string secondary) = BuildEarthquakeSummary(quake.Earthquake);
        var pages = new List<PageDraft>
        {
            CreateSummaryPage(quake, primary, secondary),
        };

        AddAdvisoryPage(
            pages,
            PageComposerSupport.GetDomesticTsunamiText(quake.Earthquake.DomesticTsunami));
        if (includeIntensity &&
            (intensityRows.Count > 0 || !IsEewNoStrongShakingComment(quake.FreeFormComment)))
        {
            AddIntensityPages(
                pages,
                intensityRows,
                "各地の詳しい震度情報はありません");
        }

        AddCommentPage(pages, quake.FreeFormComment);
        return pages;
    }

    private static List<PageDraft> ComposeForeign(QuakeEvent quake)
    {
        string time = PageComposerSupport.FormatJapanTime(quake.Earthquake.OriginTime);
        string name = quake.Earthquake.Hypocenter?.Name ?? string.Empty;
        string firstLine = string.IsNullOrWhiteSpace(name)
            ? $"{time}頃、海外で"
            : $"{time}頃、{name}付近で";
        string magnitude = MagnitudeFormatter.Format(quake.Earthquake.Hypocenter?.Magnitude);
        string remainingLines = magnitude == "-"
            ? AppendMagnitudeDescription(
                "規模の大きな地震がありました",
                quake.Earthquake.Hypocenter)
            : $"規模の大きな地震がありました\nマグニチュード {magnitude}";
        var pages = new List<PageDraft>
        {
            CreateSummaryPage(quake, firstLine, remainingLines),
        };

        string tsunamiInformation = string.IsNullOrWhiteSpace(quake.FreeFormComment)
            ? PageComposerSupport.GetForeignTsunamiText(quake.Earthquake.ForeignTsunami)
            : quake.FreeFormComment.Trim();
        AddAdvisoryPage(pages, tsunamiInformation);
        return pages;
    }

    private static IReadOnlyList<PageDraft> ComposeOther(QuakeEvent quake)
    {
        string time = PageComposerSupport.FormatJapanTime(quake.Earthquake.OriginTime);
        var blocks = CreateCorrectionPrefix(quake);
        blocks.Add(new DisplayBlock(
            string.Empty,
            $"{time}頃、地震・火山に関する情報が発表されました",
            string.Empty,
            DisplayStyleTokens.Summary));

        string name = quake.Earthquake.Hypocenter?.Name ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
        {
            blocks.Add(new DisplayBlock(
                string.Empty,
                $"対象：{name}",
                string.Empty,
                DisplayStyleTokens.Summary));
        }

        string tsunamiText = PageComposerSupport.GetDomesticTsunamiText(
            quake.Earthquake.DomesticTsunami);
        if (!string.IsNullOrWhiteSpace(tsunamiText))
        {
            blocks.Add(new DisplayBlock(
                string.Empty,
                tsunamiText,
                string.Empty,
                DisplayStyleTokens.Advisory));
        }

        if (!string.IsNullOrWhiteSpace(quake.FreeFormComment))
        {
            blocks.Add(new DisplayBlock(
                string.Empty,
                quake.FreeFormComment.Trim(),
                string.Empty,
                DisplayStyleTokens.Comment));
        }

        return [new PageDraft(blocks)];
    }

    private static IReadOnlyList<PageDraft> ComposeUnknown(QuakeEvent quake)
    {
        if (string.IsNullOrWhiteSpace(quake.FreeFormComment))
        {
            return [];
        }

        string rawType = string.IsNullOrWhiteSpace(quake.Issue.RawType)
            ? "Unknown"
            : quake.Issue.RawType;
        return
        [
            new PageDraft(
            [
                new DisplayBlock(
                    string.Empty,
                    $"地震・火山に関する情報（{rawType}）",
                    quake.FreeFormComment.Trim(),
                    DisplayStyleTokens.Comment),
            ]),
        ];
    }

    private static List<PageDraft> ComposeLongPeriodObservation(QuakeEvent quake)
    {
        LongPeriodIntensityInfo? observation = quake.LongPeriodIntensity;
        if (observation is null)
        {
            return
            [
                CreateSummaryPage(
                    quake,
                    "長周期地震動に関する観測情報が発表されました",
                    string.Empty),
            ];
        }

        string time = PageComposerSupport.FormatJapanTime(quake.Earthquake.OriginTime);
        var pages = new List<PageDraft>
        {
            CreateSummaryPage(
                quake,
                $"{time}頃　長周期地震動階級{observation.MaximumClass}を観測した地域があります",
                string.Empty),
        };

        LongPeriodIntensityArea[] areas = observation.Areas
            .OrderByDescending(static area => area.Class)
            .ThenBy(static area => area.Prefecture, StringComparer.Ordinal)
            .ThenBy(static area => area.Area, StringComparer.Ordinal)
            .ToArray();
        foreach (IGrouping<int, LongPeriodIntensityArea> group in
                 areas.GroupBy(static area => area.Class))
        {
            string[] names = group
                .Select(static area => area.Area)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            for (int offset = 0; offset < names.Length; offset += NamesPerRow * IntensityRowsPerPage)
            {
                string[] pageNames = names
                    .Skip(offset)
                    .Take(NamesPerRow * IntensityRowsPerPage)
                    .ToArray();
                var blocks = new List<DisplayBlock>();
                for (int rowOffset = 0; rowOffset < pageNames.Length; rowOffset += NamesPerRow)
                {
                    blocks.Add(new DisplayBlock(
                        rowOffset == 0 ? $"長周期階級{group.Key}" : string.Empty,
                        string.Join('　', pageNames.Skip(rowOffset).Take(NamesPerRow)),
                        string.Empty,
                        DisplayStyleTokens.Intensity));
                }

                pages.Add(new PageDraft(blocks));
            }
        }

        return pages;
    }

    private static List<PageDraft> ComposeSubsequentEarthquakeAdvisory(
        QuakeEvent quake)
    {
        const string title = "北海道・三陸沖後発地震注意情報";
        var pages = new List<PageDraft>
        {
            CreateNarrativePage(DisplayStyleTokens.Summary, title),
        };

        string headline = NormalizeSubsequentEarthquakeAdvisoryText(quake.Headline);
        string[] sentences = headline.Split(
            '。',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sentences.Length >= 3)
        {
            string occurrence = sentences[0];
            string probability = sentences[1];
            int regionEnd = probability.IndexOf(
                "巨大地震の想定震源域では",
                StringComparison.Ordinal);
            int comparisonStart = probability.IndexOf(
                "平常時と比べて",
                StringComparison.Ordinal);
            if (regionEnd > 0 && comparisonStart > regionEnd)
            {
                pages.Add(CreateNarrativePage(
                    DisplayStyleTokens.Advisory,
                    occurrence,
                    probability[..regionEnd]));
                pages.Add(CreateNarrativePage(
                    DisplayStyleTokens.Advisory,
                    probability[regionEnd..comparisonStart],
                    probability[comparisonStart..] + "。"));
                pages.Add(CreateNarrativePage(
                    DisplayStyleTokens.Advisory,
                    sentences[2]));
                return pages;
            }
        }

        AddNarrativePages(pages, headline, DisplayStyleTokens.Advisory);
        return pages;
    }

    private static List<PageDraft> ComposeNankaiTroughTemporaryInformation(
        QuakeEvent quake)
    {
        const string defaultTitle = "南海トラフ地震臨時情報";
        string[] lines = quake.Headline
            .Replace('\r', '\n')
            .Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool hasSpecificTitle = lines.FirstOrDefault()?.StartsWith(
            defaultTitle,
            StringComparison.Ordinal) == true;
        string title = hasSpecificTitle ? lines[0] : defaultTitle;
        string narrative = string.Join('\n', lines.Skip(hasSpecificTitle ? 1 : 0));
        var pages = new List<PageDraft>
        {
            CreateNarrativePage(DisplayStyleTokens.Summary, title),
        };
        AddNarrativePages(pages, narrative, DisplayStyleTokens.Advisory);
        return pages;
    }

    private static PageDraft CreateNarrativePage(string style, params string[] lines) =>
        new(
        [
            new DisplayBlock(
                string.Empty,
                string.Join('\n', lines.Where(static line => !string.IsNullOrWhiteSpace(line))),
                string.Empty,
                style),
        ]);

    private static string NormalizeSubsequentEarthquakeAdvisoryText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character is >= '０' and <= '９')
            {
                builder.Append((char)('0' + character - '０'));
            }
            else if (character == '．')
            {
                builder.Append('.');
            }
            else if (!char.IsWhiteSpace(character) && character != '　')
            {
                builder.Append(character);
            }
        }

        return builder.ToString()
            .Replace("（Ｍｗ）", string.Empty, StringComparison.Ordinal)
            .Replace("（Mw）", string.Empty, StringComparison.Ordinal)
            .Replace("(Mw)", string.Empty, StringComparison.Ordinal);
    }

    private static PageDraft CreateSummaryPage(
        QuakeEvent quake,
        string primary,
        string secondary)
    {
        List<DisplayBlock> blocks = CreateCorrectionPrefix(quake);
        blocks.Add(new DisplayBlock(
            string.Empty,
            primary,
            secondary,
            DisplayStyleTokens.Summary));
        return new PageDraft(blocks);
    }

    private static List<DisplayBlock> CreateCorrectionPrefix(QuakeEvent quake)
    {
        var blocks = new List<DisplayBlock>();
        if (quake.IsCorrection)
        {
            blocks.Add(PageComposerSupport.CreateCorrectionBlock(quake.Issue.Correction));
        }

        return blocks;
    }

    private static (string Primary, string Secondary) BuildEarthquakeSummary(
        EarthquakeInfo earthquake)
    {
        string time = PageComposerSupport.FormatJapanTime(earthquake.OriginTime);
        string name = string.IsNullOrWhiteSpace(earthquake.Hypocenter?.Name)
            ? "震源不明"
            : earthquake.Hypocenter.Name;
        var firstLine = new List<string> { $"{time}頃", $"{name}で地震" };
        if (earthquake.Hypocenter?.DepthKilometers is 0)
        {
            firstLine.Add("震源はごく浅い");
        }
        else if (earthquake.Hypocenter?.DepthKilometers is > 0 and int depth)
        {
            firstLine.Add($"震源の深さは{depth}km");
        }

        string magnitude = MagnitudeFormatter.Format(earthquake.Hypocenter?.Magnitude);
        string secondLine = magnitude == "-"
            ? earthquake.Hypocenter?.MagnitudeDescription.Trim() ?? string.Empty
            : $"マグニチュードは{magnitude}と推定されます";
        return (string.Join('　', firstLine), secondLine);
    }

    private static string AppendMagnitudeDescription(
        string firstLine,
        HypocenterInfo? hypocenter)
    {
        string description = hypocenter?.MagnitudeDescription.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(description)
            ? firstLine
            : $"{firstLine}\n{description}";
    }

    private static List<IntensityRow> BuildIntensityRows(
        IReadOnlyList<QuakePoint> points)
    {
        var byPlace = new Dictionary<string, IndexedPoint>(StringComparer.Ordinal);
        for (int index = 0; index < points.Count; index++)
        {
            QuakePoint point = points[index];
            if (point.Scale == JmaScale.Unknown)
            {
                continue;
            }

            string displayName = string.IsNullOrWhiteSpace(point.DisplayName)
                ? PlaceNormalizer.BuildDisplayName(point.Prefecture, point.Address, point.IsArea)
                : point.DisplayName;
            if (!byPlace.TryGetValue(displayName, out IndexedPoint? current))
            {
                byPlace.Add(displayName, new IndexedPoint(point.Scale, displayName, index));
            }
            else if ((int)point.Scale > (int)current.Scale)
            {
                byPlace[displayName] = current with { Scale = point.Scale };
            }
        }

        IndexedPoint[] candidates = byPlace.Values
            .Where(static point => point.Scale >= JmaScale.Three)
            .ToArray();
        if (candidates.Length == 0 && byPlace.Count > 0)
        {
            JmaScale maximum = byPlace.Values.Max(static point => point.Scale);
            candidates = byPlace.Values.Where(point => point.Scale == maximum).ToArray();
        }

        var rows = new List<IntensityRow>();
        foreach (IGrouping<JmaScale, IndexedPoint> group in candidates
                     .OrderByDescending(static point => point.Scale)
                     .ThenBy(static point => point.OriginalIndex)
                     .GroupBy(static point => point.Scale))
        {
            string[] names = group
                .OrderBy(static point => point.OriginalIndex)
                .Select(static point => point.DisplayName)
                .ToArray();
            for (int offset = 0; offset < names.Length; offset += NamesPerRow)
            {
                rows.Add(new IntensityRow(group.Key, names.Skip(offset).Take(NamesPerRow).ToArray()));
            }
        }

        return rows;
    }

    private static void AddIntensityPages(
        List<PageDraft> pages,
        IReadOnlyList<IntensityRow> rows,
        string emptyMessage)
    {
        if (rows.Count == 0)
        {
            pages.Add(new PageDraft(
            [
                new DisplayBlock(
                    string.Empty,
                    emptyMessage,
                    string.Empty,
                    DisplayStyleTokens.Empty),
            ]));
            return;
        }

        for (int offset = 0; offset < rows.Count; offset += IntensityRowsPerPage)
        {
            IntensityRow[] pageRows = rows.Skip(offset).Take(IntensityRowsPerPage).ToArray();
            var blocks = new DisplayBlock[pageRows.Length];
            for (int index = 0; index < pageRows.Length; index++)
            {
                IntensityRow row = pageRows[index];
                bool showBadge = index == 0 || row.Scale != pageRows[index - 1].Scale;
                blocks[index] = new DisplayBlock(
                    showBadge ? "震度" + ScaleFormatter.Format(row.Scale) : string.Empty,
                    string.Join('　', row.Names),
                    string.Empty,
                    DisplayStyleTokens.Intensity);
            }

            pages.Add(new PageDraft(blocks));
        }
    }

    private static void AddAdvisoryPage(List<PageDraft> pages, string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            AddNarrativePages(pages, text, DisplayStyleTokens.Advisory);
        }
    }

    private static void AddCommentPage(List<PageDraft> pages, string comment)
    {
        if (!string.IsNullOrWhiteSpace(comment))
        {
            string displayComment = FormatCommentForDisplay(comment);
            AddNarrativePages(pages, displayComment, DisplayStyleTokens.Comment);
        }
    }

    private static void AddNarrativePages(
        List<PageDraft> pages,
        string text,
        string style)
    {
        foreach (IReadOnlyList<string> pageLines in
                 NarrativeTextPaginator.Paginate([text]))
        {
            DisplayBlock[] blocks = pageLines
                .Select(static line => line.Trim())
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(line => new DisplayBlock(
                    string.Empty,
                    line,
                    string.Empty,
                    style))
                .ToArray();
            if (blocks.Length > 0)
            {
                pages.Add(new PageDraft(blocks));
            }
        }
    }

    private static string FormatCommentForDisplay(string comment) => comment
        .Trim()
        .Replace(
            "この地震で緊急地震速報を発表しましたが、強い揺れは観測されませんでした。",
            "この地震で緊急地震速報を発表しましたが\n強い揺れは観測されませんでした",
            StringComparison.Ordinal);

    private static bool IsEewNoStrongShakingComment(string comment) =>
        comment.Contains("緊急地震速報を発表しましたが", StringComparison.Ordinal) &&
        comment.Contains("強い揺れは観測されませんでした", StringComparison.Ordinal);

    private sealed record IndexedPoint(JmaScale Scale, string DisplayName, int OriginalIndex);

    private sealed record IntensityRow(JmaScale Scale, IReadOnlyList<string> Names);
}
