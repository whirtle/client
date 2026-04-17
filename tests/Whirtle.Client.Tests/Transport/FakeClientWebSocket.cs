using System.Net.WebSockets;
using System.Threading.Channels;
using Whirtle.Client.Transport;

namespace Whirtle.Client.Tests.Transport;

internal sealed class FakeClientWebSocket : IClientWebSocket
{
    private WebSocketState _state = WebSocketState.None;

    private readonly record struct Queued(byte[]? Data, WebSocketMessageType Type, Exception? Error);
    private readonly Channel<Queued> _incoming = Channel.CreateUnbounded<Queued>();

    private TaskCompletionSource? _connectBlock;

    public WebSocketState State => _state;
    public WebSocketCloseStatus? CloseStatus => null;
    public string? CloseStatusDescription => null;

    public void EnqueueMessage(byte[] data, WebSocketMessageType type = WebSocketMessageType.Binary)
        => _incoming.Writer.TryWrite(new Queued(data, type, null));

    public void EnqueueClose()
        => _incoming.Writer.TryWrite(new Queued([], WebSocketMessageType.Close, null));

    /// <summary>Causes the next ReceiveAsync call to throw <paramref name="ex"/>.</summary>
    public void EnqueueException(Exception ex)
        => _incoming.Writer.TryWrite(new Queued(null, WebSocketMessageType.Binary, ex));

    /// <summary>
    /// Makes the next <see cref="ConnectAsync"/> call block until the caller's
    /// cancellation token is cancelled or <see cref="UnblockConnect"/> is called.
    /// </summary>
    public void BlockConnect() => _connectBlock = new TaskCompletionSource();

    /// <summary>Unblocks a previously blocked <see cref="ConnectAsync"/>.</summary>
    public void UnblockConnect() => _connectBlock?.TrySetResult();

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (_connectBlock is { } block)
            await block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        _state = WebSocketState.Open;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var queued = await _incoming.Reader.ReadAsync(cancellationToken);

        if (queued.Error is not null)
            throw queued.Error;

        var (data, type) = (queued.Data!, queued.Type);

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
