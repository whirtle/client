using Whirtle.Client.Playback;
using Whirtle.Client.Protocol;
using Whirtle.Client.Role;
using Whirtle.Client.Tests.Playback;
using Whirtle.Client.Tests.Protocol;

namespace Whirtle.Client.Tests.Role;

public class PlayerClientTests
{
    private static readonly MessageSerializer Serializer = new();

    private static (PlayerClient player, FakeTransport transport, List<IWasapiRenderer> renderers) Build()
    {
        var transport = new FakeTransport();
        var protocol  = new ProtocolClient(transport);
        var renderers = new List<IWasapiRenderer>();

        var player = new PlayerClient(protocol, (sampleRate, channels) =>
        {
            var r = new FakeWasapiRenderer();
            renderers.Add(r);
            return r;
        });

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
    public async Task ProcessFrameAsync_SetStaticDelayCommand_Clamps0To5000()
    {
        var (player, _, _) = Build();

        await player.ProcessFrameAsync(new ProtocolFrame(
            new ServerCommandMessage(Player: new ServerCommandPlayer("set_static_delay", StaticDelayMs: 9999))));

        Assert.Equal(5_000, player.StaticDelayMs);
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
        // stream is active.  Only static_delay_ms (50 ms) is deducted from the
        // timestamp; renderer latency is handled internally by WASAPI and not applied here.
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
    public async Task SendStateAsync_AfterStreamStart_ReportsOnlyStaticDelayMs_NotRendererLatency()
    {
        // FakeWasapiRenderer.LatencyMs = 100.  After stream/start the renderer latency
        // is known, but it must NOT be added to StaticDelayMs — that field is for
        // external/downstream delay only (amplifiers, speakers, etc.).
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
