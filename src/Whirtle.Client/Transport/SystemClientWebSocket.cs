// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;

namespace Whirtle.Client.Transport;

internal sealed class SystemClientWebSocket : IClientWebSocket
{
    private readonly ClientWebSocket _inner = new();

    public WebSocketState State => _inner.State;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        => _inner.ConnectAsync(uri, cancellationToken);

    public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => _inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _inner.ReceiveAsync(buffer, cancellationToken);

    public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => _inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public void Dispose() => _inner.Dispose();
}
