using System.Text.Json;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class EewOrderingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 3, 48, 10, TimeSpan.Zero);

    [TestMethod]
    [DataRow("dmdata.jp")]
    [DataRow("axis")]
    [DataRow("p2pquake")]
    [DataRow("wolfx")]
    public void NewerCancellationCanRegressSerialWithoutChangingRawNumber(string provider)
    {
        var cache = new EventVersionCache();
        Assert.IsTrue(cache.TryAccept(Item("42", 0)));
        EewEvent cancel = Item("37", 40, true, provider);
        EventVersionDecision decision = cache.CheckAndAccept(cancel);
        Assert.IsTrue(decision.Accepted);
        Assert.IsNotNull(decision.Issue);
        Assert.IsTrue(decision.PreserveRaw);
        Assert.AreEqual("eew.ordering.serialRegression", decision.Issue.Path);
        Assert.AreEqual("37", cancel.Issue.Serial);
        Assert.IsTrue(cancel.IsCancelled);
        Assert.IsFalse(cache.TryAccept(Item("42", 40, true, "wolfx")));
        Assert.IsFalse(cache.TryAccept(Item("99", 20)));
        Assert.IsFalse(cache.TryAccept(Item("43", 40)));
        Assert.IsTrue(cache.TryAccept(Item("43", 60)));
    }

    [TestMethod]
    public void OldCancellationIsRejectedEvenWithHigherSerialAndLateReceipt()
    {
        var cache = new EventVersionCache();
        Assert.IsTrue(cache.TryAccept(Item("42", 30)));
        EventVersionDecision result = cache.CheckAndAccept(Item("99", 20, true));
        Assert.AreEqual(EventVersionDisposition.Stale, result.Disposition);
        Assert.IsFalse(cache.TryAccept(Item("41", 40)));
        Assert.IsTrue(cache.TryAccept(Item("42", 30, true)));
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void AmbiguousRegressedCancellationIsHeldForReview(bool firstTimeKnown, bool cancelTimeKnown)
    {
        var cache = new EventVersionCache();
        Assert.IsTrue(cache.TryAccept(Item("42", 0, knownTime: firstTimeKnown)));
        // Even if both are known, equal timestamps cannot justify a regression.
        EventVersionDecision result = cache.CheckAndAccept(Item("37", 0, true, knownTime: cancelTimeKnown));
        Assert.IsTrue(result.RequiresReview);
        Assert.IsTrue(result.PreserveRaw);
        Assert.IsNotNull(result.Issue);
        Assert.AreEqual("eew.ordering.reviewRequired", result.Issue.Path);
        Assert.IsTrue(cache.TryAccept(Item("43", 20)));
    }

    [TestMethod]
    public void FutureTimestampIsNotUsedToOverrideCurrentState()
    {
        var cache = new EventVersionCache();
        Assert.IsTrue(cache.TryAccept(Item("42", 0)));
        EewEvent corrupt = Item("37", 40, true) with { SourceMessageTime = Start.AddDays(1) };
        Assert.IsTrue(cache.CheckAndAccept(corrupt).RequiresReview);
        Assert.IsTrue(cache.TryAccept(Item("43", 20)));
    }

    [TestMethod]
    public void CancellationTombstoneSurvivesSnapshotAndOldReportsCannotResurrect()
    {
        var cache = new EventVersionCache();
        Assert.IsTrue(cache.TryAccept(Item("42", 0)));
        EewEvent cancel = Item("37", 40, true);
        Assert.IsTrue(cache.TryAccept(cancel));
        string json = JsonSerializer.Serialize(cache.GetSnapshot());
        var restored = new EventVersionCache();
        restored.Restore(JsonSerializer.Deserialize<StoredEventSignature[]>(json)!);
        Assert.IsFalse(restored.TryAccept(Item("99", 20)));
        Assert.IsFalse(restored.TryAccept(Item("42", 40)));
        Assert.IsFalse(restored.TryAccept(cancel));
        Assert.IsFalse(restored.TryAccept(Item("99", 60, knownTime: false)));
        Assert.IsFalse(restored.TryAccept(Item("41", 60)));
        Assert.IsTrue(restored.TryAccept(Item("43", 60)));
    }

    [TestMethod]
    [DataRow(SourceMode.Sandbox, false)]
    [DataRow(SourceMode.ManualTest, false)]
    [DataRow(SourceMode.HistoryRehearsal, false)]
    [DataRow(SourceMode.Production, true)]
    public void ProductionAndTrainingOrderingStatesRemainIndependent(SourceMode mode, bool test)
    {
        var cache = new EventVersionCache();
        Assert.IsTrue(cache.TryAccept(Item("42", 0, mode: mode, test: test)));
        Assert.IsTrue(cache.TryAccept(Item("37", 40, true, mode: mode, test: test)));
        Assert.IsTrue(cache.TryAccept(Item("1", 0)));
        Assert.IsTrue(cache.TryAccept(Item("2", 20)));
        var restored = new EventVersionCache();
        restored.Restore(cache.GetSnapshot());
        Assert.IsFalse(restored.TryAccept(Item("99", 20, mode: mode, test: test)));
        Assert.IsTrue(restored.TryAccept(Item("3", 30)));
    }

    [TestMethod]
    public void SeparateEarthquakeDoesNotShareCancellationState()
    {
        var cache = new EventVersionCache();
        Assert.IsTrue(cache.TryAccept(Item("42", 0)));
        Assert.IsTrue(cache.TryAccept(Item("37", 40, true)));
        Assert.IsTrue(cache.TryAccept(Item("1", 0, id: "different-event")));
    }

    [TestMethod]
    public void RejectedProviderCopyDoesNotAdvanceSourceTime()
    {
        var cache = new EventVersionCache();
        Assert.IsTrue(cache.TryAccept(Item("42", 0)));
        Assert.IsFalse(cache.TryAccept(Item("42", 50, provider: "axis")));
        Assert.IsTrue(cache.TryAccept(Item("37", 40, true)));
    }

    [TestMethod]
    public void EewOrderingHistoryRemainsBounded()
    {
        var cache = new EventVersionCache(keyLimit: 1);
        Assert.IsTrue(cache.TryAccept(Item("42", 0)));
        Assert.IsTrue(cache.TryAccept(Item("37", 40, true)));
        Assert.IsTrue(cache.TryAccept(Item("1", 0, id: "different-event")));
        Assert.IsTrue(cache.TryAccept(Item("1", 0)));
    }

    [TestMethod]
    public void PipelineAcceptsNewCancellationAndRejectsOlderStateSnapshots()
    {
        var clock = new FakeClock(Start.AddMinutes(2));
        var normalizer = new QueueNormalizer(Item("42", 0), Item("37", 40, true),
            Item("99", 20), Item("37", 40, true), Item("99", 50, knownTime: false));
        var coordinator = new PriorityCoordinator(clock, AppSettings.CreateDefault().Display);
        var pipeline = new EventIngestionPipeline(normalizer, new EventVersionCache(), new PageComposer(),
            coordinator, AppSettings.CreateDefault().Display);
        RawProviderMessage raw = new("dmdata.jp", "{}", SourceMode.Production, clock.UtcNow);
        Assert.AreEqual(EventIngestionStatus.Accepted, pipeline.Process(raw).Status);
        EventIngestionResult cancelled = pipeline.Process(raw);
        Assert.AreEqual(EventIngestionStatus.Accepted, cancelled.Status);
        Assert.IsTrue(cancelled.PreserveRawForReview);
        Assert.Contains("緊急地震速報", cancelled.Program!.Pages[0].AccessibleText);
        Assert.AreEqual(EventIngestionStatus.Duplicate, pipeline.Process(raw with { IsStateSnapshot = true }).Status);
        EventIngestionResult duplicateSync = pipeline.Process(raw with { IsStateSnapshot = true });
        Assert.AreEqual(EventIngestionStatus.Accepted, duplicateSync.Status);
        Assert.IsNull(duplicateSync.Program);
        EventIngestionResult ambiguous = pipeline.Process(raw);
        Assert.IsTrue(ambiguous.RequiresReview);
        Assert.IsTrue(ambiguous.PreserveRawForReview);
        Assert.IsNull(ambiguous.Program);
        Assert.IsTrue(coordinator.Evaluate().CurrentProgram!.Pages[0].AccessibleText.Contains("取消", StringComparison.Ordinal));
    }

    private static EewEvent Item(string serial, int seconds, bool cancelled = false, string provider = "dmdata.jp",
        bool knownTime = true, SourceMode mode = SourceMode.Production, bool test = false, string id = "eew-serial-regression")
    {
        DateTimeOffset time = Start.AddSeconds(seconds);
        var item = new EewEvent(EventId.Create(id), provider, time, Start.AddMinutes(2), "", mode,
            new IssueInfo("気象庁", time, "EEW", CorrectionType.None, serial, cancelled ? "取消" : "発表"),
            null, [], true, cancelled, cancelled, test)
        { SourceMessageTime = knownTime ? time : null };
        return item with { Signature = new EventSignatureBuilder().Build(item) };
    }

    private sealed class QueueNormalizer(params EewEvent[] items) : IEventNormalizer
    {
        private readonly Queue<EewEvent> _items = new(items);
        public NormalizeResult Normalize(RawProviderMessage raw) => NormalizeResult.Success(_items.Dequeue());
    }
}
