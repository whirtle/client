using System.Text;
using Whirtle.Client.Protocol;

namespace Whirtle.Client.Tests.Protocol;

public class MessageSerializerTests
{
    private readonly MessageSerializer _serializer = new();

    [Fact]
    public void Serialize_HelloMessage_ContainsTypeAndVersion()
    {
        var json = Encoding.UTF8.GetString(_serializer.Serialize(new HelloMessage("1.0")));

        Assert.Contains("\"type\":\"hello\"", json);
        Assert.Contains("\"version\":\"1.0\"", json);
    }

    [Fact]
    public void Roundtrip_HelloMessage()
    {
        var original = new HelloMessage("1.0");
        Assert.Equal(original, Roundtrip(original));
    }

    [Fact]
    public void Roundtrip_WelcomeMessage()
    {
        var original = new WelcomeMessage("session-123", "1.0");
        Assert.Equal(original, Roundtrip(original));
    }

    [Fact]
    public void Roundtrip_PingMessage()
    {
        Assert.IsType<PingMessage>(Roundtrip(new PingMessage()));
    }

    [Fact]
    public void Roundtrip_PongMessage()
    {
        Assert.IsType<PongMessage>(Roundtrip(new PongMessage()));
    }

    [Fact]
    public void Roundtrip_ErrorMessage()
    {
        var original = new ErrorMessage("auth_failed", "Bad token");
        Assert.Equal(original, Roundtrip(original));
    }

    [Fact]
    public void Roundtrip_GoodbyeMessage()
    {
        var original = new GoodbyeMessage("normal");
        Assert.Equal(original, Roundtrip(original));
    }

    [Fact]
    public void Deserialize_IsCaseInsensitive()
    {
        var json = """{"type":"HELLO","version":"1.0"}"""u8.ToArray();
        var msg = _serializer.Deserialize(json);
        Assert.IsType<HelloMessage>(msg);
    }

    [Fact]
    public void Serialize_HelloMessage_WithPlayerSupport_ContainsSnakeCaseKeys()
    {
        var support = new PlayerV1Support(
            SupportedFormats: [new SupportedFormat("opus", 2, 48_000, 16)],
            BufferCapacity:   500_000,
            SupportedCommands: ["volume"]);
        var hello = new HelloMessage("1.0", support);

        var json = System.Text.Encoding.UTF8.GetString(_serializer.Serialize(hello));

        Assert.Contains("\"player@v1_support\"",   json);
        Assert.Contains("\"supported_formats\"",   json);
        Assert.Contains("\"sample_rate\"",         json);
        Assert.Contains("\"bit_depth\"",           json);
        Assert.Contains("\"buffer_capacity\"",     json);
        Assert.Contains("\"supported_commands\"",  json);
    }

    [Fact]
    public void Roundtrip_HelloMessage_WithPlayerSupport()
    {
        var support = new PlayerV1Support(
            SupportedFormats:
            [
                new SupportedFormat("opus", 2, 48_000, 16),
                new SupportedFormat("pcm",  2, 48_000, 16),
            ],
            BufferCapacity:    1_000_000,
            SupportedCommands: ["volume", "mute"]);

        var original = new HelloMessage("1.0", support);
        var roundtripped = (HelloMessage)Roundtrip(original);

        Assert.Equal(original.Version, roundtripped.Version);
        Assert.NotNull(roundtripped.PlayerV1Support);
        Assert.Equal(2, roundtripped.PlayerV1Support.SupportedFormats.Length);
        Assert.Equal("opus",      roundtripped.PlayerV1Support.SupportedFormats[0].Codec);
        Assert.Equal(48_000,      roundtripped.PlayerV1Support.SupportedFormats[0].SampleRate);
        Assert.Equal(16,          roundtripped.PlayerV1Support.SupportedFormats[0].BitDepth);
        Assert.Equal(1_000_000,   roundtripped.PlayerV1Support.BufferCapacity);
        Assert.Equal(["volume", "mute"], roundtripped.PlayerV1Support.SupportedCommands);
    }

    [Fact]
    public void Roundtrip_StreamStartMessage()
    {
        var original = new StreamStartMessage(
            new StreamPlayer("opus", Channels: 2, SampleRate: 48_000, BitDepth: 16));
        var roundtripped = (StreamStartMessage)Roundtrip(original);

        Assert.Equal("opus",  roundtripped.Player.Codec);
        Assert.Equal(2,       roundtripped.Player.Channels);
        Assert.Equal(48_000,  roundtripped.Player.SampleRate);
        Assert.Equal(16,      roundtripped.Player.BitDepth);
        Assert.Null(roundtripped.Player.CodecHeader);
    }

    [Fact]
    public void Roundtrip_StreamStartMessage_WithCodecHeader()
    {
        var original = new StreamStartMessage(
            new StreamPlayer("flac", Channels: 2, SampleRate: 44_100, BitDepth: 16,
                             CodecHeader: "ZmxhY2hlYWRlcg=="));
        var roundtripped = (StreamStartMessage)Roundtrip(original);

        Assert.Equal("flac",             roundtripped.Player.Codec);
        Assert.Equal("ZmxhY2hlYWRlcg==", roundtripped.Player.CodecHeader);
    }

    [Fact]
    public void Serialize_StreamStartMessage_ContainsSnakeCaseKeys()
    {
        var msg  = new StreamStartMessage(
            new StreamPlayer("opus", Channels: 2, SampleRate: 48_000, BitDepth: 16));
        var json = System.Text.Encoding.UTF8.GetString(_serializer.Serialize(msg));

        Assert.Contains("\"stream/start\"", json);
        Assert.Contains("\"sample_rate\"",  json);
        Assert.Contains("\"bit_depth\"",    json);
    }

    private Message Roundtrip(Message msg) => _serializer.Deserialize(_serializer.Serialize(msg));
}
