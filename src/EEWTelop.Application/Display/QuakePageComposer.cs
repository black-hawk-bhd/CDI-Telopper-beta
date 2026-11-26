using System.Text;
using System.Text.RegularExpressions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Formatting;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

internal static partial class QuakePageComposer
{
    private const int IntensityRowsPerPage = 2;
    private const int NamesPerRow = 3;
    private const int OverseasTsunamiRowsPerPage = 2;
    private const string ScalePromptTsunamiCaution = "念のため津波に注意してください";

    public static DisplayProgram Compose(QuakeEvent quake, DisplaySettings settings, bool omitScalePromptTsunamiCaution)
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

        bool hideNonJmaStations = settings.HideNonJmaIntensityStations &&
            quake.IssueType is QuakeIssueType.DetailScale or QuakeIssueType.ScaleAndDestination;
        string displayComment = hideNonJmaStations || string.Equals(quake.Provider, "wolfx", StringComparison.OrdinalIgnoreCase)
            ? NonJmaStationNote().Replace(quake.FreeFormComment, string.Empty)
            : quake.FreeFormComment;
        IReadOnlyList<IntensityRow> intensityRows = BuildIntensityRows(
            quake.Points, quake.Earthquake.MaximumScale, settings.ShowLowIntensityPointsInStrongQuakes, hideNonJmaStations);
        IReadOnlyList<PageDraft> pages = quake.IssueType switch
        {
            QuakeIssueType.ScalePrompt => ComposeScalePrompt(quake, intensityRows, displayComment, settings, omitScalePromptTsunamiCaution),
            QuakeIssueType.DetailScale or QuakeIssueType.ScaleAndDestination =>
                ComposeDetailed(quake, intensityRows, displayComment, includeIntensity: true, settings),
            QuakeIssueType.Destination =>
                ComposeDetailed(quake, intensityRows, displayComment, includeIntensity: false, settings),
            QuakeIssueType.Foreign => ComposeForeign(quake, displayComment),
            QuakeIssueType.Other => ComposeOther(quake, displayComment, settings),
            QuakeIssueType.LongPeriodObservation => ComposeLongPeriodObservation(quake),
            QuakeIssueType.NankaiTroughTemporaryInformation =>
                ComposeNankaiTroughInformation(quake, "南海トラフ地震臨時情報"),
            QuakeIssueType.NankaiTroughCommentary =>
                ComposeNankaiTroughInformation(quake, PageComposerSupport.GetQuakeCancellationSubject(quake)),
            QuakeIssueType.SubsequentEarthquakeAdvisory =>
                ComposeSubsequentEarthquakeAdvisory(quake),
            _ => ComposeUnknown(quake, displayComment),
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
        IReadOnlyList<IntensityRow> intensityRows,
        string displayComment,
        DisplaySettings settings,
        bool omitScalePromptTsunamiCaution)
    {
        string time = FormatOriginTime(quake.Earthquake);
        var pages = new List<PageDraft>
        {
            CreateSummaryPage(
                quake,
                $"{time}　震度３以上の地震がありました",
                string.Empty),
        };

        // 震度速報（VXSE51）には震源・津波判定がまだ含まれず、DMDATAの
        // JMA XMLではDomesticTsunamiがUnknownになる。同じ地震の津波注意報以上を
        // 確認済みで省略設定が有効な場合だけ、暫定の注意文を外す。
        string tsunamiText = quake.Earthquake.DomesticTsunami is
            DomesticTsunami.Checking or DomesticTsunami.Unknown
            ? (omitScalePromptTsunamiCaution ? string.Empty : ScalePromptTsunamiCaution + "。")
            : PageComposerSupport.GetDomesticTsunamiText(quake.Earthquake.DomesticTsunami);
        AddAdvisoryPage(pages, tsunamiText);
        AddIntensityPages(
            pages,
            intensityRows,
            "震度速報の対象地域情報はありません", settings.SeparateIntensityPagesByScale);
        AddCommentPage(pages, GetSupplementaryComment(displayComment, tsunamiText, settings, omitScalePromptTsunamiCaution));
        return pages;
    }

    private static List<PageDraft> ComposeDetailed(
        QuakeEvent quake,
        IReadOnlyList<IntensityRow> intensityRows,
        string displayComment,
        bool includeIntensity,
        DisplaySettings settings)
    {
        // 大規模噴火の遠地情報もVXSE53で届く。地震の定型ページを付けず、
        // 津波到達予想を含む実際の噴火情報を本文から表示する。
        if (IsLargeScaleEruptionInformation(quake))
        {
            var eruptionPages = new List<PageDraft>();
            AddCommentPage(eruptionPages, displayComment);
            if (quake.IsCorrection && eruptionPages.Count > 0)
            {
                eruptionPages[0] = new PageDraft(
                    CreateCorrectionPrefix(quake).Concat(eruptionPages[0].Blocks).ToArray());
            }

            return eruptionPages;
        }

        (string primary, string secondary) = BuildEarthquakeSummary(quake.Earthquake);
        var pages = new List<PageDraft>
        {
            CreateSummaryPage(quake, primary, secondary),
        };

        string tsunamiText = PageComposerSupport.GetDomesticTsunamiText(quake.Earthquake.DomesticTsunami);
        AddAdvisoryPage(pages, tsunamiText);
        if (includeIntensity &&
            (intensityRows.Count > 0 || !IsEewNoStrongShakingComment(quake.FreeFormComment)))
        {
            string emptyMessage = intensityRows.Count == 0 && settings.HideNonJmaIntensityStations &&
                quake.Points.Any(static point => point.IsNonJmaIntensityStation)
                ? "表示対象の震度観測点はありません" : "各地の詳しい震度情報はありません";
            AddIntensityPages(
                pages,
                intensityRows,
                emptyMessage, settings.SeparateIntensityPagesByScale);
        }

        AddCommentPage(pages, GetSupplementaryComment(displayComment, tsunamiText, settings));
        return pages;
    }

    private static bool IsLargeScaleEruptionInformation(QuakeEvent quake) =>
        quake.Issue.RawType == "VXSE53" &&
        quake.Earthquake.Hypocenter?.Magnitude is null &&
        quake.Earthquake.MaximumScale == JmaScale.Unknown &&
        quake.Points.Count == 0 &&
        (quake.FreeFormComment.Contains("大規模な噴火が発生しました", StringComparison.Ordinal) ||
         quake.FreeFormComment.Contains("大規模噴火が発生しました", StringComparison.Ordinal));

    private static List<PageDraft> ComposeForeign(QuakeEvent quake, string displayComment)
    {
        string time = FormatOriginTime(quake.Earthquake);
        string name = quake.Earthquake.Hypocenter?.Name ?? string.Empty;
        string firstLine = string.IsNullOrWhiteSpace(name)
            ? $"{time}、海外で"
            : $"{time}、{name}付近で";
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

        string tsunamiInformation = string.IsNullOrWhiteSpace(displayComment)
            ? PageComposerSupport.GetForeignTsunamiText(quake.Earthquake.ForeignTsunami)
            : displayComment.Trim();
        AddPagesWithOverseasTsunamiObservations(pages, tsunamiInformation, DisplayStyleTokens.Advisory);
        return pages;
    }

    private static IReadOnlyList<PageDraft> ComposeOther(
        QuakeEvent quake,
        string displayComment,
        DisplaySettings settings)
    {
        string time = FormatOriginTime(quake.Earthquake);
        var blocks = CreateCorrectionPrefix(quake);
        blocks.Add(new DisplayBlock(
            string.Empty,
            $"{time}、地震・火山に関する情報が発表されました",
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

        string comment = GetSupplementaryComment(displayComment, tsunamiText, settings);
        if (!string.IsNullOrWhiteSpace(comment))
        {
            blocks.Add(new DisplayBlock(
                string.Empty,
                comment.Trim(),
                string.Empty,
                DisplayStyleTokens.Comment));
        }

        return [new PageDraft(blocks)];
    }

    private static IReadOnlyList<PageDraft> ComposeUnknown(QuakeEvent quake, string displayComment)
    {
        if (string.IsNullOrWhiteSpace(displayComment))
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
                    displayComment.Trim(),
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

        string time = FormatOriginTime(quake.Earthquake);
        var pages = new List<PageDraft>
        {
            CreateSummaryPage(
                quake,
                $"{time}　長周期地震動階級{observation.MaximumClass}を観測した地域があります",
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

    private static List<PageDraft> ComposeNankaiTroughInformation(QuakeEvent quake, string defaultTitle)
    {
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
        string time = FormatOriginTime(earthquake);
        string name = string.IsNullOrWhiteSpace(earthquake.Hypocenter?.Name)
            ? "震源不明"
            : earthquake.Hypocenter.Name;
        var firstLine = new List<string> { time, $"{name}で地震" };
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

    private static string FormatOriginTime(EarthquakeInfo earthquake) => earthquake.OriginTimeIsKnown
        ? PageComposerSupport.FormatJapanTime(earthquake.OriginTime) + "頃"
        : "発生時刻不明";

    private static List<IntensityRow> BuildIntensityRows(
        IReadOnlyList<QuakePoint> points,
        JmaScale earthquakeMaximum,
        bool showLowIntensityPoints,
        bool hideNonJmaStations)
    {
        var byPlace = new Dictionary<(string DisplayName, bool IsUnreported), IndexedPoint>();
        for (int index = 0; index < points.Count; index++)
        {
            QuakePoint point = points[index];
            if (point.Scale == JmaScale.Unknown || hideNonJmaStations && point.IsNonJmaIntensityStation)
            {
                continue;
            }

            string displayName = string.IsNullOrWhiteSpace(point.DisplayName)
                ? PlaceNormalizer.BuildDisplayName(point.Prefecture, point.Address, point.IsArea)
                : point.DisplayName;
            displayName = FormatIntensityPlaceNameForDisplay(displayName);
            bool isUnreported = point.Scale == JmaScale.FiveLowerOrMore;
            var key = (displayName, isUnreported);
            if (!byPlace.TryGetValue(key, out IndexedPoint? current))
            {
                byPlace.Add(key, new IndexedPoint(point.Scale, displayName, index));
            }
            else if ((int)point.Scale > (int)current.Scale)
            {
                byPlace[key] = current with { Scale = point.Scale };
            }
        }

        bool includeAllPoints = showLowIntensityPoints &&
            (earthquakeMaximum >= JmaScale.Three || byPlace.Values.Any(point => point.Scale >= JmaScale.Three));
        IndexedPoint[] candidates = byPlace.Values
            .Where(point => includeAllPoints || point.Scale >= JmaScale.Three)
            .ToArray();
        if (candidates.Length == 0 && byPlace.Count > 0 && earthquakeMaximum < JmaScale.Three)
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

    private static string FormatIntensityPlaceNameForDisplay(string value)
    {
        string display = value.Trim();
        return display.EndsWith('＊')
            ? display[..^1].TrimEnd()
            : display;
    }

    private static void AddIntensityPages(
        List<PageDraft> pages,
        IReadOnlyList<IntensityRow> rows,
        string emptyMessage,
        bool separateByScale)
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

        for (int offset = 0; offset < rows.Count;)
        {
            IntensityRow[] pageRows = rows.Skip(offset).Take(IntensityRowsPerPage)
                .TakeWhile(row => !separateByScale || row.Scale == rows[offset].Scale).ToArray();
            offset += pageRows.Length;
            var blocks = new DisplayBlock[pageRows.Length];
            for (int index = 0; index < pageRows.Length; index++)
            {
                IntensityRow row = pageRows[index];
                bool showBadge = index == 0 || row.Scale != pageRows[index - 1].Scale;
                blocks[index] = new DisplayBlock(
                    showBadge ? FormatIntensityBadge(row.Scale) : string.Empty,
                    string.Join('　', row.Names),
                    string.Empty,
                    DisplayStyleTokens.Intensity);
            }

            pages.Add(new PageDraft(blocks));
        }
    }

    private static string FormatIntensityBadge(JmaScale scale) =>
        scale == JmaScale.FiveLowerOrMore
            ? "震度5弱以上 未入電"
            : "震度" + ScaleFormatter.Format(scale);

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
            AddPagesWithOverseasTsunamiObservations(pages, displayComment, DisplayStyleTokens.Comment);
        }
    }

    private static void AddPagesWithOverseasTsunamiObservations(List<PageDraft> pages, string text, string style)
    {
        if (!text.Contains("検潮所名", StringComparison.Ordinal))
        {
            AddNarrativePages(pages, text, style);
            return;
        }

        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        int narrativeStart = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            if (!IsOverseasTsunamiHeader(SplitOverseasTsunamiColumns(lines[index])) ||
                !TryReadOverseasTsunamiTable(lines, index + 1, out int end, out var observations))
            {
                continue;
            }

            AddNarrativePages(pages, string.Join('\n', lines[narrativeStart..index]), style);
            var heading = new DisplayBlock(string.Empty, "海外の津波観測", string.Empty, style);
            foreach (DisplayBlock[] rows in observations.Chunk(OverseasTsunamiRowsPerPage))
            {
                pages.Add(new PageDraft([heading, .. rows]));
            }

            narrativeStart = end;
            index = end - 1;
        }

        AddNarrativePages(pages, string.Join('\n', lines[narrativeStart..]), style);
    }

    private static bool TryReadOverseasTsunamiTable(
        string[] lines, int start, out int end, out List<DisplayBlock> observations)
    {
        observations = [];
        for (end = start; end < lines.Length; end++)
        {
            string[] columns = SplitOverseasTsunamiColumns(lines[end]);
            if (columns.Length == 1 || IsOverseasTsunamiHeader(columns))
            {
                break;
            }

            // Keep the source table in the narrative if a row uses an unfamiliar format.
            if (columns is not [var country, var station, var sourceHeight])
            {
                return false;
            }

            Match height = OverseasTsunamiHeight().Match(sourceHeight);
            if (!height.Success)
            {
                return false;
            }

            string number = height.Groups[1].Value.Normalize(NormalizationForm.FormKC);
            string unit = height.Groups[2].Value.Normalize(NormalizationForm.FormKC);
            observations.Add(new DisplayBlock(
                string.Empty,
                $"{country}・{station}",
                $"{number} {unit}",
                DisplayStyleTokens.OverseasTsunamiObservation));
        }

        return observations.Count > 0;
    }

    private static string[] SplitOverseasTsunamiColumns(string line) => OverseasTsunamiColumnGap().Split(line.Trim());

    private static bool IsOverseasTsunamiHeader(string[] columns) =>
        columns is ["国・地域名", "検潮所名", "津波の高さ"];

    private static string GetSupplementaryComment(
        string comment, string tsunamiText, DisplaySettings settings, bool omitScalePromptTsunamiCaution = false)
    {
        if (string.IsNullOrWhiteSpace(comment)) return string.Empty;

        var replacements = SubtitlePhraseCatalog.GetReplacements(settings);
        string advisory = SubtitlePhraseCatalog.ReplaceKnownPhrases(tsunamiText, replacements);
        var shownSentences = new HashSet<string>(advisory.Split(['。', '\r', '\n'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        // Compare complete rendered sentences and retain the first occurrence's wording.
        ReadOnlySpan<char> source = comment.AsSpan();
        var remaining = new StringBuilder(source.Length);
        bool inOverseasTsunamiTable = false;
        while (!source.IsEmpty)
        {
            int separator = source.IndexOfAny('。', '\r', '\n');
            int sentenceLength = separator < 0 ? source.Length : separator;
            int next = Math.Min(sentenceLength + 1, source.Length);
            if (separator >= 0 && source[separator] == '\r' && next < source.Length && source[next] == '\n')
            {
                next++;
            }

            if (omitScalePromptTsunamiCaution && source[..sentenceLength].Trim().SequenceEqual(ScalePromptTsunamiCaution))
            {
                source = source[next..];
                continue;
            }

            string sourceSentence = source[..next].Trim().ToString();
            if (inOverseasTsunamiTable || sourceSentence.StartsWith("国・地域名", StringComparison.Ordinal))
            {
                string[] columns = SplitOverseasTsunamiColumns(sourceSentence);
                inOverseasTsunamiTable = IsOverseasTsunamiHeader(columns) || (inOverseasTsunamiTable && columns.Length > 1);
            }

            // Repeated headers and rows can belong to different observation sources.
            string rendered = SubtitlePhraseCatalog.ReplaceKnownPhrases(sourceSentence, replacements);
            string key = rendered.TrimEnd().TrimEnd('。').Trim();
            if (inOverseasTsunamiTable || key.Length == 0 || shownSentences.Add(key))
                remaining.Append(source[..next]);
            source = source[next..];
        }
        return remaining.ToString().Trim();
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

    [GeneratedRegex(
        @"(?:\A|(?<=[。\r\n]))[ \t　]*(?:なお、[ \t　]*)?[（(]?[ \t　]*(?:「＊」|＊)印?は[ \t　]*" +
        @"気象庁以外の(?:震度)?観測点(?:についての情報)?です(?:。?[）)]。?|。|(?=[ \t　]*(?:[\r\n]|\z)))",
        RegexOptions.CultureInvariant)]
    private static partial Regex NonJmaStationNote();

    [GeneratedRegex(@"[ \t　]{2,}|[\t　]", RegexOptions.CultureInvariant)]
    private static partial Regex OverseasTsunamiColumnGap();

    [GeneratedRegex(@"\A([0-9０-９]+(?:[.．][0-9０-９]+)?) *((?:[cｃ]?[mｍ]|㎝)(?:以上|以下|未満|程度)?)\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex OverseasTsunamiHeight();

    private sealed record IndexedPoint(JmaScale Scale, string DisplayName, int OriginalIndex);

    private sealed record IntensityRow(JmaScale Scale, IReadOnlyList<string> Names);
}
