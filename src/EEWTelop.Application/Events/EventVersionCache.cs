using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

public interface IEventVersionCache
{
    bool TryAccept(DisasterEvent disasterEvent);

    EventVersionDecision CheckAndAccept(DisasterEvent disasterEvent) =>
        new(TryAccept(disasterEvent) ? EventVersionDisposition.Accepted : EventVersionDisposition.Duplicate);

    IReadOnlyList<StoredEventSignature> GetSnapshot();

    void Restore(IEnumerable<StoredEventSignature> signatures);
}

public sealed record StoredEventSignature(
    string Provider,
    EventKind Kind,
    string EventId,
    string Signature)
{
    // Preserve the legacy production interpretation when a state document omits this field.
    public SourceMode SourceMode { get; init; } = SourceMode.Production;

    public bool IsTest { get; init; }

    public EewOrderingState? EewOrdering { get; init; }
}

public sealed class EventVersionCache : IEventVersionCache
{
    public const int DefaultKeyLimit = 500;
    public const int DefaultVersionLimit = 20;

    private readonly object _gate = new();
    private readonly int _keyLimit;
    private readonly int _versionLimit;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recentKeys = [];
    private readonly Dictionary<string, string> _eewReportProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EewOrderingState> _eewOrdering = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentEewEventIds = [];

    public EventVersionCache(
        int keyLimit = DefaultKeyLimit,
        int versionLimit = DefaultVersionLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keyLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(versionLimit, 1);
        _keyLimit = keyLimit;
        _versionLimit = versionLimit;
    }

    public bool TryAccept(DisasterEvent disasterEvent) => CheckAndAccept(disasterEvent).Accepted;

    public EventVersionDecision CheckAndAccept(DisasterEvent disasterEvent)
    {
        ArgumentNullException.ThrowIfNull(disasterEvent);
        string key = BuildKey(disasterEvent);

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out CacheEntry? entry))
            {
                LinkedListNode<string> keyNode = _recentKeys.AddLast(key);
                entry = new CacheEntry(keyNode);
                _entries.Add(key, entry);
                TrimKeys();
            }
            else
            {
                _recentKeys.Remove(entry.KeyNode);
                _recentKeys.AddLast(entry.KeyNode);
            }

            int? serial = GetSerialNumber(disasterEvent);
            EventVersionDecision decision = new(EventVersionDisposition.Accepted);
            if (disasterEvent is EewEvent orderedEew)
            {
                _eewOrdering.TryGetValue(BuildEewKey(orderedEew), out EewOrderingState? previous);
                decision = EewOrderingPolicy.Check(previous, orderedEew, serial);
                if (!decision.Accepted) return decision;
            }
            // Forecast and observation have independent report-number sequences.
            string serialScope = disasterEvent is TsunamiEvent tsunami ? tsunami.Issue.RawType : string.Empty;
            if (disasterEvent is not EewEvent && serial is int reportNumber &&
                entry.HighestSerials.TryGetValue(serialScope, out int highestSerial) &&
                reportNumber < highestSerial)
            {
                return new(EventVersionDisposition.Stale);
            }

            if (disasterEvent is EewEvent eew &&
                !TryAcceptEewProvider(eew, serial))
            {
                return new(EventVersionDisposition.Duplicate);
            }

            if (!entry.SignatureSet.Add(disasterEvent.Signature))
            {
                return new(EventVersionDisposition.Duplicate);
            }

            if (serial is int acceptedSerial &&
                (!entry.HighestSerials.TryGetValue(serialScope, out int currentSerial) || acceptedSerial > currentSerial))
            {
                entry.HighestSerials[serialScope] = acceptedSerial;
            }

            entry.Signatures.AddLast(disasterEvent.Signature);
            while (entry.Signatures.Count > _versionLimit)
            {
                string oldestSignature = entry.Signatures.First!.Value;
                entry.Signatures.RemoveFirst();
                entry.SignatureSet.Remove(oldestSignature);
            }

            if (disasterEvent is EewEvent acceptedEew) RememberEew(acceptedEew, serial);
            return decision;
        }
    }

    public IReadOnlyList<StoredEventSignature> GetSnapshot()
    {
        lock (_gate)
        {
            var result = new List<StoredEventSignature>();
            foreach (string key in _recentKeys)
            {
                string[] parts = key.Split('\u001f');
                if (parts.Length != 5 || !Enum.TryParse(parts[1], out EventKind kind) ||
                    !Enum.TryParse(parts[3], out SourceMode sourceMode) ||
                    !bool.TryParse(parts[4], out bool isTest))
                {
                    continue;
                }

                CacheEntry entry = _entries[key];
                result.AddRange(entry.Signatures.Select(signature =>
                    new StoredEventSignature(parts[0], kind, parts[2], signature)
                    {
                        SourceMode = sourceMode,
                        IsTest = isTest,
                        EewOrdering = kind == EventKind.Eew && signature == entry.Signatures.Last?.Value
                            ? _eewOrdering.GetValueOrDefault(BuildEewKey(sourceMode, isTest, parts[2])) : null,
                    }));
            }

            return result;
        }
    }

    public void Restore(IEnumerable<StoredEventSignature> signatures)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        lock (_gate)
        {
            _entries.Clear();
            _recentKeys.Clear();
            _eewReportProviders.Clear();
            _eewOrdering.Clear();
            _recentEewEventIds.Clear();
            foreach (StoredEventSignature item in signatures)
            {
                if (string.IsNullOrWhiteSpace(item.Provider) ||
                    string.IsNullOrWhiteSpace(item.EventId) ||
                    string.IsNullOrWhiteSpace(item.Signature) ||
                    !Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.SourceMode))
                {
                    continue;
                }

                string key = BuildKey(item.Provider, item.Kind, item.EventId, item.SourceMode,
                    item.Kind == EventKind.Eew && item.IsTest);
                if (!_entries.TryGetValue(key, out CacheEntry? entry))
                {
                    LinkedListNode<string> node = _recentKeys.AddLast(key);
                    entry = new CacheEntry(node);
                    _entries.Add(key, entry);
                    TrimKeys();
                }

                if (entry.SignatureSet.Add(item.Signature))
                {
                    entry.Signatures.AddLast(item.Signature);
                    while (entry.Signatures.Count > _versionLimit)
                    {
                        string oldest = entry.Signatures.First!.Value;
                        entry.Signatures.RemoveFirst();
                        entry.SignatureSet.Remove(oldest);
                    }
                }
                if (item.Kind == EventKind.Eew && item.EewOrdering is { } ordering &&
                    ordering.HighestSerial is not < 0 && ordering.IssuedAt != DateTimeOffset.MinValue &&
                    ordering.SourceMessageTime != DateTimeOffset.MinValue)
                {
                    string eewKey = BuildEewKey(item.SourceMode, item.IsTest, item.EventId);
                    if (!_eewOrdering.TryGetValue(eewKey, out EewOrderingState? previous) ||
                        (ordering.SourceMessageTime ?? ordering.IssuedAt) > (previous.SourceMessageTime ?? previous.IssuedAt) ||
                        (ordering.SourceMessageTime ?? ordering.IssuedAt) == (previous.SourceMessageTime ?? previous.IssuedAt) && ordering.IsCancelled)
                    {
                        if (previous is null) _recentEewEventIds.Enqueue(eewKey);
                        _eewOrdering[eewKey] = ordering;
                        TrimEewStates();
                    }
                }
            }
        }
    }

    private bool TryAcceptEewProvider(EewEvent eew, int? serial)
    {
        if (serial is not int reportNumber)
        {
            return true;
        }

        string eventId = BuildEewKey(eew);

        string reportKey = string.Join(
            '|',
            eventId,
            reportNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            eew.IsCancelled ? "cancel" : "issue",
            eew.IsTest ? "test" : "normal");
        if (_eewReportProviders.TryGetValue(reportKey, out string? firstProvider) &&
            !firstProvider.Equals(eew.Provider, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void RememberEew(EewEvent eew, int? serial)
    {
        string key = BuildEewKey(eew);
        if (!_eewOrdering.TryGetValue(key, out EewOrderingState? previous))
            _recentEewEventIds.Enqueue(key);
        int? highest = previous?.HighestSerial;
        if (serial.HasValue && (!highest.HasValue || serial > highest)) highest = serial;
        _eewOrdering[key] = new(highest, eew.SourceMessageTime, eew.IssuedAt, eew.IsCancelled);
        if (serial is int number)
        {
            string reportKey = string.Join('|', key,
                number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                eew.IsCancelled ? "cancel" : "issue", eew.IsTest ? "test" : "normal");
            _eewReportProviders.TryAdd(reportKey, eew.Provider);
        }
        TrimEewStates();
    }

    private void TrimEewStates()
    {
        while (_eewOrdering.Count > _keyLimit)
        {
            string oldestEventId = _recentEewEventIds.Dequeue();
            _eewOrdering.Remove(oldestEventId);
            foreach (string key in _eewReportProviders.Keys
                         .Where(key => key.StartsWith(oldestEventId + "|", StringComparison.Ordinal))
                         .ToArray())
            {
                _eewReportProviders.Remove(key);
            }
        }

    }

    private static string BuildEewKey(EewEvent eew) => BuildEewKey(eew.SourceMode, eew.IsTest, eew.Id.Value);

    private static string BuildEewKey(SourceMode mode, bool isTest, string eventId) =>
        string.Join('\u001f', mode.ToString(), isTest.ToString(), eventId);

    private static string BuildKey(DisasterEvent disasterEvent) => BuildKey(
        disasterEvent.Provider,
        disasterEvent.Kind,
        disasterEvent.Id.Value,
        disasterEvent.SourceMode,
        disasterEvent is EewEvent { IsTest: true });

    private static string BuildKey(
        string provider,
        EventKind kind,
        string eventId,
        SourceMode sourceMode,
        bool isTest) => string.Join(
        '\u001f',
        provider,
        kind.ToString(),
        eventId,
        sourceMode.ToString(),
        isTest.ToString());

    private static int? GetSerialNumber(DisasterEvent disasterEvent)
    {
        string? serial = disasterEvent switch
        {
            QuakeEvent quake => quake.Issue.Serial,
            TsunamiEvent tsunami => tsunami.Issue.Serial,
            WeatherWarningEvent weather => weather.Issue.Serial,
            VolcanoEvent volcano => volcano.Issue.Serial,
            EewEvent eew => eew.Issue.Serial,
            _ => null,
        };
        return int.TryParse(serial, out int value) && value >= 0 ? value : null;
    }

    private void TrimKeys()
    {
        while (_entries.Count > _keyLimit)
        {
            LinkedListNode<string> oldest = _recentKeys.First!;
            _recentKeys.RemoveFirst();
            _entries.Remove(oldest.Value);
        }
    }

    private sealed class CacheEntry(LinkedListNode<string> keyNode)
    {
        public LinkedListNode<string> KeyNode { get; } = keyNode;

        public LinkedList<string> Signatures { get; } = [];

        public HashSet<string> SignatureSet { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> HighestSerials { get; } = new(StringComparer.Ordinal);
    }
}
