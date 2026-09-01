using EEWTelop.Infrastructure.P2P.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.P2P.Tests;

[TestClass]
public sealed class ReconnectDelayPolicyTests
{
    [TestMethod]
    public void ExponentialDelayIsCappedBeforeTenPercentJitter()
    {
        var policy = new ReconnectDelayPolicy(new FixedJitterSource(0));
        double[] expectedSeconds = [1.1, 2.2, 4.4, 8.8, 17.6, 33, 33];

        TimeSpan[] actual = Enumerable.Range(0, expectedSeconds.Length)
            .Select(policy.GetDelay)
            .ToArray();

        CollectionAssert.AreEqual(
            expectedSeconds.Select(TimeSpan.FromSeconds).ToArray(),
            actual);
    }

    [TestMethod]
    public void JitterIsClampedToTwentyPercent()
    {
        var policy = new ReconnectDelayPolicy(new FixedJitterSource(5));

        Assert.AreEqual(TimeSpan.FromSeconds(1.2), policy.GetDelay(0));
    }
}
