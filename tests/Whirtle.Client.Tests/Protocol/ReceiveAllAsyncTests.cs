using Whirtle.Client.Protocol;

namespace Whirtle.Client.Tests.Protocol;

public class ReceiveAllAsyncTests
{
    private static readonly MessageSerializer Serializer = new();

    private static (ProtocolClient client, FakeTransport transport) Build()
    {
        var transport = new FakeTransport();
        return (new ProtocolClient(transport), transport);
    }

    [Fact]
    public async Task ReceiveAllAsync_YieldsProtocolFrames()
    {
        var (client, transport) = Build();
        transport.EnqueueInbound(Serializer.Serialize(new PingMessage()));
        transport.EnqueueInbound(Serializer.Serialize(new PongMessage()));
        transport.CloseInbound();

        var frames = new List<IncomingFrame>();
        await foreach (var f in client.ReceiveAllAsync())
            frames.Add(f);

        Assert.Equal(2, frames.Count);
        Assert.IsType<PingMessage>(((ProtocolFrame)frames[0]).Message);
        Assert.IsType<PongMessage>(((ProtocolFrame)frames[1]).Message);
    }

    [Fact]
    public async Task ReceiveAllAsync_StopsOnGoodbye()
    {
        var (client, transport) = Build();
        transport.EnqueueInbound(Serializer.Serialize(new PingMessage()));
        transport.EnqueueInbound(Serializer.Serialize(new GoodbyeMessage("done")));
        transport.EnqueueInbound(Serializer.Serialize(new PongMessage())); // never yielded

        var frames = new List<IncomingFrame>();
        await foreach (var f in client.ReceiveAllAsync())
            frames.Add(f);

        Assert.Single(frames); // only PingMessage
    }

    [Fact]
    public async Task ReceiveAllAsync_YieldsArtworkFrame_ForBinaryData()
    {
        var (client, transport) = Build();
        byte[] jpegBytes = [0xFF, 0xD8, 0xAA, 0xBB];
        transport.EnqueueInbound(jpegBytes);
        transport.CloseInbound();

        var frames = new List<IncomingFrame>();
        await foreach (var f in client.ReceiveAllAsync())
            frames.Add(f);

        Assert.Single(frames);
        var art = Assert.IsType<ArtworkFrame>(frames[0]);
        Assert.Equal("image/jpeg", art.MimeType);
        Assert.Equal(jpegBytes, art.Data);
    }

    [Fact]
    public async Task ReceiveAllAsync_SkipsEmptyFrames()
    {
        var (client, transport) = Build();
        transport.EnqueueInbound([]);                                     // empty — skipped
        transport.EnqueueInbound(Serializer.Serialize(new PingMessage())); // yielded
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
        byte[] binary = [0xFF, 0xD8, 0x00];
        transport.EnqueueInbound(binary);                                 // binary — skipped
        transport.EnqueueInbound(Serializer.Serialize(new PingMessage())); // yielded
        transport.CloseInbound();

        var messages = new List<Message>();
        await foreach (var m in client.ReceiveAsync())
            messages.Add(m);

        Assert.Single(messages);
        Assert.IsType<PingMessage>(messages[0]);
    }

    [Fact]
    public async Task ReceiveAllAsync_YieldsNowPlayingAsProtocolFrame()
    {
        var (client, transport) = Build();
        var msg = new NowPlayingMessage("Track", "Band", "Album", 180.0, 15.0);
        transport.EnqueueInbound(Serializer.Serialize(msg));
        transport.CloseInbound();

        var frames = new List<IncomingFrame>();
        await foreach (var f in client.ReceiveAllAsync())
            frames.Add(f);

        var pf = Assert.IsType<ProtocolFrame>(frames[0]);
        var np = Assert.IsType<NowPlayingMessage>(pf.Message);
        Assert.Equal("Track", np.Title);
        Assert.Equal(180.0,   np.DurationSeconds);
    }

    [Fact]
    public async Task ReceiveAllAsync_YieldsClientCommandRoundtrip()
    {
        var (client, transport) = Build();
        var cmd = new ClientCommandMessage("volume", 0.5);
        transport.EnqueueInbound(Serializer.Serialize(cmd));
        transport.CloseInbound();

        await foreach (var f in client.ReceiveAllAsync())
        {
            var pf = Assert.IsType<ProtocolFrame>(f);
            var c  = Assert.IsType<ClientCommandMessage>(pf.Message);
            Assert.Equal("volume", c.Command);
            Assert.Equal(0.5,      c.Value);
            return;
        }

        Assert.Fail("No frame received.");
    }
}
