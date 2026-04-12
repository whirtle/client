// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Whirtle.Client.Transport;

namespace Whirtle.Client.Protocol;

public sealed class ProtocolClient : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly MessageSerializer _serializer = new();

    public ProtocolClient(ITransport transport)
    {
        _transport = transport;
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
        => _transport.ConnectAsync(uri, cancellationToken);

    /// <summary>
    /// Sends <see cref="HelloMessage"/> and waits for the server's <see cref="WelcomeMessage"/>.
    /// Throws <see cref="HandshakeException"/> if the server replies with an error or
    /// closes the connection before welcoming.
    /// </summary>
    public Task<WelcomeMessage> HandshakeAsync(
        string clientVersion,
        CancellationToken cancellationToken = default)
        => HandshakeAsync(clientVersion, playerSupport: null, cancellationToken);

    /// <summary>
    /// Sends <see cref="HelloMessage"/> (with optional player capabilities) and waits for
    /// the server's <see cref="WelcomeMessage"/>.
    /// </summary>
    public async Task<WelcomeMessage> HandshakeAsync(
        string           clientVersion,
        PlayerV1Support? playerSupport,
        CancellationToken cancellationToken = default)
    {
        await SendAsync(new HelloMessage(clientVersion, playerSupport), cancellationToken);

        await foreach (var msg in ReceiveRawAsync(cancellationToken))
        {
            return msg switch
            {
                WelcomeMessage welcome => welcome,
                ErrorMessage error     => throw new HandshakeException(error.Code, error.Description),
                _                      => throw new HandshakeException(
                                              "unexpected_message",
                                              $"Expected Welcome but received {msg.GetType().Name}."),
            };
        }

        throw new HandshakeException(
            "connection_closed",
            "Connection closed before handshake completed.");
    }

    public async Task SendAsync(Message message, CancellationToken cancellationToken = default)
    {
        var data = _serializer.Serialize(message);
        await _transport.SendAsync(data, cancellationToken);
    }

    /// <summary>
    /// Yields decoded messages until the connection closes or a
    /// <see cref="GoodbyeMessage"/> is received (which is consumed, not yielded).
    /// </summary>
    public async IAsyncEnumerable<Message> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var msg in ReceiveRawAsync(cancellationToken))
        {
            if (msg is GoodbyeMessage)
                yield break;

            yield return msg;
        }
    }

    public async Task DisconnectAsync(
        string reason = "normal",
        CancellationToken cancellationToken = default)
    {
        await SendAsync(new GoodbyeMessage(reason), cancellationToken);
        await _transport.DisconnectAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_transport.IsConnected)
            await DisconnectAsync().ConfigureAwait(false);

        if (_transport is IAsyncDisposable d)
            await d.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Yields all incoming frames — both JSON protocol messages and binary artwork data —
    /// until the connection closes or a <see cref="GoodbyeMessage"/> is received.
    /// Use this instead of <see cref="ReceiveAsync"/> once the session is fully established.
    /// Do not call both concurrently; they share the same underlying transport stream.
    /// </summary>
    public async IAsyncEnumerable<IncomingFrame> ReceiveAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var data in _transport.ReceiveAsync(cancellationToken))
        {
            if (data.Length == 0) continue;

            if (data[0] == (byte)'{')
            {
                var msg = _serializer.Deserialize(data);
                if (msg is GoodbyeMessage) yield break;
                yield return new ProtocolFrame(msg);
            }
            else
            {
                yield return new ArtworkFrame(data, DetectMimeType(data));
            }
        }
    }

    // Deserializes the raw byte stream without Goodbye filtering,
    // so HandshakeAsync can inspect all message types including errors.
    // Binary (non-JSON) frames are skipped — they are artwork and should
    // be consumed via ReceiveAllAsync instead.
    private async IAsyncEnumerable<Message> ReceiveRawAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var data in _transport.ReceiveAsync(cancellationToken))
        {
            if (data.Length == 0 || data[0] != (byte)'{') continue;
            yield return _serializer.Deserialize(data);
        }
    }

    private static string DetectMimeType(byte[] data) =>
        data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8
            ? "image/jpeg"
            : data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
                ? "image/png"
                : "application/octet-stream";
}
