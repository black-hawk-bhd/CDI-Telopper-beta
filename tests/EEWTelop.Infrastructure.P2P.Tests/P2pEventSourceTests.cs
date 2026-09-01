using System.Net.WebSockets;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.P2P.Configuration;
using EEWTelop.Infrastructure.P2P.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.P2P.Tests;

[TestClass]
public sealed class P2pEventSourceTests
{
    [TestMethod]
    public void AxisHybridRouteAcceptsEarthquakeAndEewCodes()
    {
        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;

        Assert.IsTrue(P2pHybridRoutingPolicy.IsEarthquakeOrEew(new RawProviderMessage(
            "p2pquake",
            "{\"code\":551}",
            SourceMode.Production,
            receivedAt)));
        Assert.IsTrue(P2pHybridRoutingPolicy.IsEarthquakeOrEew(new RawProviderMessage(
            "p2pquake",
            "{\"code\":556}",
            SourceMode.Production,
            receivedAt)));
        Assert.IsFalse(P2pHybridRoutingPolicy.IsEarthquakeOrEew(new RawProviderMessage(
            "p2pquake",
            "{\"code\":552}",
            SourceMode.Production,
            receivedAt)));
        Assert.IsFalse(P2pHybridRoutingPolicy.IsEarthquakeOrEew(new RawProviderMessage(
            "p2pquake",
            "{\"code\":600}",
            SourceMode.Production,
            receivedAt)));
    }

    [TestMethod]
    public async Task ReceivesTextAndTracksLastReceiveTime()
    {
        var socket = new FakeWebSocket().EnqueueMessage("{\"code\":551}");
        var source = CreateSource(new FakeWebSocketFactory(socket));
        await using IAsyncEnumerator<RawProviderMessage> reader =
            source.ReadAllAsync().GetAsyncEnumerator();

        bool moved = await reader.MoveNextAsync();

        Assert.IsTrue(moved);
        Assert.AreEqual("{\"code\":551}", reader.Current.Json);
        Assert.AreEqual(SourceMode.Production, reader.Current.SourceMode);
        Assert.AreEqual(ProviderConnectionState.Connected, source.Connection.State);
        Assert.IsNotNull(source.Connection.LastReceivedAt);
        await source.StopAsync();
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task ConfigureProviderUsesSandboxPresetInsteadOfStoredProductionUrls()
    {
        var socket = new FakeWebSocket().EnqueueMessage("{\"code\":551}");
        var source = CreateSource(new FakeWebSocketFactory(socket));
        source.ConfigureProvider(new ProviderSettings(
            ProviderMode.Sandbox,
            ProviderOptions.Production.WebSocketUri.AbsoluteUri,
            ProviderOptions.Production.RestBaseUri.AbsoluteUri));
        await using IAsyncEnumerator<RawProviderMessage> reader =
            source.ReadAllAsync().GetAsyncEnumerator();

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(ProviderOptions.Sandbox.WebSocketUri, socket.ConnectedUri);
        Assert.AreEqual(SourceMode.Sandbox, reader.Current.SourceMode);
        await source.StopAsync();
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task ReconnectRunsRestRecoveryBeforeNewLiveMessage()
    {
        var clock = new FakeClock();
        var firstSocket = new FakeWebSocket().EnqueueClose();
        var secondSocket = new FakeWebSocket().EnqueueMessage("{\"code\":551,\"id\":\"live\"}");
        var factory = new FakeWebSocketFactory(firstSocket, secondSocket);
        var recoveryMessage = new RawProviderMessage(
            "p2pquake",
            "{\"code\":552,\"id\":\"recovered\"}",
            SourceMode.Production,
            clock.UtcNow);
        var recovery = new FakeRecoveryClient(recoveryMessage);
        var logs = new MemoryLogWriter();
        var source = CreateSource(factory, clock, recovery, logs);
        await using IAsyncEnumerator<RawProviderMessage> reader =
            source.ReadAllAsync().GetAsyncEnumerator();

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(recoveryMessage.Json, reader.Current.Json);
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.Contains("\"live\"", reader.Current.Json);

        Assert.AreEqual(2, factory.CreateCount);
        Assert.AreEqual(1, recovery.FetchCount);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            recovery.LastIssuedAfter);
        Assert.IsTrue(logs.Entries.Any(entry => entry.EventName == "EewRecoveryUnavailable"));
        await source.StopAsync();
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task NoMessageKeepsConnectedStateUntilReceiveCompletes()
    {
        var pending = new TaskCompletionSource<ProviderSocketMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new FakeWebSocket().EnqueuePending(pending);
        var source = CreateSource(new FakeWebSocketFactory(socket));
        await using IAsyncEnumerator<RawProviderMessage> reader =
            source.ReadAllAsync().GetAsyncEnumerator();

        Task<bool> moveTask = reader.MoveNextAsync().AsTask();
        await WaitForStateAsync(source, ProviderConnectionState.Connected);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        Assert.AreEqual(ProviderConnectionState.Connected, source.Connection.State);

        pending.SetResult(new ProviderSocketMessage("{\"code\":551}"));
        Assert.IsTrue(await moveTask);
        Assert.AreEqual(ProviderConnectionState.Connected, source.Connection.State);
        await source.StopAsync();
        await source.DisposeAsync();
    }

    [TestMethod]
    [DataRow(ReconnectReason.NetworkAvailable)]
    [DataRow(ReconnectReason.SystemResume)]
    [DataRow(ReconnectReason.RuntimeGap)]
    [DataRow(ReconnectReason.EndpointChanged)]
    public async Task RecoverySignalsDiscardOldSocketAndUseSingleNewConnection(
        ReconnectReason reason)
    {
        var pending = new TaskCompletionSource<ProviderSocketMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSocket = new FakeWebSocket().EnqueuePending(pending);
        var secondSocket = new FakeWebSocket().EnqueueMessage("{\"code\":551}");
        var factory = new FakeWebSocketFactory(firstSocket, secondSocket);
        var clock = new FakeClock();
        var recovered = new RawProviderMessage(
            "p2pquake",
            "{\"code\":551,\"id\":\"recovery\"}",
            SourceMode.Production,
            clock.UtcNow);
        var source = CreateSource(
            factory,
            clock,
            new FakeRecoveryClient(recovered));
        await using IAsyncEnumerator<RawProviderMessage> reader =
            source.ReadAllAsync().GetAsyncEnumerator();

        Task<bool> moveTask = reader.MoveNextAsync().AsTask();
        await WaitForStateAsync(source, ProviderConnectionState.Connected);
        source.RequestReconnect(reason);

        Assert.IsTrue(await moveTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(recovered.Json, reader.Current.Json);
        Assert.AreEqual(2, factory.CreateCount);
        Assert.IsTrue(firstSocket.IsDisposed);
        await source.StopAsync();
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task ManualStopCancelsReceiveWithoutSchedulingReconnect()
    {
        var pending = new TaskCompletionSource<ProviderSocketMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new FakeWebSocket().EnqueuePending(pending);
        var factory = new FakeWebSocketFactory(socket);
        var source = CreateSource(factory);
        await using IAsyncEnumerator<RawProviderMessage> reader =
            source.ReadAllAsync().GetAsyncEnumerator();

        Task<bool> moveTask = reader.MoveNextAsync().AsTask();
        await WaitForStateAsync(source, ProviderConnectionState.Connected);
        await source.StopAsync();

        Assert.IsFalse(await moveTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(ProviderConnectionState.Stopped, source.Connection.State);
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task ReaderCanStartAgainAfterManualStop()
    {
        var firstSocket = new FakeWebSocket().EnqueueMessage("{\"id\":\"first\"}");
        var secondSocket = new FakeWebSocket().EnqueueMessage("{\"id\":\"second\"}");
        var factory = new FakeWebSocketFactory(firstSocket, secondSocket);
        var source = CreateSource(factory);

        await using (IAsyncEnumerator<RawProviderMessage> firstReader =
            source.ReadAllAsync().GetAsyncEnumerator())
        {
            Assert.IsTrue(await firstReader.MoveNextAsync());
            await source.StopAsync();
            Assert.IsFalse(await firstReader.MoveNextAsync());
        }

        await using (IAsyncEnumerator<RawProviderMessage> secondReader =
            source.ReadAllAsync().GetAsyncEnumerator())
        {
            Assert.IsTrue(await secondReader.MoveNextAsync());
            Assert.Contains("second", secondReader.Current.Json);
            await source.StopAsync();
        }

        Assert.AreEqual(2, factory.CreateCount);
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task ShortConnectionDoesNotResetRetryCount()
    {
        var clock = new FakeClock();
        var delays = new RecordingDelay();
        var failed = new FakeWebSocket(new WebSocketException("connect failed"));
        var shortConnection = new FakeWebSocket().EnqueueClose();
        var recoveredSocket = new FakeWebSocket().EnqueueMessage("{}");
        var recovered = new RawProviderMessage(
            "p2pquake",
            "{\"id\":\"recovered\"}",
            SourceMode.Production,
            clock.UtcNow);
        var source = CreateSource(
            new FakeWebSocketFactory(failed, shortConnection, recoveredSocket),
            clock,
            new FakeRecoveryClient(recovered),
            delay: delays);
        await using IAsyncEnumerator<RawProviderMessage> reader =
            source.ReadAllAsync().GetAsyncEnumerator();

        Assert.IsTrue(await reader.MoveNextAsync());

        CollectionAssert.Contains(delays.Delays, TimeSpan.FromSeconds(1.1));
        CollectionAssert.Contains(delays.Delays, TimeSpan.FromSeconds(2.2));
        await source.StopAsync();
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task StableThirtySecondConnectionResetsRetryCount()
    {
        var clock = new FakeClock();
        var delays = new RecordingDelay();
        var failed = new FakeWebSocket(new WebSocketException("connect failed"));
        var stableConnection = new FakeWebSocket().EnqueueClose(
            () => clock.Advance(TimeSpan.FromSeconds(30)));
        var recoveredSocket = new FakeWebSocket().EnqueueMessage("{}");
        var recovered = new RawProviderMessage(
            "p2pquake",
            "{\"id\":\"recovered\"}",
            SourceMode.Production,
            clock.UtcNow);
        var source = CreateSource(
            new FakeWebSocketFactory(failed, stableConnection, recoveredSocket),
            clock,
            new FakeRecoveryClient(recovered),
            delay: delays);
        await using IAsyncEnumerator<RawProviderMessage> reader =
            source.ReadAllAsync().GetAsyncEnumerator();

        Assert.IsTrue(await reader.MoveNextAsync());

        Assert.AreEqual(2, delays.Delays.Count(delay => delay == TimeSpan.FromSeconds(1.1)));
        Assert.IsFalse(delays.Delays.Contains(TimeSpan.FromSeconds(2.2)));
        await source.StopAsync();
        await source.DisposeAsync();
    }

    private static P2pEventSource CreateSource(
        FakeWebSocketFactory factory,
        FakeClock? clock = null,
        FakeRecoveryClient? recovery = null,
        MemoryLogWriter? logs = null,
        RecordingDelay? delay = null)
    {
        FakeClock effectiveClock = clock ?? new FakeClock();
        return new P2pEventSource(
            ProviderOptions.Production,
            new P2pEventSourceOptions(
                MaximumMessageBytes: 1024,
                StaleAfter: TimeSpan.FromSeconds(1),
                StableConnectionTime: TimeSpan.FromSeconds(30)),
            effectiveClock,
            logs ?? new MemoryLogWriter(),
            factory,
            recovery ?? new FakeRecoveryClient(),
            new ReconnectDelayPolicy(new FixedJitterSource(0)),
            delay ?? new RecordingDelay());
    }

    private static async Task WaitForStateAsync(
        P2pEventSource source,
        ProviderConnectionState expected)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (source.Connection.State == expected)
            {
                return;
            }

            await Task.Yield();
        }

        Assert.Fail($"Connection state did not become {expected}.");
    }
}
