using System.Net.WebSockets;

namespace Whirtle.Client.Transport;

/// <summary>
/// Adapts an already-connected <see cref="System.Net.WebSockets.WebSocket"/>
/// (accepted by <see cref="WebSocketListener"/>) to <see cref="IClientWebSocket"/>
/// so it can be used with <see cref="WebSocketTransport"/>.
/// <see cref="ConnectAsync"/> is a no-op because the connection is already open.
/// </summary>
internal sealed class AcceptedWebSocket : IClientWebSocket
{
    private readonly WebSocket _ws;

    public AcceptedWebSocket(WebSocket ws) => _ws = ws;

    public WebSocketState State => _ws.State;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        => Task.CompletedTask; // already connected

    public ValueTask SendAsync(
        ReadOnlyMemory<byte>  buffer,
        WebSocketMessageType  messageType,
        bool                  endOfMessage,
        CancellationToken     cancellationToken)
        => _ws.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte>      buffer,
        CancellationToken cancellationToken)
        => _ws.ReceiveAsync(buffer, cancellationToken);

    public Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string?              statusDescription,
        CancellationToken    cancellationToken)
        => _ws.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public void Dispose() => _ws.Dispose();
}
