using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class PriorityCoordinatorTests
{
    [TestMethod]
    public void HigherPriorityPreemptsAndLowerPriorityWaits()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        DisplayProgram quake = CoordinatorTestSupport.Program(
            "quake", EventKind.Quake, OverlayPriority.Quake);
        DisplayProgram eew = CoordinatorTestSupport.Program(
            "eew", EventKind.Eew, OverlayPriority.Eew);

        coordinator.Apply(quake);
        CoordinatorSnapshot preempted = coordinator.Apply(eew);

        Assert.AreEqual("eew", preempted.CurrentProgram?.ProgramId);
        Assert.AreEqual(CoordinatorDecisionKind.Preempted, preempted.Decision.Kind);
        Assert.IsFalse(preempted.PendingPrograms.Any());
    }

    [TestMethod]
    public void SameKindWaitingSlotKeepsOnlyNewestIssuedProgram()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        DateTimeOffset issued = clock.UtcNow;
        coordinator.Apply(CoordinatorTestSupport.Program(
            "warning", EventKind.Tsunami, OverlayPriority.TsunamiWarning, issued,
            endPolicy: EndPolicy.HoldUntilCancelled));
        coordinator.Apply(CoordinatorTestSupport.Program(
            "quake-old", EventKind.Quake, OverlayPriority.Quake, issued));

        CoordinatorSnapshot snapshot = coordinator.Apply(CoordinatorTestSupport.Program(
            "quake-new", EventKind.Quake, OverlayPriority.Quake, issued.AddSeconds(1)));

        Assert.AreEqual(CoordinatorDecisionKind.Replaced, snapshot.Decision.Kind);
        Assert.HasCount(1, snapshot.PendingPrograms);
        Assert.AreEqual("quake-new", snapshot.PendingPrograms[0].ProgramId);
    }

    [TestMethod]
    public void SimultaneousWeatherTelegramsAreQueuedPerEventAndAllBecomeVisible()
    {
        var clock = new FakeClock();
        var settings = CoordinatorTestSupport.Settings(autoHideSeconds: 5) with
        {
            WeatherWarningAutoHideSeconds = 5,
        };
        var coordinator = new PriorityCoordinator(clock, settings);
        DateTimeOffset issued = clock.UtcNow;
        string[] telegrams =
        [
            "VPWW55", "VPWW56", "VPWW57", "VPWW58", "VPWW59", "VPWW60", "VPWW61",
        ];

        CoordinatorSnapshot snapshot = coordinator.Apply(CoordinatorTestSupport.Program(
            telegrams[0],
            EventKind.WeatherWarning,
            OverlayPriority.WeatherWarning,
            issued,
            pageCount: 1,
            eventId: $"weather-{telegrams[0]}"));
        foreach (string telegram in telegrams.Skip(1))
        {
            snapshot = coordinator.Apply(CoordinatorTestSupport.Program(
                telegram,
                EventKind.WeatherWarning,
                OverlayPriority.WeatherWarning,
                issued,
                pageCount: 1,
                eventId: $"weather-{telegram}"));
        }

        Assert.AreEqual("VPWW55", snapshot.CurrentProgram?.ProgramId);
        Assert.HasCount(telegrams.Length - 1, snapshot.PendingPrograms);
        CollectionAssert.AreEqual(
            telegrams.Skip(1).ToArray(),
            snapshot.PendingPrograms.Select(static program => program.ProgramId).ToArray());

        foreach (string expected in telegrams.Skip(1))
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            snapshot = coordinator.Evaluate();
            Assert.AreEqual(expected, snapshot.CurrentProgram?.ProgramId);
            Assert.AreEqual(CoordinatorDecisionKind.Promoted, snapshot.Decision.Kind);
            Assert.AreEqual(TimeSpan.Zero, snapshot.Elapsed);
        }

        clock.Advance(TimeSpan.FromSeconds(5));
        snapshot = coordinator.Evaluate();
        Assert.IsNull(snapshot.CurrentProgram);
    }

    [TestMethod]
    public void HigherPriorityWeatherTelegramPreemptsThenInterruptedWarningRestarts()
    {
        var clock = new FakeClock();
        var settings = CoordinatorTestSupport.Settings(autoHideSeconds: 5) with
        {
            WeatherWarningAutoHideSeconds = 5,
        };
        var coordinator = new PriorityCoordinator(clock, settings);
        coordinator.Apply(CoordinatorTestSupport.Program(
            "VPWW58-warning",
            EventKind.WeatherWarning,
            OverlayPriority.WeatherWarning,
            clock.UtcNow,
            pageCount: 1,
            eventId: "weather-VPWW58"));
        clock.Advance(TimeSpan.FromSeconds(2));

        CoordinatorSnapshot special = coordinator.Apply(CoordinatorTestSupport.Program(
            "VPWW55-special",
            EventKind.WeatherWarning,
            OverlayPriority.WeatherSpecialWarning,
            clock.UtcNow,
            pageCount: 1,
            eventId: "weather-VPWW55"));

        Assert.AreEqual("VPWW55-special", special.CurrentProgram?.ProgramId);
        Assert.AreEqual("VPWW58-warning", special.PendingPrograms.Single().ProgramId);

        clock.Advance(TimeSpan.FromSeconds(5));
        CoordinatorSnapshot resumed = coordinator.Evaluate();
        Assert.AreEqual("VPWW58-warning", resumed.CurrentProgram?.ProgramId);
        Assert.AreEqual(TimeSpan.Zero, resumed.Elapsed);
    }

    [TestMethod]
    public void WaitingWeatherUpdateReplacesOnlyTheSameEvent()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        DateTimeOffset issued = clock.UtcNow;
        coordinator.Apply(CoordinatorTestSupport.Program(
            "tsunami",
            EventKind.Tsunami,
            OverlayPriority.TsunamiWarning,
            issued,
            endPolicy: EndPolicy.HoldUntilCancelled));
        coordinator.Apply(CoordinatorTestSupport.Program(
            "VPWW55-v1",
            EventKind.WeatherWarning,
            OverlayPriority.WeatherSpecialWarning,
            issued,
            eventId: "weather-VPWW55"));
        coordinator.Apply(CoordinatorTestSupport.Program(
            "VPWW56",
            EventKind.WeatherWarning,
            OverlayPriority.WeatherWarning,
            issued,
            eventId: "weather-VPWW56"));

        CoordinatorSnapshot updated = coordinator.Apply(CoordinatorTestSupport.Program(
            "VPWW55-v2",
            EventKind.WeatherWarning,
            OverlayPriority.WeatherSpecialWarning,
            issued.AddSeconds(1),
            eventId: "weather-VPWW55"));

        Assert.HasCount(2, updated.PendingPrograms);
        Assert.IsFalse(updated.PendingPrograms.Any(static item => item.ProgramId == "VPWW55-v1"));
        Assert.IsTrue(updated.PendingPrograms.Any(static item => item.ProgramId == "VPWW55-v2"));
        Assert.IsTrue(updated.PendingPrograms.Any(static item => item.ProgramId == "VPWW56"));
    }

    [TestMethod]
    public void NewerEqualPriorityProgramPreemptsAndOlderOneIsIgnored()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        DateTimeOffset issued = clock.UtcNow;
        coordinator.Apply(CoordinatorTestSupport.Program(
            "eew-first", EventKind.Eew, OverlayPriority.Eew, issued));

        CoordinatorSnapshot newer = coordinator.Apply(CoordinatorTestSupport.Program(
            "eew-newer", EventKind.Eew, OverlayPriority.Eew, issued.AddSeconds(1)));
        CoordinatorSnapshot older = coordinator.Apply(CoordinatorTestSupport.Program(
            "eew-older", EventKind.Eew, OverlayPriority.Eew, issued.AddMilliseconds(500)));

        Assert.AreEqual(CoordinatorDecisionKind.Preempted, newer.Decision.Kind);
        Assert.AreEqual(CoordinatorDecisionKind.IgnoredOlderUpdate, older.Decision.Kind);
        Assert.AreEqual("eew-newer", older.CurrentProgram?.ProgramId);
    }

    [TestMethod]
    public void SameEventUpdateReplacesInsteadOfAdding()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        DateTimeOffset issued = clock.UtcNow;
        coordinator.Apply(CoordinatorTestSupport.Program(
            "quake-v1", EventKind.Quake, OverlayPriority.Quake, issued,
            eventId: "quake-event"));

        CoordinatorSnapshot snapshot = coordinator.Apply(CoordinatorTestSupport.Program(
            "quake-v2", EventKind.Quake, OverlayPriority.Quake, issued.AddSeconds(1),
            eventId: "quake-event"));

        Assert.AreEqual(CoordinatorDecisionKind.Replaced, snapshot.Decision.Kind);
        Assert.AreEqual("quake-v2", snapshot.CurrentProgram?.ProgramId);
        Assert.IsFalse(snapshot.PendingPrograms.Any());
    }

    [TestMethod]
    public void WaitingSameEventPriorityUpgradeCanPreemptCurrentProgram()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        DateTimeOffset issued = clock.UtcNow;
        coordinator.Apply(CoordinatorTestSupport.Program(
            "eew", EventKind.Eew, OverlayPriority.Eew, issued));
        coordinator.Apply(CoordinatorTestSupport.Program(
            "tsunami-watch", EventKind.Tsunami, OverlayPriority.TsunamiWatch, issued,
            endPolicy: EndPolicy.LoopUntilReplaced,
            eventId: "tsunami-event"));

        CoordinatorSnapshot snapshot = coordinator.Apply(CoordinatorTestSupport.Program(
            "tsunami-warning", EventKind.Tsunami, OverlayPriority.TsunamiWarning,
            issued.AddSeconds(1),
            endPolicy: EndPolicy.LoopUntilReplaced,
            eventId: "tsunami-event"));

        Assert.AreEqual("tsunami-warning", snapshot.CurrentProgram?.ProgramId);
        Assert.AreEqual(CoordinatorDecisionKind.Preempted, snapshot.Decision.Kind);
    }

    [TestMethod]
    public void ContinuingTsunamiReturnsAfterEewWithOriginalCycleStart()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(
            clock,
            CoordinatorTestSupport.Settings(autoHideSeconds: 5));
        coordinator.Apply(CoordinatorTestSupport.Program(
            "tsunami", EventKind.Tsunami, OverlayPriority.TsunamiWatch,
            pageCount: 3,
            endPolicy: EndPolicy.LoopUntilReplaced));
        clock.Advance(TimeSpan.FromSeconds(5));
        coordinator.Apply(CoordinatorTestSupport.Program(
            "eew", EventKind.Eew, OverlayPriority.Eew, pageCount: 1));
        clock.Advance(TimeSpan.FromSeconds(5));

        CoordinatorSnapshot resumed = coordinator.Evaluate();

        Assert.AreEqual(CoordinatorDecisionKind.Promoted, resumed.Decision.Kind);
        Assert.AreEqual("tsunami", resumed.CurrentProgram?.ProgramId);
        Assert.AreEqual(2, resumed.CurrentPageIndex);
        Assert.AreEqual(TimeSpan.FromSeconds(10), resumed.Elapsed);
    }

    [TestMethod]
    public void TsunamiWarningOutranksEew()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        coordinator.Apply(CoordinatorTestSupport.Program(
            "warning", EventKind.Tsunami, OverlayPriority.TsunamiWarning,
            endPolicy: EndPolicy.HoldUntilCancelled));

        CoordinatorSnapshot snapshot = coordinator.Apply(CoordinatorTestSupport.Program(
            "eew", EventKind.Eew, OverlayPriority.Eew));

        Assert.AreEqual("warning", snapshot.CurrentProgram?.ProgramId);
        Assert.AreEqual("eew", snapshot.PendingPrograms.Single().ProgramId);
    }

    [TestMethod]
    public void OlderDifferentTsunamiProgramCannotReplaceNewerCurrentOne()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        DateTimeOffset issued = clock.UtcNow;
        coordinator.Apply(CoordinatorTestSupport.Program(
            "newer-watch", EventKind.Tsunami, OverlayPriority.TsunamiWatch,
            issued.AddSeconds(1),
            endPolicy: EndPolicy.LoopUntilReplaced));

        CoordinatorSnapshot snapshot = coordinator.Apply(CoordinatorTestSupport.Program(
            "older-watch", EventKind.Tsunami, OverlayPriority.TsunamiWatch,
            issued,
            endPolicy: EndPolicy.LoopUntilReplaced));

        Assert.AreEqual(CoordinatorDecisionKind.IgnoredOlderUpdate, snapshot.Decision.Kind);
        Assert.AreEqual("newer-watch", snapshot.CurrentProgram?.ProgramId);
    }

    [TestMethod]
    public void NonPersistentTsunamiUpdateClearsOldPersistentSlot()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        DateTimeOffset issued = clock.UtcNow;
        coordinator.Apply(CoordinatorTestSupport.Program(
            "tsunami-active", EventKind.Tsunami, OverlayPriority.TsunamiWatch,
            issued,
            endPolicy: EndPolicy.LoopUntilReplaced,
            eventId: "tsunami-event"));

        CoordinatorSnapshot snapshot = coordinator.Apply(CoordinatorTestSupport.Program(
            "tsunami-empty", EventKind.Tsunami, OverlayPriority.UnknownTsunami,
            issued.AddSeconds(1),
            pageCount: 1,
            eventId: "tsunami-event"));

        Assert.AreEqual("tsunami-empty", snapshot.CurrentProgram?.ProgramId);
        Assert.IsNull(snapshot.PersistentTsunami);
    }

    [TestMethod]
    public void TsunamiCancellationClearsPersistentSlotAndLastsTwentySeconds()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        coordinator.Apply(CoordinatorTestSupport.Program(
            "tsunami", EventKind.Tsunami, OverlayPriority.TsunamiWatch,
            endPolicy: EndPolicy.LoopUntilReplaced));
        coordinator.Apply(CoordinatorTestSupport.Program(
            "cancel", EventKind.Tsunami, OverlayPriority.TsunamiCancel,
            pageCount: 1,
            durationOverride: TimeSpan.FromSeconds(20)));

        clock.Advance(TimeSpan.FromSeconds(19.999));
        CoordinatorSnapshot visible = coordinator.Evaluate();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        CoordinatorSnapshot expired = coordinator.Evaluate();

        Assert.IsNull(visible.PersistentTsunami);
        Assert.AreEqual("cancel", visible.CurrentProgram?.ProgramId);
        Assert.IsNull(expired.CurrentProgram);
        Assert.AreEqual(CoordinatorDecisionKind.Expired, expired.Decision.Kind);
    }

    [TestMethod]
    public void ProductionStopsHistoryRehearsalAndSuppressesNewRehearsal()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        coordinator.Apply(CoordinatorTestSupport.Program(
            "history", EventKind.Quake, OverlayPriority.Quake,
            sourceMode: SourceMode.HistoryRehearsal,
            rehearsalLabel: "履歴再生"));

        CoordinatorSnapshot production = coordinator.Apply(CoordinatorTestSupport.Program(
            "production", EventKind.Quake, OverlayPriority.Quake));
        CoordinatorSnapshot ignored = coordinator.Apply(CoordinatorTestSupport.Program(
            "training", EventKind.Eew, OverlayPriority.Eew,
            sourceMode: SourceMode.ManualTest,
            rehearsalLabel: "訓練"));

        Assert.IsTrue(production.RehearsalStopped);
        Assert.AreEqual("production", production.CurrentProgram?.ProgramId);
        Assert.AreEqual(CoordinatorDecisionKind.IgnoredRehearsal, ignored.Decision.Kind);
        Assert.AreEqual("production", ignored.CurrentProgram?.ProgramId);
    }

    [TestMethod]
    public void MonotonicClockIsDefaultAndUtcResyncJumpsAfterSuspension()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        coordinator.Apply(CoordinatorTestSupport.Program(
            "quake", EventKind.Quake, OverlayPriority.Quake, pageCount: 5));
        clock.AdvanceUtcOnly(TimeSpan.FromSeconds(12));

        CoordinatorSnapshot monotonic = coordinator.Evaluate();
        CoordinatorSnapshot resynchronized = coordinator.Evaluate(resynchronizeFromUtc: true);
        clock.Advance(TimeSpan.FromSeconds(2));
        CoordinatorSnapshot afterResynchronization = coordinator.Evaluate();

        Assert.AreEqual(0, monotonic.CurrentPageIndex);
        Assert.AreEqual(3, resynchronized.CurrentPageIndex);
        Assert.AreEqual(TimeSpan.FromSeconds(12), resynchronized.Elapsed);
        Assert.AreEqual(3, afterResynchronization.CurrentPageIndex);
        Assert.AreEqual(TimeSpan.FromSeconds(14), afterResynchronization.Elapsed);
    }

    [TestMethod]
    public void PageDurationSettingTakesEffectOnNextEvaluation()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(
            clock,
            CoordinatorTestSupport.Settings(pageDurationSeconds: 4));
        coordinator.Apply(CoordinatorTestSupport.Program(
            "quake", EventKind.Quake, OverlayPriority.Quake, pageCount: 5));
        clock.Advance(TimeSpan.FromSeconds(6));

        CoordinatorSnapshot before = coordinator.Evaluate();
        CoordinatorSnapshot after = coordinator.UpdateSettings(
            CoordinatorTestSupport.Settings(pageDurationSeconds: 2));

        Assert.AreEqual(1, before.CurrentPageIndex);
        Assert.AreEqual(3, after.CurrentPageIndex);
        Assert.AreEqual(CoordinatorDecisionKind.SettingsUpdated, after.Decision.Kind);
    }

    [TestMethod]
    public void ManualPageSelectionIsClamped()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(
            clock,
            CoordinatorTestSupport.Settings(autoHideSeconds: 0));
        coordinator.Apply(CoordinatorTestSupport.Program(
            "manual", EventKind.Quake, OverlayPriority.Quake,
            pageCount: 3,
            endPolicy: EndPolicy.Manual));

        CoordinatorSnapshot selected = coordinator.SelectManualPage(99);

        Assert.AreEqual(2, selected.CurrentPageIndex);
    }

    [TestMethod]
    public void ManualClearRemovesCurrentPendingAndPersistentTsunamiState()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        coordinator.Apply(CoordinatorTestSupport.Program(
            "tsunami", EventKind.Tsunami, OverlayPriority.TsunamiWarning,
            endPolicy: EndPolicy.HoldUntilCancelled));
        coordinator.Apply(CoordinatorTestSupport.Program(
            "eew", EventKind.Eew, OverlayPriority.Eew));

        CoordinatorSnapshot cleared = coordinator.Clear();

        Assert.AreEqual(CoordinatorDecisionKind.Cleared, cleared.Decision.Kind);
        Assert.IsNull(cleared.CurrentProgram);
        Assert.IsNull(cleared.PersistentTsunami);
        Assert.IsFalse(cleared.PendingPrograms.Any());

        CoordinatorSnapshot next = coordinator.Apply(CoordinatorTestSupport.Program(
            "quake-after-clear", EventKind.Quake, OverlayPriority.Quake));
        Assert.AreEqual("quake-after-clear", next.CurrentProgram?.ProgramId);
    }
}
