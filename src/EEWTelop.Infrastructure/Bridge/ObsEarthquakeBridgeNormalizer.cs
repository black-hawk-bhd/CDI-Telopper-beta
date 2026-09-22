using System.Globalization;
using System.Text.Json;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Bridge;

/// <summary>Plugin-specific input is converted here, never exposed as CDI's API contract.</summary>
public sealed class ObsEarthquakeBridgeNormalizer(IEventSignatureBuilder signatures) : IEventNormalizer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Issued, long? Serial, bool Expired)> _latest = new(StringComparer.Ordinal);
    public NormalizeResult Normalize(RawProviderMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        try
        {
            using var doc = JsonDocument.Parse(raw.Payload);
            JsonElement envelope = doc.RootElement;
            if (!BridgeV11Payload.IsSchema(Text(envelope, "schema"))) return Invalid("Unsupported Bridge schema.");
            // Both envelope and payload must be live; snapshots do not have envelope mode metadata.
            if (Text(envelope, "mode") != "live") return NormalizeResult.Ignored();
            JsonElement p = BridgeV11Payload.Adapt(envelope);
            if (p.ValueKind != JsonValueKind.Object) return Invalid("Missing payload.");
            if ((Text(p, "mode") is { Length: > 0 } payloadMode && payloadMode != "live") || Flag(p, "isTraining") || Flag(p, "isTest") ||
                Text(p, "status") is "訓練" or "試験" || Text(Get(p, "Control"), "Status") is "訓練" or "試験")
                return NormalizeResult.Ignored();
            string type = Text(envelope, "type");
            bool cancellation = type.EndsWith(".cancel", StringComparison.Ordinal) || Flag(p, "cancelled") ||
                Text(p, "infoType") == "取消" || Text(Get(p, "Head"), "InfoType") == "取消";
            if (Get(p, "available").ValueKind == JsonValueKind.False && !cancellation && !Flag(p, "expired"))
                return NormalizeResult.Ignored();
            if (raw.IsStateSnapshot && type != "eew" && Text(p, "mode") != "live")
                return NormalizeResult.Ignored(new ValidationIssue("bridge.snapshot.mode",
                    "Bridge snapshot has no verified live/test mode. Install the Bridge metadata compatibility patch.", ValidationSeverity.Warning));
            DisasterEvent? result = type switch
            {
                "eew" or "eew.cancel" => Eew(raw, envelope, p),
                "earthquake" or "earthquake.cancel" => Quake(raw, envelope, p, type == "earthquake.cancel" || Flag(p, "cancelled") || Text(p, "infoType") == "取消"),
                "tsunami.forecast" or "tsunami.observation" or "tsunami.forecast.cancel" or "tsunami.observation.cancel" => Tsunami(raw, envelope, p, type.StartsWith("tsunami.observation", StringComparison.Ordinal)),
                _ => null,
            };
            if (result is null) return NormalizeResult.Ignored(new ValidationIssue("bridge.payload",
                "No unambiguous supported information; empty tsunami data is not an all-clear.", ValidationSeverity.Warning));
            result = result with { IsExpired = Flag(p, "expired") || (Date(p, "validDateTime") is { } valid && valid <= raw.ReceivedAt) ||
                result is TsunamiEvent { ExpireAt: { } expiry } && expiry <= raw.ReceivedAt };
            lock (_gate)
            {
                string key = (type.EndsWith(".cancel", StringComparison.Ordinal) ? type[..^7] : type) + ":" + result.Id;
                string? serialText = result switch { EewEvent e => e.Issue.Serial, QuakeEvent q => q.Issue.Serial, TsunamiEvent t => t.Issue.Serial, _ => null };
                long? serial = long.TryParse(serialText, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ? parsed : null;
                if (_latest.TryGetValue(key, out var previous) && (result.IssuedAt < previous.Issued ||
                    (serial is not null && previous.Serial is not null && (serial < previous.Serial ||
                        (serial == previous.Serial && !(result.IsExpired && !previous.Expired) && Text(envelope, "schema") == "obs-earthquake.bridge.event.v1"))))) return NormalizeResult.Ignored();
                long? frontier = long.TryParse(Text(p, "highestSerial"), NumberStyles.None, CultureInfo.InvariantCulture, out long highest)
                    ? Math.Max(highest, serial ?? highest) : serial;
                _latest[key] = (result.IssuedAt, frontier ?? previous.Serial, result.IsExpired);
                while (_latest.Count > 1024) _latest.Remove(_latest.MinBy(static x => x.Value.Issued).Key);
            }
            return NormalizeResult.Success(result with { Signature = signatures.Build(result) + (result.IsExpired ? ":expired" : "") });
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return Invalid(ex.Message);
        }
    }

    private static EewEvent? Eew(RawProviderMessage raw, JsonElement env, JsonElement p)
    {
        bool cancel = Flag(p, "isCancel");
        if (!Flag(p, "isWarn") && !cancel) return null;
        string id = RequiredId(p, env);
        DateTimeOffset issued = Date(p, "time") ?? Date(p, "receivedAt") ?? throw new FormatException("Missing EEW time.");
        if (raw.IsStateSnapshot && raw.ReceivedAt - issued > TimeSpan.FromMinutes(10)) return null;
        var issue = new IssueInfo("OBS-Earthquake Bridge", issued, "BRIDGE-EEW", CorrectionType.None, Serial(p, env), cancel ? "取消" : "発表");
        var earthquake = Earthquake(p, issued, "hypo", "mag", "depth") with { OriginTimeIsKnown = false };
        EewArea[] areas = Items(Get(p, "warnAreas")).Where(static x => x.ValueKind == JsonValueKind.String)
            .Select(static x => new EewArea("", x.GetString()!, JmaScale.Unknown, -1, EewWarningKind.Unknown, null)).ToArray();
        return new(EventId.Create(id), raw.Provider, issued, raw.ReceivedAt, "", raw.SourceMode,
            issue, earthquake, areas, Flag(p, "isWarn"), Flag(p, "isFinal"), cancel, false);
    }

    private static QuakeEvent Quake(RawProviderMessage raw, JsonElement env, JsonElement p, bool cancel)
    {
        string id = RequiredId(p, env);
        DateTimeOffset issued = Date(p, "reportTime") ?? throw new FormatException("Missing earthquake report time.");
        var issue = new IssueInfo("OBS-Earthquake Bridge", issued, "BRIDGE-QUAKE", CorrectionType.None, Serial(p, env), cancel ? "取消" : "発表");
        List<QuakePoint> points = [];
        foreach (JsonElement station in Items(Get(p, "stations")))
        {
            string name = Text(station, "name");
            if (name.Length == 0) continue;
            points.Add(new(Text(station, "region"), name, false, Scale(Text(station, "intensity")), name)
            {
                StationCode = Text(station, "code"), MunicipalityName = Text(station, "city"),
                Latitude = Latitude(station), Longitude = Longitude(station),
            });
        }
        if (points.Count == 0 && Get(p, "areasByIntensity") is { ValueKind: JsonValueKind.Object } areas)
            foreach (JsonProperty group in areas.EnumerateObject())
                foreach (JsonElement name in Items(group.Value))
                    if (name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text)
                        points.Add(new("", text, true, Scale(group.Name), text));
        QuakeIssueType kind = Text(p, "type") switch
        {
            "震度速報" => QuakeIssueType.ScalePrompt,
            "震源に関する情報" => QuakeIssueType.Destination,
            "遠地地震に関する情報" => QuakeIssueType.Foreign,
            _ => QuakeIssueType.DetailScale,
        };
        return new(EventId.Create(id), raw.Provider, issued, raw.ReceivedAt, "", raw.SourceMode,
            issue, kind, Earthquake(p, Date(p, "originTime") ?? issued, "hypocenter", "magnitude", "depthKm") with { OriginTimeIsKnown = Date(p, "originTime") is not null }, points,
            "", isCancelled: cancel, headline: Text(p, "headline"));
    }

    private static EarthquakeInfo Earthquake(JsonElement p, DateTimeOffset origin, string name, string magnitude, string depth)
    {
        double? km = Number(p, depth);
        return new(origin, null, new(Text(p, name), "", Latitude(p), Longitude(p),
            km is >= 0 and <= 1000 ? (int)km : null, Number(p, magnitude), ""),
            Scale(Text(p, "maxInt")), DomesticTsunami.Unknown, ForeignTsunami.Unknown);
    }

    private static TsunamiEvent? Tsunami(RawProviderMessage raw, JsonElement env, JsonElement p, bool observation)
    {
        JsonElement head = Get(p, "Head");
        string id = Text(head, "EventID");
        if (id.Length == 0) id = Text(env, "eventId");
        if (id.Length == 0) throw new FormatException("Missing tsunami event ID.");
        DateTimeOffset issued = Date(head, "ReportDateTime") ?? throw new FormatException("Missing tsunami report time.");
        JsonElement body = Get(Get(p, "Body"), "Tsunami");
        List<TsunamiArea> areas = [];
        bool explicitRelease = false;
        if (!observation)
        {
            foreach (JsonElement item in Items(Get(Get(body, "Forecast"), "Item")))
            {
                string code = Text(Get(Get(item, "Category"), "Kind"), "Code");
                if (code is "50" or "60") { explicitRelease = true; continue; }
                TsunamiGrade grade = code switch
                {
                    "52" or "53" => TsunamiGrade.MajorWarning, "51" => TsunamiGrade.Warning,
                    "62" => TsunamiGrade.Watch, "71" or "72" or "73" => TsunamiGrade.Forecast,
                    _ => throw new FormatException("Unknown tsunami grade: " + code),
                };
                string name = Text(Get(item, "Area"), "Name");
                if (name.Length == 0) throw new FormatException("Missing tsunami area.");
                areas.Add(Area(item, name, grade));
            }
        }
        else
        {
            foreach (JsonElement item in Items(Get(Get(body, "Observation"), "Item")))
                foreach (JsonElement station in Items(Get(item, "Station")))
                {
                    string name = Text(station, "Name");
                    if (name.Length == 0) continue;
                    areas.Add(Area(station, name, TsunamiGrade.Unknown) with
                    { Role = TsunamiInformationRole.CoastalObservation, ParentAreaName = Text(Get(item, "Area"), "Name") });
                }
        }
        // r6 loses InfoType in an empty forecast: empty cannot safely mean all-clear.
        bool telegramCancellation = Text(head, "InfoType") == "取消";
        if (areas.Count == 0 && !explicitRelease && !telegramCancellation) return null;
        var issue = new IssueInfo("OBS-Earthquake Bridge", issued, observation ? "VTSE51" : "VTSE41",
            CorrectionType.None, Text(head, "Serial"), telegramCancellation ? "取消" : "発表");
        return new(EventId.Create(id), raw.Provider, issued, raw.ReceivedAt, "", raw.SourceMode, issue,
            areas, telegramCancellation || (explicitRelease && areas.Count == 0), Date(head, "ValidDateTime"),
            observation ? Date(head, "TargetDateTime") : null);
    }

    private static TsunamiArea Area(JsonElement value, string name, TsunamiGrade grade)
    {
        JsonElement first = Get(value, "FirstHeight"), maximum = Get(value, "MaxHeight");
        return new(grade, false, name,
            first.ValueKind == JsonValueKind.Object ? new(Date(first, "ArrivalTime"), Text(first, "Condition")) : null,
            maximum.ValueKind == JsonValueKind.Object ? new(Text(maximum, "TsunamiHeight"), Number(maximum, "TsunamiHeight"),
                Date(maximum, "DateTime"), Text(maximum, "Condition")) : null);
    }

    private static string RequiredId(JsonElement p, JsonElement envelope)
    {
        string id = Text(p, "eventId");
        if (id.Length == 0) id = Text(envelope, "eventId");
        return id.Length > 0 ? id : throw new FormatException("Missing event ID.");
    }
    private static string Serial(JsonElement p, JsonElement env) => Text(p, "serial") is { Length: > 0 } serial ? serial : Text(env, "serial");
    private static JsonElement Get(JsonElement p, string key) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var value) ? value : default;
    private static string Text(JsonElement p, string key) => Get(p, key) is { ValueKind: JsonValueKind.String or JsonValueKind.Number } value ? value.ToString() : "";
    private static bool Flag(JsonElement p, string key) => Get(p, key).ValueKind == JsonValueKind.True;
    private static IEnumerable<JsonElement> Items(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static DateTimeOffset? Date(JsonElement p, string key) => DateTimeOffset.TryParse(Text(p, key), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static double? Number(JsonElement p, string key) => double.TryParse(Text(p, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
    private static double? Latitude(JsonElement p) => Number(p, "lat") is >= -90 and <= 90 ? Number(p, "lat") : null;
    private static double? Longitude(JsonElement p) => Number(p, "lon") is >= -180 and <= 180 ? Number(p, "lon") : null;
    private static JmaScale Scale(string value) => value switch
    {
        "0" => JmaScale.Zero, "1" => JmaScale.One, "2" => JmaScale.Two, "3" => JmaScale.Three,
        "4" => JmaScale.Four, "5-" => JmaScale.FiveLower, "!5-" or "5-?" => JmaScale.FiveLowerOrMore,
        "5+" => JmaScale.FiveUpper, "6-" => JmaScale.SixLower, "6+" => JmaScale.SixUpper, "7" => JmaScale.Seven,
        _ => JmaScale.Unknown,
    };
    private static NormalizeResult Invalid(string message) => NormalizeResult.Invalid(new ValidationIssue("bridge", message, ValidationSeverity.Error));
}
