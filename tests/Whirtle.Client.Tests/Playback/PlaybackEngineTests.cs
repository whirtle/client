using Whirtle.Client.Codec;
using Whirtle.Client.Playback;
using Whirtle.Client.Protocol;
using Whirtle.Client.Tests.Clock;
using Whirtle.Client.Tests.Protocol;

namespace Whirtle.Client.Tests.Playback;

public class PlaybackEngineTests
{
    private static AudioFrame Frame(int samples = 960) =>
        new(new short[samples * 2], 48_000, 2); // 20 ms stereo

    private static (PlaybackEngine engine, FakeWasapiRenderer renderer, FakeClock clock)
        Build()
    {
        var renderer  = new FakeWasapiRenderer();
        var transport = new FakeTransport();
        var protocol  = new ProtocolClient(transport);
        var clock     = new FakeClock();
        var engine    = new PlaybackEngine(renderer, protocol, clock);
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
        engine.StatusChanged += (state, _) => { if (state == PlaybackState.Synchronized) reachedSynchronized = true; };

        engine.Start();

        for (int i = 0; i < 4; i++)
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
        for (int i = 0; i < 4; i++)
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

        for (int i = 0; i < 4; i++)
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

        for (int i = 0; i < 4; i++)
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
        engine.StatusChanged += (state, _) => { if (state == PlaybackState.Synchronized) reachedSynchronized = true; };

        engine.Start();

        for (int i = 0; i < 4; i++)
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

        for (int i = 0; i < 4; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => renderer.Written.Count > 0, TimeSpan.FromSeconds(2));

        Assert.NotEmpty(renderer.Written);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Drift_BelowThreshold_RendersNormally()
    {
        // 100 ms drift is below MaxDriftMs (200 ms): engine should render all frames.
        var (engine, renderer, clock) = Build();
        engine.UpdateClockOffset(TimeSpan.FromMilliseconds(100));
        engine.Start();

        const int frameCount = 4;
        for (int i = 0; i < frameCount; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => renderer.Written.Count >= frameCount, TimeSpan.FromSeconds(2));

        Assert.Equal(frameCount, renderer.Written.Count);
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
        engine.StatusChanged += (state, _) =>
        {
            if (state == PlaybackState.Synchronized)
                synchronizedCount++;
        };

        engine.Start();

        for (int i = 0; i < 4; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => engine.State == PlaybackState.Error, TimeSpan.FromSeconds(3));

        // Refill to trigger recovery.
        for (int i = 4; i < 8; i++)
            engine.Enqueue(clock.UtcNowMicroseconds + i * 20_000L, Frame());

        await PollUntil(() => synchronizedCount >= 2, TimeSpan.FromSeconds(3));

        Assert.True(synchronizedCount >= 2);
        await engine.DisposeAsync();
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
