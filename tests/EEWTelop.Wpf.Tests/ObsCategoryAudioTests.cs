using EEWTelop.Application.Configuration;
using EEWTelop.Wpf.Obs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class ObsCategoryAudioTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
    private string _audioPath = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _audioPath = Path.Combine(Path.GetTempPath(), "cdi-category-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(_audioPath, [0]);
    }

    [TestCleanup]
    public void Cleanup() => File.Delete(_audioPath);

    [TestMethod]
    public void DifferentCategoriesKeepIndependentCommandsWhenReceivedBeforeNextPoll()
    {
        var store = CreateStore();
        ObsViewSnapshot quake = store.PublishAudio("QuakeIntensity4", _audioPath, Now);
        ObsViewSnapshot weather = store.PublishAudio("WeatherSpecialWarning", _audioPath, Now.AddMilliseconds(1));
        ObsViewSnapshot tsunami = store.PublishAudio("TsunamiMajorWarning", _audioPath, Now.AddMilliseconds(2));
        Assert.AreEqual(quake.AudioSequence, store.Read(ObsViewChannel.General, Now).AudioSequence);
        Assert.AreEqual(weather.AudioSequence, store.Read(ObsViewChannel.Weather, Now).AudioSequence);
        Assert.AreEqual(tsunami.AudioSequence, store.Read(ObsViewChannel.Tsunami, Now).AudioSequence);
        Assert.AreEqual(0, store.Read(ObsViewChannel.Eew, Now).AudioSequence);
        Assert.IsTrue(store.TryReportAudioPlayback(weather.AudioSequence, "Completed", Now, out ObsAudioDiagnostics report));
        Assert.AreEqual("WeatherSpecialWarning", report.Cue);
        Assert.AreEqual("TsunamiMajorWarning", store.ReadAudioDiagnostics().Cue);
        Assert.AreEqual("Queued", store.ReadAudioDiagnostics().PlaybackResult);
    }

    [TestMethod]
    public void StopReachesAllSourcesAndEewPlayCannotReviveAnotherCategory()
    {
        var store = CreateStore();
        ObsViewSnapshot quake = store.PublishAudio("QuakeIntensity4", _audioPath, Now);
        ObsViewSnapshot weather = store.PublishAudio("WeatherWarning", _audioPath, Now);
        ObsViewSnapshot stop = store.PublishAudioStop(Now.AddSeconds(1));
        foreach (ObsViewChannel channel in Enum.GetValues<ObsViewChannel>())
        {
            ObsViewSnapshot snapshot = store.Read(channel, Now);
            Assert.AreEqual(stop.AudioSequence, snapshot.AudioSequence);
            Assert.AreEqual("stop", snapshot.AudioAction);
            Assert.AreEqual(string.Empty, snapshot.AudioCue);
        }
        ObsViewSnapshot eew = store.PublishAudio("EewInitial", _audioPath, Now.AddSeconds(1));
        foreach (ObsViewChannel channel in Enum.GetValues<ObsViewChannel>())
        {
            ObsViewSnapshot snapshot = store.Read(channel, Now);
            Assert.AreEqual(channel == ObsViewChannel.Eew ? eew.AudioSequence : stop.AudioSequence, snapshot.AudioSequence);
            Assert.AreEqual(channel == ObsViewChannel.Eew ? "play" : "stop", snapshot.AudioAction);
        }
        Assert.IsFalse(store.TryReportAudioPlayback(quake.AudioSequence, "Completed", Now, out _));
        Assert.IsFalse(store.TryReportAudioPlayback(weather.AudioSequence, "Completed", Now, out _));
        Assert.AreEqual("EewInitial", store.ReadAudioDiagnostics().Cue);
        Assert.IsTrue(store.TryReportAudioPlayback(eew.AudioSequence, "Completed", Now, out _));
        Assert.AreEqual("Completed", store.ReadAudioDiagnostics().PlaybackResult);
    }

    [TestMethod]
    public void LongEewSoundCanReportCompletionAfterItsFileReferenceExpires()
    {
        var store = CreateStore();
        ObsViewSnapshot eew = store.PublishAudio("EewInitial", _audioPath, Now);
        DateTimeOffset completedAt = Now.AddSeconds(61);
        Assert.IsFalse(store.TryReadAudio(eew.AudioSequence, completedAt, out _));
        Assert.IsTrue(store.TryReportAudioPlayback(eew.AudioSequence, "Completed", completedAt, out _));
        Assert.AreEqual("Completed", store.ReadAudioDiagnostics().PlaybackResult);
        var gate = new EEWTelop.Wpf.Services.EewAudioPriorityGate();
        Assert.IsFalse(gate.IsActive(store.ReadAudioDiagnostics(), completedAt, true));
    }

    [TestMethod]
    public void ReplacementRejectsOldPlaybackReportsWithinTheSameCategory()
    {
        var store = CreateStore();
        ObsViewSnapshot old = store.PublishAudio("TsunamiWarning", _audioPath, Now);
        ObsViewSnapshot current = store.PublishAudio("TsunamiMajorWarning", _audioPath, Now.AddSeconds(1));
        Assert.IsFalse(store.TryReportAudioPlayback(old.AudioSequence, "Completed", Now, out _));
        Assert.IsTrue(store.TryReportAudioPlayback(current.AudioSequence, "Started", Now, out _));
    }

    [TestMethod]
    public void UnknownCueCannotFallBackToTheEarthquakeSource()
    {
        var store = CreateStore();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.PublishAudio("Unknown", _audioPath, Now));
        Assert.AreEqual(0, store.Read().AudioSequence);
        foreach (ObsViewChannel channel in Enum.GetValues<ObsViewChannel>())
            Assert.AreEqual(0, store.Read(channel, Now).AudioSequence);
    }

    private static ObsSnapshotStore CreateStore() => new(AppSettings.CreateDefault().Display, Now);
}
