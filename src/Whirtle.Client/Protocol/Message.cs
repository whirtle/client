// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace Whirtle.Client.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage),          "hello")]
[JsonDerivedType(typeof(WelcomeMessage),        "welcome")]
[JsonDerivedType(typeof(PingMessage),           "ping")]
[JsonDerivedType(typeof(PongMessage),           "pong")]
[JsonDerivedType(typeof(ErrorMessage),          "error")]
[JsonDerivedType(typeof(GoodbyeMessage),        "goodbye")]
[JsonDerivedType(typeof(SyncRequestMessage),    "sync_request")]
[JsonDerivedType(typeof(SyncReplyMessage),      "sync_reply")]
[JsonDerivedType(typeof(ClientCommandMessage),  "client/command")]
[JsonDerivedType(typeof(NowPlayingMessage),     "now_playing")]
public abstract record Message;

/// <summary>Sent by the client immediately after the WebSocket connection opens.</summary>
public sealed record HelloMessage(string Version) : Message;

/// <summary>Sent by the server to accept the handshake.</summary>
public sealed record WelcomeMessage(string SessionId, string ServerVersion) : Message;

/// <summary>Keepalive probe; the receiver should reply with <see cref="PongMessage"/>.</summary>
public sealed record PingMessage : Message;

/// <summary>Keepalive reply to a <see cref="PingMessage"/>.</summary>
public sealed record PongMessage : Message;

/// <summary>Sent by either side to report a protocol-level error.</summary>
public sealed record ErrorMessage(string Code, string Description) : Message;

/// <summary>Sent by either side to begin a graceful shutdown.</summary>
public sealed record GoodbyeMessage(string Reason) : Message;

/// <summary>
/// Sent by the client to initiate one clock-sync round trip.
/// <see cref="ClientSentAt"/> is the client's UTC ticks at send time.
/// </summary>
public sealed record SyncRequestMessage(long ClientSentAt) : Message;

/// <summary>
/// Sent by the server in reply to <see cref="SyncRequestMessage"/>.
/// Echoes the original <see cref="ClientSentAt"/> and adds the server's
/// UTC ticks at the moment it processed the request.
/// </summary>
public sealed record SyncReplyMessage(long ClientSentAt, long ServerReceivedAt) : Message;

/// <summary>
/// Sent by the client to control playback for the whole group (Controller Role).
/// </summary>
/// <param name="Command">Command name: <c>play</c>, <c>pause</c>, <c>skip</c>, or <c>volume</c>.</param>
/// <param name="Value">Optional numeric parameter — for <c>volume</c>: 0.0 (silent) to 1.0 (full).</param>
public sealed record ClientCommandMessage(string Command, double? Value = null) : Message;

/// <summary>Sent by the server to push now-playing track metadata to all clients (Metadata Role).</summary>
public sealed record NowPlayingMessage(
    string? Title,
    string? Artist,
    string? Album,
    double? DurationSeconds,
    double? PositionSeconds) : Message;
