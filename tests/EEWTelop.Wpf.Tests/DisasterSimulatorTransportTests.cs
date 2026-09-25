using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class DisasterSimulatorTransportTests
{
    [TestMethod]
    public async Task AuthenticatedLocalWebSocketReceivesTrainingUpdate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new TaskCompletionSource<DisasterEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = ServeAsync(listener, timeout.Token);
        Task client = DisasterSimulatorClient.RunAsync($"http://127.0.0.1:{port}/", "test-token",
            (items, _) => { foreach (var item in items) received.TrySetResult(item); return Task.CompletedTask; },
            () => { }, timeout.Token);
        try
        {
            var item = await received.Task.WaitAsync(timeout.Token);
            Assert.AreEqual(SourceMode.ManualTest, item.SourceMode);
            Assert.AreEqual("simulator:local-test", item.Id.Value);
            Assert.IsInstanceOfType<QuakeEvent>(item);
            await server;
        }
        finally
        {
            await timeout.CancelAsync();
            try { await client; } catch (OperationCanceledException) { } catch (System.IO.IOException) { } catch (WebSocketException) { }
            try { await server; } catch (OperationCanceledException) { }
        }
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken ct)
    {
        using (var statusClient = await listener.AcceptTcpClientAsync(ct))
        {
            var stream = statusClient.GetStream();
            string request = await ReadHeadersAsync(stream, ct);
            StringAssert.Contains(request, "GET /api/v1/status ");
            StringAssert.Contains(request, "Authorization: Bearer test-token");
            const string body = """{"application":"CDI-Telopper Disaster Simulator","simulator":true,"apiVersion":"1"}""";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), ct);
        }
        using var eventsClient = await listener.AcceptTcpClientAsync(ct);
        var eventsStream = eventsClient.GetStream();
        string headers = await ReadHeadersAsync(eventsStream, ct);
        StringAssert.Contains(headers, "GET /api/v1/events ");
        StringAssert.Contains(headers, "Authorization: Bearer test-token");
        string key = headers.Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        // SHA-1 is mandated by the WebSocket handshake, not used for credential storage.
#pragma warning disable CA5350 // RFC 6455 handshake compatibility.
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
        await eventsStream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), ct);
        using var socket = WebSocket.CreateFromStream(eventsStream, true, null, Timeout.InfiniteTimeSpan);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""
            {"apiVersion":"1","type":"snapshot","sessionId":"test","sequence":1,
             "earthquake":{"hasInformation":false},"eew":{"hasInformation":false},"tsunami":{"hasInformation":false}}
            """), WebSocketMessageType.Text, true, ct);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""
            {"apiVersion":"1","type":"update","sessionId":"test","sequence":2,
             "earthquake":{"hasInformation":true,"earthquake":{"eventId":"local-test","informationType":"DetailScale","sourceMode":"live"}},
             "eew":{"hasInformation":false},"tsunami":{"hasInformation":false}}
            """), WebSocketMessageType.Text, true, ct);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", ct);
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        var text = new StringBuilder();
        byte[] b = new byte[1];
        while (text.Length < 16384)
        {
            if (await stream.ReadAsync(b, ct) == 0) throw new System.IO.EndOfStreamException();
            text.Append((char)b[0]);
            if (text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) return text.ToString();
        }
        throw new System.IO.IOException("Header too large");
    }
}
