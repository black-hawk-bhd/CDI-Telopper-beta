using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;

namespace EEWTelop.Application.History;

public static class HistoryReplayTiming
{
    public static TimeSpan GetItemDuration(
        DisplayProgram program,
        DisplaySettings display,
        int intervalSeconds)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(display);

        TimeSpan configuredInterval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        int pageCount = Math.Max(1, program.Pages.Count);
        TimeSpan pageDuration = PageClock.NormalizePageDuration(display.PageDurationSeconds);
        TimeSpan completePagePass = TimeSpan.FromTicks(pageDuration.Ticks * pageCount);
        return completePagePass > configuredInterval ? completePagePass : configuredInterval;
    }
}
