// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Serilog;
using Serilog.Events;
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
/// • Handles <c>stream/start</c> (initialises the appropriate codec decoder and
///   creates/replaces the <see cref="PlaybackEngine"/> with the correct format).
/// • Handles <c>stream/clear</c> (flushes the jitter buffer).
/// • Handles <c>server/command</c> (volume / mute / set_static_delay) and echoes
///   the new state back to the server via <c>client/state</c>.
/// • Exposes <see cref="SendStateAsync"/> for the initial state report after hello.
/// • Exposes <see cref="RequestFormatAsync"/> so callers can adapt to changing
///   network or CPU conditions.
/// • Exposes <see cref="UpdateClockOffset"/> so the clock synchroniser can keep
///   the playback engine's timestamp translation in sync.
///
/// Usage
/// ─────
/// 1. Call <see cref="BuildSupport"/> to get the <see cref="PlayerV1Support"/> object
///    for inclusion in <c>client/hello</c>.
/// 2. After the handshake, call <see cref="SendStateAsync"/> once.
/// 3. Feed every <see cref="IncomingFrame"/> from
///    <see cref="ProtocolClient.ReceiveAllAsync"/> into <see cref="ProcessFrameAsync"/>.
/// 4. Call <see cref="UpdateClockOffset"/> whenever the clock synchroniser
///    produces a new measurement.
/// </summary>
public sealed class PlayerClient : IAsyncDisposable
{
    private readonly ProtocolClient                  _protocol;
    private readonly Func<int, int, IWasapiRenderer> _rendererFactory;
    private readonly Clock.ISystemClock              _clock;

    private IAudioDecoder? _decoder;
    private PlaybackEngine? _playbackEngine;
    private bool            _streamActive;
    private TimeSpan        _clockOffset;
    private bool            _clockSynced;
    private int             _rendererLatencyMs;

    private int    _volume;
    private bool   _muted;
    private int    _staticDelayMs = 0;
    private string _playerState   = "synchronized";

    /// <summary>Number of frames currently held in the jitter buffer. Zero when no stream is active.</summary>
    public int BufferedFrameCount => _playbackEngine?.BufferedFrameCount ?? 0;

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

    /// <summary>
    /// Creates a player client that drives the system WASAPI device identified by
    /// <paramref name="deviceId"/> (or the system default when <see langword="null"/>).
    /// </summary>
    /// <param name="protocol">The protocol client to use for communication.</param>
    /// <param name="deviceId">The WASAPI device ID, or <see langword="null"/> for the system default.</param>
    /// <param name="volume">Initial volume level (0–100). Defaults to 100.</param>
    /// <param name="muted">Initial mute state. Defaults to <see langword="false"/>.</param>
    public PlayerClient(ProtocolClient protocol, string? deviceId = null, int volume = 100, bool muted = false)
        : this(protocol, (sampleRate, channels) => new WasapiRenderer(deviceId, sampleRate, channels), clock: null, volume, muted) { }

    /// <summary>Internal constructor for testing — accepts a renderer factory and optional clock seam.</summary>
    internal PlayerClient(
        ProtocolClient                  protocol,
        Func<int, int, IWasapiRenderer> rendererFactory,
        Clock.ISystemClock?             clock  = null,
        int                             volume = 100,
        bool                            muted  = false)
    {
        _protocol        = protocol;
        _rendererFactory = rendererFactory;
        _clock           = clock ?? Clock.SystemClock.Instance;
        _volume          = volume;
        _muted           = muted;
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
            new ClientStateMessage(
                State:  _playerState,
                Player: new ClientPlayerState(
                    Volume:            _volume,
                    Muted:             _muted,
                    StaticDelayMs:     _staticDelayMs,
                    SupportedCommands: ["set_static_delay"])),
            cancellationToken);

    /// <summary>
    /// Sends the initial <c>client/state</c> and <c>stream/request-format</c>
    /// messages that must be issued once after the handshake completes.
    /// </summary>
    /// <param name="preferredFormat">Preferred audio format to request from the server.</param>
    /// <param name="sampleRate">Preferred sample rate derived from the output device's max capability.</param>
    /// <param name="channels">Preferred channel count derived from the output device's max capability.</param>
    /// <param name="bitDepth">Preferred bit depth derived from the output device's max capability.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SendInitialRequestsAsync(
        AudioFormat       preferredFormat   = AudioFormat.Flac,
        int               sampleRate        = 48_000,
        int               channels          = 2,
        int               bitDepth          = 24,
        CancellationToken cancellationToken  = default)
    {
        await SendStateAsync(cancellationToken).ConfigureAwait(false);
        await _protocol.SendAsync(
            new StreamRequestFormatMessage(
                Player: new StreamRequestFormatPlayer(
                    Codec:      preferredFormat.ToCodecString(),
                    Channels:   channels,
                    SampleRate: sampleRate,
                    BitDepth:   bitDepth)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a <c>stream/request-format</c> message to ask the server to
    /// switch to a different encoding (e.g. to adapt to network conditions).
    /// </summary>
    public Task RequestFormatAsync(
        StreamRequestFormatPlayer request,
        CancellationToken         cancellationToken = default)
        => _protocol.SendAsync(new StreamRequestFormatMessage(request), cancellationToken);

    /// <summary>
    /// Suspends local audio output immediately — clears the jitter buffer and
    /// the WASAPI hardware buffer. Call when the user presses Pause.
    /// </summary>
    public void Pause() => _playbackEngine?.Pause();

    /// <summary>
    /// Lifts the pause gate so audio resumes once enough frames have buffered.
    /// Call when the user presses Play.
    /// </summary>
    public void Resume() => _playbackEngine?.Resume();

    /// <summary>
    /// Updates the clock offset used to translate server audio timestamps to
    /// local time. Call this whenever the <see cref="Clock.ClockSynchronizer"/>
    /// produces a new measurement.
    /// </summary>
    public void UpdateClockOffset(TimeSpan offset)
    {
        _clockOffset = offset;
        _clockSynced = true;
        _playbackEngine?.UpdateClockOffset(offset);
    }

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
                await HandleStreamStartAsync(player, cancellationToken).ConfigureAwait(false);
                break;

            case ProtocolFrame { Message: StreamClearMessage }:
                // Spec: discard buffered audio and continue accepting new chunks.
                // _streamActive intentionally stays true — the stream is still live.
                _playbackEngine?.ClearBuffer();
                break;

            case ProtocolFrame { Message: StreamEndMessage }:
                _streamActive = false;
                break;

            case ProtocolFrame { Message: ServerCommandMessage { Player: { } cmd } }:
                await HandleCommandAsync(cmd, cancellationToken).ConfigureAwait(false);
                break;

            case AudioChunkFrame when !_streamActive || _decoder is null:
                Log.Debug("Audio chunk received but no active stream; dropping");
                break;

            case AudioChunkFrame chunk:
                HandleAudioChunk(chunk);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _decoder?.Dispose();
        if (_playbackEngine is not null)
            await _playbackEngine.DisposeAsync().ConfigureAwait(false);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task HandleStreamStartAsync(StreamStartPlayer player, CancellationToken ct)
    {
        // Tear down any previous engine before starting a new one.
        if (_playbackEngine is not null)
        {
            await _playbackEngine.DisposeAsync().ConfigureAwait(false);
            _playbackEngine = null;
        }

        _decoder?.Dispose();

        var format = AudioFormatExtensions.FromCodecString(player.Codec);

        _decoder = AudioDecoderFactory.Create(format, player.SampleRate, player.Channels);

        var renderer       = _rendererFactory(player.SampleRate, player.Channels);
        _rendererLatencyMs = renderer.LatencyMs;
        _playbackEngine    = new PlaybackEngine(renderer);
        _playbackEngine.PlaybackStateChanged += async state =>
        {
            _playerState = state;
            try { await SendStateAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { }
        };
        _playbackEngine.UpdateClockOffset(_clockOffset);
        _playbackEngine.SetVolume(_volume / 100f);
        _playbackEngine.SetUserMuted(_muted);
        _playbackEngine.Start();

        _streamActive = true;

        // Re-send state so the server knows the actual output latency
        // (static_delay_ms includes renderer latency, which is now known).
        await SendStateAsync(ct).ConfigureAwait(false);
    }

    private void HandleAudioChunk(AudioChunkFrame chunk)
    {
        // Subtract both the user-configured static downstream delay (amplifiers, external
        // speakers) and the WASAPI renderer's pipeline latency from the server timestamp.
        // The server timestamp marks when audio should be audible; we need to submit it
        // to the hardware that many microseconds early so it emerges on time.
        long totalLatencyUs     = (_staticDelayMs + _rendererLatencyMs) * 1_000L;
        long effectiveTimestamp = chunk.Timestamp - totalLatencyUs;

        // Drop chunks that have already missed their play deadline.
        // The spec requires late arrivals to be discarded to maintain sync.
        // Guard is skipped until the first clock sync so startup frames are never dropped prematurely.
        if (_clockSynced)
        {
            long serverNowUs = _clock.UtcNowMicroseconds + (long)_clockOffset.TotalMicroseconds;
            if (effectiveTimestamp < serverNowUs)
            {
                Log.Warning(
                    "Dropping late audio chunk: effectiveTimestamp={Ts} serverNow={Now} delta={DeltaMs:F1} ms",
                    effectiveTimestamp, serverNowUs,
                    (serverNowUs - effectiveTimestamp) / 1_000.0);
                return;
            }
        }

        var audioFrame = _decoder!.Decode(chunk.EncodedData);
        _playbackEngine!.Enqueue(effectiveTimestamp, audioFrame);

        if (Log.IsEnabled(LogEventLevel.Debug))
        {
            int bufferedFrames = _playbackEngine.BufferedFrameCount;
            int bufferedBytes  = bufferedFrames * audioFrame.Samples.Length * sizeof(short);
            Log.Debug(
                "Recv Audio chunk: {EncodedBytes} bytes encoded, {BufferedBytes} bytes buffered ({BufferedFrames} frames), {DurationSeconds:F3}s/frame, serverTs={ServerTs} μs effectiveTs={EffectiveTs} μs",
                chunk.EncodedData.Length,
                bufferedBytes,
                bufferedFrames,
                audioFrame.Duration.TotalSeconds,
                chunk.Timestamp,
                effectiveTimestamp);
        }
    }

    private async Task HandleCommandAsync(ServerCommandPlayer cmd, CancellationToken ct)
    {
        switch (cmd.Command)
        {
            case "volume" when cmd.Volume.HasValue:
                _volume = Math.Clamp(cmd.Volume.Value, 0, 100);
                _playbackEngine?.SetVolume(_volume / 100f);
                VolumeChanged?.Invoke(_volume);
                break;

            case "mute" when cmd.Mute.HasValue:
                _muted = cmd.Mute.Value;
                _playbackEngine?.SetUserMuted(_muted);
                MuteChanged?.Invoke(_muted);
                break;

            case "set_static_delay" when cmd.StaticDelayMs.HasValue:
                _staticDelayMs = Math.Clamp(cmd.StaticDelayMs.Value, 0, 5_000);
                // Frames already in the buffer carry the old adjusted timestamp and
                // would produce a burst of incorrect drift readings. Flush them so the
                // engine re-buffers with timestamps adjusted by the new delay value.
                _playbackEngine?.ClearBuffer();
                StaticDelayChanged?.Invoke(_staticDelayMs);
                break;
        }

        // Spec: state updates must be sent whenever any state changes.
        await SendStateAsync(ct).ConfigureAwait(false);
    }
}
