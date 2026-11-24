using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Coordination;

public interface IPageClock
{
    PageClockResult Evaluate(
        DisplayProgram program,
        DisplaySettings settings,
        DateTimeOffset programStartedAtUtc,
        TimeSpan elapsed,
        int manualPageIndex = 0);
}

public sealed class PageClock : IPageClock
{
    private static readonly TimeSpan MinimumLastPageDuration = TimeSpan.FromSeconds(5);

    public PageClockResult Evaluate(
        DisplayProgram program,
        DisplaySettings settings,
        DateTimeOffset programStartedAtUtc,
        TimeSpan elapsed,
        int manualPageIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(settings);

        TimeSpan safeElapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        if (program.Pages.Count == 0)
        {
            return new PageClockResult(
                Page: null,
                Index: -1,
                safeElapsed,
                ExpiresAtUtc: programStartedAtUtc,
                IsExpired: true,
                Remaining: TimeSpan.Zero);
        }

        TimeSpan pageDuration = GetPageDuration(settings, program);
        long rawIndex = (long)Math.Floor(safeElapsed.TotalMilliseconds / pageDuration.TotalMilliseconds);
        int index = program.EndPolicy switch
        {
            EndPolicy.LoopUntilReplaced or EndPolicy.HoldUntilCancelled =>
                (int)(rawIndex % program.Pages.Count),
            EndPolicy.Manual => Math.Clamp(manualPageIndex, 0, program.Pages.Count - 1),
            _ => (int)Math.Min(rawIndex, program.Pages.Count - 1L),
        };

        TimeSpan? lifetime = GetLifetime(program, settings, pageDuration);
        if (program.ExpiresAtUtc is { } deadline)
        {
            TimeSpan untilDeadline = deadline - programStartedAtUtc;
            if (lifetime is null || untilDeadline < lifetime) lifetime = untilDeadline;
        }
        bool isExpired = lifetime is TimeSpan duration && safeElapsed >= duration;
        DateTimeOffset? expiresAtUtc = lifetime is TimeSpan expiryDuration
            ? programStartedAtUtc + expiryDuration
            : null;
        TimeSpan? remaining = lifetime is TimeSpan totalLifetime
            ? TimeSpan.FromTicks(Math.Max(0, (totalLifetime - safeElapsed).Ticks))
            : null;

        return new PageClockResult(
            program.Pages[index],
            index,
            safeElapsed,
            expiresAtUtc,
            isExpired,
            remaining);
    }

    public static TimeSpan GetPageDuration(DisplaySettings settings, DisplayProgram? program)
    {
        if (program?.TelegramPageDurationKey is { } key &&
            settings.TelegramPageDurationSeconds?.TryGetValue(key, out double seconds) == true &&
            double.IsFinite(seconds) && seconds > 0)
        {
            return NormalizePageDuration(seconds);
        }

        return NormalizePageDuration(settings.PageDurationSeconds);
    }

    public static TimeSpan NormalizePageDuration(double seconds)
    {
        double finite = double.IsFinite(seconds) ? seconds : 4.0;
        double clamped = Math.Clamp(finite, 1.0, 30.0);
        double halfSecondStep = Math.Round(clamped * 2, MidpointRounding.AwayFromZero) / 2;
        return TimeSpan.FromSeconds(halfSecondStep);
    }

    public static Dictionary<string, double> NormalizeTelegramPageDurations(
        IEnumerable<KeyValuePair<string, double>>? durations) =>
        (durations ?? [])
            .Where(static pair => TelegramPageDurationOptions.IsKnownKey(pair.Key) &&
                double.IsFinite(pair.Value) && pair.Value > 0)
            .ToDictionary(static pair => pair.Key, static pair => NormalizePageDuration(pair.Value).TotalSeconds,
                StringComparer.Ordinal);

    private static TimeSpan? GetLifetime(
        DisplayProgram program,
        DisplaySettings settings,
        TimeSpan pageDuration)
    {
        TimeSpan passDuration = TimeSpan.FromTicks(pageDuration.Ticks * program.Pages.Count);

        // Each production pass is finite even for a persistent tsunami or OBS copy.
        // The replay catalog schedules the remaining passes, including no passes for count=1.
        if (program.MaximumDisplayCycles is int maximumCycles)
        {
            return TimeSpan.FromTicks(passDuration.Ticks * Math.Clamp(maximumCycles, 1, 100));
        }

        if (program.SourceMode == SourceMode.Production &&
            settings.ProductionReplay.GetPolicy(program.Kind).Enabled)
        {
            return passDuration;
        }

        if (program.EndPolicy is EndPolicy.LoopUntilReplaced or EndPolicy.HoldUntilCancelled)
        {
            return null;
        }

        TimeSpan? explicitDuration = program.Pages
            .Select(static page => page.DurationOverride)
            .Max();
        if (program.Kind != EventKind.Eew)
        {
            return explicitDuration ?? (program.EndPolicy == EndPolicy.Manual ? null : passDuration);
        }

        int autoHideSeconds = Math.Max(0, settings.EffectiveEewAutoHideSeconds);
        if (explicitDuration is null && autoHideSeconds == 0)
        {
            return null;
        }

        TimeSpan configured = explicitDuration ?? TimeSpan.FromSeconds(autoHideSeconds);
        TimeSpan minimumForEveryPage = passDuration - pageDuration + MinimumLastPageDuration;
        return configured >= minimumForEveryPage ? configured : minimumForEveryPage;
    }
}

public sealed record PageClockResult(
    DisplayPage? Page,
    int Index,
    TimeSpan Elapsed,
    DateTimeOffset? ExpiresAtUtc,
    bool IsExpired,
    TimeSpan? Remaining);
