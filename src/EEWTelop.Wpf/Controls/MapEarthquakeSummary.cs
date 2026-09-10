using System.Globalization;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Controls;

internal sealed record MapEarthquakeSummary(string Hypocenter, string Magnitude, string MaximumIntensity, string Tsunami)
{
    internal static MapEarthquakeSummary Create(QuakeEvent quake)
    {
        if (quake.IsCancelled) return new("取消電文", "表示しません", "表示しません", "この電文は取り消されています");
        var info = quake.Earthquake;
        var hypo = info.Hypocenter;
        string magnitude = hypo?.Magnitude is double value && double.IsFinite(value)
            ? "M" + value.ToString("0.0", CultureInfo.InvariantCulture)
            : !string.IsNullOrWhiteSpace(hypo?.MagnitudeDescription) ? hypo.MagnitudeDescription : "M不明";
        string intensity = info.MaximumScale switch
        {
            JmaScale.Unknown => "不明（電文に情報なし）",
            JmaScale.FiveLower => "震度5弱", JmaScale.FiveUpper => "震度5強",
            JmaScale.SixLower => "震度6弱", JmaScale.SixUpper => "震度6強",
            JmaScale.FiveLowerOrMore => "震度5弱以上（未入電）",
            _ => "震度" + ((int)info.MaximumScale / 10).ToString(CultureInfo.InvariantCulture),
        };
        string domestic = info.DomesticTsunami switch
        {
            DomesticTsunami.None => "日本：津波の心配なし",
            DomesticTsunami.Checking => "日本：津波の有無を調査中。念のため注意してください",
            DomesticTsunami.NonEffective => "日本：若干の海面変動の可能性。被害の心配なし",
            DomesticTsunami.Watch => "日本：津波注意報発表（この電文時点）",
            DomesticTsunami.Warning => "日本：津波情報発表（詳細を確認）",
            _ => "日本：津波情報不明",
        };
        string foreign = info.ForeignTsunami switch
        {
            ForeignTsunami.None => "海外：津波の発生なし",
            ForeignTsunami.Checking => "海外：津波の有無を調査中",
            ForeignTsunami.NonEffectiveNearby => "震源近傍：小さな津波の可能性。被害の心配なし",
            ForeignTsunami.WarningNearby => "震源近傍：津波の可能性あり",
            ForeignTsunami.WarningPacific => "太平洋：津波の可能性あり",
            ForeignTsunami.WarningPacificWide => "太平洋の広域：津波の可能性あり",
            ForeignTsunami.WarningIndian => "インド洋：津波の可能性あり",
            ForeignTsunami.WarningIndianWide => "インド洋の広域：津波の可能性あり",
            ForeignTsunami.Potential => "一般にこの規模では津波の可能性あり",
            _ => string.Empty,
        };
        return new(string.IsNullOrWhiteSpace(hypo?.Name) ? "震源地不明" : hypo.Name, magnitude, intensity,
            domestic + (foreign.Length == 0 ? "" : "\n" + foreign));
    }
}
