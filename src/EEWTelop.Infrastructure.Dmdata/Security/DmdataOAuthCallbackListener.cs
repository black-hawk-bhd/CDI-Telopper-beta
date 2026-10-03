using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace EEWTelop.Infrastructure.Dmdata.Security;

internal sealed class DmdataOAuthCallbackListener : IDisposable
{
    public const string CallbackPath = "/oauth2/callback";
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public DmdataOAuthCallbackListener()
    {
        _listener.Start();
        RedirectUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{CallbackPath}");
    }

    public Uri RedirectUri { get; }

    public async Task<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            NetworkStream stream = client.GetStream();
            string? code = null;
            string? error = null;
            bool matched = false;
            try
            {
                string request = await ReadHeadersAsync(stream, requestTimeout.Token).ConfigureAwait(false);
                string[] parts = request.Split("\r\n", StringSplitOptions.None)[0].Split(' ');
                if (parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith(CallbackPath + "?", StringComparison.Ordinal) &&
                    Uri.TryCreate(RedirectUri.GetLeftPart(UriPartial.Authority) + parts[1], UriKind.Absolute, out Uri? uri) &&
                    uri.AbsolutePath == CallbackPath)
                {
                    Dictionary<string, string>? parameters = ParseQuery(uri.Query);
                    if (parameters is not null && parameters.TryGetValue("state", out string? state) &&
                        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
                    {
                        matched = true;
                        parameters.TryGetValue("code", out code);
                        parameters.TryGetValue("error", out error);
                    }
                }
                string message = matched ? "CDI-Telopper: browser authorization received. You may close this tab."
                    : "CDI-Telopper: invalid authorization callback.";
                byte[] body = Encoding.UTF8.GetBytes(message);
                byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(matched ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, requestTimeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, requestTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            if (!matched) continue;
            if (!string.IsNullOrEmpty(error)) throw new DmdataOAuthException("authorization_denied", true);
            if (string.IsNullOrWhiteSpace(code) || code.Length > 2048)
                throw new DmdataOAuthException("invalid_response", true);
            return code;
        }
    }

    internal static Dictionary<string, string>? ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            string value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            if (!values.TryAdd(key, value)) return null;
        }
        return values;
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        byte[] buffer = new byte[1024];
        while (bytes.Count < 8192)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("Incomplete OAuth callback.");
            bytes.AddRange(buffer.AsSpan(0, count).ToArray());
            string headers = Encoding.ASCII.GetString(bytes.ToArray());
            if (headers.Contains("\r\n\r\n", StringComparison.Ordinal)) return headers;
        }
        throw new InvalidDataException("OAuth callback headers exceeded the limit.");
    }

    public void Dispose() => _listener.Stop();
}
