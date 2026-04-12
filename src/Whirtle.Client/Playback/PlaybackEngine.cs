// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Codec;
using Whirtle.Client.Protocol;

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
///                                   state: 'error' / 'synchronized' messages
///                                   are sent via the ProtocolClient
/// </summary>
public sealed class PlaybackEngine : IAsyncDisposable
{
    // Tuning constants
    private const int    MinBufferFrames = 4;   // frames required before playback starts/recovers
    private const double MaxDriftMs      = 50;  // drift threshold before entering Error state

    private readonly JitterBuffer       _buffer;
    private readonly IWasapiRenderer    _renderer;
    private readonly ProtocolClient     _protocol;
    private readonly Clock.ISystemClock _clock;

    private volatile PlaybackState _state = PlaybackState.Buffering;
    private TimeSpan                _clockOffset;
    private CancellationTokenSource _cts  = new();
    private Task                    _renderTask = Task.CompletedTask;

    public PlaybackState State => _state;

    /// <summary>
    /// Raised when the playback state or buffer occupancy changes meaningfully.
    /// Subscribers receive the current <see cref="PlaybackState"/> and the number
    /// of frames currently held in the jitter buffer.
    /// </summary>
    public event Action<PlaybackState, int>? StatusChanged;

    internal PlaybackEngine(
        IWasapiRenderer     renderer,
        ProtocolClient      protocol,
        Clock.ISystemClock? clock = null)
    {
        _renderer = renderer;
        _protocol = protocol;
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
    public void UpdateClockOffset(TimeSpan offset) => _clockOffset = offset;

    /// <summary>
    /// Discards all buffered frames. Call when a <c>stream/clear</c> message arrives.
    /// </summary>
    public void ClearBuffer() => _buffer.Clear();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _renderTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        _renderer.Stop();
        _renderer.Dispose();
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
        if (_buffer.Count >= MinBufferFrames)
        {
            TransitionTo(PlaybackState.Synchronized);
            return;
        }
        await Task.Delay(5, ct).ConfigureAwait(false);
    }

    private async Task HandleSynchronizedAsync(CancellationToken ct)
    {
        if (!_buffer.TryDequeue(out long timestamp, out var frame))
        {
            // Underrun
            await EnterErrorAsync(ct);
            return;
        }

        double driftMs = ComputeDriftMs(timestamp);

        if (Math.Abs(driftMs) > MaxDriftMs)
        {
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

        var samples = Math.Abs(rateRatio - 1.0) > 0.001
            ? SampleInterpolator.Interpolate(frame.Samples, frame.Channels, rateRatio)
            : frame.Samples;

        _renderer.Write(samples);

        // Yield to avoid busy-spinning; real WASAPI timing comes from the hardware buffer.
        var nextFrameDelay = TimeSpan.FromMilliseconds(Math.Max(1, frame.Duration.TotalMilliseconds / 2));
        await Task.Delay(nextFrameDelay, ct).ConfigureAwait(false);
    }

    private async Task HandleErrorAsync(CancellationToken ct)
    {
        _renderer.SetMuted(true);

        if (_buffer.Count >= MinBufferFrames)
        {
            _renderer.SetMuted(false);
            _state = PlaybackState.Buffering; // will quickly advance to Synchronized
            await NotifySynchronizedAsync().ConfigureAwait(false);
            return;
        }

        await Task.Delay(10, ct).ConfigureAwait(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task EnterErrorAsync(CancellationToken ct)
    {
        TransitionTo(PlaybackState.Error);
        _renderer.SetMuted(true);
        _buffer.Clear();

        try
        {
            await _protocol.SendAsync(
                new ClientStateMessage("error"),
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort — don't let a send failure crash the render loop.
        }
    }

    private void TransitionTo(PlaybackState next)
    {
        _state = next;
        StatusChanged?.Invoke(next, _buffer.Count);
    }

    private async Task NotifySynchronizedAsync()
    {
        try
        {
            // Use a short-lived CTS so a stalled send doesn't block forever.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _protocol.SendAsync(
                new ClientStateMessage("synchronized"), cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception) { }
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
