// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.Protocol;

/// <summary>Base type for all Sendspin protocol messages.</summary>
public abstract record Message;

// ─── Client → Server ──────────────────────────────────────────────────────────

/// <summary>
/// Sent by the client immediately after the WebSocket connection opens to
/// identify itself and declare its capabilities.
/// Wire type: <c>client/hello</c>
/// </summary>
public sealed record ClientHelloMessage(
    string   ClientId,
    string   Name,
    int      Version,
    string[] SupportedRoles) : Message;

/// <summary>
/// Sent periodically by the client for clock synchronisation.
/// <see cref="ClientTransmitted"/> is the client's Unix timestamp in microseconds.
/// Wire type: <c>client/time</c>
/// </summary>
public sealed record ClientTimeMessage(long ClientTransmitted) : Message;

/// <summary>
/// Sent by the client to report its operational state to the server.
/// Wire type: <c>client/state</c>
/// </summary>
/// <param name="State">
/// <c>"synchronized"</c> | <c>"error"</c> | <c>"external_source"</c>
/// </param>
public sealed record ClientStateMessage(string? State = null) : Message;

/// <summary>
/// Sent by the client to issue playback commands (Controller Role).
/// Wire type: <c>client/command</c>
/// </summary>
public sealed record ClientCommandMessage(
    ClientControllerCommand? Controller = null) : Message;

/// <summary>A controller sub-command embedded in <see cref="ClientCommandMessage"/>.</summary>
/// <param name="Command">
/// Command name: <c>play</c>, <c>pause</c>, <c>stop</c>, <c>next</c>,
/// <c>previous</c>, <c>volume</c>, <c>mute</c>, etc.
/// </param>
/// <param name="Volume">0–100, required when <see cref="Command"/> is <c>volume</c>.</param>
/// <param name="Mute"><see langword="true"/> to mute; required when <see cref="Command"/> is <c>mute</c>.</param>
public sealed record ClientControllerCommand(
    string Command,
    int?   Volume = null,
    bool?  Mute   = null);

/// <summary>
/// Sent by the client to begin a graceful shutdown or to yield to another server.
/// Wire type: <c>client/goodbye</c>
/// </summary>
/// <param name="Reason"><c>"shutdown"</c> | <c>"another_server"</c></param>
public sealed record ClientGoodbyeMessage(string Reason) : Message;

// ─── Server → Client ──────────────────────────────────────────────────────────

/// <summary>
/// Sent by the server to acknowledge the handshake and describe the session.
/// Wire type: <c>server/hello</c>
/// </summary>
public sealed record ServerHelloMessage(
    string   ServerId,
    string   Name,
    int      Version,
    string[] ActiveRoles,
    string   ConnectionReason) : Message;

/// <summary>
/// Sent by the server as the clock-sync reply.
/// Echoes <see cref="ClientTransmitted"/> and adds the server's Unix timestamp in microseconds.
/// Wire type: <c>server/time</c>
/// </summary>
public sealed record ServerTimeMessage(
    long ClientTransmitted,
    long ServerReceived) : Message;

/// <summary>
/// Sent by the server with one or more role-specific state updates.
/// Wire type: <c>server/state</c>
/// </summary>
public sealed record ServerStateMessage(
    ServerMetadataState?   Metadata   = null,
    ServerControllerState? Controller = null) : Message;

/// <summary>Metadata portion of a <see cref="ServerStateMessage"/>.</summary>
public sealed record ServerMetadataState(
    long?   Timestamp   = null,
    string? Title       = null,
    string? Artist      = null,
    string? AlbumArtist = null,
    string? Album       = null,
    string? ArtworkUrl  = null,
    int?    Year        = null,
    int?    Track       = null,
    double? Progress    = null,
    string? Repeat      = null,
    bool?   Shuffle     = null);

/// <summary>Controller portion of a <see cref="ServerStateMessage"/>.</summary>
public sealed record ServerControllerState(
    string[] SupportedCommands,
    int      Volume,
    bool     Muted);

/// <summary>
/// Sent by the server when group playback state changes.
/// Wire type: <c>group/update</c>
/// </summary>
public sealed record GroupUpdateMessage(
    string PlaybackState,
    string GroupId) : Message;

/// <summary>
/// Returned by <see cref="MessageSerializer"/> when the wire <c>type</c> is not
/// recognised, allowing callers to ignore unknown messages gracefully rather than
/// throwing.
/// </summary>
public sealed record UnknownMessage(string Type) : Message;
