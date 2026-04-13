// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Serilog;
using Whirtle.Client.Transport;

namespace Whirtle.Client.Protocol;

public sealed class ProtocolClient : IAsyncDisposable
{
    private readonly ITransport       _transport;
    private readonly MessageSerializer _serializer = new();

    public ProtocolClient(ITransport transport)
    {
        _transport = transport;
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
        => _transport.ConnectAsync(uri, cancellationToken);

    /// <summary>
    /// Sends <see cref="ClientHelloMessage"/> and waits for the server's
    /// <see cref="ServerHelloMessage"/>.
    /// Throws <see cref="HandshakeException"/> if the connection closes before the
    /// server replies or if an unexpected message arrives.
    /// </summary>
    public async Task<ServerHelloMessage> HandshakeAsync(
        string            clientId,
        string            clientName,
        string[]?         supportedRoles    = null,
        CancellationToken cancellationToken = default)
    {
        supportedRoles ??= ["metadata@v1", "controller@v1", "artwork@v1"];

        await SendAsync(
            new ClientHelloMessage(clientId, clientName, Version: 1, supportedRoles),
            cancellationToken);

        await foreach (var msg in ReceiveRawAsync(cancellationToken))
        {
            return msg switch
            {
                ServerHelloMessage hello => hello,
                UnknownMessage     u     => throw new HandshakeException(
                                               "unexpected_message",
                                               $"Expected server/hello but received '{u.Type}'."),
                _                        => throw new HandshakeException(
                                               "unexpected_message",
                                               $"Expected server/hello but received {msg.GetType().Name}."),
            };
        }

        throw new HandshakeException(
            "connection_closed",
            "Connection closed before handshake completed.");
    }

    public async Task SendAsync(Message message, CancellationToken cancellationToken = default)
    {
        Log.Debug("Send {Type} {Message}", _serializer.GetWireType(message), message);
        var data = _serializer.Serialize(message);
        await _transport.SendAsync(data, cancellationToken);
    }

    /// <summary>
    /// Yields decoded messages until the connection closes.
    /// Binary (non-JSON) frames are skipped — consume via <see cref="ReceiveAllAsync"/>
    /// to handle artwork and audio.
    /// </summary>
    public async IAsyncEnumerable<Message> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var msg in ReceiveRawAsync(cancellationToken))
            yield return msg;
    }

    public async Task DisconnectAsync(
        string reason = "shutdown",
        CancellationToken cancellationToken = default)
    {
        try { await SendAsync(new ClientGoodbyeMessage(reason), cancellationToken); } catch { }
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
    /// Yields all incoming frames — both JSON protocol messages and binary artwork
    /// or audio data — until the connection closes.
    /// Do not call <see cref="ReceiveAsync"/> and <see cref="ReceiveAllAsync"/>
    /// concurrently; they share the same underlying transport stream.
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
                Log.Debug("Recv {Type} {Message}", _serializer.GetWireType(msg), msg);
                yield return new ProtocolFrame(msg);
            }
            else
            {
                // Binary frames: first byte is the Sendspin binary message type.
                // 4  = audio chunk
                // 8–11 = artwork channel 0–3
                var typeId  = data[0];
                var payload = data[1..];
                if (typeId is >= 8 and <= 11 && payload.Length >= 8)
                {
                    long  timestamp = BinaryPrimitives.ReadInt64BigEndian(payload);
                    var   imageData = payload[8..];
                    Log.Debug("Recv artwork channel={Channel} timestamp={Timestamp} bytes={Bytes}",
                        typeId - 8, timestamp, imageData.Length);
                    yield return new ArtworkFrame(
                        timestamp, imageData, DetectMimeType(imageData), Channel: typeId - 8);
                }
                else if (typeId == 4 && payload.Length >= 8)
                {
                    long timestamp   = BinaryPrimitives.ReadInt64BigEndian(payload);
                    var  encodedData = payload[8..];
                    Log.Debug("Recv audio-chunk timestamp={Timestamp} bytes={Bytes}", timestamp, encodedData.Length);
                    yield return new AudioChunkFrame(timestamp, encodedData);
                }
            }
        }
    }

    // Deserialises the raw byte stream without any filtering.
    // Binary (non-JSON) frames are skipped — artwork is consumed via ReceiveAllAsync.
    private async IAsyncEnumerable<Message> ReceiveRawAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var data in _transport.ReceiveAsync(cancellationToken))
        {
            if (data.Length == 0 || data[0] != (byte)'{') continue;
            var msg = _serializer.Deserialize(data);
            Log.Debug("Recv {Type} {Message}", _serializer.GetWireType(msg), msg);
            yield return msg;
        }
    }

    private static string DetectMimeType(byte[] data) =>
        data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8
            ? "image/jpeg"
            : data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
                ? "image/png"
                : "application/octet-stream";
}
