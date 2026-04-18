// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Serilog;
using Serilog.Events;
using Whirtle.Client.Codec;

namespace Whirtle.Client.Playback;

/// <summary>
/// Buffered playback engine.
///
/// Architecture
/// ────────────
///  Incoming AudioFrames (decoded by the codec layer) are enqueued into
///  a <see cref="JitterBuffer"/> keyed by the server-assigned timestamp.
///  A dedicated render loop dequeues frames in order, applies
///  <see cref="SampleInterpolator"/> to compensate for clock drift derived
///  from <see cref="ClockSyncResult"/>, and feeds them to an
///  <see cref="IWasapiRenderer"/>.
///
/// State machine
/// ─────────────
///  Buffering     → Synchronized  : buffer reaches MinBufferFrames
///  Synchronized  → Error         : underrun or drift > MaxDriftMs
///  Error         → Buffering     : buffer recovers to MinBufferFrames;
///                                   <see cref="PlaybackStateChanged"/> fires with
///                                   <c>"error"</c> or <c>"synchronized"</c> so the
///                                   caller can send a complete <c>client/state</c> message
/// </summary>
public sealed class PlaybackEngine : IAsyncDisposable
{
    // Tuning constants
    private const int    MinBufferFrames  = 4;   // frames required before playback starts/recovers
    private const double MaxDriftMs       = 200; // drift threshold before entering Error state; must exceed renderer latency (default 100 ms)

    // Ahead-buffer tuning: keeps only a small window of samples ahead of where volume
    // is applied (WasapiRenderer.Write), so volume changes take effect quickly.
    // Under CPU pressure the window expands automatically to avoid underruns.
    internal const int TargetAheadMs      = 50;  // nominal ms of samples ahead of volume application
    private  const int MaxAheadMs         = 200; // ceiling when falling behind
    internal const int LowWaterMs         = 10;  // buffer level (ms) that signals we're falling behind
    private  const int BehindThreshold    = 5;   // consecutive low-water events before doubling target
    private  const int RecoveryStepMs     = 10;  // ms stepped down per recovery interval
    internal const int RecoveryFrameCount = 50;  // healthy-buffer frames between recovery steps

    private readonly JitterBuffer       _buffer;
    private readonly IWasapiRenderer    _renderer;
    private readonly Clock.ISystemClock _clock;

    private volatile PlaybackState _state = PlaybackState.Buffering;
    private TimeSpan                _clockOffset;
    private volatile bool           _clockOffsetReady;
    private volatile bool           _paused;
    private CancellationTokenSource _cts  = new();
    private Task                    _renderTask = Task.CompletedTask;
    private bool                    _userMuted   = false;
    private bool                    _engineMuted = false;
    private int                     _bufferUnderrunCount;

    // Adaptive ahead-buffer state (render-loop-only; no volatile needed)
    private int _aheadTargetMs  = TargetAheadMs;
    private int _behindCount    = 0;
    private int _recoveryFrames = 0;

    public PlaybackState State => _state;

    /// <summary>Number of frames currently held in the jitter buffer.</summary>
    public int BufferedFrameCount => _buffer.Count;

    /// <summary>Total audio duration currently held in the jitter buffer.</summary>
    public TimeSpan BufferedAudioDuration => _buffer.TotalDuration;

    /// <summary>Number of times playback entered the error state due to the jitter buffer being empty.</summary>
    public int BufferUnderrunCount => _bufferUnderrunCount;

    /// <summary>Current ahead-of-volume buffer target in milliseconds. Normally <see cref="TargetAheadMs"/>; increases temporarily under CPU pressure.</summary>
    internal int AheadTargetMs => _aheadTargetMs;

    /// <summary>
    /// Raised when the playback state or buffer occupancy changes meaningfully.
    /// Subscribers receive the current <see cref="PlaybackState"/> and the number
    /// of frames currently held in the jitter buffer.
    /// </summary>
    public event Action<PlaybackState, int>? StatusChanged;

    /// <summary>
    /// Raised when the engine enters or leaves a synchronized state.
    /// Carries the Sendspin state string: <c>"error"</c> or <c>"synchronized"</c>.
    /// Subscribers should send a complete <c>client/state</c> message to the server.
    /// </summary>
    public event Action<string>? PlaybackStateChanged;

    internal PlaybackEngine(
        IWasapiRenderer     renderer,
        Clock.ISystemClock? clock = null)
    {
        _renderer = renderer;
        _clock    = clock ?? Clock.SystemClock.Instance;
        _buffer   = new JitterBuffer();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the render loop. Call after the transport handshake and at
    /// least one <see cref="UpdateClockOffset"/> have completed.
    /// </summary>
    public void Start()
    {
        // Dispose the previous CTS (created at field-init or by a prior Start call)
        // before replacing it so the object is not leaked.
        _cts.Dispose();
        _cts        = new CancellationTokenSource();
        _renderTask = RenderLoopAsync(_cts.Token);
        _renderer.Start();
    }

    /// <summary>Enqueues a decoded frame for playback.</summary>
    /// <param name="serverTimestamp">Server-assigned UTC ticks at the time the frame was captured.</param>
    public void Enqueue(long serverTimestamp, AudioFrame frame)
    {
        _buffer.Enqueue(serverTimestamp, frame);
        StatusChanged?.Invoke(_state, _buffer.Count);
    }

    /// <summary>
    /// Updates the measured clock offset (from <see cref="ClockSynchronizer"/>).
    /// The render loop uses this to schedule frames relative to the server clock.
    /// </summary>
    public void UpdateClockOffset(TimeSpan offset)
    {
        _clockOffset      = offset;
        _clockOffsetReady = true;
    }

    /// <summary>
    /// Discards all buffered frames. Call when a <c>stream/clear</c> message arrives.
    /// </summary>
    public void ClearBuffer() => _buffer.Clear();

    /// <summary>
    /// Suspends audio output immediately. Clears both the jitter buffer and the
    /// WASAPI hardware buffer so audio stops playing without waiting for queued
    /// samples to drain. Call when the user presses Pause.
    /// </summary>
    public void Pause()
    {
        _paused = true;
        _buffer.Clear();
        _renderer.ClearBuffer();
        TransitionTo(PlaybackState.Buffering);
        ResetAheadBuffer();
    }

    /// <summary>
    /// Lifts the pause gate so the engine can transition back to
    /// <see cref="PlaybackState.Synchronized"/> once enough frames accumulate.
    /// Call when the user presses Play.
    /// </summary>
    public void Resume()
    {
        _paused = false;
    }

    /// <summary>Sets the user-controlled volume. <paramref name="volume"/> is a linear scalar in [0.0, 1.0].</summary>
    public void SetVolume(float volume) => _renderer.SetVolume(volume);

    /// <summary>
    /// Mutes or unmutes based on the user's command.
    /// The engine may also mute internally during error recovery; both states are tracked independently.
    /// </summary>
    public void SetUserMuted(bool muted)
    {
        _userMuted = muted;
        ApplyMuteState();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _renderTask.ConfigureAwait(false); }
        catch { }   // Render task may fault (e.g. WASAPI session invalidated); swallow all

        try { _renderer.Stop(); }    catch { }
        try { _renderer.Dispose(); } catch { }
        _cts.Dispose();
    }

    // ── Render loop ───────────────────────────────────────────────────────────

    private async Task RenderLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            switch (_state)
            {
                case PlaybackState.Buffering:
                    await HandleBufferingAsync(cancellationToken);
                    break;

                case PlaybackState.Synchronized:
                    await HandleSynchronizedAsync(cancellationToken);
                    break;

                case PlaybackState.Error:
                    await HandleErrorAsync(cancellationToken);
                    break;
            }
        }
    }

    private async Task HandleBufferingAsync(CancellationToken ct)
    {
        if (_paused || !_clockOffsetReady)
        {
            if (!_clockOffsetReady)
                Log.Debug("Playback buffering — waiting for first clock sync");
            await Task.Delay(5, ct).ConfigureAwait(false);
            return;
        }

        if (_buffer.Count >= MinBufferFrames)
        {
            Log.Debug("Playback buffering complete ({Count} frames); starting playback", _buffer.Count);
            TransitionTo(PlaybackState.Synchronized);
            PlaybackStateChanged?.Invoke("synchronized");
            return;
        }
        await Task.Delay(5, ct).ConfigureAwait(false);
    }

    private async Task HandleSynchronizedAsync(CancellationToken ct)
    {
        if (!_buffer.TryDequeue(out long timestamp, out var frame))
        {
            if (_renderer.BufferedBytes > 0)
            {
                await Task.Delay(5, ct).ConfigureAwait(false);
                return;
            }
            long serverNowUs = _clock.UtcNowMicroseconds + (long)_clockOffset.TotalMicroseconds;
            Log.Warning("Playback underrun — jitter buffer empty (estServerNow={ServerNowMs:F3} ms)", serverNowUs / 1_000.0);
            _bufferUnderrunCount++;
            await EnterErrorAsync(ct);
            return;
        }

        double driftMs = ComputeDriftMs(timestamp);

        if (Log.IsEnabled(LogEventLevel.Debug))
        {
            long localNowUs  = _clock.UtcNowMicroseconds;
            long serverNowUs = localNowUs + (long)_clockOffset.TotalMicroseconds;
            Log.Debug(
                "Playback render: buffer={BufferFrames} frames, drift={DriftMs:F1} ms " +
                "(localNow={LocalNowMs:F3} ms, offset={OffsetMs:F3} ms, estServerNow={ServerNowMs:F3} ms, frameTs={FrameTs:F3} ms)",
                _buffer.Count, driftMs,
                localNowUs / 1_000.0, _clockOffset.TotalMilliseconds, serverNowUs / 1_000.0, timestamp / 1_000.0);
        }

        if (driftMs > MaxDriftMs)
        {
            Log.Warning(
                "Playback drift {DriftMs:+0.0;-0.0} ms exceeds threshold ({MaxDriftMs} ms); entering error state",
                driftMs, MaxDriftMs);
            await EnterErrorAsync(ct);
            return;
        }

        // Compute a rate ratio to gently correct residual drift.
        // A 1 ms drift on a 20 ms frame → ratio = 20/21 ≈ 0.952 (speed up).
        double frameDurationMs = frame!.Duration.TotalMilliseconds;
        double rateRatio = frameDurationMs > 0
            ? frameDurationMs / (frameDurationMs + driftMs)
            : 1.0;

        rateRatio = Math.Clamp(rateRatio, 0.9, 1.1);

        var resampled = Math.Abs(rateRatio - 1.0) > 0.001
            ? SampleInterpolator.Interpolate(frame.Samples, frame.Channels, rateRatio)
            : frame.Samples;

        var samples = frame.Channels != _renderer.Channels
            ? ChannelDownmixer.Downmix(resampled, frame.Channels)
            : resampled;

        // Pace writes by WASAPI buffer level rather than a fixed timer.
        // Task.Delay on Windows has ~15 ms granularity; using it for frame timing
        // accumulates overshoot per frame.  Waiting until the hardware buffer has room
        // lets the device clock drive the rate instead.
        //
        // We keep only TargetAheadMs of samples buffered ahead of where volume is
        // applied (WasapiRenderer.Write), so volume changes take effect promptly.
        // If the buffer level drops below LowWaterMs on BehindThreshold consecutive
        // frames, CPU pressure is assumed and the target is doubled (up to MaxAheadMs),
        // then gradually stepped back down once normal conditions resume.
        int bytesPerMs    = _renderer.SampleRate * _renderer.Channels * sizeof(float) / 1000;
        int targetBytes   = _aheadTargetMs * bytesPerMs;
        int lowWaterBytes = LowWaterMs     * bytesPerMs;

        while (_renderer.BufferedBytes > targetBytes && !ct.IsCancellationRequested)
            await Task.Delay(5, ct).ConfigureAwait(false);

        if (_renderer.BufferedBytes < lowWaterBytes)
        {
            if (++_behindCount >= BehindThreshold)
            {
                _aheadTargetMs  = Math.Min(_aheadTargetMs * 2, MaxAheadMs);
                _behindCount    = 0;
                _recoveryFrames = 0;
                Log.Warning("Audio falling behind; increasing ahead target to {AheadTargetMs} ms", _aheadTargetMs);
            }
        }
        else
        {
            _behindCount = 0;
            if (_aheadTargetMs > TargetAheadMs && ++_recoveryFrames >= RecoveryFrameCount)
            {
                _aheadTargetMs  = Math.Max(_aheadTargetMs - RecoveryStepMs, TargetAheadMs);
                _recoveryFrames = 0;
                Log.Debug("Audio caught up; reducing ahead target to {AheadTargetMs} ms", _aheadTargetMs);
            }
        }

        _renderer.Write(samples);
    }

    private async Task HandleErrorAsync(CancellationToken ct)
    {
        _engineMuted = true;
        ApplyMuteState();

        if (_buffer.Count >= MinBufferFrames)
        {
            Log.Debug("Playback recovered ({Count} frames buffered); resuming", _buffer.Count);
            _engineMuted = false;
            ApplyMuteState();
            TransitionTo(PlaybackState.Buffering);
            return;
        }

        await Task.Delay(10, ct).ConfigureAwait(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task EnterErrorAsync(CancellationToken ct)
    {
        TransitionTo(PlaybackState.Error);
        _engineMuted = true;
        ApplyMuteState();
        _buffer.Clear();
        PlaybackStateChanged?.Invoke("error");
        return Task.CompletedTask;
    }

    private void TransitionTo(PlaybackState next)
    {
        _state = next;
        StatusChanged?.Invoke(next, _buffer.Count);
    }

    private void ApplyMuteState() => _renderer.SetMuted(_engineMuted || _userMuted);

    private void ResetAheadBuffer()
    {
        _aheadTargetMs  = TargetAheadMs;
        _behindCount    = 0;
        _recoveryFrames = 0;
    }

    /// <summary>
    /// Returns the difference in ms between when we are playing the frame
    /// and when the server expected it to be played (positive = we are late).
    /// </summary>
    private double ComputeDriftMs(long serverTimestamp)
    {
        long localNowUs  = _clock.UtcNowMicroseconds;
        long serverNowUs = localNowUs + (long)_clockOffset.TotalMicroseconds;
        return TimeSpan.FromMicroseconds(serverNowUs - serverTimestamp).TotalMilliseconds;
    }
}
