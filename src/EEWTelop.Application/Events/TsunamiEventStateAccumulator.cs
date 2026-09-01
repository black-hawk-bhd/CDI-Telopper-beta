using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

/// <summary>
/// Keeps the complementary VTSE41, VTSE51, and VTSE52 sections together
/// without allowing a detail telegram to erase the active warning forecast.
/// </summary>
public sealed class TsunamiEventStateAccumulator
{
    private const int MaximumStates = 32;
    private readonly object _gate = new();
    private readonly IEventSignatureBuilder _signatureBuilder;
    private readonly Dictionary<string, StateEntry> _states = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recentKeys = [];

    public TsunamiEventStateAccumulator(IEventSignatureBuilder? signatureBuilder = null)
    {
        _signatureBuilder = signatureBuilder ?? new EventSignatureBuilder();
    }

    public TsunamiEvent Merge(TsunamiEvent incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        string key = BuildKey(incoming);
        lock (_gate)
        {
            if (incoming.IsCancelled)
            {
                Remove(key);
                return incoming;
            }

            if (!_states.TryGetValue(key, out StateEntry? entry))
            {
                entry = new StateEntry(_recentKeys.AddLast(key));
                _states.Add(key, entry);
                TrimStates();
            }
            else
            {
                _recentKeys.Remove(entry.KeyNode);
                _recentKeys.AddLast(entry.KeyNode);
            }

            ApplyIncoming(entry.Areas, incoming);
            if (incoming.ObservationAsOf is { } observationAsOf)
            {
                entry.ObservationAsOf = observationAsOf;
            }

            TsunamiArea[] mergedAreas = entry.Areas.Values
                .OrderBy(static area => area.Role)
                .ToArray();
            var merged = new TsunamiEvent(
                incoming.Id,
                incoming.Provider,
                incoming.IssuedAt,
                incoming.ReceivedAt,
                signature: string.Empty,
                incoming.SourceMode,
                incoming.Issue,
                mergedAreas,
                isCancelled: false,
                incoming.ExpireAt,
                entry.ObservationAsOf)
            {
                // This is a property of the telegram currently being rendered.
                // Do not retain it for later VTSE51/VTSE52 observation updates.
                WarningStateChanged = incoming.WarningStateChanged,
            };
            return merged with { Signature = _signatureBuilder.Build(merged) };
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _states.Clear();
            _recentKeys.Clear();
        }
    }

    private static void ApplyIncoming(
        Dictionary<string, TsunamiArea> target,
        TsunamiEvent incoming)
    {
        TsunamiArea[] areas = incoming.Areas.ToArray();
        switch (incoming.Issue.RawType)
        {
            case "VTSE41":
                ReplaceRole(target, areas, TsunamiInformationRole.ForecastArea);
                break;
            case "VTSE51":
                AddMissingRole(target, areas, TsunamiInformationRole.ForecastArea);
                ReplaceRoleWhenPresent(target, areas, TsunamiInformationRole.StationForecast);
                ReplaceRoleWhenPresent(target, areas, TsunamiInformationRole.CoastalObservation);
                break;
            case "VTSE52":
                ReplaceRoleWhenPresent(target, areas, TsunamiInformationRole.OffshoreObservation);
                break;
            default:
                target.Clear();
                foreach (TsunamiArea area in areas)
                {
                    target[BuildAreaKey(area)] = area;
                }

                break;
        }
    }

    private static void ReplaceRole(
        Dictionary<string, TsunamiArea> target,
        IEnumerable<TsunamiArea> source,
        TsunamiInformationRole role)
    {
        RemoveRole(target, role);
        UpsertRole(target, source, role);
    }

    private static void ReplaceRoleWhenPresent(
        Dictionary<string, TsunamiArea> target,
        IReadOnlyCollection<TsunamiArea> source,
        TsunamiInformationRole role)
    {
        if (source.Any(area => area.Role == role))
        {
            ReplaceRole(target, source, role);
        }
    }

    private static void UpsertRole(
        Dictionary<string, TsunamiArea> target,
        IEnumerable<TsunamiArea> source,
        TsunamiInformationRole role)
    {
        foreach (TsunamiArea area in source.Where(area => area.Role == role))
        {
            target[BuildAreaKey(area)] = area;
        }
    }

    private static void AddMissingRole(
        Dictionary<string, TsunamiArea> target,
        IEnumerable<TsunamiArea> source,
        TsunamiInformationRole role)
    {
        foreach (TsunamiArea area in source.Where(area => area.Role == role))
        {
            target.TryAdd(BuildAreaKey(area), area);
        }
    }

    private static void RemoveRole(
        Dictionary<string, TsunamiArea> target,
        TsunamiInformationRole role)
    {
        foreach (string key in target
                     .Where(pair => pair.Value.Role == role)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            target.Remove(key);
        }
    }

    private static string BuildAreaKey(TsunamiArea area) => string.Join(
        '\u001f',
        ((int)area.Role).ToString(System.Globalization.CultureInfo.InvariantCulture),
        area.ParentAreaName,
        area.Name);

    private static string BuildKey(TsunamiEvent tsunami) => string.Join(
        '\u001f',
        tsunami.Provider,
        ((int)tsunami.SourceMode).ToString(System.Globalization.CultureInfo.InvariantCulture),
        tsunami.Id.Value);

    private void Remove(string key)
    {
        if (_states.Remove(key, out StateEntry? entry))
        {
            _recentKeys.Remove(entry.KeyNode);
        }
    }

    private void TrimStates()
    {
        while (_states.Count > MaximumStates && _recentKeys.First is { } oldest)
        {
            _recentKeys.RemoveFirst();
            _states.Remove(oldest.Value);
        }
    }

    private sealed class StateEntry(LinkedListNode<string> keyNode)
    {
        public LinkedListNode<string> KeyNode { get; } = keyNode;

        public Dictionary<string, TsunamiArea> Areas { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset? ObservationAsOf { get; set; }
    }
}
