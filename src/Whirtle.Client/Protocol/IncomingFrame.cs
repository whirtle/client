// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Protocol;

/// <summary>
/// A frame received from the server, yielded by
/// <see cref="ProtocolClient.ReceiveAllAsync"/>.
/// Either a decoded JSON protocol message or raw binary artwork data.
/// </summary>
public abstract record IncomingFrame;

/// <summary>A decoded JSON protocol message.</summary>
/// <param name="Message">The deserialized protocol message.</param>
public sealed record ProtocolFrame(Message Message) : IncomingFrame;

/// <summary>
/// Raw binary image data sent by the server as a binary WebSocket frame (Artwork Role).
/// </summary>
/// <param name="Timestamp">
/// Server clock time in microseconds when the image should be displayed.
/// </param>
/// <param name="Data">
/// The raw image bytes, with the type-byte prefix and timestamp stripped.
/// Empty when the server is clearing the channel.
/// </param>
/// <param name="MimeType">
/// MIME type detected from magic bytes: <c>image/jpeg</c>, <c>image/png</c>, or
/// <c>application/octet-stream</c> when the format is not recognised.
/// Empty string when <paramref name="Data"/> is empty (clear message).
/// </param>
/// <param name="Channel">Artwork channel index (0–3).</param>
#pragma warning disable CA1819 // raw binary protocol data — callers need direct array access
public sealed record ArtworkFrame(long Timestamp, byte[] Data, string MimeType, int Channel = 0) : IncomingFrame;
#pragma warning restore CA1819

/// <summary>
/// A binary audio chunk received from the server (Player Role, message type 4).
/// </summary>
/// <param name="Timestamp">
/// Server clock time in microseconds when the first sample should be output.
/// </param>
/// <param name="EncodedData">Encoded audio payload (codec determined by the active stream).</param>
#pragma warning disable CA1819 // raw binary protocol data — callers need direct array access
public sealed record AudioChunkFrame(long Timestamp, byte[] EncodedData) : IncomingFrame;
#pragma warning restore CA1819
