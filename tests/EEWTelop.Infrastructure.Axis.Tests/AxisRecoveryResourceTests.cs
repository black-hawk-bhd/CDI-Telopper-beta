using System.Net;
using System.Text;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Axis.Configuration;
using EEWTelop.Infrastructure.Axis.Recovery;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Axis.Tests;

[TestClass]
public sealed class AxisRecoveryResourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private const string Feed = """
        <feed xmlns="http://www.w3.org/2005/Atom"><entry><updated>2026-10-22T00:00:00Z</updated>
        <link href="https://www.data.jma.go.jp/developer/xml/data/test.xml" /></entry></feed>
        """;
    private const string Telegram = """
        <Report><Control><Type>VPWW55</Type><Status>訓練</Status></Control><Head/><Body/></Report>
        """;

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task OversizeResponseStopsReadingAtLimitPlusOne(bool telegram, bool understatedLength)
    {
        int limit = (telegram ? 24 : 4) * 1024 * 1024;
        var body = new GeneratedStream(limit + 64 * 1024);
        using var http = new HttpClient(new Handler(request =>
        {
            if (telegram && request.RequestUri!.AbsolutePath.EndsWith("extra.xml", StringComparison.Ordinal))
                return Xml(Feed);
            var content = new StreamContent(body);
            if (understatedLength) content.Headers.ContentLength = 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(Client(http)));

        Assert.IsLessThanOrEqualTo((long)limit + 1, body.BytesRead);
        Assert.IsTrue(body.IsDisposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExactLimitAndTrainingMetadataRemainSupported(bool telegram)
    {
        int limit = (telegram ? 24 : 4) * 1024 * 1024;
        byte[] prefix = Encoding.UTF8.GetBytes(telegram ? Telegram : Feed);
        var body = new GeneratedStream(limit, prefix);
        using var http = new HttpClient(new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("extra.xml", StringComparison.Ordinal) != telegram
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }
                : Xml(telegram ? Feed : Telegram)));

        IReadOnlyList<RawProviderMessage> messages = await ReadAllAsync(Client(http));

        Assert.HasCount(1, messages);
        Assert.AreEqual(SourceMode.Sandbox, messages[0].SourceMode);
        StringAssert.StartsWith(messages[0].Payload, Telegram);
        Assert.AreEqual((long)limit, body.BytesRead);
        Assert.IsTrue(body.IsDisposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeclaredOversizeResponseIsRejectedWithoutReading(bool telegram)
    {
        var body = new GeneratedStream(16);
        using var http = new HttpClient(new Handler(request =>
        {
            if (telegram && request.RequestUri!.AbsolutePath.EndsWith("extra.xml", StringComparison.Ordinal)) return Xml(Feed);
            var content = new StreamContent(body);
            content.Headers.ContentLength = (telegram ? 24L : 4L) * 1024 * 1024 + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(Client(http)));
        Assert.AreEqual(0L, body.BytesRead);
        Assert.IsTrue(body.IsDisposed);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task BodyReadHonorsCallerCancellationAndFiniteDeadline(bool telegram, bool deadline)
    {
        var body = new WaitingStream();
        using var http = new HttpClient(new Handler(request =>
            telegram && request.RequestUri!.AbsolutePath.EndsWith("extra.xml", StringComparison.Ordinal)
                ? Xml(Feed) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        if (deadline) http.Timeout = TimeSpan.FromMilliseconds(150);
        using var cancellation = new CancellationTokenSource();
        Task read = ReadAllAsync(Client(http), cancellation.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!deadline) await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(body.IsDisposed);
    }

    [TestMethod]
    public async Task FailedTelegramClearsFeedEtagSoNextAttemptCanRecover()
    {
        int feeds = 0;
        int telegrams = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("extra.xml", StringComparison.Ordinal))
            {
                feeds++;
                Assert.IsEmpty(request.Headers.IfNoneMatch);
                HttpResponseMessage response = Xml(Feed);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"test\"");
                return response;
            }
            telegrams++;
            return telegrams == 1 ? new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new GeneratedStream(24 * 1024 * 1024 + 1)) } : Xml(Telegram);
        }));
        AxisJmaAtomRecoveryClient client = Client(http);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(client));
        Assert.HasCount(1, await ReadAllAsync(client));
        Assert.AreEqual(2, feeds);
        Assert.AreEqual(2, telegrams);
    }

    private static AxisJmaAtomRecoveryClient Client(HttpClient http) => new(http, new Clock(), new Log());

    private static async Task<IReadOnlyList<RawProviderMessage>> ReadAllAsync(
        AxisJmaAtomRecoveryClient client, CancellationToken cancellationToken = default)
    {
        var messages = new List<RawProviderMessage>();
        var options = new AxisProviderOptions(new Uri("https://axis.prioris.jp/api/"), "test",
            AxisProviderOptions.MeteorologyChannel);
        await foreach (RawProviderMessage message in client.FetchRecentAsync(Now.AddMinutes(-1), options, cancellationToken))
            messages.Add(message);
        return messages;
    }

    private static HttpResponseMessage Xml(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/xml") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class GeneratedStream(long length, byte[]? prefix = null) : Stream
    {
        public long BytesRead { get; private set; }
        public bool IsDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer[..count].Fill((byte)' ');
            if (prefix is not null && BytesRead < prefix.Length)
                prefix.AsSpan((int)BytesRead, Math.Min(count, prefix.Length - (int)BytesRead)).CopyTo(buffer);
            BytesRead += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }

    private sealed class WaitingStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
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
