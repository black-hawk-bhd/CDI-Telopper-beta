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

    private static QuakeEvent CreateEvent(
        string id,
        string signature,
        string? serial = null)
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
            SourceMode.Production,
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
        bool isCancelled)
    {
        DateTimeOffset issuedAt = DateTimeOffset.Parse("2026-07-31T12:00:00+09:00", null);
        return new EewEvent(
            EventId.Create("shared-eew-event"),
            provider,
            issuedAt,
            issuedAt,
            signature,
            SourceMode.Production,
            new IssueInfo("気象庁", issuedAt, "EEW", CorrectionType.None, serial),
            earthquake: null,
            areas: [],
            isWarning: true,
            isFinal: false,
            isCancelled,
            isTest: false);
    }
}
