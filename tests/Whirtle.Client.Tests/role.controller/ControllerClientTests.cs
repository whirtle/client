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
        Assert.Equal("play", msg.Controller!.Command);
        Assert.Null(msg.Controller.Volume);
    }

    [Fact]
    public async Task PauseAsync_SendsPauseCommand()
    {
        var (controller, transport) = Build();

        await controller.PauseAsync();

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("pause", msg.Controller!.Command);
    }

    [Fact]
    public async Task NextAsync_SendsNextCommand()
    {
        var (controller, transport) = Build();

        await controller.NextAsync();

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("next", msg.Controller!.Command);
    }

    [Fact]
    public async Task StopAsync_SendsStopCommand()
    {
        var (controller, transport) = Build();

        await controller.StopAsync();

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("stop", msg.Controller!.Command);
    }

    [Fact]
    public async Task PreviousAsync_SendsPreviousCommand()
    {
        var (controller, transport) = Build();

        await controller.PreviousAsync();

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("previous", msg.Controller!.Command);
    }

    [Fact]
    public async Task SetVolumeAsync_SendsVolumeCommand_ScaledTo100()
    {
        var (controller, transport) = Build();

        await controller.SetVolumeAsync(0.75);

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("volume", msg.Controller!.Command);
        Assert.Equal(75, msg.Controller.Volume);
    }

    [Fact]
    public async Task SetVolumeAsync_ClampsAbove1()
    {
        var (controller, transport) = Build();

        await controller.SetVolumeAsync(1.5);

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal(100, msg.Controller!.Volume);
    }

    [Fact]
    public async Task SetVolumeAsync_ClampsBelowZero()
    {
        var (controller, transport) = Build();

        await controller.SetVolumeAsync(-0.5);

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal(0, msg.Controller!.Volume);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetMuteAsync_SendsMuteCommand(bool muted)
    {
        var (controller, transport) = Build();

        await controller.SetMuteAsync(muted);

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal("mute", msg.Controller!.Command);
        Assert.Equal(muted, msg.Controller.Mute);
    }

    [Theory]
    [InlineData("repeat_off")]
    [InlineData("repeat_one")]
    [InlineData("repeat_all")]
    [InlineData("shuffle")]
    [InlineData("unshuffle")]
    [InlineData("switch")]
    public async Task SimpleCommands_SendCorrectCommandString(string command)
    {
        var (controller, transport) = Build();

        Task task = command switch
        {
            "repeat_off"  => controller.RepeatOffAsync(),
            "repeat_one"  => controller.RepeatOneAsync(),
            "repeat_all"  => controller.RepeatAllAsync(),
            "shuffle"     => controller.ShuffleAsync(),
            "unshuffle"   => controller.UnshuffleAsync(),
            "switch"      => controller.SwitchAsync(),
            _             => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        await task;

        var msg = (ClientCommandMessage)Serializer.Deserialize(transport.Sent[0]);
        Assert.Equal(command, msg.Controller!.Command);
    }
}
