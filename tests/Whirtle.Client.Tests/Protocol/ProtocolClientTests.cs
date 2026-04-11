using Whirtle.Client.Protocol;

namespace Whirtle.Client.Tests.Protocol;

public class ProtocolClientTests
{
    private static readonly MessageSerializer Serializer = new();

    private static (ProtocolClient client, FakeTransport transport) Build()
    {
        var transport = new FakeTransport();
        return (new ProtocolClient(transport), transport);
    }

    [Fact]
    public async Task HandshakeAsync_SendsHello_ReturnsWelcome()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));
        transport.EnqueueInbound(Serializer.Serialize(new WelcomeMessage("s-1", "1.0")));

        var welcome = await client.HandshakeAsync("1.0");

        Assert.Equal("s-1", welcome.SessionId);
        Assert.Equal("1.0", welcome.ServerVersion);
        var sent = (HelloMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("1.0", sent.Version);
    }

    [Fact]
    public async Task HandshakeAsync_ServerSendsError_ThrowsHandshakeException()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));
        transport.EnqueueInbound(Serializer.Serialize(new ErrorMessage("auth_failed", "Bad token")));

        var ex = await Assert.ThrowsAsync<HandshakeException>(() => client.HandshakeAsync("1.0"));

        Assert.Equal("auth_failed", ex.Code);
    }

    [Fact]
    public async Task HandshakeAsync_ConnectionClosedBeforeWelcome_ThrowsHandshakeException()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));
        transport.CloseInbound();

        var ex = await Assert.ThrowsAsync<HandshakeException>(() => client.HandshakeAsync("1.0"));

        Assert.Equal("connection_closed", ex.Code);
    }

    [Fact]
    public async Task HandshakeAsync_UnexpectedMessage_ThrowsHandshakeException()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));
        transport.EnqueueInbound(Serializer.Serialize(new PingMessage()));

        var ex = await Assert.ThrowsAsync<HandshakeException>(() => client.HandshakeAsync("1.0"));

        Assert.Equal("unexpected_message", ex.Code);
    }

    [Fact]
    public async Task ReceiveAsync_YieldsMessages()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));
        transport.EnqueueInbound(Serializer.Serialize(new PingMessage()));
        transport.EnqueueInbound(Serializer.Serialize(new PongMessage()));
        transport.EnqueueInbound(Serializer.Serialize(new GoodbyeMessage("normal")));

        var received = new List<Message>();
        await foreach (var msg in client.ReceiveAsync())
            received.Add(msg);

        Assert.Equal(2, received.Count);
        Assert.IsType<PingMessage>(received[0]);
        Assert.IsType<PongMessage>(received[1]);
    }

    [Fact]
    public async Task ReceiveAsync_StopsOnGoodbye()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));
        transport.EnqueueInbound(Serializer.Serialize(new GoodbyeMessage("normal")));

        var count = 0;
        await foreach (var _ in client.ReceiveAsync())
            count++;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task SendAsync_SerializesAndForwardsToTransport()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));

        await client.SendAsync(new PingMessage());

        Assert.Single(transport.Sent);
        Assert.IsType<PingMessage>(Serializer.Deserialize(transport.Sent[0]));
    }

    [Fact]
    public async Task DisconnectAsync_SendsGoodbyeAndDisconnects()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));

        await client.DisconnectAsync("shutdown");

        Assert.False(transport.IsConnected);
        var sent = (GoodbyeMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("shutdown", sent.Reason);
    }
}
