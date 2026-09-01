using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class EventKindFilteringEventNormalizerTests
{
    [TestMethod]
    public void NormalizeReturnsAllowedEventUnchanged()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale);
        var sut = new EventKindFilteringEventNormalizer(
            new StubNormalizer(quake),
            [EventKind.Eew, EventKind.Quake, EventKind.Tsunami]);

        NormalizeResult result = sut.Normalize(CreateRaw());

        Assert.IsTrue(result.IsSuccess);
        Assert.AreSame(quake, result.Event);
    }

    [TestMethod]
    public void NormalizeIgnoresEventOutsideEditionAllowList()
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale);
        var sut = new EventKindFilteringEventNormalizer(
            new StubNormalizer(quake),
            [EventKind.Eew, EventKind.Tsunami]);

        NormalizeResult result = sut.Normalize(CreateRaw());

        Assert.AreEqual(NormalizeStatus.Ignored, result.Status);
        Assert.IsNull(result.Event);
        Assert.IsTrue(result.Issues.Any(static issue => issue.Path == "event.kind"));
    }

    private static RawProviderMessage CreateRaw() => new(
        "fixture",
        "{}",
        SourceMode.HistoryRehearsal,
        new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.FromHours(9)));

    private sealed class StubNormalizer(DisasterEvent disasterEvent) : IEventNormalizer
    {
        public NormalizeResult Normalize(RawProviderMessage raw) =>
            NormalizeResult.Success(disasterEvent);
    }
}
