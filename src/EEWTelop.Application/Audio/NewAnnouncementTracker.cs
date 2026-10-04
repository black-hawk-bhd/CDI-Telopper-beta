using System.Globalization;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Audio;

// Access is serialized by AudioPolicy. State is session-local, never persisted
// as proof that an alert is currently active, and separated by source mode.
internal sealed class NewAnnouncementTracker
{
    internal const string QuakeMaximumKey = "maximum";
    private const int MaximumEntries = 1024;
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _stateOrder = new();
    private readonly Dictionary<ReportKey, Report> _reports = [];
    private readonly Queue<ReportKey> _reportOrder = new();

    public void Observe(DisasterEvent item)
    {
        if (item.Kind is not (EventKind.Quake or EventKind.Tsunami or EventKind.WeatherWarning))
            return;
        ReportKey reportKey = Key(item);
        if (_reports.ContainsKey(reportKey)) return;
        AnnouncementChange change = item.IsExpired ? AnnouncementChange.None : item switch
        {
            QuakeEvent quake => ObserveQuake(quake),
            TsunamiEvent tsunami => ObserveTsunami(tsunami),
            WeatherWarningEvent weather => ObserveWeather(weather),
            _ => AnnouncementChange.None,
        };
        _reports.Add(reportKey, new Report(change));
        _reportOrder.Enqueue(reportKey);
        while (_reports.Count > MaximumEntries) _reports.Remove(_reportOrder.Dequeue());
    }

    public AnnouncementChange Take(DisasterEvent item)
    {
        Observe(item);
        if (!_reports.TryGetValue(Key(item), out Report? report) || report.Taken)
            return AnnouncementChange.None;
        report.Taken = true;
        return report.Change;
    }

    private AnnouncementChange ObserveQuake(QuakeEvent quake)
    {
        string identity = quake.Earthquake.OriginTimeIsKnown
            ? quake.Earthquake.OriginTime.UtcTicks.ToString(CultureInfo.InvariantCulture)
            : quake.Id.Value;
        State state = GetState($"{quake.SourceMode}:quake:{identity}");
        if (quake.IssuedAt < state.IssuedAt) return AnnouncementChange.None;
        if (quake.IsCancelled)
            return Update(state, quake, [], [], eligible: false);
        var current = new Dictionary<string, int>(StringComparer.Ordinal);
        int maximum = QuakeRank(quake.Earthquake.MaximumScale);
        foreach (QuakePoint point in quake.Points)
        {
            int rank = QuakeRank(point.Scale);
            maximum = Math.Max(maximum, rank);
            if (rank >= (int)JmaScale.Three)
            {
                string key = AreaKey(point);
                current[key] = Math.Max(current.GetValueOrDefault(key), rank);
            }
        }
        // A hypocenter-only update must not end an existing earthquake episode.
        if (maximum <= 0) return AnnouncementChange.None;
        current[QuakeMaximumKey] = maximum;
        return Update(state, quake, current, NovelKeys(state, current), eligible: !quake.IsCorrection);
    }

    private AnnouncementChange ObserveTsunami(TsunamiEvent tsunami)
    {
        State state = GetState($"{tsunami.SourceMode}:tsunami");
        if (tsunami.IssuedAt < state.IssuedAt) return AnnouncementChange.None;
        bool observation = tsunami.Issue.RawType is "VTSE51" or "VTSE52" ||
            (tsunami.Areas.Count > 0 && tsunami.Areas.All(area => area.Role != TsunamiInformationRole.ForecastArea));
        // Observation cancellations concern observations, not the warning.
        if (observation && tsunami.IsCancelled) return AnnouncementChange.None;
        Dictionary<string, int> current = tsunami.IsCancelled ? [] : tsunami.Areas
            .Where(area => area.Role == TsunamiInformationRole.ForecastArea && TsunamiRank(area.Grade) > 0)
            .GroupBy(AreaKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(area => TsunamiRank(area.Grade)), StringComparer.Ordinal);
        if (observation)
        {
            // Baseline currently active grades without announcing them as new.
            foreach ((string key, int rank) in current) state.Levels.TryAdd(key, rank);
            return AnnouncementChange.None;
        }
        return Update(state, tsunami, current, NovelKeys(state, current), eligible: !tsunami.IsCorrection &&
            tsunami.Issue.Correction is CorrectionType.None or CorrectionType.Unknown && !tsunami.IsCancelled);
    }

    private AnnouncementChange ObserveWeather(WeatherWarningEvent weather)
    {
        string source = string.IsNullOrWhiteSpace(weather.Issue.Source) ? weather.Id.Value : weather.Issue.Source.Trim();
        string identity = weather.InformationType is WeatherInformationType.DisasterPreventionBulletin or
            WeatherInformationType.RecordShortDurationHeavyRain or WeatherInformationType.TornadoAdvisory
            ? ":" + weather.Id.Value : string.Empty;
        State state = GetState($"{weather.SourceMode}:weather:{weather.InformationType}:{source}{identity}");
        if (weather.IssuedAt < state.IssuedAt) return AnnouncementChange.None;
        if (state.ValidUntil is { } expiry && weather.IssuedAt > expiry) state.Levels.Clear();
        Dictionary<string, int> current = weather.IsCancelled ? [] : weather.Items
            .Where(item => item.IsActive)
            .GroupBy(AreaKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => (int)item.Level), StringComparer.Ordinal);
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (WeatherWarningItem item in weather.Items.Where(item => item.IsActive))
        {
            string key = AreaKey(item);
            string status = item.Status.Trim();
            bool continued = status.Contains("継続", StringComparison.Ordinal) ||
                status.Contains("解除", StringComparison.Ordinal) || status.Contains("発表中", StringComparison.Ordinal);
            if (continued) continue;
            bool issued = status.Contains("発表", StringComparison.Ordinal) &&
                !status.Contains("更新", StringComparison.Ordinal);
            bool escalated = state.Levels.TryGetValue(key, out int prior) && (int)item.Level > prior;
            if ((issued && !state.Levels.ContainsKey(key)) || escalated) candidates.Add(key);
        }
        if (weather.InformationType == WeatherInformationType.DisasterPreventionBulletin && state.Levels.Count == 0)
            candidates.UnionWith(current.Keys);
        AnnouncementChange change = Update(state, weather, current, candidates, eligible: !weather.IsCorrection && !weather.IsCancelled);
        state.ValidUntil = weather.ValidUntil;
        return change;
    }

    private static AnnouncementChange Update(State state, DisasterEvent item,
        Dictionary<string, int> current, HashSet<string> candidates, bool eligible)
    {
        bool first = state.Levels.Count == 0 && current.Count > 0 && candidates.Count > 0;
        state.Levels = current;
        state.IssuedAt = item.IssuedAt;
        return eligible && candidates.Count > 0 ? new(first, candidates) : AnnouncementChange.None;
    }

    private static HashSet<string> NovelKeys(State state, Dictionary<string, int> current) =>
        current.Where(pair => !state.Levels.TryGetValue(pair.Key, out int prior) || pair.Value > prior)
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

    private State GetState(string key)
    {
        if (_states.TryGetValue(key, out State? state))
        {
            _stateOrder.Remove(state.Node);
            _stateOrder.AddLast(state.Node);
            return state;
        }
        state = new State(_stateOrder.AddLast(key));
        _states.Add(key, state);
        while (_states.Count > MaximumEntries)
        {
            _states.Remove(_stateOrder.First!.Value);
            _stateOrder.RemoveFirst();
        }
        return state;
    }

    internal static string AreaKey(QuakePoint point) => "region:" + point.Prefecture + ":" +
        (point.MunicipalityCode.Length > 0 ? point.MunicipalityCode : point.DisplayName);
    internal static string AreaKey(TsunamiArea area) => area.Name.Trim();
    internal static string AreaKey(WeatherWarningItem item) =>
        (item.AreaCode.Length > 0 ? item.AreaCode : item.AreaName) + ":" +
        // Names remain stable when a warning's level/code is reorganized.
        item.KindName.Replace("特別警報", string.Empty, StringComparison.Ordinal)
            .Replace("危険警報", string.Empty, StringComparison.Ordinal)
            .Replace("警報", string.Empty, StringComparison.Ordinal)
            .Replace("注意報", string.Empty, StringComparison.Ordinal).Trim();

    private static int QuakeRank(JmaScale scale) => scale == JmaScale.FiveLowerOrMore
        ? (int)JmaScale.FiveLower // Conservative lower bound, not an observed 5-lower value.
        : (int)scale;
    private static int TsunamiRank(TsunamiGrade grade) => grade switch
    {
        TsunamiGrade.Forecast => 1, TsunamiGrade.Watch => 2,
        TsunamiGrade.Warning => 3, TsunamiGrade.MajorWarning => 4, _ => 0,
    };
    private static ReportKey Key(DisasterEvent item) => new(item.SourceMode, item.Kind,
        item.Provider, item.Id.Value, item.IssuedAt, item.Signature);
    private readonly record struct ReportKey(SourceMode Mode, EventKind Kind, string Provider,
        string Id, DateTimeOffset IssuedAt, string Signature);
    private sealed class State(LinkedListNode<string> node)
    {
        public LinkedListNode<string> Node { get; } = node;
        public Dictionary<string, int> Levels { get; set; } = new(StringComparer.Ordinal);
        public DateTimeOffset IssuedAt { get; set; }
        public DateTimeOffset? ValidUntil { get; set; }
    }
    private sealed class Report(AnnouncementChange change)
    {
        public AnnouncementChange Change { get; } = change;
        public bool Taken { get; set; }
    }
}

internal sealed record AnnouncementChange(bool IsFirst, IReadOnlySet<string> AreaKeys)
{
    public static AnnouncementChange None { get; } = new(false, new HashSet<string>(StringComparer.Ordinal));
}
