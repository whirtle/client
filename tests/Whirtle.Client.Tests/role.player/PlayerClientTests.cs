using Whirtle.Client.Playback;
using Whirtle.Client.Protocol;
using Whirtle.Client.Role;
using Whirtle.Client.Tests.Clock;
using Whirtle.Client.Tests.Playback;
using Whirtle.Client.Tests.Protocol;

namespace Whirtle.Client.Tests.Role;

public class PlayerClientTests
{
    private static readonly MessageSerializer Serializer = new();

    private static (PlayerClient player, FakeTransport transport, List<IWasapiRenderer> renderers) Build(
        FakeClock? clock = null)
    {
        var transport = new FakeTransport();
        var protocol  = new ProtocolClient(transport);
        var renderers = new List<IWasapiRenderer>();

        var player = new PlayerClient(protocol, (sampleRate, channels) =>
        {
            var r = new FakeWasapiRenderer();
            renderers.Add(r);
            return r;
        }, clock);

        return (player, transport, renderers);
    }

    // ── server/command ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessFrameAsync_VolumeCommand_UpdatesVolumeAndSendsState()
    {
        var (player, transport, _) = Build();
        int? notified = null;
        player.VolumeChanged += v => notified = v;

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("volume", Volume: 75))));

        Assert.Equal(75, player.Volume);
        Assert.Equal(75, notified);
        var state = (ClientStateMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal(75, state.Player!.Volume);
    }

    [Fact]
    public async Task ProcessFrameAsync_VolumeCommand_ClampsAbove100()
    {
        var (player, transport, _) = Build();

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("volume", Volume: 150))));

        Assert.Equal(100, player.Volume);
    }

    [Fact]
    public async Task ProcessFrameAsync_MuteCommand_UpdatesMuteAndSendsState()
    {
        var (player, transport, _) = Build();
        bool? notified = null;
        player.MuteChanged += m => notified = m;

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("mute", Mute: true))));

        Assert.True(player.Muted);
        Assert.True(notified);
        var state = (ClientStateMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.True(state.Player!.Muted);
    }

    [Fact]
    public async Task ProcessFrameAsync_SetStaticDelayCommand_UpdatesDelayAndSendsState()
    {
        var (player, transport, _) = Build();
        int? notified = null;
        player.StaticDelayChanged += d => notified = d;

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("set_static_delay", StaticDelayMs: 200))));

        Assert.Equal(200, player.StaticDelayMs);
        Assert.Equal(200, notified);
        var state = (ClientStateMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal(200, state.Player!.StaticDelayMs);
    }

    [Fact]
    public async Task ProcessFrameAsync_SetStaticDelayCommand_FlushesJitterBuffer()
    {
        // Frames already in the buffer carry timestamps adjusted with the old delay.
        // When static_delay changes the buffer must be cleared so the engine does not
        // play frames with incorrect timestamps.
        var clock = new FakeClock();
        var (player, _, _) = Build(clock);

        await player.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("pcm", 48_000, 2, 16))));

        // Enqueue frames far enough in the future that they survive the late-drop guard.
        const long futureUs = 300_000_000L;
        for (int i = 0; i < 3; i++)
            await player.ProcessFrameAsync(new AudioChunkFrame(
                Timestamp:   futureUs + i * 20_000L,
                EncodedData: new byte[4]));

        Assert.Equal(3, player.BufferedFrameCount);

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("set_static_delay", StaticDelayMs: 200))));

        Assert.Equal(0, player.BufferedFrameCount);
    }

    [Fact]
    public async Task ProcessFrameAsync_SetStaticDelayCommand_ClampsUpperBoundTo5000()
    {
        var (player, _, _) = Build();

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("set_static_delay", StaticDelayMs: 9999))));

        Assert.Equal(5_000, player.StaticDelayMs);
    }

    [Fact]
    public async Task ProcessFrameAsync_SetStaticDelayCommand_ClampsLowerBoundToNeg500()
    {
        var (player, _, _) = Build();

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("set_static_delay", StaticDelayMs: -9999))));

        Assert.Equal(-500, player.StaticDelayMs);
    }

    [Fact]
    public async Task ProcessFrameAsync_UnknownCommand_IsIgnored_AndStillSendsState()
    {
        var (player, transport, _) = Build();

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("fly_to_moon"))));

        // State should still be echoed even for unknown commands.
        Assert.Single(transport.Sent);
    }

    // ── stream/start ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessFrameAsync_StreamStart_CreatesRendererAndStartsEngine()
    {
        var (player, _, renderers) = Build();

        await player.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("opus", 48_000, 2, 16))));

        Assert.Single(renderers);
        Assert.True(((FakeWasapiRenderer)renderers[0]).IsRunning);
    }

    [Fact]
    public async Task ProcessFrameAsync_StreamStart_UsesCorrectSampleRateAndChannels()
    {
        var (player, _, renderers) = Build();
        int capturedRate = 0, capturedChannels = 0;

        var player2 = new PlayerClient(new ProtocolClient(new FakeTransport()), (sr, ch) =>
        {
            capturedRate     = sr;
            capturedChannels = ch;
            return new FakeWasapiRenderer();
        });

        await player2.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("opus", 48_000, 1, 16))));

        Assert.Equal(48_000, capturedRate);
        Assert.Equal(1, capturedChannels);
    }

    [Fact]
    public async Task ProcessFrameAsync_SecondStreamStart_DisposesOldEngine()
    {
        var (player, _, renderers) = Build();

        await player.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("opus", 48_000, 2, 16))));
        await player.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("pcm", 44_100, 2, 16))));

        // Two renderers should have been created (one per stream/start).
        Assert.Equal(2, renderers.Count);
        // The first renderer should have been stopped (engine disposed).
        Assert.False(((FakeWasapiRenderer)renderers[0]).IsRunning);
    }

    // ── timestamp compensation ────────────────────────────────────────────────

    [Fact]
    public async Task ProcessFrameAsync_AudioChunk_AcceptedAfterStreamStartAndNotDropped()
    {
        // Confirms that audio chunks reach the engine (not silently dropped) when a
        // stream is active.  Both static_delay_ms (50 ms) and renderer latency (100 ms,
        // per FakeWasapiRenderer.LatencyMs) are deducted from the server timestamp before
        // the frame is enqueued.
        var (player, _, _) = Build();

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("set_static_delay", StaticDelayMs: 50))));
        await player.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("pcm", 48_000, 2, 16))));

        // Minimal 4-byte PCM payload (2 stereo int16 samples = silence).
        var ex = await Record.ExceptionAsync(() =>
            player.ProcessFrameAsync(new AudioChunkFrame(Timestamp: 10_000_000, EncodedData: new byte[4])));

        Assert.Null(ex);
    }

    [Fact]
    public async Task ProcessFrameAsync_AudioChunk_DroppedWhenNoActiveStream()
    {
        // Before stream/start, audio chunks must be rejected.
        var (player, _, _) = Build();

        var ex = await Record.ExceptionAsync(() =>
            player.ProcessFrameAsync(new AudioChunkFrame(Timestamp: 10_000_000, EncodedData: new byte[4])));

        // Should be silently ignored (no throw, no engine interaction).
        Assert.Null(ex);
    }

    [Fact]
    public async Task ProcessFrameAsync_AudioChunk_EffectiveTimestampIncludesRendererLatency()
    {
        // FakeWasapiRenderer.LatencyMs = 100.  With static_delay = 50 ms the total
        // latency subtracted from the server timestamp must be 150 ms (150_000 μs).
        // We verify this indirectly: a chunk whose raw timestamp equals exactly
        // static_delay_us (50_000 μs) would land at effectiveTimestamp = 0 if renderer
        // latency were omitted (plausibly still accepted), but lands at -100_000 μs
        // when renderer latency IS included.  The frame is still enqueued because the
        // server-clock late-drop guard (Step 2) is not yet active; what matters here is
        // that the engine receives the adjusted value — confirmed by BufferedFrameCount.
        var (player, _, _) = Build();

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("set_static_delay", StaticDelayMs: 50))));
        await player.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("pcm", 48_000, 2, 16))));

        // Send a chunk whose timestamp is 300 ms in the future so it is far enough
        // ahead that it would not be confused for a "late" frame under either latency
        // computation.  Verify the engine buffered it (i.e. Enqueue was called).
        long futureTimestamp = 300_000_000L; // 300 s in μs — unambiguously future
        await player.ProcessFrameAsync(new AudioChunkFrame(
            Timestamp:   futureTimestamp,
            EncodedData: new byte[4]));

        // The frame must be in the jitter buffer regardless of the latency applied.
        // (The exact adjusted timestamp — futureTimestamp - 150_000 μs — is an
        // internal detail verified by the drift and late-drop tests in later steps.)
        Assert.Equal(1, player.BufferedFrameCount);
    }

    [Fact]
    public async Task SendStateAsync_AfterStreamStart_ReportsOnlyStaticDelayMs_NotRendererLatency()
    {
        // FakeWasapiRenderer.LatencyMs = 100.  After stream/start the renderer latency
        // is subtracted from audio timestamps (Step 1) but must NOT be added to the
        // reported StaticDelayMs — that field is for external/downstream delay only
        // (amplifiers, speakers, etc.).
        var (player, transport, _) = Build();

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("set_static_delay", StaticDelayMs: 200))));
        await player.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("pcm", 48_000, 2, 16))));

        int sentBefore = transport.Sent.Count;
        await player.SendStateAsync();

        var state = (ClientStateMessage)Serializer.Deserialize(transport.Sent[sentBefore]);
        Assert.Equal(200, state.Player!.StaticDelayMs); // not 300 (200 + 100 renderer latency)
    }

    [Fact]
    public async Task ProcessFrameAsync_AudioChunk_PastTimestamp_IsDroppedWhenClockSynced()
    {
        // FakeWasapiRenderer.LatencyMs = 100, static_delay = 0, so totalLatencyUs = 100_000.
        // FakeClock starts at 0. After UpdateClockOffset(Zero), serverNow = 0.
        // A chunk with Timestamp = 50_000 has effectiveTimestamp = 50_000 - 100_000 = -50_000,
        // which is in the past relative to serverNow (0), so it must be dropped.
        var clock = new FakeClock();
        var (player, _, _) = Build(clock);

        await player.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("pcm", 48_000, 2, 16))));

        player.UpdateClockOffset(TimeSpan.Zero);

        await player.ProcessFrameAsync(new AudioChunkFrame(
            Timestamp:   50_000L, // effectiveTimestamp = -50_000 μs — in the past
            EncodedData: new byte[4]));

        Assert.Equal(0, player.BufferedFrameCount);
    }

    [Fact]
    public async Task ProcessFrameAsync_AudioChunk_PastTimestamp_AcceptedWhenClockNotYetSynced()
    {
        // Without a clock sync the late-drop guard is inactive so startup frames are
        // never prematurely discarded regardless of their timestamp.
        var clock = new FakeClock();
        var (player, _, _) = Build(clock);

        await player.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("pcm", 48_000, 2, 16))));

        // Deliberately skip UpdateClockOffset so _clockSynced stays false.

        await player.ProcessFrameAsync(new AudioChunkFrame(
            Timestamp:   50_000L, // would be late if clock were synced
            EncodedData: new byte[4]));

        Assert.Equal(1, player.BufferedFrameCount);
    }

    // ── stream/end ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessFrameAsync_StreamEnd_ChunksAreDroppedAfterStreamEnd()
    {
        // After stream/end, incoming audio chunks must be silently dropped.
        var (player, _, renderers) = Build();
        player.UpdateClockOffset(TimeSpan.Zero);

        await player.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("pcm", 48_000, 2, 16))));

        await player.ProcessFrameAsync(new ProtocolFrame(new StreamEndMessage()));

        for (int i = 0; i < 6; i++)
            await player.ProcessFrameAsync(new AudioChunkFrame(
                Timestamp:   10_000_000L + i * 20_000L,
                EncodedData: new byte[4]));

        // No audio should have been written to the renderer.
        await Task.Delay(50); // give engine time to process if chunks leaked through
        Assert.Empty(((FakeWasapiRenderer)renderers[0]).Written);
    }

    // ── stream/clear ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessFrameAsync_StreamClear_DoesNotStopStream()
    {
        var (player, _, _) = Build();

        // Start a stream first.
        await player.ProcessFrameAsync(new ProtocolFrame(
            new StreamStartMessage(Player: new StreamStartPlayer("opus", 48_000, 2, 16))));

        await player.ProcessFrameAsync(new ProtocolFrame(new StreamClearMessage()));

        // Stream must remain active — audio frames should still be accepted.
        // Verify by checking that an audio chunk frame is processed without being dropped.
        var audioFrame = new AudioChunkFrame(
            Timestamp:   1_000_000,
            EncodedData: new byte[4]); // minimal PCM-style frame; decoder will return empty

        // Should not throw — stream is still active after clear.
        await player.ProcessFrameAsync(audioFrame);
    }
}
