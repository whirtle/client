// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Codec;
using Whirtle.Client.Playback;
using Whirtle.Client.Protocol;

namespace Whirtle.Client.Role;

/// <summary>
/// Implements the Sendspin player@v1 role.
///
/// Responsibilities
/// ────────────────
/// • Decodes incoming binary audio chunks and enqueues them in <see cref="PlaybackEngine"/>.
/// • Handles <c>stream/start</c> (initialises the appropriate codec decoder).
/// • Handles <c>stream/clear</c> (flushes the jitter buffer).
/// • Handles <c>server/command</c> (volume / mute / set_static_delay) and echoes
///   the new state back to the server via <c>client/state</c>.
/// • Exposes <see cref="SendStateAsync"/> for the initial state report after hello.
/// • Exposes <see cref="RequestFormatAsync"/> so callers can adapt to changing
///   network or CPU conditions.
///
/// Usage
/// ─────
/// 1. Call <see cref="BuildSupport"/> to get the <see cref="PlayerV1Support"/> object
///    for inclusion in <c>client/hello</c>.
/// 2. After the handshake, call <see cref="SendStateAsync"/> once.
/// 3. Feed every <see cref="IncomingFrame"/> from
///    <see cref="ProtocolClient.ReceiveAllAsync"/> into <see cref="ProcessFrameAsync"/>.
/// </summary>
public sealed class PlayerClient : IDisposable
{
    private readonly ProtocolClient _protocol;
    private readonly PlaybackEngine _playbackEngine;

    private IAudioDecoder? _decoder;
    private bool           _streamActive;

    private int  _volume        = 100;
    private bool _muted         = false;
    private int  _staticDelayMs = 0;

    /// <summary>Current volume level (0–100).</summary>
    public int  Volume        => _volume;

    /// <summary>Current mute state.</summary>
    public bool Muted         => _muted;

    /// <summary>
    /// Static output delay in milliseconds (0–5000).
    /// Compensates for additional delay beyond the audio port (external speakers, amplifiers).
    /// Must be persisted locally and restored on reconnection.
    /// </summary>
    public int  StaticDelayMs => _staticDelayMs;

    /// <summary>Raised when the server sends a <c>volume</c> command.</summary>
    public event Action<int>?  VolumeChanged;

    /// <summary>Raised when the server sends a <c>mute</c> command.</summary>
    public event Action<bool>? MuteChanged;

    /// <summary>Raised when the server sends a <c>set_static_delay</c> command.</summary>
    public event Action<int>?  StaticDelayChanged;

    public PlayerClient(ProtocolClient protocol, PlaybackEngine playbackEngine)
    {
        _protocol       = protocol;
        _playbackEngine = playbackEngine;
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the <see cref="PlayerV1Support"/> object to include in
    /// <c>client/hello</c>.
    /// </summary>
    public static PlayerV1Support BuildSupport(
        PlayerV1SupportFormat[] supportedFormats,
        int                     bufferCapacity,
        string[]                supportedCommands)
        => new(supportedFormats, bufferCapacity, supportedCommands);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends <c>client/state</c> with the current player state.
    /// Must be sent after connection and after any state change.
    /// </summary>
    public Task SendStateAsync(CancellationToken cancellationToken = default)
        => _protocol.SendAsync(
            new ClientStateMessage(Player: new ClientPlayerState(
                Volume:            _volume,
                Muted:             _muted,
                StaticDelayMs:     _staticDelayMs,
                SupportedCommands: ["set_static_delay"])),
            cancellationToken);

    /// <summary>
    /// Sends a <c>stream/request-format</c> message to ask the server to
    /// switch to a different encoding (e.g. to adapt to network conditions).
    /// </summary>
    public Task RequestFormatAsync(
        StreamRequestFormatPlayer request,
        CancellationToken         cancellationToken = default)
        => _protocol.SendAsync(new StreamRequestFormatMessage(request), cancellationToken);

    /// <summary>
    /// Processes a single <see cref="IncomingFrame"/> from the server.
    /// Call for every frame yielded by <see cref="ProtocolClient.ReceiveAllAsync"/>.
    /// </summary>
    public async Task ProcessFrameAsync(
        IncomingFrame     frame,
        CancellationToken cancellationToken = default)
    {
        switch (frame)
        {
            case ProtocolFrame { Message: StreamStartMessage { Player: { } player } }:
                HandleStreamStart(player);
                break;

            case ProtocolFrame { Message: StreamClearMessage }:
                _streamActive = false;
                _playbackEngine.ClearBuffer();
                break;

            case ProtocolFrame { Message: ServerCommandMessage { Player: { } cmd } }:
                await HandleCommandAsync(cmd, cancellationToken).ConfigureAwait(false);
                break;

            case AudioChunkFrame chunk when _streamActive && _decoder is not null:
                HandleAudioChunk(chunk);
                break;
        }
    }

    public void Dispose() => _decoder?.Dispose();

    // ── Private ───────────────────────────────────────────────────────────────

    private void HandleStreamStart(StreamStartPlayer player)
    {
        _decoder?.Dispose();

        var format = player.Codec.ToLowerInvariant() switch
        {
            "opus" => AudioFormat.Opus,
            "flac" => AudioFormat.Flac,
            _      => AudioFormat.Pcm,
        };

        _decoder      = AudioDecoderFactory.Create(format, player.SampleRate, player.Channels);
        _streamActive = true;
    }

    private void HandleAudioChunk(AudioChunkFrame chunk)
    {
        // Subtract static delay (microseconds) so that audio exits the hardware
        // port at the server timestamp rather than at the port + downstream delay.
        long effectiveTimestamp = chunk.Timestamp - (_staticDelayMs * 1_000L);

        var audioFrame = _decoder!.Decode(chunk.EncodedData);
        _playbackEngine.Enqueue(effectiveTimestamp, audioFrame);
    }

    private async Task HandleCommandAsync(ServerCommandPlayer cmd, CancellationToken ct)
    {
        switch (cmd.Command)
        {
            case "volume" when cmd.Volume.HasValue:
                _volume = Math.Clamp(cmd.Volume.Value, 0, 100);
                VolumeChanged?.Invoke(_volume);
                break;

            case "mute" when cmd.Mute.HasValue:
                _muted = cmd.Mute.Value;
                MuteChanged?.Invoke(_muted);
                break;

            case "set_static_delay" when cmd.StaticDelayMs.HasValue:
                _staticDelayMs = Math.Clamp(cmd.StaticDelayMs.Value, 0, 5_000);
                StaticDelayChanged?.Invoke(_staticDelayMs);
                break;
        }

        // Spec: state updates must be sent whenever any state changes.
        await SendStateAsync(ct).ConfigureAwait(false);
    }
}
