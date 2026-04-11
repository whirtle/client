using System.Text.Json;

namespace Whirtle.Client.Protocol;

internal sealed class MessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public byte[] Serialize(Message message)
        => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public Message Deserialize(byte[] data)
    {
        var msg = JsonSerializer.Deserialize<Message>(data, Options);
        return msg ?? throw new InvalidOperationException("Received null message from server.");
    }
}
