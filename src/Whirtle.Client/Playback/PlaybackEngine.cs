// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
///  <see cref="SampleInterpolator"/> to correct schedule offset using the
///  <see cref="ClockSyncResult"/> clock offset, and feeds them to an
///  <see cref="IWasapiRenderer"/>.
///
/// State machine
/// ─────────────
///  Buffering     → Synchronized  : buffer reaches StartupBufferFrames (late frames dropped to MinBufferFrames)
///  Synchronized  → Error         : underrun or scheduleOffset > MaxScheduleOffsetMs
///  Error         → Buffering     : buffer recovers to MinBufferFrames;
///                                   <see cref="PlaybackStateChanged"/> fires with
///                                   <c>"error"</c> or <c>"synchronized"</c> so the
///                                   caller can send a complete <c>client/state</c> message
/// </summary>
public sealed class PlaybackEngine : IAsyncDisposable
{
    // Tuning constants
    private const int    MinBufferFrames        = 4;   // frames required before playback recovers from underrun
    private const int    StartupBufferFrames    = 8;   // frames required before initial/resumed playback starts (provides headroom to drop late frames)
    private const double MaxScheduleOffsetMs = 200; // schedule-offset threshold before entering Error state; must exceed renderer latency (default 100 ms)

    // Ahead-buffer tuning: keeps only a small window of samples ahead of where volume
    // is applied (WasapiRenderer.Write), so volume changes take effect quickly.
    // Under CPU pressure the window expands automatically to avoid underruns.
    internal const int TargetAheadMs      = 50;  // nominal ms of samples ahead of volume application
    private  const int MaxAheadMs         = 200; // ceiling when falling behind
    internal const int LowWaterMs         = 10;  // buffer level (ms) that signals we're falling behind
    private  const int BehindThreshold    = 5;   // consecutive low-water events before doubling target
    // Recovery is amortized: small step × short interval, rather than a big step × long
    // interval. A 10 ms jump in the target flowed directly into adjustedOffset (scheduleOffset
    // + aheadTarget) and produced a rateRatio of ~1.025 for several frames. A 1 ms step keeps
    // the correction below ~1.003. Same average recovery rate (0.2 ms/frame) as before.
    private  const int RecoveryStepMs     = 1;   // ms stepped down per recovery interval
    internal const int RecoveryFrameCount = 5;   // healthy-buffer frames between recovery steps

    // Rate correction amortization: spread the correction for a residual offset across N frames
    // instead of absorbing it all in one frame. A 7 ms miss on a 96 ms frame at N=1 would produce
    // a 7% rate change (audible pitch wobble); at N=4 it's a 1.8% change accumulating over 4 frames.
    private const int RateCorrectionFrames = 4;

    // Windows multimedia timer: raises system timer resolution from default ~15.6 ms to 1 ms so
    // Task.Delay in the precise-pacing wait doesn't overshoot by a full tick (which was producing
    // consistent 7 ms write-time misses and driving rate correction).
    [DllImport("winmm.dll", ExactSpelling = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", ExactSpelling = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint timeEndPeriod(uint uPeriod);

    private const uint TimerResolutionMs = 1;
    private bool _timerResolutionRaised;

    private readonly JitterBuffer       _buffer;
    private readonly IWasapiRenderer    _renderer;
    private readonly Clock.ISystemClock _clock;

    private volatile PlaybackState _state = PlaybackState.Buffering;
    private TimeSpan                _clockOffset;
    private volatile bool           _clockOffsetReady;
    private volatile bool           _paused;
    private CancellationTokenSource _cts  = new();
    private Thread?                 _renderThread;
    private bool                    _userMuted   = false;
    private bool                    _engineMuted = false;
    private int                     _bufferUnderrunCount;
    private int                     _minBufferFloorHitCount;
    // Stored as raw bits so cross-thread reads are atomic without needing a lock
    // (plain double reads are not guaranteed atomic on all runtimes).
    private long                    _lastRateRatioBits = BitConverter.DoubleToInt64Bits(1.0);

    // Adaptive ahead-buffer state (render-loop-only; no volatile needed)
    private int _aheadTargetMs   = TargetAheadMs;
    private int _behindCount     = 0;
    private int _recoveryFrames  = 0;

    public PlaybackState State => _state;

    /// <summary>Number of frames currently held in the jitter buffer.</summary>
    public int BufferedFrameCount => _buffer.Count;

    /// <summary>Total audio duration currently held in the jitter buffer.</summary>
    public TimeSpan BufferedAudioDuration => _buffer.TotalDuration;

    /// <summary>Number of times playback entered the error state due to the jitter buffer being empty.</summary>
    public int BufferUnderrunCount => _bufferUnderrunCount;

    /// <summary>
    /// Number of times the late-frame drop at buffering→synchronized transition was held back by the
    /// <see cref="MinBufferFrames"/> floor, leaving late frames in the buffer that will force rate
    /// correction on startup.
    /// </summary>
    public int MinBufferFloorHitCount => _minBufferFloorHitCount;

    /// <summary>Current ahead-of-volume buffer target in milliseconds. Normally <see cref="TargetAheadMs"/>; increases temporarily under CPU pressure.</summary>
    public int AheadTargetMs => _aheadTargetMs;

    /// <summary>
    /// The rate ratio most recently applied by the resampler. 1.0 when playback
    /// is on-schedule; &gt;1 speeds audio up (we're behind); &lt;1 slows it down
    /// (we're ahead). Clamped to [0.9, 1.1].
    /// </summary>
    public double LastRateRatio =>
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastRateRatioBits));

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
        if (!_timerResolutionRaised && OperatingSystem.IsWindows())
        {
            if (timeBeginPeriod(TimerResolutionMs) == 0) // TIMERR_NOERROR
                _timerResolutionRaised = true;
        }

        // Run on a dedicated thread rather than the thread pool so the loop is
        // immune to pool starvation (e.g. large bursts of UI/WinRT work on Ctrl+S
        // opening the stats window). AboveNormal priority keeps Windows from
        // preempting the loop for routine background work. GC pauses will still
        // stall the loop — that's what the Verbose GC log in RenderLoop reports.
        var ct = _cts.Token;
        _renderThread = new Thread(() => RenderLoop(ct))
        {
            IsBackground = true,
            Name         = "Whirtle.PlaybackEngine.Render",
            Priority     = ThreadPriority.AboveNormal,
        };
        _renderThread.Start();
        _renderer.Start();
        Log.Debug("PlaybackEngine: engine started; ahead target = {AheadTargetMs} ms", _aheadTargetMs);
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
        if (_renderThread is { } thread)
        {
            // Offload the blocking Join so the caller's task doesn't block its
            // scheduler thread (shutdown runs on the UI dispatcher).
            try { await Task.Run(thread.Join).ConfigureAwait(false); }
            catch { }
            _renderThread = null;
        }

        try { _renderer.Stop(); }    catch { }
        try { _renderer.Dispose(); } catch { }
        if (_timerResolutionRaised && OperatingSystem.IsWindows())
        {
            timeEndPeriod(TimerResolutionMs);
            _timerResolutionRaised = false;
        }
        _cts.Dispose();
    }

    // ── Render loop ───────────────────────────────────────────────────────────

    private void RenderLoop(CancellationToken cancellationToken)
    {
        int prevGen0 = GC.CollectionCount(0);
        int prevGen1 = GC.CollectionCount(1);
        int prevGen2 = GC.CollectionCount(2);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Log.IsEnabled(LogEventLevel.Verbose))
                {
                    int g0 = GC.CollectionCount(0);
                    int g1 = GC.CollectionCount(1);
                    int g2 = GC.CollectionCount(2);
                    if (g0 != prevGen0 || g1 != prevGen1 || g2 != prevGen2)
                    {
                        Log.Verbose(
                            "PlaybackEngine: GC collections since last iteration — gen0={G0} gen1={G1} gen2={G2}",
                            g0 - prevGen0, g1 - prevGen1, g2 - prevGen2);
                        prevGen0 = g0; prevGen1 = g1; prevGen2 = g2;
                    }
                }

                switch (_state)
                {
                    case PlaybackState.Buffering:
                        HandleBuffering(cancellationToken);
                        break;

                    case PlaybackState.Synchronized:
                        HandleSynchronized(cancellationToken);
                        break;

                    case PlaybackState.Error:
                        HandleError(cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* expected during shutdown */ }
        catch (Exception ex)
        {
            // Render thread has no task-faulted observer; log so the failure
            // surfaces instead of silently killing the process.
            Log.Error(ex, "PlaybackEngine: render loop faulted");
        }
    }

    /// <summary>
    /// Sleeps for <paramref name="ms"/> or until the token is canceled.
    /// Returns <c>true</c> if cancellation was signaled and the caller should exit.
    /// </summary>
    private static bool CancelableWait(CancellationToken ct, int ms)
        => ms > 0 && ct.WaitHandle.WaitOne(ms);

    private void HandleBuffering(CancellationToken ct)
    {
        if (_paused || !_clockOffsetReady)
        {
            if (!_clockOffsetReady)
                Log.Debug("PlaybackEngine: buffering — waiting for first clock sync");
            CancelableWait(ct, 5);
            return;
        }

        if (_buffer.Count >= StartupBufferFrames)
        {
            // Discard frames whose target dequeue window (effectiveTs − aheadTarget) is already
            // in the past.  This prevents the startup overshoot where the first dequeue lands
            // far behind target and forces many cycles of max-rate resampling.
            // Guard: always keep at least MinBufferFrames so we don't stall on recovery.
            long serverNowUs   = _clock.UtcNowMicroseconds + (long)_clockOffset.TotalMicroseconds;
            long aheadTargetUs = (long)_aheadTargetMs * 1_000L;
            long threshold     = serverNowUs + aheadTargetUs;
            int  dropped = 0;
            while (_buffer.Count > MinBufferFrames
                   && _buffer.TryPeekFirstTimestamp(out long ts)
                   && ts < threshold)
            {
                _buffer.TryDequeue(out _, out _);
                dropped++;
            }
            if (dropped > 0)
                Log.Debug("PlaybackEngine: discarded {Count} late frames before playback start", dropped);
            int retainedLate = _buffer.CountBefore(threshold);
            if (retainedLate > 0)
            {
                _minBufferFloorHitCount++;
                Log.Warning(
                    "PlaybackEngine: {Count} late frames retained to hold minimum buffer floor ({Floor}); expect rate correction on startup",
                    retainedLate, MinBufferFrames);
            }

            double headScheduleOffsetMs = _buffer.TryPeekFirstTimestamp(out long headTs)
                ? ComputeScheduleOffsetMs(headTs)
                : double.NaN;
            Log.Debug(
                "PlaybackEngine: buffering complete ({Count} frames, head scheduleOffset={HeadScheduleOffsetMs:F1} ms, target={Target} ms); starting playback",
                _buffer.Count, headScheduleOffsetMs, -_aheadTargetMs);
            TransitionTo(PlaybackState.Synchronized);
            PlaybackStateChanged?.Invoke("synchronized");
            return;
        }
        CancelableWait(ct, 5);
    }

    private void HandleSynchronized(CancellationToken ct)
    {
        if (!_buffer.TryDequeue(out long timestamp, out var frame))
        {
            if (_renderer.BufferedBytes > 0)
            {
                CancelableWait(ct, 5);
                return;
            }
            long serverNowUs = _clock.UtcNowMicroseconds + (long)_clockOffset.TotalMicroseconds;
            Log.Warning("PlaybackEngine: underrun — jitter buffer empty (estServerNow={ServerNowMs:F3} ms)", serverNowUs / 1_000.0);
            _bufferUnderrunCount++;
            EnterError();
            return;
        }

        double scheduleOffsetMs = ComputeScheduleOffsetMs(timestamp);

        if (scheduleOffsetMs > MaxScheduleOffsetMs)
        {
            Log.Warning(
                "PlaybackEngine: schedule offset {ScheduleOffsetMs:+0.0;-0.0} ms exceeds threshold ({MaxScheduleOffsetMs} ms); entering error state",
                scheduleOffsetMs, MaxScheduleOffsetMs);
            EnterError();
            return;
        }

        // Precise pacing: wait until scheduleOffset equals -aheadTargetMs exactly.
        // Writing the frame at that moment lands it at the correct write time so rate
        // correction is only needed for gradual clock drift, not for the initial offset.
        // This eliminates the startup 10% resampling loop: previously, rateRatio was
        // computed at dequeue time (far too early), clamped at 1.1, and distorted the
        // first several frames of audio even though the wall-clock gate would then
        // delay the actual write by ~frameDuration.
        //
        // Cap at frameDurationMs: in steady state the natural wait is exactly one frame
        // duration (the time it takes the previous frame to play out), so a larger wait
        // means we're further ahead than necessary — just write and let the next frame's
        // pacing catch up. This also bounds per-frame wall-time in tests where the fake
        // clock is frozen.
        double frameDurationMs = frame!.Duration.TotalMilliseconds;
        double dequeueScheduleOffsetMs = scheduleOffsetMs;
        double waitMs          = Math.Min(-_aheadTargetMs - scheduleOffsetMs, frameDurationMs);
        if (waitMs > 1)
        {
            if (CancelableWait(ct, (int)waitMs)) return;
            scheduleOffsetMs = ComputeScheduleOffsetMs(timestamp);
        }

        // Pause() may have fired during the pacing wait — if so, drop the
        // already-dequeued frame rather than writing resampled audio into the
        // freshly-cleared renderer buffer with a stale _aheadTargetMs.
        if (_paused) return;

        if (Log.IsEnabled(LogEventLevel.Debug))
        {
            Log.Debug(
                "PlaybackEngine: precise-pacing dequeueScheduleOffset={DequeueMs:F1} ms, waitMs={WaitMs:F1}, postWaitScheduleOffset={PostMs:F1} ms (target={Target} ms)",
                dequeueScheduleOffsetMs, waitMs, scheduleOffsetMs, -_aheadTargetMs);
        }

        if (Log.IsEnabled(LogEventLevel.Debug))
        {
            long serverNowUs = _clock.UtcNowMicroseconds + (long)_clockOffset.TotalMicroseconds;
            Log.Debug(
                "PlaybackEngine: render buffer={BufferFrames} frames, scheduleOffset={ScheduleOffsetMs:F1} ms " +
                "(frameTs={FrameTs:F3} ms, estServerNow={ServerNowMs:F3} ms)",
                _buffer.Count, scheduleOffsetMs,
                timestamp / 1_000.0, serverNowUs / 1_000.0);
        }

        // Compute a rate ratio to gently correct residual schedule offset, amortized over
        // RateCorrectionFrames frames so a small miss doesn't produce an audible pitch wobble.
        // scheduleOffsetMs is measured at write time (post precise-pacing wait), so the
        // target is -_aheadTargetMs; any residual offset reflects clock drift the wait
        // couldn't smooth out (e.g. Task.Delay imprecision, CPU pressure).
        // Example: +7 ms adjusted offset on a 96 ms frame with N=4 → ratio = 1 - 7/(4*96) ≈ 0.982
        // (1.8% speed up per frame; the full 7 ms is absorbed after ~4 frames).
        double adjustedOffsetMs = scheduleOffsetMs + _aheadTargetMs;
        double rateRatio = frameDurationMs > 0
            ? 1.0 - adjustedOffsetMs / (RateCorrectionFrames * frameDurationMs)
            : 1.0;

        rateRatio = Math.Clamp(rateRatio, 0.9, 1.1);
        Interlocked.Exchange(ref _lastRateRatioBits, BitConverter.DoubleToInt64Bits(rateRatio));

        short[]  resampled;
        if (Math.Abs(rateRatio - 1.0) > 0.001)
        {
            Log.Debug(
                "PlaybackEngine: resampling rateRatio={RateRatio:F4} (scheduleOffset={ScheduleOffsetMs:F1} ms, adjustedOffset={AdjustedOffsetMs:F1} ms)",
                rateRatio, scheduleOffsetMs, adjustedOffsetMs);
            resampled = SampleInterpolator.Interpolate(frame.Samples, frame.Channels, rateRatio);
        }
        else
        {
            resampled = frame.Samples;
        }

        var samples = frame.Channels != _renderer.Channels
            ? ChannelDownmixer.Downmix(resampled, frame.Channels)
            : resampled;

        // We keep only TargetAheadMs of samples buffered ahead of where volume is
        // applied (WasapiRenderer.Write), so volume changes take effect promptly.
        // If the buffer level drops below LowWaterMs on BehindThreshold consecutive
        // frames, CPU pressure is assumed and the target is doubled (up to MaxAheadMs),
        // then gradually stepped back down once normal conditions resume.
        int bytesPerMs    = _renderer.SampleRate * _renderer.Channels * sizeof(float) / 1000;
        int targetBytes   = _aheadTargetMs * bytesPerMs;
        int lowWaterBytes = LowWaterMs     * bytesPerMs;

        // Safety: the precise-pacing wait above normally keeps BufferedBytes near targetBytes
        // at write time, but if it returned early this prevents buffer overflow.
        while (_renderer.BufferedBytes > targetBytes && !ct.IsCancellationRequested)
        {
            if (CancelableWait(ct, 5)) return;
        }

        if (_renderer.BufferedBytes < lowWaterBytes)
        {
            if (++_behindCount >= BehindThreshold)
            {
                _aheadTargetMs  = Math.Min(_aheadTargetMs * 2, MaxAheadMs);
                _behindCount    = 0;
                _recoveryFrames = 0;
                Log.Warning("PlaybackEngine: audio falling behind; increasing ahead target to {AheadTargetMs} ms", _aheadTargetMs);
            }
        }
        else
        {
            _behindCount = 0;
            if (_aheadTargetMs > TargetAheadMs && ++_recoveryFrames >= RecoveryFrameCount)
            {
                _aheadTargetMs  = Math.Max(_aheadTargetMs - RecoveryStepMs, TargetAheadMs);
                _recoveryFrames = 0;
                Log.Debug("PlaybackEngine: audio caught up; reducing ahead target to {AheadTargetMs} ms", _aheadTargetMs);
            }
        }

        _renderer.Write(samples);
    }

    private void HandleError(CancellationToken ct)
    {
        _engineMuted = true;
        ApplyMuteState();

        if (_buffer.Count >= MinBufferFrames)
        {
            Log.Debug("PlaybackEngine: recovered ({Count} frames buffered); resuming", _buffer.Count);
            _engineMuted = false;
            ApplyMuteState();
            TransitionTo(PlaybackState.Buffering);
            return;
        }

        CancelableWait(ct, 10);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnterError()
    {
        TransitionTo(PlaybackState.Error);
        _engineMuted = true;
        ApplyMuteState();
        _buffer.Clear();
        PlaybackStateChanged?.Invoke("error");
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
    /// Returns estimatedServerNow − frameTimestamp in ms.
    /// Positive = we are dequeuing the frame late; negative = early.
    /// </summary>
    private double ComputeScheduleOffsetMs(long serverTimestamp)
    {
        long localNowUs  = _clock.UtcNowMicroseconds;
        long serverNowUs = localNowUs + (long)_clockOffset.TotalMicroseconds;
        return TimeSpan.FromMicroseconds(serverNowUs - serverTimestamp).TotalMilliseconds;
    }
}
