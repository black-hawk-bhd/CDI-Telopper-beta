using System.Text.RegularExpressions;
using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

internal static partial class VolcanoPageComposer
{
    private const int LinesPerPage = 3;

    public static DisplayProgram Compose(VolcanoEvent volcano, DisplaySettings settings)
    {
        string badge = GetBadge(volcano);
        string style = volcano.InformationType == VolcanoInformationType.EruptionFlash
            ? DisplayStyleTokens.EruptionFlash
            : volcano.IsWarning
                ? DisplayStyleTokens.VolcanoWarning
                : DisplayStyleTokens.VolcanoForecast;
        OverlayPriority priority = volcano.InformationType == VolcanoInformationType.EruptionFlash
            ? OverlayPriority.EruptionFlash
            : style == DisplayStyleTokens.VolcanoWarning
                ? OverlayPriority.VolcanoWarning
                : OverlayPriority.WeatherAdvisory;

        var pages = new List<PageDraft>();
        if (volcano.IsTelegramCancellation ||
            PageComposerSupport.IsTelegramCancellation(volcano.Issue))
        {
            AddPrimaryPage(
                pages,
                volcano,
                GetBadge(volcano),
                PageComposerSupport.GetCancellationText(
                    PageComposerSupport.GetVolcanoCancellationSubject(volcano)),
                style);
            return PageComposerSupport.CreateProgram(
                volcano,
                settings,
                priority,
                EndPolicy.AutoHide,
                pages);
        }

        if (TryComposeAlertLevelChange(volcano, badge, style, pages))
        {
            return PageComposerSupport.CreateProgram(
                volcano,
                settings,
                priority,
                EndPolicy.AutoHide,
                pages);
        }

        if (volcano.InformationType == VolcanoInformationType.EruptionFlash)
        {
            string headline = TrimHeadlineDecoration(
                SplitSentences(volcano.Headline).FirstOrDefault() ?? string.Empty);
            string occurrence = FormatEventTime(volcano);
            string primary = !string.IsNullOrWhiteSpace(headline)
                ? $"{occurrence}{headline}"
                : !string.IsNullOrWhiteSpace(volcano.VolcanoName)
                    ? $"{occurrence}{volcano.VolcanoName}で噴火が発生"
                    : $"{occurrence}噴火が発生";

            AddPrimaryPage(pages, volcano, badge, primary, style);

            // VFVO56のVolcanoHeadlineとVolcanoActivityには、同じ噴火発生文が
            // 常体・敬体や観測日時だけを変えて重複収録されることがある。
            // 見出しは先頭ページで使用済みなので、追加情報を持つ活動文だけを送る。
            string[] activityDetails = SplitSentences(volcano.Activity)
                .Where(line => !AreEquivalentEruptionStatements(headline, line))
                .ToArray();
            AddTextPages(pages, badge, style, activityDetails);
        }
        else
        {
            string primary = string.Join("　", new[]
            {
                volcano.VolcanoName,
                volcano.AlertLevelText,
            }.Where(static value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(primary))
            {
                primary = FirstSentence(volcano.Headline);
            }

            AddPrimaryPage(pages, volcano, badge, primary, style);
        }

        if (!volcano.IsTelegramCancellation)
        {
            AddAreaPages(pages, badge, style, volcano.TargetAreas);
            if (volcano.InformationType != VolcanoInformationType.EruptionFlash)
            {
                AddTextPages(pages, badge, style, volcano.Activity, volcano.Headline);
            }

            AddTextPages(pages, "警戒事項", style, volcano.Prevention);
            AddTextPages(pages, "補足", style, volcano.ContentText, volcano.OtherInfo);
            AddTextPages(pages, "参考", style, volcano.Appendix);
        }

        return PageComposerSupport.CreateProgram(
            volcano,
            settings,
            priority,
            EndPolicy.AutoHide,
            pages);
    }

    private static string GetBadge(VolcanoEvent volcano)
    {
        if (volcano.IsTelegramCancellation ||
            PageComposerSupport.IsTelegramCancellation(volcano.Issue))
        {
            return volcano.InformationType == VolcanoInformationType.EruptionFlash
                ? "噴火速報取消"
                : "取消";
        }

        if (volcano.InformationType == VolcanoInformationType.EruptionFlash)
        {
            return "噴火速報";
        }

        if (volcano.IsCancelled)
        {
            return "解除";
        }

        return volcano.IsWarning ? "噴火警報" : "噴火予報";
    }

    private static bool TryComposeAlertLevelChange(
        VolcanoEvent volcano,
        string badge,
        string style,
        List<PageDraft> pages)
    {
        if (volcano.InformationType != VolcanoInformationType.WarningForecast ||
            !string.Equals(volcano.Issue.RawType, "VFVO50", StringComparison.Ordinal) ||
            volcano.IsCancelled ||
            volcano.IsTelegramCancellation)
        {
            return false;
        }

        string[] headlineParts = HeadlinePartPattern()
            .Matches(volcano.Headline)
            .Select(static match => match.Groups["text"].Value.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        string announcement = headlineParts.FirstOrDefault(static value =>
            value.Contains("警報", StringComparison.Ordinal) &&
            value.Contains("発表", StringComparison.Ordinal)) ?? string.Empty;
        string levelChange = headlineParts.FirstOrDefault(static value =>
            value.Contains("噴火警戒レベルを", StringComparison.Ordinal) &&
            (value.Contains("引上げ", StringComparison.Ordinal) ||
             value.Contains("引き上げ", StringComparison.Ordinal))) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(announcement) || string.IsNullOrWhiteSpace(levelChange))
        {
            return false;
        }

        announcement = AnnouncementEndingPattern().Replace(announcement, "を発表しました");
        levelChange = levelChange.Replace("引上げ", "引き上げ", StringComparison.Ordinal);
        AddPrimaryPage(
            pages,
            volcano,
            badge,
            $"{announcement}\n{levelChange}",
            style);
        return true;
    }

    private static string FormatEventTime(VolcanoEvent volcano)
    {
        if (volcano.EventTime is not DateTimeOffset eventTime)
        {
            return string.Empty;
        }

        string formatted = volcano.EventTimePrecision.Contains("Thh:mm", StringComparison.Ordinal)
            ? PageComposerSupport.FormatJapanTime(eventTime)
            : volcano.EventTimePrecision.EndsWith("Thh", StringComparison.Ordinal)
                ? $"{eventTime:HH時}"
                : PageComposerSupport.FormatJapanTime(eventTime);
        return $"{formatted}{(volcano.EventTimeIsApproximate ? "頃" : string.Empty)}　";
    }

    private static void AddPrimaryPage(
        List<PageDraft> pages,
        VolcanoEvent volcano,
        string badge,
        string primary,
        string style)
    {
        var blocks = new List<DisplayBlock>();
        if (volcano.IsCorrection && !volcano.IsTelegramCancellation)
        {
            blocks.Add(PageComposerSupport.CreateCorrectionBlock(volcano.Issue.Correction));
        }

        blocks.Add(new DisplayBlock(badge, primary, string.Empty, style));
        pages.Add(new PageDraft(blocks));
    }

    private static void AddAreaPages(
        List<PageDraft> pages,
        string badge,
        string style,
        IReadOnlyList<VolcanoTargetArea> areas)
    {
        string[] lines = areas
            .Select(static area => string.Join("　", new[]
            {
                area.Name,
                area.KindName,
            }.Where(static value => !string.IsNullOrWhiteSpace(value))))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        for (int offset = 0; offset < lines.Length; offset += LinesPerPage)
        {
            DisplayBlock[] blocks = lines
                .Skip(offset)
                .Take(LinesPerPage)
                .Select((line, index) => new DisplayBlock(
                    index == 0 ? badge : string.Empty,
                    line,
                    string.Empty,
                    style))
                .ToArray();
            pages.Add(new PageDraft(blocks));
        }
    }

    private static void AddTextPages(
        List<PageDraft> pages,
        string badge,
        string style,
        params string[] source)
    {
        string[] lines = source
            .SelectMany(SplitSentences)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (IReadOnlyList<string> pageLines in NarrativeTextPaginator.Paginate(lines))
        {
            AddTextPage(pages, badge, style, pageLines);
        }
    }

    private static void AddTextPage(
        List<PageDraft> pages,
        string badge,
        string style,
        IReadOnlyList<string> lines)
    {
        DisplayBlock[] blocks = lines
            .Select((line, index) => new DisplayBlock(
                index == 0 ? badge : string.Empty,
                line,
                string.Empty,
                style))
            .ToArray();
        pages.Add(new PageDraft(blocks));
    }

    private static string[] SplitSentences(string value) => value
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Split(['。', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static line => SpacePattern().Replace(line, " ").Trim())
        .Where(static line => !string.IsNullOrWhiteSpace(line))
        .ToArray();

    private static string FirstSentence(string value) =>
        SplitSentences(value).FirstOrDefault() ?? "火山情報が発表されました";

    private static string FirstNonBlank(params string[] values) => values
        .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string TrimHeadlineDecoration(string value) => value
        .Trim()
        .Trim('＜', '＞', '<', '>')
        .Trim();

    private static bool AreEquivalentEruptionStatements(string headline, string activity)
    {
        if (string.IsNullOrWhiteSpace(headline) || string.IsNullOrWhiteSpace(activity))
        {
            return false;
        }

        return string.Equals(
            EruptionComparisonPattern().Replace(TrimHeadlineDecoration(headline), string.Empty),
            EruptionComparisonPattern().Replace(TrimHeadlineDecoration(activity), string.Empty),
            StringComparison.Ordinal);
    }

    [GeneratedRegex(@"[ \t　]+", RegexOptions.CultureInvariant)]
    private static partial Regex SpacePattern();

    [GeneratedRegex("[＜<](?<text>[^＞>]+)[＞>]", RegexOptions.CultureInvariant)]
    private static partial Regex HeadlinePartPattern();

    [GeneratedRegex("を発表(?:しました)?$", RegexOptions.CultureInvariant)]
    private static partial Regex AnnouncementEndingPattern();

    [GeneratedRegex(
        @"(?:令和|平成|昭和)?[0-9０-９]+(?:年|月|日|時|分|秒)|頃|[、。,.\s]|しました|＜|＞|<|>",
        RegexOptions.CultureInvariant)]
    private static partial Regex EruptionComparisonPattern();
}
