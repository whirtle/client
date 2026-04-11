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

    private Message Roundtrip(Message msg) => _serializer.Deserialize(_serializer.Serialize(msg));
}
