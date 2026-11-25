using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Operations;

public static class TestCaseClassification
{
    public const string Unclassified = "未分類";

    public static EventKind GetKind(string? telegramType) => (telegramType ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "VXSE43" or "VXSE45" => EventKind.Eew,
        "VXSE51" or "VXSE52" or "VXSE53" or "VXSE61" or "VXSE62" or "VYSE50" or "VYSE51" or "VYSE52" or "VYSE60" => EventKind.Quake,
        "VTSE41" or "VTSE51" or "VTSE52" => EventKind.Tsunami,
        "VFVO50" or "VFVO56" => EventKind.Volcano,
        var type when type.StartsWith("緊急地震速報", StringComparison.Ordinal) => EventKind.Eew,
        var type when type.StartsWith("津波", StringComparison.Ordinal) ||
            type.StartsWith("沖合の津波", StringComparison.Ordinal) => EventKind.Tsunami,
        var type when type.StartsWith("噴火", StringComparison.Ordinal) => EventKind.Volcano,
        var type when type.StartsWith("震度速報", StringComparison.Ordinal) ||
            type.StartsWith("震源", StringComparison.Ordinal) ||
            type.StartsWith("長周期地震動", StringComparison.Ordinal) ||
            type.StartsWith("南海トラフ地震", StringComparison.Ordinal) ||
            type.StartsWith("北海道・三陸沖後発地震", StringComparison.Ordinal) => EventKind.Quake,
        var type when type.StartsWith("VPWW", StringComparison.Ordinal) ||
            type.StartsWith("VPWS", StringComparison.Ordinal) ||
            type.StartsWith("VPOA", StringComparison.Ordinal) ||
            type.StartsWith("VPBS", StringComparison.Ordinal) ||
            type.StartsWith("VPHW", StringComparison.Ordinal) ||
            type.StartsWith("VXKO", StringComparison.Ordinal) ||
            type.Contains("気象警報・注意報", StringComparison.Ordinal) ||
            type.Contains("特別警報・警報・注意報", StringComparison.Ordinal) ||
            type.StartsWith("気象防災速報", StringComparison.Ordinal) ||
            type.StartsWith("竜巻注意情報", StringComparison.Ordinal) ||
            type.StartsWith("記録的短時間大雨情報", StringComparison.Ordinal) ||
            type.StartsWith("指定河川洪水予報", StringComparison.Ordinal) => EventKind.WeatherWarning,
        _ => EventKind.Unknown,
    };

    public static string GetLabel(EventKind kind) => kind switch
    {
        EventKind.Eew => "緊急地震速報",
        EventKind.Quake => "地震情報",
        EventKind.Tsunami => "津波情報",
        EventKind.WeatherWarning => "気象情報",
        EventKind.Volcano => "火山情報",
        _ => Unclassified,
    };
}
