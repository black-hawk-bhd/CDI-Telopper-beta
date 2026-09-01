using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class PageClockTests
{
    private readonly PageClock _clock = new();
    private readonly DateTimeOffset _startedAt =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(3.999, 0)]
    [DataRow(4, 1)]
    [DataRow(8, 2)]
    public void AutoHideAdvancesPagesFromElapsedTime(double seconds, int expectedIndex)
    {
        DisplayProgram program = CoordinatorTestSupport.Program(
            "quake", EventKind.Quake, OverlayPriority.Quake);

        PageClockResult result = _clock.Evaluate(
            program,
            CoordinatorTestSupport.Settings(),
            _startedAt,
            TimeSpan.FromSeconds(seconds));

        Assert.AreEqual(expectedIndex, result.Index);
        Assert.IsFalse(result.IsExpired);
    }

    [TestMethod]
    public void AutoHideGuaranteesFiveSecondsOnLastPage()
    {
        DisplayProgram program = CoordinatorTestSupport.Program(
            "quake", EventKind.Quake, OverlayPriority.Quake, pageCount: 4);

        PageClockResult before = _clock.Evaluate(
            program,
            CoordinatorTestSupport.Settings(autoHideSeconds: 5),
            _startedAt,
            TimeSpan.FromSeconds(16.999));
        PageClockResult atExpiry = _clock.Evaluate(
            program,
            CoordinatorTestSupport.Settings(autoHideSeconds: 5),
            _startedAt,
            TimeSpan.FromSeconds(17));

        Assert.AreEqual(3, before.Index);
        Assert.IsFalse(before.IsExpired);
        Assert.IsTrue(atExpiry.IsExpired);
        Assert.AreEqual(_startedAt.AddSeconds(17), atExpiry.ExpiresAtUtc);
    }

    [TestMethod]
    public void ZeroAutoHideKeepsLastPageVisible()
    {
        DisplayProgram program = CoordinatorTestSupport.Program(
            "quake", EventKind.Quake, OverlayPriority.Quake);

        PageClockResult result = _clock.Evaluate(
            program,
            CoordinatorTestSupport.Settings(autoHideSeconds: 0),
            _startedAt,
            TimeSpan.FromHours(1));

        Assert.AreEqual(2, result.Index);
        Assert.IsFalse(result.IsExpired);
        Assert.IsNull(result.ExpiresAtUtc);
    }

    [TestMethod]
    public void EewUsesIndependentAutoHideSetting()
    {
        DisplayProgram eew = CoordinatorTestSupport.Program(
            "eew", EventKind.Eew, OverlayPriority.Eew, pageCount: 1);
        DisplayProgram quake = CoordinatorTestSupport.Program(
            "quake", EventKind.Quake, OverlayPriority.Quake, pageCount: 1);
        var settings = CoordinatorTestSupport.Settings(
            autoHideSeconds: 45,
            eewAutoHideSeconds: 10);

        PageClockResult eewResult = _clock.Evaluate(
            eew, settings, _startedAt, TimeSpan.FromSeconds(10));
        PageClockResult quakeResult = _clock.Evaluate(
            quake, settings, _startedAt, TimeSpan.FromSeconds(10));

        Assert.IsTrue(eewResult.IsExpired);
        Assert.IsFalse(quakeResult.IsExpired);
        Assert.AreEqual(_startedAt.AddSeconds(10), eewResult.ExpiresAtUtc);
        Assert.AreEqual(_startedAt.AddSeconds(45), quakeResult.ExpiresAtUtc);
    }

    [TestMethod]
    public void QuakeAndTsunamiUseIndependentAutoHideSettings()
    {
        DisplayProgram quake = CoordinatorTestSupport.Program(
            "quake", EventKind.Quake, OverlayPriority.Quake, pageCount: 1);
        DisplayProgram tsunami = CoordinatorTestSupport.Program(
            "tsunami", EventKind.Tsunami, OverlayPriority.TsunamiWatch, pageCount: 1);
        var settings = CoordinatorTestSupport.Settings(autoHideSeconds: 45) with
        {
            QuakeAutoHideSeconds = 12,
            TsunamiAutoHideSeconds = 30,
        };

        PageClockResult quakeResult = _clock.Evaluate(
            quake, settings, _startedAt, TimeSpan.FromSeconds(12));
        PageClockResult tsunamiBefore = _clock.Evaluate(
            tsunami, settings, _startedAt, TimeSpan.FromSeconds(29));
        PageClockResult tsunamiAtExpiry = _clock.Evaluate(
            tsunami, settings, _startedAt, TimeSpan.FromSeconds(30));

        Assert.IsTrue(quakeResult.IsExpired);
        Assert.IsFalse(tsunamiBefore.IsExpired);
        Assert.IsTrue(tsunamiAtExpiry.IsExpired);
    }

    [TestMethod]
    public void LegacyDisplaySettingsMakeEewInheritCommonAutoHide()
    {
        DisplayProgram eew = CoordinatorTestSupport.Program(
            "eew", EventKind.Eew, OverlayPriority.Eew, pageCount: 1);
        var settings = CoordinatorTestSupport.Settings(autoHideSeconds: 12) with
        {
            EewAutoHideSeconds = -1,
        };

        PageClockResult result = _clock.Evaluate(
            eew, settings, _startedAt, TimeSpan.FromSeconds(12));

        Assert.IsTrue(result.IsExpired);
        Assert.AreEqual(_startedAt.AddSeconds(12), result.ExpiresAtUtc);
    }

    [TestMethod]
    public void LoopPolicyUsesModuloAndNeverExpires()
    {
        DisplayProgram program = CoordinatorTestSupport.Program(
            "tsunami",
            EventKind.Tsunami,
            OverlayPriority.TsunamiWatch,
            pageCount: 3,
            endPolicy: EndPolicy.LoopUntilReplaced);

        PageClockResult result = _clock.Evaluate(
            program,
            CoordinatorTestSupport.Settings(autoHideSeconds: 1),
            _startedAt,
            TimeSpan.FromSeconds(28));

        Assert.AreEqual(1, result.Index);
        Assert.IsFalse(result.IsExpired);
        Assert.IsNull(result.Remaining);
    }

    [TestMethod]
    public void ManualPolicyUsesSelectedPage()
    {
        DisplayProgram program = CoordinatorTestSupport.Program(
            "manual",
            EventKind.Quake,
            OverlayPriority.Quake,
            pageCount: 4,
            endPolicy: EndPolicy.Manual);

        PageClockResult result = _clock.Evaluate(
            program,
            CoordinatorTestSupport.Settings(autoHideSeconds: 0),
            _startedAt,
            TimeSpan.FromMinutes(1),
            manualPageIndex: 2);

        Assert.AreEqual(2, result.Index);
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(1.24, 1)]
    [DataRow(1.25, 1.5)]
    [DataRow(30.6, 30)]
    [DataRow(double.NaN, 4)]
    public void PageDurationIsClampedAndRoundedToHalfSeconds(
        double input,
        double expectedSeconds)
    {
        Assert.AreEqual(
            TimeSpan.FromSeconds(expectedSeconds),
            PageClock.NormalizePageDuration(input));
    }
}
