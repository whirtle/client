// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Serilog;

namespace Whirtle.Client.Transport;

public sealed class WebSocketTransport : ITransport, IAsyncDisposable
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly IClientWebSocket _webSocket;
    private readonly int _receiveBufferSize;
    private readonly TimeSpan _connectTimeout;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _webSocket.State == WebSocketState.Open;

    public WebSocketTransport(int receiveBufferSize = 4096, TimeSpan? connectTimeout = null)
        : this(new SystemClientWebSocket(), receiveBufferSize, connectTimeout) { }

    internal WebSocketTransport(IClientWebSocket webSocket, int receiveBufferSize = 4096, TimeSpan? connectTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(receiveBufferSize, 1);
        _webSocket = webSocket;
        _receiveBufferSize = receiveBufferSize;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
    }

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(_connectTimeout);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await _webSocket.ConnectAsync(uri, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"WebSocket connection to {uri} timed out after {_connectTimeout.TotalSeconds:0}s.");
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Transport is not connected.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _webSocket
                .SendAsync(data, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
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
                {
                    Log.Debug("WebSocket closed by remote: status={CloseStatus} description={Description}",
                        _webSocket.CloseStatus?.ToString() ?? "None",
                        _webSocket.CloseStatusDescription ?? "");
                    yield break;
                }

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
