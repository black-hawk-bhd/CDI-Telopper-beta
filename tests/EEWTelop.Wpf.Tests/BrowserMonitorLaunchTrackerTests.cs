using EEWTelop.Wpf.Obs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class BrowserMonitorLaunchTrackerTests
{
    [TestMethod]
    public void AutomaticStepsReuseMonitorAndManualButtonCanReopenIt()
    {
        var tracker = new BrowserMonitorLaunchTracker();
        const string url = "http://127.0.0.1:54321/monitor/?token=test";
        Assert.IsFalse(tracker.ShouldOpen(string.Empty, automatic: true));
        Assert.IsTrue(tracker.ShouldOpen(url, automatic: true));
        tracker.MarkOpened(url);
        Assert.IsFalse(tracker.ShouldOpen(url, automatic: true));
        Assert.IsTrue(tracker.ShouldOpen(url, automatic: false));
        Assert.IsTrue(tracker.ShouldOpen(url + "-changed", automatic: true));
    }

    [TestMethod]
    public void FailedLaunchDoesNotPreventRetry()
    {
        var tracker = new BrowserMonitorLaunchTracker();
        const string url = "http://127.0.0.1:54321/monitor/";
        Assert.IsTrue(tracker.ShouldOpen(url, automatic: true));
        Assert.IsTrue(tracker.ShouldOpen(url, automatic: true));
    }
}
