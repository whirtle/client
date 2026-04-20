using Whirtle.Client.Protocol;

namespace Whirtle.Client.Tests.Protocol;

public class ReceiveAllAsyncTests
{
    private static (ProtocolClient client, FakeTransport transport) Build()
    {
        var transport = new FakeTransport();
        return (new ProtocolClient(transport), transport);
    }

    [Fact]
    public async Task ReceiveAllAsync_YieldsProtocolFrames()
    {
        var (client, transport) = Build();
        transport.EnqueueInbound(MessageSerializer.Serialize(new ServerStateMessage()));
        transport.EnqueueInbound(MessageSerializer.Serialize(new GroupUpdateMessage("playing", "g1")));
        transport.CloseInbound();

        var frames = new List<IncomingFrame>();
        await foreach (var f in client.ReceiveAllAsync())
            frames.Add(f);

        Assert.Equal(2, frames.Count);
        Assert.IsType<ServerStateMessage>(((ProtocolFrame)frames[0]).Message);
        Assert.IsType<GroupUpdateMessage>(((ProtocolFrame)frames[1]).Message);
    }

    [Fact]
    public async Task ReceiveAllAsync_YieldsArtworkFrame_ForBinaryData()
    {
        var (client, transport) = Build();
        // Artwork binary frame: type byte (8) + 8-byte timestamp + JPEG bytes.
        byte[] jpegBytes   = [0xFF, 0xD8, 0xAA, 0xBB];
        byte[] tsBytes     = [0, 0, 0, 0, 0, 0, 0, 0]; // timestamp = 0
        transport.EnqueueInbound([8, .. tsBytes, .. jpegBytes]);
        transport.CloseInbound();

        var frames = new List<IncomingFrame>();
        await foreach (var f in client.ReceiveAllAsync())
            frames.Add(f);

        Assert.Single(frames);
        var art = Assert.IsType<ArtworkFrame>(frames[0]);
        Assert.Equal("image/jpeg", art.MimeType);
        Assert.Equal(0,            art.Channel);
        Assert.Equal(jpegBytes,    art.Data);
    }

    [Fact]
    public async Task ReceiveAllAsync_SkipsEmptyFrames()
    {
        var (client, transport) = Build();
        transport.EnqueueInbound([]);
        transport.EnqueueInbound(MessageSerializer.Serialize(new ServerStateMessage()));
        transport.CloseInbound();

        var frames = new List<IncomingFrame>();
        await foreach (var f in client.ReceiveAllAsync())
            frames.Add(f);

        Assert.Single(frames);
    }

    [Fact]
    public async Task ReceiveAsync_SkipsBinaryFrames()
    {
        var (client, transport) = Build();
        transport.EnqueueInbound([8, 0xFF, 0xD8, 0x00]); // binary — skipped
        transport.EnqueueInbound(MessageSerializer.Serialize(new ServerStateMessage()));
        transport.CloseInbound();

        var messages = new List<Message>();
        await foreach (var m in client.ReceiveAsync())
            messages.Add(m);

        Assert.Single(messages);
        Assert.IsType<ServerStateMessage>(messages[0]);
    }

    [Fact]
    public async Task ReceiveAllAsync_YieldsServerStateAsProtocolFrame()
    {
        var (client, transport) = Build();
        var msg = new ServerStateMessage(
            Metadata: new ServerMetadataState(Title: "Track", Artist: "Band"));
        transport.EnqueueInbound(MessageSerializer.Serialize(msg));
        transport.CloseInbound();

        var frames = new List<IncomingFrame>();
        await foreach (var f in client.ReceiveAllAsync())
            frames.Add(f);

        var pf  = Assert.IsType<ProtocolFrame>(frames[0]);
        var ssm = Assert.IsType<ServerStateMessage>(pf.Message);
        Assert.Equal("Track", ssm.Metadata!.Title.Value);
    }

    [Fact]
    public async Task ReceiveAllAsync_YieldsClientCommandRoundtrip()
    {
        var (client, transport) = Build();
        var cmd = new ClientCommandMessage(new ClientControllerCommand("volume", Volume: 50));
        transport.EnqueueInbound(MessageSerializer.Serialize(cmd));
        transport.CloseInbound();

        await foreach (var f in client.ReceiveAllAsync())
        {
            var pf = Assert.IsType<ProtocolFrame>(f);
            var c  = Assert.IsType<ClientCommandMessage>(pf.Message);
            Assert.Equal("volume", c.Controller!.Command);
            Assert.Equal(50,       c.Controller.Volume);
            return;
        }

        Assert.Fail("No frame received.");
    }

    // ── Connection-loss / error propagation ───────────────────────────────────

    [Fact]
    public async Task ReceiveAllAsync_PropagatesTransportError()
    {
        var (client, transport) = Build();
        transport.CompleteWithError(new IOException("socket reset"));

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in client.ReceiveAllAsync()) { }
        });
    }

    [Fact]
    public async Task ReceiveAllAsync_DeliversFramesBeforeTransportError()
    {
        var (client, transport) = Build();
        transport.EnqueueInbound(MessageSerializer.Serialize(new ServerStateMessage()));
        transport.EnqueueInbound(MessageSerializer.Serialize(new GroupUpdateMessage("playing", "g1")));
        transport.CompleteWithError(new IOException("socket reset"));

        var received = new List<IncomingFrame>();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var f in client.ReceiveAllAsync())
                received.Add(f);
        });

        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task ReceiveAsync_PropagatesTransportError()
    {
        var (client, transport) = Build();
        transport.CompleteWithError(new IOException("socket reset"));

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in client.ReceiveAsync()) { }
        });
    }
}
