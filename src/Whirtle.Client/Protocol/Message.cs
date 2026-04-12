// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

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
    string          ClientId,
    string          Name,
    int             Version,
    string[]        SupportedRoles,
    [property: JsonPropertyName("player@v1_support")]
    PlayerV1Support? PlayerV1Support = null) : Message;

/// <summary>One supported audio format advertised in <see cref="PlayerV1Support"/>.</summary>
public sealed record PlayerV1SupportFormat(
    string Codec,
    int    Channels,
    int    SampleRate,
    int    BitDepth);

/// <summary>
/// Player-role capability object sent inside <c>client/hello</c>.
/// Declares supported audio formats, buffer capacity, and optional commands.
/// Wire name: <c>player@v1_support</c>
/// </summary>
public sealed record PlayerV1Support(
    PlayerV1SupportFormat[] SupportedFormats,
    int                     BufferCapacity,
    string[]                SupportedCommands);

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
public sealed record ClientStateMessage(
    string?           State  = null,
    ClientPlayerState? Player = null) : Message;

/// <summary>
/// Player-role state sent inside <c>client/state</c>.
/// Must be sent whenever any player state changes.
/// </summary>
/// <param name="StaticDelayMs">
/// Static output delay in ms (0–5000). Always required; default is 0.
/// Compensates for external speakers, amplifiers, etc.
/// </param>
public sealed record ClientPlayerState(
    int?      Volume            = null,
    bool?     Muted             = null,
    int       StaticDelayMs     = 0,
    string[]? SupportedCommands = null);

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

// ─── Player Role ──────────────────────────────────────────────────────────────

/// <summary>
/// Sent by the player client to request a different audio encoding.
/// Wire type: <c>stream/request-format</c>
/// </summary>
public sealed record StreamRequestFormatMessage(
    StreamRequestFormatPlayer? Player = null) : Message;

/// <summary>Requested format parameters inside <see cref="StreamRequestFormatMessage"/>.</summary>
public sealed record StreamRequestFormatPlayer(
    string? Codec      = null,
    int?    Channels   = null,
    int?    SampleRate = null,
    int?    BitDepth   = null);

/// <summary>
/// Sent by the server to instruct the player to change volume, mute state, or static delay.
/// Wire type: <c>server/command</c>
/// </summary>
public sealed record ServerCommandMessage(
    ServerCommandPlayer? Player = null) : Message;

/// <summary>Command payload inside <see cref="ServerCommandMessage"/>.</summary>
/// <param name="Command"><c>volume</c> | <c>mute</c> | <c>set_static_delay</c></param>
public sealed record ServerCommandPlayer(
    string Command,
    int?   Volume        = null,
    bool?  Mute          = null,
    int?   StaticDelayMs = null);

/// <summary>
/// Sent by the server to describe the active audio stream format.
/// Wire type: <c>stream/start</c>
/// </summary>
public sealed record StreamStartMessage(
    StreamStartPlayer? Player = null) : Message;

/// <summary>Stream format details inside <see cref="StreamStartMessage"/>.</summary>
/// <param name="CodecHeader">Base64-encoded codec header (e.g. required for FLAC).</param>
public sealed record StreamStartPlayer(
    string  Codec,
    int     SampleRate,
    int     Channels,
    int     BitDepth,
    string? CodecHeader = null);

/// <summary>
/// Sent by the server to instruct the player to discard all buffered audio.
/// Wire type: <c>stream/clear</c>
/// </summary>
public sealed record StreamClearMessage : Message;

/// <summary>
/// Returned by <see cref="MessageSerializer"/> when the wire <c>type</c> is not
/// recognised, allowing callers to ignore unknown messages gracefully rather than
/// throwing.
/// </summary>
public sealed record UnknownMessage(string Type) : Message;
