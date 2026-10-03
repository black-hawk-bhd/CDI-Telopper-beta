using System.Text.Json;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class EventVersionCacheTests
{
    [TestMethod]
    public void ExactSignatureIsDuplicateButCorrectionIsAccepted()
    {
        var cache = new EventVersionCache();
        QuakeEvent first = CreateEvent("event-a", "signature-1");
        QuakeEvent correction = CreateEvent("event-a", "signature-2");

        Assert.IsTrue(cache.TryAccept(first));
        Assert.IsFalse(cache.TryAccept(first));
        Assert.IsTrue(cache.TryAccept(correction));
    }

    [TestMethod]
    public void OldestSignatureIsEvictedAtVersionLimit()
    {
        var cache = new EventVersionCache(keyLimit: 5, versionLimit: 2);

        Assert.IsTrue(cache.TryAccept(CreateEvent("event-a", "signature-1")));
        Assert.IsTrue(cache.TryAccept(CreateEvent("event-a", "signature-2")));
        Assert.IsTrue(cache.TryAccept(CreateEvent("event-a", "signature-3")));
        Assert.IsTrue(cache.TryAccept(CreateEvent("event-a", "signature-1")));
    }

    [TestMethod]
    public void LeastRecentlyUsedKeyIsEvictedAtKeyLimit()
    {
        var cache = new EventVersionCache(keyLimit: 2, versionLimit: 2);

        Assert.IsTrue(cache.TryAccept(CreateEvent("event-a", "signature")));
        Assert.IsTrue(cache.TryAccept(CreateEvent("event-b", "signature")));
        Assert.IsTrue(cache.TryAccept(CreateEvent("event-c", "signature")));
        Assert.IsTrue(cache.TryAccept(CreateEvent("event-a", "signature")));
    }

    [TestMethod]
    public void SnapshotRestoreKeepsRecentSignaturesAsDuplicates()
    {
        var original = new EventVersionCache();
        QuakeEvent disasterEvent = CreateEvent("event-a", "signature-1");
        Assert.IsTrue(original.TryAccept(disasterEvent));
        IReadOnlyList<StoredEventSignature> snapshot = original.GetSnapshot();
        var restored = new EventVersionCache();

        restored.Restore(snapshot);

        Assert.IsFalse(restored.TryAccept(disasterEvent));
        Assert.HasCount(1, restored.GetSnapshot());
    }

    [TestMethod]
    public void OlderReportNumberIsRejectedButSameNumberCancellationCanBeAccepted()
    {
        var cache = new EventVersionCache();

        Assert.IsTrue(cache.TryAccept(CreateEvent("event-a", "serial-3", "3")));
        Assert.IsFalse(cache.TryAccept(CreateEvent("event-a", "late-serial-2", "2")));
        Assert.IsTrue(cache.TryAccept(CreateEvent("event-a", "cancel-serial-3", "3")));
    }

    [TestMethod]
    public void SameEewReportFromAxisAndP2pIsAcceptedOnlyOnce()
    {
        var cache = new EventVersionCache();
        EewEvent axis = CreateEew("axis", "axis-signature", "4", isCancelled: false);
        EewEvent p2p = CreateEew("p2pquake", "p2p-signature", "4", isCancelled: false);
        EewEvent cancellation = CreateEew("p2pquake", "cancel-signature", "4", isCancelled: true);

        Assert.IsTrue(cache.TryAccept(axis));
        Assert.IsFalse(cache.TryAccept(p2p));
        Assert.IsTrue(cache.TryAccept(cancellation));
    }

    [TestMethod]
    [DataRow(SourceMode.Sandbox)]
    [DataRow(SourceMode.ManualTest)]
    [DataRow(SourceMode.HistoryRehearsal)]
    public void SameSignatureIsIndependentAcrossSourceModes(SourceMode trainingMode)
    {
        var cache = new EventVersionCache();
        QuakeEvent training = CreateEvent("shared-event", "same-signature", sourceMode: trainingMode);
        QuakeEvent production = CreateEvent("shared-event", "same-signature");

        Assert.IsTrue(cache.TryAccept(training));
        Assert.IsTrue(cache.TryAccept(production));
        Assert.IsFalse(cache.TryAccept(training));
        Assert.IsFalse(cache.TryAccept(production));
    }

    [TestMethod]
    [DataRow(SourceMode.Sandbox)]
    [DataRow(SourceMode.ManualTest)]
    [DataRow(SourceMode.HistoryRehearsal)]
    public void ReportOrderingIsIndependentAcrossSourceModes(SourceMode trainingMode)
    {
        var cache = new EventVersionCache();

        Assert.IsTrue(cache.TryAccept(CreateEvent("shared-event", "training-10", "10", trainingMode)));
        Assert.IsTrue(cache.TryAccept(CreateEvent("shared-event", "production-2", "2")));
        Assert.IsFalse(cache.TryAccept(CreateEvent("shared-event", "production-1", "1")));
        Assert.IsFalse(cache.TryAccept(CreateEvent("shared-event", "training-9", "9", trainingMode)));
    }

    [TestMethod]
    [DataRow(SourceMode.Sandbox)]
    [DataRow(SourceMode.ManualTest)]
    [DataRow(SourceMode.HistoryRehearsal)]
    public void EewProviderDeduplicationIsIndependentAcrossSourceModes(SourceMode trainingMode)
    {
        var cache = new EventVersionCache();
        EewEvent training = CreateEew("axis", "training", "4", false, trainingMode);
        EewEvent production = CreateEew("p2pquake", "production", "4", false);

        Assert.IsTrue(cache.TryAccept(training));
        Assert.IsTrue(cache.TryAccept(production));
        Assert.IsFalse(cache.TryAccept(CreateEew("axis", "production-copy", "4", false)));
        Assert.IsFalse(cache.TryAccept(CreateEew("p2pquake", "training-copy", "4", false, trainingMode)));
    }

    [TestMethod]
    [DataRow(SourceMode.Sandbox)]
    [DataRow(SourceMode.ManualTest)]
    [DataRow(SourceMode.HistoryRehearsal)]
    public void EewReportOrderingIsIndependentAcrossSourceModes(SourceMode trainingMode)
    {
        var cache = new EventVersionCache();

        Assert.IsTrue(cache.TryAccept(CreateEew("axis", "training-10", "10", false, trainingMode)));
        Assert.IsTrue(cache.TryAccept(CreateEew("p2pquake", "production-2", "2", false)));
        Assert.IsFalse(cache.TryAccept(CreateEew("axis", "production-1", "1", false)));
        Assert.IsFalse(cache.TryAccept(CreateEew("p2pquake", "training-9", "9", false, trainingMode)));
    }

    [TestMethod]
    public void SnapshotRoundTripPreservesIndependentSourceModes()
    {
        var original = new EventVersionCache();
        SourceMode[] modes = [SourceMode.Production, SourceMode.Sandbox,
            SourceMode.ManualTest, SourceMode.HistoryRehearsal];
        foreach (SourceMode mode in modes)
        {
            Assert.IsTrue(original.TryAccept(CreateEvent("shared-event", "same-signature", sourceMode: mode)));
        }

        string json = JsonSerializer.Serialize(original.GetSnapshot());
        StoredEventSignature[]? snapshot = JsonSerializer.Deserialize<StoredEventSignature[]>(json);
        Assert.IsNotNull(snapshot);
        CollectionAssert.AreEquivalent(modes, snapshot.Select(static item => item.SourceMode).ToArray());
        var restored = new EventVersionCache();
        restored.Restore(snapshot);

        foreach (SourceMode mode in modes)
        {
            Assert.IsFalse(restored.TryAccept(CreateEvent("shared-event", "same-signature", sourceMode: mode)));
        }
    }

    [TestMethod]
    public void LegacySnapshotWithoutSourceModeRestoresProductionOnly()
    {
        const string json = """
            [{"Provider":"p2pquake","Kind":2,"EventId":"shared-event","Signature":"same-signature"}]
            """;
        StoredEventSignature[]? snapshot = JsonSerializer.Deserialize<StoredEventSignature[]>(json);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(SourceMode.Production, snapshot[0].SourceMode);
        var cache = new EventVersionCache();
        cache.Restore(snapshot);

        Assert.IsFalse(cache.TryAccept(CreateEvent("shared-event", "same-signature")));
        Assert.IsTrue(cache.TryAccept(CreateEvent("shared-event", "same-signature", sourceMode: SourceMode.Sandbox)));
    }

    [TestMethod]
    public void ProductionConnectionTestEewCannotSuppressNormalEewSignature()
    {
        var cache = new EventVersionCache();
        EewEvent training = CreateEew("axis", "same-signature", "4", false, isTest: true);
        EewEvent production = CreateEew("axis", "same-signature", "4", false);

        Assert.IsTrue(cache.TryAccept(training));
        Assert.IsTrue(cache.TryAccept(production));
        Assert.IsFalse(cache.TryAccept(training));
        Assert.IsFalse(cache.TryAccept(production));
    }

    [TestMethod]
    public void ProductionConnectionTestEewCannotAdvanceNormalEewSerial()
    {
        var cache = new EventVersionCache();

        Assert.IsTrue(cache.TryAccept(CreateEew("axis", "training-10", "10", false, isTest: true)));
        Assert.IsTrue(cache.TryAccept(CreateEew("axis", "production-2", "2", false)));
        Assert.IsFalse(cache.TryAccept(CreateEew("axis", "production-1", "1", false)));
        Assert.IsFalse(cache.TryAccept(CreateEew("axis", "training-9", "9", false, isTest: true)));
    }

    [TestMethod]
    public void ProductionConnectionTestEewProviderDeduplicationStaysIndependent()
    {
        var cache = new EventVersionCache();

        Assert.IsTrue(cache.TryAccept(CreateEew("axis", "training", "4", false, isTest: true)));
        Assert.IsTrue(cache.TryAccept(CreateEew("p2pquake", "production", "4", false)));
        Assert.IsFalse(cache.TryAccept(CreateEew("p2pquake", "training-copy", "4", false, isTest: true)));
        Assert.IsFalse(cache.TryAccept(CreateEew("axis", "production-copy", "4", false)));
    }

    [TestMethod]
    public void SnapshotRoundTripPreservesProductionConnectionTestEew()
    {
        var original = new EventVersionCache();
        EewEvent training = CreateEew("axis", "same-signature", "4", false, isTest: true);
        EewEvent production = CreateEew("axis", "same-signature", "4", false);
        Assert.IsTrue(original.TryAccept(training));
        Assert.IsTrue(original.TryAccept(production));
        string json = JsonSerializer.Serialize(original.GetSnapshot());
        StoredEventSignature[]? snapshot = JsonSerializer.Deserialize<StoredEventSignature[]>(json);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(1, snapshot.Count(static item => item.IsTest));
        Assert.AreEqual(1, snapshot.Count(static item => !item.IsTest));
        var restored = new EventVersionCache();
        restored.Restore(snapshot);

        Assert.IsFalse(restored.TryAccept(training));
        Assert.IsFalse(restored.TryAccept(production));
    }

    [TestMethod]
    public void LegacyEewSnapshotWithoutTestFlagRestoresNormalReportOnly()
    {
        const string json = """
            [{"Provider":"axis","Kind":1,"EventId":"shared-eew-event","Signature":"same-signature"}]
            """;
        StoredEventSignature[]? snapshot = JsonSerializer.Deserialize<StoredEventSignature[]>(json);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot[0].IsTest);
        var cache = new EventVersionCache();
        cache.Restore(snapshot);

        Assert.IsFalse(cache.TryAccept(CreateEew("axis", "same-signature", "4", false)));
        Assert.IsTrue(cache.TryAccept(CreateEew("axis", "same-signature", "4", false, isTest: true)));
    }

    private static QuakeEvent CreateEvent(
        string id,
        string signature,
        string? serial = null,
        SourceMode sourceMode = SourceMode.Production)
    {
        var issue = new IssueInfo(
            "JMA",
            DateTimeOffset.Parse("2026-07-31T12:00:00+09:00", null),
            "DetailScale",
            CorrectionType.None,
            serial);
        var earthquake = new EarthquakeInfo(
            issue.IssuedAt,
            null,
            new HypocenterInfo("Tokyo", "", 35, 139, 10, 5.5, ""),
            JmaScale.Four,
            DomesticTsunami.None,
            ForeignTsunami.None);
        return new QuakeEvent(
            EventId.Create(id),
            "p2pquake",
            issue.IssuedAt,
            issue.IssuedAt,
            signature,
            sourceMode,
            issue,
            QuakeIssueType.DetailScale,
            earthquake,
            [],
            "");
    }

    private static EewEvent CreateEew(
        string provider,
        string signature,
        string serial,
        bool isCancelled,
        SourceMode sourceMode = SourceMode.Production,
        bool isTest = false)
    {
        DateTimeOffset issuedAt = DateTimeOffset.Parse("2026-07-31T12:00:00+09:00", null);
        return new EewEvent(
            EventId.Create("shared-eew-event"),
            provider,
            issuedAt,
            issuedAt,
            signature,
            sourceMode,
            new IssueInfo("気象庁", issuedAt, "EEW", CorrectionType.None, serial),
            earthquake: null,
            areas: [],
            isWarning: true,
            isFinal: false,
            isCancelled,
            isTest);
    }
}
