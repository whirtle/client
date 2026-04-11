// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;

namespace Whirtle.Client.Transport;

internal interface IClientWebSocket : IDisposable
{
    WebSocketState State { get; }
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);
    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken);
}
