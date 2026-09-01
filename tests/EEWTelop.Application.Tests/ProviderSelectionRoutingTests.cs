using System.Runtime.CompilerServices;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class ProviderSelectionRoutingTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AxisHybridRoutesOrdinaryQuakeAndNankaiIndependently()
    {
        ProviderSettings provider = AppSettings.CreateDefault().Provider with
        {
            Routing = ProviderRoutingSettings.AxisHybrid,
            ReceptionProvider = ReceptionProvider.Axis,
        };
        var ordinaryQuake = new ProviderSelectionEventNormalizer(
            new StubNormalizer(CreateQuake(QuakeIssueType.DetailScale)),
            provider);
        var nankai = new ProviderSelectionEventNormalizer(
            new StubNormalizer(CreateQuake(
                QuakeIssueType.NankaiTroughTemporaryInformation)),
            provider);

        Assert.AreEqual(
            NormalizeStatus.Success,
            ordinaryQuake.Normalize(Message("p2pquake")).Status);
        Assert.AreEqual(
            NormalizeStatus.Ignored,
            ordinaryQuake.Normalize(Message("axis")).Status);
        Assert.AreEqual(
            NormalizeStatus.Success,
            nankai.Normalize(Message("axis")).Status);
        Assert.AreEqual(
            NormalizeStatus.Ignored,
            nankai.Normalize(Message("p2pquake")).Status);
    }

    [TestMethod]
    public void ProviderSelectionCanBeUpdatedWithoutReplacingNormalizer()
    {
        ProviderSettings provider = AppSettings.CreateDefault().Provider with
        {
            Routing = ProviderRoutingSettings.AxisHybrid,
            ReceptionProvider = ReceptionProvider.Axis,
        };
        var normalizer = new ProviderSelectionEventNormalizer(
            new StubNormalizer(CreateQuake(QuakeIssueType.DetailScale)),
            provider);

        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Message("axis")).Status);

        normalizer.UpdateProviderSelection(provider with
        {
            Routing = provider.Routing with { Quake = ReceptionProvider.Axis },
        });

        Assert.AreEqual(NormalizeStatus.Success, normalizer.Normalize(Message("axis")).Status);
    }

    [TestMethod]
    public void DisabledInformationCategoryRejectsMessagesFromEveryProvider()
    {
        ProviderSettings provider = AppSettings.CreateDefault().Provider with
        {
            Routing = ProviderRoutingSettings.Default with
            {
                Quake = ReceptionProvider.Disabled,
            },
        };
        var normalizer = new ProviderSelectionEventNormalizer(
            new StubNormalizer(CreateQuake(QuakeIssueType.DetailScale)),
            provider);

        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Message("p2pquake")).Status);
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Message("axis")).Status);
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Message("dmdata.jp")).Status);
    }

    [TestMethod]
    public async Task RoutedSourceStartsOnlyDistinctSelectedProviders()
    {
        var p2p = new FakeSource("p2pquake");
        var axis = new FakeSource("axis");
        var dmdata = new FakeSource("dmdata.jp");
        ProviderSettings provider = AppSettings.CreateDefault().Provider with
        {
            Routing = ProviderRoutingSettings.AxisHybrid,
            ReceptionProvider = ReceptionProvider.Axis,
        };
        await using var source = new RoutedProviderEventSource(
            provider,
            new Dictionary<ReceptionProvider, IEventSource>
            {
                [ReceptionProvider.P2pQuake] = p2p,
                [ReceptionProvider.Axis] = axis,
                [ReceptionProvider.Dmdata] = dmdata,
            });
        var received = new List<RawProviderMessage>();

        await foreach (RawProviderMessage message in source.ReadAllAsync())
        {
            received.Add(message);
        }

        Assert.AreEqual(1, p2p.ReadCount);
        Assert.AreEqual(1, axis.ReadCount);
        Assert.AreEqual(0, dmdata.ReadCount);
        Assert.HasCount(2, received);
        Assert.IsTrue(received.Any(static message => message.Provider == "p2pquake"));
        Assert.IsTrue(received.Any(static message => message.Provider == "axis"));
    }

    [TestMethod]
    public async Task AllDisabledRoutingDoesNotStartAnyProviderApi()
    {
        var p2p = new FakeSource("p2pquake");
        var axis = new FakeSource("axis");
        var dmdata = new FakeSource("dmdata.jp");
        ProviderSettings provider = AppSettings.CreateDefault().Provider with
        {
            ReceptionProvider = ReceptionProvider.Disabled,
            Routing = ProviderRoutingSettings.FromLegacy(ReceptionProvider.Disabled),
        };
        await using var source = new RoutedProviderEventSource(
            provider,
            new Dictionary<ReceptionProvider, IEventSource>
            {
                [ReceptionProvider.P2pQuake] = p2p,
                [ReceptionProvider.Axis] = axis,
                [ReceptionProvider.Dmdata] = dmdata,
            });

        await foreach (RawProviderMessage _ in source.ReadAllAsync())
        {
            Assert.Fail("Disabled routing must not yield provider messages.");
        }

        Assert.AreEqual(0, p2p.ReadCount);
        Assert.AreEqual(0, axis.ReadCount);
        Assert.AreEqual(0, dmdata.ReadCount);
        Assert.AreEqual(ProviderConnectionState.Stopped, source.Connection.State);
        Assert.IsEmpty(source.GetProviderConnections());
    }

    private static RawProviderMessage Message(string provider) => new(
        provider,
        "{}",
        SourceMode.Production,
        Now);

    private static QuakeEvent CreateQuake(QuakeIssueType issueType)
    {
        var issue = new IssueInfo(
            "気象庁",
            Now,
            issueType == QuakeIssueType.NankaiTroughTemporaryInformation
                ? "VYSE50"
                : "VXSE53",
            CorrectionType.None);
        var earthquake = new EarthquakeInfo(
            Now,
            null,
            null,
            JmaScale.Three,
            DomesticTsunami.None,
            ForeignTsunami.None);
        return new QuakeEvent(
            EventId.Create("routing-test"),
            "test",
            Now,
            Now,
            "routing-signature",
            SourceMode.Production,
            issue,
            issueType,
            earthquake,
            [],
            string.Empty);
    }

    private sealed class StubNormalizer(DisasterEvent disasterEvent) : IEventNormalizer
    {
        public NormalizeResult Normalize(RawProviderMessage raw) =>
            NormalizeResult.Success(disasterEvent);
    }

    private sealed class FakeSource(string provider) : IEventSource
    {
        public int ReadCount { get; private set; }

        public ProviderConnectionSnapshot Connection { get; } = new(
            ProviderConnectionState.Stopped,
            Now);

        public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged
        {
            add { }
            remove { }
        }

        public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            yield return Message(provider);
            await Task.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public void RequestReconnect(ReconnectReason reason)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
