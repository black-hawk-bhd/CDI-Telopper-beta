using System.Globalization;
using System.Text.Json;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Testing;

/// <summary>Simulator 0.3.1 projections are always training, even when marked live upstream.</summary>
internal static class DisasterSimulatorDecoder
{
    internal static JsonElement Get(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;
    internal static string Text(JsonElement value, string name) => Get(value, name) is var v &&
        v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
    internal static bool Flag(JsonElement value, string name) => Get(value, name).ValueKind == JsonValueKind.True;
    private static double? Number(JsonElement v, string name) => Get(v, name) is var n &&
        n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
    private static DateTimeOffset? Time(JsonElement v, string name) => DateTimeOffset.TryParse(
        Text(v, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
    private static IEnumerable<JsonElement> Array(JsonElement v, string name) =>
        Get(v, name) is var a && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];
    private static T EnumValue<T>(JsonElement v, string name) where T : struct, Enum =>
        Enum.TryParse<T>(Text(v, name), out var result) && Enum.IsDefined(result) ? result : default;

    internal static JmaScale Scale(string value) => value switch
    {
        "0" => JmaScale.Zero, "1" => JmaScale.One, "2" => JmaScale.Two,
        "3" => JmaScale.Three, "4" => JmaScale.Four,
        "5-" or "5弱" => JmaScale.FiveLower, "5-?" => JmaScale.FiveLowerOrMore,
        "5+" or "5強" => JmaScale.FiveUpper, "6-" or "6弱" => JmaScale.SixLower,
        "6+" or "6強" => JmaScale.SixUpper, "7" => JmaScale.Seven, _ => JmaScale.Unknown,
    };

    internal static DisasterEvent Decode(string kind, JsonElement item, DateTimeOffset receivedAt)
    {
        string id = Text(item, "eventId");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256)
            throw new FormatException("シミュレーターのイベントIDが不正です。");
        var eventId = EventId.Create("simulator:" + id);
        const string provider = "cdi-disaster-simulator";
        var issued = Time(item, "issuedAt") ?? receivedAt;
        bool cancelled = Flag(item, "isCancelled");
        var issue = new IssueInfo(provider, issued,
            kind == "tsunami" ? Text(item, "telegramType") : Text(item, "informationType"),
            CorrectionType.None, Text(item, "serial"), Flag(item, "isTelegramCancellation") ? "取消" : "");
        var eq = Get(item, "earthquake");
        var h = Get(eq, "hypocenter");
        double? depth = Number(h, "depthKilometers");
        var earthquake = new EarthquakeInfo(Time(eq, "originTime") ?? issued, null,
            h.ValueKind == JsonValueKind.Object ? new HypocenterInfo(Text(h, "name"), Text(h, "name"),
                Number(h, "latitude"), Number(h, "longitude"),
                depth is >= 0 and <= 1000 ? (int)depth.Value : null,
                Number(eq, "magnitude") ?? Number(h, "magnitude"), "") : null,
            Scale(Text(eq, "maximumIntensity")), EnumValue<DomesticTsunami>(eq, "domesticTsunami"),
            EnumValue<ForeignTsunami>(eq, "foreignTsunami")) { OriginTimeIsKnown = Time(eq, "originTime") is not null };
        DisasterEvent result = kind switch
        {
            "earthquake" => new QuakeEvent(eventId, provider, issued, receivedAt, "", SourceMode.ManualTest,
                issue, EnumValue<QuakeIssueType>(item, "informationType"), earthquake,
                Array(item, "points").Select(p => new QuakePoint(Text(p, "prefecture"), Text(p, "name"),
                    string.IsNullOrEmpty(Text(p, "municipalityName")), Scale(Text(p, "intensity")), Text(p, "name"))
                {
                    MunicipalityName = Text(p, "municipalityName"), MunicipalityCode = Text(p, "municipalityCode"),
                    SeismicAreaName = Text(p, "seismicAreaName"), SeismicAreaCode = Text(p, "areaCode"),
                    StationCode = Text(p, "stationCode"), Latitude = Number(p, "latitude"), Longitude = Number(p, "longitude"),
                }).ToArray(), "", isCancelled: cancelled, headline: Text(item, "headline")),
            "eew" => new EewEvent(eventId, provider, issued, receivedAt, "", SourceMode.ManualTest, issue,
                earthquake, Array(item, "areas").Select(a => new EewArea(Text(a, "prefecture"), Text(a, "name"),
                    Scale(Text(a, "intensity")), (int)Scale(Text(a, "intensity")), EewWarningKind.Unknown,
                    Time(a, "arrivalTime"))).ToArray(), Flag(item, "isWarning"), Flag(item, "isFinal"), cancelled, true),
            "tsunami" => new TsunamiEvent(eventId, provider, issued, receivedAt, "", SourceMode.ManualTest, issue,
                Array(item, "areas").Select(a => new TsunamiArea(EnumValue<TsunamiGrade>(a, "grade"), false,
                    Text(a, "name"), Get(a, "firstHeight").ValueKind == JsonValueKind.Object
                        ? new TsunamiFirstHeight(Time(Get(a, "firstHeight"), "arrivalTime"), Text(Get(a, "firstHeight"), "condition")) : null,
                    Get(a, "maximumHeight").ValueKind == JsonValueKind.Object
                        ? new TsunamiMaximumHeight(Text(Get(a, "maximumHeight"), "description"), Number(Get(a, "maximumHeight"), "valueMeters"),
                            Time(Get(a, "maximumHeight"), "observedAt"), Text(Get(a, "maximumHeight"), "condition")) : null)
                    { Role = EnumValue<TsunamiInformationRole>(a, "role"), ParentAreaName = Text(a, "parentAreaName"), HighTideAt = Time(a, "highTideAt") }).ToArray(),
                cancelled || Flag(item, "isTelegramCancellation"), Time(item, "expiresAt")),
            _ => throw new FormatException("未対応のシミュレーター情報種別です。"),
        };
        return result with { IsExpired = Flag(item, "isExpired") };
    }
}
