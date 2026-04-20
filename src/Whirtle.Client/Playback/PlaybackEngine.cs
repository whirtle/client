// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;
using Serilog.Events;
using Whirtle.Client.Codec;

namespace Whirtle.Client.Playback;

/// <summary>Event data for <see cref="PlaybackEngine.StatusChanged"/>.</summary>
public sealed class PlaybackStatusEventArgs(PlaybackState state, int bufferCount) : EventArgs
{
    public PlaybackState State       { get; } = state;
    public int           BufferCount { get; } = bufferCount;
}

/// <summary>Event data for <see cref="PlaybackEngine.PlaybackStateChanged"/>.</summary>
public sealed class PlaybackStateChangedEventArgs(string state) : EventArgs
{
    public string State { get; } = state;
}

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
    public   const int TargetAheadMs      = 50;  // nominal ms of samples ahead of volume application
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
    // instead of absorbing it all in one frame. The trim is added to a baseline ratio derived
    // from the Kalman-filtered clock drift, so this only needs to absorb measurement noise on
    // the per-frame schedule offset — N is large to keep that trim tiny per frame.
    private const int RateCorrectionFrames = 16;

    // Resampler control-loop tuning. Resampling here exists only to compensate for crystal
    // drift between the server and client clocks; real drift is on the order of 5–50 ppm, so
    // anything beyond a few hundred ppm is a sign that some other layer (jitter buffer, clock
    // sync) should be handling the situation instead.
    //
    // MaxRatioDeviation: hard ceiling on |ratio − 1|. ±200 ppm is well above any physical
    // crystal drift and well below any audible pitch shift (~1000 ppm). Saturating it is a
    // diagnostic signal, not a routine occurrence.
    //
    // MaxRatioDeltaPerFrame: caps the per-frame change in the smoothed ratio so a sudden
    // target swing can't produce a step discontinuity at a chunk boundary (the kind that
    // pops or clicks even when the absolute ratio is small).
    //
    // RatioEwmaAlpha: low-pass on the target ratio. α=0.05 gives a time constant of ~20
    // frames; clock drift evolves over seconds, so the loop has plenty of bandwidth.
    private const double MaxRatioDeviation     = 0.0002;   // ±200 ppm
    private const double MaxRatioDeltaPerFrame = 50e-6;    // ±50 ppm per frame
    private const double RatioEwmaAlpha        = 0.05;

    // Filter-health gating. The resampler trusts the Kalman drift estimate as its baseline
    // only when σ_drift is below this threshold; otherwise it falls back to ratio = 1.0 and
    // lets the jitter buffer absorb timing noise. Likewise, when σ_offset is above the
    // schedule-offset gate the resampler is skipped entirely for that frame — typical of the
    // first 1–2 frames after a reconnect, when filter state is recovering.
    private const double DriftTrustedStdDevUsPerS = 50.0;   // 50 ppm = 0.05 ms/s
    private const double OffsetGateStdDevUs       = 5_000;  // 5 ms

    // Windows multimedia timer: raises system timer resolution from default ~15.6 ms to 1 ms so
    // Task.Delay in the precise-pacing wait doesn't overshoot by a full tick (which was producing
    // consistent 7 ms write-time misses and driving rate correction).
    [DllImport("winmm.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
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
    private bool                    _userMuted;
    private bool                    _engineMuted;
    private int                     _bufferUnderrunCount;
    private int                     _minBufferFloorHitCount;
    private int                     _rateRatioClampHitCount;
    // Stored as raw bits so cross-thread reads are atomic without needing a lock
    // (plain double reads are not guaranteed atomic on all runtimes).
    private long                    _lastRateRatioBits = BitConverter.DoubleToInt64Bits(1.0);

    // Filter snapshot from the most recent ClockSynchronizer update. Read on the render
    // thread; written via UpdateClockState (UI / sync callback). Stored as raw bits for
    // atomic cross-thread reads of the doubles.
    private long _driftUsPerSBits        = BitConverter.DoubleToInt64Bits(0.0);
    private long _offsetStdDevUsBits     = BitConverter.DoubleToInt64Bits(double.PositiveInfinity);
    private long _driftStdDevUsPerSBits  = BitConverter.DoubleToInt64Bits(double.PositiveInfinity);

    // EWMA-smoothed rate ratio, render-loop-only.
    private double _smoothedRatio = 1.0;

    // Adaptive ahead-buffer state (render-loop-only; no volatile needed)
    private int _aheadTargetMs   = TargetAheadMs;
    private int _behindCount;
    private int _recoveryFrames;

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
    /// is on-schedule; &gt;1 slows audio down (we're ahead); &lt;1 speeds it up
    /// (we're behind). Clamped to [1 − <see cref="MaxRatioDeviation"/>, 1 + <see cref="MaxRatioDeviation"/>].
    /// </summary>
    public double LastRateRatio =>
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastRateRatioBits));

    /// <summary>
    /// Number of times the resampler ratio was clamped against
    /// <see cref="MaxRatioDeviation"/>. A healthy stream should never increment this;
    /// a non-zero value indicates either filter instability or a control-loop bug.
    /// </summary>
    public int RateRatioClampHitCount => _rateRatioClampHitCount;

    /// <summary>
    /// Raised when the playback state or buffer occupancy changes meaningfully.
    /// Subscribers receive the current <see cref="PlaybackState"/> and the number
    /// of frames currently held in the jitter buffer.
    /// </summary>
    public event EventHandler<PlaybackStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Raised when the engine enters or leaves a synchronized state.
    /// Carries the Sendspin state string: <c>"error"</c> or <c>"synchronized"</c>.
    /// Subscribers should send a complete <c>client/state</c> message to the server.
    /// </summary>
    public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;

    internal PlaybackEngine(
        IWasapiRenderer     renderer,
        Clock.ISystemClock? clock = null)
    {
        _renderer = renderer;
        _clock    = clock ?? Clock.SystemClock.Instance;
        _buffer   = new JitterBuffer();
        _renderer.RendererFailed += OnRendererFailed;
    }

    /// <summary>
    /// Raised when the underlying <see cref="IWasapiRenderer"/> stops unexpectedly
    /// (device unplug, endpoint disable, session reset). The engine will have
    /// already transitioned to <see cref="PlaybackState.Error"/> by the time this
    /// fires; subscribers should treat it as a hard failure that will not recover
    /// without user intervention.
    /// </summary>
    public event EventHandler? RendererFailed;

    private void OnRendererFailed(object? sender, EventArgs e)
    {
        Log.Error("PlaybackEngine: audio renderer stopped unexpectedly (device removed or reset)");
        try { _cts.Cancel(); } catch { }
        EnterError();
        RendererFailed?.Invoke(this, EventArgs.Empty);
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
        // Drop frames that arrive while paused. The server keeps streaming for ~1 RTT
        // after we send client/command pause, and those in-flight frames carry the
        // pre-pause timeline. If we buffered them, the next Resume would transition to
        // Synchronized with a head scheduleOffset of ~-1 s (server lookahead) and no
        // way to catch up — the resampler would saturate and we'd underrun.
        if (_paused) return;
        _buffer.Enqueue(serverTimestamp, frame);
        StatusChanged?.Invoke(this, new PlaybackStatusEventArgs(_state, _buffer.Count));
    }

    /// <summary>
    /// Updates the measured clock offset (from <see cref="ClockSynchronizer"/>).
    /// The render loop uses this to schedule frames relative to the server clock.
    /// Equivalent to <see cref="UpdateClockState"/> with no drift information — the
    /// resampler will fall back to ratio = 1.0 plus per-frame trim only.
    /// </summary>
    public void UpdateClockOffset(TimeSpan offset)
        => UpdateClockState(offset, driftUsPerS: 0.0,
                            offsetStdDevUs: double.PositiveInfinity,
                            driftStdDevUsPerS: double.PositiveInfinity);

    /// <summary>
    /// Updates the full Kalman-filter snapshot from <see cref="ClockSynchronizer"/>.
    /// The render loop uses <paramref name="offset"/> to schedule frames and uses
    /// <paramref name="driftUsPerS"/> as the steady-state baseline for the resampler
    /// ratio when σ_drift is below the trust threshold. The σ values gate filter use
    /// during reconnect transients.
    /// </summary>
    public void UpdateClockState(
        TimeSpan offset,
        double   driftUsPerS,
        double   offsetStdDevUs,
        double   driftStdDevUsPerS)
    {
        _clockOffset      = offset;
        _clockOffsetReady = true;
        Interlocked.Exchange(ref _driftUsPerSBits,       BitConverter.DoubleToInt64Bits(driftUsPerS));
        Interlocked.Exchange(ref _offsetStdDevUsBits,    BitConverter.DoubleToInt64Bits(offsetStdDevUs));
        Interlocked.Exchange(ref _driftStdDevUsPerSBits, BitConverter.DoubleToInt64Bits(driftStdDevUsPerS));
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

        try { await _renderer.FadeOutAsync().ConfigureAwait(false); } catch { }
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
                Log.Verbose("PlaybackEngine: buffering — waiting for first clock sync");
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
                Log.Verbose("PlaybackEngine: discarded {Count} late frames before playback start", dropped);
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
            Log.Verbose(
                "PlaybackEngine: buffering complete ({Count} frames, head scheduleOffset={HeadScheduleOffsetMs:F1} ms, target={Target} ms); starting playback",
                _buffer.Count, headScheduleOffsetMs, -_aheadTargetMs);
            TransitionTo(PlaybackState.Synchronized);
            PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs("synchronized"));
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
        // No cap on waitMs: server lookahead is ~1.8 s, so the first frame on a fresh
        // stream can legitimately need a wait close to that. A cap here caused the
        // frame to be written ~1 s early, which the resampler then could not close
        // (200 ppm can't absorb a 1 s offset inside the jitter-buffer lifetime) —
        // resulting in a persistent negative scheduleOffset and eventual underrun.
        double frameDurationMs = frame!.Duration.TotalMilliseconds;
        double dequeueScheduleOffsetMs = scheduleOffsetMs;
        double waitMs          = -_aheadTargetMs - scheduleOffsetMs;
        if (waitMs > 1)
        {
            if (CancelableWait(ct, (int)waitMs)) return;
            scheduleOffsetMs = ComputeScheduleOffsetMs(timestamp);
        }

        // Pause() may have fired during the pacing wait — if so, drop the
        // already-dequeued frame rather than writing resampled audio into the
        // freshly-cleared renderer buffer with a stale _aheadTargetMs.
        if (_paused) return;

        if (Log.IsEnabled(LogEventLevel.Verbose))
        {
            Log.Verbose(
                "PlaybackEngine: precise-pacing dequeueScheduleOffset={DequeueMs:F1} ms, waitMs={WaitMs:F1}, postWaitScheduleOffset={PostMs:F1} ms (target={Target} ms)",
                dequeueScheduleOffsetMs, waitMs, scheduleOffsetMs, -_aheadTargetMs);
        }

        if (Log.IsEnabled(LogEventLevel.Verbose))
        {
            long serverNowUs = _clock.UtcNowMicroseconds + (long)_clockOffset.TotalMicroseconds;
            Log.Verbose(
                "PlaybackEngine: render buffer={BufferFrames} frames, scheduleOffset={ScheduleOffsetMs:F1} ms " +
                "(frameTs={FrameTs:F3} ms, estServerNow={ServerNowMs:F3} ms)",
                _buffer.Count, scheduleOffsetMs,
                timestamp / 1_000.0, serverNowUs / 1_000.0);
        }

        double driftUsPerS       = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _driftUsPerSBits));
        double offsetStdDevUs    = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _offsetStdDevUsBits));
        double driftStdDevUsPerS = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _driftStdDevUsPerSBits));

        double prevSmoothed = _smoothedRatio;
        var    step         = ComputeRateRatio(
            scheduleOffsetMs:  scheduleOffsetMs,
            aheadTargetMs:     _aheadTargetMs,
            frameDurationMs:   frameDurationMs,
            driftUsPerS:       driftUsPerS,
            offsetStdDevUs:    offsetStdDevUs,
            driftStdDevUsPerS: driftStdDevUsPerS,
            previousSmoothedRatio: prevSmoothed);
        _smoothedRatio      = step.SmoothedRatio;
        double rateRatio    = step.RateRatio;
        bool   resamplerSkipped = step.ResamplerSkipped;
        if (step.ClampSaturated)
        {
            _rateRatioClampHitCount++;
            Log.Warning(
                "PlaybackEngine: rate ratio clamp saturated (preClamp={PreClamp:F6}, clamped={Clamped:F6}); " +
                "drift={DriftUsPerS:+0.0;-0.0} µs/s ±{DriftSigma:F1}, σ_offset={OffsetSigma:F1} µs",
                step.PreClampRatio, rateRatio, driftUsPerS, driftStdDevUsPerS, offsetStdDevUs);
        }
        if (resamplerSkipped && Log.IsEnabled(LogEventLevel.Verbose))
        {
            Log.Verbose(
                "PlaybackEngine: skipping resampler (σ_offset={OffsetSigmaUs:F1} µs > gate {GateUs:F0} µs)",
                offsetStdDevUs, OffsetGateStdDevUs);
        }

        Interlocked.Exchange(ref _lastRateRatioBits, BitConverter.DoubleToInt64Bits(rateRatio));

        short[] resampled;
        if (!resamplerSkipped && Math.Abs(rateRatio - 1.0) > 1e-6)
        {
            if (Log.IsEnabled(LogEventLevel.Verbose))
            {
                Log.Verbose(
                    "PlaybackEngine: resampling rateRatio={RateRatio:F6} " +
                    "(baseline={Baseline:F6}, trim={Trim:+0.000000;-0.000000}, " +
                    "scheduleOffset={ScheduleOffsetMs:F1} ms, adjustedOffset={AdjustedOffsetMs:F1} ms, " +
                    "drift={DriftUsPerS:+0.0;-0.0} µs/s, driftTrusted={DriftTrusted})",
                    rateRatio, step.BaselineRatio, step.Trim, scheduleOffsetMs, step.AdjustedOffsetMs,
                    driftUsPerS, step.DriftTrusted);
            }
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
                Log.Verbose("PlaybackEngine: audio caught up; reducing ahead target to {AheadTargetMs} ms", _aheadTargetMs);
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
            Log.Warning("PlaybackEngine: recovered ({Count} frames buffered); resuming", _buffer.Count);
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
        PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs("error"));
    }

    private void TransitionTo(PlaybackState next)
    {
        _state = next;
        StatusChanged?.Invoke(this, new PlaybackStatusEventArgs(next, _buffer.Count));
    }

    private void ApplyMuteState() => _renderer.SetMuted(_engineMuted || _userMuted);

    private void ResetAheadBuffer()
    {
        _aheadTargetMs  = TargetAheadMs;
        _behindCount    = 0;
        _recoveryFrames = 0;
        _smoothedRatio  = 1.0;
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

    /// <summary>
    /// Result of a single rate-ratio control-loop step.
    /// </summary>
    /// <param name="RateRatio">Final ratio to apply to the resampler (post-clamp).</param>
    /// <param name="SmoothedRatio">EWMA state to carry into the next step.</param>
    /// <param name="ResamplerSkipped">
    /// <see langword="true"/> when σ_offset exceeds the gate; the caller should bypass
    /// the resampler entirely and write the frame as-is.
    /// </param>
    /// <param name="ClampSaturated"><see langword="true"/> when the clamp pinned the ratio at the boundary.</param>
    /// <param name="PreClampRatio">Smoothed ratio before the clamp was applied (for diagnostics).</param>
    /// <param name="BaselineRatio">Drift-derived baseline (for diagnostics).</param>
    /// <param name="Trim">Per-frame schedule-offset correction (for diagnostics).</param>
    /// <param name="AdjustedOffsetMs">Schedule offset relative to the precise-pacing target (for diagnostics).</param>
    /// <param name="DriftTrusted">Whether the drift estimate was used as the baseline (for diagnostics).</param>
    internal readonly record struct RateRatioStep(
        double RateRatio,
        double SmoothedRatio,
        bool   ResamplerSkipped,
        bool   ClampSaturated,
        double PreClampRatio,
        double BaselineRatio,
        double Trim,
        double AdjustedOffsetMs,
        bool   DriftTrusted);

    /// <summary>
    /// Pure computation of the next rate-ratio control step. Splits the ratio into a
    /// baseline derived from clock drift, a trim derived from residual schedule offset,
    /// then applies EWMA smoothing, a per-frame slew limit, and a tight clamp. When the
    /// filter health gate trips (σ_offset above <see cref="OffsetGateStdDevUs"/>) the
    /// resampler is signalled to be skipped entirely and the smoothed state is reset.
    /// </summary>
    internal static RateRatioStep ComputeRateRatio(
        double scheduleOffsetMs,
        int    aheadTargetMs,
        double frameDurationMs,
        double driftUsPerS,
        double offsetStdDevUs,
        double driftStdDevUsPerS,
        double previousSmoothedRatio)
    {
        bool filterHealthy = offsetStdDevUs <= OffsetGateStdDevUs;
        bool driftTrusted  = driftStdDevUsPerS <= DriftTrustedStdDevUsPerS;

        double baselineRatio = driftTrusted
            ? 1.0 - driftUsPerS / 1_000_000.0
            : 1.0;

        double adjustedOffsetMs = scheduleOffsetMs + aheadTargetMs;
        double trim = frameDurationMs > 0
            ? -adjustedOffsetMs / (RateCorrectionFrames * frameDurationMs)
            : 0.0;

        if (!filterHealthy)
        {
            return new RateRatioStep(
                RateRatio:        1.0,
                SmoothedRatio:    1.0,
                ResamplerSkipped: true,
                ClampSaturated:   false,
                PreClampRatio:    1.0,
                BaselineRatio:    baselineRatio,
                Trim:             trim,
                AdjustedOffsetMs: adjustedOffsetMs,
                DriftTrusted:     driftTrusted);
        }

        double targetRatio = baselineRatio + trim;
        double next        = RatioEwmaAlpha * targetRatio + (1.0 - RatioEwmaAlpha) * previousSmoothedRatio;
        double delta       = Math.Clamp(next - previousSmoothedRatio, -MaxRatioDeltaPerFrame, MaxRatioDeltaPerFrame);
        double smoothed    = previousSmoothedRatio + delta;
        double clamped     = Math.Clamp(smoothed, 1.0 - MaxRatioDeviation, 1.0 + MaxRatioDeviation);

        return new RateRatioStep(
            RateRatio:        clamped,
            SmoothedRatio:    clamped,             // pin smoothed state at the clamp boundary
            ResamplerSkipped: false,
            ClampSaturated:   clamped != smoothed,
            PreClampRatio:    smoothed,
            BaselineRatio:    baselineRatio,
            Trim:             trim,
            AdjustedOffsetMs: adjustedOffsetMs,
            DriftTrusted:     driftTrusted);
    }
}
