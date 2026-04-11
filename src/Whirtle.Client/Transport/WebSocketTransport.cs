// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Runtime.CompilerServices;

namespace Whirtle.Client.Transport;

public sealed class WebSocketTransport : ITransport, IAsyncDisposable
{
    private readonly IClientWebSocket _webSocket;
    private readonly int _receiveBufferSize;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _webSocket.State == WebSocketState.Open;

    public WebSocketTransport(int receiveBufferSize = 4096)
        : this(new SystemClientWebSocket(), receiveBufferSize) { }

    internal WebSocketTransport(IClientWebSocket webSocket, int receiveBufferSize = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(receiveBufferSize, 1);
        _webSocket = webSocket;
        _receiveBufferSize = receiveBufferSize;
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
        => _webSocket.ConnectAsync(uri, cancellationToken);

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Transport is not connected.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _webSocket
                .SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<byte[]> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[_receiveBufferSize];

        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult result;

            do
            {
                result = await _webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                    yield break;

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            yield return message.ToArray();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            return;

        await _webSocket
            .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsConnected)
            await DisconnectAsync().ConfigureAwait(false);

        _sendLock.Dispose();
        _webSocket.Dispose();
    }
}
