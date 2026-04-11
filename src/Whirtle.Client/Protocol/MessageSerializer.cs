// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

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
        // [JsonPolymorphic] discriminator matching is case-sensitive.
        // Normalise the "type" value to lowercase so servers that capitalise
        // the discriminator (e.g. "HELLO") are handled transparently.
        data = NormalizeTypeDiscriminator(data);

        var msg = JsonSerializer.Deserialize<Message>(data, Options);
        return msg ?? throw new InvalidOperationException("Received null message from server.");
    }

    /// <summary>
    /// If the JSON object has a <c>"type"</c> property whose value is not already
    /// lowercase, rebuilds the JSON with the value lowercased.  Returns the original
    /// array unchanged when no normalisation is needed (the common path).
    /// </summary>
    private static byte[] NormalizeTypeDiscriminator(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            return data;

        var typeValue = typeProp.GetString();
        if (typeValue is null)
            return data;

        var lower = typeValue.ToLowerInvariant();
        if (lower == typeValue)
            return data; // already lowercase — skip allocation

        using var ms     = new MemoryStream(data.Length);
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartObject();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("type"))
                writer.WriteString("type", lower);
            else
                prop.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();

        return ms.ToArray();
    }
}
