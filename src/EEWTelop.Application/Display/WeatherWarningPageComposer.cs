using System.Globalization;
using System.Text.RegularExpressions;
using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

internal static partial class WeatherWarningPageComposer
{
    private const int ReleaseRowsPerPage = 3;
    private const int ReleaseAreasPerRow = 6;
    private const int ActiveWarningRowsPerPage = 3;
    private const int AreasPerWarningRow = 3;
    private const int HeadlineLinesPerPage = 2;
    private const int HeadlineCharactersPerLine = 24;

    private static readonly Regex HeadlineBadgePattern = new(
        "^【(?<badge>[^】]+)】\\s*",
        RegexOptions.CultureInvariant);

    private static readonly WeatherWarningLevel[] LevelOrder =
    [
        WeatherWarningLevel.SpecialWarning,
        WeatherWarningLevel.Warning,
        WeatherWarningLevel.Advisory,
        WeatherWarningLevel.Unknown,
    ];

    public static DisplayProgram Compose(WeatherWarningEvent weather, DisplaySettings settings)
    {
        if (PageComposerSupport.IsTelegramCancellation(weather.Issue))
        {
            var telegramCancel = new DisplayBlock(
                weather.RiverFlood is null ? GetWeatherKind(weather) : "取消",
                PageComposerSupport.GetCancellationText(
                    PageComposerSupport.GetWeatherCancellationSubject(weather)),
                string.Empty,
                DisplayStyleTokens.WeatherCancel)
            { WeatherHeading = weather.RiverFlood is null ? GetWeatherHeading(weather, explicitState: "取消") : string.Empty };
            return PageComposerSupport.CreateProgram(
                weather,
                settings,
                OverlayPriority.WeatherAdvisory,
                EndPolicy.AutoHide,
                [new PageDraft([telegramCancel])]);
        }

        if (weather.RiverFlood is not null)
        {
            return ComposeRiverFlood(weather, settings);
        }

        if (!weather.IsCancelled &&
            weather.InformationType == WeatherInformationType.RecordShortDurationHeavyRain)
        {
            PageDraft[] bulletinPages = CreateRecordShortDurationHeavyRainPages(weather);
            if (bulletinPages.Length > 0)
            {
                return PageComposerSupport.CreateProgram(
                    weather,
                    settings,
                    OverlayPriority.WeatherWarning,
                    EndPolicy.AutoHide,
                    bulletinPages);
            }
        }

        if (!weather.IsCancelled &&
            weather.InformationType == WeatherInformationType.DisasterPreventionBulletin)
        {
            PageDraft[] bulletinPages = CreateDisasterPreventionBulletinPages(weather);
            if (bulletinPages.Length > 0)
            {
                return PageComposerSupport.CreateProgram(
                    weather,
                    settings,
                    OverlayPriority.WeatherWarning,
                    EndPolicy.AutoHide,
                    bulletinPages);
            }
        }

        if (!weather.IsCancelled &&
            weather.InformationType == WeatherInformationType.TornadoAdvisory)
        {
            PageDraft[] bulletinPages = CreateTornadoAdvisoryPages(weather);
            if (bulletinPages.Length > 0)
            {
                return PageComposerSupport.CreateProgram(
                    weather,
                    settings,
                    OverlayPriority.WeatherAdvisory,
                    EndPolicy.AutoHide,
                    bulletinPages);
            }
        }

        WeatherWarningItem[] active = weather.Items
            .Where(static item => item.IsActive)
            .OrderBy(static item => GetStatusDisplayOrder(item.Status))
            .ThenBy(static item => GetStyleDisplayOrder(GetStyle(item)))
            .ThenBy(static item => GetLevelDisplayOrder(item.Level))
            .ThenBy(static item => item.AreaName, StringComparer.Ordinal)
            .ThenBy(static item => item.KindName, StringComparer.Ordinal)
            .ToArray();
        WeatherWarningItem[] releasedWarnings = weather.Items
            .Where(static item =>
                !item.IsActive &&
                item.Level is WeatherWarningLevel.SpecialWarning or
                    WeatherWarningLevel.Warning or
                    WeatherWarningLevel.Advisory)
            .OrderBy(static item => item.AreaName, StringComparer.Ordinal)
            .ThenBy(static item => item.KindName, StringComparer.Ordinal)
            .DistinctBy(static item => (item.AreaCode, item.AreaName, item.KindName))
            .ToArray();

        if (weather.IsCancelled || active.Length == 0)
        {
            if (releasedWarnings.Length > 0)
            {
                return PageComposerSupport.CreateProgram(
                    weather,
                    settings,
                    GetPriority(releasedWarnings.Max(static item => item.Level)),
                    EndPolicy.AutoHide,
                    CreateReleasePages(releasedWarnings).ToArray());
            }

            var cancel = new DisplayBlock(
                GetWeatherKind(weather),
                string.IsNullOrWhiteSpace(weather.Headline)
                    ? "気象警報・注意報は解除されました"
                    : weather.Headline,
                string.Empty,
                DisplayStyleTokens.WeatherCancel) { WeatherHeading = GetWeatherHeading(weather, explicitState: "解除") };
            return PageComposerSupport.CreateProgram(
                weather,
                settings,
                OverlayPriority.WeatherAdvisory,
                EndPolicy.AutoHide,
                [new PageDraft([cancel])]);
        }

        var pages = new List<PageDraft>();
        pages.AddRange(CreateWarningHeadlinePages(weather, active));
        pages.AddRange(CreateActiveWarningPages(active));

        pages.AddRange(CreateReleasePages(releasedWarnings));

        return PageComposerSupport.CreateProgram(
            weather,
            settings,
            GetPriority(weather.MaximumLevel),
            EndPolicy.AutoHide,
            pages);
    }

    private static IEnumerable<PageDraft> CreateActiveWarningPages(
        WeatherWarningItem[] active)
    {
        IOrderedEnumerable<IGrouping<ActiveWarningKey, WeatherWarningItem>> warningGroups =
            active
                .GroupBy(static item => new ActiveWarningKey(
                    item.KindName.Trim(),
                    item.Level,
                    GetStyle(item)))
                .OrderBy(static group => group.Min(item =>
                    GetStatusDisplayOrder(item.Status)))
                .ThenBy(static group => GetStyleDisplayOrder(group.Key.StyleToken))
                .ThenBy(static group => GetLevelDisplayOrder(group.Key.Level))
                .ThenBy(static group => group.Key.KindName, StringComparer.Ordinal);

        foreach (var warningGroup in warningGroups)
        {
            foreach (var group in warningGroup.GroupBy(item => new ActiveAreaStatusKey(
                         GetPrefectureName(item), item.Status.Trim()))
                         .OrderBy(group => GetStatusDisplayOrder(group.Key.Status))
                         .ThenBy(group => group.Key.PrefectureName, StringComparer.Ordinal))
            {
                string heading = JoinWeatherRowParts(group.Key.PrefectureName, "｜" + FormatStatus(group.Key.Status));
                string[] rows = CreateGroupedAreaRows(group).Select(row => row.PrimaryText).ToArray();
                foreach (var chunk in rows.Chunk(ActiveWarningRowsPerPage))
                    yield return CreateAreaListPage(warningGroup.Key.KindName, heading, chunk, warningGroup.Key.StyleToken);
            }
        }
    }

    private static IEnumerable<ActiveWeatherWarningRow> CreateGroupedAreaRows(
        IGrouping<ActiveAreaStatusKey, WeatherWarningItem> group)
    {
        string[] areaNames = group
            .Select(item => FormatGroupedAreaName(
                item.AreaName,
                group.Key.PrefectureName))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (areaNames.Length == 0)
        {
            yield return new ActiveWeatherWarningRow(
                string.Join("　", group.Select(item => item.AreaName.Trim()).Where(name => name.Length > 0).Distinct()));
            yield break;
        }

        foreach (string row in WeatherTextPagination.Group(areaNames, AreasPerWarningRow,
                     names => string.Join("　", names)))
            yield return new ActiveWeatherWarningRow(row);
    }

    private static string GetPrefectureName(WeatherWarningItem item)
    {
        string areaCode = item.AreaCode.Trim();
        return areaCode.Length >= 2
            ? WeatherPrefectureCatalog.Find(areaCode[..2])?.Name ?? string.Empty
            : string.Empty;
    }

    private static string FormatGroupedAreaName(
        string areaName,
        string prefectureName)
    {
        string normalized = areaName.Trim();
        if (string.IsNullOrWhiteSpace(prefectureName) ||
            !normalized.StartsWith(prefectureName, StringComparison.Ordinal))
        {
            return normalized;
        }

        return normalized[prefectureName.Length..].Trim();
    }

    private static string JoinWeatherRowParts(params string[] parts) => string.Join(
        "　",
        parts.Where(static part => !string.IsNullOrWhiteSpace(part)));

    private static IEnumerable<PageDraft> CreateWarningHeadlinePages(
        WeatherWarningEvent weather,
        WeatherWarningItem[] active)
    {
        string headline = weather.Headline.Trim();
        Match badgeMatch = HeadlineBadgePattern.Match(headline);
        if (!badgeMatch.Success)
        {
            // 「発表・更新しました」だけの管理的な見出しは、市区町村別ページと
            // 内容が重なるため追加表示しない。防災上の本文を伴うJMA見出しだけを扱う。
            yield break;
        }

        headline = headline[badgeMatch.Length..].Trim();
        if (string.IsNullOrWhiteSpace(headline))
        {
            yield break;
        }

        headline = AddPrefectureContext(headline, active);

        WeatherWarningItem strongest = active
            .OrderBy(static item => GetLevelDisplayOrder(item.Level))
            .First();
        string defaultBadge = ResolveHeadlineBadge(
            badgeMatch.Groups["badge"].Value,
            active,
            strongest.KindName);
        string style = GetStyle(strongest);

        string[] sentences = SplitBulletinSentences(headline)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (string sentence in sentences)
        {
            string[] lines = WeatherTextPagination.Split(sentence,
                active.SelectMany(i => new[] { i.AreaName, GetPrefectureName(i) }),
                78).ToArray();
            string badge = defaultBadge;
            for (int offset = 0; offset < lines.Length; offset++)
            {
                string primaryText = string.Join(
                    '\n',
                    lines.Skip(offset).Take(1));
                yield return new PageDraft(
                [
                    new DisplayBlock(
                        badge, primaryText, string.Empty, style)
                    { WeatherHeading = GetWeatherHeading(weather, includeStatus: true) },
                ]);
            }
        }
    }

    private static string AddPrefectureContext(
        string headline,
        IEnumerable<WeatherWarningItem> active)
    {
        string[] prefectures = active
            .Select(GetPrefectureName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (prefectures.Length == 0 ||
            prefectures.Any(name => headline.Contains(name, StringComparison.Ordinal)))
        {
            return headline;
        }

        return JoinWeatherRowParts(
            string.Join('・', prefectures),
            headline);
    }

    private static string ResolveHeadlineBadge(
        string xmlBadge,
        WeatherWarningItem[] active,
        string fallback)
    {
        string normalized = xmlBadge.Trim();
        int open = normalized.IndexOf('（');
        int close = normalized.LastIndexOf('）');
        if (open > 0 && close > open)
        {
            string levelName = normalized[..open].Trim();
            string phenomenon = normalized[(open + 1)..close].Trim();
            string expected = phenomenon + levelName;
            string? matchingKind = active
                .Select(static item => item.KindName.Trim())
                .FirstOrDefault(kind =>
                    kind.Contains(expected, StringComparison.Ordinal));
            return string.IsNullOrWhiteSpace(matchingKind) ? expected : matchingKind;
        }

        return string.IsNullOrWhiteSpace(normalized) ? fallback.Trim() : normalized;
    }

    private static string[] WrapHeadlineSentence(string sentence)
    {
        string remaining = string.Join(
            ' ',
            sentence.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var lines = new List<string>();
        while (remaining.Length > HeadlineCharactersPerLine)
        {
            int breakIndex = FindHeadlineBreakIndex(remaining);
            if (breakIndex <= 0)
            {
                breakIndex = HeadlineCharactersPerLine;
            }

            lines.Add(remaining[..breakIndex].Trim());
            remaining = remaining[breakIndex..].TrimStart(' ', '　', '、');
        }

        if (!string.IsNullOrWhiteSpace(remaining))
        {
            lines.Add(remaining.Trim());
        }

        return lines.Where(static line => !string.IsNullOrWhiteSpace(line)).ToArray();
    }

    private static int FindHeadlineBreakIndex(string text)
    {
        string[] preferredStarts =
        [
            "最大級の警戒",
            "レベル５", "レベル４", "レベル３", "レベル２",
            "レベル5", "レベル4", "レベル3", "レベル2",
        ];
        foreach (string phrase in preferredStarts)
        {
            int index = text.IndexOf(phrase, StringComparison.Ordinal);
            if (index is > 0 and <= HeadlineCharactersPerLine)
            {
                return index;
            }
        }

        int searchEnd = Math.Min(HeadlineCharactersPerLine, text.Length - 1);
        int punctuation = text.LastIndexOf('、', searchEnd);
        return punctuation > 0 ? punctuation + 1 : HeadlineCharactersPerLine;
    }

    private static PageDraft[] CreateRecordShortDurationHeavyRainPages(
        WeatherWarningEvent weather,
        string? badgeOverride = null)
    {
        string[] sentences = SplitRecordRainSentences(weather.Headline);
        if (sentences.Length == 0)
        {
            return [];
        }

        int occurrenceIndex = Array.FindIndex(sentences, static sentence =>
            sentence.Contains("記録的短時間大雨", StringComparison.Ordinal));
        if (occurrenceIndex < 0)
        {
            return [];
        }

        string badge = badgeOverride ?? "記録的短時間大雨情報";
        const string style = DisplayStyleTokens.WeatherWarning;
        var pages = new List<PageDraft>
        {
            CreateTextPage(
                badge,
                [FormatRecordRainOccurrence(sentences[occurrenceIndex])],
                style),
        };

        string[] rainfallLines = sentences
            .Where((sentence, index) =>
                index != occurrenceIndex && IsRecordRainfallLine(sentence))
            .ToArray();
        pages.AddRange(CreateTextPages(badge, rainfallLines, style, weather));

        string[] warningLines = sentences
            .Where((sentence, index) =>
                index != occurrenceIndex && !IsRecordRainfallLine(sentence))
            .ToArray();
        // 「猛烈な雨が…」などの警戒文は、発生地域や雨量のページへ
        // 詰め込まず、必ず次ページ以降へ1文ずつ送る。
        pages.AddRange(warningLines.SelectMany(line =>
            CreateTextPages(badge, [line], style, weather)));
        return WithWeatherHeading(pages, GetWeatherHeading(weather));
    }

    private static PageDraft[] CreateDisasterPreventionBulletinPages(
        WeatherWarningEvent weather)
    {
        string badge = string.Equals(
            weather.Issue.RawType,
            "VPBS51",
            StringComparison.Ordinal)
                ? "気象防災速報（潮位）"
                : "気象防災速報";
        if (weather.Headline.Contains("記録的短時間大雨", StringComparison.Ordinal))
        {
            PageDraft[] recordRainPages =
                CreateRecordShortDurationHeavyRainPages(weather, badge);
            if (recordRainPages.Length > 0)
            {
                return recordRainPages;
            }
        }

        string[] lines = SplitBulletinSentences(weather.Headline);
        string areaHeading = string.Empty;
        if (lines.Length > 0)
        {
            int boundary = lines[0].IndexOf("では、", StringComparison.Ordinal);
            if (boundary is > 0 and <= 80)
            {
                areaHeading = lines[0][..boundary];
                lines[0] = lines[0][(boundary + 3)..];
            }
        }
        // 気象防災速報は一文が長く、複数文を同じページへ詰めると
        // WPF/OBS側の折り返し後に4行以上になる。1ページ1要点にして、
        // 線状降水帯情報などの長文も安全な行数で順に表示する。
        var pages = lines
            .SelectMany(line => CreateTextPages(
                badge,
                [line],
                DisplayStyleTokens.WeatherWarning, weather))
            .ToArray();
        return WithWeatherHeading(pages, GetWeatherHeading(weather, areaOverride: areaHeading));
    }

    private static PageDraft[] CreateTornadoAdvisoryPages(WeatherWarningEvent weather)
    {
        string[] headlineLines = SplitBulletinSentences(weather.Headline)
            .Where(static sentence =>
                !sentence.StartsWith("この情報は", StringComparison.Ordinal) ||
                !sentence.Contains("有効", StringComparison.Ordinal))
            .ToArray();
        if (headlineLines.Length == 0 && weather.ValidUntil is null)
        {
            return [];
        }

        string badge = "竜巻注意情報";
        const string style = DisplayStyleTokens.WeatherAdvisory;
        // 竜巻注意情報は一文が長く、安全行動を含むため、通常の注警報と同じ
        // 3項目詰め込みにはしない。1ページ1要点にして自然な折り返しに任せる。
        var pages = headlineLines
            .SelectMany(line => CreateTextPages(badge, [line], style, weather))
            .ToList();
        if (weather.ValidUntil is DateTimeOffset validUntil)
        {
            pages.Add(CreateTextPage(
                badge,
                [$"この情報は{FormatWeatherValidTime(validUntil)}まで有効です"],
                style));
        }

        return WithWeatherHeading(pages, GetWeatherHeading(weather));
    }

    private static string[] SplitBulletinSentences(string headline) => headline
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Split(['。', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static sentence => !string.IsNullOrWhiteSpace(sentence))
        .ToArray();

    private static string[] SplitRecordRainSentences(string headline)
    {
        const string warningPhrase = "猛烈な雨が";
        int warningIndex = headline.IndexOf(warningPhrase, StringComparison.Ordinal);
        if (warningIndex > 0)
        {
            // 配信経路によってXML本文の句点や改行が失われても、警戒文を
            // 発生文と同じ表示ブロックへ結合させない。
            char preceding = headline[warningIndex - 1];
            if (preceding is not ('。' or '\n' or '\r'))
            {
                headline = headline.Insert(warningIndex, "\n");
            }
        }

        return SplitBulletinSentences(headline);
    }

    private static string FormatWeatherValidTime(DateTimeOffset value) => value
        .ToOffset(TimeSpan.FromHours(9))
        .ToString("d日 HH時mm分", CultureInfo.InvariantCulture);

    private static IEnumerable<PageDraft> CreateTextPages(
        string badge,
        string[] lines,
        string style,
        WeatherWarningEvent? weather = null)
    {
        string[] names = weather?.Items.SelectMany(i => new[] { i.AreaName, GetPrefectureName(i) })
            .Where(s => s.Length > 0).Distinct().ToArray() ?? [];
        var pending = new List<string>();
        int visualLines = 0;
        foreach (string fragment in lines.SelectMany(line => WeatherTextPagination.Split(line, names, 72)))
        {
            int height = NarrativeTextPaginator.EstimateVisualLineCount(fragment);
            if (pending.Count > 0 && visualLines + height > 3)
            {
                yield return CreateTextPage(badge, pending.ToArray(), style);
                pending.Clear();
                visualLines = 0;
            }
            pending.Add(fragment);
            visualLines += height;
        }
        if (pending.Count > 0)
            yield return CreateTextPage(badge, pending.ToArray(), style);
    }

    private static PageDraft[] WithWeatherHeading(IEnumerable<PageDraft> pages, string heading) => pages.Select(page => page with
    {
        Blocks = page.Blocks.Select((block, index) => index == 0 ? block with { WeatherHeading = heading } : block).ToArray(),
    }).ToArray();

    private static string GetWeatherHeading(WeatherWarningEvent weather, bool includeStatus = false, string? explicitState = null, string? areaOverride = null)
    {
        string area = string.Join("・", weather.Items.Select(i => GetPrefectureName(i) is { Length: > 0 } pref ? pref : i.AreaName.Trim())
            .Where(n => n.Length > 0).Distinct(StringComparer.Ordinal));
        if (!string.IsNullOrWhiteSpace(areaOverride)) area = areaOverride;
        // Bulletin item statuses may be synthesized by a provider; do not treat them as explicit state.
        string[] states = includeStatus ? weather.Items.Select(i => i.Status.Trim()).Where(s => s.Length > 0).Distinct().ToArray() : [];
        string state = explicitState ?? (states.Length == 1 ? FormatStatus(states[0]) :
            !includeStatus && weather.Issue.InformationType.Trim() is "発表" or "訂正" or "解除" or "取消"
                ? weather.Issue.InformationType.Trim() : string.Empty);
        return string.Join("｜", new[] { area, state }.Where(s => s.Length > 0));
    }

    private static string GetWeatherKind(WeatherWarningEvent weather) => weather.InformationType switch
    {
        WeatherInformationType.DisasterPreventionBulletin => weather.Issue.RawType == "VPBS51" ? "気象防災速報（潮位）" : "気象防災速報",
        WeatherInformationType.TornadoAdvisory => "竜巻注意情報",
        WeatherInformationType.RecordShortDurationHeavyRain => "記録的短時間大雨情報",
        _ => "気象警報・注意報",
    };

    private static PageDraft CreateTextPage(
        string badge,
        string[] lines,
        string style)
    {
        DisplayBlock[] blocks = lines
            .Select((line, index) => new DisplayBlock(
                index == 0 ? badge : string.Empty,
                line.Trim(),
                string.Empty,
                style))
            .ToArray();
        return new PageDraft(blocks);
    }

    private static bool IsRecordRainfallLine(string sentence) =>
        sentence.Contains("１時間に", StringComparison.Ordinal) ||
        sentence.Contains("1時間に", StringComparison.Ordinal);

    private static string FormatRecordRainOccurrence(string sentence)
    {
        int separatorIndex = sentence.IndexOf("分、", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return sentence.Trim();
        }

        return (sentence[..(separatorIndex + 1)] +
            "　" +
            sentence[(separatorIndex + 2)..]).Trim();
    }

    private static IEnumerable<PageDraft> CreateReleasePages(
        WeatherWarningItem[] releasedWarnings)
    {
        foreach (var group in releasedWarnings.GroupBy(item => new ReleasedWarningKey(
                     GetPrefectureName(item), item.KindName.Trim(), item.Level))
                     .OrderBy(group => GetLevelDisplayOrder(group.Key.Level))
                     .ThenBy(group => group.Key.KindName, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.PrefectureName, StringComparer.Ordinal))
        {
            string heading = JoinWeatherRowParts(group.Key.PrefectureName, "｜解除");
            string[] rows = CreateGroupedReleaseRows(group).Select(row => row.PrimaryText).ToArray();
            foreach (var chunk in rows.Chunk(ReleaseRowsPerPage))
                yield return CreateAreaListPage(group.Key.KindName, heading, chunk, DisplayStyleTokens.WeatherCancel);
        }
    }

    private static PageDraft CreateAreaListPage(string kind, string context, string[] rows, string style) =>
        CreateTextPage(kind, rows.Select((row, index) => index == 0 ? context + "\n" + row : row).ToArray(), style);

    private static IEnumerable<ReleasedWarningRow> CreateGroupedReleaseRows(
        IGrouping<ReleasedWarningKey, WeatherWarningItem> group)
    {
        string[] areaNames = group
            .Select(item => FormatGroupedAreaName(
                item.AreaName,
                group.Key.PrefectureName))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (areaNames.Length == 0)
        {
            yield return new ReleasedWarningRow(
                string.Join("　", group.Select(item => item.AreaName.Trim()).Where(name => name.Length > 0).Distinct()));
            yield break;
        }

        foreach (string row in WeatherTextPagination.Group(areaNames, ReleaseAreasPerRow,
                     names => string.Join("　", names)))
            yield return new ReleasedWarningRow(row);
    }

    private static string FormatStatus(string status) => status switch
    {
        "発表" => "新たに発表",
        "継続" => "継続中",
        "解除" => "解除",
        _ => status,
    };

    private static int GetStatusDisplayOrder(string status)
    {
        string normalized = status.Trim();
        if (normalized.Contains("発表", StringComparison.Ordinal))
        {
            return 0;
        }

        if (normalized.Contains("更新", StringComparison.Ordinal))
        {
            return 1;
        }

        return normalized.Contains("継続", StringComparison.Ordinal) ? 3 : 2;
    }

    private static int GetLevelDisplayOrder(WeatherWarningLevel level)
    {
        int index = Array.IndexOf(LevelOrder, level);
        return index >= 0 ? index : LevelOrder.Length;
    }

    private static string GetStyle(WeatherWarningItem item)
    {
        if (item.Level == WeatherWarningLevel.SpecialWarning)
        {
            return DisplayStyleTokens.WeatherSpecialWarning;
        }

        if (item.Level == WeatherWarningLevel.Warning)
        {
            return IsLevelFourWarning(item.KindName)
                ? DisplayStyleTokens.WeatherDangerWarning
                : DisplayStyleTokens.WeatherWarning;
        }

        return item.Level == WeatherWarningLevel.Unknown
            ? DisplayStyleTokens.WeatherWarning
            : DisplayStyleTokens.WeatherAdvisory;
    }

    private static bool IsLevelFourWarning(string kindName) =>
        kindName.Contains("レベル４", StringComparison.Ordinal) ||
        kindName.Contains("レベル4", StringComparison.Ordinal) ||
        kindName.Contains("危険警報", StringComparison.Ordinal);

    private static int GetStyleDisplayOrder(string styleToken) => styleToken switch
    {
        DisplayStyleTokens.WeatherSpecialWarning => 0,
        DisplayStyleTokens.WeatherDangerWarning => 1,
        DisplayStyleTokens.WeatherWarning => 2,
        DisplayStyleTokens.WeatherAdvisory => 3,
        _ => 4,
    };

    private static OverlayPriority GetPriority(WeatherWarningLevel level) => level switch
    {
        WeatherWarningLevel.SpecialWarning => OverlayPriority.WeatherSpecialWarning,
        WeatherWarningLevel.Warning => OverlayPriority.WeatherWarning,
        WeatherWarningLevel.Unknown => OverlayPriority.WeatherWarning,
        _ => OverlayPriority.WeatherAdvisory,
    };

    private sealed record ActiveWarningKey(
        string KindName,
        WeatherWarningLevel Level,
        string StyleToken);

    private sealed record ActiveAreaStatusKey(
        string PrefectureName,
        string Status);

    private sealed record ActiveWeatherWarningRow(string PrimaryText);

    private sealed record ReleasedWarningKey(
        string PrefectureName,
        string KindName,
        WeatherWarningLevel Level);

    private sealed record ReleasedWarningRow(string PrimaryText);
}
