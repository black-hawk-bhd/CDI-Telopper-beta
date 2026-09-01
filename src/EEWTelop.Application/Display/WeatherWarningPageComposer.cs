using System.Globalization;
using System.Text.RegularExpressions;
using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

internal static class WeatherWarningPageComposer
{
    private const int ItemsPerPage = 3;
    private const int ActiveWarningRowsPerPage = 2;
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
                "取消",
                PageComposerSupport.GetCancellationText(
                    PageComposerSupport.GetWeatherCancellationSubject(weather)),
                string.Empty,
                DisplayStyleTokens.WeatherCancel);
            return PageComposerSupport.CreateProgram(
                weather,
                settings,
                OverlayPriority.WeatherAdvisory,
                EndPolicy.AutoHide,
                [new PageDraft([telegramCancel])]);
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
                "解除",
                string.IsNullOrWhiteSpace(weather.Headline)
                    ? "気象警報・注意報は解除されました"
                    : weather.Headline,
                string.Empty,
                DisplayStyleTokens.WeatherCancel);
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

        foreach (IGrouping<ActiveWarningKey, WeatherWarningItem> warningGroup in warningGroups)
        {
            ActiveWeatherWarningRow[] rows = warningGroup
                .GroupBy(static item => new ActiveAreaStatusKey(
                    GetPrefectureName(item),
                    item.Status.Trim()))
                .OrderBy(static group => GetStatusDisplayOrder(group.Key.Status))
                .ThenBy(static group => group.Key.PrefectureName, StringComparer.Ordinal)
                .SelectMany(static group => CreateGroupedAreaRows(group))
                .ToArray();

            // 市区町村が多い注警報は、1ページへ詰め込み過ぎると
            // OBS側の折り返しで読みにくくなる。地域一覧は2行ずつ送り、
            // 残りは次ページへ送る。
            for (int offset = 0; offset < rows.Length; offset += ActiveWarningRowsPerPage)
            {
                DisplayBlock[] blocks = rows
                    .Skip(offset)
                    .Take(ActiveWarningRowsPerPage)
                    .Select((row, index) => new DisplayBlock(
                        index == 0 ? warningGroup.Key.KindName : string.Empty,
                        row.PrimaryText,
                        string.Empty,
                        warningGroup.Key.StyleToken))
                    .ToArray();
                if (blocks.Length > 0)
                {
                    yield return new PageDraft(blocks);
                }
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
                JoinWeatherRowParts(
                    group.Key.PrefectureName,
                    FormatStatus(group.Key.Status)));
            yield break;
        }

        for (int offset = 0; offset < areaNames.Length; offset += AreasPerWarningRow)
        {
            string areas = string.Join(
                "　",
                areaNames.Skip(offset).Take(AreasPerWarningRow));
            yield return new ActiveWeatherWarningRow(
                JoinWeatherRowParts(
                    group.Key.PrefectureName,
                    areas,
                    FormatStatus(group.Key.Status)));
        }
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
            string[] lines = WrapHeadlineSentence(sentence);
            string badge = sentence.Contains("最大級の警戒", StringComparison.Ordinal)
                ? "最大級の警戒"
                : defaultBadge;
            for (int offset = 0; offset < lines.Length; offset += HeadlineLinesPerPage)
            {
                string primaryText = string.Join(
                    '\n',
                    lines.Skip(offset).Take(HeadlineLinesPerPage));
                yield return new PageDraft(
                [
                    new DisplayBlock(badge, primaryText, string.Empty, style),
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

        string badge = badgeOverride ?? weather.Items
            .Select(static item => item.KindName)
            .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name)) ??
            "記録的短時間大雨情報";
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
        pages.AddRange(CreateTextPages(badge, rainfallLines, style));

        string[] warningLines = sentences
            .Where((sentence, index) =>
                index != occurrenceIndex && !IsRecordRainfallLine(sentence))
            .ToArray();
        // 「猛烈な雨が…」などの警戒文は、発生地域や雨量のページへ
        // 詰め込まず、必ず次ページ以降へ1文ずつ送る。
        pages.AddRange(warningLines.SelectMany(line =>
            CreateTextPages(badge, [line], style)));
        return pages.ToArray();
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
        // 気象防災速報は一文が長く、複数文を同じページへ詰めると
        // WPF/OBS側の折り返し後に4行以上になる。1ページ1要点にして、
        // 線状降水帯情報などの長文も安全な行数で順に表示する。
        return lines
            .SelectMany(line => CreateTextPages(
                badge,
                [line],
                DisplayStyleTokens.WeatherWarning))
            .ToArray();
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

        string badge = weather.Items
            .Select(static item => item.KindName)
            .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name)) ??
            "竜巻注意情報";
        const string style = DisplayStyleTokens.WeatherAdvisory;
        // 竜巻注意情報は一文が長く、安全行動を含むため、通常の注警報と同じ
        // 3項目詰め込みにはしない。1ページ1要点にして自然な折り返しに任せる。
        var pages = headlineLines
            .SelectMany(line => CreateTextPages(badge, [line], style))
            .ToList();
        if (weather.ValidUntil is DateTimeOffset validUntil)
        {
            pages.Add(CreateTextPage(
                badge,
                [$"この情報は{FormatWeatherValidTime(validUntil)}まで有効です"],
                style));
        }

        return pages.ToArray();
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
        string style)
    {
        foreach (IReadOnlyList<string> pageLines in NarrativeTextPaginator.Paginate(lines))
        {
            yield return CreateTextPage(
                badge,
                pageLines.ToArray(),
                style);
        }
    }

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
        for (int offset = 0; offset < releasedWarnings.Length; offset += ItemsPerPage)
        {
            DisplayBlock[] blocks = releasedWarnings
                .Skip(offset)
                .Take(ItemsPerPage)
                .Select(static item => new DisplayBlock(
                    "解除",
                    $"{FormatReleaseArea(item)}の{item.KindName}は解除されました",
                    string.Empty,
                    DisplayStyleTokens.WeatherCancel))
                .ToArray();
            yield return new PageDraft(blocks);
        }
    }

    private static string FormatReleaseArea(WeatherWarningItem item)
    {
        string areaName = item.AreaName.Trim();
        if (item.AreaCode.Length < 2)
        {
            return areaName;
        }

        WeatherPrefectureOption? prefecture =
            WeatherPrefectureCatalog.Find(item.AreaCode[..2]);
        return prefecture is null ||
            areaName.Contains(prefecture.Name, StringComparison.Ordinal)
                ? areaName
                : $"{prefecture.Name}{areaName}";
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
}
