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
    [DataRow("jma-xml", ReceptionProvider.JmaXml, true)]
    [DataRow("wolfx", ReceptionProvider.Wolfx, true)]
    [DataRow("dmdata.jp", ReceptionProvider.Dmdata, false)]
    [DataRow("axis", ReceptionProvider.Axis, false)]
    public void SubsequentEarthquakeAdvisoryUsesItsConfiguredSource(
        string providerName, ReceptionProvider source, bool usesSpecialEarthquakeSetting)
    {
        ProviderSettings defaults = AppSettings.CreateDefault().Provider;
        ProviderSettings provider = defaults with
        {
            Routing = defaults.Routing with { Quake = ReceptionProvider.Disabled, NankaiTrough = source },
        };
        var normalizer = new ProviderSelectionEventNormalizer(
            new StubNormalizer(CreateQuake(QuakeIssueType.SubsequentEarthquakeAdvisory)), provider);

        Assert.AreEqual(usesSpecialEarthquakeSetting ? NormalizeStatus.Success : NormalizeStatus.Ignored,
            normalizer.Normalize(Message(providerName)).Status);

        normalizer.UpdateProviderSelection(provider with
        {
            Routing = provider.Routing with { Quake = source, NankaiTrough = ReceptionProvider.Disabled },
        });

        Assert.AreEqual(usesSpecialEarthquakeSetting ? NormalizeStatus.Ignored : NormalizeStatus.Success,
            normalizer.Normalize(Message(providerName)).Status);
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
    public async Task RoutedReconnectCountsRemainIndependentWhenAggregateIsFaulted()
    {
        var p2p = new FakeSource("p2pquake");
        var axis = new FakeSource("axis");
        ProviderSettings settings = AppSettings.CreateDefault().Provider with { Routing = ProviderRoutingSettings.AxisHybrid };
        await using var source = new RoutedProviderEventSource(settings, new Dictionary<ReceptionProvider, IEventSource>
        {
            [ReceptionProvider.P2pQuake] = p2p,
            [ReceptionProvider.Axis] = axis,
        });

        axis.SetConnectionState(ProviderConnectionState.Faulted);
        p2p.SetConnectionState(ProviderConnectionState.Reconnecting);
        p2p.SetConnectionState(ProviderConnectionState.Reconnecting, TimeSpan.FromSeconds(5));
        Assert.AreEqual(ProviderConnectionState.Faulted, source.Connection.State);
        Assert.AreEqual(1, source.GetProviderConnections().Single(provider => provider.Name == "P2P").ReconnectCount);
        Assert.AreEqual(0, source.GetProviderConnections().Single(provider => provider.Name == "AXIS").ReconnectCount);

        p2p.SetConnectionState(ProviderConnectionState.Connected);
        p2p.SetConnectionState(ProviderConnectionState.Reconnecting);
        axis.SetConnectionState(ProviderConnectionState.Reconnecting);
        Assert.AreEqual(2, source.GetProviderConnections().Single(provider => provider.Name == "P2P").ReconnectCount);
        Assert.AreEqual(1, source.GetProviderConnections().Single(provider => provider.Name == "AXIS").ReconnectCount);

        source.ConfigureProvider(settings with { Routing = ProviderRoutingSettings.Default });
        Assert.AreEqual(2, source.GetProviderConnections().Single().ReconnectCount);
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

    [TestMethod]
    public void AutomaticSelectionRejectsHealthyBackupAndSuppressesIdenticalRecoveryReport()
    {
        var clock = new FallbackClock();
        var settings = AppSettings.CreateDefault().Provider with { Mode = ProviderMode.Production };
        var fallback = new JmaFallbackRouting(settings, clock);
        var normalizer = new ProviderSelectionEventNormalizer(new StubNormalizer(CreateQuake(QuakeIssueType.DetailScale)), settings)
            { FallbackRouting = fallback };
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Message("jma-xml")).Status);
        fallback.Observe(ReceptionProvider.P2pQuake, ProviderConnectionState.Reconnecting);
        clock.UtcNow += TimeSpan.FromSeconds(30);
        Assert.AreEqual(NormalizeStatus.Success, normalizer.Normalize(Message("jma-xml")).Status);
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Message("p2pquake")).Status);
        fallback.Observe(ReceptionProvider.P2pQuake, ProviderConnectionState.Connected);
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Message("p2pquake")).Status);
    }

    [TestMethod]
    [DataRow(SourceMode.Sandbox)]
    [DataRow(SourceMode.ManualTest)]
    [DataRow(SourceMode.HistoryRehearsal)]
    public void TrainingReportCannotSuppressMatchingProductionBackup(SourceMode trainingMode)
    {
        var clock = new FallbackClock();
        var settings = AppSettings.CreateDefault().Provider with { Mode = ProviderMode.Production };
        QuakeEvent training = CreateQuake(QuakeIssueType.DetailScale, trainingMode);
        QuakeEvent production = CreateQuake(QuakeIssueType.DetailScale);
        Assert.AreEqual(EventSignatureBuilder.BuildCrossProvider(training),
            EventSignatureBuilder.BuildCrossProvider(production));
        var fallback = new JmaFallbackRouting(settings, clock);
        var normalizer = new ProviderSelectionEventNormalizer(
            new QueueNormalizer(training, production, production), settings)
        {
            FallbackRouting = fallback,
        };

        // The normalized telegram's mode can differ from its production transport.
        Assert.AreEqual(NormalizeStatus.Success, normalizer.Normalize(Message("p2pquake")).Status);
        fallback.Observe(ReceptionProvider.P2pQuake, ProviderConnectionState.Reconnecting);
        clock.UtcNow += JmaFallbackRouting.FailureDelay;
        Assert.AreEqual(NormalizeStatus.Success, normalizer.Normalize(Message("jma-xml")).Status);
        fallback.Observe(ReceptionProvider.P2pQuake, ProviderConnectionState.Connected);
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Message("p2pquake")).Status);
    }

    private sealed class FallbackClock : EEWTelop.Application.Abstractions.IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }

    private static RawProviderMessage Message(string provider) => new(
        provider,
        "{}",
        SourceMode.Production,
        Now);

    private static QuakeEvent CreateQuake(
        QuakeIssueType issueType,
        SourceMode sourceMode = SourceMode.Production)
    {
        var issue = new IssueInfo(
            "気象庁",
            Now,
            issueType switch
            {
                QuakeIssueType.NankaiTroughTemporaryInformation => "VYSE50",
                QuakeIssueType.SubsequentEarthquakeAdvisory => "VYSE60",
                _ => "VXSE53",
            },
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
            sourceMode,
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

    private sealed class QueueNormalizer(params DisasterEvent[] events) : IEventNormalizer
    {
        private readonly Queue<DisasterEvent> _events = new(events);

        public NormalizeResult Normalize(RawProviderMessage raw) =>
            NormalizeResult.Success(_events.Dequeue());
    }

    private sealed class FakeSource(string provider) : IEventSource
    {
        public int ReadCount { get; private set; }

        public ProviderConnectionSnapshot Connection { get; private set; } = new(
            ProviderConnectionState.Stopped,
            Now);

        public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged;

        public void SetConnectionState(ProviderConnectionState state, TimeSpan? retryDelay = null)
        {
            Connection = Connection with { State = state, RetryDelay = retryDelay };
            ConnectionChanged?.Invoke(this, Connection);
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
