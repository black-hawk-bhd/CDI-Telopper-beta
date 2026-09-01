using EEWTelop.Application.Abstractions;

namespace EEWTelop.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    private readonly TimeProvider _timeProvider;

    public SystemClock()
        : this(TimeProvider.System)
    {
    }

    public SystemClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    public long GetTimestamp() => _timeProvider.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        _timeProvider.GetElapsedTime(startingTimestamp);
}

