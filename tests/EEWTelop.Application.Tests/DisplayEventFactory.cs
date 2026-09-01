using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Tests;

internal static class DisplayEventFactory
{
    private static readonly DateTimeOffset OriginTime =
        new(2026, 7, 31, 12, 34, 0, TimeSpan.FromHours(9));

    public static DisplaySettings Settings => AppSettings.CreateDefault().Display;

    public static QuakeEvent CreateQuake(
        QuakeIssueType issueType,
        IReadOnlyList<QuakePoint>? points = null,
        DomesticTsunami domesticTsunami = DomesticTsunami.None,
        ForeignTsunami foreignTsunami = ForeignTsunami.Unknown,
        string comment = "",
        string hypocenterName = "固定震源",
        int? depth = 10,
        double? magnitude = 6,
        CorrectionType correction = CorrectionType.None,
        SourceMode sourceMode = SourceMode.Production,
        string rawType = "",
        LongPeriodIntensityInfo? longPeriodIntensity = null,
        bool isCancelled = false,
        string headline = "")
    {
        string effectiveRawType = rawType.Length > 0 ? rawType : issueType.ToString();
        var issue = new IssueInfo(
            "気象庁",
            OriginTime.AddMinutes(1),
            effectiveRawType,
            correction);
        var earthquake = new EarthquakeInfo(
            OriginTime,
            ArrivalTime: null,
            new HypocenterInfo(
                hypocenterName,
                string.Empty,
                35.0,
                139.0,
                depth,
                magnitude,
                string.Empty),
            points is { Count: > 0 }
                ? points.Max(static point => point.Scale)
                : JmaScale.Unknown,
            domesticTsunami,
            foreignTsunami);

        return new QuakeEvent(
            EventId.Create("quake-display-fixture"),
            "P2PQuake",
            issue.IssuedAt,
            issue.IssuedAt.AddSeconds(1),
            "QUAKE-SIGNATURE",
            sourceMode,
            issue,
            issueType,
            earthquake,
            points ?? [],
            comment,
            longPeriodIntensity,
            isCancelled,
            headline);
    }

    public static QuakePoint Point(
        int number,
        JmaScale scale,
        string prefecture = "固定県") => new(
        prefecture,
        $"固定市{number}",
        IsArea: false,
        scale,
        $"{prefecture}固定市{number}");

    public static TsunamiEvent CreateTsunami(
        IReadOnlyList<TsunamiArea> areas,
        bool cancelled = false,
        SourceMode sourceMode = SourceMode.Production,
        string rawType = "Focus",
        DateTimeOffset? observationAsOf = null,
        string informationType = "")
    {
        var issue = new IssueInfo(
            "気象庁",
            OriginTime,
            rawType,
            CorrectionType.None,
            InformationType: informationType);
        return new TsunamiEvent(
            EventId.Create("tsunami-display-fixture"),
            "P2PQuake",
            issue.IssuedAt,
            issue.IssuedAt.AddSeconds(1),
            "TSUNAMI-SIGNATURE",
            sourceMode,
            issue,
            areas,
            cancelled,
            expireAt: null,
            observationAsOf);
    }

    public static TsunamiArea TsunamiArea(int number, TsunamiGrade grade) => new(
        grade,
        Immediate: false,
        $"固定沿岸{number}",
        new TsunamiFirstHeight(
            OriginTime.AddMinutes(number),
            string.Empty),
        new TsunamiMaximumHeight("１ｍ", 1));

    public static EewEvent CreateEew(
        IReadOnlyList<EewArea>? areas = null,
        bool cancelled = false,
        bool isTest = false,
        SourceMode sourceMode = SourceMode.Production,
        bool isWarning = true,
        bool isFinal = false,
        string eventId = "eew-display-fixture",
        string hypocenterName = "固定震源",
        DateTimeOffset? issuedAt = null,
        string? signature = null,
        string provider = "P2PQuake")
    {
        DateTimeOffset effectiveIssuedAt = issuedAt ?? OriginTime;
        var issue = new IssueInfo(
            string.Empty,
            effectiveIssuedAt,
            "EEW",
            CorrectionType.None,
            Serial: "3");
        var earthquake = new EarthquakeInfo(
            effectiveIssuedAt.AddSeconds(-10),
            effectiveIssuedAt,
            new HypocenterInfo(
                hypocenterName,
                "固定地域",
                35,
                139,
                10,
                6.5,
                string.Empty),
            JmaScale.Unknown,
            DomesticTsunami.Unknown,
            ForeignTsunami.Unknown);
        return new EewEvent(
            EventId.Create(eventId),
            provider,
            issue.IssuedAt,
            issue.IssuedAt.AddSeconds(1),
            signature ?? $"EEW-SIGNATURE-{eventId}",
            sourceMode,
            issue,
            cancelled ? null : earthquake,
            areas ?? [],
            isWarning,
            isFinal,
            cancelled,
            isTest);
    }
}
