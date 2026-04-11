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
        var clock     = new FakeClock(DateTime.UtcNow.Ticks);
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
        engine.Start();

        for (int i = 0; i < 4; i++)
            engine.Enqueue(clock.UtcNowTicks + i * TimeSpan.FromMilliseconds(20).Ticks, Frame());

        await PollUntil(() => engine.State == PlaybackState.Synchronized, TimeSpan.FromSeconds(2));

        Assert.Equal(PlaybackState.Synchronized, engine.State);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task State_TransitionsToError_OnUnderrun()
    {
        var (engine, _, clock) = Build();
        engine.Start();

        // Provide just enough to reach Synchronized…
        for (int i = 0; i < 4; i++)
            engine.Enqueue(clock.UtcNowTicks + i * TimeSpan.FromMilliseconds(20).Ticks, Frame());

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
        engine.Start();

        for (int i = 0; i < 4; i++)
            engine.Enqueue(clock.UtcNowTicks + i * TimeSpan.FromMilliseconds(20).Ticks, Frame());

        await PollUntil(() => engine.State == PlaybackState.Synchronized, TimeSpan.FromSeconds(2));
        await PollUntil(() => engine.State == PlaybackState.Error, TimeSpan.FromSeconds(3));

        Assert.True(renderer.Muted);
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
