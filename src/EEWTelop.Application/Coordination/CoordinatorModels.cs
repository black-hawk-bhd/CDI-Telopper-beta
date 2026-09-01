using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;

namespace EEWTelop.Application.Coordination;

public interface IDisplayCoordinator
{
    CoordinatorSnapshot Apply(DisplayProgram program);

    CoordinatorSnapshot Evaluate(bool resynchronizeFromUtc = false);

    CoordinatorSnapshot UpdateSettings(DisplaySettings settings);

    CoordinatorSnapshot SelectManualPage(int pageIndex);

    CoordinatorSnapshot Clear();

    CoordinatorSnapshot Restore(CoordinatorRestoreState state);
}

public sealed record RestoredProgram(
    DisplayProgram Program,
    DateTimeOffset StartedAtUtc);

public sealed record CoordinatorRestoreState(
    RestoredProgram? Current,
    RestoredProgram? PersistentTsunami,
    IReadOnlyList<RestoredProgram> Pending);

public enum CoordinatorDecisionKind
{
    Evaluated = 0,
    Activated,
    Preempted,
    Queued,
    Replaced,
    Promoted,
    Expired,
    SettingsUpdated,
    ManualPageSelected,
    Cleared,
    Restored,
    IgnoredDuplicate,
    IgnoredOlderUpdate,
    IgnoredRehearsal,
    IgnoredEmptyProgram,
}

public sealed record CoordinatorDecision(
    CoordinatorDecisionKind Kind,
    string Message,
    string? RelatedProgramId = null);

public sealed record CoordinatorSnapshot(
    DisplayProgram? CurrentProgram,
    DisplayPage? CurrentPage,
    int CurrentPageIndex,
    TimeSpan Elapsed,
    DateTimeOffset? ProgramStartedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    TimeSpan? Remaining,
    IReadOnlyList<DisplayProgram> PendingPrograms,
    DisplayProgram? PersistentTsunami,
    CoordinatorDecision Decision,
    bool RehearsalStopped);
