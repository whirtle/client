// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Serilog;
using Whirtle.Client.Clock;
using Whirtle.Client.Transport;

namespace Whirtle.Client.Protocol;

public sealed class ProtocolClient : IAsyncDisposable
{
    private readonly ITransport        _transport;
    private readonly MessageSerializer _serializer = new();
    private string                     _serverTag  = "";

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
        string             clientId,
        string             clientName,
        string[]?          supportedRoles    = null,
        ArtworkV1Support?  artworkSupport    = null,
        PlayerV1Support?   playerSupport     = null,
        CancellationToken  cancellationToken = default)
    {
        supportedRoles ??= ["metadata@v1", "controller@v1"];

        await SendAsync(
            new ClientHelloMessage(
                clientId, clientName, Version: 1, supportedRoles,
                PlayerV1Support:  playerSupport,
                ArtworkV1Support: artworkSupport),
            cancellationToken);

        await foreach (var msg in ReceiveRawAsync(cancellationToken))
        {
            return msg switch
            {
                ServerHelloMessage hello => SetServerTag(hello),
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
        var data = _serializer.Serialize(message);
        Log.Debug("{Tag:l}> {Type:l} {Json:l}", _serverTag, _serializer.GetWireType(message), ExtractPayloadJson(data));
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
                Message msg;
                try
                {
                    msg = _serializer.Deserialize(data);
                }
                catch (JsonException ex)
                {
                    Log.Warning(ex, "{Tag:l}Skipping malformed message", _serverTag);
                    continue;
                }
                if (msg is ServerTimeMessage)
                    Log.Debug("{Tag:l}< {Type:l} {Json:l} client_now={ClientNow:F3} ms",
                        _serverTag, _serializer.GetWireType(msg), ExtractPayloadJson(data),
                        SystemClock.Instance.UtcNowMicroseconds / 1_000.0);
                else
                    Log.Debug("{Tag:l}< {Type:l} {Json:l}", _serverTag, _serializer.GetWireType(msg), ExtractPayloadJson(data));
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
                    if (imageData.Length > MaxArtworkBytes)
                    {
                        Log.Warning("{Tag:l}Dropping oversized artwork frame: channel={Channel} bytes={Bytes}",
                            _serverTag, typeId - 8, imageData.Length);
                    }
                    else
                    {
                        Log.Verbose("{Tag:l}Recv artwork channel={Channel} timestamp={Timestamp:F3} ms bytes={Bytes}",
                            _serverTag, typeId - 8, timestamp / 1_000.0, imageData.Length);
                        yield return new ArtworkFrame(
                            timestamp, imageData, DetectMimeType(imageData), Channel: typeId - 8);
                    }
                }
                else if (typeId == 4 && payload.Length >= 8)
                {
                    long timestamp   = BinaryPrimitives.ReadInt64BigEndian(payload);
                    var  encodedData = payload[8..];
                    Log.Verbose("{Tag:l}Recv audio-chunk timestamp={Timestamp:F3} ms bytes={Bytes}", _serverTag, timestamp / 1_000.0, encodedData.Length);
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
            Log.Debug("{Tag:l}< {Type:l} {Json:l}", _serverTag, _serializer.GetWireType(msg), ExtractPayloadJson(data));
            yield return msg;
        }
    }

    // Hard cap applied before yielding an ArtworkFrame to prevent a malicious or
    // misbehaving server from exhausting the client's address space.
    internal const int MaxArtworkBytes = 10 * 1024 * 1024;

    private ServerHelloMessage SetServerTag(ServerHelloMessage hello)
    {
        var suffix = hello.ServerId.Length >= 4
            ? hello.ServerId[^4..]
            : hello.ServerId;
        _serverTag = $"{hello.Name}_{suffix}: ";
        return hello;
    }

    private static string ExtractPayloadJson(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        return doc.RootElement.TryGetProperty("payload", out var payload)
            ? payload.GetRawText()
            : System.Text.Encoding.UTF8.GetString(data);
    }

    private static string DetectMimeType(byte[] data) =>
        data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8
            ? "image/jpeg"
            : data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
                ? "image/png"
                : data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D
                    ? "image/bmp"
                    : "application/octet-stream";
}
