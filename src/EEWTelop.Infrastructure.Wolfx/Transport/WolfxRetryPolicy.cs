using System;
using System.Net;

namespace EEWTelop.Infrastructure.Wolfx.Transport;

internal static class WolfxRetryPolicy
{
    public static readonly TimeSpan DetailPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StableWebSocketDuration = TimeSpan.FromSeconds(60);

    public static int NextWebSocketFailureCount(int failureCount, TimeSpan? connectionDuration) =>
        connectionDuration >= StableWebSocketDuration ? 1 : Math.Min(failureCount + 1, 5);

    public static TimeSpan WebSocketDelay(int failureCount, HttpStatusCode? status, TimeSpan? retryAfter) =>
        RespectServerDelay(Math.Min(60, 5 << Math.Clamp(failureCount - 1, 0, 4)), status, retryAfter);

    public static TimeSpan DetailDelay(int failureCount, HttpStatusCode? status, TimeSpan? retryAfter) =>
        RespectServerDelay(Math.Min(300, 60 << Math.Clamp(failureCount - 1, 0, 3)), status, retryAfter);

    private static TimeSpan RespectServerDelay(int seconds, HttpStatusCode? status, TimeSpan? retryAfter)
    {
        if (status == HttpStatusCode.TooManyRequests) seconds = Math.Max(seconds, 120);
        TimeSpan minimum = TimeSpan.FromSeconds(seconds);
        return retryAfter > minimum ? retryAfter.Value : minimum;
    }
}
