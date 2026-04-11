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
        // Fast path: use Utf8JsonReader (stack-only, no heap allocation) to find
        // the "type" value and check whether it is already lowercase.  This avoids
        // the JsonDocument allocation on every message — the vast majority of servers
        // send lowercase type discriminators so this branch is almost always taken.
        var typeValue = ReadTypeValue(data);
        if (typeValue is null)
            return data;

        var lower = typeValue.ToLowerInvariant();
        if (lower == typeValue)
            return data;

        // Slow path (non-lowercase discriminator): rebuild the JSON object with
        // the lowercased value.  JsonDocument is only allocated here.
        using var doc    = JsonDocument.Parse(data);
        using var ms     = new MemoryStream(data.Length);
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartObject();
        foreach (var prop in doc.RootElement.EnumerateObject())
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

    /// <summary>
    /// Scans <paramref name="data"/> with a <see cref="Utf8JsonReader"/> to locate
    /// the top-level <c>"type"</c> property and return its string value.
    /// Returns <see langword="null"/> when the property is absent or has a non-string
    /// value.  Any parse errors are treated as "not found" — the caller's subsequent
    /// <see cref="JsonSerializer.Deserialize"/> will surface the real error.
    /// </summary>
    private static string? ReadTypeValue(byte[] data)
    {
        try
        {
            var reader = new Utf8JsonReader(data, isFinalBlock: true, state: default);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName &&
                    reader.ValueTextEquals("type"u8))
                {
                    return reader.Read() ? reader.GetString() : null;
                }
            }
        }
        catch { /* malformed JSON — let Deserialize report the error */ }

        return null;
    }
}
