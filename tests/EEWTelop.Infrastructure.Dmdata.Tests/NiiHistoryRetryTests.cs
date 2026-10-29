using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.History;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.History;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class NiiHistoryRetryTests
{
    private const string ReportId = "20261016092026_0_VXSE53_270000";
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"CDI-NiiRetry-{Guid.NewGuid():N}");
    private readonly UiLogBuffer _log = new();

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    [TestMethod]
    public async Task HeaderTimeoutRetriesThenCachesOnlyTheCompletedTelegram()
    {
        var handler = new Handler(async (attempt, token) =>
        {
            if (attempt == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Telegram();
        });
        using var http = new HttpClient(handler);
        await using var source = Create(http, TimeSpan.FromMilliseconds(60));
        var first = await source.FetchAsync(DirectRequest());
        var second = await source.FetchAsync(DirectRequest());
        Assert.HasCount(1, first);
        Assert.AreEqual(SourceMode.HistoryRehearsal, first[0].SourceMode);
        Assert.AreEqual(first[0].Payload, second[0].Payload);
        Assert.AreEqual(2, handler.Attempts);
        Assert.HasCount(1, Directory.GetFiles(_cache, "*.xml"));
        Assert.HasCount(1, _log.GetSnapshot().Where(item => item.EventName == "NiiHistoryRequestRetry"));
    }

    [TestMethod]
    public async Task BodyTimeoutIsBoundedAndRetriesWithoutCachingPartialContent()
    {
        var handler = new Handler((attempt, _) => Task.FromResult(attempt == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) }
            : Telegram()));
        using var http = new HttpClient(handler);
        await using var source = Create(http, TimeSpan.FromMilliseconds(60));
        var items = await source.FetchAsync(DirectRequest());
        Assert.HasCount(1, items);
        Assert.AreEqual(2, handler.Attempts);
        Assert.AreEqual("<Report/>", items[0].Payload);
        Assert.AreEqual("<Report/>", await File.ReadAllTextAsync(Path.Combine(_cache, ReportId + ".xml")));
    }

    [TestMethod]
    [DataRow(408)]
    [DataRow(429)]
    [DataRow(500)]
    [DataRow(502)]
    [DataRow(503)]
    [DataRow(504)]
    public async Task TransientHttpFailureRetriesOnce(int status)
    {
        var handler = new Handler((attempt, _) => Task.FromResult(attempt == 1
            ? new HttpResponseMessage((HttpStatusCode)status) : Telegram()));
        using var http = new HttpClient(handler);
        await using var source = Create(http);
        Assert.HasCount(1, await source.FetchAsync(DirectRequest()));
        Assert.AreEqual(2, handler.Attempts);
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(404)]
    public async Task PermanentHttpFailureDoesNotRetry(int status)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        await using var source = Create(http);
        var failure = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => source.FetchAsync(DirectRequest()));
        Assert.AreEqual((HttpStatusCode)status, failure.StatusCode);
        Assert.AreEqual(1, handler.Attempts);
        Assert.IsEmpty(Directory.GetFiles(_cache));
    }

    [TestMethod]
    [DataRow(false, "個別電文")]
    [DataRow(true, "日付別一覧")]
    public async Task ExhaustedTimeoutNamesTheFailedResourceAndStopsAtTwoAttempts(bool daily, string resource)
    {
        var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Telegram();
        });
        using var http = new HttpClient(handler);
        await using var source = Create(http, TimeSpan.FromMilliseconds(60));
        var request = daily ? DirectRequest() with { NiiReportUrl = "" } : DirectRequest();
        var failure = await Assert.ThrowsExactlyAsync<TimeoutException>(() => source.FetchAsync(request));
        StringAssert.Contains(failure.Message, resource);
        StringAssert.Contains(failure.Message, "2/2");
        Assert.AreEqual(2, handler.Attempts);
        Assert.IsEmpty(Directory.GetFiles(_cache));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransportFailureRetriesTheDailyIndexAsWell(bool ioFailure)
    {
        var handler = new Handler((attempt, _) => attempt == 1
            ? throw (ioFailure ? (Exception)new IOException("Connection reset") : new HttpRequestException("Connection reset"))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>No reports</html>", Encoding.UTF8, "text/html"),
            }));
        using var http = new HttpClient(handler);
        await using var source = Create(http);
        Assert.IsEmpty(await source.FetchAsync(DirectRequest() with { NiiReportUrl = "" }));
        Assert.AreEqual(2, handler.Attempts);
        StringAssert.Contains(_log.GetSnapshot().Single(item => item.EventName == "NiiHistoryRequestRetry").Message, "日付別一覧");
    }

    [TestMethod]
    public async Task CallerCancellationDuringRetryDelayIsNotRetriedOrReportedAsFailure()
    {
        using var stop = new CancellationTokenSource();
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);
        await using var source = new NiiJmaXmlHistoryMessageSource(http, _cache, new FakeClock(),
            requestTimeout: TimeSpan.FromSeconds(1), retryDelay: TimeSpan.FromSeconds(10),
            logWriter: _log, requestInterval: TimeSpan.Zero);
        _log.EntryAdded += (_, entry) => { if (entry.EventName == "NiiHistoryRequestRetry") stop.Cancel(); };
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.FetchAsync(DirectRequest(), stop.Token));
        Assert.AreEqual(1, handler.Attempts);
        Assert.IsFalse(_log.GetSnapshot().Any(item => item.Level == AppLogLevel.Error));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CallerCancellationDuringRequestIsNotTreatedAsTimeout(bool duringBody)
    {
        using var stop = new CancellationTokenSource();
        var handler = new Handler(async (_, token) =>
        {
            if (!duringBody)
            {
                stop.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingStream(stop.Cancel)),
            };
        });
        using var http = new HttpClient(handler);
        await using var source = Create(http);
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.FetchAsync(DirectRequest(), stop.Token));
        Assert.AreEqual(1, handler.Attempts);
        Assert.IsEmpty(Directory.GetFiles(_cache));
        Assert.IsFalse(_log.GetSnapshot().Any(item => item.EventName == "NiiHistoryRequestRetry" || item.Level == AppLogLevel.Error));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LongRetryAfterIsRespectedWithoutAnEarlyRetry(bool absoluteDate)
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = absoluteDate
                ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(5))
                : new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        await using var source = Create(http);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => source.FetchAsync(DirectRequest()));
        Assert.AreEqual(1, handler.Attempts);
    }

    [TestMethod]
    public async Task OversizedResponseAndMalformedXmlAreNotRetriedOrCached()
    {
        var handler = new Handler((_, _) =>
        {
            var response = Telegram();
            response.Content.Headers.ContentLength = 2 * 1024 * 1024 + 1;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        await using var source = Create(http);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => source.FetchAsync(DirectRequest()));
        Assert.AreEqual(1, handler.Attempts);
        Assert.IsEmpty(Directory.GetFiles(_cache));

        var malformed = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>No XML</html>"),
        }));
        using var malformedHttp = new HttpClient(malformed);
        await using var malformedSource = Create(malformedHttp);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => malformedSource.FetchAsync(DirectRequest()));
        Assert.AreEqual(1, malformed.Attempts);
        Assert.IsEmpty(Directory.GetFiles(_cache));
    }

    private NiiJmaXmlHistoryMessageSource Create(HttpClient http, TimeSpan? timeout = null) =>
        new(http, _cache, new FakeClock(), timeout ?? TimeSpan.FromSeconds(2), TimeSpan.Zero,
            logWriter: _log, requestInterval: TimeSpan.Zero);

    private static HistoryFetchRequest DirectRequest() => new(HistoryApi.NiiJmaXml, 1, AppSettings.CreateDefault().Provider)
    {
        NiiReportUrl = "https://agora.ex.nii.ac.jp/cgi-bin/cps/report_xml.pl?id=" + ReportId,
    };

    private static HttpResponseMessage Telegram() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("<html><pre>&lt;Report/&gt;</pre></html>", Encoding.UTF8, "text/html"),
    };

    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("agora.ex.nii.ac.jp", request.RequestUri!.Host);
            return send(++Attempts, token);
        }
    }

    private sealed class HangingStream(Action? onWait = null) : Stream
    {
        private bool _prefixRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_prefixRead)
            {
                _prefixRead = true;
                byte[] prefix = Encoding.UTF8.GetBytes("<html><pre>&lt;Report&gt;");
                prefix.AsMemory().CopyTo(buffer);
                return prefix.Length;
            }
            onWait?.Invoke();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
