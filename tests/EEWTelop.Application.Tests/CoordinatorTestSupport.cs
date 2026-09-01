using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Tests;

internal sealed class FakeClock : IClock
{
    private long _timestampTicks;

    public FakeClock(DateTimeOffset? utcNow = null)
    {
        UtcNow = utcNow ?? new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset UtcNow { get; private set; }

    public long GetTimestamp() => _timestampTicks;

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        TimeSpan.FromTicks(_timestampTicks - startingTimestamp);

    public void Advance(TimeSpan elapsed)
    {
        _timestampTicks += elapsed.Ticks;
        UtcNow += elapsed;
    }

    public void AdvanceUtcOnly(TimeSpan elapsed) => UtcNow += elapsed;
}

internal static class CoordinatorTestSupport
{
    public static DisplaySettings Settings(
        int autoHideSeconds = 45,
        double pageDurationSeconds = 4,
        int? eewAutoHideSeconds = null,
        int? quakeAutoHideSeconds = null,
        int? tsunamiAutoHideSeconds = null) =>
        AppSettings.CreateDefault().Display with
        {
            AutoHideSeconds = autoHideSeconds,
            PageDurationSeconds = pageDurationSeconds,
            EewAutoHideSeconds = eewAutoHideSeconds ?? autoHideSeconds,
            QuakeAutoHideSeconds = quakeAutoHideSeconds ?? autoHideSeconds,
            TsunamiAutoHideSeconds = tsunamiAutoHideSeconds ?? autoHideSeconds,
        };

    public static DisplayProgram Program(
        string id,
        EventKind kind,
        OverlayPriority priority,
        DateTimeOffset? issuedAt = null,
        int pageCount = 3,
        EndPolicy endPolicy = EndPolicy.AutoHide,
        SourceMode sourceMode = SourceMode.Production,
        string rehearsalLabel = "",
        TimeSpan? durationOverride = null,
        string? eventId = null)
    {
        DisplayPage[] pages = Enumerable.Range(0, pageCount)
            .Select(index => new DisplayPage(
                index,
                [new DisplayBlock("", $"page-{index}", "", DisplayStyleTokens.Summary)],
                $"page-{index}",
                durationOverride))
            .ToArray();

        return new DisplayProgram(
            id,
            EventId.Create(eventId ?? id),
            kind,
            sourceMode,
            issuedAt ?? new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            priority,
            pages,
            DateTimeOffset.UnixEpoch,
            endPolicy,
            rehearsalLabel);
    }
}
