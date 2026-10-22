using System.Runtime.CompilerServices;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Axis.Configuration;
using EEWTelop.Infrastructure.Axis.Recovery;
using EEWTelop.Infrastructure.Axis.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Axis.Tests;

[TestClass]
public sealed class AxisRecoveryBufferTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly AxisProviderOptions Options = new(new Uri("https://axis.prioris.jp/api/"), "test",
        AxisProviderOptions.SeismologyChannel);

    [TestMethod]
    [DataRow(2, 8192L, 3)]
    [DataRow(10, 800L, 2)]
    public async Task RecoveryStreamsUnderBothBudgetsWithoutLosingUpdates(int capacity, long bytes, int maximumProduced)
    {
        var recovery = new Recovery(20);
        await using var source = new AxisEventSource(Options, new Clock(), new Log(), new HttpClient(), recovery);
        await using IAsyncEnumerator<RawProviderMessage> reader = source.ReadRecoveryAsync(Now, Options,
            new ProviderMessageBuffer(capacity, bytes), CancellationToken.None).GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.IsLessThanOrEqualTo(maximumProduced, recovery.Produced);
        int count = 0;
        do
        {
            Assert.Contains($"<Index>{count}</Index>", reader.Current.Payload);
            Assert.AreEqual(count % 2 == 0 ? SourceMode.Production : SourceMode.Sandbox, reader.Current.SourceMode);
            if (count == 19) Assert.Contains("取消", reader.Current.Payload);
            count++;
        } while (await reader.MoveNextAsync());
        Assert.AreEqual(20, count);
        Assert.IsTrue(recovery.Finished);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveryCancellationOrDisposalUnblocksProducer(bool cancel)
    {
        var recovery = new Recovery(20);
        await using var source = new AxisEventSource(Options, new Clock(), new Log(), new HttpClient(), recovery);
        using var cancellation = new CancellationTokenSource();
        await using IAsyncEnumerator<RawProviderMessage> reader = source.ReadRecoveryAsync(Now, Options,
            new ProviderMessageBuffer(1, 1024), cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        if (cancel)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => reader.MoveNextAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else await reader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(recovery.Finished);
    }

    [TestMethod]
    public async Task InitialConnectionDoesNotStartRecovery()
    {
        var recovery = new Recovery(1);
        await using var source = new AxisEventSource(Options, new Clock(), new Log(), new HttpClient(), recovery);
        await foreach (RawProviderMessage _ in source.ReadRecoveryAsync(null, Options,
            new ProviderMessageBuffer(), CancellationToken.None)) Assert.Fail("Initial connection must not replay history.");
        Assert.AreEqual(0, recovery.Produced);
    }

    private sealed class Recovery(int count) : IAxisRecoveryClient
    {
        public int Produced { get; private set; }
        public bool Finished { get; private set; }
        public async IAsyncEnumerable<RawProviderMessage> FetchRecentAsync(DateTimeOffset since, AxisProviderOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            try
            {
                for (int index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Produced++;
                    yield return new RawProviderMessage("axis",
                        $"<Report><Control><Type>VXSE45</Type></Control><Head><InfoType>{(index == count - 1 ? "取消" : "発表")}</InfoType></Head><Body><Index>{index}</Index></Body></Report>",
                        index % 2 == 0 ? SourceMode.Production : SourceMode.Sandbox, Now)
                    { ContentFormat = RawProviderContentFormat.JmaXml };
                }
            }
            finally { Finished = true; }
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
    private sealed class Log : IAppLogWriter
    {
        public ValueTask WriteAsync(AppLogEntry entry, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
