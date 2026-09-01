namespace EEWTelop.Infrastructure.P2P.Transport;

public interface IJitterSource
{
    double NextUnit();
}

public sealed class RandomJitterSource : IJitterSource
{
    public double NextUnit() => Random.Shared.NextDouble();
}

public sealed class ReconnectDelayPolicy
{
    private readonly IJitterSource _jitterSource;

    public ReconnectDelayPolicy(IJitterSource jitterSource)
    {
        ArgumentNullException.ThrowIfNull(jitterSource);
        _jitterSource = jitterSource;
    }

    public TimeSpan GetDelay(int retryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryCount);
        double baseSeconds = retryCount >= 5 ? 30 : 1 << retryCount;
        double unit = Math.Clamp(_jitterSource.NextUnit(), 0, 1);
        double jitterRatio = 0.1 + (unit * 0.1);
        return TimeSpan.FromSeconds(baseSeconds * (1 + jitterRatio));
    }
}
