// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Runtime.Versioning;
using Serilog;
using Serilog.Events;
using Whirtle.Client.Codec;
using Whirtle.Client.Playback;
using Whirtle.Client.Protocol;

namespace Whirtle.Client.Role;

/// <summary>Event data for <see cref="PlayerClient.VolumeChanged"/>.</summary>
public sealed class VolumeChangedEventArgs(int volume) : EventArgs
{
    public int Volume { get; } = volume;
}

/// <summary>Event data for <see cref="PlayerClient.MuteChanged"/>.</summary>
public sealed class MuteChangedEventArgs(bool muted) : EventArgs
{
    public bool Muted { get; } = muted;
}

/// <summary>Event data for <see cref="PlayerClient.StaticDelayChanged"/>.</summary>
public sealed class StaticDelayChangedEventArgs(int delayMs) : EventArgs
{
    public int DelayMs { get; } = delayMs;
}

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
    private double          _driftUsPerS;
    private double          _offsetStdDevUs    = double.PositiveInfinity;
    private double          _driftStdDevUsPerS = double.PositiveInfinity;
    private int             _rendererLatencyMs;

    private int    _volume;
    private bool   _muted;
    private int    _staticDelayMs;
    private string _playerState   = "synchronized";

    private (string PlayerState, int Volume, bool Muted, int StaticDelayMs)? _lastSentState;

    // ── Codec statistics ──────────────────────────────────────────────────────
    private long _totalChunksReceived;
    private readonly Dictionary<AudioFormat, (long Chunks, long Encoded, long Decoded)> _codecStats = new();

    /// <summary>Number of frames currently held in the jitter buffer. Zero when no stream is active.</summary>
    public int BufferedFrameCount => _playbackEngine?.BufferedFrameCount ?? 0;

    /// <summary>Total audio duration currently held in the jitter buffer.</summary>
    public TimeSpan BufferedAudioDuration => _playbackEngine?.BufferedAudioDuration ?? TimeSpan.Zero;

    /// <summary>Total number of audio chunks received since the current stream started.</summary>
    public long TotalChunksReceived => _totalChunksReceived;

    /// <summary>Number of buffer underruns (jitter buffer empty during playback) since the current stream started.</summary>
    public int BufferUnderrunCount => _playbackEngine?.BufferUnderrunCount ?? 0;

    /// <summary>Number of times the minimum-buffer floor held back late-frame drops at startup, forcing rate correction.</summary>
    public int MinBufferFloorHitCount => _playbackEngine?.MinBufferFloorHitCount ?? 0;

    /// <summary>Current ahead-buffer target in milliseconds.</summary>
    public int AheadTargetMs => _playbackEngine?.AheadTargetMs ?? 0;

    /// <summary>Rate ratio most recently applied by the resampler (1.0 = on-schedule).</summary>
    public double LastRateRatio => _playbackEngine?.LastRateRatio ?? 1.0;

    /// <summary>Number of times the resampler ratio saturated against the ±200 ppm clamp.</summary>
    public int RateRatioClampHitCount => _playbackEngine?.RateRatioClampHitCount ?? 0;

    /// <summary>Current playback engine state. <see cref="PlaybackState.Buffering"/> when no engine is active.</summary>
    public PlaybackState EngineState => _playbackEngine?.State ?? PlaybackState.Buffering;

    /// <summary>
    /// Returns a snapshot of per-codec statistics accumulated since the current stream started.
    /// </summary>
    public IReadOnlyList<CodecStats> GetCodecStats()
    {
        lock (_codecStats)
        {
            return _codecStats
                .Select(kv => new CodecStats(kv.Key, kv.Value.Chunks, kv.Value.Encoded, kv.Value.Decoded))
                .ToList();
        }
    }

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
    public event EventHandler<VolumeChangedEventArgs>?      VolumeChanged;

    /// <summary>Raised when the server sends a <c>mute</c> command.</summary>
    public event EventHandler<MuteChangedEventArgs>?        MuteChanged;

    /// <summary>Raised when the server sends a <c>set_static_delay</c> command.</summary>
    public event EventHandler<StaticDelayChangedEventArgs>? StaticDelayChanged;

    /// <summary>
    /// Creates a player client that drives the system WASAPI device identified by
    /// <paramref name="deviceId"/> (or the system default when <see langword="null"/>).
    /// </summary>
    /// <param name="protocol">The protocol client to use for communication.</param>
    /// <param name="deviceId">The WASAPI device ID, or <see langword="null"/> for the system default.</param>
    /// <param name="volume">Initial volume level (0–100). Defaults to 100.</param>
    /// <param name="muted">Initial mute state. Defaults to <see langword="false"/>.</param>
    [SupportedOSPlatform("windows")]
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
    /// Sends <c>client/state</c> with the current player state, but only if
    /// the state differs from the last value sent (or has never been sent).
    /// </summary>
    public Task SendStateAsync(CancellationToken cancellationToken = default)
    {
        var current = (_playerState, _volume, _muted, _staticDelayMs);
        if (_lastSentState == current)
            return Task.CompletedTask;
        _lastSentState = current;
        return _protocol.SendAsync(
            new ClientStateMessage(
                State:  _playerState,
                Player: new ClientPlayerState(
                    Volume:            _volume,
                    Muted:             _muted,
                    StaticDelayMs:     _staticDelayMs,
                    SupportedCommands: ["set_static_delay"])),
            cancellationToken);
    }

    /// <summary>
    /// Sends <c>stream/request-format</c> once after the handshake completes.
    /// <c>client/state</c> is intentionally deferred until the clock is ready;
    /// call <see cref="SendStateAsync"/> once <see cref="UpdateClockOffset"/> has been invoked.
    /// </summary>
    /// <param name="preferredFormat">Preferred audio format to request from the server.</param>
    /// <param name="sampleRate">Preferred sample rate derived from the output device's max capability.</param>
    /// <param name="channels">Preferred channel count derived from the output device's max capability.</param>
    /// <param name="bitDepth">Preferred bit depth derived from the output device's max capability.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SendInitialRequestsAsync(
        AudioFormat       preferredFormat   = AudioFormat.Flac,
        int               sampleRate        = 48_000,
        int               channels          = 2,
        int               bitDepth          = 24,
        CancellationToken cancellationToken  = default)
        => _protocol.SendAsync(
            new StreamRequestFormatMessage(
                Player: new StreamRequestFormatPlayer(
                    Codec:      preferredFormat.ToCodecString(),
                    Channels:   channels,
                    SampleRate: sampleRate,
                    BitDepth:   bitDepth)),
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
    /// produces a new measurement. Equivalent to <see cref="UpdateClockState"/>
    /// with no drift information.
    /// </summary>
    public void UpdateClockOffset(TimeSpan offset)
    {
        _clockOffset = offset;
        _clockSynced = true;
        _playbackEngine?.UpdateClockOffset(offset);
    }

    /// <summary>
    /// Updates the full Kalman-filter snapshot from <see cref="Clock.ClockSynchronizer"/>.
    /// Call this in preference to <see cref="UpdateClockOffset"/> so the playback engine
    /// can drive the resampler from the drift estimate and gate on filter health.
    /// </summary>
    public void UpdateClockState(
        TimeSpan offset,
        double   driftUsPerS,
        double   offsetStdDevUs,
        double   driftStdDevUsPerS)
    {
        _clockOffset       = offset;
        _clockSynced       = true;
        _driftUsPerS       = driftUsPerS;
        _offsetStdDevUs    = offsetStdDevUs;
        _driftStdDevUsPerS = driftStdDevUsPerS;
        _playbackEngine?.UpdateClockState(offset, driftUsPerS, offsetStdDevUs, driftStdDevUsPerS);
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

        // Reset per-stream counters so statistics reflect only the current stream.
        lock (_codecStats) { _codecStats.Clear(); }
        _totalChunksReceived = 0;

        var format = AudioFormatExtensions.FromCodecString(player.Codec);

        _decoder = AudioDecoderFactory.Create(format, player.SampleRate, player.Channels);

        var renderer       = _rendererFactory(player.SampleRate, player.Channels);
        _rendererLatencyMs = renderer.LatencyMs;
        _playbackEngine    = new PlaybackEngine(renderer);
        _playbackEngine.PlaybackStateChanged += async (_, e) =>
        {
            _playerState = e.State;
            try { await SendStateAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { }
        };
        if (_clockSynced)
            _playbackEngine.UpdateClockState(_clockOffset, _driftUsPerS, _offsetStdDevUs, _driftStdDevUsPerS);
        _playbackEngine.SetVolume(_volume / 100f);
        _playbackEngine.SetUserMuted(_muted);
        _playbackEngine.Start();

        _streamActive = true;

        // Re-send state so the server knows the actual output latency
        // (static_delay_ms includes renderer latency, which is now known).
        // Skip if the clock isn't ready yet — the convergence callback will send state instead.
        if (_clockSynced)
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
                    "Dropping late audio chunk: effectiveTimestamp={Ts:F3} ms serverNow={Now:F3} ms delta={DeltaMs:F1} ms",
                    effectiveTimestamp / 1_000.0, serverNowUs / 1_000.0,
                    (serverNowUs - effectiveTimestamp) / 1_000.0);
                return;
            }
        }

        var audioFrame = _decoder!.Decode(chunk.EncodedData);
        _playbackEngine!.Enqueue(effectiveTimestamp, audioFrame);

        // Accumulate codec statistics.
        _totalChunksReceived++;
        long encodedBytes = chunk.EncodedData.Length;
        long decodedBytes = (long)audioFrame.Samples.Length * sizeof(short);
        lock (_codecStats)
        {
            _codecStats.TryGetValue(_decoder.Format, out var prev);
            _codecStats[_decoder.Format] = (
                prev.Chunks  + 1,
                prev.Encoded + encodedBytes,
                prev.Decoded + decodedBytes);
        }

        if (Log.IsEnabled(LogEventLevel.Debug))
        {
            int bufferedFrames = _playbackEngine.BufferedFrameCount;
            double? msUntilPlayback = _clockSynced
                ? (effectiveTimestamp - (_clock.UtcNowMicroseconds + (long)_clockOffset.TotalMicroseconds)) / 1_000.0
                : null;
            Log.Verbose(
                "< Audio chunk: {EncodedBytes} bytes encoded, {BufferedBytes} bytes buffered ({BufferedFrames} frames), {DurationSeconds:F3}s/frame, playsIn={PlaysInMs} ms, serverTs={ServerTs:F3} ms effectiveTs={EffectiveTs:F3} ms",
                chunk.EncodedData.Length,
                decodedBytes,
                bufferedFrames,
                audioFrame.Duration.TotalSeconds,
                msUntilPlayback.HasValue ? $"{msUntilPlayback.Value:F1}" : "?",
                chunk.Timestamp / 1_000.0,
                effectiveTimestamp / 1_000.0);
        }
    }

    private async Task HandleCommandAsync(ServerCommandPlayer cmd, CancellationToken ct)
    {
        bool notifyServer = false;

        switch (cmd.Command)
        {
            case "volume" when cmd.Volume.HasValue:
                _volume = Math.Clamp(cmd.Volume.Value, 0, 100);
                _playbackEngine?.SetVolume(_volume / 100f);
                VolumeChanged?.Invoke(this, new VolumeChangedEventArgs(_volume));
                notifyServer = true;
                break;

            case "mute" when cmd.Mute.HasValue:
                _muted = cmd.Mute.Value;
                _playbackEngine?.SetUserMuted(_muted);
                MuteChanged?.Invoke(this, new MuteChangedEventArgs(_muted));
                notifyServer = true;
                break;

            case "set_static_delay" when cmd.StaticDelayMs.HasValue:
                _staticDelayMs = Math.Clamp(cmd.StaticDelayMs.Value, -500, 5_000);
                // Frames already in the buffer carry the old adjusted timestamp and
                // would produce a burst of incorrect drift readings. Flush them so the
                // engine re-buffers with timestamps adjusted by the new delay value.
                _playbackEngine?.ClearBuffer();
                StaticDelayChanged?.Invoke(this, new StaticDelayChangedEventArgs(_staticDelayMs));
                notifyServer = true;
                break;
        }

        if (notifyServer)
            await SendStateAsync(ct).ConfigureAwait(false);
    }
}
