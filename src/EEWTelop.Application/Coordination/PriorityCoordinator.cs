using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Coordination;

public sealed class PriorityCoordinator : IDisplayCoordinator
{
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly IPageClock _pageClock;
    private readonly Dictionary<EventKind, ScheduledProgram> _pendingByKind = [];
    private readonly Dictionary<EventId, ScheduledProgram> _pendingWeatherByEventId = [];
    private DisplaySettings _settings;
    private ScheduledProgram? _current;
    private ScheduledProgram? _persistentTsunami;
    private long _nextSequence;

    public PriorityCoordinator(IClock clock, DisplaySettings settings)
        : this(clock, settings, new PageClock())
    {
    }

    public PriorityCoordinator(IClock clock, DisplaySettings settings, IPageClock pageClock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pageClock);
        _clock = clock;
        _settings = settings;
        _pageClock = pageClock;
    }

    public CoordinatorSnapshot Apply(DisplayProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        lock (_gate)
        {
            EvaluateCurrentAndPromote(resynchronizeFromUtc: false);
            if (program.Pages.Count == 0)
            {
                return BuildSnapshot(
                    new CoordinatorDecision(
                        CoordinatorDecisionKind.IgnoredEmptyProgram,
                        "The display program has no pages.",
                        program.ProgramId),
                    rehearsalStopped: false,
                    resynchronizeFromUtc: false);
            }

            ScheduledProgram? existing = FindByEventId(program.EventId);
            if (existing is not null)
            {
                if (existing.Program.ProgramId == program.ProgramId)
                {
                    return BuildSnapshot(
                        new CoordinatorDecision(
                            CoordinatorDecisionKind.IgnoredDuplicate,
                            "An identical program is already active or queued.",
                            program.ProgramId),
                        rehearsalStopped: false,
                        resynchronizeFromUtc: false);
                }

                if (program.IssuedAt < existing.Program.IssuedAt)
                {
                    return BuildSnapshot(
                        new CoordinatorDecision(
                            CoordinatorDecisionKind.IgnoredOlderUpdate,
                            "An older update cannot replace a newer event version.",
                            existing.Program.ProgramId),
                        rehearsalStopped: false,
                        resynchronizeFromUtc: false);
                }
            }

            bool incomingProduction = IsProduction(program);
            if (!incomingProduction && HasProductionState())
            {
                return BuildSnapshot(
                    new CoordinatorDecision(
                        CoordinatorDecisionKind.IgnoredRehearsal,
                        "Rehearsal content is suppressed while production content is active.",
                        program.ProgramId),
                    rehearsalStopped: false,
                    resynchronizeFromUtc: false);
            }

            bool rehearsalStopped = incomingProduction && RemoveRehearsalState();
            var incoming = CreateScheduledProgram(program);

            if (program.Kind == EventKind.Tsunami &&
                program.Priority == OverlayPriority.TsunamiCancel)
            {
                _persistentTsunami = null;
                _pendingByKind.Remove(EventKind.Tsunami);
            }
            else if (program.Kind == EventKind.Tsunami)
            {
                _persistentTsunami = IsPersistentProductionTsunami(program)
                    ? incoming
                    : null;
            }

            bool replacedWaitingVersion = false;
            if (existing is not null && _current == existing)
            {
                ReplaceExisting(existing, incoming);
                return BuildSnapshot(
                    new CoordinatorDecision(
                        CoordinatorDecisionKind.Replaced,
                        "A newer version replaced the same event.",
                        existing.Program.ProgramId),
                    rehearsalStopped,
                    resynchronizeFromUtc: false);
            }

            if (existing is not null)
            {
                RemovePending(existing);

                if (_persistentTsunami == existing)
                {
                    _persistentTsunami = incoming;
                }

                replacedWaitingVersion = true;
            }

            if (_current is null)
            {
                _current = incoming;
                return BuildSnapshot(
                    new CoordinatorDecision(
                        CoordinatorDecisionKind.Activated,
                        "The program became active.",
                        program.ProgramId),
                    rehearsalStopped,
                    resynchronizeFromUtc: false);
            }

            if (_current.Program.Kind == EventKind.Tsunami && program.Kind == EventKind.Tsunami)
            {
                if (program.IssuedAt < _current.Program.IssuedAt)
                {
                    return BuildSnapshot(
                        new CoordinatorDecision(
                            CoordinatorDecisionKind.IgnoredOlderUpdate,
                            "An older tsunami program was ignored.",
                            program.ProgramId),
                        rehearsalStopped,
                        resynchronizeFromUtc: false);
                }

                string replacedProgramId = _current.Program.ProgramId;
                _current = incoming;
                return BuildSnapshot(
                    new CoordinatorDecision(
                        CoordinatorDecisionKind.Replaced,
                        "The current tsunami program was updated.",
                        replacedProgramId),
                    rehearsalStopped,
                    resynchronizeFromUtc: false);
            }

            int priorityComparison = program.Priority.CompareTo(_current.Program.Priority);
            if (program.Kind == EventKind.WeatherWarning &&
                _current.Program.Kind == EventKind.WeatherWarning &&
                priorityComparison == 0)
            {
                return BuildSnapshot(
                    Queue(incoming),
                    rehearsalStopped,
                    resynchronizeFromUtc: false);
            }

            if (priorityComparison > 0 ||
                (priorityComparison == 0 && program.IssuedAt >= _current.Program.IssuedAt))
            {
                string preemptedProgramId = _current.Program.ProgramId;
                if (_current.Program.Kind == EventKind.WeatherWarning)
                {
                    Queue(_current);
                }

                _current = incoming;
                return BuildSnapshot(
                    new CoordinatorDecision(
                        CoordinatorDecisionKind.Preempted,
                        "A higher-priority or newer equal-priority program became active.",
                        preemptedProgramId),
                    rehearsalStopped,
                    resynchronizeFromUtc: false);
            }

            if (priorityComparison == 0)
            {
                return BuildSnapshot(
                    new CoordinatorDecision(
                        CoordinatorDecisionKind.IgnoredOlderUpdate,
                        "An older equal-priority program was ignored.",
                        program.ProgramId),
                    rehearsalStopped,
                    resynchronizeFromUtc: false);
            }

            CoordinatorDecision queueDecision = Queue(incoming);
            if (replacedWaitingVersion && queueDecision.Kind == CoordinatorDecisionKind.Queued)
            {
                queueDecision = new CoordinatorDecision(
                    CoordinatorDecisionKind.Replaced,
                    "A newer version replaced the waiting event.",
                    existing?.Program.ProgramId);
            }
            return BuildSnapshot(
                queueDecision,
                rehearsalStopped,
                resynchronizeFromUtc: false);
        }
    }

    public CoordinatorSnapshot Evaluate(bool resynchronizeFromUtc = false)
    {
        lock (_gate)
        {
            CoordinatorDecision decision = EvaluateCurrentAndPromote(resynchronizeFromUtc);
            return BuildSnapshot(decision, rehearsalStopped: false, resynchronizeFromUtc);
        }
    }

    public CoordinatorSnapshot UpdateSettings(DisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            _settings = settings;
            CoordinatorDecision decision = EvaluateCurrentAndPromote(resynchronizeFromUtc: false);
            if (decision.Kind == CoordinatorDecisionKind.Evaluated)
            {
                decision = new CoordinatorDecision(
                    CoordinatorDecisionKind.SettingsUpdated,
                    "Display timing settings were updated.");
            }

            return BuildSnapshot(decision, rehearsalStopped: false, resynchronizeFromUtc: false);
        }
    }

    public CoordinatorSnapshot SelectManualPage(int pageIndex)
    {
        lock (_gate)
        {
            if (_current is not null && _current.Program.EndPolicy == EndPolicy.Manual)
            {
                _current.ManualPageIndex = Math.Clamp(
                    pageIndex,
                    0,
                    Math.Max(0, _current.Program.Pages.Count - 1));
            }

            return BuildSnapshot(
                new CoordinatorDecision(
                    CoordinatorDecisionKind.ManualPageSelected,
                    "The manual page selection was evaluated."),
                rehearsalStopped: false,
                resynchronizeFromUtc: false);
        }
    }

    public CoordinatorSnapshot Clear()
    {
        lock (_gate)
        {
            _current = null;
            _persistentTsunami = null;
            _pendingByKind.Clear();
            _pendingWeatherByEventId.Clear();
            return BuildSnapshot(
                new CoordinatorDecision(
                    CoordinatorDecisionKind.Cleared,
                    "All active and waiting display programs were cleared manually."),
                rehearsalStopped: false,
                resynchronizeFromUtc: false);
        }
    }

    public CoordinatorSnapshot Clear(EventKind kind)
    {
        lock (_gate)
        {
            bool removedCurrent = _current?.Program.Kind == kind;
            if (removedCurrent)
            {
                _current = null;
            }

            if (kind == EventKind.Tsunami)
            {
                _persistentTsunami = null;
            }

            _pendingByKind.Remove(kind);
            if (kind == EventKind.WeatherWarning)
            {
                _pendingWeatherByEventId.Clear();
            }

            if (removedCurrent)
            {
                EvaluateCurrentAndPromote(resynchronizeFromUtc: false);
            }

            return BuildSnapshot(
                new CoordinatorDecision(
                    CoordinatorDecisionKind.Cleared,
                    $"All {kind} display programs were cleared manually."),
                rehearsalStopped: false,
                resynchronizeFromUtc: false);
        }
    }

    public CoordinatorSnapshot Restore(CoordinatorRestoreState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _current = state.Current is null
                ? null
                : CreateRestoredScheduledProgram(state.Current);
            _persistentTsunami = state.PersistentTsunami is null
                ? null
                : CreateRestoredScheduledProgram(state.PersistentTsunami);
            _pendingByKind.Clear();
            _pendingWeatherByEventId.Clear();
            foreach (RestoredProgram pending in state.Pending)
            {
                ScheduledProgram restored = CreateRestoredScheduledProgram(pending);
                if (restored.Program.Kind == EventKind.WeatherWarning)
                {
                    if (!_pendingWeatherByEventId.TryGetValue(
                            restored.Program.EventId,
                            out ScheduledProgram? existing) ||
                        restored.Program.IssuedAt >= existing.Program.IssuedAt)
                    {
                        _pendingWeatherByEventId[restored.Program.EventId] = restored;
                    }
                }
                else
                {
                    _pendingByKind[restored.Program.Kind] = restored;
                }
            }

            CoordinatorDecision evaluation = EvaluateCurrentAndPromote(resynchronizeFromUtc: true);
            return BuildSnapshot(
                new CoordinatorDecision(
                    CoordinatorDecisionKind.Restored,
                    evaluation.Kind == CoordinatorDecisionKind.Expired
                        ? "Expired persisted display state was discarded."
                        : "Eligible production display state was restored."),
                rehearsalStopped: false,
                resynchronizeFromUtc: true);
        }
    }

    private CoordinatorDecision EvaluateCurrentAndPromote(bool resynchronizeFromUtc)
    {
        if (_current is not null)
        {
            PageClockResult evaluation = EvaluateState(_current, resynchronizeFromUtc);
            if (!evaluation.IsExpired)
            {
                return new CoordinatorDecision(
                    CoordinatorDecisionKind.Evaluated,
                    resynchronizeFromUtc
                        ? "The active program was resynchronized from UTC."
                        : "The active program was evaluated.");
            }

            string expiredProgramId = _current.Program.ProgramId;
            _current = null;
            ScheduledProgram? promoted = TakeNextEligible(resynchronizeFromUtc);
            if (promoted is not null)
            {
                _current = promoted;
                return new CoordinatorDecision(
                    CoordinatorDecisionKind.Promoted,
                    "The active program expired and the highest-priority waiting program resumed.",
                    expiredProgramId);
            }

            return new CoordinatorDecision(
                CoordinatorDecisionKind.Expired,
                "The active program expired.",
                expiredProgramId);
        }

        ScheduledProgram? next = TakeNextEligible(resynchronizeFromUtc);
        if (next is not null)
        {
            _current = next;
            return new CoordinatorDecision(
                CoordinatorDecisionKind.Promoted,
                "The highest-priority waiting program became active.",
                next.Program.ProgramId);
        }

        return new CoordinatorDecision(
            CoordinatorDecisionKind.Evaluated,
            "There is no active display program.");
    }

    private ScheduledProgram? TakeNextEligible(bool resynchronizeFromUtc)
    {
        var candidates = new List<ScheduledProgram>();
        if (_persistentTsunami is not null)
        {
            candidates.Add(_persistentTsunami);
        }

        foreach (ScheduledProgram pending in _pendingByKind.Values)
        {
            if (!candidates.Any(candidate =>
                    candidate.Program.ProgramId == pending.Program.ProgramId))
            {
                candidates.Add(pending);
            }
        }

        foreach (ScheduledProgram pending in _pendingWeatherByEventId.Values)
        {
            if (!candidates.Any(candidate =>
                    candidate.Program.ProgramId == pending.Program.ProgramId))
            {
                candidates.Add(pending);
            }
        }

        _pendingByKind.Clear();
        _pendingWeatherByEventId.Clear();
        ScheduledProgram[] eligible = candidates
            .Where(candidate =>
                candidate.Program.Kind == EventKind.WeatherWarning ||
                !EvaluateState(candidate, resynchronizeFromUtc).IsExpired)
            .OrderByDescending(static candidate => candidate.Program.Priority)
            .ThenBy(static candidate => candidate.Program.Kind == EventKind.WeatherWarning
                ? candidate.Sequence
                : long.MaxValue)
            .ThenByDescending(static candidate => candidate.Program.IssuedAt)
            .ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        ScheduledProgram selected = eligible[0].Program.Kind == EventKind.WeatherWarning
            ? RestartForActivation(eligible[0])
            : eligible[0];
        foreach (ScheduledProgram remaining in eligible.Skip(1))
        {
            if (_persistentTsunami is null ||
                remaining.Program.ProgramId != _persistentTsunami.Program.ProgramId)
            {
                StorePending(remaining);
            }
        }

        return selected;
    }

    private CoordinatorDecision Queue(ScheduledProgram incoming)
    {
        if (_persistentTsunami is not null &&
            incoming.Program.ProgramId == _persistentTsunami.Program.ProgramId)
        {
            return new CoordinatorDecision(
                CoordinatorDecisionKind.Queued,
                "The continuing tsunami is waiting in its persistent slot.",
                incoming.Program.ProgramId);
        }

        if (incoming.Program.Kind == EventKind.WeatherWarning)
        {
            if (_pendingWeatherByEventId.TryGetValue(
                    incoming.Program.EventId,
                    out ScheduledProgram? queuedWeather))
            {
                if (incoming.Program.IssuedAt < queuedWeather.Program.IssuedAt)
                {
                    return new CoordinatorDecision(
                        CoordinatorDecisionKind.IgnoredOlderUpdate,
                        "An older weather program cannot replace its waiting event.",
                        incoming.Program.ProgramId);
                }

                _pendingWeatherByEventId[incoming.Program.EventId] = incoming;
                return new CoordinatorDecision(
                    CoordinatorDecisionKind.Replaced,
                    "A newer weather program replaced the waiting version of the same event.",
                    queuedWeather.Program.ProgramId);
            }

            _pendingWeatherByEventId[incoming.Program.EventId] = incoming;
            return new CoordinatorDecision(
                CoordinatorDecisionKind.Queued,
                "The weather program is waiting without replacing other weather telegrams.",
                incoming.Program.ProgramId);
        }

        if (_pendingByKind.TryGetValue(incoming.Program.Kind, out ScheduledProgram? queued))
        {
            if (incoming.Program.IssuedAt < queued.Program.IssuedAt)
            {
                return new CoordinatorDecision(
                    CoordinatorDecisionKind.IgnoredOlderUpdate,
                    "An older program cannot replace the waiting item for this kind.",
                    incoming.Program.ProgramId);
            }

            _pendingByKind[incoming.Program.Kind] = incoming;
            return new CoordinatorDecision(
                CoordinatorDecisionKind.Replaced,
                "The newer program replaced the waiting item for this kind.",
                queued.Program.ProgramId);
        }

        _pendingByKind[incoming.Program.Kind] = incoming;
        return new CoordinatorDecision(
            CoordinatorDecisionKind.Queued,
            "The lower-priority program is waiting.",
            incoming.Program.ProgramId);
    }

    private void ReplaceExisting(ScheduledProgram existing, ScheduledProgram incoming)
    {
        if (_current == existing)
        {
            _current = incoming;
        }

        if (_persistentTsunami == existing)
        {
            _persistentTsunami = incoming;
        }

        if (_pendingByKind.TryGetValue(existing.Program.Kind, out ScheduledProgram? pending) &&
            pending == existing)
        {
            _pendingByKind[existing.Program.Kind] = incoming;
        }

        if (_pendingWeatherByEventId.TryGetValue(
                existing.Program.EventId,
                out ScheduledProgram? pendingWeather) &&
            pendingWeather == existing)
        {
            _pendingWeatherByEventId[existing.Program.EventId] = incoming;
        }
    }

    private ScheduledProgram CreateScheduledProgram(DisplayProgram program)
    {
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        DisplayProgram scheduledProgram = program with { StartedAtUtc = startedAtUtc };
        return new ScheduledProgram(
            scheduledProgram,
            startedAtUtc,
            _clock.GetTimestamp(),
            ++_nextSequence);
    }

    private ScheduledProgram CreateRestoredScheduledProgram(RestoredProgram restored)
    {
        DateTimeOffset startedAtUtc = restored.StartedAtUtc > _clock.UtcNow
            ? _clock.UtcNow
            : restored.StartedAtUtc;
        TimeSpan elapsed = _clock.UtcNow - startedAtUtc;
        return new ScheduledProgram(
            restored.Program with { StartedAtUtc = startedAtUtc },
            startedAtUtc,
            _clock.GetTimestamp(),
            ++_nextSequence)
        {
            ElapsedAtAnchor = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed,
        };
    }

    private ScheduledProgram RestartForActivation(ScheduledProgram pending)
    {
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        return new ScheduledProgram(
            pending.Program with { StartedAtUtc = startedAtUtc },
            startedAtUtc,
            _clock.GetTimestamp(),
            pending.Sequence);
    }

    private ScheduledProgram? FindByEventId(EventId eventId)
    {
        if (_current?.Program.EventId == eventId)
        {
            return _current;
        }

        if (_persistentTsunami?.Program.EventId == eventId)
        {
            return _persistentTsunami;
        }

        return _pendingWeatherByEventId.GetValueOrDefault(eventId) ??
            _pendingByKind.Values.FirstOrDefault(program => program.Program.EventId == eventId);
    }

    private bool RemoveRehearsalState()
    {
        bool removed = false;
        if (_current is not null && !IsProduction(_current.Program))
        {
            _current = null;
            removed = true;
        }

        EventKind[] rehearsalKinds = _pendingByKind
            .Where(static pair => !IsProduction(pair.Value.Program))
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (EventKind kind in rehearsalKinds)
        {
            _pendingByKind.Remove(kind);
            removed = true;
        }

        EventId[] rehearsalWeatherEvents = _pendingWeatherByEventId
            .Where(static pair => !IsProduction(pair.Value.Program))
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (EventId eventId in rehearsalWeatherEvents)
        {
            _pendingWeatherByEventId.Remove(eventId);
            removed = true;
        }

        if (_persistentTsunami is not null && !IsProduction(_persistentTsunami.Program))
        {
            _persistentTsunami = null;
            removed = true;
        }

        return removed;
    }

    private bool HasProductionState() =>
        (_current is not null && IsProduction(_current.Program)) ||
        (_persistentTsunami is not null && IsProduction(_persistentTsunami.Program)) ||
        _pendingByKind.Values.Any(static program => IsProduction(program.Program)) ||
        _pendingWeatherByEventId.Values.Any(static program => IsProduction(program.Program));

    private static bool IsProduction(DisplayProgram program) =>
        program.SourceMode == SourceMode.Production &&
        string.IsNullOrWhiteSpace(program.RehearsalLabel);

    private static bool IsPersistentProductionTsunami(DisplayProgram program) =>
        program.Kind == EventKind.Tsunami &&
        program.EndPolicy is EndPolicy.LoopUntilReplaced or EndPolicy.HoldUntilCancelled &&
        IsProduction(program);

    private PageClockResult EvaluateState(
        ScheduledProgram state,
        bool resynchronizeFromUtc)
    {
        TimeSpan elapsed;
        if (resynchronizeFromUtc)
        {
            elapsed = _clock.UtcNow - state.StartedAtUtc;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            state.ElapsedAtAnchor = elapsed;
            state.AnchorTimestamp = _clock.GetTimestamp();
        }
        else
        {
            elapsed = state.ElapsedAtAnchor + _clock.GetElapsedTime(state.AnchorTimestamp);
        }

        return _pageClock.Evaluate(
            state.Program,
            _settings,
            state.StartedAtUtc,
            elapsed,
            state.ManualPageIndex);
    }

    private CoordinatorSnapshot BuildSnapshot(
        CoordinatorDecision decision,
        bool rehearsalStopped,
        bool resynchronizeFromUtc)
    {
        if (_current is null)
        {
            return new CoordinatorSnapshot(
                CurrentProgram: null,
                CurrentPage: null,
                CurrentPageIndex: -1,
                Elapsed: TimeSpan.Zero,
                ProgramStartedAtUtc: null,
                ExpiresAtUtc: null,
                Remaining: null,
                PendingPrograms: GetPendingPrograms(),
                PersistentTsunami: _persistentTsunami?.Program,
                decision,
                rehearsalStopped);
        }

        PageClockResult evaluation = EvaluateState(_current, resynchronizeFromUtc);
        return new CoordinatorSnapshot(
            _current.Program,
            evaluation.Page,
            evaluation.Index,
            evaluation.Elapsed,
            _current.StartedAtUtc,
            evaluation.ExpiresAtUtc,
            evaluation.Remaining,
            GetPendingPrograms(),
            _persistentTsunami?.Program,
            decision,
            rehearsalStopped);
    }

    private DisplayProgram[] GetPendingPrograms() => _pendingByKind.Values
        .Concat(_pendingWeatherByEventId.Values)
        .OrderByDescending(static pending => pending.Program.Priority)
        .ThenBy(static pending => pending.Program.Kind == EventKind.WeatherWarning
            ? pending.Sequence
            : long.MaxValue)
        .ThenByDescending(static pending => pending.Program.IssuedAt)
        .Select(static pending => pending.Program)
        .ToArray();

    private void StorePending(ScheduledProgram pending)
    {
        if (pending.Program.Kind == EventKind.WeatherWarning)
        {
            _pendingWeatherByEventId[pending.Program.EventId] = pending;
        }
        else
        {
            _pendingByKind[pending.Program.Kind] = pending;
        }
    }

    private void RemovePending(ScheduledProgram pending)
    {
        if (_pendingByKind.TryGetValue(pending.Program.Kind, out ScheduledProgram? byKind) &&
            byKind == pending)
        {
            _pendingByKind.Remove(pending.Program.Kind);
        }

        if (_pendingWeatherByEventId.TryGetValue(
                pending.Program.EventId,
                out ScheduledProgram? weather) &&
            weather == pending)
        {
            _pendingWeatherByEventId.Remove(pending.Program.EventId);
        }
    }

    private sealed class ScheduledProgram
    {
        public ScheduledProgram(
            DisplayProgram program,
            DateTimeOffset startedAtUtc,
            long anchorTimestamp,
            long sequence)
        {
            Program = program;
            StartedAtUtc = startedAtUtc;
            AnchorTimestamp = anchorTimestamp;
            Sequence = sequence;
        }

        public DisplayProgram Program { get; }

        public DateTimeOffset StartedAtUtc { get; }

        public long AnchorTimestamp { get; set; }

        public TimeSpan ElapsedAtAnchor { get; set; }

        public int ManualPageIndex { get; set; }

        public long Sequence { get; }
    }
}
