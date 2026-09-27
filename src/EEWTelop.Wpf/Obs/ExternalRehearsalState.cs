using System.Text.Json;
using System.Text.Json.Nodes;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Obs;

// A separate store, never a switch on the production store or its source metadata.
internal sealed class ExternalRehearsalState
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private ExternalApiState _tsunami = new(true);
    private ExternalEarthquakeState _earthquake = new(true);
    private readonly Dictionary<EventKind, DateTimeOffset> _accepted = [];
    private string _session = Guid.NewGuid().ToString("N");
    private string _source = "unknownTraining";
    private string _health = "disabled";
    private bool _active;
    private long _revision;

    internal string Begin(string source)
    {
        lock (_gate)
        {
            _session = Guid.NewGuid().ToString("N");
            _source = source;
            _active = true;
            _health = source == "simulator" ? "connecting" : "healthy";
            _tsunami = new(true);
            _earthquake = new(true);
            _accepted.Clear();
            _revision++;
            return _session;
        }
    }

    internal void SetHealth(string session, string health)
    {
        lock (_gate)
        {
            if (session != _session || !_active) return;
            if (_health == health) return;
            _health = health;
            _revision++;
        }
    }

    internal void End(string? session = null)
    {
        lock (_gate)
        {
            if (session is not null && session != _session) return;
            _active = false;
            _health = "disabled";
            _revision++;
        }
    }

    internal void ReplaceSnapshot(string session, IReadOnlyList<DisasterEvent> events)
    {
        lock (_gate)
        {
            if (session != _session || !_active) return;
            _tsunami = new(true);
            _earthquake = new(true);
            _accepted.Clear();
            foreach (var item in events) Observe(item, session);
            _health = "healthy";
            _revision++;
        }
    }

    internal void Observe(DisasterEvent item, string? session = null, bool manualReplay = false)
    {
        if (item.Kind is not (EventKind.Quake or EventKind.Eew or EventKind.Tsunami) ||
            (!manualReplay && !ExternalChannelSource.Accepts(item, true))) return;
        lock (_gate)
        {
            if (session is not null && (session != _session || !_active)) return;
            string source = manualReplay ? "manualReplay" : ExternalChannelSource.Mode(item);
            if (session is null && (!_active || source != _source)) Begin(source);
            var result = new EventIngestionResult(EventIngestionStatus.Accepted, item, null, null, []);
            _tsunami.Observe(result, manualReplay);
            _earthquake.Observe(result, manualReplay);
            _accepted[item.Kind] = item.ReceivedAt;
            _health = "healthy";
            _revision++;
        }
    }

    internal JsonObject Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            ExternalDomainHealth Health(EventKind kind) => new(_active, _health,
                _accepted.TryGetValue(kind, out var time) ? time : null);
            var result = JsonSerializer.SerializeToNode(new
            {
                rehearsalSessionId = _session,
                rehearsal = new { active = _active, sourceMode = _source },
                dataHealth = new { tsunami = Health(EventKind.Tsunami), earthquake = Health(EventKind.Quake), eew = Health(EventKind.Eew) },
                tsunami = _tsunami.Read(now), earthquake = _earthquake.ReadQuake(), eew = _earthquake.ReadEew(now),
            }, JsonOptions)!.AsObject();
            foreach (string resource in new[] { "tsunami", "earthquake", "eew" })
                result[resource]!["revision"] = _revision;
            MarkTraining(result);
            return result;
        }
    }

    internal void UpdateSandboxHealth(ProviderConnectionState? state)
    {
        lock (_gate)
        {
            if (!_active || _source != "sandbox") return;
            SetHealth(_session, state switch
            {
                ProviderConnectionState.Connected => "healthy",
                ProviderConnectionState.Faulted => "failed",
                ProviderConnectionState.Stopped or ProviderConnectionState.Stale or ProviderConnectionState.Reconnecting => "stale",
                ProviderConnectionState.Connecting => "connecting",
                _ => "unknown",
            });
        }
    }

    private static void MarkTraining(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            // Only explicit manual replay can enter this store with a production source.
            if (obj["sourceMode"]?.GetValue<string>() == "production") obj["sourceMode"] = "manualReplay";
            foreach (var property in obj) MarkTraining(property.Value);
        }
        else if (node is JsonArray array)
            foreach (var item in array) MarkTraining(item);
    }
}
