using System.Net.WebSockets;
using System.Text;

namespace EEWTelop.Infrastructure.P2P.Transport;

internal interface IProviderWebSocketFactory
{
    IProviderWebSocket Create();
}

internal interface IProviderWebSocket : IAsyncDisposable
{
    ValueTask ConnectAsync(Uri uri, CancellationToken cancellationToken);

    ValueTask<ProviderSocketMessage> ReceiveAsync(
        int maximumMessageBytes,
        CancellationToken cancellationToken);
}

internal sealed record ProviderSocketMessage(
    string? Json,
    bool IsClosed = false,
    string? RejectionReason = null);

internal sealed class ClientWebSocketFactory : IProviderWebSocketFactory
{
    public IProviderWebSocket Create() => new ClientWebSocketAdapter(new ClientWebSocket());
}

internal sealed class ClientWebSocketAdapter : IProviderWebSocket
{
    private const int BufferSize = 16 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly ClientWebSocket _socket;

    public ClientWebSocketAdapter(ClientWebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
    }

    public ValueTask ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        new(_socket.ConnectAsync(uri, cancellationToken));

    public async ValueTask<ProviderSocketMessage> ReceiveAsync(
        int maximumMessageBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        using var payload = new MemoryStream(Math.Min(maximumMessageBytes, BufferSize));
        bool tooLarge = false;
        WebSocketMessageType? messageType = null;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new ProviderSocketMessage(null, IsClosed: true);
            }

            messageType ??= result.MessageType;
            if (payload.Length + result.Count > maximumMessageBytes)
            {
                tooLarge = true;
            }
            else if (!tooLarge)
            {
                payload.Write(buffer, 0, result.Count);
            }
        }
        while (!result.EndOfMessage);

        if (messageType != WebSocketMessageType.Text)
        {
            return new ProviderSocketMessage(null, RejectionReason: "Binary WebSocket messages are not supported.");
        }

        if (tooLarge)
        {
            return new ProviderSocketMessage(
                null,
                RejectionReason: $"The WebSocket message exceeded {maximumMessageBytes} bytes.");
        }

        try
        {
            return new ProviderSocketMessage(StrictUtf8.GetString(payload.GetBuffer(), 0, (int)payload.Length));
        }
        catch (DecoderFallbackException)
        {
            return new ProviderSocketMessage(null, RejectionReason: "The WebSocket message was not valid UTF-8.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
