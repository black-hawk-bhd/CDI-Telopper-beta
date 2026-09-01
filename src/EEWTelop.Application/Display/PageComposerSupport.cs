using System.Globalization;
using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

internal sealed record PageDraft(
    IReadOnlyList<DisplayBlock> Blocks,
    TimeSpan? DurationOverride = null);

internal static class PageComposerSupport
{
    private static readonly TimeSpan JapanStandardTimeOffset = TimeSpan.FromHours(9);

    public static DisplayProgram CreateProgram(
        DisasterEvent disasterEvent,
        DisplaySettings settings,
        OverlayPriority priority,
        EndPolicy endPolicy,
        IReadOnlyList<PageDraft> drafts)
    {
        int pageCount = drafts.Count;
        var pages = new DisplayPage[pageCount];
        for (int index = 0; index < pageCount; index++)
        {
            PageDraft draft = drafts[index];
            IReadOnlyList<DisplayBlock> blocks = AddPageIndicator(
                draft.Blocks,
                index + 1,
                pageCount,
                settings.ShowPageIndicator);
            pages[index] = new DisplayPage(
                Index: index + 1,
                Blocks: blocks,
                AccessibleText: BuildAccessibleText(blocks),
                draft.DurationOverride);
        }

        return new DisplayProgram(
            ProgramId: $"{disasterEvent.Id.Value}:{disasterEvent.Signature}",
            EventId: disasterEvent.Id,
            Kind: disasterEvent.Kind,
            SourceMode: disasterEvent.SourceMode,
            IssuedAt: disasterEvent.IssuedAt,
            priority,
            Pages: pages,
            StartedAtUtc: disasterEvent.ReceivedAt.ToUniversalTime(),
            endPolicy,
            RehearsalLabel: GetRehearsalLabel(disasterEvent));
    }

    public static string FormatJapanTime(DateTimeOffset value) =>
        value.ToOffset(JapanStandardTimeOffset).ToString("HH時mm分", CultureInfo.InvariantCulture);

    public static bool IsTelegramCancellation(IssueInfo issue) =>
        issue.InformationType?.Contains("取消", StringComparison.Ordinal) == true;

    public static string GetCancellationText(string subject) =>
        $"先ほどの、{subject}を取り消します";

    public static string GetQuakeCancellationSubject(QuakeEvent quake) =>
        quake.Issue.RawType switch
        {
            "VXSE51" => "震度速報",
            "VXSE52" => "震源に関する情報",
            "VXSE53" => "震源・震度に関する情報",
            "VXSE62" => "長周期地震動に関する観測情報",
            "VYSE50" => "南海トラフ地震臨時情報",
            "VYSE60" => "北海道・三陸沖後発地震注意情報",
            _ => quake.IssueType switch
            {
                QuakeIssueType.ScalePrompt => "震度速報",
                QuakeIssueType.Destination => "震源に関する情報",
                QuakeIssueType.DetailScale or QuakeIssueType.ScaleAndDestination =>
                    "震源・震度に関する情報",
                QuakeIssueType.LongPeriodObservation => "長周期地震動に関する観測情報",
                QuakeIssueType.NankaiTroughTemporaryInformation =>
                    "南海トラフ地震臨時情報",
                QuakeIssueType.SubsequentEarthquakeAdvisory =>
                    "北海道・三陸沖後発地震注意情報",
                _ => "地震情報",
            },
        };

    public static string GetTsunamiCancellationSubject(TsunamiEvent tsunami) =>
        tsunami.Issue.RawType switch
        {
            "VTSE41" => "津波警報・注意報・予報",
            "VTSE51" => "津波観測に関する情報",
            "VTSE52" => "沖合の津波観測に関する情報",
            _ => "津波情報",
        };

    public static string GetWeatherCancellationSubject(WeatherWarningEvent weather) =>
        weather.InformationType switch
        {
            WeatherInformationType.RecordShortDurationHeavyRain => "記録的短時間大雨情報",
            WeatherInformationType.DisasterPreventionBulletin => "気象防災速報",
            WeatherInformationType.TornadoAdvisory => "竜巻注意情報",
            _ => "気象警報・注意報",
        };

    public static string GetVolcanoCancellationSubject(VolcanoEvent volcano) =>
        volcano.InformationType == VolcanoInformationType.EruptionFlash
            ? "噴火速報"
            : "噴火警報・予報";

    public static string GetDomesticTsunamiText(DomesticTsunami value) => value switch
    {
        DomesticTsunami.None => "この地震による津波の心配はありません",
        DomesticTsunami.Checking => "津波の有無を調査中です。念のため津波に注意してください。",
        DomesticTsunami.NonEffective =>
            "日本の沿岸では若干の海面変動があるかもしれませんが、被害の心配はありません",
        DomesticTsunami.Watch => "この地震により津波注意報が発表されています",
        DomesticTsunami.Warning => "この地震により津波情報を発表しています",
        _ => string.Empty,
    };

    public static string GetForeignTsunamiText(ForeignTsunami value) => value switch
    {
        ForeignTsunami.None => "海外で津波の発生はありません",
        ForeignTsunami.Checking => "海外での津波の有無を調査中です",
        ForeignTsunami.NonEffectiveNearby =>
            "震源の近傍で小さな津波の可能性がありますが、被害の心配はありません",
        ForeignTsunami.WarningNearby => "震源の近傍で津波の可能性があります",
        ForeignTsunami.WarningPacific => "太平洋で津波の可能性があります",
        ForeignTsunami.WarningPacificWide => "太平洋の広域で津波の可能性があります",
        ForeignTsunami.WarningIndian => "インド洋で津波の可能性があります",
        ForeignTsunami.WarningIndianWide => "インド洋の広域で津波の可能性があります",
        ForeignTsunami.Potential => "一般にこの規模では津波の可能性があります",
        _ => string.Empty,
    };

    public static DisplayBlock CreateCorrectionBlock(CorrectionType correction)
    {
        string text = correction switch
        {
            CorrectionType.ScaleOnly => "震度を訂正します",
            CorrectionType.DestinationOnly => "震源を訂正します",
            CorrectionType.ScaleAndDestination => "震度・震源を訂正します",
            CorrectionType.Generic => "内容を訂正します",
            _ => "内容を訂正します",
        };
        return new DisplayBlock("訂正", text, string.Empty, DisplayStyleTokens.Correction);
    }

    private static IReadOnlyList<DisplayBlock> AddPageIndicator(
        IReadOnlyList<DisplayBlock> source,
        int index,
        int count,
        bool enabled)
    {
        if (!enabled || count <= 1)
        {
            return source;
        }

        var blocks = new DisplayBlock[source.Count + 1];
        for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            blocks[sourceIndex] = source[sourceIndex];
        }

        blocks[^1] = new DisplayBlock(
            string.Empty,
            $"{index} / {count}",
            string.Empty,
            DisplayStyleTokens.PageIndicator);
        return blocks;
    }

    private static string BuildAccessibleText(IEnumerable<DisplayBlock> blocks) => string.Join(
        "。",
        blocks
            .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
            .SelectMany(static block => new[] { block.Badge, block.PrimaryText, block.SecondaryText })
            .Where(static text => !string.IsNullOrWhiteSpace(text)));

    private static string GetRehearsalLabel(DisasterEvent disasterEvent)
    {
        if (disasterEvent is EewEvent { IsTest: true })
        {
            return "操作テスト／訓練";
        }

        return disasterEvent.SourceMode switch
        {
            SourceMode.Sandbox => "サンドボックス／訓練",
            SourceMode.ManualTest => "操作テスト／訓練",
            SourceMode.HistoryRehearsal => "履歴リハーサル／訓練",
            _ => string.Empty,
        };
    }
}
