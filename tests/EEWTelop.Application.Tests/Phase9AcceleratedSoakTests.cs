using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class Phase9AcceleratedSoakTests
{
    [TestMethod]
    public async Task SimulatedTwentyFourHoursKeepsTsunamiPagesAndLogsBounded()
    {
        var clock = new FakeClock();
        var coordinator = new PriorityCoordinator(clock, CoordinatorTestSupport.Settings());
        var logs = new UiLogBuffer();
        DisplayProgram tsunami = CoordinatorTestSupport.Program(
            "soak-tsunami",
            EventKind.Tsunami,
            OverlayPriority.TsunamiWarning,
            issuedAt: clock.UtcNow,
            pageCount: 5,
            endPolicy: EndPolicy.LoopUntilReplaced);
        coordinator.Apply(tsunami);

        for (int minute = 1; minute <= 24 * 60; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            if (minute % 15 == 0)
            {
                DisplayProgram eew = CoordinatorTestSupport.Program(
                    $"soak-eew-{minute}",
                    EventKind.Eew,
                    OverlayPriority.Eew,
                    issuedAt: clock.UtcNow,
                    pageCount: 2,
                    eventId: $"soak-eew-{minute}");
                coordinator.Apply(eew);
            }

            CoordinatorSnapshot snapshot = coordinator.Evaluate(resynchronizeFromUtc: true);
            Assert.IsNotNull(snapshot.CurrentProgram);
            Assert.IsTrue(snapshot.CurrentPageIndex >= 0);
            Assert.IsTrue(snapshot.PendingPrograms.Count <= 1);
            await logs.WriteAsync(new AppLogEntry(
                clock.UtcNow,
                AppLogLevel.Information,
                "SoakMinute",
                $"minute={minute};page={snapshot.CurrentPageIndex}"));
        }

        CoordinatorSnapshot final = coordinator.Evaluate(resynchronizeFromUtc: true);
        Assert.AreEqual("soak-tsunami", final.CurrentProgram?.ProgramId);
        Assert.HasCount(UiLogBuffer.MaximumCapacity, logs.GetSnapshot());
        Assert.AreEqual(24 * 60, int.Parse(
            logs.GetSnapshot()[^1].Message.Split('=')[1].Split(';')[0],
            System.Globalization.CultureInfo.InvariantCulture));
    }

}
