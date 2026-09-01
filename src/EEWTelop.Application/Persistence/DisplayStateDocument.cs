using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Persistence;

public sealed record DisplayStateDocument(
    int SchemaVersion,
    DateTimeOffset SavedAtUtc,
    bool LastShutdownWasClean,
    StoredDisplayProgram? Current,
    StoredDisplayProgram? PersistentTsunami,
    IReadOnlyList<StoredDisplayProgram> Pending,
    IReadOnlyList<StoredEventSignature> RecentSignatures,
    DateTimeOffset? LastReceivedAtUtc,
    string ConnectionIdentity)
{
    public const int CurrentSchemaVersion = 1;

    public static DisplayStateDocument Empty(DateTimeOffset now) => new(
        CurrentSchemaVersion,
        now,
        LastShutdownWasClean: true,
        Current: null,
        PersistentTsunami: null,
        Pending: [],
        RecentSignatures: [],
        LastReceivedAtUtc: null,
        ConnectionIdentity: string.Empty);

    public static DisplayStateDocument FromSnapshot(
        CoordinatorSnapshot snapshot,
        DateTimeOffset now,
        bool lastShutdownWasClean,
        DateTimeOffset? lastReceivedAtUtc = null,
        string connectionIdentity = "",
        IReadOnlyList<StoredEventSignature>? recentSignatures = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool allowProductionPersistence = string.IsNullOrWhiteSpace(connectionIdentity) ||
            string.Equals(connectionIdentity, "Production", StringComparison.OrdinalIgnoreCase);
        StoredDisplayProgram? current = allowProductionPersistence &&
            snapshot.CurrentProgram is { } currentProgram &&
            IsProduction(currentProgram) && snapshot.ProgramStartedAtUtc is { } currentStart
                ? StoredDisplayProgram.From(currentProgram, currentStart)
                : null;
        StoredDisplayProgram? persistent = allowProductionPersistence &&
            snapshot.PersistentTsunami is { } tsunami &&
            IsProduction(tsunami)
            ? StoredDisplayProgram.From(
                tsunami,
                current?.ProgramId == tsunami.ProgramId
                    ? current.StartedAtUtc
                    : tsunami.StartedAtUtc)
            : null;
        return new DisplayStateDocument(
            CurrentSchemaVersion,
            now,
            lastShutdownWasClean,
            current,
            persistent,
            (allowProductionPersistence
                ? snapshot.PendingPrograms.Where(IsProduction)
                : Enumerable.Empty<DisplayProgram>())
                .Select(program => StoredDisplayProgram.From(program, program.StartedAtUtc))
                .ToArray(),
            RecentSignatures: recentSignatures?.ToArray() ?? [],
            lastReceivedAtUtc,
            connectionIdentity);
    }

    private static bool IsProduction(DisplayProgram program) =>
        program.SourceMode == SourceMode.Production &&
        string.IsNullOrWhiteSpace(program.RehearsalLabel);

    public CoordinatorRestoreState ToRestoreState(DateTimeOffset now)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported state schema version: {SchemaVersion}.");
        }

        RestoredProgram? current = RestoreIfEligible(Current, now, persistentOnly: false);
        RestoredProgram? tsunami = RestoreIfEligible(PersistentTsunami, now, persistentOnly: true);
        RestoredProgram[] pending = (Pending ?? [])
            .Select(item => RestoreIfEligible(item, now, persistentOnly: false))
            .OfType<RestoredProgram>()
            .Where(item => current is null ||
                item.Program.ProgramId != current.Program.ProgramId)
            .Where(item => tsunami is null ||
                item.Program.ProgramId != tsunami.Program.ProgramId)
            .ToArray();
        return new CoordinatorRestoreState(current, tsunami, pending);
    }

    private static RestoredProgram? RestoreIfEligible(
        StoredDisplayProgram? stored,
        DateTimeOffset now,
        bool persistentOnly)
    {
        if (stored is null || !stored.TryToProgram(out DisplayProgram? program) || program is null ||
            program.SourceMode != SourceMode.Production ||
            !string.IsNullOrWhiteSpace(program.RehearsalLabel) ||
            program.Pages.Count == 0)
        {
            return null;
        }

        if (persistentOnly && (program.Kind != EventKind.Tsunami ||
            program.Priority == OverlayPriority.TsunamiCancel ||
            program.EndPolicy is not (EndPolicy.LoopUntilReplaced or EndPolicy.HoldUntilCancelled)))
        {
            return null;
        }

        if (program.Kind == EventKind.Tsunami &&
            program.Priority == OverlayPriority.TsunamiCancel)
        {
            return null;
        }

        TimeSpan maximumAge = program.Kind switch
        {
            EventKind.Eew => TimeSpan.FromSeconds(90),
            EventKind.Quake => TimeSpan.FromMinutes(10),
            EventKind.Tsunami => TimeSpan.FromHours(24),
            EventKind.WeatherWarning => TimeSpan.FromHours(12),
            EventKind.Volcano => TimeSpan.FromHours(12),
            _ => TimeSpan.Zero,
        };
        TimeSpan age = now - program.IssuedAt;
        if (age < TimeSpan.Zero || age > maximumAge)
        {
            return null;
        }

        return new RestoredProgram(program, stored.StartedAtUtc);
    }
}

public sealed record StoredDisplayProgram(
    string ProgramId,
    string EventId,
    EventKind Kind,
    SourceMode SourceMode,
    DateTimeOffset IssuedAt,
    OverlayPriority Priority,
    IReadOnlyList<StoredDisplayPage> Pages,
    DateTimeOffset StartedAtUtc,
    EndPolicy EndPolicy,
    string RehearsalLabel)
{
    public static StoredDisplayProgram From(DisplayProgram program, DateTimeOffset startedAtUtc) => new(
        program.ProgramId,
        program.EventId.Value,
        program.Kind,
        program.SourceMode,
        program.IssuedAt,
        program.Priority,
        program.Pages.Select(StoredDisplayPage.From).ToArray(),
        startedAtUtc,
        program.EndPolicy,
        program.RehearsalLabel);

    public bool TryToProgram(out DisplayProgram? program)
    {
        program = null;
        if (string.IsNullOrWhiteSpace(ProgramId) || string.IsNullOrWhiteSpace(EventId) ||
            Pages is null || !Enum.IsDefined(Kind) || !Enum.IsDefined(SourceMode) ||
            !Enum.IsDefined(Priority) || !Enum.IsDefined(EndPolicy))
        {
            return false;
        }

        DisplayPage[] pages = Pages.Select(static page => page.ToPage()).ToArray();
        program = new DisplayProgram(
            ProgramId,
            EEWTelop.Domain.Events.EventId.Create(EventId),
            Kind,
            SourceMode,
            IssuedAt,
            Priority,
            pages,
            StartedAtUtc,
            EndPolicy,
            RehearsalLabel ?? string.Empty);
        return true;
    }
}

public sealed record StoredDisplayPage(
    int Index,
    IReadOnlyList<DisplayBlock> Blocks,
    string AccessibleText,
    double? DurationOverrideSeconds)
{
    public static StoredDisplayPage From(DisplayPage page) => new(
        page.Index,
        page.Blocks.ToArray(),
        page.AccessibleText,
        page.DurationOverride?.TotalSeconds);

    public DisplayPage ToPage() => new(
        Index,
        Blocks?.ToArray() ?? [],
        AccessibleText ?? string.Empty,
        DurationOverrideSeconds is { } seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null);
}
