using Whirtle.Client.Protocol;
using Whirtle.Client.Role;
using Whirtle.Client.Tests.Protocol;

namespace Whirtle.Client.Tests.Role;

public class ControllerClientTests
{
    private static readonly MessageSerializer Serializer = new();

    private static (ControllerClient controller, FakeTransport transport) Build()
    {
        var transport  = new FakeTransport();
        var protocol   = new ProtocolClient(transport);
        var controller = new ControllerClient(protocol);
        return (controller, transport);
    }

    [Fact]
    public async Task PlayAsync_SendsPlayCommand()
    {
        var (controller, transport) = Build();

        await controller.PlayAsync();

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("play", msg.Command);
        Assert.Null(msg.Value);
    }

    [Fact]
    public async Task PauseAsync_SendsPauseCommand()
    {
        var (controller, transport) = Build();

        await controller.PauseAsync();

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("pause", msg.Command);
    }

    [Fact]
    public async Task SkipAsync_SendsSkipCommand()
    {
        var (controller, transport) = Build();

        await controller.SkipAsync();

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("skip", msg.Command);
    }

    [Fact]
    public async Task SetVolumeAsync_SendsVolumeCommand_WithNormalisedValue()
    {
        var (controller, transport) = Build();

        await controller.SetVolumeAsync(0.75);

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("volume", msg.Command);
        Assert.Equal(0.75, msg.Value);
    }

    [Fact]
    public async Task SetVolumeAsync_ClampsAbove1()
    {
        var (controller, transport) = Build();

        await controller.SetVolumeAsync(1.5);

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal(1.0, msg.Value);
    }

    [Fact]
    public async Task SetVolumeAsync_ClampsBelowZero()
    {
        var (controller, transport) = Build();

        await controller.SetVolumeAsync(-0.5);

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal(0.0, msg.Value);
    }
}
