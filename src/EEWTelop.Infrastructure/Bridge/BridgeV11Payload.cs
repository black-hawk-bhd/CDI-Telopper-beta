using System.Text.Json;
using System.Text.Json.Nodes;

namespace EEWTelop.Infrastructure.Bridge;

/// <summary>Adapts v1.1 metadata/wrappers to the existing Bridge boundary, not the public CDI API.</summary>
internal static class BridgeV11Payload
{
    public static bool IsSchema(string schema) => schema is "cdi.bridge.event.v1" or "obs-earthquake.bridge.event.v1";

    public static JsonElement Adapt(JsonElement envelope)
    {
        var e = JsonNode.Parse(envelope.GetRawText())!.AsObject();
        var p = e["payload"] as JsonObject ?? new JsonObject();
        string type = e["type"]?.GetValue<string>() ?? "";
        foreach (string key in new[] { "eventId", "serial", "infoType", "reportTime", "receivedAt" })
            if (p[key] is null && e[key] is not null) p[key] = e[key]!.DeepClone();
        if (type.EndsWith(".cancel", StringComparison.Ordinal))
        {
            p["cancelled"] = true;
            p["infoType"] = "取消";
        }
        if (type.StartsWith("tsunami.", StringComparison.Ordinal))
        {
            var body = (p["payload"] as JsonObject)?.DeepClone().AsObject() ?? p.DeepClone().AsObject();
            var head = body["Head"] as JsonObject;
            if (head is null) { head = new JsonObject(); body["Head"] = head; }
            foreach (var pair in new[] { ("eventId", "EventID"), ("serial", "Serial"), ("reportTime", "ReportDateTime"),
                ("infoType", "InfoType"), ("validDateTime", "ValidDateTime") })
                if (p[pair.Item1] is not null) head[pair.Item2] = p[pair.Item1]!.DeepClone();
            // A cancelled tombstone may contain no original telegram, but is not an all-clear.
            if (p["cancelled"]?.ToString() == "true") head["InfoType"] = "取消";
            if (head["InfoType"]?.ToString() == "取消" && head["ReportDateTime"] is null)
                head["ReportDateTime"] = p["receivedAt"]?.DeepClone();
            foreach (string key in new[] { "mode", "status", "available", "reason", "expired", "highestSerial" })
                if (p[key] is not null) body[key] = p[key]!.DeepClone();
            // Contradictory inner mode is never promoted to live by outer metadata.
            if ((p["payload"] as JsonObject)?["mode"] is { } inner && inner.ToString() != "live") body["mode"] = inner.DeepClone();
            p = body;
        }
        else
        {
            if (p["hypocenter"] is JsonObject h)
            {
                bool eew = type.StartsWith("eew", StringComparison.Ordinal);
                p[eew ? "hypo" : "hypocenter"] = h["name"]?.DeepClone();
                p[eew ? "mag" : "magnitude"] = h["magnitude"]?.DeepClone();
                p[eew ? "depth" : "depthKm"] = h["depthKm"]?.DeepClone();
                p["lat"] = h["latitude"]?.DeepClone();
                p["lon"] = h["longitude"]?.DeepClone();
            }
            if (p["maxIntensity"] is not null) p["maxInt"] = p["maxIntensity"]!.DeepClone();
            if (type.StartsWith("eew", StringComparison.Ordinal))
            {
                if (p["reportTime"] is not null) p["time"] = p["reportTime"]!.DeepClone();
                if (p["warningAreas"] is JsonArray areas)
                    p["warnAreas"] = new JsonArray(areas.OfType<JsonObject>().Select(a => a["name"]?.DeepClone()).ToArray());
                if (p["cancelled"] is not null) p["isCancel"] = p["cancelled"]!.DeepClone();
            }
            if (p["cancelled"]?.ToString() == "true" && p["reportTime"] is null)
                p["reportTime"] = p["receivedAt"]?.DeepClone();
        }
        return JsonSerializer.SerializeToElement(p);
    }
}
