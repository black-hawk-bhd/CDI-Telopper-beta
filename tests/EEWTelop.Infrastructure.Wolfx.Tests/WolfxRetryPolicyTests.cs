using System;
using System.Net;
using EEWTelop.Infrastructure.Wolfx.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Wolfx.Tests;

[TestClass]
public sealed class WolfxRetryPolicyTests
{
    [TestMethod]
    [DataRow(0, -1, 1, 5)]
    [DataRow(1, 0, 2, 10)]
    [DataRow(2, 15, 3, 20)]
    [DataRow(3, 59, 4, 40)]
    [DataRow(4, -1, 5, 60)]
    [DataRow(5, 59, 5, 60)]
    [DataRow(5, 60, 1, 5)]
    public void ShortWebSocketSessionsKeepEscalatingTheWait(int failures, int secondsConnected, int nextCount, int delay)
    {
        TimeSpan? duration = secondsConnected < 0 ? null : TimeSpan.FromSeconds(secondsConnected);
        int count = WolfxRetryPolicy.NextWebSocketFailureCount(failures, duration);
        Assert.AreEqual(nextCount, count);
        Assert.AreEqual(TimeSpan.FromSeconds(delay), WolfxRetryPolicy.WebSocketDelay(count, null, null));
    }

    [TestMethod]
    [DataRow(1, HttpStatusCode.InternalServerError, 0, 60)]
    [DataRow(2, HttpStatusCode.ServiceUnavailable, 75, 120)]
    [DataRow(3, HttpStatusCode.ServiceUnavailable, 0, 240)]
    [DataRow(4, HttpStatusCode.ServiceUnavailable, 0, 300)]
    [DataRow(1, HttpStatusCode.TooManyRequests, 0, 120)]
    [DataRow(4, HttpStatusCode.TooManyRequests, 600, 600)]
    public void FailedDetailRequestsWaitLongerThanNormalPolls(int failures, HttpStatusCode status, int retryAfter, int delay)
    {
        Assert.AreEqual(TimeSpan.FromSeconds(30), WolfxRetryPolicy.DetailPollInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(delay),
            WolfxRetryPolicy.DetailDelay(failures, status, TimeSpan.FromSeconds(retryAfter)));
    }

    [TestMethod]
    public void WebSocketUpgradeRespectsRateLimitsAndRetryAfter()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(2), WolfxRetryPolicy.WebSocketDelay(1, HttpStatusCode.TooManyRequests, null));
        Assert.AreEqual(TimeSpan.FromMinutes(3),
            WolfxRetryPolicy.WebSocketDelay(3, HttpStatusCode.ServiceUnavailable, TimeSpan.FromMinutes(3)));
    }
}
