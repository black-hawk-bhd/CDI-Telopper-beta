using EEWTelop.Application.Audio;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Bootstrap;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.Services;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

public sealed partial class Phase6ViewModelTests
{
    [TestMethod]
    public async Task EewContinuationsWaitForActualPlaybackCompletionAndCancellationInterrupts()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cdi-eew-sequence-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(path, [0]);
        try
        {
            AppSettings settings = AppSettings.CreateDefault() with
            {
                Audio = AudioSettings.Disabled with
                {
                    EewEnabled = true, EewInitialEnabled = true, EewContinuationEnabled = true,
                    EewCancellationEnabled = true, EewInitialFilePath = path,
                    EewContinuationFilePath = path, EewCancellationFilePath = path,
                },
            };
            AppServices services = CreateServices(ProviderConnectionState.Stopped,
                suppliedSettings: settings, audioPolicy: new AudioPolicy());
            DateTimeOffset now = services.Clock.UtcNow;
            var store = new ObsSnapshotStore(settings.Display, now);
            var server = new FakeObsServer();
            await using var vm = new ControlWindowViewModel(services, settings,
                new FakeConfirmationService(), new ImmediateUiDispatcher(), store, server);
            await WaitUntilAsync(() => server.StartCount == 1);
            server.SetClientCount(1);

            TestScenario scenario = TestScenarioCatalog.Create(now).Single(x => x.Id == "eew-expanding-15");
            var reports = scenario.Steps.Select(step =>
            {
                var training = (EewEvent)step.Event;
                return new EewEvent(training.Id, training.Provider, training.IssuedAt, training.ReceivedAt,
                    training.Signature, SourceMode.Production, training.Issue, training.Earthquake,
                    training.Areas, isWarning: true, isFinal: false, isCancelled: false, isTest: false);
            }).ToArray();

            vm.PlayEventAudio(reports[0], settings.Audio);
            ObsViewSnapshot initial = store.Read(ObsViewChannel.Eew, now);
            Assert.AreEqual("EewInitial", initial.AudioCue);

            vm.PlayEventAudio(reports[1], settings.Audio with { EewContinuationEnabled = false });
            vm.PlayEventAudio(reports[1], settings.Audio);
            vm.PlayEventAudio(reports[2], settings.Audio);
            Assert.AreEqual(initial.AudioSequence, store.Read(ObsViewChannel.Eew, now).AudioSequence);
            Assert.IsTrue(store.TryReportAudioPlayback(initial.AudioSequence, "Started", now, out _));
            Assert.AreEqual(initial.AudioSequence, store.Read(ObsViewChannel.Eew, now).AudioSequence);

            var completedAt = now.AddSeconds(12);
            Assert.IsTrue(store.TryReportAudioPlayback(initial.AudioSequence, "Completed", completedAt, out _));
            ObsViewSnapshot firstContinuation = store.Read(ObsViewChannel.Eew, completedAt);
            Assert.AreEqual("EewContinuation", firstContinuation.AudioCue);
            Assert.AreEqual(completedAt, firstContinuation.AudioIssuedAtUtc);
            Assert.AreNotEqual(initial.AudioSequence, firstContinuation.AudioSequence);
            Assert.IsFalse(store.TryReportAudioPlayback(initial.AudioSequence, "Completed", completedAt, out _));

            Assert.IsTrue(store.TryReportAudioPlayback(firstContinuation.AudioSequence, "Completed", completedAt, out _));
            ObsViewSnapshot secondContinuation = store.Read(ObsViewChannel.Eew, completedAt);
            Assert.AreEqual("EewContinuation", secondContinuation.AudioCue);
            Assert.AreNotEqual(firstContinuation.AudioSequence, secondContinuation.AudioSequence);

            vm.PlayEventAudio(reports[3], settings.Audio);
            Assert.AreEqual(secondContinuation.AudioSequence, store.Read(ObsViewChannel.Eew, now).AudioSequence);
            var cancellation = new EewEvent(reports[0].Id, reports[0].Provider, now, now, "cancel",
                SourceMode.Production, reports[3].Issue with { Serial = "5" }, null, [],
                isWarning: false, isFinal: false, isCancelled: true, isTest: false);
            vm.PlayEventAudio(cancellation, settings.Audio);
            ObsViewSnapshot cancelled = store.Read(ObsViewChannel.Eew, now);
            Assert.AreEqual("EewCancellation", cancelled.AudioCue);
            Assert.IsFalse(store.TryReportAudioPlayback(secondContinuation.AudioSequence, "Completed", now, out _));
            Assert.IsTrue(store.TryReportAudioPlayback(cancelled.AudioSequence, "Completed", now, out _));
            Assert.AreEqual(cancelled.AudioSequence, store.Read(ObsViewChannel.Eew, now).AudioSequence);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
