// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

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
/// <param name="Data">The raw image bytes (JPEG or PNG).</param>
/// <param name="MimeType">
/// MIME type detected from magic bytes: <c>image/jpeg</c>, <c>image/png</c>, or
/// <c>application/octet-stream</c> when the format is not recognised.
/// </param>
public sealed record ArtworkFrame(byte[] Data, string MimeType) : IncomingFrame;
