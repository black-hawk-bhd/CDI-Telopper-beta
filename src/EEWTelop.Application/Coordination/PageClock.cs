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

        TimeSpan pageDuration = NormalizePageDuration(settings.PageDurationSeconds);
        long rawIndex = (long)Math.Floor(safeElapsed.TotalMilliseconds / pageDuration.TotalMilliseconds);
        int index = program.EndPolicy switch
        {
            EndPolicy.LoopUntilReplaced or EndPolicy.HoldUntilCancelled =>
                (int)(rawIndex % program.Pages.Count),
            EndPolicy.Manual => Math.Clamp(manualPageIndex, 0, program.Pages.Count - 1),
            _ => (int)Math.Min(rawIndex, program.Pages.Count - 1L),
        };

        TimeSpan? lifetime = GetLifetime(program, settings, pageDuration);
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

    public static TimeSpan NormalizePageDuration(double seconds)
    {
        double finite = double.IsFinite(seconds) ? seconds : 4.0;
        double clamped = Math.Clamp(finite, 1.0, 30.0);
        double halfSecondStep = Math.Round(clamped * 2, MidpointRounding.AwayFromZero) / 2;
        return TimeSpan.FromSeconds(halfSecondStep);
    }

    private static TimeSpan? GetLifetime(
        DisplayProgram program,
        DisplaySettings settings,
        TimeSpan pageDuration)
    {
        if (program.EndPolicy is EndPolicy.LoopUntilReplaced or EndPolicy.HoldUntilCancelled)
        {
            return null;
        }

        TimeSpan? explicitDuration = program.Pages
            .Select(static page => page.DurationOverride)
            .Where(static duration => duration is not null)
            .Max();
        int configuredAutoHideSeconds = program.Kind switch
        {
            EventKind.Eew => settings.EffectiveEewAutoHideSeconds,
            EventKind.Quake => settings.EffectiveQuakeAutoHideSeconds,
            EventKind.Tsunami => settings.EffectiveTsunamiAutoHideSeconds,
            EventKind.WeatherWarning => settings.EffectiveWeatherWarningAutoHideSeconds,
            _ => settings.AutoHideSeconds,
        };
        double autoHideSeconds = Math.Max(0, configuredAutoHideSeconds);
        if (explicitDuration is null && autoHideSeconds == 0)
        {
            return null;
        }

        TimeSpan configured = explicitDuration ?? TimeSpan.FromSeconds(autoHideSeconds);
        TimeSpan minimumForEveryPage =
            TimeSpan.FromTicks(pageDuration.Ticks * (program.Pages.Count - 1L))
            + MinimumLastPageDuration;
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
