using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

public interface IEventVersionCache
{
    bool TryAccept(DisasterEvent disasterEvent);

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
    private readonly Dictionary<string, int> _highestEewSerials = new(StringComparer.Ordinal);
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

    public bool TryAccept(DisasterEvent disasterEvent)
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
            // Forecast and observation have independent report-number sequences.
            string serialScope = disasterEvent is TsunamiEvent tsunami ? tsunami.Issue.RawType : string.Empty;
            if (serial is int reportNumber &&
                entry.HighestSerials.TryGetValue(serialScope, out int highestSerial) &&
                reportNumber < highestSerial)
            {
                return false;
            }

            if (disasterEvent is EewEvent eew &&
                !TryAcceptEewProvider(eew, serial))
            {
                return false;
            }

            if (!entry.SignatureSet.Add(disasterEvent.Signature))
            {
                return false;
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

            return true;
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
            _highestEewSerials.Clear();
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
            }
        }
    }

    private bool TryAcceptEewProvider(EewEvent eew, int? serial)
    {
        if (serial is not int reportNumber)
        {
            return true;
        }

        // A training report must not suppress a live report or advance its serial.
        string eventId = string.Join('\u001f', eew.SourceMode.ToString(),
            eew.IsTest.ToString(), eew.Id.Value);
        if (_highestEewSerials.TryGetValue(eventId, out int highestSerial) &&
            reportNumber < highestSerial)
        {
            return false;
        }

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

        if (!_highestEewSerials.ContainsKey(eventId))
        {
            _recentEewEventIds.Enqueue(eventId);
        }

        _highestEewSerials[eventId] = Math.Max(highestSerial, reportNumber);
        _eewReportProviders.TryAdd(reportKey, eew.Provider);
        while (_highestEewSerials.Count > _keyLimit)
        {
            string oldestEventId = _recentEewEventIds.Dequeue();
            _highestEewSerials.Remove(oldestEventId);
            foreach (string key in _eewReportProviders.Keys
                         .Where(key => key.StartsWith(oldestEventId + "|", StringComparison.Ordinal))
                         .ToArray())
            {
                _eewReportProviders.Remove(key);
            }
        }

        return true;
    }

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
