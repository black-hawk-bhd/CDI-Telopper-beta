using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class ProductionReplayCatalogTests
{
    [TestMethod]
    public void UpdateDisabledPolicyDoesNotEnterRotation()
    {
        var catalog = new ProductionReplayCatalog();
        EewEvent eew = DisplayEventFactory.CreateEew();

        catalog.Update(
            eew,
            CreateProgram(eew, OverlayPriority.Eew),
            ProductionReplaySettings.Default,
            eew.ReceivedAt);

        Assert.IsNull(catalog.SelectNext(ProductionReplaySettings.Default, eew.ReceivedAt));
    }

    [TestMethod]
    public void UpdateNewerSameEventReplacesOlderAndProgramRemovalRemovesIt()
    {
        var catalog = new ProductionReplayCatalog();
        ProductionReplaySettings settings = Enable(EventKind.Quake);
        QuakeEvent first = DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale) with
        {
            Signature = "first",
        };
        QuakeEvent second = first with
        {
            Signature = "second",
        };

        catalog.Update(first, CreateProgram(first, OverlayPriority.Quake), settings, first.ReceivedAt);
        catalog.Update(second, CreateProgram(second, OverlayPriority.Quake), settings, second.ReceivedAt);

        ProductionReplaySelection? selected = catalog.SelectNext(settings, second.ReceivedAt);
        Assert.IsNotNull(selected);
        Assert.AreEqual("second", selected.Event.Signature);

        catalog.Update(second, null, settings, second.ReceivedAt);

        Assert.IsNull(catalog.SelectNext(settings, second.ReceivedAt));
    }

    [TestMethod]
    public void SelectNextRotatesInPriorityOrderWithoutStarvingLowerPriority()
    {
        var catalog = new ProductionReplayCatalog();
        ProductionReplaySettings settings = Enable(EventKind.Tsunami, EventKind.Quake);
        QuakeEvent quake = DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale);
        TsunamiEvent tsunami = DisplayEventFactory.CreateTsunami(
            [DisplayEventFactory.TsunamiArea(1, TsunamiGrade.Warning)]);
        DateTimeOffset now = quake.ReceivedAt;

        catalog.Update(tsunami, CreateProgram(tsunami, OverlayPriority.TsunamiWarning), settings, now);
        catalog.Update(quake, CreateProgram(quake, OverlayPriority.Quake), settings, now);

        Assert.AreEqual(EventKind.Tsunami, catalog.SelectNext(settings, now)!.Event.Kind);
        Assert.AreEqual(EventKind.Quake, catalog.SelectNext(settings, now)!.Event.Kind);
        Assert.AreEqual(EventKind.Tsunami, catalog.SelectNext(settings, now)!.Event.Kind);
    }

    [TestMethod]
    public void SelectNextStopsAfterConfiguredRepeatCount()
    {
        var catalog = new ProductionReplayCatalog();
        ProductionReplaySettings settings = Enable(EventKind.Quake) with
        {
            Quake = new ProductionReplayPolicy(true, 2, false),
        };
        QuakeEvent quake = DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale);

        catalog.Update(
            quake,
            CreateProgram(quake, OverlayPriority.Quake),
            settings,
            quake.IssuedAt.AddSeconds(1));

        Assert.IsNotNull(catalog.SelectNext(settings, quake.IssuedAt.AddSeconds(1)));
        Assert.IsNotNull(catalog.SelectNext(settings, quake.IssuedAt.AddHours(1)));
        Assert.IsNull(catalog.SelectNext(settings, quake.IssuedAt.AddHours(2)));

        catalog.Update(
            quake,
            CreateProgram(quake, OverlayPriority.Quake),
            settings,
            quake.IssuedAt.AddHours(3));

        Assert.IsNull(catalog.SelectNext(settings, quake.IssuedAt.AddHours(3)));
    }

    [TestMethod]
    public void SameIssuedVersionWithDifferentSignatureDoesNotResetRepeatCount()
    {
        var catalog = new ProductionReplayCatalog();
        ProductionReplaySettings settings = Enable(EventKind.Quake) with
        {
            Quake = new ProductionReplayPolicy(true, 2, false),
        };
        QuakeEvent firstProvider = DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale) with
        {
            Signature = "provider-a",
        };
        QuakeEvent secondProvider = firstProvider with
        {
            Signature = "provider-b",
        };
        DateTimeOffset now = firstProvider.IssuedAt.AddSeconds(1);

        catalog.Update(
            firstProvider,
            CreateProgram(firstProvider, OverlayPriority.Quake),
            settings,
            now);
        Assert.IsNotNull(catalog.SelectNext(settings, now));

        catalog.Update(
            secondProvider,
            CreateProgram(secondProvider, OverlayPriority.Quake),
            settings,
            now.AddSeconds(1));
        Assert.IsNotNull(catalog.SelectNext(settings, now.AddSeconds(1)));
        Assert.IsNull(catalog.SelectNext(settings, now.AddSeconds(2)));
    }

    [TestMethod]
    public void CompletedIssuedVersionIsNotReopenedByAnotherProviderSignature()
    {
        var catalog = new ProductionReplayCatalog();
        ProductionReplaySettings settings = Enable(EventKind.Quake) with
        {
            Quake = new ProductionReplayPolicy(true, 1, false),
        };
        QuakeEvent firstProvider = DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale) with
        {
            Signature = "provider-a",
        };
        QuakeEvent secondProvider = firstProvider with
        {
            Signature = "provider-b",
        };
        DateTimeOffset now = firstProvider.IssuedAt.AddSeconds(1);

        catalog.Update(
            firstProvider,
            CreateProgram(firstProvider, OverlayPriority.Quake),
            settings,
            now);
        Assert.IsNotNull(catalog.SelectNext(settings, now));
        Assert.IsNull(catalog.SelectNext(settings, now.AddSeconds(1)));

        catalog.Update(
            secondProvider,
            CreateProgram(secondProvider, OverlayPriority.Quake),
            settings,
            now.AddSeconds(2));

        Assert.IsNull(catalog.SelectNext(settings, now.AddSeconds(2)));
    }

    [TestMethod]
    public void SelectNextReportsPerCategoryAudioChoice()
    {
        var catalog = new ProductionReplayCatalog();
        ProductionReplaySettings settings = Enable(EventKind.Tsunami) with
        {
            Tsunami = new ProductionReplayPolicy(true, 1, true),
        };
        TsunamiEvent tsunami = DisplayEventFactory.CreateTsunami(
            [DisplayEventFactory.TsunamiArea(1, TsunamiGrade.Warning)]);

        catalog.Update(
            tsunami,
            CreateProgram(tsunami, OverlayPriority.TsunamiWarning),
            settings,
            tsunami.ReceivedAt);

        Assert.IsTrue(catalog.SelectNext(settings, tsunami.ReceivedAt)!.PlayAudio);
    }

    private static ProductionReplaySettings Enable(params EventKind[] kinds)
    {
        ProductionReplaySettings defaults = ProductionReplaySettings.Default;
        return defaults with
        {
            Eew = defaults.Eew with { Enabled = kinds.Contains(EventKind.Eew) },
            Quake = defaults.Quake with { Enabled = kinds.Contains(EventKind.Quake) },
            Tsunami = defaults.Tsunami with { Enabled = kinds.Contains(EventKind.Tsunami) },
            WeatherWarning = defaults.WeatherWarning with
            {
                Enabled = kinds.Contains(EventKind.WeatherWarning),
            },
            Volcano = defaults.Volcano with { Enabled = kinds.Contains(EventKind.Volcano) },
        };
    }

    private static DisplayProgram CreateProgram(
        DisasterEvent disasterEvent,
        OverlayPriority priority) => new(
        ProgramId: $"program-{disasterEvent.Signature}",
        EventId: disasterEvent.Id,
        Kind: disasterEvent.Kind,
        SourceMode: disasterEvent.SourceMode,
        IssuedAt: disasterEvent.IssuedAt,
        Priority: priority,
        Pages:
        [
            new DisplayPage(
                0,
                [new DisplayBlock(string.Empty, "test", string.Empty, DisplayStyleTokens.Summary)],
                "test",
                null),
        ],
        StartedAtUtc: disasterEvent.ReceivedAt,
        EndPolicy: EndPolicy.AutoHide,
        RehearsalLabel: string.Empty);
}
