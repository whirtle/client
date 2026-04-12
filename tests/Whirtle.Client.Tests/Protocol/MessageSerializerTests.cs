using System.Text;
using Whirtle.Client.Protocol;

namespace Whirtle.Client.Tests.Protocol;

public class MessageSerializerTests
{
    private readonly MessageSerializer _serializer = new();

    // ── Envelope structure ────────────────────────────────────────────────────

    [Fact]
    public void Serialize_ProducesTypeAndPayloadEnvelope()
    {
        var json = Encoding.UTF8.GetString(
            _serializer.Serialize(new ClientHelloMessage("id-1", "Test", 1, [])));

        Assert.Contains("\"type\":\"client/hello\"", json);
        Assert.Contains("\"payload\":", json);
    }

    [Fact]
    public void Serialize_PayloadUsesSnakeCase()
    {
        var json = Encoding.UTF8.GetString(
            _serializer.Serialize(new ClientHelloMessage("id-1", "Test", 1, [])));

        Assert.Contains("\"client_id\"", json);
        Assert.Contains("\"supported_roles\"", json);
    }

    // ── Roundtrips ───────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_ClientHelloMessage()
    {
        var original = new ClientHelloMessage("abc", "My Client", 1, ["metadata@v1"]);
        var result   = (ClientHelloMessage)Roundtrip(original);

        Assert.Equal("abc",        result.ClientId);
        Assert.Equal("My Client",  result.Name);
        Assert.Equal(1,            result.Version);
        Assert.Equal(new[] { "metadata@v1" }, result.SupportedRoles);
    }

    [Fact]
    public void Roundtrip_ClientTimeMessage()
    {
        var original = new ClientTimeMessage(1_234_567_890L);
        var result   = (ClientTimeMessage)Roundtrip(original);
        Assert.Equal(1_234_567_890L, result.ClientTransmitted);
    }

    [Fact]
    public void Roundtrip_ClientCommandMessage_WithController()
    {
        var original = new ClientCommandMessage(new ClientControllerCommand("volume", Volume: 75));
        var result   = (ClientCommandMessage)Roundtrip(original);

        Assert.Equal("volume", result.Controller!.Command);
        Assert.Equal(75,       result.Controller.Volume);
    }

    [Fact]
    public void Roundtrip_ClientGoodbyeMessage()
    {
        var original = new ClientGoodbyeMessage("another_server");
        var result   = (ClientGoodbyeMessage)Roundtrip(original);
        Assert.Equal("another_server", result.Reason);
    }

    [Fact]
    public void Roundtrip_ServerHelloMessage()
    {
        var original = new ServerHelloMessage(
            "srv-001", "Server", 1, ["controller@v1"], "discovery");
        var result = (ServerHelloMessage)Roundtrip(original);

        Assert.Equal("srv-001",      result.ServerId);
        Assert.Equal("discovery",    result.ConnectionReason);
        Assert.Equal(new[] { "controller@v1" }, result.ActiveRoles);
    }

    [Fact]
    public void Roundtrip_ServerTimeMessage()
    {
        var original = new ServerTimeMessage(100L, 200L);
        var result   = (ServerTimeMessage)Roundtrip(original);

        Assert.Equal(100L, result.ClientTransmitted);
        Assert.Equal(200L, result.ServerReceived);
    }

    [Fact]
    public void Roundtrip_ServerStateMessage_WithMetadata()
    {
        var original = new ServerStateMessage(
            Metadata: new ServerMetadataState(Title: "Track", Artist: "Band"));
        var result = (ServerStateMessage)Roundtrip(original);

        Assert.Equal("Track", result.Metadata!.Title);
        Assert.Equal("Band",  result.Metadata.Artist);
    }

    [Fact]
    public void Roundtrip_GroupUpdateMessage()
    {
        var original = new GroupUpdateMessage("playing", "group-42");
        var result   = (GroupUpdateMessage)Roundtrip(original);

        Assert.Equal("playing",  result.PlaybackState);
        Assert.Equal("group-42", result.GroupId);
    }

    // ── Deserialise from server JSON ──────────────────────────────────────────

    [Fact]
    public void Deserialize_ServerHello_FromRealServerFormat()
    {
        // Matches what the real Sendspin server sends.
        var json = """
            {"payload":{"server_id":"srv-abc","name":"Sendspin Server","version":1,
             "active_roles":["controller@v1","metadata@v1"],"connection_reason":"discovery"},
             "type":"server/hello"}
            """u8.ToArray();

        var msg = (ServerHelloMessage)_serializer.Deserialize(json);

        Assert.Equal("srv-abc",   msg.ServerId);
        Assert.Equal("discovery", msg.ConnectionReason);
    }

    [Fact]
    public void Deserialize_UnknownType_ReturnsUnknownMessage()
    {
        var json = """{"type":"future/extension","payload":{}}"""u8.ToArray();
        var msg  = _serializer.Deserialize(json);
        var unknown = Assert.IsType<UnknownMessage>(msg);
        Assert.Equal("future/extension", unknown.Type);
    }

    [Fact]
    public void Deserialize_TypeCaseInsensitive()
    {
        var json = """{"type":"SERVER/HELLO","payload":{"server_id":"s","name":"n","version":1,"active_roles":[],"connection_reason":"discovery"}}"""u8.ToArray();
        var msg  = _serializer.Deserialize(json);
        Assert.IsType<ServerHelloMessage>(msg);
    }

    private Message Roundtrip(Message msg) => _serializer.Deserialize(_serializer.Serialize(msg));
}
