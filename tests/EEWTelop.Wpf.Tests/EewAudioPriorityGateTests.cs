using EEWTelop.Application.Audio;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class EewAudioPriorityGateTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 4, 20, 0, 0, TimeSpan.FromHours(9));

    [TestMethod]
    public void PendingEewBlocksOtherAudioBeforeObsReceivesThePlayCommand()
    {
        var gate = new EewAudioPriorityGate();
        long generation = gate.BeginEew();

        Assert.IsTrue(gate.IsActive(ObsAudioDiagnostics.Empty, Now, false));

        gate.CompleteEew(generation);
        Assert.IsFalse(gate.IsActive(ObsAudioDiagnostics.Empty, Now, true));
    }

    [TestMethod]
    public void QueuedOrStartedEewBlocksUntilPlaybackFinishes()
    {
        var gate = new EewAudioPriorityGate();
        var queued = new ObsAudioDiagnostics(
            AudioCueId.EewInitial.ToString(), "Queued", Now, 1);
        var started = queued with { PlaybackResult = "Started" };
        var completed = queued with { PlaybackResult = "Completed" };

        Assert.IsTrue(gate.IsActive(queued, Now, true));
        Assert.IsTrue(gate.IsActive(started, Now.AddSeconds(5), true));
        Assert.IsFalse(gate.IsActive(completed, Now.AddSeconds(5), true));
        Assert.IsFalse(gate.IsActive(started, Now.AddSeconds(5), false));
    }

    [TestMethod]
    public void NonEewCueNeverActivatesThePriorityGate()
    {
        var gate = new EewAudioPriorityGate();
        var warning = new ObsAudioDiagnostics(
            AudioCueId.TsunamiMajorWarning.ToString(), "Started", Now, 1);

        Assert.IsFalse(gate.IsActive(warning, Now, true));
    }

    [TestMethod]
    public void ResetInvalidatesAnEewThatHasNotReachedObsYet()
    {
        var gate = new EewAudioPriorityGate();
        long generation = gate.BeginEew();

        gate.Reset();

        Assert.IsFalse(gate.IsCurrent(generation));
        Assert.IsFalse(gate.IsActive(ObsAudioDiagnostics.Empty, Now, true));
    }
}
