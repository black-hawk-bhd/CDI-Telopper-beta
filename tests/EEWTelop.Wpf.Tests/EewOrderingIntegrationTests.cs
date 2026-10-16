using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Audio;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Operations;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Diagnostics;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using EEWTelop.Wpf.Obs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class EewOrderingIntegrationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 3, 48, 10, TimeSpan.Zero);

    [TestMethod]
    public async Task DisplayAudioAndApiShareOrderingAndAnomaliesAreArchivedEvenWhenRawLoggingIsOff()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cdi-eew-ordering-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppSettings settings = AppSettings.CreateDefault();
            var clock = new FixedClock(Start.AddMinutes(2));
            var coordinator = new PriorityCoordinator(clock, settings.Display);
            var pipeline = new EventIngestionPipeline(new JmaXmlEventNormalizer(new EventSignatureBuilder()),
                new EventVersionCache(), new PageComposer(), coordinator, settings.Display);
            var log = new UiLogBuffer();
            using var archive = new FileRawProviderMessageArchive(directory,
                settings.Log with { SaveRawProviderMessages = false }, log);
            await using var source = new UnusedSource();
            var alerts = new OperationalAlertCenter(TimeSpan.Zero);
            var reception = new EventReceptionService(source, pipeline, archive, alerts: alerts);
            var api = new ExternalEarthquakeState();
            var obs = new ObsSnapshotStore(settings.Display, clock.UtcNow);
            var audio = new AudioPolicy();
            AudioSettings sound = AudioSettings.Disabled with
            {
                EewEnabled = true, EewInitialEnabled = true, EewContinuationEnabled = true,
                EewCancellationEnabled = true, EewInitialFilePath = "initial.wav",
                EewContinuationFilePath = "continued.wav", EewCancellationFilePath = "cancel.wav",
                FileAudioConfigured = true,
            };
            var cues = new List<AudioCueId>();
            reception.EventProcessed += (_, result) =>
            {
                api.Observe(result);
                if (result.Status != EventIngestionStatus.Accepted || result.Event is null || result.Program is null) return;
                obs.PublishProgram(result.Event, result.Program, settings.Display, clock.UtcNow);
                AudioDecision decision = audio.Evaluate(result.Event, sound);
                if (decision.ShouldPlay && decision.Cue is { } cue) cues.Add(cue);
            };

            await reception.ProcessAsync(Raw("42", "発表", 0));
            Assert.IsFalse(Directory.Exists(directory));
            RawProviderMessage cancelRaw = Raw("37", "取消", 40);
            cancelRaw = cancelRaw with { Json = cancelRaw.Json.Replace(
                $"<ReportDateTime>{Start:O}</ReportDateTime>",
                $"<ReportDateTime>{Start.AddSeconds(-10):O}</ReportDateTime>", StringComparison.Ordinal) };
            EventIngestionResult cancelled = await reception.ProcessAsync(cancelRaw);
            Assert.IsTrue(cancelled.PreserveRawForReview);
            Assert.IsTrue(cancelled.Event!.IsCancelled);
            Assert.IsTrue(coordinator.Evaluate().CurrentProgram!.Pages[0].AccessibleText.Contains("取消", StringComparison.Ordinal));
            Assert.IsTrue(obs.Read(ObsViewChannel.Eew, clock.UtcNow).AccessibleText.Contains("取消", StringComparison.Ordinal));
            Assert.AreEqual(1, cues.Count(cue => cue == AudioCueId.EewCancellation));
            Assert.HasCount(1, Directory.GetFiles(directory, "*_ordering-review.provider.xml"));
            string archived = Directory.GetFiles(directory, "*_ordering-review.provider.xml").Single();
            Assert.AreEqual(cancelRaw.Payload, await File.ReadAllTextAsync(archived));

            await reception.ProcessAsync(Raw("99", "発表", 20));
            await reception.ProcessAsync(cancelRaw);
            await reception.ProcessAsync(Raw("99", "発表", 20) with { IsStateSnapshot = true });
            using JsonDocument state = JsonDocument.Parse(JsonSerializer.Serialize(api.ReadEew(clock.UtcNow)));
            JsonElement current = state.RootElement.GetProperty("events")[0];
            Assert.IsTrue(current.GetProperty("isCancelled").GetBoolean());
            Assert.AreEqual("37", current.GetProperty("serial").GetString());
            Assert.AreEqual(Start.AddSeconds(40), current.GetProperty("sourceMessageTime").GetDateTimeOffset());
            Assert.AreEqual(Start.AddSeconds(-10), current.GetProperty("issuedAt").GetDateTimeOffset());
            Assert.AreEqual(1, cues.Count(cue => cue == AudioCueId.EewCancellation));
            Assert.HasCount(1, Directory.GetFiles(directory, "*.xml"));

            EventIngestionResult ambiguous = await reception.ProcessAsync(Raw("99", "発表", 50,
                controlTime: "invalid"));
            Assert.IsTrue(ambiguous.RequiresReview);
            Assert.IsNull(ambiguous.Program);
            Assert.HasCount(2, Directory.GetFiles(directory, "*_ordering-review.provider.xml"));
            Assert.IsTrue(alerts.GetSnapshot().Any(alert => alert.Key == "eew-ordering-review"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnconfirmedXmlCancellationIsNotPublishedAndIsPreserved()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cdi-eew-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppSettings settings = AppSettings.CreateDefault();
            var pipeline = new EventIngestionPipeline(new JmaXmlEventNormalizer(new EventSignatureBuilder()),
                new EventVersionCache(), new PageComposer(), new PriorityCoordinator(new FixedClock(Start), settings.Display), settings.Display);
            using var archive = new FileRawProviderMessageArchive(directory, settings.Log, new UiLogBuffer());
            await using var source = new UnusedSource();
            var reception = new EventReceptionService(source, pipeline, archive);
            RawProviderMessage raw = Raw("37", "発表", 40);
            raw = raw with { Json = raw.Json.Replace("<Headline/>", "<Headline><Text>緊急地震速報を取消します</Text></Headline>", StringComparison.Ordinal) };
            EventIngestionResult result = await reception.ProcessAsync(raw);
            Assert.IsTrue(result.RequiresReview);
            Assert.AreEqual(EventIngestionStatus.Invalid, result.Status);
            Assert.IsNull(result.Program);
            Assert.AreEqual(raw.Payload, await File.ReadAllTextAsync(Directory.GetFiles(directory).Single()));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static RawProviderMessage Raw(string serial, string infoType, int seconds, string? controlTime = null) => new("dmdata.jp",
        $"""
        <Report><Control><Title>緊急地震速報（警報）</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice><DateTime>{controlTime ?? Start.AddSeconds(seconds).ToString("O", System.Globalization.CultureInfo.InvariantCulture)}</DateTime></Control>
        <Head><ReportDateTime>{Start:O}</ReportDateTime><EventID>20261016123810</EventID><Serial>{serial}</Serial><InfoType>{infoType}</InfoType><Headline/></Head><Body/></Report>
        """, SourceMode.Production, Start.AddMinutes(2)) { ContentFormat = RawProviderContentFormat.JmaXml };

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }

    private sealed class UnusedSource : IEventSource
    {
        public ProviderConnectionSnapshot Connection => new(ProviderConnectionState.Stopped, Start);
        public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged { add { } remove { } }
        public IAsyncEnumerable<RawProviderMessage> ReadAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void RequestReconnect(ReconnectReason reason) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
