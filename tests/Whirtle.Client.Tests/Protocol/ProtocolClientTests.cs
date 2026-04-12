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
    public async Task HandshakeAsync_SendsClientHello_ReturnsServerHello()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));
        transport.EnqueueInbound(Serializer.Serialize(
            new ServerHelloMessage("srv-1", "Server", 1, ["metadata@v1"], "discovery")));

        var welcome = await client.HandshakeAsync("client-id", "Test Client");

        Assert.Equal("srv-1",     welcome.ServerId);
        Assert.Equal("discovery", welcome.ConnectionReason);

        var sent = (ClientHelloMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("client-id",   sent.ClientId);
        Assert.Equal("Test Client", sent.Name);
        Assert.Equal(1,             sent.Version);
    }

    [Fact]
    public async Task HandshakeAsync_ConnectionClosedBeforeServerHello_ThrowsHandshakeException()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));
        transport.CloseInbound();

        var ex = await Assert.ThrowsAsync<HandshakeException>(
            () => client.HandshakeAsync("id", "name"));

        Assert.Equal("connection_closed", ex.Code);
    }

    [Fact]
    public async Task HandshakeAsync_UnexpectedMessage_ThrowsHandshakeException()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));
        transport.EnqueueInbound(Serializer.Serialize(new GroupUpdateMessage("playing", "g-1")));

        var ex = await Assert.ThrowsAsync<HandshakeException>(
            () => client.HandshakeAsync("id", "name"));

        Assert.Equal("unexpected_message", ex.Code);
    }

    [Fact]
    public async Task ReceiveAsync_YieldsMessages()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));
        transport.EnqueueInbound(Serializer.Serialize(new ServerStateMessage()));
        transport.EnqueueInbound(Serializer.Serialize(new GroupUpdateMessage("playing", "g1")));
        transport.CloseInbound();

        var received = new List<Message>();
        await foreach (var msg in client.ReceiveAsync())
            received.Add(msg);

        Assert.Equal(2, received.Count);
        Assert.IsType<ServerStateMessage>(received[0]);
        Assert.IsType<GroupUpdateMessage>(received[1]);
    }

    [Fact]
    public async Task SendAsync_SerializesAndForwardsToTransport()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));

        await client.SendAsync(new ClientTimeMessage(12345L));

        Assert.Single(transport.Sent);
        var msg = (ClientTimeMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal(12345L, msg.ClientTransmitted);
    }

    [Fact]
    public async Task DisconnectAsync_SendsClientGoodbyeAndDisconnects()
    {
        var (client, transport) = Build();
        await client.ConnectAsync(new Uri("ws://localhost"));

        await client.DisconnectAsync("shutdown");

        Assert.False(transport.IsConnected);
        var sent = (ClientGoodbyeMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("shutdown", sent.Reason);
    }
}
