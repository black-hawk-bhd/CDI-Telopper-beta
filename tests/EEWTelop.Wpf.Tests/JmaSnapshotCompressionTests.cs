using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EEWTelop.Wpf.Obs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class JmaSnapshotCompressionTests
{
    private const string Snapshot = """{"reportDateTime":"2026/09/20 09:00","item":[{"area":{"name":"岩手県"},"kind":{"code":"52"}}]}""";

    [TestMethod]
    public void ProductionHandlerAllowsGzipAndDeflateButNeverBrotli()
    {
        using var handler = JmaTsunamiSnapshotClient.CreateHandler();
        Assert.IsFalse(handler.AllowAutoRedirect);
        Assert.AreEqual(DecompressionMethods.GZip | DecompressionMethods.Deflate, handler.AutomaticDecompression);
        Assert.AreEqual(0, (int)(handler.AutomaticDecompression & DecompressionMethods.Brotli));
    }

    [TestMethod]
    [DataRow("br")]
    [DataRow("BR")]
    [DataRow(" br ")]
    [DataRow("gzip, br")]
    [DataRow("br, gzip")]
    [DataRow("identity, br")]
    [DataRow("zstd")]
    public async Task UnsupportedOrLayeredEncodingIsRejectedBeforeOpeningBody(string encoding)
    {
        using var content = new ProbeContent(Encoding.UTF8.GetBytes(Snapshot));
        content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);
        using var http = new HttpClient(new ResponseHandler(content));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => JmaTsunamiSnapshotClient.FetchAsync(http, CancellationToken.None));
        Assert.AreEqual(0, content.StreamOpenCount);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("identity")]
    [DataRow("IDENTITY")]
    public async Task IdentityAndUnencodedSnapshotsKeepExistingBehavior(string encoding)
    {
        using var content = new ProbeContent(Encoding.UTF8.GetBytes(Snapshot));
        if (encoding.Length > 0) content.Headers.ContentEncoding.Add(encoding);
        using var http = new HttpClient(new ResponseHandler(content));
        var value = await JmaTsunamiSnapshotClient.FetchAsync(http, CancellationToken.None);
        Assert.AreEqual("岩手県", value.Areas.Single().Name);
        Assert.IsFalse(value.IsCancelled);
        Assert.AreEqual(1, content.StreamOpenCount);
    }

    [TestMethod]
    public async Task ExplicitOversizeAndStreamedOversizeRemainRejected()
    {
        using var declared = new ProbeContent(Encoding.UTF8.GetBytes(Snapshot));
        declared.Headers.ContentLength = 1024 * 1024 + 1;
        using var declaredHttp = new HttpClient(new ResponseHandler(declared));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => JmaTsunamiSnapshotClient.FetchAsync(declaredHttp, CancellationToken.None));
        Assert.AreEqual(0, declared.StreamOpenCount);

        using var streamed = new ProbeContent(new byte[1024 * 1024 + 1]);
        using var streamedHttp = new HttpClient(new ResponseHandler(streamed));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => JmaTsunamiSnapshotClient.FetchAsync(streamedHttp, CancellationToken.None));
        Assert.AreEqual(1, streamed.StreamOpenCount);
    }

    [TestMethod]
    [DataRow("gzip")]
    [DataRow("deflate")]
    public async Task ProductionTransportStillNegotiatesAndDecodesSupportedCompression(string encoding)
    {
        byte[] payload;
        using (var output = new MemoryStream())
        {
            using (Stream compressor = encoding == "gzip"
                ? new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)
                : new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                await compressor.WriteAsync(Encoding.UTF8.GetBytes(Snapshot));
            payload = output.ToArray();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var local = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/snapshot");
        Task<string> serving = ServeAsync(listener, payload, encoding, timeout.Token);
        try
        {
            using var transport = JmaTsunamiSnapshotClient.CreateHandler();
            transport.UseProxy = false;
            using var http = new HttpClient(new LoopbackHandler(local, transport));
            var value = await JmaTsunamiSnapshotClient.FetchAsync(http, timeout.Token);
            Assert.AreEqual("岩手県", value.Areas.Single().Name);
            string request = await serving;
            string accepted = request.Split('\n').Single(line => line.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(accepted, "gzip");
            StringAssert.Contains(accepted, "deflate");
            Assert.IsFalse(accepted.Contains("br", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(request, "no-cache");
            StringAssert.Contains(request, "no-store");
        }
        finally
        {
            await timeout.CancelAsync();
            listener.Stop();
            try { await serving; }
            catch (OperationCanceledException) { }
            catch (SocketException) when (timeout.IsCancellationRequested) { }
        }
    }

    private static async Task<string> ServeAsync(TcpListener listener, byte[] payload, string encoding, CancellationToken token)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(token);
        await using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var request = new StringBuilder();
        for (int index = 0; index < 128; index++)
        {
            string line = await reader.ReadLineAsync(token) ?? throw new IOException("Incomplete loopback request.");
            if (line.Length == 0) break;
            request.AppendLine(line);
        }
        string headers = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: {encoding}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), token);
        await stream.WriteAsync(payload, token);
        return request.ToString();
    }

    private sealed class LoopbackHandler(Uri local, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(JmaTsunamiSnapshotClient.Endpoint, request.RequestUri!.AbsoluteUri);
            request.RequestUri = local;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ResponseHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ProbeContent(byte[] body) : HttpContent
    {
        public int StreamOpenCount { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StreamOpenCount++;
            return Task.FromResult<Stream>(new MemoryStream(body, writable: false));
        }
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CreateContentReadStreamAsync();
        }
    }
}
