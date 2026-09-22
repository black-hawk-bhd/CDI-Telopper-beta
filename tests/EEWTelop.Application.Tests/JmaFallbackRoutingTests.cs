using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class JmaFallbackRoutingTests
{
    [TestMethod]
    public void WaitsThirtySecondsAndOnlyReplacesFailedProvidersExceptEew()
    {
        var clock = new Clock();
        var settings = Settings();
        var state = new JmaFallbackRouting(settings, clock);
        state.Observe(ReceptionProvider.Axis, ProviderConnectionState.Reconnecting);
        clock.Advance(29);
        Assert.AreEqual(settings.Routing, state.GetRouting());
        state.Observe(ReceptionProvider.Axis, ProviderConnectionState.Connecting);
        clock.Advance(1);
        var route = state.GetRouting();
        Assert.AreEqual(ReceptionProvider.JmaXml, route.Weather);
        Assert.AreEqual(ReceptionProvider.Axis, route.Eew);
        Assert.AreEqual(ReceptionProvider.P2pQuake, route.Quake);
        Assert.AreEqual(ReceptionProvider.Disabled, route.Volcano);
        Assert.AreEqual(ReceptionProvider.Wolfx, route.NankaiTrough);
        state.Observe(ReceptionProvider.Axis, ProviderConnectionState.Connected);
        Assert.AreEqual(settings.Routing, state.GetRouting());
    }

    [TestMethod]
    public void HandlesSimultaneousFailuresAndIndependentRecovery()
    {
        var clock = new Clock();
        var state = new JmaFallbackRouting(Settings(), clock);
        foreach (var provider in new[] { ReceptionProvider.Axis, ReceptionProvider.P2pQuake, ReceptionProvider.Dmdata })
            state.Observe(provider, ProviderConnectionState.Faulted);
        clock.Advance(30);
        Assert.AreEqual(ReceptionProvider.JmaXml, state.GetRouting().Quake);
        Assert.AreEqual(ReceptionProvider.JmaXml, state.GetRouting().Tsunami);
        state.Observe(ReceptionProvider.P2pQuake, ProviderConnectionState.Connected);
        Assert.AreEqual(ReceptionProvider.P2pQuake, state.GetRouting().Quake);
        Assert.AreEqual(ReceptionProvider.JmaXml, state.GetRouting().Tsunami);
    }

    [TestMethod]
    public void SilenceStaleInitialConnectingAndStoppedDoNotTriggerFailover()
    {
        var clock = new Clock();
        var state = new JmaFallbackRouting(Settings(), clock);
        foreach (var status in new[] { ProviderConnectionState.Connecting, ProviderConnectionState.Stale, ProviderConnectionState.Stopped })
        {
            state.Observe(ReceptionProvider.Axis, status);
            clock.Advance(300);
            Assert.AreEqual(ReceptionProvider.Axis, state.GetRouting().Weather);
        }
    }

    [TestMethod]
    public void DisabledSettingSandboxAndResetPreventFallback()
    {
        var clock = new Clock();
        foreach (var settings in new[] { Settings() with { JmaXmlAutoFallback = false }, Settings() with { Mode = ProviderMode.Sandbox } })
        {
            var state = new JmaFallbackRouting(settings, clock);
            state.Observe(ReceptionProvider.Axis, ProviderConnectionState.Faulted);
            clock.Advance(60);
            Assert.AreEqual(settings.Routing, state.GetRouting());
        }
        var enabled = new JmaFallbackRouting(Settings(), clock);
        enabled.Observe(ReceptionProvider.Axis, ProviderConnectionState.Faulted);
        clock.Advance(60);
        enabled.Reset();
        Assert.AreEqual(Settings().Routing, enabled.GetRouting());
    }

    [TestMethod]
    public async Task FailedPrimaryDoesNotKillSharedBackupAndStopCancelsAllReaders()
    {
        var settings = Settings() with
        {
            Routing = ProviderRoutingSettings.FromLegacy(ReceptionProvider.Disabled) with
            { Quake = ReceptionProvider.Axis, Tsunami = ReceptionProvider.P2pQuake },
        };
        var backup = new TestSource(false);
        var sources = new Dictionary<ReceptionProvider, IEventSource>
        {
            [ReceptionProvider.Axis] = new TestSource(true),
            [ReceptionProvider.P2pQuake] = new TestSource(true),
            [ReceptionProvider.JmaXml] = backup,
        };
        await using var router = new RoutedProviderEventSource(settings, sources, new JmaFallbackRouting(settings, new Clock()));
        await using var reader = router.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, backup.Starts);
        await router.StopAsync();
        try { await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { }
        Assert.AreEqual(1, backup.Starts);
    }

    private sealed class TestSource(bool fail) : IEventSource
    {
        public int Starts { get; private set; }
        public ProviderConnectionSnapshot Connection { get; } = new(ProviderConnectionState.Stopped, DateTimeOffset.UtcNow);
        public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged { add { } remove { } }
        public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Starts++;
            if (fail) throw new IOException("Simulated primary outage");
            yield return new("jma-xml", "{}", EEWTelop.Domain.Events.SourceMode.Production, DateTimeOffset.UtcNow);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void RequestReconnect(ReconnectReason reason) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ProviderSettings Settings() => AppSettings.CreateDefault().Provider with
    {
        Mode = ProviderMode.Production,
        Routing = new(ReceptionProvider.Axis, ReceptionProvider.P2pQuake, ReceptionProvider.Dmdata,
            ReceptionProvider.Axis, ReceptionProvider.Disabled, ReceptionProvider.Wolfx),
    };

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        public void Advance(int seconds) => UtcNow += TimeSpan.FromSeconds(seconds);
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
