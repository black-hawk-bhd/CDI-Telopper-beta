using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class PageClockTests
{
    [TestMethod]
    [DataRow(EventKind.Quake)]
    [DataRow(EventKind.Tsunami)]
    [DataRow(EventKind.WeatherWarning)]
    public void ProductionPassExpiresAfterAllPagesRegardlessOfPersistentEndPolicy(EventKind kind)
    {
        var program = CoordinatorTestSupport.Program("finite", kind, OverlayPriority.Quake, pageCount: 3) with
        {
            SourceMode = SourceMode.Production,
            EndPolicy = EndPolicy.LoopUntilReplaced,
        };
        var policy = new EEWTelop.Application.Configuration.ProductionReplayPolicy(true, 2, false);
        var settings = CoordinatorTestSupport.Settings(autoHideSeconds: 0);
        settings = settings with
        {
            ProductionReplay = settings.ProductionReplay with
            {
                Quake = policy, Tsunami = policy, WeatherWarning = policy,
                RotationIntervalSeconds = 300, ResumeDelaySeconds = 300,
            },
        };
        Assert.IsFalse(_clock.Evaluate(program, settings, _startedAt, TimeSpan.FromSeconds(11.9)).IsExpired);
        Assert.IsTrue(_clock.Evaluate(program, settings, _startedAt, TimeSpan.FromSeconds(12)).IsExpired);
        Assert.IsTrue(_clock.Evaluate(program, settings, _startedAt, TimeSpan.FromDays(1)).IsExpired);
        var boundedReplay = program with { MaximumDisplayCycles = 1 };
        Assert.IsTrue(_clock.Evaluate(boundedReplay, CoordinatorTestSupport.Settings(autoHideSeconds: 0),
            _startedAt, TimeSpan.FromSeconds(12)).IsExpired);
    }

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
    public void EewAutoHideGuaranteesFiveSecondsOnLastPage()
    {
        DisplayProgram program = CoordinatorTestSupport.Program(
            "eew", EventKind.Eew, OverlayPriority.Eew, pageCount: 4);

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
    public void ZeroEewAutoHideKeepsLastPageVisible()
    {
        DisplayProgram program = CoordinatorTestSupport.Program(
            "eew", EventKind.Eew, OverlayPriority.Eew);

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
        Assert.IsTrue(quakeResult.IsExpired);
        Assert.AreEqual(_startedAt.AddSeconds(10), eewResult.ExpiresAtUtc);
        Assert.AreEqual(_startedAt.AddSeconds(4), quakeResult.ExpiresAtUtc);
    }

    [TestMethod]
    [DataRow(EventKind.Quake, 1, 4.0)]
    [DataRow(EventKind.Quake, 3, 4.0)]
    [DataRow(EventKind.Tsunami, 2, 1.5)]
    [DataRow(EventKind.WeatherWarning, 3, 2.5)]
    [DataRow(EventKind.Volcano, 1, 2.0)]
    public void NonEewAutoHideExpiresAfterAllPages(EventKind kind, int pageCount, double pageSeconds)
    {
        DisplayProgram program = CoordinatorTestSupport.Program(
            "one-pass", kind, OverlayPriority.Quake, pageCount: pageCount);
        var settings = CoordinatorTestSupport.Settings(autoHideSeconds: 0, pageDurationSeconds: pageSeconds);
        TimeSpan lifetime = TimeSpan.FromSeconds(pageCount * pageSeconds);

        PageClockResult before = _clock.Evaluate(
            program, settings, _startedAt, lifetime - TimeSpan.FromMilliseconds(1));
        PageClockResult atExpiry = _clock.Evaluate(program, settings, _startedAt, lifetime);

        Assert.AreEqual(pageCount - 1, before.Index);
        Assert.IsFalse(before.IsExpired);
        Assert.IsTrue(atExpiry.IsExpired);
        Assert.AreEqual(_startedAt + lifetime, atExpiry.ExpiresAtUtc);
    }

    [TestMethod]
    [DataRow(QuakeIssueType.ScalePrompt, "", "VXSE51", 2.5)]
    [DataRow(QuakeIssueType.ScaleAndDestination, "", "VXSE53", 6.0)]
    [DataRow(QuakeIssueType.Other, " vxse61 ", "VXSE61", 4.0)]
    public void QuakeBulletinsUseTheirOwnPageDurationOrFallBackToCommon(
        QuakeIssueType issueType, string rawType, string expectedKey, double expectedSeconds)
    {
        var settings = CoordinatorTestSupport.Settings(pageDurationSeconds: 4) with
        {
            TelegramPageDurationSeconds = new() { ["VXSE51"] = 2.5, ["VXSE53"] = 6 },
        };
        QuakeEvent quake = DisplayEventFactory.CreateQuake(issueType, rawType: rawType, comment: "検証用");
        DisplayProgram program = new PageComposer().Compose(quake, settings);
        TimeSpan duration = TimeSpan.FromSeconds(expectedSeconds);
        TimeSpan lifetime = duration * program.Pages.Count;

        Assert.AreEqual(expectedKey, program.TelegramPageDurationKey);
        Assert.AreEqual(duration, PageClock.GetPageDuration(settings, program));
        PageClockResult first = _clock.Evaluate(program, settings, _startedAt,
            duration - TimeSpan.FromMilliseconds(1));
        PageClockResult next = _clock.Evaluate(program, settings, _startedAt, duration);
        PageClockResult expired = _clock.Evaluate(program, settings, _startedAt, lifetime);
        Assert.AreEqual(0, first.Index);
        Assert.IsFalse(first.IsExpired);
        Assert.AreEqual(Math.Min(1, program.Pages.Count - 1), next.Index);
        Assert.AreEqual(_startedAt + lifetime, next.ExpiresAtUtc);
        Assert.IsTrue(expired.IsExpired);
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
        Assert.IsFalse(result.IsExpired);
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
