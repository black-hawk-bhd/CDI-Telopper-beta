using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class ReceptionBufferSecurityTests
{
    [TestMethod]
    public async Task ByteBudgetIncludesTransportAndKeepsCurrentDeliveryReserved()
    {
        var buffer = new ProviderMessageBuffer(10, 512);
        RawProviderMessage message = Message("a") with { TransportPayload = new string('t', 100) };
        await buffer.WriteAsync(message);
        Task pending = buffer.WriteAsync(Message("b")).AsTask();
        Assert.IsFalse(pending.IsCompleted);
        await using IAsyncEnumerator<RawProviderMessage> reader = buffer.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreSame(message, reader.Current);
        Assert.IsFalse(pending.IsCompleted);
        Task<bool> next = reader.MoveNextAsync().AsTask();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("b", reader.Current.Payload);
        buffer.TryComplete();
        Assert.IsFalse(await reader.MoveNextAsync());
    }

    [TestMethod]
    public async Task CancelledWaitingWriterDoesNotConsumeCapacityOrLoseOtherMessages()
    {
        var buffer = new ProviderMessageBuffer(1, 1024);
        await buffer.WriteAsync(Message("first"));
        using var cancellation = new CancellationTokenSource();
        Task pending = buffer.WriteAsync(Message("cancelled"), cancellation.Token).AsTask();
        Assert.IsFalse(pending.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await using IAsyncEnumerator<RawProviderMessage> reader = buffer.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual("first", reader.Current.Payload);
        Task last = buffer.WriteAsync(Message("last")).AsTask();
        Task<bool> next = reader.MoveNextAsync().AsTask();
        await last.WaitAsync(TimeSpan.FromSeconds(5));
        buffer.TryComplete();
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("last", reader.Current.Payload);
        Assert.IsFalse(await reader.MoveNextAsync());
    }

    [TestMethod]
    public async Task CompletionWakesBlockedWritersAndDrainsExistingMessagesBeforeError()
    {
        var buffer = new ProviderMessageBuffer(1, 1024);
        await buffer.WriteAsync(Message("first"));
        Task pending = buffer.WriteAsync(Message("second")).AsTask();
        var failure = new IOException("test completion");
        Assert.IsTrue(buffer.TryComplete(failure));
        Assert.IsFalse(buffer.TryComplete());
        await Assert.ThrowsExactlyAsync<ChannelClosedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await using IAsyncEnumerator<RawProviderMessage> reader = buffer.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual("first", reader.Current.Payload);
        IOException error = await Assert.ThrowsExactlyAsync<IOException>(() => reader.MoveNextAsync().AsTask());
        Assert.AreSame(failure, error);
    }

    [TestMethod]
    public async Task IndividuallyOversizeMessageIsRejectedRatherThanWaitingForever()
    {
        var buffer = new ProviderMessageBuffer(1, 300);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            buffer.WriteAsync(Message(new string('x', 100))).AsTask());
        await buffer.WriteAsync(Message("ok"));
        buffer.TryComplete();
        await foreach (RawProviderMessage message in buffer.ReadAllAsync()) Assert.AreEqual("ok", message.Payload);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SlowConsumerBackpressuresProducerAndPreservesEveryMessage(bool parallel)
    {
        var producer = new CountingSource(600);
        await using IEventSource source = CreateSource(parallel, producer);
        await using IAsyncEnumerator<RawProviderMessage> reader = source.ReadAllAsync().GetAsyncEnumerator();

        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.IsLessThanOrEqualTo(258, producer.Produced);
        var received = new List<RawProviderMessage> { reader.Current };
        while (await reader.MoveNextAsync()) received.Add(reader.Current);

        Assert.HasCount(600, received);
        for (int index = 0; index < received.Count; index++)
        {
            Assert.AreEqual(index == 599 ? "cancel" : index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                received[index].Payload);
            Assert.AreEqual(index % 2 == 0 ? SourceMode.Production : SourceMode.Sandbox, received[index].SourceMode);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task StopOrDisposeUnblocksFullReceptionAndStopAllowsRestart(bool parallel, bool dispose)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var producer = new CountingSource(600);
        await using IEventSource source = CreateSource(parallel, producer);
        await using (IAsyncEnumerator<RawProviderMessage> reader = source.ReadAllAsync(timeout.Token).GetAsyncEnumerator())
        {
            Assert.IsTrue(await reader.MoveNextAsync());
            if (dispose) await source.DisposeAsync();
            else await source.StopAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => reader.MoveNextAsync().AsTask());
        }
        if (!dispose)
        {
            int count = 0;
            await foreach (RawProviderMessage _ in source.ReadAllAsync(timeout.Token)) count++;
            Assert.AreEqual(600, count);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EarlyEnumeratorDisposalUnblocksFullWriters(bool parallel)
    {
        var producer = new CountingSource(600);
        await using IEventSource source = CreateSource(parallel, producer);
        IAsyncEnumerator<RawProviderMessage> reader = source.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        await reader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReaderGuardRemainsActiveUntilOldCancellationRegistrationIsCleared(bool parallel)
    {
        await using IEventSource source = CreateSource(parallel, new CountingSource(1));
        await using IAsyncEnumerator<RawProviderMessage> reader = source.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object gate = source.GetType().GetField("_gate", flags)!.GetValue(source)!;
        System.Reflection.FieldInfo active = source.GetType().GetField("_readerActive", flags)!;
        using var started = new ManualResetEventSlim();
        Task<bool> finishing;
        Monitor.Enter(gate);
        try
        {
            finishing = Task.Run(async () =>
            {
                started.Set();
                return await reader.MoveNextAsync();
            });
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(SpinWait.SpinUntil(() => (int)active.GetValue(source)! == 0, TimeSpan.FromMilliseconds(250)),
                "A new reader must not start while old cancellation cleanup is blocked.");
        }
        finally { Monitor.Exit(gate); }
        Assert.IsFalse(await finishing.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task NestedMergerBackpressuresItsProducerAndPreservesCancellation()
    {
        var producer = new CountingSource(900);
        await using IEventSource inner = CreateSource(true, producer);
        await using var outer = new RoutedProviderEventSource(AppSettings.CreateDefault().Provider with
            { JmaXmlAutoFallback = false, Routing = ProviderRoutingSettings.FromLegacy(ReceptionProvider.P2pQuake) },
            new Dictionary<ReceptionProvider, IEventSource> { [ReceptionProvider.P2pQuake] = inner });
        await using IAsyncEnumerator<RawProviderMessage> reader = outer.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.IsLessThanOrEqualTo(514, producer.Produced);
        int count = 1;
        string last = reader.Current.Payload;
        while (await reader.MoveNextAsync()) { count++; last = reader.Current.Payload; }
        Assert.AreEqual(900, count);
        Assert.AreEqual("cancel", last);
    }

    private static RawProviderMessage Message(string payload) => new("test", payload, SourceMode.Production, DateTimeOffset.UtcNow);

    private static IEventSource CreateSource(bool parallel, CountingSource producer) => parallel
        ? new ParallelEventSource([new ParallelEventSourceBranch("first", producer),
            new ParallelEventSourceBranch("empty", new CountingSource(0))])
        : new RoutedProviderEventSource(AppSettings.CreateDefault().Provider with
            { JmaXmlAutoFallback = false, Routing = ProviderRoutingSettings.FromLegacy(ReceptionProvider.P2pQuake) },
            new Dictionary<ReceptionProvider, IEventSource> { [ReceptionProvider.P2pQuake] = producer });

    private sealed class CountingSource(int count) : IEventSource
    {
        public int Produced { get; private set; }
        public ProviderConnectionSnapshot Connection { get; } = new(ProviderConnectionState.Connected, DateTimeOffset.UtcNow);
        public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged { add { } remove { } }
        public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Produced++;
                yield return new RawProviderMessage("test", index == count - 1 ? "cancel" :
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    index % 2 == 0 ? SourceMode.Production : SourceMode.Sandbox, DateTimeOffset.UtcNow);
            }
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void RequestReconnect(ReconnectReason reason) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
