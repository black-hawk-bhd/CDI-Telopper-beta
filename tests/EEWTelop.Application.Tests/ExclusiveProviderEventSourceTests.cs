using System.Runtime.CompilerServices;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class ExclusiveProviderEventSourceTests
{
    private static readonly string[] ExpectedParallelPayloads =
        ["keep-first", "keep-second"];

    [TestMethod]
    public async Task SelectedProviderAloneIsReadAndCanBeSwitchedWhileStopped()
    {
        var p2p = new FakeSource("p2pquake");
        var dmdata = new FakeSource("dmdata.jp");
        await using var exclusive = new ExclusiveProviderEventSource(
            ReceptionProvider.P2pQuake,
            new Dictionary<ReceptionProvider, IEventSource>
            {
                [ReceptionProvider.P2pQuake] = p2p,
                [ReceptionProvider.Dmdata] = dmdata,
            });

        await DrainAsync(exclusive);
        exclusive.ConfigureProvider(AppSettings.CreateDefault().Provider with
        {
            ReceptionProvider = ReceptionProvider.Dmdata,
        });
        await DrainAsync(exclusive);

        Assert.AreEqual(1, p2p.ReadCount);
        Assert.AreEqual(1, dmdata.ReadCount);
        Assert.AreEqual(0, p2p.ConcurrentReadCount);
        Assert.AreEqual(0, dmdata.ConcurrentReadCount);
    }

    [TestMethod]
    public async Task ParallelSourceReadsBothBranchesAndAppliesTheirRouteFilters()
    {
        var first = new FakeSource("first", ["keep-first", "drop-first"]);
        var second = new FakeSource("second", ["keep-second"]);
        await using var parallel = new ParallelEventSource(
        [
            new ParallelEventSourceBranch(
                "first",
                first,
                static message => message.Payload.StartsWith("keep", StringComparison.Ordinal)),
            new ParallelEventSourceBranch("second", second),
        ]);
        var payloads = new List<string>();

        await foreach (RawProviderMessage message in parallel.ReadAllAsync())
        {
            payloads.Add(message.Payload);
        }

        CollectionAssert.AreEquivalent(
            ExpectedParallelPayloads,
            payloads);
        Assert.AreEqual(1, first.ReadCount);
        Assert.AreEqual(1, second.ReadCount);
    }

    private static async Task DrainAsync(ExclusiveProviderEventSource source)
    {
        await foreach (RawProviderMessage _ in source.ReadAllAsync())
        {
        }
    }

    private sealed class FakeSource : IEventSource, IProviderConfigurableEventSource
    {
        private readonly string _provider;
        private readonly IReadOnlyList<string> _payloads;
        private int _active;

        public FakeSource(string provider, IReadOnlyList<string>? payloads = null)
        {
            _provider = provider;
            _payloads = payloads ?? ["{}"];
            Connection = new ProviderConnectionSnapshot(
                ProviderConnectionState.Stopped,
                DateTimeOffset.UtcNow);
        }

        public int ReadCount { get; private set; }

        public int ConcurrentReadCount { get; private set; }

        public ProviderConnectionSnapshot Connection { get; }

        public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged
        {
            add { }
            remove { }
        }

        public void ConfigureProvider(ProviderSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
        }

        public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (Interlocked.Increment(ref _active) != 1)
            {
                ConcurrentReadCount++;
            }

            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string payload in _payloads)
                {
                    yield return new RawProviderMessage(
                        _provider,
                        payload,
                        SourceMode.Production,
                        DateTimeOffset.UtcNow);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public void RequestReconnect(ReconnectReason reason)
        {
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
