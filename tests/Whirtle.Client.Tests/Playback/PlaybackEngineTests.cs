using Whirtle.Client.Codec;
using Whirtle.Client.Playback;
using Whirtle.Client.Tests.Clock;

namespace Whirtle.Client.Tests.Playback;

public class PlaybackEngineTests
{
    private static AudioFrame Frame(int samples = 960) =>
        new(new short[samples * 2], 48_000, 2); // 20 ms stereo

    private static (PlaybackEngine engine, FakeWasapiRenderer renderer, FakeClock clock)
        Build()
    {
        var renderer = new FakeWasapiRenderer();
        var clock    = new FakeClock();
        var engine   = new PlaybackEngine(renderer, clock);
        return (engine, renderer, clock);
    }

    [Fact]
    public void InitialState_IsBuffering()
    {
        var (engine, _, _) = Build();
        Assert.Equal(PlaybackState.Buffering, engine.State);
    }

    [Fact]
    public async Task State_TransitionsToSynchronized_WhenBufferFull()
    {
        var (engine, renderer, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero); // mark clock as ready

        bool reachedSynchronized = false;
        engine.StatusChanged += (_, e) => { if (e.State == PlaybackState.Synchronized) reachedSynchronized = true; };

        engine.Start();

        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * (long)TimeSpan.FromMilliseconds(20).TotalMicroseconds, Frame());

        await PollUntil(() => reachedSynchronized, TimeSpan.FromSeconds(2));

        Assert.True(reachedSynchronized);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task State_TransitionsToError_OnUnderrun()
    {
        var (engine, _, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero); // mark clock as ready
        engine.Start();

        // Provide just enough to reach Synchronized…
        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * (long)TimeSpan.FromMilliseconds(20).TotalMicroseconds, Frame());

        await PollUntil(() => engine.State == PlaybackState.Synchronized, TimeSpan.FromSeconds(2));

        // …then drain the buffer and wait for underrun detection
        await PollUntil(() => engine.State == PlaybackState.Error, TimeSpan.FromSeconds(3));

        Assert.Equal(PlaybackState.Error, engine.State);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Renderer_IsMuted_WhenInErrorState()
    {
        var (engine, renderer, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero); // mark clock as ready
        engine.Start();

        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * (long)TimeSpan.FromMilliseconds(20).TotalMicroseconds, Frame());

        await PollUntil(() => engine.State == PlaybackState.Synchronized, TimeSpan.FromSeconds(2));
        await PollUntil(() => engine.State == PlaybackState.Error, TimeSpan.FromSeconds(3));

        Assert.True(renderer.Muted);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task State_RemainsBuffering_WhenBufferFullButClockNotReady()
    {
        var (engine, _, clock) = Build();
        // Deliberately do NOT call UpdateClockOffset.
        engine.Start();

        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * (long)TimeSpan.FromMilliseconds(20).TotalMicroseconds, Frame());

        // Give the render loop plenty of time to advance if the gate were absent.
        await Task.Delay(200);

        Assert.Equal(PlaybackState.Buffering, engine.State);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task State_TransitionsToSynchronized_OncClockOffsetSet()
    {
        var (engine, _, clock) = Build();

        bool reachedSynchronized = false;
        engine.StatusChanged += (_, e) => { if (e.State == PlaybackState.Synchronized) reachedSynchronized = true; };

        engine.Start();

        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * (long)TimeSpan.FromMilliseconds(20).TotalMicroseconds, Frame());

        // Engine is stuck in Buffering without a clock offset.
        await Task.Delay(100);
        Assert.Equal(PlaybackState.Buffering, engine.State);

        // Providing the offset unblocks the gate.
        engine.UpdateClockOffset(TimeSpan.Zero);

        await PollUntil(() => reachedSynchronized, TimeSpan.FromSeconds(2));
        Assert.True(reachedSynchronized);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Renderer_ReceivesSamples_DuringPlayback()
    {
        var (engine, renderer, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero);
        engine.Start();

        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => renderer.Written.Count > 0, TimeSpan.FromSeconds(2));

        Assert.NotEmpty(renderer.Written);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Drift_BelowThreshold_RendersNormally()
    {
        // 100 ms clock offset is below MaxScheduleOffsetMs (200 ms): engine should render all frames.
        // Frame timestamps start 200 ms into the future relative to localNow so they clear
        // the late-drop threshold (serverNow + 50 ms = localNow + 150 ms) on startup.
        var (engine, renderer, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.FromMilliseconds(100));
        engine.Start();

        const int frameCount = 8;
        for (int i = 0; i < frameCount; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + 200_000L + i * 20_000L, Frame());

        await PollUntil(() => renderer.Written.Count >= frameCount, TimeSpan.FromSeconds(2));

        Assert.Equal(frameCount, renderer.Written.Count);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task EnterError_RaisesPlaybackStateChangedWithErrorString()
    {
        var (engine, _, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero);

        string? receivedState = null;
        engine.PlaybackStateChanged += (_, e) => receivedState = e.State;
        engine.Start();

        // Provide just enough frames to reach Synchronized, then let it underrun.
        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => receivedState == "error", TimeSpan.FromSeconds(3));

        Assert.Equal("error", receivedState);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task LargeDrift_ExceedingThreshold_EntersErrorState()
    {
        // 250 ms drift exceeds MaxDriftMs (200 ms): engine should enter Error on the
        // first dequeued frame rather than attempting rate correction.
        var (engine, _, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.FromMilliseconds(250));
        engine.Start();

        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => engine.State == PlaybackState.Error, TimeSpan.FromSeconds(2));

        Assert.Equal(PlaybackState.Error, engine.State);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Error_RecoveryReturnsEngineToSynchronized()
    {
        // After an underrun drives the engine to Error, refilling the buffer should
        // bring it back through Buffering into Synchronized a second time.
        var (engine, _, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero);

        int synchronizedCount = 0;
        engine.StatusChanged += (_, e) =>
        {
            if (e.State == PlaybackState.Synchronized)
                synchronizedCount++;
        };

        engine.Start();

        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => engine.State == PlaybackState.Error, TimeSpan.FromSeconds(3));

        // Refill to trigger recovery — needs StartupBufferFrames to reach Synchronized again.
        for (int i = 8; i < 16; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => synchronizedCount >= 2, TimeSpan.FromSeconds(3));

        Assert.True(synchronizedCount >= 2);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlaybackStateChanged_FiresSynchronized_OnlyWhenActuallySynchronized()
    {
        // "synchronized" must be raised when transitioning Buffering→Synchronized,
        // not when leaving Error (which visits Buffering as an intermediate step).
        // We verify there are no two consecutive "synchronized" events — the old code
        // sent "synchronized" from HandleErrorAsync before the engine reached Synchronized,
        // which would produce the sequence: ..., "synchronized", "synchronized".
        var (engine, _, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero);

        var states = new List<string>();
        var tcs    = new TaskCompletionSource<bool>();
        engine.PlaybackStateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.State);
                // Stop collecting once we have our error + second synchronized.
                if (states.Count(x => x == "synchronized") >= 2)
                    tcs.TrySetResult(true);
            }
        };
        engine.Start();

        // Initial fill → Buffering → Synchronized.
        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => engine.State == PlaybackState.Error, TimeSpan.FromSeconds(3));

        // Refill → Buffering → Synchronized again — needs StartupBufferFrames.
        for (int i = 8; i < 16; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await engine.DisposeAsync();

        lock (states)
        {
            // The critical invariant: no two consecutive "synchronized" events.
            // Such a pair would indicate a spurious notification fired while the
            // engine was still in Buffering state during error recovery.
            for (int i = 1; i < states.Count; i++)
                Assert.False(
                    states[i] == "synchronized" && states[i - 1] == "synchronized",
                    $"Spurious consecutive 'synchronized' at index {i}: [{string.Join(", ", states)}]");

            // Must have seen at least: synchronized, error, synchronized.
            Assert.True(states.Count >= 3, $"Too few state events: [{string.Join(", ", states)}]");
            Assert.Equal("synchronized", states[0]);
            Assert.Contains("error",        states);
        }
    }

    [Fact]
    public async Task LateFrames_DroppedBeforePlaybackStart()
    {
        // Enqueue 8 frames whose timestamps start well in the past.
        // serverNow = 0 (FakeClock=0, clockOffset=0).
        // aheadTargetUs = TargetAheadMs * 1000 = 50_000 µs.
        // Transition fires at StartupBufferFrames=8.
        // Frames with ts < 50_000 are "past their dequeue window":
        //   i=0: ts=0, i=1: ts=20_000, i=2: ts=40_000  → dropped (3 frames)
        //   i=3: ts=60_000 … i=7: ts=140_000           → kept   (5 frames)
        // Guard keeps at least MinBufferFrames=4, so all 3 can be dropped (8-3=5≥4).
        var (engine, renderer, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero);
        engine.Start();

        for (int i = 0; i < 8; i++)
            engine.Enqueue(i * 20_000L, Frame());

        // Wait until the engine drains the kept frames and underruns.
        await PollUntil(() => engine.State == PlaybackState.Error, TimeSpan.FromSeconds(3));

        // 5 frames should have been rendered (3 were silently dropped).
        Assert.Equal(5, renderer.Written.Count);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task AheadBuffer_IncreasesTarget_WhenConsistentlyBehind()
    {
        // FakeWasapiRenderer.BufferedBytes defaults to 0, which is below LowWaterMs.
        // After BehindThreshold (5) consecutive frames the target should double.
        var (engine, _, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero);
        engine.Start();

        // Enqueue enough frames to stay in Synchronized through the BehindThreshold.
        for (int i = 0; i < 10; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => engine.AheadTargetMs > PlaybackEngine.TargetAheadMs, TimeSpan.FromSeconds(2));

        Assert.True(engine.AheadTargetMs > PlaybackEngine.TargetAheadMs,
            $"Expected ahead target to exceed {PlaybackEngine.TargetAheadMs} ms, but it is {engine.AheadTargetMs} ms");
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task AheadBuffer_DoesNotIncrease_WhenBufferHealthy()
    {
        // With BufferedBytes held above LowWaterMs, the behind counter never fires.
        var (engine, renderer, clock) = Build();

        int bytesPerMs = renderer.SampleRate * renderer.Channels * sizeof(float) / 1000;
        renderer.BufferedBytesValue = PlaybackEngine.LowWaterMs * bytesPerMs + 1;

        engine.UpdateClockOffset(TimeSpan.Zero);
        engine.Start();

        for (int i = 0; i < 10; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        // Wait until the engine leaves Synchronized (either error or frames consumed).
        await PollUntil(() => engine.State != PlaybackState.Synchronized, TimeSpan.FromSeconds(3));

        Assert.Equal(PlaybackEngine.TargetAheadMs, engine.AheadTargetMs);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task AheadBuffer_RecoversTowardNominal_AfterSustainingHealthyBuffer()
    {
        // Phase 1: let the target elevate (BufferedBytes = 0).
        // Phase 2: switch to a healthy buffer level and verify the target steps back down.
        var (engine, renderer, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero);
        engine.Start();

        long ts = clock.UtcNowMicroseconds;

        for (int i = 0; i < 10; i++)
            engine.Enqueue(ts + i * 20_000L, Frame());

        await PollUntil(() => engine.AheadTargetMs > PlaybackEngine.TargetAheadMs, TimeSpan.FromSeconds(2));
        int elevated = engine.AheadTargetMs;

        // Phase 2: healthy buffer level, engine will be in Error and refill.
        int bytesPerMs = renderer.SampleRate * renderer.Channels * sizeof(float) / 1000;
        renderer.BufferedBytesValue = PlaybackEngine.LowWaterMs * bytesPerMs + 1;

        ts += 10 * 20_000L;
        for (int i = 0; i < PlaybackEngine.RecoveryFrameCount + 10; i++)
            engine.Enqueue(ts + i * 20_000L, Frame());

        await PollUntil(() => engine.AheadTargetMs < elevated, TimeSpan.FromSeconds(3));

        Assert.True(engine.AheadTargetMs < elevated,
            $"Expected ahead target to decrease from {elevated} ms, but it is {engine.AheadTargetMs} ms");
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Resampler_Skipped_WhenFilterUnhealthy()
    {
        // UpdateClockOffset (the legacy entrypoint) sets σ_offset = +∞, which trips the
        // OffsetGateStdDevUs gate. The render loop must skip resampling entirely in that
        // mode, leaving LastRateRatio at exactly 1.0.
        var (engine, renderer, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero);
        engine.Start();

        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => renderer.Written.Count > 0, TimeSpan.FromSeconds(2));

        Assert.Equal(1.0, engine.LastRateRatio);
        Assert.Equal(0,   engine.RateRatioClampHitCount);
        await engine.DisposeAsync();
    }

    // ── ComputeRateRatio (pure control-loop) ──────────────────────────────────
    //
    // These tests exercise the rate-ratio control loop directly. The end-to-end
    // engine tests above can't reliably observe the loop because FakeClock doesn't
    // advance during the precise-pacing wait, which makes per-frame trim swamp the
    // baseline; a pure unit test gives deterministic inputs.

    private const double GoodOffsetSigmaUs = 100.0;   // « OffsetGateStdDevUs (5000)
    private const double GoodDriftSigma    = 1.0;     // « DriftTrustedStdDevUsPerS (50)

    [Fact]
    public void ComputeRateRatio_PositiveDrift_ProducesRatioBelowOne()
    {
        // Server clock pulling ahead → client must speed up → ratio < 1.
        // Steady-state schedule offset = -aheadTarget so trim is zero and baseline drives.
        var step = PlaybackEngine.ComputeRateRatio(
            scheduleOffsetMs:  -50.0,    // exactly at precise-pacing target
            aheadTargetMs:     50,
            frameDurationMs:   20.0,
            driftUsPerS:       100.0,    // +100 ppm
            offsetStdDevUs:    GoodOffsetSigmaUs,
            driftStdDevUsPerS: GoodDriftSigma,
            previousSmoothedRatio: 1.0);

        Assert.False(step.ResamplerSkipped);
        Assert.True(step.DriftTrusted);
        Assert.Equal(0.999_900, step.BaselineRatio, precision: 6);
        Assert.True(step.RateRatio < 1.0, $"got {step.RateRatio:F6}");
    }

    [Fact]
    public void ComputeRateRatio_NegativeDrift_ProducesRatioAboveOne()
    {
        var step = PlaybackEngine.ComputeRateRatio(
            scheduleOffsetMs:  -50.0,
            aheadTargetMs:     50,
            frameDurationMs:   20.0,
            driftUsPerS:       -100.0,   // server clock falling behind
            offsetStdDevUs:    GoodOffsetSigmaUs,
            driftStdDevUsPerS: GoodDriftSigma,
            previousSmoothedRatio: 1.0);

        Assert.True(step.RateRatio > 1.0, $"got {step.RateRatio:F6}");
    }

    [Fact]
    public void ComputeRateRatio_HighOffsetSigma_SkipsResampler()
    {
        var step = PlaybackEngine.ComputeRateRatio(
            scheduleOffsetMs:  -50.0,
            aheadTargetMs:     50,
            frameDurationMs:   20.0,
            driftUsPerS:       100.0,
            offsetStdDevUs:    50_000.0,  // » OffsetGateStdDevUs
            driftStdDevUsPerS: GoodDriftSigma,
            previousSmoothedRatio: 0.9995);

        Assert.True(step.ResamplerSkipped);
        Assert.Equal(1.0, step.RateRatio);
        Assert.Equal(1.0, step.SmoothedRatio); // state reset on gate trip
    }

    [Fact]
    public void ComputeRateRatio_HighDriftSigma_FallsBackToBaselineOne()
    {
        var step = PlaybackEngine.ComputeRateRatio(
            scheduleOffsetMs:  -50.0,
            aheadTargetMs:     50,
            frameDurationMs:   20.0,
            driftUsPerS:       5000.0,   // big drift, but…
            offsetStdDevUs:    GoodOffsetSigmaUs,
            driftStdDevUsPerS: 1000.0,   // …σ_drift too high → ignore it
            previousSmoothedRatio: 1.0);

        Assert.False(step.DriftTrusted);
        Assert.Equal(1.0, step.BaselineRatio);
        Assert.Equal(1.0, step.RateRatio); // EWMA on (target=1.0, prev=1.0) = 1.0
    }

    [Fact]
    public void ComputeRateRatio_PerFrameSlewLimit_CapsDelta()
    {
        // Huge target swing; smoothed ratio must move by at most MaxRatioDeltaPerFrame (50 ppm).
        var step = PlaybackEngine.ComputeRateRatio(
            scheduleOffsetMs:  +500.0,   // wildly late → strong negative trim
            aheadTargetMs:     50,
            frameDurationMs:   20.0,
            driftUsPerS:       0.0,
            offsetStdDevUs:    GoodOffsetSigmaUs,
            driftStdDevUsPerS: GoodDriftSigma,
            previousSmoothedRatio: 1.0);

        // Bounded movement: at most 50 ppm per frame.
        Assert.InRange(step.SmoothedRatio, 1.0 - 50e-6 - 1e-12, 1.0 + 1e-12);
    }

    [Fact]
    public void ComputeRateRatio_HardClamp_PinnedAtBoundary_AndCounted()
    {
        // Pretend the previous smoothed ratio was already below the floor; clamp must pull it
        // back to exactly 1 - MaxRatioDeviation and report saturation.
        var step = PlaybackEngine.ComputeRateRatio(
            scheduleOffsetMs:  +500.0,    // negative trim pushes target way below floor
            aheadTargetMs:     50,
            frameDurationMs:   20.0,
            driftUsPerS:       0.0,
            offsetStdDevUs:    GoodOffsetSigmaUs,
            driftStdDevUsPerS: GoodDriftSigma,
            previousSmoothedRatio: 0.9998);  // already at the floor

        Assert.True(step.ClampSaturated);
        Assert.Equal(1.0 - 0.0002, step.RateRatio, precision: 9);
    }

    [Fact]
    public async Task Resampler_Skipped_AcrossFullPipeline_WhenFilterUnhealthy_KeepsRatioAtOne()
    {
        // End-to-end pendant of the gate test: UpdateClockState with a huge σ_offset
        // must keep LastRateRatio at exactly 1.0 even after many frames render.
        var (engine, renderer, clock) = Build();
        engine.UpdateClockState(
            offset:            TimeSpan.Zero,
            driftUsPerS:       5000.0,
            offsetStdDevUs:    50_000.0,    // » OffsetGateStdDevUs
            driftStdDevUsPerS: GoodDriftSigma);
        engine.Start();

        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => renderer.Written.Count > 0, TimeSpan.FromSeconds(2));

        Assert.Equal(1.0, engine.LastRateRatio);
        await engine.DisposeAsync();
    }

    [Fact]
    public void Enqueue_WhilePaused_DropsFrames()
    {
        // Server keeps streaming for ~1 RTT after the client sends pause. If the engine
        // accepted those frames, the next Resume would see a buffer full of pre-pause
        // far-future timestamps and transition to Synchronized with a ~1 s head offset
        // that the resampler cannot close — leading to underrun.
        var (engine, _, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.Zero);

        engine.Pause();
        for (int i = 0; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        Assert.Equal(0, engine.BufferedFrameCount);
    }

    [Fact]
    public async Task DisposeAsync_StopsEngine()
    {
        var (engine, renderer, _) = Build();
        engine.Start();
        await engine.DisposeAsync();
        Assert.False(renderer.IsRunning);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static async Task PollUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }
}
