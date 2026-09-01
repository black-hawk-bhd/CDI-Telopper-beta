using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Coordination;

/// <summary>
/// Keeps only currently relevant production programs for on-air rotation.
/// This is intentionally separate from history rehearsal: old reports are
/// never admitted and audio is not replayed unless the operator opts in.
/// </summary>
public sealed class ProductionReplayCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<ProductionReplayKey, Entry> _entries = [];
    private readonly Dictionary<ProductionReplayKey, CompletedEntry> _completedEntries = [];
    private ProductionReplayKey? _lastSelected;
    private long _nextSequence;

    public void Update(
        DisasterEvent disasterEvent,
        DisplayProgram? program,
        ProductionReplaySettings settings,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(disasterEvent);
        ArgumentNullException.ThrowIfNull(settings);

        if (disasterEvent.SourceMode != SourceMode.Production)
        {
            return;
        }

        var key = new ProductionReplayKey(disasterEvent.Kind, disasterEvent.Id.Value);
        lock (_gate)
        {
            RemoveExpiredAndDisabled(settings, nowUtc);
            if (IsTerminal(disasterEvent))
            {
                if (disasterEvent.Kind == EventKind.Tsunami)
                {
                    RemoveKindCore(EventKind.Tsunami);
                }
                else
                {
                    _entries.Remove(key);
                    _completedEntries.Remove(key);
                }

                ResetCursorIfMissing();
                return;
            }

            ProductionReplayPolicy policy = settings.GetPolicy(disasterEvent.Kind);
            if (!policy.Enabled || program is null || program.Pages.Count == 0)
            {
                _entries.Remove(key);
                _completedEntries.Remove(key);
                ResetCursorIfMissing();
                return;
            }

            DateTimeOffset? expiresAtUtc = GetExpiresAtUtc(disasterEvent);
            if (expiresAtUtc is DateTimeOffset expiry && expiry <= nowUtc)
            {
                _entries.Remove(key);
                _completedEntries.Remove(key);
                ResetCursorIfMissing();
                return;
            }

            if (_entries.TryGetValue(key, out Entry? existing) &&
                disasterEvent.IssuedAt < existing.Event.IssuedAt)
            {
                return;
            }

            if (_completedEntries.TryGetValue(key, out CompletedEntry? completed))
            {
                // The same JMA telegram can arrive from more than one provider. Its
                // provider-specific signature may differ even though it is the same
                // issued version. Do not reopen a completed replay unless a strictly
                // newer version of the event was issued.
                if (disasterEvent.IssuedAt <= completed.IssuedAt)
                {
                    return;
                }

                _completedEntries.Remove(key);
            }

            long sequence = existing?.Sequence ?? ++_nextSequence;
            int replayCount = existing is not null &&
                disasterEvent.IssuedAt <= existing.Event.IssuedAt
                    ? existing.ReplayCount
                    : 0;
            _entries[key] = new Entry(
                key,
                disasterEvent,
                program,
                expiresAtUtc,
                replayCount,
                sequence);
        }
    }

    public ProductionReplaySelection? SelectNext(
        ProductionReplaySettings settings,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            RemoveExpiredAndDisabled(settings, nowUtc);
            Entry[] ordered = _entries.Values
                .OrderByDescending(static item => item.Program.Priority)
                .ThenByDescending(static item => item.Event.IssuedAt)
                .ThenBy(static item => item.Sequence)
                .ToArray();
            if (ordered.Length == 0)
            {
                _lastSelected = null;
                return null;
            }

            int selectedIndex = 0;
            if (_lastSelected is ProductionReplayKey cursor)
            {
                int cursorIndex = Array.FindIndex(
                    ordered,
                    item => item.Key == cursor);
                if (cursorIndex >= 0)
                {
                    selectedIndex = (cursorIndex + 1) % ordered.Length;
                }
            }

            Entry selected = ordered[selectedIndex];
            _entries[selected.Key] = selected with
            {
                ReplayCount = selected.ReplayCount + 1,
            };
            _lastSelected = selected.Key;
            ProductionReplayPolicy policy = settings.GetPolicy(selected.Event.Kind);
            return new ProductionReplaySelection(
                selected.Key.ToString(),
                selected.Event,
                selected.Program,
                policy.AudioOnEachCycle,
                ordered.Length);
        }
    }

    public void Prune(ProductionReplaySettings settings, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            RemoveExpiredAndDisabled(settings, nowUtc);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _completedEntries.Clear();
            _lastSelected = null;
        }
    }

    public void RemoveKind(EventKind kind)
    {
        lock (_gate)
        {
            RemoveKindCore(kind);
            ResetCursorIfMissing();
        }
    }

    private void RemoveExpiredAndDisabled(
        ProductionReplaySettings settings,
        DateTimeOffset nowUtc)
    {
        KeyValuePair<ProductionReplayKey, Entry>[] removed = _entries
            .Where(pair =>
                !settings.GetPolicy(pair.Key.Kind).Enabled ||
                pair.Value.ReplayCount >= settings.GetPolicy(pair.Key.Kind).RepeatCount ||
                pair.Value.ExpiresAtUtc is DateTimeOffset expiry && expiry <= nowUtc)
            .ToArray();
        foreach ((ProductionReplayKey key, Entry entry) in removed)
        {
            _entries.Remove(key);
            ProductionReplayPolicy policy = settings.GetPolicy(key.Kind);
            if (policy.Enabled && entry.ReplayCount >= policy.RepeatCount)
            {
                _completedEntries[key] = new CompletedEntry(
                    entry.Event.Signature,
                    entry.Event.IssuedAt);
            }
            else
            {
                _completedEntries.Remove(key);
            }
        }

        ResetCursorIfMissing();
    }

    private static DateTimeOffset? GetExpiresAtUtc(DisasterEvent disasterEvent)
    {
        return disasterEvent switch
        {
            TsunamiEvent tsunami => tsunami.ExpireAt,
            WeatherWarningEvent weather => weather.ValidUntil,
            _ => null,
        };
    }

    private static bool IsTerminal(DisasterEvent disasterEvent) =>
        disasterEvent.IsCancelled ||
        disasterEvent is WeatherWarningEvent weather &&
        weather.Items.Count > 0 &&
        weather.Items.All(static item => !item.IsActive);

    private void RemoveKindCore(EventKind kind)
    {
        ProductionReplayKey[] keys = _entries.Keys
            .Where(key => key.Kind == kind)
            .ToArray();
        foreach (ProductionReplayKey key in keys)
        {
            _entries.Remove(key);
        }

        ProductionReplayKey[] completedKeys = _completedEntries.Keys
            .Where(key => key.Kind == kind)
            .ToArray();
        foreach (ProductionReplayKey key in completedKeys)
        {
            _completedEntries.Remove(key);
        }
    }

    private void ResetCursorIfMissing()
    {
        if (_lastSelected is ProductionReplayKey key && !_entries.ContainsKey(key))
        {
            _lastSelected = null;
        }
    }

    private readonly record struct ProductionReplayKey(EventKind Kind, string EventId)
    {
        public override string ToString() => $"{Kind}:{EventId}";
    }

    private sealed record Entry(
        ProductionReplayKey Key,
        DisasterEvent Event,
        DisplayProgram Program,
        DateTimeOffset? ExpiresAtUtc,
        int ReplayCount,
        long Sequence);

    private sealed record CompletedEntry(
        string Signature,
        DateTimeOffset IssuedAt);
}

public sealed record ProductionReplaySelection(
    string Key,
    DisasterEvent Event,
    DisplayProgram Program,
    bool PlayAudio,
    int ActiveItemCount);
