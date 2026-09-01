using EEWTelop.Application.Configuration;

namespace EEWTelop.Application.Display;

public sealed record SubtitlePhraseDefinition(
    string Id,
    string Category,
    string Label,
    string DefaultText);

public static class SubtitlePhraseCatalog
{
    public static IReadOnlyList<SubtitlePhraseDefinition> All { get; } =
    [
        new("quake.tsunami.none", "地震・津波", "津波の心配なし",
            "この地震による津波の心配はありません"),
        new("quake.tsunami.checking", "地震・津波", "津波調査中",
            "津波の有無を調査中です。"),
        new("quake.tsunami.caution", "地震・津波", "念のため津波に注意",
            "念のため津波に注意してください。"),
        new("quake.tsunami.minor", "地震・津波", "若干の海面変動",
            "日本の沿岸では若干の海面変動があるかもしれませんが、被害の心配はありません"),
        new("quake.tsunami.watch", "地震・津波", "津波注意報発表中",
            "この地震により津波注意報が発表されています"),
        new("quake.tsunami.warning", "地震・津波", "津波情報発表中",
            "この地震により津波情報を発表しています"),
        new("foreign-tsunami.none", "海外地震", "海外で津波なし",
            "海外で津波の発生はありません"),
        new("foreign-tsunami.checking", "海外地震", "海外津波調査中",
            "海外での津波の有無を調査中です"),
        new("foreign-tsunami.nearby-no-damage", "海外地震", "震源近傍の小さな津波",
            "震源の近傍で小さな津波の可能性がありますが、被害の心配はありません"),
        new("quake.scale-prompt.cancel", "地震・津波", "震度速報取消",
            "先ほどの、震度速報を取り消します"),
        new("quake.hypocenter.cancel", "取消電文", "震源情報取消",
            "先ほどの、震源に関する情報を取り消します"),
        new("quake.detail.cancel", "取消電文", "震源・震度情報取消",
            "先ほどの、震源・震度に関する情報を取り消します"),
        new("quake.long-period.cancel", "取消電文", "長周期地震動情報取消",
            "先ほどの、長周期地震動に関する観測情報を取り消します"),
        new("eew.cancel", "取消電文", "緊急地震速報取消",
            "先ほどの、緊急地震速報を取り消します"),
        new("tsunami.warning.cancel", "取消電文", "津波警報等取消",
            "先ほどの、津波警報・注意報・予報を取り消します"),
        new("tsunami.observation.cancel", "取消電文", "津波観測情報取消",
            "先ほどの、津波観測に関する情報を取り消します"),
        new("tsunami.offshore.cancel", "取消電文", "沖合津波観測情報取消",
            "先ほどの、沖合の津波観測に関する情報を取り消します"),
        new("weather.warning.cancel", "取消電文", "気象警報・注意報取消",
            "先ほどの、気象警報・注意報を取り消します"),
        new("weather.rainfall.cancel", "取消電文", "記録的短時間大雨情報取消",
            "先ほどの、記録的短時間大雨情報を取り消します"),
        new("weather.bulletin.cancel", "取消電文", "気象防災速報取消",
            "先ほどの、気象防災速報を取り消します"),
        new("weather.tornado.cancel", "取消電文", "竜巻注意情報取消",
            "先ほどの、竜巻注意情報を取り消します"),
        new("volcano.warning.cancel", "取消電文", "噴火警報・予報取消",
            "先ほどの、噴火警報・予報を取り消します"),
        new("volcano.flash.cancel", "取消電文", "噴火速報取消",
            "先ほどの、噴火速報を取り消します"),
        new("correction.scale", "訂正報", "震度訂正",
            "震度を訂正します"),
        new("correction.hypocenter", "訂正報", "震源訂正",
            "震源を訂正します"),
        new("correction.both", "訂正報", "震度・震源訂正",
            "震度・震源を訂正します"),
        new("correction.generic", "訂正報", "内容訂正",
            "内容を訂正します"),
    ];

    public static DisplayProgram ApplyOverrides(
        DisplayProgram program,
        DisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(settings);
        Dictionary<string, string>? overrides = settings.SubtitlePhraseOverrides;
        if (overrides is null || overrides.Count == 0)
        {
            return program;
        }

        KeyValuePair<string, string>[] replacements = All
            .Where(definition => overrides.ContainsKey(definition.Id))
            .Select(definition => new KeyValuePair<string, string>(
                definition.DefaultText,
                overrides[definition.Id] ?? string.Empty))
            .Where(replacement => !string.Equals(
                replacement.Key,
                replacement.Value,
                StringComparison.Ordinal))
            .ToArray();
        if (replacements.Length == 0)
        {
            return program;
        }

        DisplayPage[] pages = program.Pages.Select(page =>
        {
            DisplayBlock[] blocks = page.Blocks.Select(block => block with
            {
                PrimaryText = ReplaceKnownPhrases(block.PrimaryText, replacements),
                SecondaryText = ReplaceKnownPhrases(block.SecondaryText, replacements),
            }).ToArray();
            string accessibleText = string.Join(
                Environment.NewLine,
                blocks
                    .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
                    .SelectMany(static block => new[]
                    {
                        block.Badge,
                        block.PrimaryText,
                        block.SecondaryText,
                    })
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));
            return page with { Blocks = blocks, AccessibleText = accessibleText };
        }).ToArray();

        return program with { Pages = pages };
    }

    private static string ReplaceKnownPhrases(
        string text,
        IReadOnlyList<KeyValuePair<string, string>> replacements)
    {
        string result = text;
        foreach (KeyValuePair<string, string> replacement in replacements)
        {
            result = result.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
        }

        return result;
    }
}
