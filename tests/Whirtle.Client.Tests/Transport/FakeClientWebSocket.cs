using System.Net.WebSockets;
using System.Threading.Channels;
using Whirtle.Client.Transport;

namespace Whirtle.Client.Tests.Transport;

internal sealed class FakeClientWebSocket : IClientWebSocket
{
    private WebSocketState _state = WebSocketState.None;
    private readonly Channel<(byte[] Data, WebSocketMessageType Type)> _incoming =
        Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();

    public WebSocketState State => _state;

    public void EnqueueMessage(byte[] data, WebSocketMessageType type = WebSocketMessageType.Binary)
        => _incoming.Writer.TryWrite((data, type));

    public void EnqueueClose()
        => _incoming.Writer.TryWrite(([], WebSocketMessageType.Close));

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Open;
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var (data, type) = await _incoming.Reader.ReadAsync(cancellationToken);

        if (type == WebSocketMessageType.Close)
        {
            _state = WebSocketState.CloseReceived;
            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
        }

        data.CopyTo(buffer);
        return new ValueWebSocketReceiveResult(data.Length, type, endOfMessage: true);
    }

    public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
