namespace EEWTelop.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp);
}

