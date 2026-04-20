// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Transport;

public interface ITransport
{
    bool IsConnected { get; }
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default);
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    IAsyncEnumerable<byte[]> ReceiveAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
