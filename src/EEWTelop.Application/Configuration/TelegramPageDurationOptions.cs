using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Configuration;

public sealed record TelegramPageDurationOption(string Key, string Label);

public static class TelegramPageDurationOptions
{
    private const string ForeignQuake = "FOREIGN-QUAKE";
    private const string OtherQuake = "OTHER-QUAKE";
    private const string GeneralWeatherWarning = "WEATHER-WARNING";
    private const string RiverFlood = "VXKO";

    public static IReadOnlyList<TelegramPageDurationOption> All { get; } = Array.AsReadOnly<TelegramPageDurationOption>(
    [
        new("VXSE43", "緊急地震速報（警報） · VXSE43"),
        new("VXSE45", "緊急地震速報（地震動予報） · VXSE45"),
        new("VXSE51", "震度速報 · VXSE51"),
        new("VXSE52", "震源に関する情報 · VXSE52"),
        new("VXSE53", "震源・震度に関する情報 · VXSE53"),
        new("VXSE61", "地震情報（その他） · VXSE61"),
        new("VXSE62", "長周期地震動に関する観測情報 · VXSE62"),
        new(ForeignQuake, "遠地地震（電文コードなし）"),
        new(OtherQuake, "その他の地震情報（電文コードなし）"),
        new("VYSE50", "南海トラフ地震臨時情報 · VYSE50"),
        new("VYSE51", "南海トラフ地震臨時情報に関する解説情報 · VYSE51"),
        new("VYSE52", "南海トラフ地震関連解説情報 · VYSE52"),
        new("VYSE60", "北海道・三陸沖後発地震注意情報 · VYSE60"),
        new("VTSE41", "津波警報・注意報・予報 · VTSE41"),
        new("VTSE51", "津波観測に関する情報 · VTSE51"),
        new("VTSE52", "沖合の津波観測に関する情報 · VTSE52"),
        new("VPWW55", "大雨 · VPWW55"),
        new("VPWW56", "土砂災害 · VPWW56"),
        new("VPWW57", "高潮 · VPWW57"),
        new("VPWW58", "暴風・暴風雪 · VPWW58"),
        new("VPWW59", "波浪 · VPWW59"),
        new("VPWW60", "大雪 · VPWW60"),
        new("VPWW61", "その他の注意報 · VPWW61"),
        new("VPWW53", "気象警報・注意報（旧体系） · VPWW53"),
        new("VPWW54", "気象警報・注意報（旧体系） · VPWW54"),
        new("VPWS50", "気象警報・注意報まとめ · VPWS50"),
        new(GeneralWeatherWarning, "気象警報・注意報（電文コードなし）"),
        new("VPOA50", "記録的短時間大雨情報 · VPOA50"),
        new("VPBS50", "気象防災速報 · VPBS50"),
        new("VPBS51", "気象防災速報（潮位） · VPBS51"),
        new("VPHW50", "竜巻注意情報 · VPHW50"),
        new("VPHW51", "竜巻注意情報（目撃情報付き） · VPHW51"),
        new(RiverFlood, "指定河川洪水予報 · VXKO"),
        new("VFVO50", "噴火警報・予報 · VFVO50"),
        new("VFVO56", "噴火速報 · VFVO56"),
    ]);

    private static readonly HashSet<string> KnownKeys = All.Select(static option => option.Key)
        .ToHashSet(StringComparer.Ordinal);

    public static bool IsKnownKey(string key) => KnownKeys.Contains(key);

    internal static string? GetKey(DisasterEvent disasterEvent)
    {
        string? rawType = disasterEvent switch
        {
            EewEvent eew => eew.Issue.RawType,
            QuakeEvent quake => quake.Issue.RawType,
            TsunamiEvent tsunami => tsunami.Issue.RawType,
            WeatherWarningEvent weather => weather.Issue.RawType,
            VolcanoEvent volcano => volcano.Issue.RawType,
            _ => null,
        };

        string? key = rawType?.Trim().ToUpperInvariant();
        if (key is not null && KnownKeys.Contains(key))
        {
            return key;
        }

        // Some feeds provide a normalized report type without a JMA telegram code.
        return disasterEvent switch
        {
            EewEvent { IsWarning: true } => "VXSE43",
            EewEvent => "VXSE45",
            QuakeEvent quake => quake.IssueType switch
            {
                QuakeIssueType.ScalePrompt => "VXSE51",
                QuakeIssueType.Destination => "VXSE52",
                QuakeIssueType.ScaleAndDestination or QuakeIssueType.DetailScale => "VXSE53",
                QuakeIssueType.LongPeriodObservation => "VXSE62",
                QuakeIssueType.Foreign => ForeignQuake,
                QuakeIssueType.NankaiTroughTemporaryInformation => "VYSE50",
                QuakeIssueType.NankaiTroughCommentary => "VYSE52",
                QuakeIssueType.SubsequentEarthquakeAdvisory => "VYSE60",
                _ => OtherQuake,
            },
            TsunamiEvent tsunami when tsunami.Areas.Any(static area => area.Role == TsunamiInformationRole.OffshoreObservation) => "VTSE52",
            TsunamiEvent tsunami when tsunami.Areas.Any(static area => area.Role == TsunamiInformationRole.CoastalObservation) => "VTSE51",
            TsunamiEvent => "VTSE41",
            WeatherWarningEvent weather => weather.InformationType switch
            {
                WeatherInformationType.RecordShortDurationHeavyRain => "VPOA50",
                WeatherInformationType.DisasterPreventionBulletin => "VPBS50",
                WeatherInformationType.TornadoAdvisory => "VPHW50",
                WeatherInformationType.RiverFlood => RiverFlood,
                _ => GeneralWeatherWarning,
            },
            VolcanoEvent { InformationType: VolcanoInformationType.EruptionFlash } => "VFVO56",
            VolcanoEvent => "VFVO50",
            _ => null,
        };
    }
}
